using System;

namespace ZFramework.GAS
{
    /// <summary>
    /// 效果**规格**：一次具体施加的意图 = 定义 + 来源 + 调用方提供的数值。
    /// 对应 UE 的 <c>FGameplayEffectSpec</c>。本身不含运行时状态，可按值传递。
    /// 参数由只读的 <see cref="EffectEvaluationContext"/> 提供，效果与子效果共享同一份上下文。
    /// </summary>
    public readonly struct GameplayEffectSpec
    {
        public readonly GameplayEffectDefinition Definition;

        /// <summary>来源的属性集，供 <see cref="MagnitudeCapture.Source"/> 取值；可为 null。</summary>
        public readonly AttributeSet SourceAttributes;

        /// <summary>来源标识，<see cref="EffectStacking.AggregateBySource"/> 按引用比对；可为 null。</summary>
        public readonly object Source;

        public readonly EffectEvaluationContext Context;
        public int Level => Context?.Level ?? 1;

        public GameplayEffectSpec(
            GameplayEffectDefinition definition,
            AttributeSet sourceAttributes = null,
            object source = null,
            EffectEvaluationContext context = null)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            SourceAttributes = sourceAttributes;
            Source = source is AbilitySystemComponent owner ? owner.SourceIdentity : source;
            Context = context;
        }
        internal GameplayEffectSpec ForChild(GameplayEffectDefinition definition)
            => new GameplayEffectSpec(definition, SourceAttributes, Source, Context);
        public float ResolvePeriod(AttributeSet target)
        {
            var d = Definition;
            return d.PeriodMagnitude.HasValue ? Resolve(d.PeriodMagnitude.Value, target) : d.Period;
        }
        public float ResolveDuration(AttributeSet target)
        {
            var d = Definition;
            return d.DurationMagnitude.HasValue ? Resolve(d.DurationMagnitude.Value, target) : d.Duration;
        }

        public float SourceValue(AttributeId id) => Context?.SourceValue(id, SourceAttributes) ?? SourceAttributes?.GetCurrent(id) ?? 0;
        public float Parameter(string id) => Context?.Parameter(id)
            ?? throw new InvalidOperationException($"Missing skill parameter '{id}'.");

        /// <summary>
        /// 解析数值。参数 ID 缺失时抛出，避免把配置错误解释为零伤害。
        /// </summary>
        public float Resolve(in EffectMagnitude magnitude, AttributeSet targetAttributes)
        {
            switch (magnitude.Kind)
            {
                case MagnitudeKind.Fixed:
                    return magnitude.Constant;

                case MagnitudeKind.AttributeScaled:
                {
                    float captured = magnitude.CaptureFrom == MagnitudeCapture.Source
                        ? SourceValue(magnitude.CaptureAttribute) : targetAttributes?.GetCurrent(magnitude.CaptureAttribute) ?? 0;
                    return magnitude.Constant + magnitude.Coefficient * captured;
                }

                case MagnitudeKind.Parameter:
                    return magnitude.Constant + magnitude.Coefficient * Parameter(magnitude.ParameterId);
                case MagnitudeKind.LevelTable:
                    return magnitude.Levels[Math.Min(Level, magnitude.Levels.Length) - 1];
                case MagnitudeKind.Calculation:
                    return magnitude.Calculation.Evaluate(this, targetAttributes);

                default:
                    throw new InvalidOperationException($"Unhandled magnitude kind {magnitude.Kind}.");
            }
        }
    }
}
