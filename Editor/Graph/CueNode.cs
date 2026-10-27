using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZFramework.GAS.Editor
{
    // Read-only projection of the source asset's Cue declaration; never creates runtime data.
    internal sealed class CueNode : Node
    {
        private readonly SkillDocument _document;
        private readonly SkillGraphView _graph;
        private readonly Label _tag, _source;
        public readonly string Key, Track, Id;
        public readonly ScriptableObject Source;
        public readonly Port Input;
        public Edge Connection;
        public CueNode(SkillDocument document, SkillGraphView graph, string key, ScriptableObject source, string track, string id)
        {
            _document = document; _graph = graph; Key = key; Source = source; Track = track; Id = id;
            title = "Cue · 只读"; AddToClassList("skill-node"); style.width = 230;
            capabilities = Capabilities.Selectable | Capabilities.Movable;
            titleContainer.style.backgroundColor = SkillColors.Cue;
            Input = InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Single, typeof(string));
            Input.portName = "来源"; Input.portColor = SkillColors.Cue; Input.SetEnabled(false); inputContainer.Add(Input);
            _tag = new Label(); _tag.AddToClassList("node-summary"); extensionContainer.Add(_tag);
            _source = new Label(); _source.AddToClassList("node-summary"); extensionContainer.Add(_source); RefreshExpandedState();
        }
        public void SetLabel(string tag, string source) { _tag.text = tag; _source.text = source; }
        public override void OnSelected()
        {
            base.OnSelected();
            if (!_graph.IsSyncing) _document.Selection.Select(Source, Track, Track != null ? Id : null);
        }
    }
}
