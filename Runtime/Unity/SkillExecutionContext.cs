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
            public readonly TargetSearcher Searcher;
            public readonly Stack<TargetQueryBuffer> Buffers = new();
            public readonly Dictionary<string, float> Channels = new();
            public Dictionary<string, HashSet<object>> Records;
            public int Version, Users;
            public CastState(GameplayAbilityAsset asset, GameplayAbilitySpec ability, TargetSearcher searcher)
            {
                Asset = asset; Ability = ability; Source = ability.Owner.SourceIdentity;
                Version = ability.ActivationVersion;
                Searcher = searcher ?? new TargetSearcher(256);
            }
        }
        // Value copy: selecting a target or receiving an event never changes a sibling's branch.
        private struct Branch
        {
            public AbilitySourceIdentity Target;
            public Vector3 Position, Forward;
            public Vector3? HitPoint, AimOffset;
            public TargetSequenceFrame Sequence;
            public GameplayEvent? Event;
        }
        private readonly CastState _state;
        private Branch _branch;
        private bool _detached;
        internal bool Reusable => _state.Users == 0;
        /// <summary>任务或扩展持有本次施放的上下文期间调用，阻止下一次施放复用它；结束时调用 <see cref="Release"/>。</summary>
        public void Retain() => _state.Users++;
        public void Release() => _state.Users--;
        internal TargetSequenceFrame Sequence { get => _branch.Sequence; set => _branch.Sequence = value; }
        public Vector3? HitPoint { get => _branch.HitPoint; set => _branch.HitPoint = value; }
        public Vector3? AimOffset => _branch.AimOffset;
        internal HashSet<object> HitRecord(string key){
            if(string.IsNullOrEmpty(key))return new HashSet<object>();
            _state.Records??=new();if(!_state.Records.TryGetValue(key,out var record))_state.Records.Add(key,record=new());
            return record;
        }
        public Vector3 OriginAt(SkillSpawnPosition origin){
            return origin switch{
                SkillSpawnPosition.Caster=>Position,
                SkillSpawnPosition.Target=>Target!=null && Services.Target!=null?Services.Target.position:throw new InvalidOperationException("缺少当前目标。"),
                SkillSpawnPosition.TargetPoint=>Services.RequireTargetPosition(),
                SkillSpawnPosition.HitPoint=>HitPoint??throw new InvalidOperationException("缺少命中点。"),
                SkillSpawnPosition.EventPosition=>Event?.Position is { } p && float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)?new Vector3(p.X,p.Y,p.Z):throw new InvalidOperationException("事件没有有效位置。"),
                SkillSpawnPosition.LaunchPoint=>Source!=null && Services.LaunchPoint!=null?Services.LaunchPoint.position:Position,
                _=>throw new InvalidOperationException("未知原点。")
            };
        }
        // Values are immutable on this parent. A jump's module frames die with the jump;
        // a periodic occupant or a reusable cast can keep its warmed frames.
        private Dictionary<ModuleDefinition, SkillExecutionContext> _modules;
        internal void ExecuteModule(ModuleDefinition module)
        {
            _modules ??= new Dictionary<ModuleDefinition, SkillExecutionContext>();
            if (!_modules.TryGetValue(module, out var scope) || scope == null)
                scope = WithParameters(module.Resolve(Values));
            else scope.InheritBranch(this);
            _modules[module] = null; // Reentrant calls need their own active frame.
#if UNITY_EDITOR
            scope.EditorCallSiteId = EditorCallSiteId ?? (module.IsExternalCall ? module.Id : null);
#endif
            try { scope.Execute(module.Actions); Status = scope.Status; }
            finally { _modules[module] = scope; }
        }
        public GameplayAbilityAsset Asset => _state.Asset;
        public GameplayAbilitySpec Ability => _state.Ability;
        public AbilitySystemComponent Source => _state.Source.Owner;
        public AbilitySystemComponent Target => _branch.Target?.Owner;
        public EffectEvaluationContext Values { get; }
        public GameplayEvent? Event => _branch.Event;
        public TimelineTaskContext Services { get; }
#if UNITY_EDITOR
        // Actual outer call, including continuations that outlive the synchronous action stack.
        public string EditorCallSiteId { get; private set; }
#endif
        public string CurrentActionId { get; internal set; }
        public string Status { get; internal set; } = "就绪";
        public bool IsCastActive => Source != null && Ability.IsActive && Ability.ActivationVersion == _state.Version;
        public bool CanContinue => _detached || IsCastActive;
        internal void BindActivation() => _state.Version = Ability.ActivationVersion;
        public int Version => _state.Version;
        public IReadOnlyDictionary<string, float> Channels => _state.Channels;
        public void SetChannel(string id, float value) => _state.Channels[id] = value;
        public void RemoveChannel(string id) => _state.Channels.Remove(id);
        public Vector3 Position => Source != null && Services.Origin != null ? Services.Origin.position : _branch.Position;
        public Vector3 Forward => Source != null && Services.Origin != null ? Services.Origin.forward : _branch.Forward;
        public object SourceIdentity => _state.Source;

        public SkillExecutionContext(GameplayAbilityAsset asset, GameplayAbilitySpec ability, AbilitySystemComponent target,
            EffectEvaluationContext values, TimelineTaskContext services)
            : this(asset, ability, SkillCastTarget.From(target, services), values, services) { }
        internal SkillExecutionContext(GameplayAbilityAsset asset, GameplayAbilitySpec ability, SkillCastTarget target,
            EffectEvaluationContext values, SkillUnitServices services)
        {
            _state = new CastState(asset, ability, services?.Searcher);
            Values = values; Services = new TimelineTaskContext();
            Reset(target, services);
        }
        public bool Allows(TargetQueryDefinition query, AbilitySystemComponent target)
        {
            var source = Source;
            if (source == null && Services.TargetFilter != null && !Services.FilterHasSnapshot) return false;
            return query.Allows(_state.Source, target, Services);
        }

        private SkillExecutionContext(SkillExecutionContext parent, EffectEvaluationContext values = null)
        {
            _state = parent._state; Values = values ?? parent.Values;
            Services = new TimelineTaskContext { Execution = this };
            InheritBranch(parent);
        }
        private void InheritBranch(SkillExecutionContext parent)
        {
            _branch = parent._branch;
            _branch.Position = parent.Position; _branch.Forward = parent.Forward;
            _detached = parent._detached;
            Services.CopyFrom(parent.Services);
#if UNITY_EDITOR
            EditorCallSiteId = parent.EditorCallSiteId;
#endif
        }
        public SkillExecutionContext WithTarget(AbilitySystemComponent target, Transform transform = null)
        {
            var scope = new SkillExecutionContext(this); scope._branch.Target = target?.SourceIdentity;
            scope.Services.RebindTarget(transform); scope._branch.AimOffset = null; return scope;
        }
        internal SkillExecutionContext Selected(AbilitySystemComponent target, Transform t, Vector3 point){
            var scope=WithTarget(target,t);scope.Services.TargetPosition=point;
            scope._branch.AimOffset=t!=null?point-t.position:Vector3.zero;return scope;
        }
        public SkillExecutionContext WithParameters(SkillParameterValues parameters)
            => new SkillExecutionContext(this, Values.WithParameters(parameters));
        public SkillExecutionContext WithEvent(GameplayEvent message)
        { var result = new SkillExecutionContext(this); result._branch.Event = message; return result; }
        internal SkillExecutionContext ForTask() => new SkillExecutionContext(this);
        internal void DetachTask() => _detached = true;
        internal void RefreshAsyncTargets()
        {
            if (Source == null) { Services.Origin = null; Services.LaunchPoint = null; }
            if (_branch.Target != null && Target == null) { _branch.Target = null; Services.Target = null; }
        }
        private static readonly SkillUnitServices EmptyServices = new();
        internal void Reset(SkillCastTarget target, SkillUnitServices services)
        {
            _branch = new Branch { Target = target.Unit?.SourceIdentity };
            _detached=false;CurrentActionId=null;Status="就绪";
            Services.CopyServicesFrom(services ?? EmptyServices);
            Services.FilterHasSnapshot = services is TimelineTaskContext legacy && legacy.FilterHasSnapshot;
            Services.Target = target.Aim;
            Services.ClearTargetPosition();
            if (target.Point.HasValue) Services.TargetPosition = target.Point.Value;
            else if (target.Aim != null) Services.TargetPosition = target.Aim.position;
            Services.Execution=this;Services.Searcher??=_state.Searcher;Services.Objects??=InstantiateSkillObjectService.Instance;
            if(!Services.Scene.IsValid() && Services.Origin!=null)Services.Scene=Services.Origin.gameObject.scene;
            if(Services.TargetFilter is ITargetFilterSnapshot filter){Services.TargetFilter=filter.Capture(Source);Services.FilterHasSnapshot=true;}
            _branch.Position=Services.Origin!=null?Services.Origin.position:Vector3.zero;_branch.Forward=Services.Origin!=null?Services.Origin.forward:Vector3.forward;
            if (_state.Records != null) foreach (var record in _state.Records.Values) record.Clear();
            _state.Channels.Clear();
        }
        internal void Cleanup(IReadOnlyList<SkillActionDefinition> actions, IDisposable request)
        {
            request?.Dispose();
            if (actions.Count > 0) new SkillExecutionContext(this) { _detached = true }.Execute(actions);
        }
        public GameplayEffectSpec Effect(GameplayEffectDefinition definition)
            => new GameplayEffectSpec(definition, Source?.Attributes, SourceIdentity, context: Values);
        public TargetQueryBuffer RentBuffer() => _state.Buffers.Count > 0 ? _state.Buffers.Pop() : new TargetQueryBuffer();
        public void ReturnBuffer(TargetQueryBuffer buffer) { buffer.Clear(); _state.Buffers.Push(buffer); }
        public void Start(GameplayAbilityTask task, bool independent)
            => Start(task, independent ? SkillTaskOwnership.Scene : SkillTaskOwnership.Ability);
        public void Start(GameplayAbilityTask task, SkillTaskOwnership ownership)
        {
            if (ownership == SkillTaskOwnership.Ability)
            {
                if (!IsCastActive || _detached) throw new InvalidOperationException("当前执行范围不能启动技能所属任务。");
                Source.StartTask(Ability, task); return;
            }
            var host = Services.SceneTasks;
            if (host == null)
            {
                var scene = Services.Scene;
                if (!scene.IsValid() && Services.Origin != null) scene = Services.Origin.gameObject.scene;
                host = SceneSkillTaskHost.For(scene).Tasks;
            }
            if (task is SkillActionTask actionTask) actionTask.Detach();
            host.Start(Ability, task, _state.Version);
        }
        public void Execute(IReadOnlyList<SkillActionDefinition> actions, int startIndex = 0)
        {
            if (actions == null) return;
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
