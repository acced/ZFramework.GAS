using System;

namespace ZFramework.GAS.Unity
{
    public static class SkillActionDebug
    {
        private static readonly DebugSubscribers<Action<SkillExecutionContext, SkillActionDefinition>> _Boundary = new();
        public static event Action<SkillExecutionContext, SkillActionDefinition> Boundary
        { add => _Boundary.Add(value); remove => _Boundary.Remove(value); }
        internal static void Begin(SkillExecutionContext context, SkillActionDefinition action)
        {
            foreach (var callback in _Boundary.Snapshot)
                try { callback(context, action); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
        private static readonly DebugSubscribers<Action<SkillExecutionContext, SkillActionDefinition, Exception>> _ActionFinished = new();
        public static event Action<SkillExecutionContext, SkillActionDefinition, Exception> ActionFinished
        { add => _ActionFinished.Add(value); remove => _ActionFinished.Remove(value); }
        internal static void End(SkillExecutionContext context, SkillActionDefinition action, Exception error)
        {
            foreach (var callback in _ActionFinished.Snapshot)
                try { callback(context, action, error); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
        private static readonly DebugSubscribers<Action<SkillExecutionContext, AnimationMarker>> _MarkerReached = new();
        public static event Action<SkillExecutionContext, AnimationMarker> MarkerReached
        { add => _MarkerReached.Add(value); remove => _MarkerReached.Remove(value); }
        internal static void Marker(SkillExecutionContext context, AnimationMarker m)
        {
            foreach (var callback in _MarkerReached.Snapshot)
                try { callback(context, m); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
        private static readonly DebugSubscribers<Action<SkillExecutionContext, string, bool>> _BranchSelected = new();
        public static event Action<SkillExecutionContext, string, bool> BranchSelected
        { add => _BranchSelected.Add(value); remove => _BranchSelected.Remove(value); }
        internal static void Branch(SkillExecutionContext context, string id, bool taken)
        {
            foreach (var callback in _BranchSelected.Snapshot)
                try { callback(context, id, taken); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
        private static readonly DebugSubscribers<Action<SkillExecutionContext, TargetQueryBuffer>> _TargetsSelected = new();
        public static event Action<SkillExecutionContext, TargetQueryBuffer> TargetsSelected
        { add => _TargetsSelected.Add(value); remove => _TargetsSelected.Remove(value); }
        internal static void Query(SkillExecutionContext context, TargetQueryBuffer buffer)
        {
            foreach (var callback in _TargetsSelected.Snapshot)
                try { callback(context, buffer); } catch (Exception ex) { DebugObserverErrors.Record(ex); }
        }
    }
}
