using System;
using System.Collections.Generic;
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
            try
            {
                if (asset is GameplayAbilityAsset skill) { skill.ResolveParameters(1); skill.BuildActions(); }
                else if (asset is SkillAnimationAsset animation) { _ = animation.Definition; }
                else if (asset is SkillActionModuleAsset module) module.Build();
            }
            catch (Exception error)
            {
                string path = "", actionId = (error as SkillActionException)?.ActionId;
                using var so = new SerializedObject(asset); var p = so.GetIterator();
                while (!string.IsNullOrEmpty(actionId) && p.Next(true))
                    if (p.name == "id" && p.propertyType == SerializedPropertyType.String && p.stringValue == actionId)
                    { path = p.propertyPath.Substring(0, p.propertyPath.LastIndexOf('.')); break; }
                context.Add(issues, Id, asset, path, error.Message);
            }
            var known = new HashSet<string>();
            foreach (var p in SkillNumberDrawer.Parameters(asset is SkillActionModuleAsset ? asset : context.Document.Root)) if (p != null) known.Add(p.Id);
            using var serialized = new SerializedObject(asset); var field = serialized.GetIterator();
            while (field.Next(true))
            {
                if (field.propertyType != SerializedPropertyType.String || string.IsNullOrEmpty(field.stringValue)) continue;
                if (field.name != "parameterId" && field.name != "booleanParameterId" && field.name != "callerParameterId") continue;
                int dot = field.propertyPath.LastIndexOf('.');
                var parent = dot >= 0 ? serialized.FindProperty(field.propertyPath.Substring(0, dot)) : null;
                var kind = parent?.FindPropertyRelative("kind");
                // Magnitude fields are inactive for fixed/source-only values.
                if (parent?.FindPropertyRelative("captureAttributeId") != null && kind != null && kind.enumValueIndex != (int)MagnitudeKind.Parameter && kind.enumValueIndex != (int)MagnitudeKind.Calculation) continue;
                if (!known.Contains(field.stringValue)) context.Add(issues, Id, asset, field.propertyPath, "引用的技能参数已删除，或不在当前参数集中。请重新绑定。");
            }
        }
    }
}
