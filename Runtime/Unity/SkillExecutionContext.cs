using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>Per-cast state. Never stored in authoring assets or shared definitions.</summary>
    public sealed class SkillExecutionContext
    {
        // Shared across every branch and task from one cast, including work started during cancellation.
        private sealed class CastState
        {
            public readonly GameplayAbilityAsset Asset;
            public readonly GameplayAbilitySpec Ability;
            public readonly AbilitySourceIdentity Source;
            public TargetSearcher Searcher;
            public readonly Stack<TargetQueryBuffer> Buffers = new();
            public int Version, Users;
            public CastState(GameplayAbilityAsset asset, GameplayAbilitySpec ability, TargetSearcher searcher)
            {
                Asset = asset; Ability = ability; Source = ability.Owner.SourceIdentity;
                Version = ability.ActivationVersion;
                Searcher = searcher ?? DefaultSearcher;
            }
        }
        // Value copy: selecting a target or receiving an event never changes a sibling's branch.
        private struct Branch
        {
            public AbilitySourceIdentity Target;
            public Vector3 Position, Forward;
            public GameplayEvent? Event;
        }
        private readonly CastState _state;
        private Branch _branch;
        private bool _detached;
        internal bool Reusable => _state.Users == 0;
        /// <summary>任务或扩展持有本次施放的上下文期间调用，阻止下一次施放复用它；结束时调用 <see cref="Release"/>。</summary>
        public void Retain() => _state.Users++;
        public void Release() => _state.Users--;
        public Vector3 OriginAt(SkillOrigin origin){
            return origin switch{
                SkillOrigin.Caster=>Position,
                SkillOrigin.Target=>Target!=null && Services.Target!=null?Services.Target.position:throw new InvalidOperationException("缺少当前目标。"),
                SkillOrigin.TargetPoint=>Services.RequireTargetPosition(),
                SkillOrigin.EventPosition=>Event?.Position is { } p && float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)?new Vector3(p.X,p.Y,p.Z):throw new InvalidOperationException("事件没有有效位置。"),
                _=>throw new InvalidOperationException("未知原点。")
            };
        }
        public GameplayAbilityAsset Asset => _state.Asset;
        public GameplayAbilitySpec Ability => _state.Ability;
        public AbilitySystemComponent Source => _state.Source.Owner;
        public AbilitySystemComponent Target => _branch.Target?.Owner;
        public EffectEvaluationContext Values { get; }
        public GameplayEvent? Event => _branch.Event;
        public SkillExecutionServices Services { get; }
        public string CurrentActionId { get; internal set; }
        public string Status { get; internal set; } = "就绪";
        public bool IsCastActive => Source != null && Ability.IsActive && Ability.ActivationVersion == _state.Version;
        public bool CanContinue => _detached || IsCastActive;
        internal void BindActivation() => _state.Version = Ability.ActivationVersion;
        public int Version => _state.Version;
        public Vector3 Position => Source != null && Services.Origin != null ? Services.Origin.position : _branch.Position;
        public Vector3 Forward => Source != null && Services.Origin != null ? Services.Origin.forward : _branch.Forward;
        public object SourceIdentity => _state.Source;

        public SkillExecutionContext(GameplayAbilityAsset asset, GameplayAbilitySpec ability, SkillCastTarget target,
            EffectEvaluationContext values, SkillUnitServices services)
        {
            _state = new CastState(asset, ability, services?.Searcher);
            Values = values; Services = new SkillExecutionServices();
            Reset(target, services);
        }
        public bool Allows(TargetQueryDefinition query, AbilitySystemComponent target)
            => query.Allows(_state.Source, target, Services);

        private SkillExecutionContext(SkillExecutionContext parent)
        {
            _state = parent._state; Values = parent.Values;
            Services = new SkillExecutionServices { Execution = this };
            InheritBranch(parent);
        }
        private void InheritBranch(SkillExecutionContext parent)
        {
            _branch = parent._branch;
            _branch.Position = parent.Position; _branch.Forward = parent.Forward;
            _detached = parent._detached;
            Services.CopyFrom(parent.Services);
        }
        public SkillExecutionContext WithTarget(AbilitySystemComponent target, Transform transform = null)
        {
            var scope = new SkillExecutionContext(this); scope._branch.Target = target?.SourceIdentity;
            scope.Services.RebindTarget(transform); return scope;
        }
        internal SkillExecutionContext Selected(AbilitySystemComponent target, Transform t, Vector3? point){
            var scope=WithTarget(target,t);
            if (point.HasValue) scope.Services.TargetPosition=point.Value;
            return scope;
        }
        public SkillExecutionContext WithEvent(GameplayEvent message)
        { var result = new SkillExecutionContext(this); result._branch.Event = message; return result; }
        internal SkillExecutionContext ForTask() => new SkillExecutionContext(this);
        internal void DetachTask() => _detached = true;
        internal void RefreshAsyncTargets()
        {
            if (Source == null) Services.Origin = null;
            if (_branch.Target != null && Target == null) { _branch.Target = null; Services.Target = null; }
        }
        private static readonly TargetSearcher DefaultSearcher = new();
        private static readonly SkillUnitServices EmptyServices = new();
        internal void Reset(SkillCastTarget target, SkillUnitServices services)
        {
            _branch = new Branch { Target = target.Unit?.SourceIdentity };
            _detached=false;CurrentActionId=null;Status="就绪";
            Services.CopyServicesFrom(services ?? EmptyServices);
            Services.Target = target.Aim;
            Services.ClearTargetPosition();
            if (target.Point.HasValue) Services.TargetPosition = target.Point.Value;
            else if (target.Aim != null) Services.TargetPosition = target.Aim.position;
            Services.Execution = this;
            Services.Searcher ??= DefaultSearcher;
            if (!ReferenceEquals(_state.Searcher, Services.Searcher))
            {
                _state.Searcher = Services.Searcher;
                _state.Buffers.Clear();
            }
            if(!Services.Scene.IsValid() && Services.Origin!=null)Services.Scene=Services.Origin.gameObject.scene;
            _branch.Position=Services.Origin!=null?Services.Origin.position:Vector3.zero;_branch.Forward=Services.Origin!=null?Services.Origin.forward:Vector3.forward;
        }
        internal void Cleanup(IReadOnlyList<SkillActionDefinition> actions, IDisposable request)
        {
            request?.Dispose();
            if (actions.Count > 0) new SkillExecutionContext(this) { _detached = true }.Execute(actions);
        }
        public GameplayEffectSpec Effect(GameplayEffectDefinition definition)
            => new GameplayEffectSpec(definition, Source?.Attributes, SourceIdentity, context: Values);
        public TargetQueryBuffer RentBuffer() => _state.Buffers.Count > 0 ? _state.Buffers.Pop() : _state.Searcher.CreateBuffer();
        public void ReturnBuffer(TargetQueryBuffer buffer) { buffer.Clear(); _state.Buffers.Push(buffer); }
        public void Start(GameplayAbilityTask task, SkillTaskOwnership ownership)
        {
            if (ownership == SkillTaskOwnership.Ability)
            {
                if (!IsCastActive || _detached) throw new InvalidOperationException("当前执行范围不能启动技能所属任务。");
                Source.StartTask(Ability, task); return;
            }
            if (task is SkillActionTask actionTask) actionTask.Detach();
            Services.SceneTasks.Start(Ability, task, _state.Version);
        }
        public void Execute(IReadOnlyList<SkillActionDefinition> actions, int startIndex = 0)
        {
            for (int i = startIndex; i < actions.Count; i++)
            {
                if (!ExecuteAction(actions[i])) return;
            }
        }
        public bool ExecuteAction(SkillActionDefinition action)
        {
            if (!CanContinue) return false;
            CurrentActionId = action.Id; Status = action.Label;
            SkillActionDebug.Begin(this, action);
            try { action.Execute(this); } finally { SkillActionDebug.End(this, action, null); }
            return CanContinue;
        }
    }
}
