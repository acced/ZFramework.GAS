using System;

namespace ZFramework.GAS
{
    public readonly struct GameplayEffectSpec
    {
        public readonly GameplayEffectDefinition Definition;
        public readonly AttributeSet SourceAttributes;
        public readonly object Source;
        public readonly EffectEvaluationContext Context;
        public int Level => Context?.Level ?? 1;

        public GameplayEffectSpec(GameplayEffectDefinition definition, AttributeSet sourceAttributes = null,
            object source = null, EffectEvaluationContext context = null)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            SourceAttributes = sourceAttributes;
            Source = source is AbilitySystemComponent owner ? owner.SourceIdentity : source;
            Context = context;
        }

        internal GameplayEffectSpec ForChild(GameplayEffectDefinition definition)
            => new(definition, SourceAttributes, Source, Context);

        public float ResolvePeriod(AttributeSet target)
            => Definition.PeriodMagnitude.HasValue ? Resolve(Definition.PeriodMagnitude.Value, target) : Definition.Period;

        public float ResolveDuration(AttributeSet target)
            => Definition.DurationMagnitude.HasValue ? Resolve(Definition.DurationMagnitude.Value, target) : Definition.Duration;

        public float SourceValue(AttributeId id)
            => Context?.SourceValue(id, SourceAttributes) ?? SourceAttributes?.GetCurrent(id) ?? 0f;

        public float Parameter(string id)
            => Context?.Parameter(id) ?? throw new InvalidOperationException($"Missing skill parameter '{id}'.");

        public float Resolve(in EffectMagnitude magnitude, AttributeSet targetAttributes) => magnitude.Kind switch
        {
            MagnitudeKind.Fixed => magnitude.Constant,
            MagnitudeKind.AttributeScaled => magnitude.Constant + magnitude.Coefficient * (magnitude.CaptureFrom == MagnitudeCapture.Source
                ? SourceValue(magnitude.CaptureAttribute)
                : targetAttributes?.GetCurrent(magnitude.CaptureAttribute) ?? 0f),
            MagnitudeKind.Parameter => magnitude.Constant + magnitude.Coefficient * Parameter(magnitude.ParameterId),
            MagnitudeKind.LevelTable => magnitude.Levels[Math.Min(Level, magnitude.Levels.Length) - 1],
            MagnitudeKind.Calculation => magnitude.Calculation.Evaluate(this, targetAttributes),
            _ => throw new InvalidOperationException($"Unhandled magnitude kind {magnitude.Kind}."),
        };
    }
}
