using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class TimelinePanel : VisualElement, IDisposable
    {
        private const float HeaderWidth = 160, RowHeight = 33;
        private readonly SkillDocument _doc;
        private readonly VisualElement _tracks, _ruler, _head;
        private readonly Label _clock;
        private readonly Button _play;
        private readonly List<ElementView> _elements = new();
        private readonly List<RowView> _rows = new();
        private readonly HashSet<string> _foldedCues = new();
        private readonly ScrollView _scroll;
        private SkillAnimationAsset _animation;
        private bool _dragging, _rebuildPending;
        private readonly Dictionary<ElementView, (float start, float end)> _dragSpans = new();
        private readonly HashSet<string> _selected = new();
        private float _zoom = 500, _offset;
        private bool _scrub, _pan;
        private float _panX;
        private string _rangeStartId;
        private string _clipboard;
        private (string track, string id)[] _copyIds;
        private float _copyOrigin;
        private readonly VisualElement _clipRow;
        private Label _clipCaption;
        private Label _tail;
        private BlendRamps _ramps;
        private readonly VisualElement _marquee;
        private bool _selecting;
        private Vector2 _selectionStart;
        private HashSet<string> _selectionBefore;
        private sealed class ElementView
        {
            public ITrackType Track; public string Id; public float Start, End; public VisualElement View, Ghost;
            public int Lane;
            public string Key => Track.PropertyPath + "/" + Id;
        }
        private sealed class RowView
        {
            public VisualElement Lane;
            public Label Heading;
            public string CueTag;
            public readonly List<ElementView> Elements = new();
        }
        public TimelinePanel(SkillDocument doc)
        {
            _doc = doc; focusable = true; AddToClassList("timeline-panel");
            var toolbar = new Toolbar(); toolbar.AddToClassList("timeline-toolbar");
            toolbar.Add(new ToolbarButton(() => Seek(0)) { text = "|◀", tooltip = "跳到开始 Home" });
            _play = new ToolbarButton(TogglePlay) { text = "▶", tooltip = "播放 / 暂停 Space" }; toolbar.Add(_play);
            toolbar.Add(new ToolbarButton(() => Seek(doc.PlaybackDuration)) { text = "▶|", tooltip = "跳到末尾 End" });
            var loop = new ToolbarToggle { text = "循环", value = doc.Playback.Loop }; loop.RegisterValueChangedCallback(e => doc.Playback.Loop = e.newValue); toolbar.Add(loop);
            _clock = new Label(); _clock.AddToClassList("timeline-clock"); toolbar.Add(_clock);
            var speed = new PopupField<string>(new List<string> { "×0.5", "×1.0", "×1.5", "×2.0", "×3.0" }, 1) { tooltip = "攻速预览；虚影显示实际触发时间" };
            speed.style.width = 72; speed.RegisterValueChangedCallback(e => { doc.Playback.Speed = float.Parse(e.newValue.Substring(1), System.Globalization.CultureInfo.InvariantCulture); Rebuild(new ChangeSet(doc.Root)); }); toolbar.Add(speed);
            var add = new ToolbarMenu { text = "+ 轨道" };
            foreach (var type in TrackTypeRegistry.Types) { var track = type; add.menu.AppendAction(track.DisplayName, _ => Add(track)); } toolbar.Add(add);
            toolbar.Add(new ToolbarButton(Fit) { text = "适应", tooltip = "缩放至完整动画及预演中的后续命中" }); Add(toolbar);
            _clipRow = new VisualElement(); _clipRow.AddToClassList("clip-picker-row"); Add(_clipRow);
            _ruler = new VisualElement { style = { height = 24, marginLeft = HeaderWidth, overflow = Overflow.Hidden } }; _ruler.AddToClassList("time-ruler"); Add(_ruler);
            _scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1, minHeight = 0 } };
            _scroll.AddToClassList("timeline-scroll"); Add(_scroll);
            _tracks = new VisualElement { style = { flexGrow = 1, overflow = Overflow.Hidden } }; _scroll.Add(_tracks);
            _head = new VisualElement { pickingMode = PickingMode.Ignore }; _head.AddToClassList("playhead"); _tracks.Add(_head);
            _marquee = new VisualElement { pickingMode = PickingMode.Ignore, style = { display = DisplayStyle.None } };
            _marquee.AddToClassList("timeline-marquee");
            _ruler.RegisterCallback<PointerDownEvent>(e => { if (e.button != 0) return; _scrub = true; Focus(); _ruler.CapturePointer(e.pointerId); Seek(TimeAt(e.localPosition.x)); });
            _ruler.RegisterCallback<PointerMoveEvent>(e => { if (_scrub) Seek(TimeAt(e.localPosition.x)); });
            _ruler.RegisterCallback<PointerUpEvent>(e => { _scrub = false; _ruler.ReleasePointer(e.pointerId); });
            RegisterCallback<WheelEvent>(e =>
            {
                if (!e.ctrlKey && !_ruler.worldBound.Contains(e.mousePosition)) return;
                float local = e.mousePosition.x - worldBound.x - HeaderWidth;
                float time = TimeAt(local); _zoom = Mathf.Clamp(_zoom * (e.delta.y < 0 ? 1.15f : .87f), 30, 4000);
                _offset = Mathf.Max(0, time * _zoom - local); PackRows(); PositionElements(); DrawRuler(); e.StopPropagation();
            }, TrickleDown.TrickleDown);
            _ruler.tooltip = "滚轮缩放 · 轨道区滚轮上下滚动 · Ctrl + 滚轮缩放 · 中键平移";
            _tracks.RegisterCallback<PointerDownEvent>(e => { if (e.button == 2) { _pan = true; _panX = e.position.x; _tracks.CapturePointer(e.pointerId); e.StopPropagation(); } });
            _tracks.RegisterCallback<PointerMoveEvent>(e => { if (_pan) { _offset = Mathf.Max(0, _offset - (e.position.x - _panX)); _panX = e.position.x; PositionElements(); DrawRuler(); } });
            _tracks.RegisterCallback<PointerUpEvent>(e => { if (_pan) { _pan = false; _tracks.ReleasePointer(e.pointerId); } });
            _tracks.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0 || e.clickCount != 1 || e.target is not VisualElement target || !target.ClassListContains("track-lane")) return;
                _selectionStart = _tracks.WorldToLocal(e.position); _selecting = true;
                _selectionBefore = e.ctrlKey || e.shiftKey ? new HashSet<string>(_selected) : new HashSet<string>();
                _tracks.CapturePointer(e.pointerId);
            });
            _tracks.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!_selecting) return;
                Vector2 point = _tracks.WorldToLocal(e.position);
                var rect = Rect.MinMaxRect(Mathf.Min(point.x, _selectionStart.x), Mathf.Min(point.y, _selectionStart.y), Mathf.Max(point.x, _selectionStart.x), Mathf.Max(point.y, _selectionStart.y));
                if (rect.size.magnitude < 4) return;
                _marquee.style.display = DisplayStyle.Flex; _marquee.style.left = rect.x; _marquee.style.top = rect.y; _marquee.style.width = rect.width; _marquee.style.height = rect.height;
                _selected.Clear(); _selected.UnionWith(_selectionBefore);
                foreach (var item in _elements)
                {
                    if (item.View.resolvedStyle.display == DisplayStyle.None) continue;
                    Vector2 local = _tracks.WorldToLocal(item.View.worldBound.position);
                    var bounds = new Rect(local, item.View.worldBound.size);
                    if (item.Track.Shape == TrackElementShape.Point) bounds.width = 18;
                    if (rect.Overlaps(bounds)) _selected.Add(item.Key);
                    item.View.EnableInClassList("selected-element", _selected.Contains(item.Key));
                }
            });
            _tracks.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!_selecting) return; _selecting = false; _tracks.ReleasePointer(e.pointerId); _marquee.style.display = DisplayStyle.None;
                var last = _elements.LastOrDefault(x => _selected.Contains(x.Key));
                _doc.Selection.Select(_doc.Root?.Animation(), last?.Track.PropertyPath, last?.Id);
            });
            _tracks.RegisterCallback<PointerCaptureOutEvent>(_ => { _selecting = false; _marquee.style.display = DisplayStyle.None; });
            RegisterCallback<KeyDownEvent>(OnKey);
            RegisterCallback<GeometryChangedEvent>(_ => { DrawRuler(); PositionElements(); });
            doc.Changed += Rebuild; doc.Selection.Changed += SelectionChanged; doc.Playback.Changed += PlaybackChanged;
            doc.PreviewRangeChanged+=PreviewRangeChanged;
        }
        private float Duration => ValidPositive(_doc.Root?.Animation()?.Duration ?? 1, 1);
        private float Fps => ValidPositive(_doc.Root?.Animation()?.FrameRate ?? 30, 30);
        private static float ValidPositive(float value, float fallback) => value > 0 && !float.IsInfinity(value) ? value : fallback;
        private float TimeAt(float x) => TimelineMath.Time(x, _zoom, _offset);
        private void Seek(float time) { if (!_doc.Playback.Live) _doc.Playback.Seek(time, _doc.PlaybackDuration); }
        public void TogglePlay() { if (_doc.Playback.Live) return; _doc.Playback.Playing = !_doc.Playback.Playing; if (_doc.Playback.Time >= _doc.PlaybackDuration) Seek(0); PlaybackChanged(); }
        public void Fit() { _zoom = Mathf.Clamp(Mathf.Max(100, ValidPositive(resolvedStyle.width, 640) - HeaderWidth - 24) / Mathf.Max(Duration,_doc.PlaybackDuration), 30, 4000); _offset = 0; PackRows(); PositionElements(); DrawRuler(); }
        public void Rebuild(ChangeSet change)
        {
            if (_dragging) { _rebuildPending = true; return; }
            if (!change.Structure && change.Paths.Length > 0 && _animation == _doc.Root?.Animation())
            {
                if (change.Asset != _animation) return;
                bool regroup = change.Paths.Any(path => path.Contains("cueTag") || path.EndsWith(".id", StringComparison.Ordinal));
                if (!regroup && _animation != null)
                {
                    foreach (var element in _elements)
                    {
                        var property = TimelineCommands.Find(_doc.Serialized(_animation), element.Track.PropertyPath, element.Id);
                        if (property == null) { regroup = true; break; }
                        var span = element.Track.ReadSpan(property); element.Start = span.start; element.End = span.end;
                        var label = element.View.Q<Label>();
                        label.text = element.Track.PropertyPath == "cueRegions" ? $"{span.start:0.##}–{(span.end < 0 ? "结束" : span.end.ToString("0.##") + "s")}" : element.Track.Label(property);
                        element.View.tooltip = element.Track.Label(property) + $" · {span.start:0.###}s";
                        element.View.EnableInClassList("invalid-element", span.start < 0 || span.start > Duration || span.end > Duration);
                    }
                    if (!regroup)
                    {
                        if (_clipCaption != null) _clipCaption.text = _animation.ClipName + $" · {_animation.Duration:0.##}s";
                        _ramps?.MarkDirtyRepaint(); UpdateFrameData(_tracks.Q<Label>("frame-data")); PackRows(); PositionElements(); DrawRuler(); SelectionChanged(); PlaybackChanged(); return;
                    }
                }
            }
            _rebuildPending = false;
            _elements.Clear(); _rows.Clear(); _tracks.Clear(); _clipRow.Clear(); _ramps = null; _clipCaption = null;
            var animation = _doc.Root?.Animation();
            if (_animation != animation) { _foldedCues.Clear(); _selected.Clear(); _animation = animation; }
            if (animation == null)
            {
                var empty = new Button(() =>
                {
                    if (_doc.Root == null) { _doc.Report("先选择或新建技能"); return; }
                    SkillAssetFactory.Create(_doc, typeof(SkillAnimationAsset), "技能动画", EffectDurationPolicy.Instant, _doc.Root, new PortId(PortKind.Animation));
                }) { text = "+ 添加动画 · 选择角色片段后开始编辑" };
                empty.AddToClassList("empty-action"); _tracks.Add(empty); PlaybackChanged(); return;
            }
            BuildClipPicker(animation);
            var clipLane = AddRow("动画", SkillColors.Animation, null).Lane;
            var clip = new Label(); clip.AddToClassList("animation-clip"); clip.style.backgroundColor = SkillColors.Animation;
            clip.style.width = Duration * _zoom; clip.style.left = -_offset; clipLane.Add(clip);
            clip.RegisterCallback<PointerDownEvent>(_ => { _doc.Selection.Select(animation); Focus(); });
            _ramps = new BlendRamps(this, animation); clipLane.Add(_ramps);
            // 文字放在斜坡绘制层上方，保持可读；忽略拾取以免遮住混入、混出手柄。
            _clipCaption = new Label(animation.ClipName + $"  ·  {animation.Duration:0.##}s") { pickingMode = PickingMode.Ignore };
            _clipCaption.AddToClassList("animation-clip-caption"); clipLane.Add(_clipCaption);
            _tail=new Label("后续 · 独立任务"){pickingMode=PickingMode.Ignore};_tail.AddToClassList("preview-tail");clipLane.Add(_tail);
            clip.tooltip = _clipCaption.text;
            var frames=AddRow("帧数据",SkillColors.Animation,null);
            var info=new Label{ name="frame-data" };frames.Lane.Add(info); UpdateFrameData(info);
            foreach (var track in TrackTypeRegistry.Types)
            {
                var array = _doc.Serialized(animation).FindProperty(track.PropertyPath); if (array == null) continue;
                bool cues = track.PropertyPath == "cueRegions", presentation=track.PropertyPath=="runtimeTracks";
                var groups = Enumerable.Range(0, array.arraySize).GroupBy(i => (cues || presentation) ? track.Label(array.GetArrayElementAtIndex(i)) : "")
                    .OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
                foreach (var group in groups)
                {
                    var row = AddRow(cues ? "Cue · " + (string.IsNullOrEmpty(group.Key) ? "未设置标签" : group.Key)
                        : presentation ? "表现 · "+group.Key : track.DisplayName, track.Color, track, cues ? group.Key : null);
                    var lane = row.Lane;
                    foreach (int i in group)
                    {
                        var item = array.GetArrayElementAtIndex(i); var span = track.ReadSpan(item);
                        var element = new ElementView { Track = track, Id = item.FindPropertyRelative("id").stringValue, Start = span.start, End = span.end };
                        var view = new VisualElement { focusable = true, userData = element.Key }; element.View = view;
                        view.AddToClassList(track.Shape == TrackElementShape.Point ? "timeline-event" : "timeline-range");
                        view.style.backgroundColor = track.Shape == TrackElementShape.Point ? Color.clear : track.Color;
                        if (track.Shape == TrackElementShape.Point)
                        {
                            var diamond = new VisualElement { pickingMode = PickingMode.Ignore };
                            diamond.AddToClassList("event-diamond"); diamond.style.backgroundColor = track.Color; view.Add(diamond);
                        }
                        var label = new Label(cues ? $"{span.start:0.##}–{(span.end < 0 ? "结束" : span.end.ToString("0.##") + "s")}" : track.Label(item)); label.pickingMode = PickingMode.Ignore; view.Add(label);
                        view.tooltip = $"{track.Label(item)}  ·  {span.start:0.000}s" + (track.Shape == TrackElementShape.Range ? $" → {(span.end < 0 ? "随动画结束" : span.end.ToString("0.000") + "s")}" : "");
                        if (span.start > Duration || span.start < 0 || span.end > Duration) view.AddToClassList("invalid-element");
                        lane.Add(view); _elements.Add(element); row.Elements.Add(element); BindDrag(element);
                        if (Mathf.Abs(_doc.Playback.Speed - 1) > .01f)
                        {
                            var ghost = new Label("◇") { pickingMode = PickingMode.Ignore }; ghost.AddToClassList("speed-ghost");
                            element.Ghost = ghost; lane.Add(ghost);
                        }
                    }
                }
            }
            _tracks.Add(_head); _tracks.Add(_marquee); PackRows(); PositionElements(); DrawRuler(); SelectionChanged(); PlaybackChanged();
        }
        private RowView AddRow(string title, Color color, ITrackType track, string cueTag = null)
        {
            var row = new VisualElement(); row.AddToClassList("track-row");row.style.minHeight=RowHeight; row.style.height = RowHeight;
            var heading = new Label(title){tooltip=title}; heading.AddToClassList("track-heading"); heading.style.width = HeaderWidth; heading.style.borderLeftColor = color; row.Add(heading);
            var lane = new VisualElement(); lane.AddToClassList("track-lane"); row.Add(lane); _tracks.Add(row);
            var data = new RowView { Lane = lane, Heading = heading, CueTag = cueTag }; _rows.Add(data);
            row.userData = cueTag ?? track?.PropertyPath;
            if (cueTag != null)
            {
                row.AddToClassList("cue-track-row"); heading.tooltip = title + " · 点击折叠 / 展开";
                heading.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button != 0 || _dragging) return;
                    if (!_foldedCues.Add(cueTag)) _foldedCues.Remove(cueTag);
                    PackRows(); e.StopPropagation();
                });
            }
            if (track != null)
            {
                lane.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.target != lane || e.button != 0) return;
                    Focus(); Seek(Mathf.Clamp(TimeAt(e.localPosition.x), 0, Duration));
                    if (e.clickCount == 2) TimelineCommands.Add(_doc, track, TimelineMath.Snap(_doc.Playback.Time, Fps), cueTag: cueTag);
                });
                lane.AddManipulator(new ContextualMenuManipulator(e =>
                {
                    float time = Mathf.Clamp(TimeAt(lane.WorldToLocal(e.mousePosition).x), 0, Duration);
                    e.menu.AppendAction("添加" + track.DisplayName, _ => TimelineCommands.Add(_doc, track, time, cueTag: cueTag));
                    e.menu.AppendAction("删除选中元素  Del", _ => DeleteSelection());
                    e.menu.AppendAction("复制  Ctrl+C", _ => Copy()); e.menu.AppendAction("粘贴到播放头  Ctrl+V", _ => Paste());
                }));
                lane.RegisterCallback<DragUpdatedEvent>(e => { if (DragAndDrop.objectReferences.Any(track.AcceptsDrop)) { DragAndDrop.visualMode = DragAndDropVisualMode.Copy; e.StopPropagation(); } });
                lane.RegisterCallback<DragPerformEvent>(e =>
                {
                    var asset = DragAndDrop.objectReferences.FirstOrDefault(track.AcceptsDrop);
                    if (asset == null) return;
                    TimelineCommands.Add(_doc, track, Mathf.Clamp(TimelineMath.Snap(TimeAt(e.localMousePosition.x), Fps), 0, Duration), asset);
                    DragAndDrop.AcceptDrag(); e.StopPropagation();
                });
            }
            return data;
        }
        private void PackRows()
        {
            if (_dragging) return; // 拖拽只改横向位置，抬手写回后 Rebuild 才重排行号。
            foreach (var row in _rows)
            {
                bool folded = row.CueTag != null && _foldedCues.Contains(row.CueTag);
                var ends = new List<float>();
                foreach (var element in row.Elements.OrderBy(e => e.Start).ThenBy(e => e.Id, StringComparer.Ordinal))
                {
                    float end = element.Track.Shape == TrackElementShape.Point ? element.Start + 134 / _zoom
                        : Mathf.Max(element.End < 0 ? Duration : element.End, element.Start + 12 / _zoom);
                    int lane = ends.FindIndex(t => t <= element.Start);
                    if (lane < 0) { lane = ends.Count; ends.Add(end); } else ends[lane] = end;
                    element.Lane = lane;
                    element.View.style.top = 5 + lane * RowHeight;
                    element.View.style.display = folded ? DisplayStyle.None : DisplayStyle.Flex;
                    if (element.Ghost != null) { element.Ghost.style.top = 6 + lane * RowHeight; element.Ghost.style.display = element.View.style.display; }
                    element.View.Q<Label>(className: "event-count")?.RemoveFromHierarchy();
                }
                foreach (var coincident in row.Elements.Where(e => e.Track.Shape == TrackElementShape.Point).GroupBy(e => e.Start).Where(g => g.Count() > 1))
                {
                    var badge = new Label("×" + coincident.Count()) { pickingMode = PickingMode.Ignore }; badge.AddToClassList("event-count");
                    coincident.OrderBy(e => e.Lane).First().View.Add(badge);
                }
                row.Lane.parent.style.height = RowHeight * (folded ? 1 : Mathf.Max(1, ends.Count));
                if (row.CueTag != null) row.Heading.text = (folded ? "▸ " : "▾ ") + "Cue · " +
                    (string.IsNullOrEmpty(row.CueTag) ? "未设置标签" : row.CueTag.StartsWith("Cue.", StringComparison.Ordinal) ? row.CueTag.Substring(4) : row.CueTag) +
                    (folded ? $" ({row.Elements.Count})" : "");
            }
        }
        private void UpdateFrameData(Label label)
        {
            if(label==null || _animation==null)return;
            var a=_doc.Serialized(_animation);var markers=a.FindProperty("markers");float first=float.PositiveInfinity,last=0;
            for(int i=0;i<markers.arraySize;i++)
            {var m=markers.GetArrayElementAtIndex(i);if(m.FindPropertyRelative("actions").arraySize==0)continue;
             float at=m.FindPropertyRelative("time").floatValue;first=Mathf.Min(first,at);last=Mathf.Max(last,at);}
            int Frame(float t)=>Mathf.RoundToInt(t*Fps/Mathf.Max(.001f,_doc.Playback.Speed));
            foreach (var hit in _animation.hitWindows)
            { first = Mathf.Min(first, hit.start); last = Mathf.Max(last, hit.end); }
            var ranges=new List<string>();
            foreach(var s in _animation.sections??Array.Empty<AnimationSection>())if(s!=null && s.inputBufferEnd>s.inputBufferStart)
                ranges.Add("输入 "+Frame(s.inputBufferStart)+"–"+Frame(s.inputBufferEnd)+"f");
            var windows=a.FindProperty("tagWindows");
            for(int i=0;i<windows.arraySize;i++)
            {
                var w=windows.GetArrayElementAtIndex(i);var tags=w.FindPropertyRelative("tags");
                for(int j=0;j<tags.arraySize;j++)
                {
                    string name=tags.GetArrayElementAtIndex(j).FindPropertyRelative("m_Name").stringValue;
                    if(name!="Window.Combo" && !name.StartsWith("Window.Combo.",StringComparison.Ordinal))continue;
                    float end=w.FindPropertyRelative("end").floatValue;
                    ranges.Add("连招 "+Frame(w.FindPropertyRelative("start").floatValue)+"–"+Frame(end<0?Duration:end)+"f");break;
                }
            }
            label.text=(float.IsPositiveInfinity(first)?"无命中标记":$"前摇 {Frame(first)}f · 命中跨度 {Frame(last)-Frame(first)}f · 后摇 {Frame(Duration)-Frame(last)}f")+
                (ranges.Count>0?" · "+string.Join("、",ranges):"")+(_doc.PreviewEnd>Duration?$" · 后续至 {_doc.PreviewEnd:0.##}s":"");
        }
        private void BuildClipPicker(SkillAnimationAsset animation)
        {
            var adapter = AnimationAdapterRegistry.Resolve(_doc.PreviewPrefab);
            var clips = _doc.PreviewPrefab != null ? adapter.EnumerateClips(_doc.PreviewPrefab) : Array.Empty<ClipInfo>();
            _clipRow.Add(new Label("动画片段"));
            if (clips.Count > 0)
            {
                var choices = clips.Select(c => c.Name + $"  ({c.Length:0.##}s)").ToList();
                int selected = clips.ToList().FindIndex(c => c.Name == animation.ClipName);
                if (selected < 0) { choices.Insert(0, animation.ClipName + " · 未匹配"); selected = 0; }
                bool unmatched = choices.Count > clips.Count;
                var picker = new PopupField<string>(choices, selected); picker.style.flexGrow = 1;
                picker.RegisterValueChangedCallback(e =>
                { int index = choices.IndexOf(e.newValue) - (unmatched ? 1 : 0); if (index >= 0) SetClip(clips[index], animation); });
                _clipRow.Add(picker);
                _clipRow.Add(new Button(() => { var match = clips.FirstOrDefault(c => c.Name == animation.ClipName); if (match.Length > 0) SetClip(match, animation); }) { text = "同步时长" });
            }
            else
            {
                var name = new TextField { value = animation.ClipName, isDelayed = true, style = { flexGrow = 1 } };
                name.RegisterValueChangedCallback(e => EditCommand.Run(_doc, "修改动画片段", animation, so => so.FindProperty("clipName").stringValue = e.newValue));
                _clipRow.Add(name);
            }
            BlendField(animation, "混入", "blendIn", "从施放前的动画过渡进来的秒数，随攻速缩放；0 为硬切", animation.BlendIn);
            BlendField(animation, "混出", "blendOut", "结束或被打断后过渡回施放前动画的秒数；0 表示交给 Animator 自己的转换", animation.BlendOut);
            var fps = new FloatField { value = animation.FrameRate, isDelayed = true, tooltip = "帧率 / 吸附单位", style = { width = 50 } };
            fps.RegisterValueChangedCallback(e => EditCommand.Run(_doc, "修改吸附帧率", animation, so => so.FindProperty("frameRate").floatValue = Mathf.Max(1, e.newValue)));
            _clipRow.Add(fps); _clipRow.Add(new Label("fps"));
        }
        private void BlendField(SkillAnimationAsset animation, string label, string property, string tooltip, float value)
        {
            _clipRow.Add(new Label(label) { tooltip = tooltip });
            var field = new FloatField { value = value, isDelayed = true, tooltip = tooltip, style = { width = 46 } };
            field.RegisterValueChangedCallback(e => SetBlend(animation, property, e.newValue));
            _clipRow.Add(field);
        }

        private void SetBlend(SkillAnimationAsset animation, string property, float value)
        {
            // 混入不能超过动画时长（运行期定义会拒绝）；混出发生在动画之后，只要求非负。
            float max = property == "blendIn" ? animation.Duration : float.MaxValue;
            EditCommand.Run(_doc, property == "blendIn" ? "调整混入时长" : "调整混出时长", animation,
                so => so.FindProperty(property).floatValue = Mathf.Clamp(value, 0, max));
        }

        /// <summary>
        /// 动画条上的混入斜坡与混出尾巴。只负责绘制和两端手柄的拖拽，点击动画条本身仍穿透到下面。
        /// 拖动期间只改临时值，抬手一次写回，与时间轴其他元素一样形成一步撤销。
        /// </summary>
        private sealed class BlendRamps : VisualElement
        {
            private const float Height = 23;
            private readonly TimelinePanel _owner;
            private readonly SkillAnimationAsset _animation;
            private readonly VisualElement _inHandle, _outHandle;
            private float _blendIn, _blendOut;

            public BlendRamps(TimelinePanel owner, SkillAnimationAsset animation)
            {
                _owner = owner; _animation = animation;
                _blendIn = animation.BlendIn; _blendOut = animation.BlendOut;
                pickingMode = PickingMode.Ignore;
                style.position = UnityEngine.UIElements.Position.Absolute; style.top = 5; style.height = Height;
                generateVisualContent += Draw;
                _inHandle = Handle("blendIn"); _outHandle = Handle("blendOut");
                Layout();
            }

            public void Layout()
            {
                float zoom = _owner._zoom;
                style.left = -_owner._offset;
                style.width = (_owner.Duration + _blendOut) * zoom + 8;
                _inHandle.style.left = _blendIn * zoom - 4;
                _outHandle.style.left = (_owner.Duration + _blendOut) * zoom - 4;
                _inHandle.tooltip = $"混入 {_blendIn:0.###}s · 拖动调整";
                _outHandle.tooltip = $"混出 {_blendOut:0.###}s · 拖动调整";
                MarkDirtyRepaint();
            }

            private VisualElement Handle(string property)
            {
                var handle = new VisualElement { pickingMode = PickingMode.Position };
                handle.AddToClassList("blend-handle");
                bool dragging = false;
                handle.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button != 0) return;
                    dragging = true; handle.CapturePointer(e.pointerId); _owner.Focus(); e.StopPropagation();
                });
                handle.RegisterCallback<PointerMoveEvent>(e =>
                {
                    if (!dragging) return;
                    float x = this.WorldToLocal(e.position).x / _owner._zoom;
                    float t = TimelineMath.Snap(property == "blendIn" ? x : x - _owner.Duration, _owner.Fps, e.altKey);
                    if (property == "blendIn") _blendIn = Mathf.Clamp(t, 0, _owner.Duration); else _blendOut = Mathf.Max(0, t);
                    Layout(); e.StopPropagation();
                });
                handle.RegisterCallback<PointerUpEvent>(e =>
                {
                    if (!dragging) return;
                    dragging = false; handle.ReleasePointer(e.pointerId); e.StopPropagation();
                    _owner.SetBlend(_animation, property, property == "blendIn" ? _blendIn : _blendOut);
                });
                Add(handle);
                return handle;
            }

            private void Draw(MeshGenerationContext context)
            {
                float zoom = _owner._zoom, end = _owner.Duration * zoom, blendIn = _blendIn * zoom, blendOut = _blendOut * zoom;
                var painter = context.painter2D;
                painter.lineWidth = 1.5f;
                DrawCurve(0, blendIn, _animation.BlendInSpec, false);
                DrawCurve(end, blendOut, _animation.BlendOutSpec, true);
                if (_animation.sections != null) foreach (var section in _animation.sections)
                    if (section != null) DrawCurve(section.start * zoom, section.transition.duration * zoom, section.transition, false);
                void DrawCurve(float x, float width, SkillBlend spec, bool fadeOut)
                {
                    if (width < .5f) return;
                    var tint = spec.mode == SkillBlendMode.Freeze ? SkillColors.Cue : SkillColors.Animation;
                    painter.strokeColor = Color.Lerp(tint, Color.white, .45f);
                    painter.fillColor = new Color(tint.r, tint.g, tint.b, .3f);
                    Vector2 At(int i) { float u = i / 24f, w = spec.Evaluate(u); return new Vector2(x + width*u, Height * (fadeOut ? w : 1-w)); }
                    painter.BeginPath(); painter.MoveTo(At(0)); for (int i=1; i<=24; i++) painter.LineTo(At(i)); painter.Stroke();
                    painter.LineTo(new Vector2(x+width,Height)); painter.LineTo(new Vector2(x,Height)); painter.ClosePath(); painter.Fill();
                }
            }

        }

        private void SetClip(ClipInfo clip, SkillAnimationAsset animation)
        {
            EditCommand.Run(_doc, "选择动画并同步时长", animation, so =>
            {
                float ratio = clip.Length / Mathf.Max(.001f, animation.Duration);
                foreach (var track in TrackTypeRegistry.Types)
                {
                    var list = so.FindProperty(track.PropertyPath); if (list == null) continue;
                    for (int i = 0; i < list.arraySize; i++)
                    { var element = list.GetArrayElementAtIndex(i); var span = track.ReadSpan(element); track.WriteSpan(element, span.start * ratio, span.end < 0 ? -1 : span.end * ratio); }
                }
                so.FindProperty("clipName").stringValue = clip.Name; so.FindProperty("duration").floatValue = Mathf.Max(.001f, clip.Length);
                so.FindProperty("frameRate").floatValue = Mathf.Max(1, clip.FrameRate); so.FindProperty("loop").boolValue = clip.Loop;
            }, true);
            if (!string.IsNullOrEmpty(clip.Warning)) _doc.Report(clip.Warning);
            Seek(Mathf.Min(_doc.Playback.Time, Duration)); Fit();
        }
        private void BindDrag(ElementView element)
        {
            if(element.Track.PropertyPath=="markers") element.View.AddManipulator(new ContextualMenuManipulator(e=>e.menu.AppendAction("切换标记断点",_=>SkillActionDebugger.Toggle(element.Id))));
            float origin = 0, delta = 0; int resize = 0; bool dragging = false;
            ElementView[] moved = null;
            element.View.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0 || !float.IsFinite(e.position.x)) return; Focus();
                if (!e.ctrlKey && !e.shiftKey && !_selected.Contains(element.Key)) _selected.Clear();
                if (e.ctrlKey && _selected.Contains(element.Key))
                {
                    _selected.Remove(element.Key);
                    var last = _elements.LastOrDefault(x => _selected.Contains(x.Key));
                    _doc.Selection.Select(_doc.Root.Animation(), last?.Track.PropertyPath, last?.Id);
                    SelectionChanged(); e.StopPropagation(); return;
                }
                _selected.Add(element.Key);
                _doc.Selection.Select(_doc.Root.Animation(), element.Track.PropertyPath, element.Id);
                if (e.clickCount == 2 && element.Track.PropertyPath == "markers") { _doc.OpenFlow(); e.StopPropagation(); return; }
                if (e.clickCount == 2 && element.Track is HitWindowTrack) { Seek(element.Start); e.StopPropagation(); return; }
                moved = _elements.Where(x => _selected.Contains(x.Key)).ToArray();
                origin = e.position.x; delta = 0; dragging = true; _dragging = true;
                resize = element.Track.Shape == TrackElementShape.Range ? e.localPosition.x < 7 ? -1 : e.localPosition.x > element.View.layout.width - 7 ? 1 : 0 : 0;
                if (resize != 0) moved = new[] { element };
                element.View.CapturePointer(e.pointerId); SelectionChanged(); e.StopPropagation();
            });
            element.View.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging || moved.Length == 0 || !float.IsFinite(e.position.x)) return;
                float anchor = resize == 1 ? (element.End < 0 ? Duration : element.End) : element.Start;
                float candidate = TimelineMath.Snap(anchor + (e.position.x - origin) / _zoom, Fps, e.altKey);
                if (!e.altKey)
                {
                    var targets = _elements.Where(x => !moved.Contains(x)).SelectMany(x => new[] { x.Start, x.End < 0 ? Duration : x.End }).Append(_doc.Playback.Time);
                    foreach (float t in targets) if (Mathf.Abs(candidate - t) * _zoom < 6) { candidate = t; break; }
                }
                delta = candidate - anchor;
                if (resize == 0)
                {
                    float min = moved.Min(x => x.Start), max = moved.Max(x => x.Track.Shape == TrackElementShape.Point || x.End < 0 ? x.Start : x.End);
                    delta = Mathf.Clamp(delta, -min, Mathf.Max(0, Duration - max));
                    foreach (var item in moved) PreviewDrag(item, item.Start + delta, item.End < 0 ? -1 : item.End + delta);
                }
                else if (resize < 0) { delta = Mathf.Clamp(delta, -element.Start, (element.End < 0 ? Duration : element.End) - element.Start); PreviewDrag(element, element.Start + delta, element.End); }
                else { delta = Mathf.Clamp(delta, element.Start - anchor, Duration - anchor); PreviewDrag(element, element.Start, anchor + delta); }
            });
            element.View.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!dragging) return; dragging = false; _dragging = false; _dragSpans.Clear(); element.View.ReleasePointer(e.pointerId);
                if (!float.IsFinite(delta) || Mathf.Abs(delta) < .00001f) { FinishDrag(); return; }
                var data=_doc.Serialized(_doc.Root.Animation());
                var paths=moved.Select(item=>{
                    var p=TimelineCommands.Find(data,item.Track.PropertyPath,item.Id);
                    return p?.propertyPath+(item.Track.Shape==TrackElementShape.Point?".time":resize>0?".end":".start");
                }).ToArray();
                EditCommand.Run(_doc, "移动时间轴元素", _doc.Root.Animation(), so =>
                {
                    foreach (var item in moved)
                    {
                        var p = TimelineCommands.Find(so, item.Track.PropertyPath, item.Id); if (p == null) continue;
                        float start = item.Start + (resize <= 0 ? delta : 0);
                        float end = resize > 0 ? (item.End < 0 ? Duration : item.End) + delta : resize < 0 || item.End < 0 ? item.End : item.End + delta;
                        item.Track.WriteSpan(p, start, end);
                    }
                }, false,paths);
                FinishDrag();
                e.StopPropagation();
            });
            element.View.RegisterCallback<PointerCaptureOutEvent>(_ => { if (dragging) { dragging = false; _dragging = false; _dragSpans.Clear(); FinishDrag(); } });
        }
        private void PreviewDrag(ElementView element, float start, float end)
        { _dragSpans[element] = (start, end); Position(element, start, end); }
        private void FinishDrag()
        {
            if (_rebuildPending) Rebuild(new ChangeSet(_doc.Root));
            else { PackRows(); PositionElements(); }
        }
        private void Position(ElementView element, float start, float end)
        {
            element.View.style.left = TimelineMath.X(start, _zoom, _offset);
            element.View.style.width = element.Track.Shape == TrackElementShape.Point ? 130 : Mathf.Max(12, ((end < 0 ? Duration : end) - start) * _zoom);
            if(element.Track.PropertyPath=="runtimeTracks"){
                var label=element.View.Q<Label>(); if(label!=null){
                    bool shortBar=element.View.style.width.value.value<64;
                    // Keep the precise bar length. Its always-visible row heading carries the name for short spans.
                    label.style.display=shortBar?DisplayStyle.None:DisplayStyle.Flex;
                }}
            if (element.Ghost != null) element.Ghost.style.left = TimelineMath.X(start / _doc.Playback.Speed, _zoom, _offset);
        }
        private void PositionElements()
        {
            foreach (var element in _elements)
            { var span = _dragSpans.TryGetValue(element, out var drag) ? drag : (element.Start, element.End); Position(element, span.Item1, span.Item2); }
            var clip = _tracks.Q<Label>(className: "animation-clip"); if (clip != null) { clip.style.width = Duration * _zoom; clip.style.left = -_offset; }
            if (_clipCaption != null) { _clipCaption.style.left = -_offset + 7; _clipCaption.style.width = Mathf.Max(0, Duration * _zoom - 14); }
            _ramps?.Layout();
            PositionTail();
            PlaybackChanged();
        }
        private void PositionTail()
        {
            if(_tail==null)return;
            _tail.style.left=Duration*_zoom-_offset;_tail.style.width=Mathf.Max(0,_doc.PreviewEnd-Duration)*_zoom;
            _tail.style.display=_doc.PreviewEnd>Duration?DisplayStyle.Flex:DisplayStyle.None;
        }
        private void PreviewRangeChanged(){PositionTail();UpdateFrameData(_tracks.Q<Label>("frame-data"));PlaybackChanged();}
        private void DrawRuler()
        {
            _ruler.Clear(); float width = _ruler.layout.width; if (float.IsNaN(width) || width < 1) return;
            float step = TimelineMath.RulerStep(_zoom);
            int first = Mathf.Max(0, Mathf.FloorToInt(_offset / _zoom / step));
            for (int index = first; index * step < (_offset + width) / _zoom; index++)
            {
                float time = index * step;
                var tick = new Label(time.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "s") { pickingMode = PickingMode.Ignore }; tick.AddToClassList("ruler-tick");
                tick.style.left = TimelineMath.X(time, _zoom, _offset); _ruler.Add(tick);
            }
        }
        private void PlaybackChanged()
        {
            _head.EnableInClassList("live-playhead", _doc.Playback.Live);
            _play.SetEnabled(!_doc.Playback.Live);
            _clock.text = (_doc.Playback.Live ? "运行时 · " : "") + $"{_doc.Playback.Time:0.00}s / {Mathf.Max(Duration,_doc.PreviewEnd):0.00}s · {Mathf.RoundToInt(_doc.Playback.Time * Fps)}f";
            _play.text = _doc.Playback.Playing ? "Ⅱ" : "▶";
            // 用刚算出的位置判断：resolvedStyle 要等下一次布局才更新，读它会让平移后的显隐慢一拍。
            float headX = TimelineMath.X(_doc.Playback.Time, _zoom, _offset);
            _head.style.left = HeaderWidth + headX;
            _head.style.display = headX >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var item in _elements) item.View.EnableInClassList("playing-element", item.Track.Shape == TrackElementShape.Point
                ? Mathf.Abs(item.Start - _doc.Playback.Time) < .04f : _doc.Playback.Time >= item.Start && _doc.Playback.Time < (item.End < 0 ? Duration : item.End));
        }
        private void SelectionChanged()
        {
            if (_doc.Selection.Track != null && _doc.Selection.ElementId != null) _selected.Add(_doc.Selection.Track + "/" + _doc.Selection.ElementId);
            foreach (var item in _elements) item.View.EnableInClassList("selected-element", _selected.Contains(item.Key));
            var selected = _elements.FirstOrDefault(e => e.Id == _doc.Selection.ElementId && e.Track.PropertyPath == _doc.Selection.Track);
            if (selected == null || _dragging) return;
            var row = _rows.First(r => r.Elements.Contains(selected));
            if (row.CueTag != null && _foldedCues.Remove(row.CueTag)) PackRows();
            schedule.Execute(() => { if (selected.View.panel != null && !_dragging) _scroll.ScrollTo(selected.View); });
        }
        private void Add(ITrackType track) => TimelineCommands.Add(_doc, track, TimelineMath.Snap(_doc.Playback.Time, Fps));
        private void DeleteSelection()
        {
            var groups = _elements.Where(e => _selected.Contains(e.Key)).GroupBy(e => e.Track.PropertyPath).ToArray();
            if (groups.Length == 0) return;
            EditCommand.Run(_doc, "删除时间轴元素", _doc.Root.Animation(), so =>
            {
                foreach (var group in groups)
                {
                    var ids = new HashSet<string>(group.Select(e => e.Id)); var array = so.FindProperty(group.Key);
                    for (int i = array.arraySize - 1; i >= 0; i--)
                        if (ids.Contains(array.GetArrayElementAtIndex(i).FindPropertyRelative("id").stringValue)) array.DeleteArrayElementAtIndex(i);
                }
            }, true);
            _doc.Selection.Select(_doc.Root.Animation());
            _selected.Clear();
        }
        private void Copy()
        {
            if (_doc.Root?.Animation() == null) return;
            var items = _elements.Where(e => _selected.Contains(e.Key)).ToArray(); if (items.Length == 0) return;
            _clipboard = EditorJsonUtility.ToJson(_doc.Root.Animation()); _copyIds = items.Select(e => (e.Track.PropertyPath, e.Id)).ToArray(); _copyOrigin = items.Min(e => e.Start);
        }
        private void Paste()
        {
            if (_clipboard == null || _doc.Root?.Animation() == null) return;
            var temp = ScriptableObject.CreateInstance<SkillAnimationAsset>();
            try
            {
                EditorJsonUtility.FromJsonOverwrite(_clipboard, temp);
                using var source = new SerializedObject(temp);
                EditCommand.Run(_doc, "粘贴时间轴元素", _doc.Root.Animation(), so =>
                {
                    foreach (var item in _copyIds)
                    {
                        var from = TimelineCommands.Find(source, item.track, item.id); if (from == null) continue;
                        var track = TrackTypeRegistry.Get(item.track); var span = track.ReadSpan(from); var array = so.FindProperty(item.track);
                        int index = array.arraySize++; var to = array.GetArrayElementAtIndex(index); to.boxedValue = from.boxedValue;
                        to.FindPropertyRelative("id").stringValue = Guid.NewGuid().ToString("N");
                        if (item.track == "markers") to.FindPropertyRelative("key").stringValue = "";
                        float start = Mathf.Clamp(span.start - _copyOrigin + _doc.Playback.Time, 0, Duration);
                        track.WriteSpan(to, start, span.end < 0 ? -1 : Mathf.Min(Duration, start + span.end - span.start));
                    }
                }, true);
            }
            finally { UnityEngine.Object.DestroyImmediate(temp); }
        }
        private void OnKey(KeyDownEvent e)
        {
            if (e.target is TextElement || e.target is TextField || _doc.Root?.Animation() == null) return;
            switch (e.keyCode)
            {
                case KeyCode.Space: TogglePlay(); break;
                case KeyCode.Comma: Seek(_doc.Playback.Time - 1 / Fps); break;
                case KeyCode.Period: Seek(_doc.Playback.Time + 1 / Fps); break;
                case KeyCode.Home: Seek(0); break;
                  case KeyCode.End: Seek(_doc.PlaybackDuration); break;
                case KeyCode.L: _doc.Playback.Loop = !_doc.Playback.Loop; break;
                case KeyCode.M: Add(TrackTypeRegistry.Get("markers")); break;
                case KeyCode.R:
                    if (_rangeStartId == null) _rangeStartId = TimelineCommands.Add(_doc, TrackTypeRegistry.Get("cueRegions"), _doc.Playback.Time);
                    else { EditCommand.Run(_doc, "设置区间终点", _doc.Root.Animation(), so => { var p = TimelineCommands.Find(so, "cueRegions", _rangeStartId); if (p != null) p.FindPropertyRelative("end").floatValue = Mathf.Max(p.FindPropertyRelative("start").floatValue, _doc.Playback.Time); }); _rangeStartId = null; }
                    break;
                case KeyCode.Delete: case KeyCode.Backspace: DeleteSelection(); break;
                case KeyCode.C: if (e.ctrlKey) Copy(); else return; break;
                case KeyCode.V: if (e.ctrlKey) Paste(); else return; break;
                default: return;
            }
            e.StopPropagation();
        }
        public void Dispose() { _doc.Changed -= Rebuild; _doc.Selection.Changed -= SelectionChanged; _doc.Playback.Changed -= PlaybackChanged; _doc.PreviewRangeChanged-=PreviewRangeChanged; }
    }
}
