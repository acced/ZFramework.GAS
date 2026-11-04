using System;

namespace ZFramework.GAS
{
    /// <summary>Observer failures are retained separately from gameplay diagnostics.</summary>
    public static class DebugObserverErrors
    {
        public static Exception Last { get; private set; }
        public static long Count { get; private set; }
        public static void Record(Exception error) { Last = error; Count++; }
    }

    // Subscription changes are rare; publishing reads an immutable typed array, including on reentry.
    public sealed class DebugSubscribers<T> where T : Delegate
    {
        private T _combined;
        private T[] _callbacks = Array.Empty<T>();
        public ReadOnlySpan<T> Snapshot => _callbacks;
        public void Add(T value) => Update((T)Delegate.Combine(_combined,value));
        public void Remove(T value) => Update((T)Delegate.Remove(_combined,value));
        private void Update(T value)
        {
            _combined=value;
            _callbacks=value==null?Array.Empty<T>():Array.ConvertAll(value.GetInvocationList(), d=>(T)d);
        }
    }
    public static class GasDebugHooks
    {
        public delegate void EffectHook(AbilitySystemComponent target, GameplayEffectDefinition effect, object source, int stacks);
        private static readonly DebugSubscribers<Action<GameplayCueParameters>> _Cue = new();
        public static event Action<GameplayCueParameters> Cue
        { add => _Cue.Add(value); remove => _Cue.Remove(value); }
        internal static void NotifyCue(GameplayCueParameters p)
        {
            foreach (var callback in _Cue.Snapshot)
                try { callback(p); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
        private static readonly DebugSubscribers<EffectHook> _Applied = new();
        public static event EffectHook Applied
        { add => _Applied.Add(value); remove => _Applied.Remove(value); }
        internal static void Apply(object owner, GameplayEffectDefinition effect, object source, int count)
        {
            foreach (var callback in _Applied.Snapshot)
                try { callback(owner as AbilitySystemComponent, effect, source, count); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
        private static readonly DebugSubscribers<EffectHook> _Removed = new();
        public static event EffectHook Removed
        { add => _Removed.Add(value); remove => _Removed.Remove(value); }
        internal static void Remove(object owner, GameplayEffectDefinition effect, object source, int count)
        {
            foreach (var callback in _Removed.Snapshot)
                try { callback(owner as AbilitySystemComponent, effect, source, count); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
        private static readonly DebugSubscribers<EffectHook> _Stacked = new();
        public static event EffectHook Stacked
        { add => _Stacked.Add(value); remove => _Stacked.Remove(value); }
        internal static void Stack(object owner, GameplayEffectDefinition effect, object source, int count)
        {
            foreach (var callback in _Stacked.Snapshot)
                try { callback(owner as AbilitySystemComponent, effect, source, count); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
        private static readonly DebugSubscribers<EffectHook> _PeriodExecuted = new();
        public static event EffectHook PeriodExecuted
        { add => _PeriodExecuted.Add(value); remove => _PeriodExecuted.Remove(value); }
        internal static void Period(object owner, GameplayEffectDefinition effect, object source, int count)
        {
            foreach (var callback in _PeriodExecuted.Snapshot)
                try { callback(owner as AbilitySystemComponent, effect, source, count); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
        private static readonly DebugSubscribers<EffectHook> _Refreshed = new();
        public static event EffectHook Refreshed
        { add => _Refreshed.Add(value); remove => _Refreshed.Remove(value); }
        internal static void Refresh(object owner, GameplayEffectDefinition effect, object source, int count)
        {
            foreach (var callback in _Refreshed.Snapshot)
                try { callback(owner as AbilitySystemComponent, effect, source, count); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
        private static readonly DebugSubscribers<Action<AbilitySystemComponent, AbilityDiagnostic>> _DiagnosticRecorded = new();
        public static event Action<AbilitySystemComponent, AbilityDiagnostic> DiagnosticRecorded
        { add => _DiagnosticRecorded.Add(value); remove => _DiagnosticRecorded.Remove(value); }
        internal static void Diagnostic(AbilitySystemComponent owner, AbilityDiagnostic value)
        {
            foreach (var callback in _DiagnosticRecorded.Snapshot)
                try { callback(owner, value); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
    }
}
