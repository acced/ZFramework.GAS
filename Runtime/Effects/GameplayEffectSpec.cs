using System;
using System.Collections.Generic;

namespace ZFramework.GAS
{
    /// <summary>
    /// 效果**规格**：一次具体施加的意图 = 定义 + 来源 + 调用方提供的数值。
    /// 对应 UE 的 <c>FGameplayEffectSpec</c>。本身不含运行时状态，可按值传递。
    ///
    /// 是 struct 且 <see cref="SetByCallerValues"/> 允许为 null，因此不使用 SetByCaller 的
    /// 效果施加路径上不产生任何分配。
    /// </summary>
    public readonly struct GameplayEffectSpec
    {
        public readonly GameplayEffectDefinition Definition;

        /// <summary>来源的属性集，供 <see cref="MagnitudeCapture.Source"/> 取值；可为 null。</summary>
        public readonly AttributeSet SourceAttributes;

        /// <summary>来源标识，<see cref="EffectStacking.AggregateBySource"/> 按引用比对；可为 null。</summary>
        public readonly object Source;

        /// <summary>调用方按键提供的数值；可为 null。</summary>
        public readonly Dictionary<string, float> SetByCallerValues;
        public readonly EffectEvaluationContext Context;
        private readonly bool _callerFrozen;
        public int Level => Context?.Level ?? 1;

        public GameplayEffectSpec(
            GameplayEffectDefinition definition,
            AttributeSet sourceAttributes = null,
            object source = null,
            Dictionary<string, float> setByCallerValues = null,
            EffectEvaluationContext context = null)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            SourceAttributes = sourceAttributes;
            Source = source;
            SetByCallerValues = setByCallerValues;
            Context = context;
            _callerFrozen = setByCallerValues == null;
        }
        private GameplayEffectSpec(in GameplayEffectSpec source, Dictionary<string, float> values)
        {
            Definition = source.Definition; SourceAttributes = source.SourceAttributes; Source = source.Source;
            Context = source.Context; SetByCallerValues = values; _callerFrozen = true;
        }
        internal GameplayEffectSpec Snapshot() => _callerFrozen ? this : new GameplayEffectSpec(this,
            new Dictionary<string, float>(SetByCallerValues, SetByCallerValues.Comparer));
        private GameplayEffectSpec(in GameplayEffectSpec source, GameplayEffectDefinition definition)
        {
            Definition = definition; SourceAttributes = source.SourceAttributes; Source = source.Source;
            Context = source.Context; SetByCallerValues = source.SetByCallerValues; _callerFrozen = source._callerFrozen;
        }
        internal GameplayEffectSpec ForChild(GameplayEffectDefinition definition) => new GameplayEffectSpec(this, definition);
        /// <summary>Resolve the entire configured effect tree without publishing effects or changing attributes.
        /// Includes deferred children: a missing parameter is a configuration error even before its trigger fires.</summary>
        public void Validate(AttributeSet target) => Validate(target, 0);
        private void Validate(AttributeSet target, int depth)
        {
            if (depth > 8) throw new InvalidOperationException("Conditional effect chain exceeded depth 8; check for a cycle.");
            foreach (var modifier in Definition.Modifiers) Resolve(modifier.Magnitude, target);
            if (Definition.DurationPolicy != EffectDurationPolicy.Instant) { ResolveDuration(target); ResolvePeriod(target); }
            foreach (var child in Definition.ConditionalEffects) ForChild(child.Effect).Validate(target, depth + 1);
        }
        public float ResolvePeriod(AttributeSet target)
        {
            var d = Definition;
            return Math.Max(0, Finite(d.PeriodAttribute.HasValue ? SourceValue(d.PeriodAttribute.Value)
                : d.PeriodMagnitude.HasValue ? Resolve(d.PeriodMagnitude.Value, target) : d.Period, "period"));
        }
        public float ResolveDuration(AttributeSet target)
        {
            var d = Definition;
            if (d.DurationAttribute.HasValue)
            { float value = Finite(SourceValue(d.DurationAttribute.Value), "duration attribute"); if (value > 0) return value; }
            return Finite(d.DurationMagnitude.HasValue ? Resolve(d.DurationMagnitude.Value, target) : d.Duration, "duration");
        }
        internal static float Finite(float value, string name)
        { if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("Non-finite effect " + name); return value; }

        public float SourceValue(AttributeId id) => Context?.SourceValue(id, SourceAttributes) ?? SourceAttributes?.GetCurrent(id) ?? 0;
        public float Parameter(string id) => Context?.Parameter(id)
            ?? throw new InvalidOperationException($"Missing skill parameter '{id}'.");

        /// <summary>
        /// 解析数值。SetByCaller 键缺失时**抛出**而非返回 0——
        /// 静默回落会让配置错误伪装成「伤害为零」这类难以定位的现象。
        /// </summary>
        public float Resolve(in EffectMagnitude magnitude, AttributeSet targetAttributes)
            => Finite(ResolveValue(magnitude, targetAttributes), "magnitude");
        private float ResolveValue(in EffectMagnitude magnitude, AttributeSet targetAttributes)
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
                    return magnitude.Constant + magnitude.Coefficient * Parameter(magnitude.CallerKey);
                case MagnitudeKind.LevelTable:
                    return magnitude.Levels[Math.Min(Level, magnitude.Levels.Length) - 1];
                case MagnitudeKind.Calculation:
                    return magnitude.Calculation.Evaluate(this, targetAttributes);

                case MagnitudeKind.SetByCaller:
                    if (SetByCallerValues != null && SetByCallerValues.TryGetValue(magnitude.CallerKey, out float value))
                    {
                        return value;
                    }

                    throw new InvalidOperationException(
                        $"SetByCaller value '{magnitude.CallerKey}' was not supplied on this spec.");

                default:
                    throw new InvalidOperationException($"Unhandled magnitude kind {magnitude.Kind}.");
            }
        }
    }
}
