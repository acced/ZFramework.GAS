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

        public GameplayEffectSpec(
            GameplayEffectDefinition definition,
            AttributeSet sourceAttributes = null,
            object source = null,
            Dictionary<string, float> setByCallerValues = null)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            SourceAttributes = sourceAttributes;
            Source = source;
            SetByCallerValues = setByCallerValues;
        }

        /// <summary>
        /// 解析数值。SetByCaller 键缺失时**抛出**而非返回 0——
        /// 静默回落会让配置错误伪装成「伤害为零」这类难以定位的现象。
        /// </summary>
        internal float Resolve(in EffectMagnitude magnitude, AttributeSet targetAttributes)
        {
            switch (magnitude.Kind)
            {
                case MagnitudeKind.Fixed:
                    return magnitude.Constant;

                case MagnitudeKind.AttributeScaled:
                {
                    AttributeSet set = magnitude.CaptureFrom == MagnitudeCapture.Source
                        ? SourceAttributes
                        : targetAttributes;
                    return magnitude.Constant + magnitude.Coefficient * (set?.GetCurrent(magnitude.CaptureAttribute) ?? 0f);
                }

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
