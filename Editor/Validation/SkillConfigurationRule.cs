using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class SkillConfigurationRule : ISkillValidationRule
    {
        public string Id => "GAS_ACTION";
        public void Validate(ValidationContext context, ScriptableObject asset, List<Issue> issues)
        {
            void Check(string id, Action build)
            {
                try { build(); }
                catch (Exception error)
                {
                    string path = "", actionId = (error as SkillActionException)?.ActionId;
                    using var so = new SerializedObject(asset); var p = so.GetIterator();
                    while (!string.IsNullOrEmpty(actionId) && p.Next(true))
                        if (p.name == "id" && p.propertyType == SerializedPropertyType.String && p.stringValue == actionId)
                        { path = p.propertyPath.Substring(0, p.propertyPath.LastIndexOf('.')); break; }
                    context.Add(issues, id, asset, path, error.Message, track: path.Length > 0 ? "@field" : null, elementId: path);
                }
            }
            if (asset is GameplayAbilityAsset skill)
            {
                Check("GAS_DEFINITION", () => _ = skill.Definition);
                Check(Id, () => { ValidateParameters(skill); skill.BuildActions(); });
            }
            else if (asset is GameplayEffectAsset definitionEffect) Check("GAS_DEFINITION", () => _ = definitionEffect.Definition);
            else if (SkillEditorExtensions.Supports(asset)) Check(Id, () =>
            {
                SkillEditorExtensions.Timeline.BuildDefinition(asset);
                if (context.Document.Root == null) return;
                var builder = new SkillActionBuildContext(context.Document.Root, new HashSet<string>());
                using var timeline = new SerializedObject(asset);
                foreach (var entry in SkillEditorExtensions.Entries(timeline))
                {
                    var actions = new SkillAction[entry.Actions.arraySize];
                    for (int i = 0; i < actions.Length; i++) actions[i] = entry.Actions.GetArrayElementAtIndex(i).managedReferenceValue as SkillAction;
                    builder.Build(actions);
                }
            });
            else if (asset is SkillActionModuleAsset module) Check(Id, () => module.Build());
            else if (asset is SkillParameterSetAsset parameters)
                Check(Id, () => ParameterScopeAction.ResolveParameters(new EffectEvaluationContext(1), parameters.parameters, Array.Empty<ModuleParameterBinding>()));
            else if (asset is AbilitySetAsset set) Check(Id, () => ValidateSet(set));

            // Reusable presentation assets acquire their parameter scope at the ability use site.
            if (asset is not GameplayAbilityAsset && asset is not SkillActionModuleAsset && context.Document.Root == null) return;
            var known = new Dictionary<string, SkillParameter>();
            foreach (var p in SkillNumberDrawer.Parameters(asset is SkillActionModuleAsset ? asset : context.Document.Root)) if (p != null) known[p.Id] = p;
            using var serialized = new SerializedObject(asset); var field = serialized.GetIterator();
            SkillParameter FindParameter(SerializedProperty property,string id)
            {
                var local=SkillNumberDrawer.LocalScope(property);
                return local!=null ? local.FirstOrDefault(p=>p!=null && p.Id==id) : known.TryGetValue(id,out var parameter) ? parameter : null;
            }
            while (field.Next(true))
            {
                // Resolve every effect reference at its use site (cost, cooldown or action). A shared effect can legitimately run with different module-local parameters.
                if(asset is not GameplayEffectAsset && field.propertyType==SerializedPropertyType.ObjectReference && field.objectReferenceValue is GameplayEffectAsset effect){
                    foreach(string id in SkillFlowCommands.RequiredEffectParameters(effect))
                        if(FindParameter(field,id)==null)context.Add(issues,Id,asset,field.propertyPath,"引用的效果需要参数 "+id+"，当前作用域没有声明。",track:"@field",elementId:field.propertyPath);
                }
                if(asset is GameplayEffectAsset)continue;
                if (field.propertyType != SerializedPropertyType.String || string.IsNullOrEmpty(field.stringValue)) continue;
                if (field.name != "parameterId" && field.name != "booleanParameterId" && field.name != "callerParameterId" && field.name!="indexParameterId" && field.name!="scaleParameterId") continue;
                int dot = field.propertyPath.LastIndexOf('.');
                var parent = dot >= 0 ? serialized.FindProperty(field.propertyPath.Substring(0, dot)) : null;
                var kind = parent?.FindPropertyRelative("kind");
                // Magnitude fields are inactive for fixed/source-only values.
                if (parent?.FindPropertyRelative("captureAttributeId") != null && kind != null && kind.enumValueIndex != (int)MagnitudeKind.Parameter && kind.enumValueIndex != (int)MagnitudeKind.Calculation) continue;
                var declared = FindParameter(field,field.stringValue);
                var required = field.name switch { "booleanParameterId" => SkillParameterKind.Bool, "indexParameterId" => SkillParameterKind.Int, "scaleParameterId" => SkillParameterKind.Float, _ => (SkillParameterKind?)null };
                if (declared != null && required.HasValue && declared.kind != required)
                    context.Add(issues, Id, asset, field.propertyPath, "此处需要 " + required + " 参数。", track:"@field", elementId:field.propertyPath);
                if (declared == null) context.Add(issues, Id, asset, field.propertyPath, "引用的技能参数已删除，或不在当前参数集中。请重新绑定。", track:"@field", elementId:field.propertyPath);
            }
        }

        private static void ValidateParameters(GameplayAbilityAsset skill, SkillParameterOverride[] overrides = null)
        {
            int levels = 1;
            void Include(SkillParameterOverride[] values)
            {
                if (values != null) foreach (var value in values) levels = Math.Max(levels, value.levels?.Length ?? 0);
            }
            Include(skill.parameterOverrides); Include(overrides);
            for (int level = 1; level <= levels; level++) skill.ResolveParameters(level, overrides);
        }

        private static void ValidateSet(AbilitySetAsset set)
        {
            var skills = new HashSet<GameplayAbilityAsset>();
            foreach (var entry in set.abilities)
            {
                if (entry?.skill == null || !skills.Add(entry.skill)) throw new InvalidOperationException("技能套组包含缺失或重复技能。");
                if (entry.level < 1) throw new InvalidOperationException("技能授予等级必须大于零。");
                ValidateParameters(entry.skill, entry.parameters);
            }
        }
    }
}
