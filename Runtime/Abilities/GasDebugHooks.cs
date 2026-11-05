using System;

namespace ZFramework.GAS
{
    public static class GasDebugHooks
    {
        public delegate void EffectHook(AbilitySystemComponent target, GameplayEffectDefinition effect, object source, int stacks);
        public static event Action<GameplayCueParameters> Cue;
        internal static void NotifyCue(GameplayCueParameters p) => Cue?.Invoke(p);
        public static event EffectHook Applied;
        internal static void Apply(object owner, GameplayEffectDefinition effect, object source, int count) => Applied?.Invoke(owner as AbilitySystemComponent, effect, source, count);
        public static event EffectHook Removed;
        internal static void Remove(object owner, GameplayEffectDefinition effect, object source, int count) => Removed?.Invoke(owner as AbilitySystemComponent, effect, source, count);
        public static event EffectHook Stacked;
        internal static void Stack(object owner, GameplayEffectDefinition effect, object source, int count) => Stacked?.Invoke(owner as AbilitySystemComponent, effect, source, count);
        public static event EffectHook PeriodExecuted;
        internal static void Period(object owner, GameplayEffectDefinition effect, object source, int count) => PeriodExecuted?.Invoke(owner as AbilitySystemComponent, effect, source, count);
        public static event EffectHook Refreshed;
        internal static void Refresh(object owner, GameplayEffectDefinition effect, object source, int count) => Refreshed?.Invoke(owner as AbilitySystemComponent, effect, source, count);
    }
}
