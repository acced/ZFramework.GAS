using System;

namespace ZFramework.GAS
{
    /// <summary>
    /// 能力的延迟动作：跨帧运行、由所属能力持有、能力结束时一并取消。
    /// 对应 UE 的 <c>UAbilityTask</c>。
    ///
    /// 库只提供生命周期，具体动作（投射物飞行、召唤生成、瞄准等待…）由使用方派生实现——
    /// 它们通常需要引擎能力，不属于纯规则层。
    ///
    /// ASC 调度的任务随能力结束；由 GameplayTaskHost 调度的独立任务由该宿主负责清理。
    /// Ability 在独立任务中只记录来源，不控制其寿命。
    /// </summary>
    public abstract class GameplayAbilityTask
    {
        public GameplayAbilitySpec Ability { get; private set; }
        public int ActivationVersion { get; private set; }
        internal long RunVersion { get; private set; }

        public bool IsActive { get; private set; }
        /// <summary>Optional authoring identity for failure diagnostics.</summary>
        public virtual string DiagnosticId => null;

        /// <summary>本次运行是否以取消告终（而非自行完成）。</summary>
        public bool WasCancelled { get; private set; }

        /// <summary>每帧推进，dt 由宿主经 <see cref="AbilitySystemComponent.Tick"/> 传入。</summary>
        protected abstract void OnTick(float deltaTime);
        protected virtual void OnBegin() { }

        /// <summary>结束时回调，<paramref name="cancelled"/> 区分「被取消」与「自行完成」。</summary>
        protected virtual void OnEnd(bool cancelled)
        {
        }

        /// <summary>由派生类在动作达成时调用（如投射物命中）。</summary>
        protected void Complete()
        {
            if (IsActive)
            {
                Finish(cancelled: false);
            }
        }

        internal void Begin(GameplayAbilitySpec ability, int activationVersion)
        {
            if (IsActive) throw new InvalidOperationException("Task is already running.");
            Ability = ability;
            RunVersion++;
            ActivationVersion = activationVersion;
            IsActive = true;
            WasCancelled = false;
            OnBegin();
        }

        internal void Advance(float deltaTime)
        {
            if (IsActive)
            {
                OnTick(deltaTime);
            }
        }

        internal void CancelInternal()
        {
            if (IsActive)
            {
                Finish(cancelled: true);
            }
        }

        private void Finish(bool cancelled)
        {
            IsActive = false;
            WasCancelled = cancelled;
            OnEnd(cancelled);
        }
    }
}
