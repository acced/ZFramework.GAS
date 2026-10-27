using System;
using System.Collections.Generic;
using System.Linq;
using GameplayTags;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public enum IssueSeverity { Info, Warning, Error }
    public sealed class Issue
    {
        public string Id, Message, PropertyPath, ElementId, Track;
        public IssueSeverity Severity;
        public ScriptableObject Asset;
        public Action FixAction;
    }
    public sealed class ValidationContext
    {
        public SkillDocument Document { get; }
        public ValidationContext(SkillDocument document) => Document = document;
        public void Add(List<Issue> issues, string id, ScriptableObject asset, string path, string message, IssueSeverity severity = IssueSeverity.Error,
            Action fix = null, string track = null, string elementId = null)
            => issues.Add(new Issue { Id = id, Asset = asset, PropertyPath = path, Message = message, Severity = severity, FixAction = fix, Track = track, ElementId = elementId });
        public Action Edit(ScriptableObject asset, string label, Action<SerializedObject> edit)
            => () => EditCommand.Run(Document, label, asset, edit, true);
    }
    public interface ISkillValidationRule
    {
        string Id { get; }
        void Validate(ValidationContext context, ScriptableObject asset, List<Issue> issues);
    }
    public sealed class ValidationService : IDisposable
    {
        private readonly SkillDocument _doc;
        private double _due = double.MaxValue;
        private readonly Dictionary<ScriptableObject, List<Issue>> _cache = new();
        private readonly HashSet<ScriptableObject> _dirty = new();
        private bool _all = true;
        public IReadOnlyList<Issue> Issues { get; private set; } = Array.Empty<Issue>();
        public event Action Changed;
        private readonly ISkillValidationRule[] _rules = TypeCache.GetTypesDerivedFrom<ISkillValidationRule>()
            .Where(t => !t.IsAbstract && !t.IsInterface && t.GetConstructor(Type.EmptyTypes) != null).Select(t => (ISkillValidationRule)Activator.CreateInstance(t)).ToArray();
        public ValidationService(SkillDocument doc)
        { _doc = doc; doc.Changed += Schedule; EditorApplication.update += Update; }
        private void Schedule(ChangeSet change)
        {
            _due = EditorApplication.timeSinceStartup + .15;
            if (change.Structure || change.Asset == _doc.Root) _all = true;
            else if (change.Asset is ScriptableObject asset) _dirty.Add(asset);
            if (_doc.Root != null) _dirty.Add(_doc.Root);
        }
        private void Update() { if (EditorApplication.timeSinceStartup >= _due) ValidateNow(); }
        public void ValidateNow(bool all = false)
        {
            _due = double.MaxValue; var context = new ValidationContext(_doc);
            foreach (var key in _cache.Keys.ToArray()) if (key == null || !_doc.Assets.Contains(key)) _cache.Remove(key);
            foreach (var asset in _doc.Assets)
            {
                if (!all && !_all && !_dirty.Contains(asset) && _cache.ContainsKey(asset)) continue;
                var issues = new List<Issue>();
                foreach (var rule in _rules)
                {
                    if (EditorPrefs.GetBool("ZFramework.GAS.Validation.Disabled." + rule.Id, false)) continue;
                    try { rule.Validate(context, asset, issues); }
                    catch (Exception ex) { context.Add(issues, rule.Id, asset, "", "校验规则执行失败：" + ex.Message); }
                }
                _cache[asset] = issues;
            }
            _dirty.Clear(); _all = false;
            Issues = _cache.Values.SelectMany(list => list).OrderByDescending(i => i.Severity).ThenBy(i => i.Id).ToArray(); Changed?.Invoke();
        }
        public void Dispose() { _doc.Changed -= Schedule; EditorApplication.update -= Update; }
    }
    public sealed class CostRule : ISkillValidationRule
    {
        public string Id => "GAS001";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not GameplayAbilityAsset) return;
            var cost = c.Document.Serialized(asset).FindProperty("cost").objectReferenceValue as GameplayEffectAsset;
            if (cost != null && c.Document.Serialized(cost).FindProperty("durationPolicy").enumValueIndex != 0)
                c.Add(issues, Id, cost, "durationPolicy", "消耗必须是瞬时（Instant）效果", fix: c.Edit(cost, "修复消耗策略", so => so.FindProperty("durationPolicy").enumValueIndex = 0));
        }
    }
    public sealed class CooldownRule : ISkillValidationRule
    {
        public string Id => "GAS002";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not GameplayAbilityAsset) return;
            var cooldown = c.Document.Serialized(asset).FindProperty("cooldown").objectReferenceValue as GameplayEffectAsset;
            if (cooldown == null) return;
            var so = c.Document.Serialized(cooldown);
            if (!PortRules.HasGrantedTag(so)) { c.Add(issues, Id, cooldown, "tags.granted", "冷却效果必须至少授予一个标签"); return; }
            // 加载时未注册的标签会被跳过；一个都解析不出来时，运行期构造能力定义会直接抛异常。
            var granted = so.FindProperty("tags.granted"); var names = new List<string>();
            for (int i = 0; i < granted.arraySize; i++)
            {
                string name = granted.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (GameplayTagManager.RequestTag(name).IsValid) return;
                names.Add(name);
            }
            c.Add(issues, Id, cooldown, "tags.granted", "冷却授予的标签都未注册：「" + string.Join("」「", names) + "」。运行期构造能力定义会抛异常，请先在 Gameplay Tags 词表中注册");
        }
    }
    public sealed class ChainRule : ISkillValidationRule
    {
        public string Id => "GAS003";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not GameplayEffectAsset) return;
            bool Visit(ScriptableObject current, HashSet<ScriptableObject> path, int depth)
            {
                if (!path.Add(current) || depth > 8) return true;
                bool bad = PortRules.Links(current).Any(l => Visit(l.Child, path, depth + 1)); path.Remove(current); return bad;
            }
            if (Visit(asset, new HashSet<ScriptableObject>(), 0)) c.Add(issues, Id, asset, "conditionalEffects", "子效果链成环，或深度超过 8");
        }
    }
    public sealed class DuplicateEffectRule : ISkillValidationRule
    {
        public string Id => "GAS004";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not GameplayEffectAsset) return;
            var list = c.Document.Serialized(asset).FindProperty("conditionalEffects"); var seen = new HashSet<string>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var item = list.GetArrayElementAtIndex(i); var effect = item.FindPropertyRelative("effect").objectReferenceValue;
                string key = AssetReferences.Key(effect) + "/" + item.FindPropertyRelative("trigger").enumValueIndex;
                if (effect == null) { c.Add(issues, Id, asset, item.propertyPath, "子效果未指定资产", IssueSeverity.Warning); continue; }
                if (!seen.Add(key))
                    c.Add(issues, Id, asset, item.propertyPath, "同一子效果在同一时机重复挂载", fix: c.Edit(asset, "移除重复子效果", so =>
                    {
                        var array = so.FindProperty("conditionalEffects"); var keys = new HashSet<string>();
                        for (int j = 0; j < array.arraySize; j++)
                        { var e = array.GetArrayElementAtIndex(j); string k = AssetReferences.Key(e.FindPropertyRelative("effect").objectReferenceValue) + "/" + e.FindPropertyRelative("trigger").enumValueIndex; if (!keys.Add(k)) array.DeleteArrayElementAtIndex(j--); }
                    }));
            }
        }
    }
    public sealed class ClipRule : ISkillValidationRule
    {
        public string Id => "GAS010";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation) return;
            if (string.IsNullOrWhiteSpace(animation.ClipName)) c.Add(issues, Id, asset, "clipName", "动画片段名为空");
            else if (c.Document.PreviewPrefab != null)
            {
                var adapter = AnimationAdapterRegistry.Resolve(c.Document.PreviewPrefab);
                if (!adapter.EnumerateClips(c.Document.PreviewPrefab).Any(x => x.Name == animation.ClipName))
                    c.Add(issues, Id, asset, "clipName", "预览角色上找不到片段「" + animation.ClipName + "」", IssueSeverity.Warning);
            }
        }
    }
    public sealed class SpanRule : ISkillValidationRule
    {
        public string Id => "GAS011";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation) return;
            bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
            if (!(animation.Duration > 0) || !Finite(animation.Duration)) { c.Add(issues, Id, asset, "duration", "动画时长必须是有限正数"); return; }
            if (!(animation.FrameRate > 0) || !Finite(animation.FrameRate)) c.Add(issues, Id, asset, "frameRate", "显示帧率必须是有限正数");
            var so = c.Document.Serialized(asset);
            foreach (var track in TrackTypeRegistry.Types)
            {
                var array = so.FindProperty(track.PropertyPath); if (array == null) continue;
                for (int i = 0; i < array.arraySize; i++)
                {
                    var e = array.GetArrayElementAtIndex(i); var span = track.ReadSpan(e); string id = e.FindPropertyRelative("id").stringValue;
                    if (!Finite(span.start) || !Finite(span.end) || span.start < 0 || span.start > animation.Duration || span.end > animation.Duration || span.end >= 0 && span.end < span.start)
                        c.Add(issues, Id, asset, e.propertyPath, track.DisplayName + "超出时长或起止顺序错误", fix: c.Edit(asset, "修复时间范围", target =>
                        {
                            var p = TimelineCommands.Find(target, track.PropertyPath, id); if (p == null) return; var s = track.ReadSpan(p);
                            float start = Finite(s.start) ? Mathf.Clamp(s.start, 0, animation.Duration) : 0;
                            float end = Finite(s.end) ? s.end < 0 ? -1 : Mathf.Clamp(s.end, start, animation.Duration) : animation.Duration;
                            track.WriteSpan(p, start, end);
                        }), track: track.PropertyPath, elementId: id);
                }
            }
        }
    }
    public sealed class MarkerKeyRule : ISkillValidationRule
    {
        public string Id => "GAS012";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset) return;
            var array = c.Document.Serialized(asset).FindProperty("markers"); var seen = new HashSet<string>();
            for (int i = 0; i < array.arraySize; i++)
            {
                var e = array.GetArrayElementAtIndex(i); string key = e.FindPropertyRelative("key").stringValue, id = e.FindPropertyRelative("id").stringValue;
                if (!string.IsNullOrEmpty(key) && !seen.Add(key))
                    c.Add(issues, Id, asset, e.propertyPath, "事件名称重复：「" + key + "」", fix: c.Edit(asset, "修复事件名称", so =>
                    {
                        var p = TimelineCommands.Find(so, "markers", id); if (p == null) return;
                        var names = new HashSet<string>(); var all = so.FindProperty("markers");
                        for (int j = 0; j < all.arraySize; j++) names.Add(all.GetArrayElementAtIndex(j).FindPropertyRelative("key").stringValue);
                        int suffix = 2; while (names.Contains(key + "_" + suffix)) suffix++; p.FindPropertyRelative("key").stringValue = key + "_" + suffix;
                    }), track: "markers", elementId: id);
            }
        }
    }
    public sealed class ClipLengthRule : ISkillValidationRule
    {
        public string Id => "GAS013";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation || c.Document.PreviewPrefab == null) return;
            var clips = AnimationAdapterRegistry.Resolve(c.Document.PreviewPrefab).EnumerateClips(c.Document.PreviewPrefab);
            foreach (var clip in clips)
                if (clip.Name == animation.ClipName && Mathf.Abs(clip.Length - animation.Duration) > .5f / Mathf.Max(1, animation.FrameRate))
                    c.Add(issues, Id, asset, "duration", $"授权时长 {animation.Duration:0.###}s 与片段 {clip.Length:0.###}s 不同", IssueSeverity.Warning);
        }
    }
    public sealed class CallerParameterRule : ISkillValidationRule
    {
        public string Id => "GAS020";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not GameplayEffectAsset || c.Document.Root == null) return;
            var declared = c.Document.Serialized(c.Document.Root).FindProperty("callerParameters"); var keys = new HashSet<string>();
            for (int i = 0; i < declared.arraySize; i++) keys.Add(declared.GetArrayElementAtIndex(i).FindPropertyRelative("key").stringValue);
            var modifiers = c.Document.Serialized(asset).FindProperty("modifiers");
            for (int i = 0; i < modifiers.arraySize; i++)
            {
                var magnitude = modifiers.GetArrayElementAtIndex(i).FindPropertyRelative("magnitude"); if (magnitude.FindPropertyRelative("kind").enumValueIndex != 2) continue;
                string key = magnitude.FindPropertyRelative("callerKey").stringValue;
                if (string.IsNullOrWhiteSpace(key)) c.Add(issues, Id, asset, magnitude.propertyPath, "SetByCaller 键为空");
                else if (!keys.Contains(key)) c.Add(issues, Id, asset, magnitude.propertyPath, "能力未声明调用方参数「" + key + "」", IssueSeverity.Warning,
                    c.Edit(c.Document.Root, "声明调用方参数", so => { var array = so.FindProperty("callerParameters"); int index = array.arraySize++; var item = array.GetArrayElementAtIndex(index); item.FindPropertyRelative("key").stringValue = key; item.FindPropertyRelative("suggestedValue").floatValue = 0; item.FindPropertyRelative("description").stringValue = "由宿主传入"; }));
            }
        }
    }
    public sealed class AttributeRule : ISkillValidationRule
    {
        public string Id => "GAS021";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not GameplayEffectAsset || AttributeCatalog.Entries.Count == 0) return;
            var p = c.Document.Serialized(asset).GetIterator();
            while (p.Next(true))
            {
                if (p.propertyType != SerializedPropertyType.Integer || !p.name.EndsWith("AttributeId", StringComparison.Ordinal)) continue;
                if (p.name == "captureAttributeId")
                {
                    string parent = p.propertyPath.Substring(0, p.propertyPath.LastIndexOf('.'));
                    if (p.serializedObject.FindProperty(parent + ".kind").enumValueIndex != 1) continue;
                }
                if (p.name == "durationAttributeId" && p.serializedObject.FindProperty("durationPolicy").enumValueIndex != 1) continue;
                if (p.intValue < 0 && p.name != "targetAttributeId") continue;
                if (!AttributeCatalog.Entries.Any(e => e.id == p.intValue)) c.Add(issues, Id, asset, p.propertyPath, "属性 #" + p.intValue + " 不在词表中", IssueSeverity.Warning);
            }
        }
    }
    public sealed class CueTagRule : ISkillValidationRule
    {
        public string Id => "GAS022";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            var p = c.Document.Serialized(asset).GetIterator();
            while (p.Next(true))
                if (p.propertyType == SerializedPropertyType.String && p.name == "m_Name" && p.propertyPath != "m_Name")
                {
                    string name = p.stringValue;
                    bool cue = p.propertyPath.Contains("cue");
                    if (cue && string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(name) && !GameplayTagManager.RequestTag(name).IsValid)
                        c.Add(issues, Id, asset, p.propertyPath, string.IsNullOrEmpty(name) ? "Cue 标签为空；运行期会跳过该区间" : "标签未注册：「" + name + "」", IssueSeverity.Warning);
                }
        }
    }
    public sealed class PeriodicRule : ISkillValidationRule
    {
        public string Id => "GAS030";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not GameplayEffectAsset) return;
            var so = c.Document.Serialized(asset);
            if (so.FindProperty("period").floatValue > 0 && so.FindProperty("modifiers").arraySize > 0)
                c.Add(issues, Id, asset, "modifiers", "周期修改器作用于 BaseValue；常驻加成请拆成独立子效果", IssueSeverity.Info);
        }
    }
    public sealed class BackendRule : ISkillValidationRule
    {
        public string Id => "GAS031";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset != c.Document.Root || c.Document.PreviewPrefab == null) return;
            var animator = c.Document.PreviewPrefab.GetComponentInChildren<SkillAnimator>(true);
            if (animator == null) c.Add(issues, Id, asset, "", "预览角色未挂 SkillAnimator；宿主仍可手动创建驱动", IssueSeverity.Warning);
            else if (animator.Backend == null) c.Add(issues, Id, asset, "", "SkillAnimator 未选择后端", IssueSeverity.Warning);
            else if (!animator.Backend.Validate(c.Document.PreviewPrefab, out string error)) c.Add(issues, Id, asset, "", error, IssueSeverity.Warning);
            else if (animator.Backend is AnimatorBackend backend)
            {
                var controller = c.Document.PreviewPrefab.GetComponentInChildren<Animator>(true)?.runtimeAnimatorController;
                while (controller is AnimatorOverrideController overrides) controller = overrides.runtimeAnimatorController;
                if (controller is UnityEditor.Animations.AnimatorController ac && backend.layer >= ac.layers.Length)
                    c.Add(issues, Id, asset, "", "Animator 层索引越界", IssueSeverity.Warning);
            }
        }
    }
    public sealed class TaskEventRule : ISkillValidationRule
    {
        public string Id => "GAS032";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset) return;
            var array = c.Document.Serialized(asset).FindProperty("taskEvents");
            for (int i = 0; i < array.arraySize; i++)
            {
                var item = array.GetArrayElementAtIndex(i); var spawner = item.FindPropertyRelative("spawner").managedReferenceValue;
                string error = spawner == null ? "任务没有选择生成器" : spawner is ProjectileSpawner p && (!(p.speed > 0) || p.visualPrefab == null) ? "投射物任务需要视觉预制体和正速度" :
                    spawner is AreaEffectSpawner a && (a.enterEffect == null || !(a.radius > 0) || a.maxOccupants < 1) ? "区域任务需要效果、正半径和容量" : null;
                if (error != null) c.Add(issues, Id, asset, item.propertyPath, error, track: "taskEvents", elementId: item.FindPropertyRelative("id").stringValue);
            }
        }
    }
    public static class BatchValidate
    {
        [MenuItem("Tools/ZFramework GAS/校验所有技能")]
        public static void Run()
        {
            int errors = 0, warnings = 0;
            foreach (var ability in SkillIndex.Abilities)
            {
                using var doc = new SkillDocument(); doc.Open(ability, false);
                using var service = new ValidationService(doc); service.ValidateNow(true);
                foreach (var issue in service.Issues)
                {
                    string text = $"[{issue.Id}] {ability.name} / {issue.Asset.name}: {issue.Message}";
                    if (issue.Severity == IssueSeverity.Error) { errors++; Debug.LogError(text, issue.Asset); }
                    else if (issue.Severity == IssueSeverity.Warning) { warnings++; Debug.LogWarning(text, issue.Asset); }
                }
            }
            Debug.Log($"GAS 批量校验：{errors} 个错误，{warnings} 个警告。");
            if (Application.isBatchMode) EditorApplication.Exit(errors == 0 ? 0 : 1);
        }
    }
    public sealed class MessagePanel : VisualElement, IDisposable
    {
        private readonly SkillDocument _doc; private readonly ValidationService _validation;
        private readonly ScrollView _list; private readonly DropdownField _filter;
        public MessagePanel(SkillDocument doc, ValidationService validation)
        {
            _doc = doc; _validation = validation; AddToClassList("message-panel");
            _filter = new DropdownField("消息", new List<string> { "全部", "错误", "警告", "提示", "当前选中资产" }, 0);
            _filter.RegisterValueChangedCallback(_ => Refresh()); Add(_filter);
            _list = new ScrollView(); _list.style.flexGrow = 1; Add(_list);
            validation.Changed += Refresh; doc.Selection.Changed += Refresh;
        }
        private void Refresh()
        {
            _list.Clear();
            foreach (var issue in _validation.Issues)
            {
                if (_filter.index == 1 && issue.Severity != IssueSeverity.Error || _filter.index == 2 && issue.Severity != IssueSeverity.Warning || _filter.index == 3 && issue.Severity != IssueSeverity.Info || _filter.index == 4 && issue.Asset != _doc.Selection.Asset) continue;
                var row = new VisualElement(); row.AddToClassList("issue-row");
                var button = new Button(() => _doc.Selection.Select(issue.Asset, issue.Track, issue.ElementId))
                { text = (issue.Severity == IssueSeverity.Error ? "✕" : issue.Severity == IssueSeverity.Warning ? "!" : "i") + $"  {issue.Id}  {issue.Asset.name}：{issue.Message}" };
                button.style.flexGrow = 1; button.style.unityTextAlign = TextAnchor.MiddleLeft; button.style.color = issue.Severity == IssueSeverity.Error ? SkillColors.Error : issue.Severity == IssueSeverity.Warning ? SkillColors.Warning : SkillColors.Task;
                row.Add(button); if (issue.FixAction != null) row.Add(new Button(issue.FixAction) { text = "修复" }); _list.Add(row);
            }
            if (_list.childCount == 0) _list.Add(new Label("✓ 没有匹配的校验消息"));
        }
        public void Dispose() { _validation.Changed -= Refresh; _doc.Selection.Changed -= Refresh; }
    }
}
