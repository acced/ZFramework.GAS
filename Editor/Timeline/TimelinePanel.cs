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
        private const float HeaderWidth = 146, RowHeight = 33;
        private readonly SkillDocument _doc;
        private readonly VisualElement _tracks, _ruler, _head;
        private readonly Label _clock;
        private readonly Button _play;
        private readonly List<ElementView> _elements = new();
        private readonly HashSet<string> _selected = new();
        private float _zoom = 500, _offset;
        private bool _scrub, _pan;
        private float _panX;
        private string _rangeStartId;
        private string _clipboard;
        private (string track, string id)[] _copyIds;
        private float _copyOrigin;
        private readonly VisualElement _clipRow;
        private readonly VisualElement _marquee;
        private bool _selecting;
        private Vector2 _selectionStart;
        private HashSet<string> _selectionBefore;
        private sealed class ElementView
        {
            public ITrackType Track; public string Id; public float Start, End; public VisualElement View;
            public string Key => Track.PropertyPath + "/" + Id;
        }
        public TimelinePanel(SkillDocument doc)
        {
            _doc = doc; focusable = true; AddToClassList("timeline-panel");
            var toolbar = new Toolbar(); toolbar.AddToClassList("timeline-toolbar");
            toolbar.Add(new ToolbarButton(() => Seek(0)) { text = "|◀", tooltip = "跳到开始 Home" });
            _play = new ToolbarButton(TogglePlay) { text = "▶", tooltip = "播放 / 暂停 Space" }; toolbar.Add(_play);
            toolbar.Add(new ToolbarButton(() => Seek(Duration)) { text = "▶|", tooltip = "跳到末尾 End" });
            var loop = new ToolbarToggle { text = "循环", value = doc.Playback.Loop }; loop.RegisterValueChangedCallback(e => doc.Playback.Loop = e.newValue); toolbar.Add(loop);
            _clock = new Label(); _clock.AddToClassList("timeline-clock"); toolbar.Add(_clock);
            var speed = new PopupField<string>(new List<string> { "×0.5", "×1.0", "×1.5", "×2.0", "×3.0" }, 1) { tooltip = "攻速预览；虚影显示实际触发时间" };
            speed.style.width = 72; speed.RegisterValueChangedCallback(e => { doc.Playback.Speed = float.Parse(e.newValue.Substring(1), System.Globalization.CultureInfo.InvariantCulture); Rebuild(new ChangeSet(doc.Root)); }); toolbar.Add(speed);
            var add = new ToolbarMenu { text = "+ 轨道" };
            foreach (var type in TrackTypeRegistry.Types) { var track = type; add.menu.AppendAction(track.DisplayName, _ => Add(track)); } toolbar.Add(add);
            toolbar.Add(new ToolbarButton(Fit) { text = "适应", tooltip = "缩放至完整动画" }); Add(toolbar);
            _clipRow = new VisualElement(); _clipRow.AddToClassList("clip-picker-row"); Add(_clipRow);
            _ruler = new VisualElement { style = { height = 24, marginLeft = HeaderWidth, overflow = Overflow.Hidden } }; _ruler.AddToClassList("time-ruler"); Add(_ruler);
            _tracks = new VisualElement { style = { flexGrow = 1, minHeight = RowHeight * 5, overflow = Overflow.Hidden } }; Add(_tracks);
            _head = new VisualElement { pickingMode = PickingMode.Ignore }; _head.AddToClassList("playhead"); _tracks.Add(_head);
            _marquee = new VisualElement { pickingMode = PickingMode.Ignore, style = { display = DisplayStyle.None } };
            _marquee.AddToClassList("timeline-marquee");
            _ruler.RegisterCallback<PointerDownEvent>(e => { if (e.button != 0) return; _scrub = true; Focus(); _ruler.CapturePointer(e.pointerId); Seek(TimeAt(e.localPosition.x)); });
            _ruler.RegisterCallback<PointerMoveEvent>(e => { if (_scrub) Seek(TimeAt(e.localPosition.x)); });
            _ruler.RegisterCallback<PointerUpEvent>(e => { _scrub = false; _ruler.ReleasePointer(e.pointerId); });
            RegisterCallback<WheelEvent>(e =>
            {
                float local = e.mousePosition.x - worldBound.x - HeaderWidth;
                float time = TimeAt(local); _zoom = Mathf.Clamp(_zoom * (e.delta.y < 0 ? 1.15f : .87f), 30, 4000);
                _offset = Mathf.Max(0, time * _zoom - local); PositionElements(); DrawRuler(); e.StopPropagation();
            });
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
                _doc.Selection.Select(_doc.Root?.Animation, last?.Track.PropertyPath, last?.Id);
            });
            _tracks.RegisterCallback<PointerCaptureOutEvent>(_ => { _selecting = false; _marquee.style.display = DisplayStyle.None; });
            RegisterCallback<KeyDownEvent>(OnKey);
            RegisterCallback<GeometryChangedEvent>(_ => { DrawRuler(); PositionElements(); });
            doc.Changed += Rebuild; doc.Selection.Changed += SelectionChanged; doc.Playback.Changed += PlaybackChanged;
        }
        private float Duration => ValidPositive(_doc.Root?.Animation?.Duration ?? 1, 1);
        private float Fps => ValidPositive(_doc.Root?.Animation?.FrameRate ?? 30, 30);
        private static float ValidPositive(float value, float fallback) => value > 0 && !float.IsInfinity(value) ? value : fallback;
        private float TimeAt(float x) => TimelineMath.Time(x, _zoom, _offset);
        private void Seek(float time) => _doc.Playback.Seek(time, Duration);
        public void TogglePlay() { _doc.Playback.Playing = !_doc.Playback.Playing; if (_doc.Playback.Time >= Duration) Seek(0); PlaybackChanged(); }
        public void Fit() { _zoom = Mathf.Clamp(Mathf.Max(100, resolvedStyle.width - HeaderWidth - 24) / Duration, 30, 4000); _offset = 0; PositionElements(); DrawRuler(); }
        public void Rebuild(ChangeSet change)
        {
            _elements.Clear(); _tracks.Clear(); _clipRow.Clear();
            var animation = _doc.Root?.Animation;
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
            AddRow("▶  动画", SkillColors.Animation, null);
            var clipLane = _tracks[0][1];
            var clip = new Label(animation.ClipName + $"  ·  {animation.Duration:0.##}s"); clip.AddToClassList("animation-clip"); clip.style.backgroundColor = SkillColors.Animation;
            clip.style.width = Duration * _zoom; clip.style.left = -_offset; clipLane.Add(clip);
            clip.RegisterCallback<PointerDownEvent>(_ => { _doc.Selection.Select(animation); Focus(); });
            foreach (var track in TrackTypeRegistry.Types)
            {
                var lane = AddRow((track.Shape == TrackElementShape.Point ? "◆  " : "▰  ") + track.DisplayName, track.Color, track);
                var array = _doc.Serialized(animation).FindProperty(track.PropertyPath); if (array == null) continue;
                for (int i = 0; i < array.arraySize; i++)
                {
                    var item = array.GetArrayElementAtIndex(i); var span = track.ReadSpan(item);
                    var element = new ElementView { Track = track, Id = item.FindPropertyRelative("id").stringValue, Start = span.start, End = span.end };
                    var view = new VisualElement { focusable = true }; element.View = view;
                    view.AddToClassList(track.Shape == TrackElementShape.Point ? "timeline-event" : "timeline-range");
                    view.style.backgroundColor = track.Shape == TrackElementShape.Point ? Color.clear : track.Color;
                    if (track.Shape == TrackElementShape.Point)
                    {
                        var diamond = new VisualElement { pickingMode = PickingMode.Ignore };
                        diamond.AddToClassList("event-diamond"); diamond.style.backgroundColor = track.Color; view.Add(diamond);
                    }
                    var label = new Label(track.Label(item)); label.pickingMode = PickingMode.Ignore; view.Add(label);
                    view.tooltip = $"{track.Label(item)}  ·  {span.start:0.000}s" + (track.Shape == TrackElementShape.Range ? $" → {(span.end < 0 ? "随动画结束" : span.end.ToString("0.000") + "s")}" : "");
                    if (span.start > Duration || span.start < 0 || span.end > Duration) view.AddToClassList("invalid-element");
                    lane.Add(view); _elements.Add(element); BindDrag(element);
                    if (Mathf.Abs(_doc.Playback.Speed - 1) > .01f)
                    {
                        var ghost = new Label("◇") { pickingMode = PickingMode.Ignore }; ghost.AddToClassList("speed-ghost");
                        ghost.style.left = TimelineMath.X(span.start / _doc.Playback.Speed, _zoom, _offset); lane.Add(ghost);
                    }
                }
            }
            _tracks.Add(_head); _tracks.Add(_marquee); PositionElements(); DrawRuler(); SelectionChanged(); PlaybackChanged();
        }
        private VisualElement AddRow(string title, Color color, ITrackType track)
        {
            var row = new VisualElement(); row.AddToClassList("track-row"); row.style.height = RowHeight;
            var heading = new Label(title); heading.AddToClassList("track-heading"); heading.style.width = HeaderWidth; heading.style.borderLeftColor = color; row.Add(heading);
            var lane = new VisualElement(); lane.AddToClassList("track-lane"); row.Add(lane); _tracks.Add(row);
            if (track != null)
            {
                lane.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.target != lane || e.button != 0) return;
                    Focus(); Seek(Mathf.Clamp(TimeAt(e.localPosition.x), 0, Duration));
                    if (e.clickCount == 2) TimelineCommands.Add(_doc, track, TimelineMath.Snap(_doc.Playback.Time, Fps));
                });
                lane.AddManipulator(new ContextualMenuManipulator(e =>
                {
                    float time = Mathf.Clamp(TimeAt(lane.WorldToLocal(e.mousePosition).x), 0, Duration);
                    e.menu.AppendAction("添加" + track.DisplayName, _ => TimelineCommands.Add(_doc, track, time));
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
            return lane;
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
            var fps = new FloatField { value = animation.FrameRate, isDelayed = true, tooltip = "帧率 / 吸附单位", style = { width = 50 } };
            fps.RegisterValueChangedCallback(e => EditCommand.Run(_doc, "修改吸附帧率", animation, so => so.FindProperty("frameRate").floatValue = Mathf.Max(1, e.newValue)));
            _clipRow.Add(fps); _clipRow.Add(new Label("fps"));
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
            float origin = 0, delta = 0; int resize = 0; bool dragging = false;
            ElementView[] moved = null;
            element.View.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return; Focus();
                if (!e.ctrlKey && !e.shiftKey && !_selected.Contains(element.Key)) _selected.Clear();
                if (e.ctrlKey && _selected.Contains(element.Key))
                {
                    _selected.Remove(element.Key);
                    var last = _elements.LastOrDefault(x => _selected.Contains(x.Key));
                    _doc.Selection.Select(_doc.Root.Animation, last?.Track.PropertyPath, last?.Id);
                    SelectionChanged(); e.StopPropagation(); return;
                }
                _selected.Add(element.Key);
                _doc.Selection.Select(_doc.Root.Animation, element.Track.PropertyPath, element.Id);
                moved = _elements.Where(x => _selected.Contains(x.Key)).ToArray();
                origin = e.position.x; delta = 0; dragging = true;
                resize = element.Track.Shape == TrackElementShape.Range ? e.localPosition.x < 7 ? -1 : e.localPosition.x > element.View.layout.width - 7 ? 1 : 0 : 0;
                if (resize != 0) moved = new[] { element };
                element.View.CapturePointer(e.pointerId); SelectionChanged(); e.StopPropagation();
            });
            element.View.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging || moved.Length == 0) return;
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
                    foreach (var item in moved) Position(item, item.Start + delta, item.End < 0 ? -1 : item.End + delta);
                }
                else if (resize < 0) { delta = Mathf.Clamp(delta, -element.Start, (element.End < 0 ? Duration : element.End) - element.Start); Position(element, element.Start + delta, element.End); }
                else { delta = Mathf.Clamp(delta, element.Start - anchor, Duration - anchor); Position(element, element.Start, anchor + delta); }
            });
            element.View.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!dragging) return; dragging = false; element.View.ReleasePointer(e.pointerId);
                if (Mathf.Abs(delta) < .00001f) return;
                EditCommand.Run(_doc, "移动时间轴元素", _doc.Root.Animation, so =>
                {
                    foreach (var item in moved)
                    {
                        var p = TimelineCommands.Find(so, item.Track.PropertyPath, item.Id); if (p == null) continue;
                        float start = item.Start + (resize <= 0 ? delta : 0);
                        float end = resize > 0 ? (item.End < 0 ? Duration : item.End) + delta : resize < 0 || item.End < 0 ? item.End : item.End + delta;
                        item.Track.WriteSpan(p, start, end);
                    }
                }, false);
                e.StopPropagation();
            });
            element.View.RegisterCallback<PointerCaptureOutEvent>(_ => { if (dragging) { dragging = false; PositionElements(); } });
        }
        private void Position(ElementView element, float start, float end)
        {
            element.View.style.left = TimelineMath.X(start, _zoom, _offset);
            element.View.style.width = element.Track.Shape == TrackElementShape.Point ? 130 : Mathf.Max(12, ((end < 0 ? Duration : end) - start) * _zoom);
        }
        private void PositionElements()
        {
            foreach (var element in _elements) Position(element, element.Start, element.End);
            var clip = _tracks.Q<Label>(className: "animation-clip"); if (clip != null) { clip.style.width = Duration * _zoom; clip.style.left = -_offset; }
            PlaybackChanged();
        }
        private void DrawRuler()
        {
            _ruler.Clear(); float width = _ruler.layout.width; if (float.IsNaN(width) || width < 1) return;
            float step = 1f / Fps; while (step * _zoom < 45) step *= 2;
            for (float time = Mathf.Floor(_offset / _zoom / step) * step; time < (_offset + width) / _zoom; time += step)
            {
                var tick = new Label(time.ToString("0.##") + "s") { pickingMode = PickingMode.Ignore }; tick.AddToClassList("ruler-tick");
                tick.style.left = TimelineMath.X(time, _zoom, _offset); _ruler.Add(tick);
            }
        }
        private void PlaybackChanged()
        {
            _clock.text = $"{_doc.Playback.Time:0.00}s / {Duration:0.00}s · {Mathf.RoundToInt(_doc.Playback.Time * Fps)}f";
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
        }
        private void Add(ITrackType track) => TimelineCommands.Add(_doc, track, TimelineMath.Snap(_doc.Playback.Time, Fps));
        private void DeleteSelection()
        {
            var groups = _elements.Where(e => _selected.Contains(e.Key)).GroupBy(e => e.Track.PropertyPath).ToArray();
            if (groups.Length == 0) return;
            EditCommand.Run(_doc, "删除时间轴元素", _doc.Root.Animation, so =>
            {
                foreach (var group in groups)
                {
                    var ids = new HashSet<string>(group.Select(e => e.Id)); var array = so.FindProperty(group.Key);
                    for (int i = array.arraySize - 1; i >= 0; i--)
                        if (ids.Contains(array.GetArrayElementAtIndex(i).FindPropertyRelative("id").stringValue)) array.DeleteArrayElementAtIndex(i);
                }
            }, true);
            _doc.Selection.Select(_doc.Root.Animation);
            _selected.Clear();
        }
        private void Copy()
        {
            if (_doc.Root?.Animation == null) return;
            var items = _elements.Where(e => _selected.Contains(e.Key)).ToArray(); if (items.Length == 0) return;
            _clipboard = EditorJsonUtility.ToJson(_doc.Root.Animation); _copyIds = items.Select(e => (e.Track.PropertyPath, e.Id)).ToArray(); _copyOrigin = items.Min(e => e.Start);
        }
        private void Paste()
        {
            if (_clipboard == null || _doc.Root?.Animation == null) return;
            var temp = ScriptableObject.CreateInstance<SkillAnimationAsset>();
            try
            {
                EditorJsonUtility.FromJsonOverwrite(_clipboard, temp);
                using var source = new SerializedObject(temp);
                EditCommand.Run(_doc, "粘贴时间轴元素", _doc.Root.Animation, so =>
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
            if (e.target is TextElement || e.target is TextField || _doc.Root?.Animation == null) return;
            switch (e.keyCode)
            {
                case KeyCode.Space: TogglePlay(); break;
                case KeyCode.Comma: Seek(_doc.Playback.Time - 1 / Fps); break;
                case KeyCode.Period: Seek(_doc.Playback.Time + 1 / Fps); break;
                case KeyCode.Home: Seek(0); break;
                case KeyCode.End: Seek(Duration); break;
                case KeyCode.L: _doc.Playback.Loop = !_doc.Playback.Loop; break;
                case KeyCode.M: Add(TrackTypeRegistry.Get("markers")); break;
                case KeyCode.R:
                    if (_rangeStartId == null) _rangeStartId = TimelineCommands.Add(_doc, TrackTypeRegistry.Get("cueRegions"), _doc.Playback.Time);
                    else { EditCommand.Run(_doc, "设置区间终点", _doc.Root.Animation, so => { var p = TimelineCommands.Find(so, "cueRegions", _rangeStartId); if (p != null) p.FindPropertyRelative("end").floatValue = Mathf.Max(p.FindPropertyRelative("start").floatValue, _doc.Playback.Time); }); _rangeStartId = null; }
                    break;
                case KeyCode.Delete: case KeyCode.Backspace: DeleteSelection(); break;
                case KeyCode.C: if (e.ctrlKey) Copy(); else return; break;
                case KeyCode.V: if (e.ctrlKey) Paste(); else return; break;
                default: return;
            }
            e.StopPropagation();
        }
        public void Dispose() { _doc.Changed -= Rebuild; _doc.Selection.Changed -= SelectionChanged; _doc.Playback.Changed -= PlaybackChanged; }
    }
}
