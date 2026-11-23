using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>零分配栈上目标查询作用域，退出 using 时自动归还缓冲。</summary>
    public readonly ref struct TargetQueryScope
    {
        private readonly SkillExecutionContext _context;
        private readonly TargetQueryBuffer _buffer;

        public TargetQueryBuffer Buffer => _buffer;
        public int Count => _buffer.Count;
        public bool Saturated => _buffer.Saturated;
        public AbilitySystemComponent[] Units => _buffer.Units;
        public Transform[] Transforms => _buffer.Transforms;
        public bool IsCurrent(int index) => _buffer.IsCurrent(index);
        internal Vector3? PointAt(int index) => _buffer.Points[index];

        internal TargetQueryScope(SkillExecutionContext context, TargetQueryBuffer buffer)
        {
            _context = context; _buffer = buffer;
        }

        public void Dispose() => _context.ReturnBuffer(_buffer);
    }

    /// <summary>单次施放上下文；分支通过值拷贝隔离，不污染兄弟分支。</summary>
    public sealed class SkillExecutionContext
    {
        private sealed class CastState
        {
            public readonly GameplayAbilityAsset Asset;
            public readonly GameplayAbilitySpec Ability;
            public readonly AbilitySourceIdentity Source;
            public readonly Stack<TargetQueryBuffer> Buffers = new();
            public TargetSearcher Searcher;
            public int Version, Users;
            public float TimeScale = 1f;

            public CastState(GameplayAbilityAsset asset, GameplayAbilitySpec ability, TargetSearcher searcher)
            {
                Asset = asset; Ability = ability; Source = ability.Owner.SourceIdentity;
                Version = ability.ActivationVersion; Searcher = searcher ?? DefaultSearcher;
            }
        }

        private struct Branch
        {
            public AbilitySourceIdentity Target;
            public Vector3 Position, Forward;
            public bool HasExplicitForward;
            public GameplayEvent? Event;
        }

        private static readonly TargetSearcher DefaultSearcher = new();
        private static readonly SkillUnitServices EmptyServices = new();

        private readonly CastState _state;
        private Branch _branch;
        private bool _detached;

        internal bool Reusable => _state.Users == 0;
        public void Retain() => _state.Users++;
        public void Release() => _state.Users--;

        public float TimeScale
        {
            get => _state.TimeScale;
            set => _state.TimeScale = value;
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
        public int Version => _state.Version;
        public object SourceIdentity => _state.Source;
        public Vector3 Position => Source != null && Services.Origin != null ? Services.Origin.position : _branch.Position;
        public Vector3 Forward => _branch.HasExplicitForward
            ? _branch.Forward : Source != null && Services.Origin != null ? Services.Origin.forward : _branch.Forward;

        internal void BindActivation() => _state.Version = Ability.ActivationVersion;

        public Vector3 OriginAt(SkillOrigin origin) => origin switch
        {
            SkillOrigin.Caster => Position,
            SkillOrigin.Target => Target != null && Services.Target != null ? Services.Target.position : throw new InvalidOperationException("缺少当前目标。"),
            SkillOrigin.TargetPoint => Services.RequireTargetPosition(),
            SkillOrigin.EventPosition => Event?.Position is { } p && float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
                ? new Vector3(p.X, p.Y, p.Z) : throw new InvalidOperationException("事件没有有效位置。"),
            _ => throw new InvalidOperationException("未知原点。"),
        };

        public SkillExecutionContext(GameplayAbilityAsset asset, GameplayAbilitySpec ability,
            SkillCastTarget target, EffectEvaluationContext values, SkillUnitServices services)
        {
            _state = new CastState(asset, ability, services?.Searcher);
            Values = values;
            Services = new SkillExecutionServices();
            Reset(target, services);
        }

        private SkillExecutionContext(SkillExecutionContext parent)
        {
            _state = parent._state;
            Values = parent.Values;
            Services = new SkillExecutionServices { Execution = this };
            _branch = parent._branch;
            _branch.Position = parent.Position;
            _branch.Forward = parent.Forward;
            _detached = parent._detached;
            Services.CopyFrom(parent.Services);
        }

        public bool Allows(TargetQueryDefinition query, AbilitySystemComponent target)
            => query.Allows(_state.Source, target, Services);

        public SkillExecutionContext WithTarget(AbilitySystemComponent target, Transform transform = null)
        {
            var scope = new SkillExecutionContext(this);
            scope._branch.Target = target?.SourceIdentity;
            scope.Services.RebindTarget(transform);
            return scope;
        }

        internal SkillExecutionContext Selected(AbilitySystemComponent target, Transform transform, Vector3? point)
        {
            SkillExecutionContext scope = WithTarget(target, transform);
            if (point.HasValue) scope.Services.TargetPosition = point.Value;
            return scope;
        }

        public SkillExecutionContext WithEvent(GameplayEvent message)
        {
            var result = new SkillExecutionContext(this);
            result._branch.Event = message;
            return result;
        }

        internal SkillExecutionContext ForTask() => new(this);
        internal void DetachTask() => _detached = true;

        internal void RefreshAsyncTargets()
        {
            if (Source == null) Services.Origin = null;
            if (_branch.Target != null && Target == null) { _branch.Target = null; Services.Target = null; }
        }

        internal void Reset(SkillCastTarget target, SkillUnitServices services)
        {
            _branch = new Branch { Target = target.Unit?.SourceIdentity };
            _detached = false;
            _state.TimeScale = 1f;
            CurrentActionId = null; Status = "就绪";

            Services.CopyServicesFrom(services ?? EmptyServices);
            Services.Target = target.Aim;
            Services.ClearTargetPosition();
            if (target.Point.HasValue) Services.TargetPosition = target.Point.Value;
            else if (target.Aim != null) Services.TargetPosition = target.Aim.position;

            Services.Execution = this;
            Services.Searcher ??= DefaultSearcher;
            if (!ReferenceEquals(_state.Searcher, Services.Searcher)) { _state.Searcher = Services.Searcher; _state.Buffers.Clear(); }
            if (!Services.Scene.IsValid() && Services.Origin != null) Services.Scene = Services.Origin.gameObject.scene;

            _branch.Position = Services.Origin != null ? Services.Origin.position : Vector3.zero;
            if (target.Direction is { } dir && dir.sqrMagnitude > 1e-8f) { _branch.Forward = dir.normalized; _branch.HasExplicitForward = true; }
            else _branch.Forward = Services.Origin != null ? Services.Origin.forward : Vector3.forward;
        }

        internal void Cleanup(IReadOnlyList<SkillActionDefinition> actions, IDisposable request)
        {
            request?.Dispose();
            if (actions.Count > 0) new SkillExecutionContext(this) { _detached = true }.Execute(actions);
        }

        public GameplayEffectSpec Effect(GameplayEffectDefinition definition)
            => new(definition, Source?.Attributes, SourceIdentity, context: Values);

        public TargetQueryBuffer RentBuffer() => _state.Buffers.Count > 0 ? _state.Buffers.Pop() : _state.Searcher.CreateBuffer();
        public void ReturnBuffer(TargetQueryBuffer buffer) { buffer.Clear(); _state.Buffers.Push(buffer); }

        public TargetQueryScope QueryTargets(TargetQueryDefinition query, Vector3? centerOverride = null,
            Vector3? forwardOverride = null, string saturatedMessage = "目标查询缓冲区已满（256 个碰撞体），请收窄 LayerMask 或范围。")
        {
            TargetQueryBuffer buffer = RentBuffer();
            try
            {
                Services.Searcher.Query(query, this, buffer, centerOverride, forwardOverride);
                GasAssert.That(!buffer.Saturated, saturatedMessage);
                return new TargetQueryScope(this, buffer);
            }
            catch { ReturnBuffer(buffer); throw; }
        }

        public void Start(GameplayAbilityTask task, SkillTaskOwnership ownership)
        {
            if (ownership == SkillTaskOwnership.Ability)
            {
                if (!IsCastActive || _detached) throw new InvalidOperationException("当前执行范围不能启动技能所属任务。");
                Source.StartTask(Ability, task);
                return;
            }
            if (Services.SceneTasks == null) throw new InvalidOperationException("启动场景归属任务需在 SkillUnitServices 中注入 SceneTasks。");
            if (task is SkillActionTask actionTask) actionTask.Detach();
            Services.SceneTasks.Start(Ability, task, _state.Version);
        }

        public void Execute(IReadOnlyList<SkillActionDefinition> actions, int startIndex = 0)
        {
            for (int i = startIndex; i < actions.Count; i++)
                if (!ExecuteAction(actions[i])) return;
        }

        public bool ExecuteAction(SkillActionDefinition action)
        {
            if (!CanContinue) return false;
            CurrentActionId = action.Id; Status = action.Label;
            SkillActionDebug.Begin(this, action);
            Exception error = null;
            try { action.Execute(this); }
            catch (Exception ex) { error = ex; throw; }
            finally { SkillActionDebug.End(this, action, error); }
            return CanContinue;
        }
    }
}
