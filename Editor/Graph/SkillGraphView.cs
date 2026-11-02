using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    /// <summary>GraphView is confined to this view. Port rules and commands have no GraphView dependencies.</summary>
    public sealed class SkillGraphView : GraphView, IDisposable
    {
        private readonly SkillDocument _doc;
        private readonly Dictionary<ScriptableObject, SkillNode> _nodes = new();
        private readonly Dictionary<string, Edge> _edges = new();
        private readonly HashSet<ScriptableObject> _pendingPlacement = new();
        private readonly Dictionary<ScriptableObject, Vector2> _preferredPositions = new();
        private readonly Dictionary<ScriptableObject, Vector2> _measuredSizes = new();
        private readonly Dictionary<ScriptableObject, Vector2> _layoutPositions = new();
        private IVisualElementScheduledItem _layoutWork;
        private bool _arrangeRequested, _frameRequested, _framing, _disposed;
        private readonly HashSet<ScriptableObject> _liveAssets = new();
        private readonly VisualElement _miniMapHost;
        public bool MiniMapVisible
        {
            get => _miniMapHost.style.display.value != DisplayStyle.None;
            set => _miniMapHost.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
        }
        private bool _syncing;
        internal bool IsSyncing => _syncing;
        private GameplayAbilityAsset _root;
        private Vector2 _mouse;
        private readonly List<Group> _groups = new();
        private ScriptableObject[] _clipboard = Array.Empty<ScriptableObject>();
        public int NodeCount => _nodes.Count;
        public SkillGraphView(SkillDocument doc)
        {
            _doc = doc; style.flexGrow = 1; focusable = true;
            SetupZoom(.2f, 1.8f);
            var grid = new GridBackground(); Insert(0, grid); grid.StretchToParentSize();
            this.AddManipulator(new ContentDragger()); this.AddManipulator(new SelectionDragger()); this.AddManipulator(new RectangleSelector());
            _miniMapHost = new VisualElement { name = "minimap-overlay" }; _miniMapHost.AddToClassList("skill-minimap-overlay");
            // MiniMap.Resize 会重写自身的 left/top。外层固定角落，内层原点保持为零，隔离它的自动定位。
            var surface = new VisualElement { style = { flexGrow = 1 } }; _miniMapHost.Add(surface);
            var minimap = new MiniMap { anchored = true, maxWidth = 150, maxHeight = 92 };
            minimap.AddToClassList("skill-minimap"); minimap.Q<Label>().style.display = DisplayStyle.None;
            surface.Add(minimap); Add(_miniMapHost);
            graphViewChanged = OnGraphChanged;
            nodeCreationRequest = ctx => Search(ctx.screenMousePosition);
            RegisterCallback<MouseMoveEvent>(e => _mouse = e.localMousePosition);
            RegisterCallback<KeyDownEvent>(OnKey);
            RegisterCallback<DragUpdatedEvent>(e => { if (DragAndDrop.objectReferences.Any(o => o is ScriptableObject)) DragAndDrop.visualMode = DragAndDropVisualMode.Link; });
            RegisterCallback<DragPerformEvent>(e =>
            {
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is GameplayAbilityAsset ability) { SkillEditorWindow.Open().Show(ability); break; }
                    if (obj is GameplayEffectAsset || obj is SkillAnimationAsset)
                    { var asset = (ScriptableObject)obj; _doc.AddLoose(asset); SuggestPosition(asset, contentViewContainer.WorldToLocal(e.mousePosition)); _doc.Selection.Select(asset); }
                }
                DragAndDrop.AcceptDrag(); e.StopPropagation();
            });
            doc.Changed += Sync; doc.Selection.Changed += SyncSelection;
        }
        public void Sync(ChangeSet change)
        {
            _syncing = true;
            try
            {
                bool rootChanged = _root != _doc.Root;
                if (rootChanged)
                {
                    _root = _doc.Root;
                    _pendingPlacement.Clear(); _preferredPositions.Clear(); _arrangeRequested = false;
                    foreach (var node in _nodes.Values) RemoveElement(node); _nodes.Clear();
                    foreach (var group in _groups) RemoveElement(group); _groups.Clear();
                    foreach (var comment in _doc.Layout.Comments)
                    {
                        var group = new Group { title = comment.title }; group.SetPosition(comment.rect); AddElement(group); _groups.Add(group);
                    }
                }
                var live = new HashSet<ScriptableObject>(_doc.Assets);
                foreach (var pair in _nodes.ToArray())
                    if (!live.Contains(pair.Key)) { RemoveElement(pair.Value); _nodes.Remove(pair.Key); }
                foreach (var asset in _doc.Assets)
                {
                    if (!_nodes.TryGetValue(asset, out var node))
                    {
                        node = new SkillNode(_doc, this, asset); _nodes[asset] = node; AddElement(node);
                        bool saved = _doc.Layout.TryGetPosition(asset, out var point);
                        node.SetPosition(new Rect(saved ? point : new Vector2(40, 50), Vector2.zero));
                        node.style.height = StyleKeyword.Auto;
                        if (!saved) _pendingPlacement.Add(asset);
                    }
                    node.Sync(); node.EnableInClassList("live-active", _liveAssets.Contains(asset));
                }
                var links = _doc.Assets.SelectMany(PortRules.Links).GroupBy(l => l.Key).Select(g => g.First()).ToArray();
                var keys = new HashSet<string>(links.Select(l => l.Key));
                foreach (var pair in _edges.ToArray())
                {
                    if (!keys.Contains(pair.Key) || pair.Value.output?.panel == null || pair.Value.input?.panel == null)
                    { pair.Value.output?.Disconnect(pair.Value); pair.Value.input?.Disconnect(pair.Value); RemoveElement(pair.Value); _edges.Remove(pair.Key); }
                }
                foreach (var link in links)
                {
                    if (_edges.ContainsKey(link.Key) || !_nodes.TryGetValue(link.Parent, out var from) || !_nodes.TryGetValue(link.Child, out var to)) continue;
                    var port = from.Output(link.Port); if (port == null) continue;
                    var edge = port.ConnectTo(to.Input); edge.userData = link; AddElement(edge); _edges[link.Key] = edge;
                }
                _pendingPlacement.IntersectWith(live);
                _frameRequested |= rootChanged;
                QueueLayout();
                SyncSelection();
            }
            finally { _syncing = false; }
        }
        private GraphViewChange OnGraphChanged(GraphViewChange change)
        {
            if (_syncing) return change;
            if (change.movedElements != null)
            {
                if (_framing) { _framing = false; _frameRequested = false; _layoutWork?.Pause(); }
                foreach (var element in change.movedElements)
                    if (element is SkillNode node)
                    { _pendingPlacement.Remove(node.Asset); _doc.Layout.SetPosition(node.Asset, node.GetPosition().position); }
                SaveComments(); _doc.Layout.Save();
            }
            if (change.elementsToRemove != null)
            {
                var removed = change.elementsToRemove.ToArray(); change.elementsToRemove.Clear();
                schedule.Execute(() =>
                {
                    var deletedNodes = removed.OfType<SkillNode>().ToArray();
                    var deletedAssets = new HashSet<ScriptableObject>(deletedNodes.Select(n => n.Asset));
                    // 节点删除会清理自己的进出引用；只断开两端都保留的连线，混选时也不丢弃用户选中的连线。
                    var links = removed.OfType<Edge>().Select(e => e.userData).OfType<AssetLink>()
                        .Where(l => l.Parent != null && !deletedAssets.Contains(l.Parent) && !deletedAssets.Contains(l.Child)).ToArray();
                    if (deletedNodes.Length > 0 || links.Length > 0)
                        EditCommand.Transaction(_doc, deletedNodes.Length > 0 ? "删除选中节点" : "断开连线", _doc.Root, () =>
                        {
                            foreach (var link in links)
                            { using var so = new SerializedObject(link.Parent); PortRules.Write(so, link.Port, link.Child, true); so.ApplyModifiedProperties(); }
                            foreach (var node in deletedNodes) SkillAssetFactory.DeleteNode(_doc, node.Asset);
                        });
                    foreach (var group in removed.OfType<Group>()) { _groups.Remove(group); RemoveElement(group); } SaveComments();
                });
            }
            return change;
        }
        public void Search(Vector2 screen, ScriptableObject parent = null, PortId? port = null)
        {
            if (_doc.Root == null) { _doc.Report("先从技能库选择或新建一个技能"); return; }
            var provider = ScriptableObject.CreateInstance<SkillNodeSearchProvider>();
            Vector2 graphPosition = contentViewContainer.WorldToLocal(screen - (EditorWindow.focusedWindow != null ? EditorWindow.focusedWindow.position.position : Vector2.zero));
            provider.Initialize(_doc, parent, port, graphPosition, this);
            SearchWindow.Open(new SearchWindowContext(screen), provider);
        }
        public override List<Port> GetCompatiblePorts(Port start, NodeAdapter adapter)
        {
            var result = new List<Port>();
            foreach (var port in ports.ToList())
            {
                if (port == start || port.node == start.node || port.direction == start.direction) continue;
                var output = start.direction == Direction.Output ? start : port;
                var input = start.direction == Direction.Input ? start : port;
                if (output.userData is PortId id && output.node is SkillNode from && input.node is SkillNode to)
                {
                    bool valid = PortRules.CanAttach(from.Asset, id, to.Asset, out string error, _doc);
                    port.tooltip = error ?? id.Label;
                    if (valid) result.Add(port);
                }
            }
            return result;
        }
        private void SyncSelection()
        {
            bool previous = _syncing; _syncing = true;
            try
            {
                GraphElement selected = _doc.Selection.Asset is ScriptableObject asset && _nodes.TryGetValue(asset, out var node) ? node : null;
                if (selected != null && !selection.Contains(selected)) { ClearSelection(); AddToSelection(selected); }
            }
            finally { _syncing = previous; }
            foreach (var pair in _nodes)
            {
                pair.Value.EnableInClassList("document-selected", pair.Key == _doc.Selection.Asset);
                pair.Value.SyncCueSelection();
            }
            foreach (var edge in _edges.Values)
                if (edge.userData is AssetLink link)
                    edge.selected = link.Parent == _doc.Selection.Asset && link.Port.ElementId == _doc.Selection.ElementId && link.Port.Kind == PortKind.Marker;
        }
        public void FocusAsset(Object asset)
        {
            if (asset is ScriptableObject so && _nodes.TryGetValue(so, out var node))
            { ClearSelection(); AddToSelection(node); FrameSelection(); }
        }
        public void ApplyIssues(IReadOnlyList<Issue> issues)
        { foreach (var pair in _nodes) pair.Value.SetIssues(issues.Where(i => i.Asset == pair.Key).ToArray()); }
        public void SetLive(IReadOnlyCollection<ScriptableObject> assets)
        {
            _liveAssets.Clear();
            if (assets != null) foreach (var asset in assets) if (asset != null) _liveAssets.Add(asset);
            foreach (var pair in _nodes) pair.Value.EnableInClassList("live-active", _liveAssets.Contains(pair.Key));
        }
        public void AutoArrange()
        { _arrangeRequested = true; _frameRequested = true; QueueLayout(); }

        // 搜索菜单提供初始位置；已经摆好的节点只保留原位。
        internal void SuggestPosition(ScriptableObject asset, Vector2 point)
        {
            if (asset == null || _doc.Layout.TryGetPosition(asset, out _)) return;
            _preferredPositions[asset] = point; _pendingPlacement.Add(asset); QueueLayout();
        }
        private void QueueLayout()
        {
            _layoutWork?.Pause(); _measuredSizes.Clear(); _framing = false;
            if (_disposed || (!_arrangeRequested && !_frameRequested && _pendingPlacement.Count == 0)) return;
            _layoutWork = schedule.Execute(LayoutWhenMeasured).Every(16);
        }
        private void LayoutWhenMeasured()
        {
            if (_disposed || _nodes.Count == 0) { _layoutWork?.Pause(); return; }
            if (_framing)
            {
                // SetPosition 后再等实际布局更新；否则 FrameAll 仍会按旧坐标取景。
                if (_nodes.Any(p => (p.Value.GetPosition().position - _layoutPositions[p.Key]).sqrMagnitude > 1)) return;
                _layoutWork.Pause(); _framing = false; _frameRequested = false; FrameAll(); return;
            }
            // 字段绑定和 Cue 条会改变高度。连续两次取得相同的有效尺寸后才定位。
            bool stable = true;
            foreach (var pair in _nodes)
            {
                Vector2 size = pair.Value.layout.size;
                if (!(size.x > 0 && size.y > 0) || float.IsInfinity(size.x) || float.IsInfinity(size.y)) return;
                if (!_measuredSizes.TryGetValue(pair.Key, out var previous) || (size - previous).sqrMagnitude > .01f) stable = false;
                _measuredSizes[pair.Key] = size;
            }
            if (!stable) return;
            _layoutPositions.Clear();
            foreach (var pair in _nodes) _layoutPositions[pair.Key] = pair.Value.GetPosition().position;
            if (_arrangeRequested) ArrangeMeasured(); else PlaceNewNodes();
            _arrangeRequested = false; _pendingPlacement.Clear(); _preferredPositions.Clear();
            _doc.Layout.Save();
            if (_frameRequested) _framing = true; else _layoutWork.Pause();
        }
        private Rect Bounds(ScriptableObject asset) => new(_layoutPositions[asset], _measuredSizes[asset]);
        private void Place(ScriptableObject asset, Vector2 point)
        {
            var node = _nodes[asset];
            node.SetPosition(new Rect(point, _measuredSizes[asset])); node.style.height = StyleKeyword.Auto;
            // GetPosition 返回上一轮布局的 Rect；本轮后续子节点必须使用刚设置的位置。
            _layoutPositions[asset] = point;
            _doc.Layout.SetPosition(asset, point);
        }
        private void PlaceNewNodes()
        {
            // 用户可能在等待布局的这一帧已拖动节点；保存过的位置永远优先。
            foreach (var asset in _pendingPlacement.ToArray())
                if (_doc.Layout.TryGetPosition(asset, out var saved)) { Place(asset, saved); _pendingPlacement.Remove(asset); }
            var occupied = _nodes.Keys.Where(a => !_pendingPlacement.Contains(a)).Select(Bounds).ToList();
            var links = _doc.Assets.SelectMany(PortRules.Links).ToArray();
            while (_pendingPlacement.Count > 0)
            {
                var asset = _doc.Assets.FirstOrDefault(a => _pendingPlacement.Contains(a) &&
                    !links.Any(l => l.Child == a && _pendingPlacement.Contains(l.Parent))) ?? _pendingPlacement.First();
                var parent = links.FirstOrDefault(l => l.Child == asset && !_pendingPlacement.Contains(l.Parent)).Parent;
                if (parent == null && asset != _root) parent = _root;
                Vector2 point = parent != null && _nodes.ContainsKey(parent) ? new Vector2(Bounds(parent).xMax + 60, Bounds(parent).y) : new Vector2(40, 50);
                if (_preferredPositions.TryGetValue(asset, out var preferred)) point = preferred;
                var candidate = new Rect(point, _measuredSizes[asset]);
                while (true)
                {
                    var padded = Rect.MinMaxRect(candidate.xMin - 24, candidate.yMin - 24, candidate.xMax + 24, candidate.yMax + 24);
                    var collisions = occupied.Where(r => r.Overlaps(padded)).ToArray();
                    if (collisions.Length == 0) break;
                    candidate.y = collisions.Max(r => r.yMax) + 40;
                }
                Place(asset, candidate.position); occupied.Add(candidate); _pendingPlacement.Remove(asset);
            }
        }
        private void ArrangeMeasured()
        {
            var levels = new Dictionary<ScriptableObject, int>();
            if (_root != null) levels[_root] = 0;
            // Longest path layering with a bounded relaxation also tolerates malformed cyclic assets.
            for (int pass = 0; pass < 8; pass++)
                foreach (var asset in _doc.Assets)
                    if (levels.TryGetValue(asset, out int depth))
                        foreach (var link in PortRules.Links(asset))
                            if (link.Child != _root && depth < 7 && (!levels.TryGetValue(link.Child, out int old) || old < depth + 1)) levels[link.Child] = depth + 1;
            var links = _doc.Assets.SelectMany(PortRules.Links).ToArray();
            float x = 40;
            foreach (var column in _doc.Assets.GroupBy(a => levels.TryGetValue(a, out var level) ? level : 1).OrderBy(g => g.Key))
            {
                float ParentY(ScriptableObject a) => links.Where(l => l.Child == a && _nodes.ContainsKey(l.Parent))
                    .Select(l => Bounds(l.Parent).center.y).DefaultIfEmpty(50).Average();
                float y = 50;
                foreach (var asset in column.OrderBy(ParentY).ThenBy(a => _nodes[a].GetPosition().y).ThenBy(AssetReferences.Key, StringComparer.Ordinal))
                { Place(asset, new Vector2(x, y)); y += _measuredSizes[asset].y + 55; }
                x += column.Max(a => _measuredSizes[a].x) + 60;
            }
        }
        private void SaveComments()
        {
            _doc.Layout.Comments.Clear();
            foreach (var group in _groups) _doc.Layout.Comments.Add(new LayoutStore.CommentLayout { title = group.title, rect = group.GetPosition() });
            _doc.Layout.MarkDirty();
        }
        private void AddComment()
        {
            var group = new Group { title = "注释" }; group.SetPosition(new Rect(contentViewContainer.WorldToLocal(_mouse), new Vector2(300, 200)));
            AddElement(group); _groups.Add(group);
            foreach (var node in selection.OfType<SkillNode>().ToArray()) group.AddElement(node);
            SaveComments(); _doc.Layout.Save();
        }
        private void OnKey(KeyDownEvent e)
        {
            if (e.target is TextField || e.target is TextElement) return;
            if (e.keyCode == KeyCode.Space) { Search(GUIUtility.GUIToScreenPoint(_mouse)); e.StopPropagation(); }
            else if (e.keyCode == KeyCode.F) { FrameSelection(); e.StopPropagation(); }
            else if (e.keyCode == KeyCode.A && !e.ctrlKey) { FrameAll(); e.StopPropagation(); }
            else if (e.keyCode == KeyCode.C && !e.ctrlKey) { AddComment(); e.StopPropagation(); }
            else if (e.ctrlKey && e.keyCode == KeyCode.C) { _clipboard = selection.OfType<SkillNode>().Select(n => n.Asset).Where(a => a != _doc.Root).ToArray(); e.StopPropagation(); }
            else if (e.ctrlKey && (e.keyCode == KeyCode.D || e.keyCode == KeyCode.V))
            {
                var assets = e.keyCode == KeyCode.D ? selection.OfType<SkillNode>().Select(n => n.Asset).ToArray() : _clipboard;
                EditCommand.Transaction(_doc, "复制选中节点", _doc.Root, () =>
                { foreach (var source in assets) if (source != null && source != _doc.Root) SkillAssetFactory.CopyNode(_doc, source); });
                e.StopPropagation();
            }
        }
        public override void BuildContextualMenu(ContextualMenuPopulateEvent e)
        {
            e.menu.AppendAction("添加节点  Space", _ => Search(GUIUtility.GUIToScreenPoint(e.localMousePosition)));
            e.menu.AppendAction("自动整理", _ => AutoArrange());
            e.menu.AppendAction("显示全部  A", _ => FrameAll());
            e.menu.AppendAction("添加注释  C", _ => AddComment());
            if (e.target is VisualElement target)
            {
                var node = target.GetFirstAncestorOfType<SkillNode>();
                if (node != null && node.Asset != _doc.Root)
                {
                    e.menu.AppendAction("复制节点", _ => SkillAssetFactory.CopyNode(_doc, node.Asset));
                    e.menu.AppendAction("删除节点", _ => SkillAssetFactory.DeleteNode(_doc, node.Asset));
                }
            }
        }
        public void Dispose() { _disposed = true; _layoutWork?.Pause(); _doc.Changed -= Sync; _doc.Selection.Changed -= SyncSelection; this.Unbind(); }
    }

    internal sealed class SkillNode : Node
    {
        private readonly SkillDocument _doc;
        private readonly SkillGraphView _graph;
        private readonly Dictionary<PortId, Port> _outputs = new();
        private readonly Label _badge, _summary;
        private readonly VisualElement _cues;
        public ScriptableObject Asset { get; }
        public Port Input { get; }
        public SkillNode(SkillDocument doc, SkillGraphView graph, ScriptableObject asset)
        {
            _doc = doc; _graph = graph; Asset = asset; title = asset.name;
            AddToClassList("skill-node"); style.width = 245;
            if (asset == doc.Root) capabilities &= ~Capabilities.Deletable;
            Input = new SkillPort(Direction.Input, Port.Capacity.Multi, asset.GetType(), graph, doc);
            Input.portName = ""; Input.tooltip = "被引用"; inputContainer.Add(Input);
            // RefreshPorts 会把容器放回 #top；保留层级，用绝对定位把连接点放到标题左侧。
            inputContainer.AddToClassList("node-title-input"); EnableInClassList("has-title-input", asset != doc.Root);
            if (asset == doc.Root) Input.style.display = DisplayStyle.None;
            _badge = new Label(); _badge.AddToClassList("node-badge"); titleContainer.Add(_badge);
            _summary = new Label(); _summary.AddToClassList("node-summary"); extensionContainer.Add(_summary);
            var so = doc.Serialized(asset);
            if (asset is GameplayEffectAsset)
            {
                Field(so, "durationPolicy", new EnumField("策略", (EffectDurationPolicy)so.FindProperty("durationPolicy").intValue));
                Field(so, "duration", new FloatField("时长 / s") { isDelayed = true });
                Field(so, "period", new FloatField("周期 / s") { isDelayed = true });
            }
            else if (asset is SkillAnimationAsset)
            {
                Field(so, "clipName", new TextField("片段") { isDelayed = true });
                Field(so, "duration", new FloatField("时长 / s") { isDelayed = true });
                Field(so, "loop", new Toggle("循环"));
            }
            else
            {
                var button = new Button(() => { doc.Selection.Select(asset); SkillEditorWindow.Open().ShowAbilitySettings(); }) { text = "编辑能力标签与门禁" };
                extensionContainer.Add(button);
            }
            _cues = new VisualElement(); _cues.AddToClassList("node-cues"); extensionContainer.Add(_cues);
            RegisterCallback<MouseDownEvent>(e => { if (e.clickCount == 2) EditorGUIUtility.PingObject(asset); });
            RefreshExpandedState();
        }
        private void Field<T>(SerializedObject so, string path, BaseField<T> field)
        {
            field.name = "node-" + path; field.AddToClassList("node-field"); field.BindProperty(so.FindProperty(path));
            // 等绑定完成写回后再通知文档，保留 Unity 的 Undo 和外部修改同步。
            field.RegisterValueChangedCallback(e => schedule.Execute(() =>
            { if (Asset != null && panel != null) _doc.Notify(new ChangeSet(Asset, path == "clipName", path)); }));
            extensionContainer.Add(field);
        }
        public override void OnSelected() { base.OnSelected(); if (!_graph.IsSyncing) _doc.Selection.Select(Asset); }
        public Port Output(PortId id) => _outputs.TryGetValue(id, out var port) ? port : null;
        private void SyncCues(SerializedObject so)
        {
            _cues.Clear();
            bool regions = Asset is SkillAnimationAsset;
            var array = so.FindProperty(regions ? "cueRegions" : "cueTags");
            if (array != null) for (int i = 0; i < array.arraySize; i++)
            {
                var item = array.GetArrayElementAtIndex(i);
                string tag = item.FindPropertyRelative(regions ? "cueTag.m_Name" : "m_Name").stringValue;
                if (string.IsNullOrEmpty(tag) && !regions) continue;
                string id = regions ? item.FindPropertyRelative("id").stringValue : null;
                string label = "Cue · " + (string.IsNullOrEmpty(tag) ? "未设置标签" : tag.StartsWith("Cue.", StringComparison.Ordinal) ? tag.Substring(4) : tag);
                if (regions) label += $"  {item.FindPropertyRelative("start").floatValue:0.##}–" +
                    (item.FindPropertyRelative("end").floatValue < 0 ? "动画结束" : $"{item.FindPropertyRelative("end").floatValue:0.##}s");
                var strip = new Button(() => _doc.Selection.Select(Asset, regions ? "cueRegions" : null, id))
                { text = label, userData = id, tooltip = tag + (regions ? " · 点击定位时间轴区间" : " · 效果触发的 Cue") };
                strip.AddToClassList("node-cue-strip"); _cues.Add(strip);
            }
            _cues.style.display = _cues.childCount == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            SyncCueSelection();
        }
        public void SyncCueSelection()
        {
            foreach (var strip in _cues.Children())
            {
                bool selected = _doc.Selection.Asset == Asset && _doc.Selection.Track == "cueRegions" && (string)strip.userData == _doc.Selection.ElementId;
                strip.EnableInClassList("selected-cue", selected);
                var cue = SkillColors.Cue;
                strip.style.color = Color.Lerp(cue, Color.white, .72f);
                strip.style.backgroundColor = new Color(cue.r, cue.g, cue.b, selected ? .28f : .12f);
                var border = selected ? Color.Lerp(cue, Color.white, .6f) : new Color(cue.r, cue.g, cue.b, .55f);
                strip.style.borderLeftColor = strip.style.borderRightColor = strip.style.borderTopColor = strip.style.borderBottomColor = border;
            }
        }
        public void Sync()
        {
            if (Asset == null) return;
            title = Asset.name;
            var so = _doc.Serialized(Asset); var desired = new Dictionary<PortId, string>(); Color color;
            if (Asset is GameplayAbilityAsset)
            {
                foreach (var kind in new[] { PortKind.Cost, PortKind.Cooldown, PortKind.Animation }) { var id = new PortId(kind); desired[id] = id.Label; }
                color = SkillColors.Ability; _summary.text = "ABILITY  /  技能定义";
            }
            else if (Asset is GameplayEffectAsset)
            {
                foreach (var kind in new[] { PortKind.Applied, PortKind.Periodic, PortKind.Removed }) { var id = new PortId(kind); desired[id] = id.Label; }
                int policy = so.FindProperty("durationPolicy").enumValueIndex;
                color = policy == 0 ? SkillColors.Instant : policy == 1 ? SkillColors.Duration : SkillColors.Infinite;
                var root = _doc.Serialized(_doc.Root);
                if (root?.FindProperty("cost").objectReferenceValue == Asset) color = SkillColors.Cost;
                if (root?.FindProperty("cooldown").objectReferenceValue == Asset) color = SkillColors.Cooldown;
                int count = so.FindProperty("modifiers").arraySize;
                _summary.text = count == 0 ? "EFFECT  /  无修改器" : count + " 个修改器 · " + ModifierDrawer.Summary(so.FindProperty("modifiers").GetArrayElementAtIndex(0));
            }
            else
            {
                color = SkillColors.Animation; var markers = so.FindProperty("markers");
                for (int i = 0; i < markers.arraySize; i++)
                {
                    var m = markers.GetArrayElementAtIndex(i); var id = new PortId(PortKind.Marker, m.FindPropertyRelative("id").stringValue);
                    desired[id] = $"◆ {m.FindPropertyRelative("time").floatValue:0.00}s  {m.FindPropertyRelative("key").stringValue}";
                }
                _summary.text = "ANIMATION  /  " + markers.arraySize + " 个事件";
            }
            titleContainer.style.borderTopColor = color; Input.portColor = color; elementTypeColor = color;
            bool external = Asset != _doc.Root && AssetDatabase.GetAssetPath(Asset) != AssetDatabase.GetAssetPath(_doc.Root);
            EnableInClassList("external-node", external);
            if (external) title += "  ↗";
            foreach (var pair in _outputs.ToArray()) if (!desired.ContainsKey(pair.Key)) { outputContainer.Remove(pair.Value); _outputs.Remove(pair.Key); }
            foreach (var pair in desired)
            {
                if (!_outputs.TryGetValue(pair.Key, out var port))
                {
                    port = new SkillPort(Direction.Output, pair.Key.Single ? Port.Capacity.Single : Port.Capacity.Multi, pair.Key.AcceptedType, _graph, _doc) { userData = pair.Key };
                    var id = pair.Key;
                    port.RegisterCallback<DragUpdatedEvent>(e => { if (DragAndDrop.objectReferences.Any(o => id.AcceptedType.IsInstanceOfType(o))) { DragAndDrop.visualMode = DragAndDropVisualMode.Link; e.StopPropagation(); } });
                    port.RegisterCallback<DragPerformEvent>(e =>
                    {
                        foreach (var asset in DragAndDrop.objectReferences.OfType<ScriptableObject>()) if (id.AcceptedType.IsInstanceOfType(asset)) PortRules.Attach(_doc, Asset, id, asset);
                        DragAndDrop.AcceptDrag(); e.StopPropagation();
                    });
                    port.RegisterCallback<MouseDownEvent>(e => { if (id.Kind == PortKind.Marker) _doc.Selection.Select(Asset, "markers", id.ElementId); });
                    _outputs[id] = port; outputContainer.Add(port);
                }
                port.portName = pair.Value; port.portColor = color;
            }
            SyncCues(so); RefreshPorts(); RefreshExpandedState();
            inputContainer.style.display = Asset == _doc.Root ? DisplayStyle.None : DisplayStyle.Flex;
            outputContainer.style.display = _outputs.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
        }
        public void SetIssues(IReadOnlyList<Issue> issues)
        {
            int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
            _badge.text = issues.Count == 0 ? "" : (errors > 0 ? "✕ " : "! ") + issues.Count;
            _badge.tooltip = string.Join("\n", issues.Select(i => i.Message));
            EnableInClassList("invalid-node", errors > 0);
        }
    }
    internal sealed class SkillPort : Port
    {
        public SkillPort(Direction direction, Capacity capacity, Type type, SkillGraphView graph, SkillDocument doc)
            : base(Orientation.Horizontal, direction, capacity, type)
        { m_EdgeConnector = new EdgeConnector<Edge>(new Connector(graph, doc)); this.AddManipulator(m_EdgeConnector); }
        private sealed class Connector : IEdgeConnectorListener
        {
            private readonly SkillGraphView _graph; private readonly SkillDocument _doc;
            public Connector(SkillGraphView graph, SkillDocument doc) { _graph = graph; _doc = doc; }
            public void OnDropOutsidePort(Edge edge, Vector2 position)
            {
                if (edge.output?.node is SkillNode from && edge.output.userData is PortId id)
                    _graph.Search(GUIUtility.GUIToScreenPoint(position), from.Asset, id);
            }
            public void OnDrop(GraphView graphView, Edge edge)
            {
                if (edge.output?.node is SkillNode from && edge.input?.node is SkillNode to && edge.output.userData is PortId id)
                    PortRules.Attach(_doc, from.Asset, id, to.Asset);
            }
        }
    }
}
