using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public enum SkillBlendCurve { Linear, SineIn, SineOut, SineInOut, QuadIn, QuadOut, QuadInOut, CubicIn, CubicOut, CubicInOut, ExpoIn, ExpoOut, ExpoInOut, [InspectorName("BackIn（权重限幅）")] BackIn, [InspectorName("BackOut（权重限幅）")] BackOut, [InspectorName("BackInOut（权重限幅）")] BackInOut, Custom }
    public enum SkillBlendMode { CrossFade, Freeze, Inertialize }
    [Flags] public enum AnimationBlendCapabilities { None = 0, Curve = 1, SeekBlend = 2, Freeze = 4, Inertialize = 8 }

    public sealed class BlendPresetAttribute : PropertyAttribute
    {
        public readonly string CustomField;
        public BlendPresetAttribute(string customField)=>CustomField=customField;
    }
    [Serializable]
    public struct SkillBlend
    {
        [Min(0)] public float duration;
        [BlendPreset(nameof(custom))] public SkillBlendCurve curve;
        [HideInInspector] public AnimationCurve custom;
        public SkillBlendMode mode;
        public SkillBlend(float seconds, SkillBlendCurve curve = default, SkillBlendMode mode = default, AnimationCurve custom = null)
        { duration = seconds; this.curve = curve; this.mode = mode; this.custom = custom; }

        internal SkillBlend Snapshot()
        {
            if (!(duration >= 0) || float.IsInfinity(duration)) throw new ArgumentOutOfRangeException(nameof(duration));
            if (curve == SkillBlendCurve.Custom && (custom == null || custom.length < 2))
                throw new ArgumentException("自定义过渡曲线至少需要两个键。");
            return new SkillBlend(duration, curve, mode, curve == SkillBlendCurve.Custom ? new AnimationCurve(custom.keys) : null);
        }

        public float Evaluate(float t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            float u = 1 - t;
            float v = curve switch
            {
                SkillBlendCurve.SineIn => 1 - Mathf.Cos(t * Mathf.PI / 2),
                SkillBlendCurve.SineOut => Mathf.Sin(t * Mathf.PI / 2),
                SkillBlendCurve.SineInOut => (1 - Mathf.Cos(Mathf.PI * t)) / 2,
                SkillBlendCurve.QuadIn => t*t, SkillBlendCurve.QuadOut => 1-u*u,
                SkillBlendCurve.QuadInOut => t < .5f ? 2*t*t : 1-2*u*u,
                SkillBlendCurve.CubicIn => t*t*t, SkillBlendCurve.CubicOut => 1-u*u*u,
                SkillBlendCurve.CubicInOut => t < .5f ? 4*t*t*t : 1-4*u*u*u,
                SkillBlendCurve.ExpoIn => Mathf.Pow(2, 10*t-10), SkillBlendCurve.ExpoOut => 1-Mathf.Pow(2,-10*t),
                SkillBlendCurve.ExpoInOut => t < .5f ? Mathf.Pow(2,20*t-10)/2 : 1-Mathf.Pow(2,10-20*t)/2,
                SkillBlendCurve.BackIn => Back(t), SkillBlendCurve.BackOut => 1-Back(u),
                SkillBlendCurve.BackInOut => t < .5f ? Back(2*t)/2 : 1-Back(2*u)/2,
                SkillBlendCurve.Custom when custom != null => custom.Evaluate(t),
                _ => t
            };
            return Mathf.Clamp01(v); // Playable layer weights must stay in [0,1].
        }
        private static float Back(float t) => t*t*(2.70158f*t-1.70158f);
    }

    public interface IAdvancedBlendingDriver : IBlendingAnimationDriver
    {
        AnimationBlendCapabilities Capabilities { get; }
        float Play(string clip, float duration, bool loop, in SkillBlend blendIn);
        void Stop(in SkillBlend blendOut);
        // The caller supplies transition duration in actual seconds.
        bool SeekBlend(float normalizedTime, in SkillBlend blend);
    }
    public readonly struct ClipWeight
    { public readonly string Name; public readonly float Weight; public ClipWeight(string name,float weight){Name=name;Weight=weight;} }
    public interface IAnimationDebugInfo { int CopyWeights(Span<ClipWeight> into); }
    public interface ISkillLayerDriver
    {
        void ConfigureNextLayer(AvatarMask mask, bool additive, float weight);
    }
}
