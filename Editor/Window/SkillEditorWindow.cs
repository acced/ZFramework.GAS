using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class SkillEditorWindow : EditorWindow
    {
        [SerializeField] private GameplayAbilityAsset activeAbility;
        private SkillDocument _doc;
        private SkillGraphView _graph;
        private TimelinePanel _timeline;
        private DetailsPanel _details;
        private LibraryPanel _library;
        private PreviewPanel _preview;
        private MessagePanel _messages;
        private ValidationService _validation;
        private AbilitySettingsPanel _settings;
        private VisualElement _graphHost, _messagesHost, _center, _rightTabs;
        private Label _breadcrumb, _status;
        private ToolbarMenu _backend;
        private ToolbarButton _counts;
        private ObjectField _rootField, _previewField;
        private double _lastTime;
        private readonly List<GameplayAbilityAsset> _history = new();
        private int _historyIndex = -1;
        private bool _showSettings, _showMessages, _compactPreview;
        public SkillDocument Document => _doc;
        public int NodeCount => _graph?.NodeCount ?? 0;
        [MenuItem("Window/ZFramework GAS/技能编辑器")]
        public static SkillEditorWindow Open()
        {
            var window = GetWindow<SkillEditorWindow>(); window.titleContent = new GUIContent("GAS 技能编辑器");
            window.minSize = new Vector2(780, 520); return window;
        }
        [MenuItem("Window/ZFramework GAS/技能结构图")]
        private static void OpenLegacy() => Open();
        [UnityEditor.Callbacks.OnOpenAsset]
        private static bool OnOpenAsset(int instanceId, int line)
        {
            if (EditorUtility.EntityIdToObject(instanceId) is not GameplayAbilityAsset ability) return false;
            Open().Show(ability); return true;
        }
        private void OnEnable() { titleContent = new GUIContent("GAS 技能编辑器"); _lastTime = EditorApplication.timeSinceStartup; EditorApplication.update += Tick; }
        private void CreateGUI()
        {
            Cleanup(); rootVisualElement.Clear(); rootVisualElement.AddToClassList("gas-editor");
            string path = "Packages/com.acced.zframework.gas/Editor/Window/";
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path + "SkillEditorWindow.uxml");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path + "SkillEditor.uss");
            if (tree != null) tree.CloneTree(rootVisualElement);
            else rootVisualElement.Add(new VisualElement { name = "workspace", style = { flexGrow = 1 } });
            if (sheet != null) rootVisualElement.styleSheets.Add(sheet);
            _doc = new SkillDocument(); _validation = new ValidationService(_doc);
            _graph = new SkillGraphView(_doc); _timeline = new TimelinePanel(_doc); _details = new DetailsPanel(_doc);
            _library = new LibraryPanel(_doc, Show); _preview = new PreviewPanel(_doc); _settings = new AbilitySettingsPanel(_doc);
            _messages = new MessagePanel(_doc, _validation);
            BuildToolbar(rootVisualElement.Q("toolbar-host") ?? rootVisualElement);
            var workspace = rootVisualElement.Q("workspace");
            var leftSplit = Split("LibraryWidth", 0, 220, TwoPaneSplitViewOrientation.Horizontal);
            _library.style.minWidth = 155; leftSplit.Add(_library);
            var contentSplit = Split("DetailsWidth", 1, 300, TwoPaneSplitViewOrientation.Horizontal); leftSplit.Add(contentSplit);
            _center = new VisualElement { style = { flexGrow = 1, minWidth = 300 } }; contentSplit.Add(_center);
            var centerTabs = new Toolbar(); centerTabs.AddToClassList("section-tabs");
            centerTabs.Add(new ToolbarButton(() => SetSettings(false)) { text = "结构图" });
            centerTabs.Add(new ToolbarButton(() => SetSettings(true)) { text = "能力设置" });
            centerTabs.Add(new ToolbarButton(() => _graph.AutoArrange()) { text = "自动整理" });
            centerTabs.Add(new ToolbarButton(() => _graph.FrameAll()) { text = "显示全部" });
            centerTabs.Add(new ToolbarButton(() => ToggleMessages()) { text = "消息" }); _center.Add(centerTabs);
            var timelineSplit = Split("TimelineHeight", 1, 278, TwoPaneSplitViewOrientation.Vertical); _center.Add(timelineSplit);
            _graphHost = new VisualElement { style = { flexGrow = 1, minHeight = 120 } }; _graphHost.Add(_graph); _graphHost.Add(_settings); _settings.style.display = DisplayStyle.None;
            _messagesHost = new VisualElement { style = { flexGrow = 1, display = DisplayStyle.None } }; _messagesHost.Add(_messages); _graphHost.Add(_messagesHost);
            timelineSplit.Add(_graphHost); timelineSplit.Add(_timeline);
            var right = new VisualElement { style = { flexGrow = 1, minWidth = 220 } }; contentSplit.Add(right);
            _rightTabs = new Toolbar(); _rightTabs.Add(new ToolbarButton(() => { _compactPreview = true; Responsive(); }) { text = "预览" });
            _rightTabs.Add(new ToolbarButton(() => { _compactPreview = false; Responsive(); }) { text = "Details" }); right.Add(_rightTabs);
            var rightSplit = Split("PreviewHeight", 0, 235, TwoPaneSplitViewOrientation.Vertical); rightSplit.name = "preview-split";
            rightSplit.Add(_preview); rightSplit.Add(_details); right.Add(rightSplit); workspace.Add(leftSplit);
            _status = new Label(); _status.AddToClassList("status-label");
            var statusHost = rootVisualElement.Q("status-host") ?? rootVisualElement; statusHost.Add(_status);
            _doc.Changed += Changed; _doc.Feedback += Feedback; _validation.Changed += ValidationChanged;
            rootVisualElement.RegisterCallback<GeometryChangedEvent>(_ => Responsive());
            _doc.Open(activeAbility); SyncToolbar(); _validation.ValidateNow(true);
        }
        private TwoPaneSplitView Split(string key, int fixedPane, float initial, TwoPaneSplitViewOrientation orientation)
        {
            float saved = EditorPrefs.GetFloat("ZFramework.GAS." + key, initial);
            var split = new TwoPaneSplitView(fixedPane, saved, orientation) { viewDataKey = key, style = { flexGrow = 1 } };
            split.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var pane = split.fixedPane; if (pane == null || pane.resolvedStyle.display == DisplayStyle.None) return;
                float size = orientation == TwoPaneSplitViewOrientation.Horizontal ? pane.layout.width : pane.layout.height;
                if (!float.IsNaN(size) && size > 60) EditorPrefs.SetFloat("ZFramework.GAS." + key, size);
            });
            return split;
        }
        private void BuildToolbar(VisualElement host)
        {
            var toolbar = new Toolbar(); toolbar.AddToClassList("main-toolbar");
            var create = new ToolbarMenu { text = "+ 新建技能" }; create.AddToClassList("primary-button");
            foreach (var item in SkillTemplateRegistry.Templates) { var template = item; create.menu.AppendAction(template.DisplayName, _ => SkillTemplateRegistry.Choose(template, Show)); }
            toolbar.Add(create); toolbar.Add(new ToolbarButton(Save) { text = "保存", tooltip = "Ctrl+S" });
            toolbar.Add(new ToolbarButton(() => Navigate(-1)) { text = "◀" }); toolbar.Add(new ToolbarButton(() => Navigate(1)) { text = "▶" });
            _breadcrumb = new Label("选择技能"); _breadcrumb.AddToClassList("breadcrumb"); toolbar.Add(_breadcrumb);
            _counts = new ToolbarButton(() => ToggleMessages()) { text = "校验" }; toolbar.Add(_counts); host.Add(toolbar);
            var context = new Toolbar(); context.AddToClassList("context-toolbar");
            _rootField = new ObjectField("当前技能") { objectType = typeof(GameplayAbilityAsset), allowSceneObjects = false, style = { flexGrow = 1, flexBasis = 0, minWidth = 0 } };
            _rootField.RegisterValueChangedCallback(e => Show(e.newValue as GameplayAbilityAsset)); context.Add(_rootField);
            _previewField = new ObjectField("预览角色") { objectType = typeof(GameObject), allowSceneObjects = false, style = { flexGrow = 1, flexBasis = 0, minWidth = 0 } };
            _previewField.RegisterValueChangedCallback(e =>
            {
                _doc.PreviewPrefab = e.newValue as GameObject; _doc.Layout.PreviewPrefab = _doc.PreviewPrefab; _doc.Layout.Save();
                _preview.Load(); SyncToolbar(); _doc.Notify(new ChangeSet(_doc.Root, true));
            }); context.Add(_previewField);
            _backend = new ToolbarMenu { tooltip = "切换预览角色的 SkillAnimator 后端" }; _backend.AddToClassList("backend-label"); context.Add(_backend); host.Add(context);
        }
        public void Show(GameplayAbilityAsset ability)
        {
            if (_doc == null) CreateGUI();
            if (activeAbility != ability || _doc.Root != ability)
            {
                activeAbility = ability;
                if (_historyIndex < 0 || _history[_historyIndex] != ability)
                {
                    if (_historyIndex + 1 < _history.Count) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                    _history.Add(ability); _historyIndex = _history.Count - 1;
                }
                _doc.Open(ability); _timeline.schedule.Execute(_timeline.Fit).StartingIn(150);
            }
            SyncToolbar(); Focus();
        }
        private void Navigate(int direction)
        {
            int next = _historyIndex + direction; if (next < 0 || next >= _history.Count) return;
            _historyIndex = next; activeAbility = _history[next]; _doc.Open(activeAbility); SyncToolbar();
        }
        private void Changed(ChangeSet change) { SyncToolbar(); if (_showSettings && change.Structure) _settings.Rebuild(); UpdateStatus(); }
        private void SyncToolbar()
        {
            _rootField.SetValueWithoutNotify(_doc.Root); _previewField.SetValueWithoutNotify(_doc.PreviewPrefab);
            _breadcrumb.text = _doc.Root != null ? AssetDatabase.GetAssetPath(_doc.Root).Replace("Assets/", "技能 / ").Replace("/", " / ") : "GAS  /  技能工作台";
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
            titleContent = new GUIContent("GAS · " + (_doc.Root != null ? _doc.Root.name : "技能编辑器"));
        }
        private void ValidationChanged()
        {
            int errors = _validation.Issues.Count(i => i.Severity == IssueSeverity.Error), warnings = _validation.Issues.Count(i => i.Severity == IssueSeverity.Warning);
            _counts.text = $"✕ {errors}   ! {warnings}"; _counts.EnableInClassList("has-errors", errors > 0);
            _graph.ApplyIssues(_validation.Issues); UpdateStatus();
        }
        private void UpdateStatus()
        {
            var issue = _validation.Issues.FirstOrDefault(i => i.Severity != IssueSeverity.Info);
            bool dirty = _doc.Assets.Any(EditorUtility.IsDirty);
            _status.text = (issue != null ? issue.Id + "  " + issue.Message : "✓ 配置校验通过") + $"     ·     {_graph.NodeCount} 个节点     ·     " + (dirty ? "有未保存修改" : "已保存");
            _status.tooltip = issue?.Message ?? "Ctrl+S 保存 · 结构图 Space 添加节点 · 时间轴 M 添加事件";
        }
        private void Feedback(string text) => ShowNotification(new GUIContent(text), 2);
        private void SetSettings(bool show)
        {
            _showSettings = show; _showMessages = false; _messagesHost.style.display = DisplayStyle.None;
            _graph.style.display = show ? DisplayStyle.None : DisplayStyle.Flex; _settings.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show) _settings.Rebuild();
        }
        public void ShowAbilitySettings() => SetSettings(true);
        private void ToggleMessages()
        {
            _showMessages = !_showMessages;
            _graph.style.display = _showMessages || _showSettings ? DisplayStyle.None : DisplayStyle.Flex;
            _settings.style.display = !_showMessages && _showSettings ? DisplayStyle.Flex : DisplayStyle.None;
            _messagesHost.style.display = _showMessages ? DisplayStyle.Flex : DisplayStyle.None;
        }
        private void Responsive()
        {
            if (_rightTabs == null) return;
            bool compact = position.width < 900; _rightTabs.style.display = compact ? DisplayStyle.Flex : DisplayStyle.None;
            var split = rootVisualElement.Q<TwoPaneSplitView>("preview-split");
            if (compact) split.CollapseChild(_compactPreview ? 1 : 0); else split.UnCollapse();
        }
        public void Save() { _doc?.Save(); _validation?.ValidateNow(true); }
        public void NewSkill() => SkillTemplateRegistry.Choose(new MeleeTemplate(), Show);
        public void FocusSearch() => _library?.FocusSearch();
        private void Tick()
        {
            double now = EditorApplication.timeSinceStartup; float delta = Mathf.Min(.1f, (float)(now - _lastTime)); _lastTime = now;
            _doc?.Playback.Tick(delta, _doc.Root?.Animation?.Duration ?? 0);
        }
        private void OnFocus() { if (_doc != null && !EditorApplication.isCompiling) _doc.Notify(new ChangeSet(_doc.Root, true)); }
        private void OnDisable() { EditorApplication.update -= Tick; Cleanup(); }
        private void Cleanup()
        {
            _preview?.Dispose(); _timeline?.Dispose(); _graph?.Dispose(); _details?.Dispose(); _library?.Dispose(); _messages?.Dispose(); _validation?.Dispose();
            _doc?.Dispose(); _doc = null;
        }
        [Shortcut("GAS/保存技能", typeof(SkillEditorWindow), KeyCode.S, ShortcutModifiers.Action)]
        private static void SaveShortcut(ShortcutArguments args) => ((SkillEditorWindow)args.context).Save();
        [Shortcut("GAS/新建技能", typeof(SkillEditorWindow), KeyCode.N, ShortcutModifiers.Action)]
        private static void NewShortcut(ShortcutArguments args) => ((SkillEditorWindow)args.context).NewSkill();
        [Shortcut("GAS/搜索技能", typeof(SkillEditorWindow), KeyCode.F, ShortcutModifiers.Action)]
        private static void FindShortcut(ShortcutArguments args) => ((SkillEditorWindow)args.context).FocusSearch();
        [Shortcut("GAS/后退", typeof(SkillEditorWindow), KeyCode.LeftArrow, ShortcutModifiers.Alt)]
        private static void BackShortcut(ShortcutArguments args) => ((SkillEditorWindow)args.context).Navigate(-1);
        [Shortcut("GAS/前进", typeof(SkillEditorWindow), KeyCode.RightArrow, ShortcutModifiers.Alt)]
        private static void ForwardShortcut(ShortcutArguments args) => ((SkillEditorWindow)args.context).Navigate(1);
    }
}
