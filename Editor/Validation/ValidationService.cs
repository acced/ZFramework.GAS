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
            .Where(t => !t.IsAbstract && !t.IsInterface && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal).Select(t => (ISkillValidationRule)Activator.CreateInstance(t)).ToArray();
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
        public void ValidateNow(bool all = false, bool strict = false)
        {
            _due = double.MaxValue; var context = new ValidationContext(_doc);
            if (strict)
                foreach (var asset in _doc.Assets)
                    if (asset is IDefinitionAsset definition) definition.InvalidateDefinition();
            foreach (var key in _cache.Keys.ToArray()) if (key == null || !_doc.Assets.Contains(key)) _cache.Remove(key);
            foreach (var asset in _doc.Assets)
            {
                if (!strict && !all && !_all && !_dirty.Contains(asset) && _cache.ContainsKey(asset)) continue;
                var issues = new List<Issue>();
                ValidateAsset(context, asset, issues, strict);
                _cache[asset] = issues;
            }
            _dirty.Clear(); _all = false;
            Issues = _cache.Values.SelectMany(list => list).OrderByDescending(i => i.Severity).ThenBy(i => i.Id).ToArray(); Changed?.Invoke();
        }
        internal void ValidateAsset(ValidationContext context, ScriptableObject asset, List<Issue> issues, bool strict)
        {
            foreach (var rule in _rules)
            {
                if (!strict && EditorPrefs.GetBool("ZFramework.GAS.Validation.Disabled." + rule.Id, false)) continue;
                try { rule.Validate(context, asset, issues); }
                catch (Exception ex) { context.Add(issues, rule.Id, asset, "", "校验规则执行失败：" + ex.Message); }
            }
        }
        public void Dispose() { _doc.Changed -= Schedule; EditorApplication.update -= Update; }
    }
    public sealed class ModuleCallersRule : ISkillValidationRule
    {
        public string Id => "GAS_MODULE_CALLERS";
        public void Validate(ValidationContext context, ScriptableObject asset, List<Issue> issues)
        {
            if(asset is not SkillActionModuleAsset)return;
            string path=AssetDatabase.GetAssetPath(asset);if(string.IsNullOrEmpty(path))return;
            foreach(string guid in AssetDatabase.FindAssets("t:GameplayAbilityAsset")) {
                string callerPath=AssetDatabase.GUIDToAssetPath(guid);
                if(!AssetDatabase.GetDependencies(callerPath).Contains(path))continue;
                var caller=AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(callerPath);
                try { caller.BuildActions(); if (SkillEditorExtensions.Asset(caller) is { } assetToBuild) SkillEditorExtensions.Timeline.BuildDefinition(assetToBuild); }
                catch(Exception error) { context.Add(issues,Id,caller,"activationActions",callerPath+": "+error.Message); }
            }
        }
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
                if (p.propertyPath.StartsWith("durationMagnitude.", StringComparison.Ordinal) && !p.serializedObject.FindProperty("useDurationMagnitude").boolValue ||
                    p.propertyPath.StartsWith("periodMagnitude.", StringComparison.Ordinal) && !p.serializedObject.FindProperty("usePeriodMagnitude").boolValue) continue;
                if (p.name == "captureAttributeId" || p.name == "targetAttributeId")
                {
                    string parent = p.propertyPath.Substring(0, p.propertyPath.LastIndexOf('.'));
                    var kindProperty = p.serializedObject.FindProperty(parent + ".kind");
                    // Modifier targets are always used; magnitude attributes depend on the selected formula.
                    if (kindProperty != null)
                    {
                        var kind = (MagnitudeKind)kindProperty.enumValueIndex;
                        if (p.name == "captureAttributeId" ? kind != MagnitudeKind.AttributeScaled && kind != MagnitudeKind.Calculation : kind != MagnitudeKind.Calculation) continue;
                    }
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
                        c.Add(issues, Id, asset, p.propertyPath, string.IsNullOrEmpty(name) ? "Cue 标签为空；请指定标签或删除该区间" : "标签未注册：「" + name + "」");
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
    public static class BatchValidate
    {
        /// <summary>Build every authored GAS definition, including assets not referenced by an ability.</summary>
        public static IReadOnlyList<Issue> CheckAll()
        {
            string query = "t:GameplayAbilityAsset t:GameplayEffectAsset t:SkillActionModuleAsset t:SkillParameterSetAsset t:AbilitySetAsset";
            if (SkillEditorExtensions.Timeline is { } timeline) query += " t:" + timeline.AssetType.Name;
            var assets = AssetDatabase.FindAssets(query).Select(AssetDatabase.GUIDToAssetPath).Distinct()
                .OrderBy(path => path, StringComparer.Ordinal).SelectMany(AssetDatabase.LoadAllAssetsAtPath)
                .OfType<ScriptableObject>().Distinct().ToArray();
            var issues = new List<Issue>();
            var checkedAssets = new HashSet<ScriptableObject>();
            foreach (var ability in assets.OfType<GameplayAbilityAsset>())
            {
                using var doc = new SkillDocument(); doc.Open(ability, false);
                using var service = new ValidationService(doc); service.ValidateNow(strict: true);
                issues.AddRange(service.Issues);
                checkedAssets.UnionWith(doc.Assets);
            }
            using var standalone = new SkillDocument();
            using var standaloneValidation = new ValidationService(standalone);
            var context = new ValidationContext(standalone);
            foreach (var asset in assets)
            {
                if (!checkedAssets.Add(asset)) continue;
                if (asset is IDefinitionAsset definition) definition.InvalidateDefinition();
                standaloneValidation.ValidateAsset(context, asset, issues, true);
            }
            return issues;
        }

        internal static int Log(IReadOnlyList<Issue> issues)
        {
            int errors = 0;
            foreach (var issue in issues)
            {
                string text = $"[{issue.Id}] {AssetDatabase.GetAssetPath(issue.Asset)} / {issue.Asset.name} / {issue.PropertyPath}: {issue.Message}";
                if (issue.Severity == IssueSeverity.Error) { errors++; Debug.LogError(text, issue.Asset); }
                else if (issue.Severity == IssueSeverity.Warning) Debug.LogWarning(text, issue.Asset);
            }
            return errors;
        }

        [MenuItem("Tools/ZFramework GAS/校验所有技能")]
        public static void Run()
        {
            var issues = CheckAll();
            int errors = Log(issues);
            Debug.Log($"GAS 批量校验：{errors} 个错误，{issues.Count(i => i.Severity == IssueSeverity.Warning)} 个警告。");
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
                var button = new Button(() => SkillActionNavigation.Select(_doc, issue))
                { text = (issue.Severity == IssueSeverity.Error ? "✕" : issue.Severity == IssueSeverity.Warning ? "!" : "i") + $"  {issue.Id}  {issue.Asset.name}：{issue.Message}" };
                button.style.flexGrow = 1; button.style.unityTextAlign = TextAnchor.MiddleLeft; button.style.color = issue.Severity == IssueSeverity.Error ? SkillColors.Error : issue.Severity == IssueSeverity.Warning ? SkillColors.Warning : SkillColors.Task;
                row.Add(button); if (issue.FixAction != null) row.Add(new Button(issue.FixAction) { text = "修复" }); _list.Add(row);
            }
            if (_list.childCount == 0) _list.Add(new Label("✓ 没有匹配的校验消息"));
        }
        public void Dispose() { _validation.Changed -= Refresh; _doc.Selection.Changed -= Refresh; }
    }
}
