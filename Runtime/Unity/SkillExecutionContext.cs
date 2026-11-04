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
        private readonly Stack<TargetQueryBuffer> _buffers = new();
        private readonly List<(GameplayTaskHost host, GameplayAbilityTask task)> _detached = new();
        private Vector3 _position, _forward;
        private sealed class CastState { public int Version; public string Error; }
        private readonly CastState _state = new();
        private readonly bool _independent, _cleanup;
        private string _moduleCallId, _modulePath;
        public string NavigationId(string actionId) => _moduleCallId ?? actionId;
        private sealed class ModuleCache
        {
            public EffectEvaluationContext Values;
            public string Path;
            public readonly Stack<SkillExecutionContext> Idle = new();
        }
        private readonly Dictionary<object, ModuleCache> _modules = new();
        internal SkillExecutionContext ModuleScope(object key, Func<SkillExecutionContext, SkillParameterValues> bind, string callId, string moduleName)
        {
            if (!_modules.TryGetValue(key, out var cache))
                _modules.Add(key, cache = new ModuleCache { Values = Values.WithParameters(bind(this)),
                    Path = (_modulePath == null ? "" : _modulePath + " → ") + moduleName + " [" + callId + "]" });
            var scope = cache.Idle.Count > 0 ? cache.Idle.Pop() : new SkillExecutionContext(this, Target, cache.Values);
            scope._moduleCallId = NavigationId(callId);
            scope._modulePath = cache.Path;
            // Only parameter bindings are frozen. A leased synchronous frame gets fresh aim/event data.
            scope.Services.CopyFrom(Services); scope.Event = Event; scope._target = _target; scope._targetGeneration = _targetGeneration;
            scope._position = Position; scope._forward = Forward;
            return scope;
        }
        internal void ReleaseModule(object key, SkillExecutionContext scope) => _modules[key].Idle.Push(scope);
        public GameplayAbilityAsset Asset { get; }
        public GameplayAbilitySpec Ability { get; }
        public AbilitySystemComponent Source => _source.Generation == _sourceGeneration ? _source : null;
        public AbilitySystemComponent Target => _target != null && _target.Generation == _targetGeneration ? _target : null;
        public EffectEvaluationContext Values { get; }
        public GameplayEvent? Event { get; private set; }
        public TimelineTaskContext Services { get; }
        public string CurrentActionId { get; internal set; }
        private string _status = "就绪";
        public string Status { get => Error != null ? (_cleanup ? "取消清理失败" : "执行失败") : _status; internal set => _status = value; }
        public string Error => _state.Error;
        public bool IsCastActive => Source != null && Ability.IsActive && Ability.ActivationVersion == _state.Version;
        internal bool CanContinue => Error == null && (_independent || _cleanup || IsCastActive);
        internal void BindActivation() => _state.Version = Ability.ActivationVersion;
        public int Version => _state.Version;
        private readonly Dictionary<string, float> _channels = new();
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
            Asset = asset; Ability = ability; _source = ability.Owner; _sourceGeneration = _source.Generation;
            _target = target; _targetGeneration = target?.Generation ?? 0; Values = values;
            Services = services?.Snapshot() ?? new TimelineTaskContext();
            if (!Services.HasTargetPosition && Services.Target != null) Services.TargetPosition = Services.Target.position;
            Services.Searcher ??= new TargetSearcher(256);
            Services.Objects ??= Services.RentVisual != null && Services.ReturnVisual != null
                ? new LegacyVisualObjects(Services.RentVisual, Services.ReturnVisual) : InstantiateSkillObjectService.Instance;
            Services.Execution = this;
            if (!Services.Scene.IsValid() && Services.Origin != null) Services.Scene = Services.Origin.gameObject.scene;
            _position = Services.Origin != null ? Services.Origin.position : Vector3.zero;
            _forward = Services.Origin != null ? Services.Origin.forward : Vector3.forward;
            SourceIdentity = ability.Owner.SourceIdentity;
            _state.Version = ability.ActivationVersion;
            if (Services.TargetFilter is ITargetFilterSnapshot snapshot)
            { Services.TargetFilter = snapshot.Capture(_source); Services.FilterHasSnapshot = true; }
        }
        private sealed class LegacyVisualObjects : ISkillObjectService
        {
            private readonly Func<GameObject, Transform> _rent;
            private readonly Action<Transform> _release;
            public LegacyVisualObjects(Func<GameObject, Transform> rent, Action<Transform> release) { _rent = rent; _release = release; }
            public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent)
            {
                var visual = _rent(prefab); if (visual == null) return null;
                visual.SetParent(parent, true); visual.SetPositionAndRotation(position, rotation); return visual.gameObject;
            }
            public void Release(GameObject instance) { if (instance != null) _release(instance.transform); }
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
            Asset = parent.Asset; Ability = parent.Ability; _source = parent._source; _sourceGeneration = parent._sourceGeneration;
            _target = target; _targetGeneration = target?.Generation ?? 0; Values = values ?? parent.Values;
            Services = parent.Services.Snapshot(); Services.Execution = this;
            _position = parent.Position; _forward = parent.Forward; SourceIdentity = parent.SourceIdentity;
            _detached = cleanup ? new() : parent._detached; _buffers = parent._buffers;
            _state = cleanup ? new CastState { Version = parent._state.Version } : parent._state;
            _independent = independent || parent._independent; _cleanup = cleanup || parent._cleanup;
            _channels = parent._channels;
            Event = parent.Event;
            _moduleCallId = parent._moduleCallId; _modulePath = parent._modulePath;
        }
        public SkillExecutionContext WithTarget(AbilitySystemComponent target, Transform transform = null)
        {
            var scope = new SkillExecutionContext(this, target, null);
            scope.Services.RebindTarget(transform); return scope;
        }
        public SkillExecutionContext WithParameters(SkillParameterValues parameters)
            => new SkillExecutionContext(this, Target, Values.WithParameters(parameters));
        public SkillExecutionContext WithEvent(GameplayEvent message)
        { var result = new SkillExecutionContext(this, Target, null); result.Event = message; return result; }
        internal SkillExecutionContext ForTask() => new SkillExecutionContext(this, Target, null);
        internal SkillExecutionContext Independent() => new SkillExecutionContext(this, Target, null, independent: true);
        internal void Cleanup(IReadOnlyList<SkillActionDefinition> actions, IDisposable request, string actionId)
        {
            var scope = new SkillExecutionContext(this, Target, null, cleanup: true);
            try { request?.Dispose(); }
            catch (Exception exception) { scope.Fail(exception, actionId); }
            // Disposal failure must not prevent the authored cleanup from being attempted.
            if (scope.Error != null) scope = new SkillExecutionContext(this, Target, null, cleanup: true);
            try { scope.Execute(actions); }
            catch (Exception exception) { scope.Fail(exception, scope.CurrentActionId ?? actionId); }
        }
        public GameplayEffectSpec Effect(GameplayEffectDefinition definition)
            => new GameplayEffectSpec(definition, source: SourceIdentity, context: Values);
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
            _detached.RemoveAll(static item => !item.task.IsActive);
            _detached.Add((host, task));
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
            CurrentActionId = NavigationId(action.Id); Status = action.Label;
            Exception fault = null;
            try { SkillActionDebug.Begin(this, action); if (CanContinue) action.Execute(this); }
            catch (Exception exception) { fault = exception; Fail(exception, action.Id); if (!_cleanup) throw; }
            finally { SkillActionDebug.End(this, action, fault); }
            return CanContinue;
        }
        internal void ExecuteModule(IReadOnlyList<SkillActionDefinition> actions, SkillExecutionContext module)
        {
            try { module.Execute(actions); }
            finally { Status = module.Status; }
        }
        internal void Fail(Exception exception, string actionId, bool alreadyRecorded = false)
        {
            if (Error != null) return;
            // A raw project task can fail during StartTask, before the lifetime observer is installed.
            // Its ASC already ended and diagnosed this run; unwinding the enclosing action isn't another fault.
            alreadyRecorded |= !_cleanup && !IsCastActive && Ability.ActivationVersion == Version
                && ReferenceEquals(Ability.LastExecutionFailure.Error, exception);
            if (_modulePath != null) exception = new SkillActionException(NavigationId(actionId), _modulePath + " / 动作 [" + actionId + "]: " + exception.Message, exception);
            actionId = NavigationId(actionId);
            _state.Error = exception.Message; Status = _cleanup ? "取消清理失败" : "执行失败"; CurrentActionId = actionId;
            if (_cleanup)
            {
                _source.RecordCleanupError(Ability, _state.Version, exception, actionId);
            }
            else if (!alreadyRecorded)
                _source.RecordExecutionFailure(Ability, _state.Version, exception, actionId);
            // Cancel all resources created by this cast, including work detached before the failed action.
            for (int i = _detached.Count - 1; i >= 0; i--)
                try { _detached[i].host.Cancel(_detached[i].task); } catch (Exception cleanup) { Debug.LogException(cleanup); }
            _detached.Clear();
            if (!_cleanup && IsCastActive) Source.EndAbility(Ability);
        }
    }
}
