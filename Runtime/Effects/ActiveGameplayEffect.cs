using System;
using System.Globalization;

namespace ZFramework.GAS
{
    /// <summary>
    /// 持续效果的稳定外部身份。默认值无效；效果删除后旧 Handle 永远不会指向复用后的新效果。
    /// </summary>
    public readonly struct ActiveGameplayEffectHandle : IEquatable<ActiveGameplayEffectHandle>
    {
        private readonly long _value;

        internal ActiveGameplayEffectHandle(long value) => _value = value;

        /// <summary>
        /// 仅表示该值不是默认 Handle，不代表它当前仍对应存活效果。
        /// 存活性必须由所属容器的 <see cref="GameplayEffectContainer.TryGetInfo"/> 确认。
        /// </summary>
        public bool IsValid => _value != 0;

        public bool Equals(ActiveGameplayEffectHandle other) => _value == other._value;
        public override bool Equals(object obj) => obj is ActiveGameplayEffectHandle other && Equals(other);
        public override int GetHashCode() => _value.GetHashCode();
        public override string ToString() => _value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(ActiveGameplayEffectHandle left, ActiveGameplayEffectHandle right) => left.Equals(right);
        public static bool operator !=(ActiveGameplayEffectHandle left, ActiveGameplayEffectHandle right) => !left.Equals(right);
    }

    /// <summary>
    /// 持续效果的只读浅快照；不暴露容器内部可复用状态，但 Definition 与 Source 仍是对象引用。
    /// 长期缓存该值会延长这些对象的存活期，它不是完全无引用快照。
    /// </summary>
    public readonly struct ActiveGameplayEffectInfo
    {
        public readonly ActiveGameplayEffectHandle Handle;
        public readonly GameplayEffectDefinition Definition;
        /// <summary>施加来源的原始引用；缓存该 Info 会相应保活 Source。</summary>
        public readonly object Source;
        public readonly int StackCount;
        public readonly float RemainingTime;

        internal ActiveGameplayEffectInfo(
            ActiveGameplayEffectHandle handle,
            GameplayEffectDefinition definition,
            object source,
            int stackCount,
            float remainingTime)
        {
            Handle = handle;
            Definition = definition;
            Source = source;
            StackCount = stackCount;
            RemainingTime = remainingTime;
        }
    }

    /// <summary>
    /// 容器内部的持续效果状态。对象会被复用，绝不能作为外部身份暴露。
    /// </summary>
    internal sealed class ActiveGameplayEffect
    {
        internal ActiveGameplayEffectHandle Handle;
        internal GameplayEffectDefinition Definition;
        internal object Source;
        internal int StackCount;
        internal float ElapsedTime;
        internal float PeriodTimer;
        internal float ResolvedPeriod;
        internal float ResolvedDuration;
        internal AttributeSet SourceAttributes;
        internal float[] ResolvedMagnitudes;
        internal GameplayAttribute[] ModifierAttributes;
        internal int ModifierAttributeCount;
        internal bool PendingRemove;
        internal bool FullyDetached;

        internal void Initialize(
            ActiveGameplayEffectHandle handle,
            GameplayEffectDefinition definition,
            object source,
            AttributeSet sourceAttributes,
            float resolvedPeriod,
            float resolvedDuration)
        {
            Handle = handle;
            Definition = definition;
            Source = source;
            SourceAttributes = sourceAttributes;
            ResolvedPeriod = resolvedPeriod;
            ResolvedDuration = resolvedDuration;
            StackCount = 1;
            ElapsedTime = 0f;
            PeriodTimer = 0f;
            ModifierAttributeCount = 0;
            PendingRemove = false;
            FullyDetached = false;
        }

        internal float RemainingTime => Definition.DurationPolicy == EffectDurationPolicy.Duration
            ? Math.Max(0f, ResolvedDuration - ElapsedTime)
            : -1f;

        internal void Discard()
        {
            // 对象身份本身必须保留：遗留 ActiveModifier.Source 仍可能引用 this。
            // 其余状态全部释放，且调用方不再持有本对象；最后一个 Modifier 脱钩后即可由 GC 回收。
            Handle = default;
            Definition = null;
            Source = null;
            SourceAttributes = null;
            StackCount = 0;
            ElapsedTime = 0f;
            PeriodTimer = 0f;
            ResolvedPeriod = 0f;
            ResolvedDuration = 0f;
            PendingRemove = false;
            FullyDetached = false;
            ResolvedMagnitudes = null;
            ModifierAttributes = null;
            ModifierAttributeCount = 0;
        }

        internal void PrepareForPool()
        {
            // 租出时会覆盖所有值字段和缓冲区有效区间；这里只释放可能保活对象图的强引用。
            Handle = default;
            Definition = null;
            Source = null;
            SourceAttributes = null;
            if (ModifierAttributes != null && ModifierAttributeCount > 0)
            {
                Array.Clear(ModifierAttributes, 0, ModifierAttributeCount);
            }

            ModifierAttributeCount = 0;
        }
    }
}
