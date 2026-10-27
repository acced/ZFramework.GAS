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
        private readonly Dictionary<string, CueNode> _cueNodes = new();
        private bool _syncing;
        internal bool IsSyncing => _syncing;
        private GameplayAbilityAsset _root;
        private Vector2 _mouse;
        private readonly List<Group> _groups = new();
        private ScriptableObject[] _clipboard = Array.Empty<ScriptableObject>();
        public int NodeCount => _nodes.Count + _cueNodes.Count;
        public SkillGraphView(SkillDocument doc)
        {
            _doc = doc; style.flexGrow = 1; focusable = true;
            SetupZoom(.2f, 1.8f);
            var grid = new GridBackground(); Insert(0, grid); grid.StretchToParentSize();
            this.AddManipulator(new ContentDragger()); this.AddManipulator(new SelectionDragger()); this.AddManipulator(new RectangleSelector());
            var minimap = new MiniMap { anchored = true }; minimap.SetPosition(new Rect(12, 12, 135, 82));
            minimap.style.position = Position.Absolute; minimap.style.right = 12; minimap.style.bottom = 12;
            Add(minimap);
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
                    { var asset = (ScriptableObject)obj; _doc.Layout.SetPosition(asset, contentViewContainer.WorldToLocal(e.mousePosition)); _doc.AddLoose(asset); _doc.Selection.Select(asset); }
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
                    foreach (var group in _groups) RemoveElement(group); _groups.Clear();
                    foreach (var comment in _doc.Layout.Comments)
                    {
                        var group = new Group { title = comment.title }; group.SetPosition(comment.rect); AddElement(group); _groups.Add(group);
                    }
                }
                var live = new HashSet<ScriptableObject>(_doc.Assets);
                foreach (var pair in _nodes.ToArray())
                    if (!live.Contains(pair.Key)) { RemoveElement(pair.Value); _nodes.Remove(pair.Key); }
                int index = 0;
                foreach (var asset in _doc.Assets)
                {
                    if (!_nodes.TryGetValue(asset, out var node))
                    {
                        node = new SkillNode(_doc, this, asset); _nodes[asset] = node; AddElement(node);
                        Vector2 fallback = asset == _root ? new Vector2(45, 80) : new Vector2(350 + (index / 3) * 290, 55 + (index % 3) * 230);
                        node.SetPosition(new Rect(_doc.Layout.Position(asset, fallback), new Vector2(245, 170)));
                    }
                    node.Sync(); index++;
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
                SyncCues();
                if (rootChanged) schedule.Execute(() => { if (_nodes.Count > 0) FrameAll(); }).StartingIn(100);
                SyncSelection();
            }
            finally { _syncing = false; }
        }
        private GraphViewChange OnGraphChanged(GraphViewChange change)
        {
            if (_syncing) return change;
            if (change.movedElements != null)
            {
                foreach (var element in change.movedElements)
                    if (element is SkillNode node) _doc.Layout.SetPosition(node.Asset, node.GetPosition().position);
                    else if (element is CueNode cue) _doc.Layout.SetPosition(cue.Key, cue.GetPosition().position);
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
            provider.Initialize(_doc, parent, port, graphPosition);
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
                GraphElement selected = _cueNodes.Values.FirstOrDefault(c => c.Source == _doc.Selection.Asset && c.Track == "cueRegions" && c.Track == _doc.Selection.Track && c.Id == _doc.Selection.ElementId);
                if (selected == null && _doc.Selection.Asset is ScriptableObject asset && _nodes.TryGetValue(asset, out var node)) selected = node;
                if (selected != null && !selection.Contains(selected)) { ClearSelection(); AddToSelection(selected); }
            }
            finally { _syncing = previous; }
            foreach (var pair in _nodes)
                pair.Value.EnableInClassList("document-selected", pair.Key == _doc.Selection.Asset);
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
        public void AutoArrange()
        {
            var levels = new Dictionary<ScriptableObject, int>();
            if (_root != null) levels[_root] = 0;
            // Longest path layering with a bounded relaxation also tolerates malformed cyclic assets.
            for (int pass = 0; pass < 8; pass++)
                foreach (var asset in _doc.Assets)
                    if (levels.TryGetValue(asset, out int depth))
                        foreach (var link in PortRules.Links(asset))
                            if (link.Child != _root && depth < 7 && (!levels.TryGetValue(link.Child, out int old) || old < depth + 1)) levels[link.Child] = depth + 1;
            var rows = new Dictionary<int, float>();
            foreach (var asset in _doc.Assets)
            {
                int col = levels.TryGetValue(asset, out int level) ? level : 1;
                rows.TryGetValue(col, out float y); var node = _nodes[asset];
                Vector2 point = new(40 + col * 300, 50 + y);
                node.SetPosition(new Rect(point, node.GetPosition().size)); _doc.Layout.SetPosition(asset, point);
                rows[col] = y + Mathf.Max(200, node.layout.height) + 55;
            }
            foreach (var cue in _cueNodes.Values)
            {
                int col = (levels.TryGetValue(cue.Source, out int level) ? level : 1) + 1;
                rows.TryGetValue(col, out float y); Vector2 point = new(40 + col * 300, 50 + y);
                cue.SetPosition(new Rect(point, cue.GetPosition().size)); _doc.Layout.SetPosition(cue.Key, point); rows[col] = y + 160;
            }
            _doc.Layout.Save(); schedule.Execute(() => FrameAll()).StartingIn(50);
        }
        private void SyncCues()
        {
            var live = new HashSet<string>();
            foreach (var asset in _doc.Assets)
            {
                bool regions = asset is SkillAnimationAsset;
                var array = _doc.Serialized(asset).FindProperty(regions ? "cueRegions" : "cueTags");
                if (array == null) continue;
                for (int i = 0; i < array.arraySize; i++)
                {
                    var item = array.GetArrayElementAtIndex(i);
                    string tag = item.FindPropertyRelative(regions ? "cueTag.m_Name" : "m_Name").stringValue;
                    if (string.IsNullOrEmpty(tag)) continue;
                    string id = regions ? item.FindPropertyRelative("id").stringValue : tag;
                    string key = AssetReferences.Key(asset) + "/cue/" + id; live.Add(key);
                    if (!_cueNodes.TryGetValue(key, out var cue))
                    {
                        cue = new CueNode(_doc, this, key, asset, regions ? "cueRegions" : null, id); _cueNodes[key] = cue; AddElement(cue);
                        var point = _nodes[asset].GetPosition().position + new Vector2(300, i * 140);
                        cue.SetPosition(new Rect(_doc.Layout.Position(key, point), new Vector2(230, 110)));
                        cue.Connection = _nodes[asset].CuePort().ConnectTo(cue.Input);
                        cue.Connection.capabilities &= ~Capabilities.Deletable; AddElement(cue.Connection);
                    }
                    cue.SetLabel(tag, regions ? $"区间 · {item.FindPropertyRelative("start").floatValue:0.##}s → " +
                        (item.FindPropertyRelative("end").floatValue < 0 ? "动画结束" : $"{item.FindPropertyRelative("end").floatValue:0.##}s") : "效果 · " + asset.name);
                }
            }
            foreach (var pair in _cueNodes.ToArray()) if (!live.Contains(pair.Key))
            {
                var edge = pair.Value.Connection; edge.output?.Disconnect(edge); edge.input?.Disconnect(edge); RemoveElement(edge);
                RemoveElement(pair.Value); _cueNodes.Remove(pair.Key);
            }
            foreach (var node in _nodes.Values) node.ShowCuePort(_cueNodes.Values.Any(c => c.Source == node.Asset));
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
        public void Dispose() { _doc.Changed -= Sync; _doc.Selection.Changed -= SyncSelection; this.Unbind(); }
    }

    internal sealed class SkillNode : Node
    {
        private readonly SkillDocument _doc;
        private readonly SkillGraphView _graph;
        private readonly Dictionary<PortId, Port> _outputs = new();
        private readonly Label _badge, _summary;
        private Port _cuePort;
        public ScriptableObject Asset { get; }
        public Port Input { get; }
        public SkillNode(SkillDocument doc, SkillGraphView graph, ScriptableObject asset)
        {
            _doc = doc; _graph = graph; Asset = asset; title = asset.name;
            AddToClassList("skill-node"); style.width = 245;
            if (asset == doc.Root) capabilities &= ~Capabilities.Deletable;
            Input = new SkillPort(Direction.Input, Port.Capacity.Multi, asset.GetType(), graph, doc);
            Input.portName = asset == doc.Root ? "能力" : "被引用"; inputContainer.Add(Input);
            if (asset == doc.Root) Input.style.display = DisplayStyle.None;
            _badge = new Label(); _badge.AddToClassList("node-badge"); titleContainer.Add(_badge);
            _summary = new Label(); _summary.AddToClassList("node-summary"); extensionContainer.Add(_summary);
            var so = doc.Serialized(asset);
            if (asset is GameplayEffectAsset) { Field(so, "durationPolicy", "策略"); Field(so, "duration", "时长 / s"); Field(so, "period", "周期 / s"); }
            else if (asset is SkillAnimationAsset) { Field(so, "clipName", "片段"); Field(so, "duration", "时长 / s"); Field(so, "loop", "循环"); }
            else
            {
                var button = new Button(() => { doc.Selection.Select(asset); SkillEditorWindow.Open().ShowAbilitySettings(); }) { text = "编辑能力标签与门禁" };
                extensionContainer.Add(button);
            }
            RegisterCallback<MouseDownEvent>(e => { if (e.clickCount == 2) EditorGUIUtility.PingObject(asset); });
            RefreshExpandedState();
        }
        private void Field(SerializedObject so, string path, string label)
        {
            var field = new PropertyField(so.FindProperty(path), label); field.AddToClassList("node-field"); field.Bind(so);
            field.RegisterCallback<SerializedPropertyChangeEvent>(e => schedule.Execute(() => _doc.Notify(new ChangeSet(Asset, path == "clipName", path))));
            extensionContainer.Add(field);
        }
        public override void OnSelected() { base.OnSelected(); if (!_graph.IsSyncing) _doc.Selection.Select(Asset); }
        public Port Output(PortId id) => _outputs.TryGetValue(id, out var port) ? port : null;
        public Port CuePort()
        {
            if (_cuePort == null)
            {
                _cuePort = InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(string));
                _cuePort.portName = "Cue · 只读"; _cuePort.portColor = SkillColors.Cue; _cuePort.SetEnabled(false); outputContainer.Add(_cuePort);
            }
            return _cuePort;
        }
        public void ShowCuePort(bool visible) { if (_cuePort != null) _cuePort.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None; }
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
            titleContainer.style.backgroundColor = color;
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
            RefreshPorts();
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
