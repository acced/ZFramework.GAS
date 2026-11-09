using ZFramework.GAS.Unity;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZFramework.GAS.Editor
{
    internal sealed class RealtimeWorkspace : ISkillTimelineWorkspace
    {
        private readonly SkillDocument _doc;
        private readonly TimelinePanel _timeline;
        private readonly PreviewPanel _preview;
        private readonly ObjectField _previewField;
        public VisualElement Timeline => _timeline;
        public VisualElement Preview => _preview;
        public VisualElement Toolbar { get; }
        internal RealtimeWorkspace(SkillDocument doc)
        {
            _doc = doc; _timeline = new TimelinePanel(doc); _preview = new PreviewPanel(doc);
            var context = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1, flexBasis = 0 } };
            Toolbar = context;
            _previewField = new ObjectField("预览角色") { objectType = typeof(GameObject), allowSceneObjects = false, style = { flexGrow = 1, flexBasis = 0, minWidth = 0 } };
            _previewField.RegisterValueChangedCallback(e =>
            {
                _doc.PreviewPrefab = e.newValue as GameObject; _doc.Layout.PreviewPrefab = _doc.PreviewPrefab; _doc.Layout.Save();
                _preview.Load(); SyncToolbar(); _doc.Notify(new ChangeSet(_doc.Root, true));
            }); context.Add(_previewField);
            _preview.Load();
        }
        public void SyncToolbar() => _previewField.SetValueWithoutNotify(_doc.PreviewPrefab);
        public void Fit() => _timeline.schedule.Execute(_timeline.Fit).StartingIn(150);
        public void Dispose() { _preview.Dispose(); _timeline.Dispose(); }
    }
}
