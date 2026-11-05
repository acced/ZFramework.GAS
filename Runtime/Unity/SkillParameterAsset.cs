using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public sealed class SkillParameterReferenceAttribute : PropertyAttribute { }
    [Serializable]
    public sealed class SkillParameter
    {
        [SerializeField, HideInInspector] private string id = Guid.NewGuid().ToString("N");
        public string Id => id;
        public string displayName;
        [TextArea] public string description;
        public SkillParameterKind kind;
        public float defaultValue;
        public float[] levels = Array.Empty<float>();
        public SkillParameterValue Resolve(int level)
        {
            SkillParameterValue.FromNumber(kind, defaultValue);
            if (levels != null) foreach (var v in levels) SkillParameterValue.FromNumber(kind, v);
            float value = levels != null && levels.Length > 0 ? levels[Mathf.Clamp(level - 1, 0, levels.Length - 1)] : defaultValue;
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException($"参数 {displayName} 的值不是有限数。");
            return SkillParameterValue.FromNumber(kind, value);
        }
    }

    [Serializable]
    public struct SkillParameterOverride
    {
        [SkillParameterReference] public string parameterId;
        public SkillParameterKind kind;
        public float value;
        public float[] levels;
        public SkillParameterValue Resolve(int level)
        {
            float number = levels != null && levels.Length > 0 ? levels[Mathf.Clamp(level - 1, 0, levels.Length - 1)] : value;
            if (!float.IsFinite(number)) throw new InvalidOperationException("参数覆盖必须为有限数: " + parameterId);
            return SkillParameterValue.FromNumber(kind, number);
        }
    }

    [Serializable]
    public struct SkillNumber
    {
        public float value;
        public string parameterId;
        public float coefficient;
        public SkillNumber(float value) { this.value = value; parameterId = null; coefficient = 1; }
        public float Resolve(EffectEvaluationContext context)
        {
            float number = string.IsNullOrEmpty(parameterId) ? value : context.Parameter(parameterId) * coefficient;
            GasAssert.That(float.IsFinite(number), "无效数值参数。");
            return number;
        }
    }


}
