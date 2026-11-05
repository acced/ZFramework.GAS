using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [CreateAssetMenu(menuName = "ZFramework GAS/Skill Parameter Set")]
    public sealed class SkillParameterSetAsset : ScriptableObject
    {
        private void OnValidate() => DefinitionSource.InvalidateDependents(this);
        public SkillParameter[] parameters = Array.Empty<SkillParameter>();
        internal static void AddTo(Dictionary<string, SkillParameterValue> values, SkillParameter[] parameters, int level)
        {
            foreach (var parameter in parameters)
            {
                if (parameter == null || string.IsNullOrEmpty(parameter.Id)) throw new InvalidOperationException("技能参数缺少稳定 ID");
                if (!values.TryAdd(parameter.Id, parameter.Resolve(level))) throw new InvalidOperationException("重复参数 ID: " + parameter.Id);
            }
        }
        internal static void Apply(Dictionary<string, SkillParameterValue> values, SkillParameterOverride[] overrides, int level)
        {
            if (overrides == null) return;
            foreach (var entry in overrides)
            {
                if (entry.parameterId == null || !values.TryGetValue(entry.parameterId, out var original))
                    throw new InvalidOperationException($"引用了不存在的参数 {entry.parameterId}");
                var value = entry.Resolve(level);
                if (value.Kind != original.Kind) throw new InvalidOperationException($"参数类型不匹配 {entry.parameterId}");
                values[entry.parameterId] = value;
            }
        }
    }
}

