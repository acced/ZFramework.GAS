using System;
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
            SkillParameterValue def = SkillParameterValue.FromNumber(kind, defaultValue);
            if (levels == null || levels.Length == 0) return def;
            for (int i = 0; i < levels.Length; i++) SkillParameterValue.FromNumber(kind, levels[i]);
            return SkillParameterValue.FromNumber(kind, levels[Mathf.Clamp(level - 1, 0, levels.Length - 1)]);
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
            => SkillParameterValue.FromNumber(kind, levels != null && levels.Length > 0 ? levels[Mathf.Clamp(level - 1, 0, levels.Length - 1)] : value);
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
