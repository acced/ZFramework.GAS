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
    /// **生命周期约定**：任务的存活期是所属能力存活期的子集。
    /// 投射物这类「发射后仍在飞行」的动作，应当让能力保持激活直到命中，
    /// 而不是先结束能力再指望任务活下去——那会留下无主的跨帧状态。
    /// </summary>
    public abstract class GameplayAbilityTask
    {
        public GameplayAbilitySpec Ability { get; private set; }

        public bool IsActive { get; private set; }

        /// <summary>本次运行是否以取消告终（而非自行完成）。</summary>
        public bool WasCancelled { get; private set; }

        /// <summary>每帧推进，dt 由宿主经 <see cref="AbilitySystemComponent.Tick"/> 传入。</summary>
        protected abstract void OnTick(float deltaTime);

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

        internal void Begin(GameplayAbilitySpec ability)
        {
            Ability = ability;
            IsActive = true;
            WasCancelled = false;
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
