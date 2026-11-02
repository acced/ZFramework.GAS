using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class RuntimeDebugWindow : EditorWindow
    {
        private ToolbarMenu _units;
        private ToolbarToggle _follow;
        private Label _hint, _unitName;
        private ScrollView _scroll;
        private readonly List<Row> _abilities = new(), _tasks = new(), _tags = new(), _attributes = new(), _effects = new();
        private VisualElement _abilityHost, _taskHost, _tagHost, _attributeHost, _effectHost;

        private sealed class Row : VisualElement
        {
            public readonly Label Title = new(), Detail = new();
            public readonly VisualElement Dot = new();
            public readonly ProgressBar Bar = new() { lowValue = 0, highValue = 1 };
            public readonly TaskStrip Strip;
            public GameplayAbilityAsset Asset;
            public Row(bool ability, bool task)
            {
                AddToClassList("debug-row");
                var line = new VisualElement(); line.AddToClassList("debug-row-title");
                Dot.AddToClassList("debug-state-dot"); line.Add(Dot);
                if (ability)
                {
                    var button = new Button(() => { if (Asset != null) SkillEditorWindow.Open().Show(Asset); });
                    button.Add(Title); button.AddToClassList("debug-ability-link"); line.Add(button);
                }
                else line.Add(Title);
                Add(line); Detail.AddToClassList("debug-detail"); Add(Detail);
                Bar.AddToClassList("debug-progress"); Add(Bar);
                if (task) { Strip = new TaskStrip(); Add(Strip); }
            }
            public void Fill(string name, string detail, Color color, float fraction = -1, string progress = "")
            {
                Title.text = name; Detail.text = detail; Dot.style.backgroundColor = color;
                Bar.style.display = fraction >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
                Bar.value = Mathf.Clamp01(fraction); Bar.title = progress;
            }
        }

        [MenuItem("Window/ZFramework GAS/运行时调试")]
        public static RuntimeDebugWindow Open()
        {
            var window = GetWindow<RuntimeDebugWindow>();
            window.titleContent = new GUIContent("GAS 运行时调试"); window.minSize = new Vector2(390, 450);
            return window;
        }
        private void OnEnable()
        {
            RuntimeDebugSession.Changed += Render;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }
        private void OnDisable()
        {
            RuntimeDebugSession.Changed -= Render;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
        }
        private void PlayModeChanged(PlayModeStateChange state) => Render(RuntimeDebugSession.Current);
        public void CreateGUI()
        {
            rootVisualElement.Clear(); rootVisualElement.AddToClassList("gas-editor"); rootVisualElement.AddToClassList("runtime-debug");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.acced.zframework.gas/Editor/Window/SkillEditor.uss");
            if (sheet != null) rootVisualElement.styleSheets.Add(sheet);
            _abilities.Clear(); _tasks.Clear(); _tags.Clear(); _attributes.Clear(); _effects.Clear();
            var toolbar = new Toolbar();
            _units = new ToolbarMenu { text = "选择单位", tooltip = "列出已加载场景中的 IAbilitySystemProvider" };
            toolbar.Add(_units);
            _follow = new ToolbarToggle { text = "跟随选中", value = RuntimeDebugSession.FollowSelection };
            _follow.RegisterValueChangedCallback(e => RuntimeDebugSession.FollowSelection = e.newValue); toolbar.Add(_follow);
            toolbar.Add(new ToolbarButton(() => { RuntimeDebugSession.RefreshProviders(); RuntimeDebugSession.Refresh(); }) { text = "刷新单位" });
            rootVisualElement.Add(toolbar);
            _unitName = new Label(); _unitName.AddToClassList("debug-unit-name"); rootVisualElement.Add(_unitName);
            _hint = new Label(); _hint.AddToClassList("debug-guide"); rootVisualElement.Add(_hint);
            _scroll = new ScrollView(); rootVisualElement.Add(_scroll);
            _abilityHost = Section("技能", "debug-abilities"); _taskHost = Section("正在播放", "debug-tasks");
            _tagHost = Section("标签", "debug-tags"); _attributeHost = Section("属性", "debug-attributes"); _effectHost = Section("效果", "debug-effects");
            Render(RuntimeDebugSession.Current);
        }
        private VisualElement Section(string title, string key)
        {
            var foldout = new Foldout { text = title, value = true, viewDataKey = key, name = key };
            var content = new VisualElement(); foldout.Add(content); _scroll.Add(foldout); return content;
        }
        private static void Resize(List<Row> rows, VisualElement host, int count, bool ability = false, bool task = false)
        {
            while (rows.Count > count) { rows[rows.Count - 1].RemoveFromHierarchy(); rows.RemoveAt(rows.Count - 1); }
            while (rows.Count < count) { var row = new Row(ability, task); rows.Add(row); host.Add(row); }
            var empty = host.Q<Label>("empty");
            if (count == 0 && empty == null) { empty = new Label("暂无") { name = "empty" }; empty.AddToClassList("debug-detail"); host.Add(empty); }
            if (count > 0) empty?.RemoveFromHierarchy();
        }
        private void Render(RuntimeSnapshot snapshot)
        {
            if (_hint == null) return;
            bool playing = EditorApplication.isPlaying;
            _units.SetEnabled(playing); _follow.SetEnabled(playing);
            _follow.SetValueWithoutNotify(RuntimeDebugSession.FollowSelection);
            _units.text = RuntimeDebugSession.Observed != null ? RuntimeDebugSession.Observed.name : "选择单位";
            _units.menu.MenuItems().Clear();
            foreach (var item in RuntimeDebugSession.Providers)
            {
                if (item == null) continue;
                var unit = item;
                _units.menu.AppendAction(RuntimeDebugSession.UnitLabel(unit).Replace('/', '›') + "  #" + unit.GetInstanceID(),
                    _ => RuntimeDebugSession.Observe(unit), unit == RuntimeDebugSession.Observed ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
            if (RuntimeDebugSession.Providers.Count == 0) _units.menu.AppendAction("场景中没有可观察单位", _ => { }, DropdownMenuAction.Status.Disabled);
            _hint.style.display = snapshot == null ? DisplayStyle.Flex : DisplayStyle.None;
            _hint.text = !playing ? "进入 Play 后，在 Hierarchy 选中带有 IAbilitySystemProvider 的单位或它的子物体。\n也可以关闭「跟随选中」，从上方下拉框固定观察一个单位。\n点击技能可打开技能编辑器，勾选「跟随运行时」同步时间轴与效果高亮。" :
                "请选择单位；单位需要实现 IAbilitySystemProvider，并已初始化 AbilitySystem。";
            _scroll.style.display = snapshot != null ? DisplayStyle.Flex : DisplayStyle.None;
            _unitName.text = snapshot?.UnitName ?? "尚未观察单位";
            if (snapshot == null) return;
            Resize(_abilities, _abilityHost, snapshot.Abilities.Count, ability: true);
            for (int i = 0; i < _abilities.Count; i++)
            {
                var value = snapshot.Abilities[i]; var row = _abilities[i]; row.Asset = value.Asset;
                string status = value.Active ? "激活中" : value.OnCooldown ? "冷却中" : !value.TagGatePassed ? "标签门禁未通过" : "可尝试施放";
                if (value.Active && value.OnCooldown) status += " · 冷却中";
                if ((value.Active || value.OnCooldown) && !value.TagGatePassed) status += " · 标签门禁未通过";
                row.Fill(value.Name + "  Lv." + value.Level, status,
                    value.Active ? SkillColors.Duration : value.OnCooldown ? SkillColors.Cooldown : !value.TagGatePassed ? SkillColors.Error : SkillColors.Task,
                    value.CooldownDuration > 0 ? value.CooldownRemaining / value.CooldownDuration : -1,
                    value.OnCooldown && value.CooldownRemaining <= 0 ? "冷却标签仍在（无有限计时）" : $"冷却 {value.CooldownRemaining:0.00} / {value.CooldownDuration:0.00}s");
                row.tooltip = "只读显示激活、标签门禁和冷却；「可尝试」不保证资源足够。";
            }
            Resize(_tasks, _taskHost, snapshot.Tasks.Count, task: true);
            for (int i = 0; i < _tasks.Count; i++)
            {
                var task = snapshot.Tasks[i];
                _tasks[i].Fill(task.AbilityName + " · " + task.Definition.ClipName,
                    $"实际 {task.NormalizedTime * task.Duration:0.00} / {task.Duration:0.00}s  ·  授权 {task.AuthoredTime:0.00} / {task.Definition.Duration:0.00}s" + (task.Loop ? "  · 循环" : ""), SkillColors.Animation);
                _tasks[i].Strip.SetTask(task);
            }
            Resize(_tags, _tagHost, snapshot.Tags.Count);
            for (int i = 0; i < _tags.Count; i++)
            {
                var tag = snapshot.Tags[i];
                _tags[i].Fill(tag.Value.Name, "计数 ×" + tag.Count, SkillColors.Cost);
                _tags[i].EnableInClassList("debug-tag-new", snapshot.CapturedAt - tag.AddedAt < .6);
            }
            Resize(_attributes, _attributeHost, snapshot.Attributes.Count);
            for (int i = 0; i < _attributes.Count; i++)
            {
                var a = snapshot.Attributes[i]; var row = _attributes[i];
                row.Fill(a.Name + "  #" + a.Id.Value, $"基础 {a.BaseValue:0.##}   →   当前 {a.CurrentValue:0.##}    差值 {a.Delta:+0.##;-0.##;0}", SkillColors.Task);
                row.EnableInClassList("debug-positive", a.Delta > 0); row.EnableInClassList("debug-negative", a.Delta < 0);
            }
            Resize(_effects, _effectHost, snapshot.Effects.Count);
            for (int i = 0; i < _effects.Count; i++)
            {
                var effect = snapshot.Effects[i]; bool infinite = effect.Policy == EffectDurationPolicy.Infinite;
                _effects[i].Fill(effect.Name + "  ×" + effect.Stacks,
                    (infinite ? "无限" : "持续") + (effect.Period > 0 ? $"  ·  周期 {effect.Period:0.##}s" : "") + $"  ·  来源 {effect.SourceName}",
                    infinite ? SkillColors.Infinite : SkillColors.Duration,
                    infinite ? 1 : effect.Duration > 0 ? effect.Remaining / effect.Duration : 0,
                    infinite ? "∞" : $"剩余 {effect.Remaining:0.00} / {effect.Duration:0.00}s");
            }
        }
    }
}
