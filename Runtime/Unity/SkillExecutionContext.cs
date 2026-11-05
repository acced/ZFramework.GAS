using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>Per-cast state. Never stored in authoring assets or shared definitions.</summary>
    public sealed class SkillExecutionContext
    {
        private readonly int _sourceGeneration;
        private int _targetGeneration;
        private readonly AbilitySystemComponent _source;
        private AbilitySystemComponent _target;
        private readonly Stack<TargetQueryBuffer> _buffers;
        private readonly TargetSearcher _searcher;

        private Vector3 _position, _forward;
        private sealed class CastState { public int Version, Tasks; public Dictionary<string,HashSet<object>> Records; }
        private readonly CastState _state;
        private readonly bool _independent, _cleanup;
        internal bool Executing;
        internal bool Reusable => !Executing && _state.Tasks == 0;
        internal void RetainTask() => _state.Tasks++;
        internal void ReleaseTask() => _state.Tasks--;
        internal TargetSequenceFrame Sequence {get;set;}
        public Vector3? HitPoint {get;internal set;}
        internal Vector3? AimOffset {get;private set;}
        internal HashSet<object> HitRecord(string key){
            if(string.IsNullOrEmpty(key))return new HashSet<object>();
            _state.Records??=new();if(!_state.Records.TryGetValue(key,out var record))_state.Records.Add(key,record=new());
            return record;
        }
        internal Vector3 OriginAt(SkillSpawnPosition origin){
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
            else
            {
                scope.Services.CopyFrom(Services); scope.Source = Source; scope.Event = Event;
                scope._target = _target; scope._targetGeneration = _targetGeneration;
                scope._position = Position; scope._forward = Forward; scope.Sequence = Sequence;
                scope.HitPoint = HitPoint; scope.AimOffset = AimOffset;
            }
            _modules[module] = null; // Reentrant calls need their own active frame.
#if UNITY_EDITOR
            scope.EditorCallSiteId = EditorCallSiteId ?? (module.IsExternalCall ? module.Id : null);
#endif
            try { scope.Execute(module.Actions); Status = scope.Status; }
            finally { _modules[module] = scope; }
        }
        public GameplayAbilityAsset Asset { get; }
        public GameplayAbilitySpec Ability { get; }
        public AbilitySystemComponent Source { get; private set; }
        public AbilitySystemComponent Target => _target;
        public EffectEvaluationContext Values { get; }
        public GameplayEvent? Event { get; private set; }
        public TimelineTaskContext Services { get; }
#if UNITY_EDITOR
        // Actual outer call, including continuations that outlive the synchronous action stack.
        public string EditorCallSiteId { get; private set; }
#endif
        public string CurrentActionId { get; internal set; }
        public string Status { get; internal set; } = "就绪";
        public bool IsCastActive => Source != null && Ability.IsActive && Ability.ActivationVersion == _state.Version;
        internal bool CanContinue => (_independent || _cleanup || IsCastActive);
        internal void BindActivation() => _state.Version = Ability.ActivationVersion;
        public int Version => _state.Version;
        private readonly Dictionary<string, float> _channels;
        public IReadOnlyDictionary<string, float> Channels => _channels;
        public void SetChannel(string id, float value) => _channels[id] = value;
        public void RemoveChannel(string id) => _channels.Remove(id);
        public Vector3 Position => Source != null && Services.Origin != null ? Services.Origin.position : _position;
        public Vector3 Forward => Source != null && Services.Origin != null ? Services.Origin.forward : _forward;
        // Reference identity remains constant per incarnation, independent of the pooled ASC object.
        public object SourceIdentity { get; }

        public SkillExecutionContext(GameplayAbilityAsset asset, GameplayAbilitySpec ability, AbilitySystemComponent target,
            EffectEvaluationContext values, TimelineTaskContext services)
        {
            _buffers = new(); _state = new CastState { Version = ability.ActivationVersion }; _channels = new();
            Asset = asset; Ability = ability; _source = ability.Owner; _sourceGeneration = _source.Generation;
            SourceIdentity = _source.SourceIdentity; Values = values; Services = new TimelineTaskContext();
            _searcher = services?.Searcher ?? new TargetSearcher(256);
            Reset(target, services);
        }
        internal bool Allows(TargetQueryDefinition query, AbilitySystemComponent target)
        {
            var source = Source;
            if (source == null && Services.TargetFilter != null && !Services.FilterHasSnapshot) return false;
            return query.Allows(source, target, Services.TargetFilter);
        }

        private SkillExecutionContext(SkillExecutionContext parent, AbilitySystemComponent target,
            EffectEvaluationContext values, bool independent = false, bool cleanup = false)
        {
            Asset = parent.Asset; Ability = parent.Ability; Source = parent.Source; _source = parent._source; _sourceGeneration = parent._sourceGeneration;
            _target = target; _targetGeneration = target?.Generation ?? 0; Values = values ?? parent.Values;
            Services = parent.Services.Snapshot(); Services.Execution = this;
            _position = parent.Position; _forward = parent.Forward; SourceIdentity = parent.SourceIdentity;
            _buffers = parent._buffers; _searcher = parent._searcher;
            _state = cleanup ? new CastState { Version = parent._state.Version } : parent._state;
            _independent = independent || parent._independent; _cleanup = cleanup || parent._cleanup;
            _channels = parent._channels;
#if UNITY_EDITOR
            EditorCallSiteId = parent.EditorCallSiteId;
#endif
            Event = parent.Event; Sequence=parent.Sequence; HitPoint=parent.HitPoint; AimOffset=parent.AimOffset;
        }
        public SkillExecutionContext WithTarget(AbilitySystemComponent target, Transform transform = null)
        {
            var scope = new SkillExecutionContext(this, target, null);
            scope.Services.RebindTarget(transform); scope.AimOffset=null; return scope;
        }
        internal SkillExecutionContext Selected(AbilitySystemComponent target, Transform t, Vector3 point){
            var scope=WithTarget(target,t);scope.Services.TargetPosition=point;
            scope.AimOffset=t!=null?point-t.position:Vector3.zero;return scope;
        }
        public SkillExecutionContext WithParameters(SkillParameterValues parameters)
            => new SkillExecutionContext(this, Target, Values.WithParameters(parameters));
        public SkillExecutionContext WithEvent(GameplayEvent message)
        { var result = new SkillExecutionContext(this, Target, null); result.Event = message; return result; }
        internal SkillExecutionContext ForTask()
            => new SkillExecutionContext(this, Target, Values);
        internal SkillExecutionContext Independent() => new SkillExecutionContext(this, Target, null, independent: true);
        // Generation checks are made at the asynchronous resume boundary.
        internal void RefreshAsyncTargets()
        {
            if (_source.Generation != _sourceGeneration) { Source = null; Services.Origin = null; Services.LaunchPoint = null; }
            if (_target != null && _target.Generation != _targetGeneration) { _target = null; Services.Target = null; }
        }
        private static readonly TimelineTaskContext EmptyServices = new();
        internal void Reset(AbilitySystemComponent target, TimelineTaskContext services)
        {
            _target=target;_targetGeneration=target?.Generation??0;Source=_source;CurrentActionId=null;Status="就绪";
            Services.CopyFrom(services ?? EmptyServices);
            Services.Execution=this;Services.Searcher??=_searcher;Services.Objects??=InstantiateSkillObjectService.Instance;
            if(!Services.HasTargetPosition && Services.Target!=null)Services.TargetPosition=Services.Target.position;
            if(!Services.Scene.IsValid() && Services.Origin!=null)Services.Scene=Services.Origin.gameObject.scene;
            if(Services.TargetFilter is ITargetFilterSnapshot filter){Services.TargetFilter=filter.Capture(_source);Services.FilterHasSnapshot=true;}
            _position=Services.Origin!=null?Services.Origin.position:Vector3.zero;_forward=Services.Origin!=null?Services.Origin.forward:Vector3.forward;
            if (_state.Records != null) foreach (var record in _state.Records.Values) record.Clear();_channels.Clear();Sequence=null;HitPoint=null;AimOffset=null;
        }
        internal void Cleanup(IReadOnlyList<SkillActionDefinition> actions, IDisposable request)
        {
            request?.Dispose();
            if (actions.Count > 0) new SkillExecutionContext(this, Target, null, cleanup: true).Execute(actions);
        }
        public GameplayEffectSpec Effect(GameplayEffectDefinition definition)
            => new GameplayEffectSpec(definition, Source?.Attributes, SourceIdentity, context: Values);
        public TargetQueryBuffer RentBuffer() => _buffers.Count > 0 ? _buffers.Pop() : new TargetQueryBuffer();
        public void ReturnBuffer(TargetQueryBuffer buffer) { buffer.Clear(); _buffers.Push(buffer); }
        public void Start(GameplayAbilityTask task, bool independent)
        {
            if (!independent)
            {
                if (!IsCastActive || _cleanup || _independent) throw new InvalidOperationException("当前执行范围不能启动技能所属任务。");
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
