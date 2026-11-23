using System;
using System.Globalization;

namespace ZFramework.GAS
{
    public readonly struct ActiveGameplayEffectHandle : IEquatable<ActiveGameplayEffectHandle>
    {
        private readonly long _value;
        internal ActiveGameplayEffectHandle(long value) => _value = value;
        public bool IsValid => _value != 0;
        public bool Equals(ActiveGameplayEffectHandle other) => _value == other._value;
        public override bool Equals(object obj) => obj is ActiveGameplayEffectHandle other && Equals(other);
        public override int GetHashCode() => _value.GetHashCode();
        public override string ToString() => _value.ToString(CultureInfo.InvariantCulture);
        public static bool operator ==(ActiveGameplayEffectHandle left, ActiveGameplayEffectHandle right) => left.Equals(right);
        public static bool operator !=(ActiveGameplayEffectHandle left, ActiveGameplayEffectHandle right) => !left.Equals(right);
    }

    public readonly struct ActiveGameplayEffectInfo
    {
        public readonly ActiveGameplayEffectHandle Handle;
        public readonly GameplayEffectDefinition Definition;
        public readonly object Source;
        public readonly int StackCount;
        public readonly float RemainingTime, Duration, Period;

        internal ActiveGameplayEffectInfo(ActiveGameplayEffectHandle handle, GameplayEffectDefinition definition,
            object source, int stackCount, float remainingTime, float duration, float period)
        {
            Handle = handle; Definition = definition; Source = source; StackCount = stackCount;
            RemainingTime = remainingTime; Duration = duration; Period = period;
        }
    }

    internal sealed class ActiveGameplayEffect
    {
        internal ActiveGameplayEffectHandle Handle;
        internal GameplayEffectDefinition Definition;
        internal object Source;
        internal int StackCount, ModifierAttributeCount, GrantedTagCount;
        internal float ElapsedTime, PeriodTimer, ResolvedPeriod, ResolvedDuration;
        internal GameplayEffectSpec Spec;
        internal float[] ResolvedMagnitudes;
        internal GameplayAttribute[] ModifierAttributes;
        internal bool HasOngoingModifiers, PendingRemove, FullyDetached;

        internal float RemainingTime => Definition.DurationPolicy == EffectDurationPolicy.Duration
            ? Math.Max(0f, ResolvedDuration - ElapsedTime) : -1f;

        internal void Initialize(ActiveGameplayEffectHandle handle, GameplayEffectDefinition definition,
            object source, float resolvedPeriod, float resolvedDuration)
        {
            Handle = handle; Definition = definition; Source = source;
            ResolvedPeriod = resolvedPeriod; ResolvedDuration = resolvedDuration;
            StackCount = 1; ElapsedTime = PeriodTimer = 0f;
            ModifierAttributeCount = GrantedTagCount = 0;
            HasOngoingModifiers = PendingRemove = FullyDetached = false;
        }

        internal void PrepareForPool()
        {
            Handle = default; Definition = null; Source = null; Spec = default;
            HasOngoingModifiers = false;
            if (ModifierAttributes != null && ModifierAttributeCount > 0)
                Array.Clear(ModifierAttributes, 0, ModifierAttributeCount);
            ModifierAttributeCount = 0;
        }
    }
}
