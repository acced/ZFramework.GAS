using System.Linq;
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
        private readonly ToolbarMenu _backend;
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
            _backend = new ToolbarMenu { tooltip = "切换预览角色的 SkillAnimator 后端" }; _backend.AddToClassList("backend-label"); context.Add(_backend);
        }
        public void SyncToolbar()
        {
            _previewField.SetValueWithoutNotify(_doc.PreviewPrefab);
            _backend.text = AnimationAdapterRegistry.Resolve(_doc.PreviewPrefab).DisplayName;
            _backend.menu.MenuItems().Clear();
            var animator = _doc.PreviewPrefab != null ? _doc.PreviewPrefab.GetComponentInChildren<SkillAnimator>(true) : null;
            if (animator == null) _backend.menu.AppendAction("请在角色上添加 SkillAnimator", _ => { }, DropdownMenuAction.Status.Disabled);
            else foreach (var item in AnimationAdapterRegistry.Adapters.Where(a => a.CanHandle(_doc.PreviewPrefab)))
            {
                var adapter = item;
                _backend.menu.AppendAction(adapter.DisplayName, _ =>
                {
                    if (animator.Backend?.BackendId == adapter.BackendId) return;
                    EditCommand.Run(_doc, "切换动画后端", animator, so => so.FindProperty("backend").managedReferenceValue = adapter.CreateDefaultBackend(_doc.PreviewPrefab), true);
                    _preview.Load();
                }, animator.Backend?.BackendId == adapter.BackendId ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
        }
        public void Fit() => _timeline.schedule.Execute(_timeline.Fit).StartingIn(150);
        public void Dispose() { _preview.Dispose(); _timeline.Dispose(); }
    }
}
