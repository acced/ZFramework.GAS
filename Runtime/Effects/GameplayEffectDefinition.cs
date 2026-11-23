using GameplayTags;
using System;

namespace ZFramework.GAS
{
    public enum EffectDurationPolicy { Instant, Duration, Infinite }
    public enum EffectStacking { None, AggregateByTarget, AggregateBySource }
    public enum StackOverflowPolicy { Deny, RemoveExisting }
    public enum StackDurationRefreshPolicy { RefreshOnApply = 0, NeverRefresh = 1 }
    public enum StackPeriodResetPolicy { KeepProgress = 0, ResetOnApply = 1 }
    public enum MagnitudeKind { Fixed, AttributeScaled, Parameter = 3, LevelTable, Calculation }
    public enum MagnitudeCapture { Source, Target }
    public enum EffectTrigger { Applied, Periodic, Removed }

    /// <summary>数值来源：编辑期配置为结构，运行期只做算术。</summary>
    public readonly struct EffectMagnitude
    {
        public readonly MagnitudeKind Kind;
        public readonly float Constant, Coefficient;
        public readonly AttributeId CaptureAttribute;
        public readonly MagnitudeCapture CaptureFrom;
        public readonly string ParameterId;
        internal readonly float[] Levels;
        internal readonly IEffectCalculation Calculation;

        private EffectMagnitude(MagnitudeKind kind, float constant, float coefficient,
            AttributeId captureAttribute, MagnitudeCapture captureFrom, string parameterId,
            float[] levels = null, IEffectCalculation calculation = null)
        {
            if (!float.IsFinite(constant) || !float.IsFinite(coefficient))
                throw new ArgumentException("Effect magnitude must be finite.");
            if (levels != null)
                for (int i = 0; i < levels.Length; i++)
                    if (!float.IsFinite(levels[i])) throw new ArgumentException("Effect level table must be finite.");

            Kind = kind; Constant = constant; Coefficient = coefficient;
            CaptureAttribute = captureAttribute; CaptureFrom = captureFrom;
            ParameterId = parameterId; Levels = levels; Calculation = calculation;
        }

        public static EffectMagnitude Fixed(float value) => new(MagnitudeKind.Fixed, value, 0f, default, default, null);
        public static EffectMagnitude FromAttribute(AttributeId attribute, float coefficient, MagnitudeCapture captureFrom, float constant = 0f)
            => new(MagnitudeKind.AttributeScaled, constant, coefficient, attribute, captureFrom, null);
        public static EffectMagnitude FromParameter(string id, float coefficient = 1f, float constant = 0f)
            => new(MagnitudeKind.Parameter, constant, coefficient, default, default, id ?? throw new ArgumentNullException(nameof(id)));
        public static EffectMagnitude ByLevel(params float[] values)
            => new(MagnitudeKind.LevelTable, 0f, 0f, default, default, null,
                values != null && values.Length > 0 ? (float[])values.Clone() : throw new ArgumentException("Empty level table."));
        public static EffectMagnitude FromCalculation(IEffectCalculation calculation)
            => new(MagnitudeKind.Calculation, 0f, 0f, default, default, null, null,
                calculation ?? throw new ArgumentNullException(nameof(calculation)));
    }

    public readonly struct EffectModifier
    {
        public readonly AttributeId Target;
        public readonly ModifierOperation Operation;
        public readonly EffectMagnitude Magnitude;

        public EffectModifier(AttributeId target, ModifierOperation operation, in EffectMagnitude magnitude)
        {
            Target = target; Operation = operation; Magnitude = magnitude;
        }
    }

    public readonly struct EffectTags
    {
        public readonly GameplayTagSet Granted, ApplicationRequired, ApplicationBlocked, OngoingBlocked, RemoveEffectsWith, OngoingRequired;

        public EffectTags(
            GameplayTagSet granted = default,
            GameplayTagSet applicationRequired = default,
            GameplayTagSet applicationBlocked = default,
            GameplayTagSet ongoingBlocked = default,
            GameplayTagSet removeEffectsWith = default,
            GameplayTagSet ongoingRequired = default)
        {
            Granted = granted; ApplicationRequired = applicationRequired; ApplicationBlocked = applicationBlocked;
            OngoingBlocked = ongoingBlocked; RemoveEffectsWith = removeEffectsWith; OngoingRequired = ongoingRequired;
        }
    }

    public readonly struct EffectStackingRule
    {
        public readonly EffectStacking Mode;
        public readonly int Limit;
        public readonly StackOverflowPolicy Overflow;
        public readonly StackDurationRefreshPolicy DurationRefresh;
        public readonly StackPeriodResetPolicy PeriodReset;

        public EffectStackingRule(EffectStacking mode, int limit = 0,
            StackOverflowPolicy overflow = StackOverflowPolicy.Deny,
            StackDurationRefreshPolicy durationRefresh = StackDurationRefreshPolicy.RefreshOnApply,
            StackPeriodResetPolicy periodReset = StackPeriodResetPolicy.KeepProgress)
        {
            Mode = mode; Limit = limit; Overflow = overflow; DurationRefresh = durationRefresh; PeriodReset = periodReset;
        }
    }

    public readonly struct ConditionalEffect
    {
        public readonly GameplayEffectDefinition Effect;
        public readonly EffectTrigger Trigger;

        public ConditionalEffect(GameplayEffectDefinition effect, EffectTrigger trigger)
        {
            Effect = effect ?? throw new ArgumentNullException(nameof(effect)); Trigger = trigger;
        }
    }

    /// <summary>不可变效果定义；堆叠身份按定义引用判定。</summary>
    public sealed class GameplayEffectDefinition
    {
        private readonly EffectModifier[] _modifiers;
        private readonly ConditionalEffect[] _conditionalEffects;
        private readonly GameplayTagRequirements _applicationRequirements;
        private readonly GameplayTagRequirements _ongoingRequirements;

        public EffectDurationPolicy DurationPolicy { get; }
        public float Duration { get; }
        public EffectMagnitude? DurationMagnitude { get; }
        public EffectMagnitude? PeriodMagnitude { get; }
        public float Period { get; }
        public bool ExecutePeriodicOnApplication { get; }
        public ReadOnlySpan<EffectModifier> Modifiers => _modifiers;
        public EffectTags Tags { get; }
        public ref readonly GameplayTagRequirements ApplicationRequirements => ref _applicationRequirements;
        public ref readonly GameplayTagRequirements OngoingRequirements => ref _ongoingRequirements;
        public EffectStackingRule Stacking { get; }
        public GameplayTagSet CueTags { get; }
        public ReadOnlySpan<ConditionalEffect> ConditionalEffects => _conditionalEffects;

        public GameplayEffectDefinition(
            EffectDurationPolicy durationPolicy,
            float duration = 0f,
            ReadOnlySpan<EffectModifier> modifiers = default,
            EffectTags tags = default,
            EffectStackingRule stacking = default,
            float period = 0f,
            bool executePeriodicOnApplication = false,
            GameplayTagSet cueTags = default,
            ReadOnlySpan<ConditionalEffect> conditionalEffects = default,
            EffectMagnitude? durationMagnitude = null,
            EffectMagnitude? periodMagnitude = null)
        {
            if (!float.IsFinite(duration) || duration < 0f || !float.IsFinite(period) || period < 0f)
                throw new ArgumentOutOfRangeException(nameof(duration), "Effect duration/period must be finite and nonnegative.");

            DurationPolicy = durationPolicy; Duration = duration;
            DurationMagnitude = durationMagnitude; PeriodMagnitude = periodMagnitude;
            Period = period; ExecutePeriodicOnApplication = executePeriodicOnApplication;
            _modifiers = modifiers.IsEmpty ? Array.Empty<EffectModifier>() : modifiers.ToArray();
            Tags = tags;
            _applicationRequirements = GameplayTagRequirements.Create(tags.ApplicationBlocked.AsSpan(), tags.ApplicationRequired.AsSpan());
            _ongoingRequirements = GameplayTagRequirements.Create(tags.OngoingBlocked.AsSpan(), tags.OngoingRequired.AsSpan());
            Stacking = stacking; CueTags = cueTags;
            _conditionalEffects = conditionalEffects.IsEmpty ? Array.Empty<ConditionalEffect>() : conditionalEffects.ToArray();
        }
    }
}
