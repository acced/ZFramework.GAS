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
        private Label _watch;
        private readonly List<SkillTrace> _traceItems = new();
        private ListView _trace;
        private SkillTraceLanes _lanes;
        private ToolbarSearchField _traceFilter;
        private ToolbarToggle _freeze;
        private SkillHistory _history;
        private readonly List<SkillTrace> _frozen=new();
        private int _page, _kind=-1;
        private long _lastSequence = -1;
        private ScrollView _scroll;
        private readonly List<Row> _abilities = new(), _tasks = new(), _tags = new(), _attributes = new(), _effects = new(), _actions = new();
        private VisualElement _abilityHost, _taskHost, _tagHost, _attributeHost, _effectHost, _actionHost;

        private sealed class Row : VisualElement
        {
            public readonly Label Title = new(), Detail = new();
            public readonly VisualElement Dot = new();
            public readonly ProgressBar Bar = new() { lowValue = 0, highValue = 1 };
            public readonly TaskStrip Strip;
            public GameplayAbilityAsset Asset;
            public string ActionId;
            public Row(bool ability, bool task)
            {
                AddToClassList("debug-row");
                this.AddManipulator(new ContextualMenuManipulator(e => e.menu.AppendAction("切换此动作断点", _ => SkillActionDebugger.Toggle(ActionId),
                    string.IsNullOrEmpty(ActionId) ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal)));
                var line = new VisualElement(); line.AddToClassList("debug-row-title");
                Dot.AddToClassList("debug-state-dot"); line.Add(Dot);
                if (ability)
                {
                    var button = new Button(() => { if (Asset != null) { var window = SkillEditorWindow.Open(); window.Show(Asset); SkillActionNavigation.Select(window.Document, ActionId); } });
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
            SkillEventRecorder.Acquire(); RuntimeDebugSession.Changed += Render;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }
        private void OnDisable()
        {
            SkillEventRecorder.Release(); RuntimeDebugSession.Changed -= Render;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
        }
        private void PlayModeChanged(PlayModeStateChange state) => Render(RuntimeDebugSession.Current);
        public void CreateGUI()
        {
            rootVisualElement.Clear(); rootVisualElement.AddToClassList("gas-editor"); rootVisualElement.AddToClassList("runtime-debug");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.acced.zframework.gas/Editor/Window/SkillEditor.uss");
            if (sheet != null) rootVisualElement.styleSheets.Add(sheet);
            _abilities.Clear(); _tasks.Clear(); _tags.Clear(); _attributes.Clear(); _effects.Clear(); _actions.Clear();
            var toolbar = new Toolbar();
            _units = new ToolbarMenu { text = "选择单位", tooltip = "列出已加载场景中的 IAbilitySystemProvider" };
            toolbar.Add(_units);
            _follow = new ToolbarToggle { text = "跟随选中", value = RuntimeDebugSession.FollowSelection };
            _follow.RegisterValueChangedCallback(e => RuntimeDebugSession.FollowSelection = e.newValue); toolbar.Add(_follow);
            toolbar.Add(new ToolbarButton(() => { RuntimeDebugSession.RefreshProviders(); RuntimeDebugSession.Refresh(); }) { text = "刷新单位" });
            toolbar.Add(new ToolbarButton(() => { SkillActionDebugger.PauseNext = true; if(EditorApplication.isPlaying) EditorApplication.isPaused=false; }) { text = "下个动作暂停" });
            toolbar.Add(new ToolbarButton(()=>{if(EditorApplication.isPlaying) EditorApplication.isPaused=false;}){text="继续"});
            toolbar.Add(new ToolbarButton(SkillActionDebugger.StepScene) { text = "场景逐帧" });
            rootVisualElement.Add(toolbar);
            _unitName = new Label(); _unitName.AddToClassList("debug-unit-name"); rootVisualElement.Add(_unitName);
            _hint = new Label(); _hint.AddToClassList("debug-guide"); rootVisualElement.Add(_hint);
            var tabs=new Toolbar();rootVisualElement.Add(tabs);
            var names=new[]{"单位","事件","监视"};
            for(int i=0;i<names.Length;i++){int page=i;tabs.Add(new ToolbarButton(()=>{_page=page;ShowPage();}){text=names[i]});}
            _scroll = new ScrollView(); rootVisualElement.Add(_scroll);
            _abilityHost = Section("技能", "debug-abilities"); _taskHost = Section("正在播放", "debug-tasks");
            _tagHost = Section("标签", "debug-tags"); _attributeHost = Section("属性", "debug-attributes"); _effectHost = Section("效果", "debug-effects");
            _actionHost = Section("动作与独立任务", "debug-actions");
            var traceHost=Section("事件历史 · 最近 4096 条", "debug-events");
            var filters=new Toolbar(); _freeze=new ToolbarToggle{text="冻结"}; filters.Add(_freeze);
            var kinds=new ToolbarMenu{text="全部类型"};filters.Add(kinds);
            void SelectKind(int value){_kind=value;kinds.text=value<0?"全部类型":((TraceKind)value).ToString();_lastSequence=-1;RefreshTrace(RuntimeDebugSession.Current);}
            kinds.menu.AppendAction("全部类型",_=>SelectKind(-1));
            foreach(TraceKind type in Enum.GetValues(typeof(TraceKind)))kinds.menu.AppendAction(type.ToString(),_=>SelectKind((int)type));
            _traceFilter=new ToolbarSearchField {tooltip="按技能名或内容搜索"}; filters.Add(_traceFilter); traceHost.Add(filters);
            _trace=new ListView(_traceItems,20,()=>new Label(),(el,i)=>{var e=_traceItems[i]; ((Label)el).text=$"帧 {e.Frame} · {e.Kind} · {(e.Skill!=null?e.Skill.name:"")}  {e.Label}";});
            _lanes=new SkillTraceLanes(_traceItems,i=>{var picked=_traceItems[i]; _freeze.value=true;
                int at=_traceItems.FindIndex(e=>e.Serial==picked.Serial); if(at>=0){_trace.SetSelection(at);_trace.ScrollToItem(at);} });traceHost.Add(_lanes);
            _trace.style.height=155; traceHost.Add(_trace);
            _trace.selectionChanged+=items=>{foreach(SkillTrace e in items) if(e.Skill!=null)
            {
                var w=SkillEditorWindow.Open();w.Show(e.Skill);
                if(e.Kind==TraceKind.标记 && e.Skill.Animation()!=null)
                {
                    w.Document.Selection.Select(e.Skill.Animation(),"markers",e.RawId);
                    var p=TimelineCommands.Find(w.Document.Serialized(e.Skill.Animation()),"markers",e.RawId);
                    if(p!=null && !w.Document.Playback.Live)w.Document.Playback.Seek(p.FindPropertyRelative("time").floatValue,e.Skill.Animation().Duration);
                }
                else if(SkillActionNavigation.Select(w.Document,e.NavigationId ?? e.RawId) || (e.NavigationId != e.RawId && SkillActionNavigation.Select(w.Document,e.RawId)))w.Document.OpenFlow();
            }};
            _traceFilter.RegisterValueChangedCallback(_=>{_lastSequence=-1; RefreshTrace(RuntimeDebugSession.Current);});
            _watch=new Label{style={whiteSpace=WhiteSpace.Normal}}; Section("断点监视","debug-watch").Add(_watch);
            _freeze.RegisterValueChangedCallback(e=>{
                _frozen.Clear();
                if(e.newValue && _history!=null)for(int i=0;i<_history.Count;i++)_frozen.Add(_history[i]);
                _lastSequence=-1;RefreshTrace(RuntimeDebugSession.Current);
            });
            ShowPage();
            Render(RuntimeDebugSession.Current);
        }
        private void ShowPage()
        {
            foreach(var f in _scroll.Query<Foldout>().ToList())
                f.style.display=(_page==(f.name=="debug-events"?1:f.name=="debug-watch"?2:0))?DisplayStyle.Flex:DisplayStyle.None;
            _lastSequence=-1;RefreshTrace(RuntimeDebugSession.Current);
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
        private void RefreshTrace(RuntimeSnapshot snapshot)
        {
            if(_trace==null) return;
            var history=SkillEventRecorder.Of(snapshot?.Owner); bool same=history==_history;
            _watch.text=SkillActionDebugger.Frame is { } f && f.Owner==snapshot?.Owner ? f.Summary : "在流程节点按 F9 设置断点。";
            if(!same){_history=history;_freeze.SetValueWithoutNotify(false);_frozen.Clear();}
            long revision=_freeze.value?_frozen.Count:history?.Sequence??0;
            if(_page!=1 || same && revision==_lastSequence) return;
            _lastSequence=revision; _traceItems.Clear();
            string filter=_traceFilter.value;
            int count=_freeze.value?_frozen.Count:history?.Count??0;
            for(int i=count-1;i>=0;i--){var e=_freeze.value?_frozen[i]:history[i];if(_kind>=0 && _kind!=(int)e.Kind)continue;
                if(string.IsNullOrEmpty(filter) || (e.Kind+" "+e.Label+" "+(e.Skill!=null?e.Skill.name:"")).IndexOf(filter,StringComparison.OrdinalIgnoreCase)>=0)_traceItems.Add(e);}
            _trace.RefreshItems();_lanes.MarkDirtyRepaint();
        }
        private void Render(RuntimeSnapshot snapshot)
        {
            RefreshTrace(snapshot);
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
                row.ActionId = null;
                if (!value.Failure.Succeeded)
                    status += "\n最近失败：" + value.Failure.Message + (value.Failure.Tag.IsValid ? " · " + value.Failure.Tag.Name : "") +
                        (value.Failure.Amount > 0 ? " · " + value.Failure.Amount.ToString("0.###") : "");
                foreach (var parameter in value.Parameters)
                {
                    string label = parameter.Key;
                    foreach (var declared in SkillNumberDrawer.Parameters(value.Asset)) if (declared.Id == parameter.Key) { label = declared.displayName; break; }
                    status += "\n" + label + " = " + parameter.Value;
                }
                row.Fill(value.Name + "  Lv." + value.Level, status,
                    value.Active ? SkillColors.Duration : value.OnCooldown ? SkillColors.Cooldown : !value.TagGatePassed ? SkillColors.Error : SkillColors.Task,
                    value.CooldownDuration > 0 ? value.CooldownRemaining / value.CooldownDuration : -1,
                    value.OnCooldown && value.CooldownRemaining <= 0 ? "冷却标签仍在（无有限计时）" : $"冷却 {value.CooldownRemaining:0.00} / {value.CooldownDuration:0.00}s");
                row.tooltip = "显示实际施放时的资源、标签或冷却门槛。";
            }
            Resize(_actions, _actionHost, snapshot.Actions.Count, ability: true);
            for (int i = 0; i < _actions.Count; i++)
            {
                var state = snapshot.Actions[i]; var row = _actions[i]; row.Asset = state.Asset; row.ActionId = state.Id;
                row.Fill((state.Asset != null ? state.Asset.DisplayName : "技能") + (state.Independent ? " · 场景任务" : " · 技能任务"), state.State, SkillColors.Task);
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
