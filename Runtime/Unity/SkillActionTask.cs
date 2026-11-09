using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public enum SkillTaskOwnership { [InspectorName("打断后继续（场景）")] Scene, [InspectorName("随施放取消（技能）")] Ability }
    public enum SkillSpawnPosition { Caster, Target, TargetPoint, HitPoint, EventPosition, LaunchPoint }

    public abstract class SkillActionTask : GameplayAbilityTask, ISkillTaskState
    {
        public SkillExecutionContext Execution { get; }
        public string ActionId { get; }
        public abstract string State { get; }
        protected SkillActionTask(SkillExecutionContext context, string id) { Execution = context.ForTask(); ActionId = id; }
        internal void Detach() => Execution.DetachTask();
        protected sealed override void OnBegin()
        {
            Execution.Retain(); // Until OnEnd.
            Execution.Retain(); // BeginAction may complete and immediately recast.
            try { BeginAction(); } finally { Execution.Release(); }
        }
        protected virtual void BeginAction() { }
        protected sealed override void OnTick(float deltaTime)
        {
            Execution.Retain(); // Complete() may precede the last callback in this tick.
            try { Execution.RefreshAsyncTargets(); Tick(deltaTime); } finally { Execution.Release(); }
        }
        protected abstract void Tick(float deltaTime);
        protected sealed override void OnEnd(bool cancelled)
        { try { EndAction(cancelled); } finally { Execution.Release(); } }
        protected virtual void EndAction(bool cancelled) { }
    }

}
