namespace ZFramework.GAS
{
    /// <summary>跨帧能力任务：ASC 调度的任务随能力结束，GameplayTaskHost 调度的任务由宿主清理。</summary>
    public abstract class GameplayAbilityTask
    {
        public GameplayAbilitySpec Ability { get; private set; }
        public int ActivationVersion { get; private set; }
        internal long RunVersion { get; private set; }
        public bool IsActive { get; private set; }
        public bool WasCancelled { get; private set; }
        public virtual string DiagnosticId => null;

        protected abstract void OnTick(float deltaTime);
        protected virtual void OnBegin() { }
        protected virtual void OnEnd(bool cancelled) { }
        protected void Complete() { if (IsActive) Finish(false); }

        internal void Begin(GameplayAbilitySpec ability, int activationVersion)
        {
            Ability = ability; RunVersion++; ActivationVersion = activationVersion;
            IsActive = true; WasCancelled = false;
            OnBegin();
        }

        internal void Advance(float deltaTime) { if (IsActive) OnTick(deltaTime); }
        internal void CancelInternal() { if (IsActive) Finish(true); }

        private void Finish(bool cancelled)
        {
            IsActive = false; WasCancelled = cancelled; OnEnd(cancelled);
        }
    }
}
