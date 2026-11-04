using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    public enum SkillTaskOwnership { Scene, Ability }
    public enum SkillSpawnPosition { Caster, Target, TargetPoint }

    internal abstract class SkillActionTask : GameplayAbilityTask, ISkillTaskState
    {
        public SkillExecutionContext Execution { get; private set; }
        public string ActionId { get; }
        public override string DiagnosticId => Execution.NavigationId(ActionId);
        string ISkillTaskState.ActionId => DiagnosticId;
        public abstract string State { get; }
        protected SkillActionTask(SkillExecutionContext context, string id) { Execution = context.ForTask(); ActionId = id; }
        internal void Detach() => Execution = Execution.Independent();
        protected sealed override void OnBegin()
        {
            try { BeginAction(); }
            catch (Exception error) { Execution.Fail(error, ActionId); throw; }
        }
        protected virtual void BeginAction() { }
        protected sealed override void OnTick(float deltaTime)
        {
            try { Tick(deltaTime); }
            catch (Exception exception) { Execution.Fail(exception, ActionId); throw; }
        }
        protected abstract void Tick(float deltaTime);
    }

    internal static class SkillSpawn
    {
        public static void Validate(SkillExecutionContext context, GameObject prefab, bool required = true)
        {
            if (required && prefab == null) throw new InvalidOperationException("缺少 Prefab。");
            if (context.Services.Origin == null) throw new InvalidOperationException("缺少 Origin 场景服务。");
            if (context.Services.Objects == null) throw new InvalidOperationException("缺少对象生成服务。");
            if (context.Services.SceneTasks == null && (!context.Services.Scene.IsValid() || !context.Services.Scene.isLoaded))
                throw new InvalidOperationException("缺少有效场景或场景任务宿主。");
        }
        public static GameObject Create(SkillExecutionContext context, GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            var instance = context.Services.Objects.Spawn(prefab, position, rotation, parent);
            if (instance == null) throw new InvalidOperationException("对象生成服务返回空对象。");
            try
            {
                if (!context.CanContinue) return ReleaseCancelled();
                var scene = context.Services.Scene;
                if (!scene.IsValid() && context.Services.Origin != null) scene = context.Services.Origin.gameObject.scene;
                if (parent == null && scene.IsValid() && instance.scene != scene) SceneManager.MoveGameObjectToScene(instance, scene);
                foreach (var initializer in instance.GetComponentsInChildren<ISkillSpawnInitializer>(true))
                {
                    initializer.InitializeSpawn(context);
                    if (!context.CanContinue) return ReleaseCancelled();
                }
                return instance;
            }
            catch { if (instance != null) context.Services.Objects.Release(instance); throw; }
            GameObject ReleaseCancelled()
            { var cancelled = instance; instance = null; context.Services.Objects.Release(cancelled); return null; }
        }
        public static void Positive(float value, string name)
        { if (!(value > 0) || float.IsInfinity(value)) throw new InvalidOperationException(name + "必须大于零。"); }
    }

    [Serializable]
    public sealed class SpawnPrefabAction : SkillAction
    {
        public GameObject prefab;
        public SkillSpawnPosition position;
        public Vector3 offset;
        public Vector3 rotation;
        public string anchor;
        public bool follow;
        public SkillNumber lifetime = new SkillNumber(5);
        public SkillNumber count = new SkillNumber(1);
        public Vector3 spacing;
        public SkillTaskOwnership ownership;
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            var visual = prefab; var where = position; var localOffset = offset; var euler = rotation;
            var path = anchor; bool follows = follow, independent = ownership == SkillTaskOwnership.Scene;
            if (!independent) builder.RequireAbilityTask(Id);
            var duration = lifetime; var id = Id;
            var quantity = count; var separation = spacing;
            if (visual == null) throw new InvalidOperationException("生成对象缺少 Prefab。");
            return Define("生成预制体", (context, deferred) =>
            {
                SkillSpawn.Validate(context, visual); SkillSpawn.Positive(duration.Resolve(context.Values), "存活时间");
                float amount = quantity.Resolve(context.Values);
                if (amount < 1 || amount > 128 || amount != Mathf.Floor(amount)) throw new InvalidOperationException("生成数量必须为 1–128 的整数。");
                if (where == SkillSpawnPosition.Target && !deferred && context.Services.Target == null) throw new InvalidOperationException("缺少目标挂点。");
                if (where == SkillSpawnPosition.TargetPoint && !deferred) context.Services.RequireTargetPosition();
                var parent = where == SkillSpawnPosition.Target ? context.Services.Target : context.Services.Origin;
                if (!string.IsNullOrEmpty(path) && parent != null && parent.Find(path) == null) throw new InvalidOperationException("挂点不存在: " + path);
            }, context =>
            {
                var parent = where == SkillSpawnPosition.Target ? (context.Target != null ? context.Services.Target : null) : (context.Source != null ? context.Services.Origin : null);
                if (!string.IsNullOrEmpty(path)) parent = parent != null ? parent.Find(path) : null;
                if (where == SkillSpawnPosition.Target && parent == null || !string.IsNullOrEmpty(path) && parent == null) throw new InvalidOperationException("生成位置已失效。");
                var facing = parent != null ? parent.rotation : Quaternion.LookRotation(context.Forward);
                var point = where == SkillSpawnPosition.TargetPoint ? context.Services.RequireTargetPosition() : parent != null ? parent.position : context.Position;
                int amount = (int)quantity.Resolve(context.Values);
                for (int i = 0; i < amount && context.CanContinue; i++)
                {
                    var instance = SkillSpawn.Create(context, visual, point + facing * (localOffset + separation * i), facing * Quaternion.Euler(euler));
                    if (instance == null) return;
                    try { context.Start(new SpawnLifetimeTask(context, id, instance, duration.Resolve(context.Values), follows ? parent : null,
                        localOffset + separation * i, Quaternion.Euler(euler), where == SkillSpawnPosition.Target), independent); }
                    catch { context.Services.Objects.Release(instance); throw; }
                }
            });
        }
    }
    internal sealed class SpawnLifetimeTask : SkillActionTask
    {
        private GameObject _instance;
        private float _remaining;
        private readonly Transform _follow;
        private readonly Vector3 _offset;
        private readonly Quaternion _rotation;
        private readonly bool _target;
        public override string State => "对象存活";
        public SpawnLifetimeTask(SkillExecutionContext context, string id, GameObject instance, float duration, Transform follow, Vector3 offset, Quaternion rotation, bool target) : base(context, id)
        { _instance = instance; _remaining = duration; _follow = follow; _offset = offset; _rotation = rotation; _target = target; }
        protected override void Tick(float dt)
        {
            if (_follow != null && _instance != null && (_target ? Execution.Target : Execution.Source) != null)
                _instance.transform.SetPositionAndRotation(_follow.position + _follow.rotation * _offset, _follow.rotation * _rotation);
            _remaining -= dt; if (_remaining <= 0 || _instance == null) Complete();
        }
        protected override void OnEnd(bool cancelled)
        { var instance = _instance; _instance = null; if (instance != null) Execution.Services.Objects.Release(instance); }
    }

    [Serializable]
    public sealed class LaunchProjectileAction : SkillAction
    {
        public GameObject prefab;
        public SkillNumber speed = new SkillNumber(8);
        public SkillNumber lifetime = new SkillNumber(5);
        public float radius = .15f;
        public float arcHeight;
        public bool tracking = true;
        public int maxHits = 1;
        public TargetQuery collision = new TargetQuery { kind = TargetQueryKind.Circle };
        public SkillTaskOwnership ownership;
        [SerializeReference, FlowBranch("命中时")] public SkillAction[] onHit = Array.Empty<SkillAction>();
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            var visual = prefab; var velocity = speed; var duration = lifetime; float size = radius, arc = arcHeight;
            bool track = tracking, independent = ownership == SkillTaskOwnership.Scene;
            if (!independent) builder.RequireAbilityTask(Id);
            int limit = Math.Max(1, maxHits); var query = collision.Build(); var hit = independent ? builder.BuildDetached(onHit) : builder.Build(onHit); var id = Id;
            if (visual == null) throw new InvalidOperationException("投射物缺少 Prefab。");
            return Define("发射投射物", (context, deferred) =>
            {
                SkillSpawn.Validate(context, visual); SkillSpawn.Positive(velocity.Resolve(context.Values), "速度");
                SkillSpawn.Positive(duration.Resolve(context.Values), "寿命"); SkillSpawn.Positive(size, "碰撞半径");
                if (!deferred && context.Services.Target == null) context.Services.RequireTargetPosition();
                SkillActionBuildContext.Validate(hit, context, true);
            }, context =>
            {
                var instance = SkillSpawn.Create(context, visual, context.Position, Quaternion.LookRotation(context.Forward));
                if (instance == null) return;
                try { context.Start(new SkillProjectileTask(context, id, instance, velocity.Resolve(context.Values),
                    duration.Resolve(context.Values), size, arc, track, limit, query, hit), independent); }
                catch { context.Services.Objects.Release(instance); throw; }
            });
        }
    }
    internal sealed class SkillProjectileTask : SkillActionTask
    {
        private GameObject _visual;
        private readonly float _speed, _radius, _arc, _initialDistance;
        private float _remaining, _traveled;
        private readonly bool _tracking;
        private readonly int _limit;
        private readonly TargetQueryDefinition _query;
        private readonly SkillActionDefinition[] _onHit;
        private readonly RaycastHit[] _hits = new RaycastHit[64];
        private readonly Collider[] _overlaps = new Collider[64];
        private readonly HashSet<AbilitySystemComponent> _struck = new();
        private Vector3 _flat, _destination;
        public override string State => "飞行中";
        public SkillProjectileTask(SkillExecutionContext context, string id, GameObject visual, float speed, float duration,
            float radius, float arc, bool tracking, int limit, TargetQueryDefinition query, SkillActionDefinition[] onHit) : base(context, id)
        {
            _visual = visual; _speed = speed; _remaining = duration; _radius = radius; _arc = arc; _tracking = tracking;
            _limit = limit; _query = query; _onHit = onHit; _flat = visual.transform.position;
            _destination = context.Services.Target != null ? context.Services.Target.position : context.Services.RequireTargetPosition();
            _initialDistance = Vector3.Distance(_flat, _destination);
        }
        protected override void Tick(float dt)
        {
            if (_visual == null) { Complete(); return; }
            _remaining -= dt;
            if (_tracking && Execution.Target != null && Execution.Services.Target != null) _destination = Execution.Services.Target.position;
            var previous = _visual.transform.position;
            var next = Vector3.MoveTowards(_flat, _destination, _speed * dt);
            _traveled += Vector3.Distance(next, _flat); _flat = next;
            float t = _initialDistance > .001f ? Mathf.Clamp01(_traveled / _initialDistance) : 1;
            next.y += _arc * 4 * t * (1 - t);
            var delta = next - previous; float distance = delta.magnitude;
            _visual.transform.position = next;
            if (distance > .0001f) _visual.transform.rotation = Quaternion.LookRotation(delta);
            int overlaps = Physics.OverlapSphereNonAlloc(previous, _radius, _overlaps, _query.LayerMask, QueryTriggerInteraction.Collide);
            if (overlaps == _overlaps.Length) throw new InvalidOperationException("投射物碰撞缓冲已满，请收紧碰撞层或减少重叠碰撞体。");
            for (int i = 0; i < overlaps && IsActive; i++) Hit(_overlaps[i]);
            if (!IsActive) return;
            int count = distance > .0001f ? Physics.SphereCastNonAlloc(previous, _radius, delta / distance, _hits,
                distance, _query.LayerMask, QueryTriggerInteraction.Collide) : 0;
            if (count == _hits.Length) throw new InvalidOperationException("投射物扫掠缓冲已满，请收紧碰撞层。");
            // Physics does not promise ordering. Sweep hits are ordered before applying a hit cap.
            for (int i = 1; i < count; i++)
            {
                var hit = _hits[i]; int j = i;
                while (j > 0 && _hits[j - 1].distance > hit.distance) { _hits[j] = _hits[j - 1]; j--; }
                _hits[j] = hit;
            }
            for (int i = 0; i < count && IsActive; i++) Hit(_hits[i].collider);
            if (IsActive && (_remaining <= 0 || (_flat - _destination).sqrMagnitude < .00001f)) Complete();
        }
        private void Hit(Collider collider)
        {
            var provider = collider.GetComponentInParent<IAbilitySystemProvider>(); var unit = provider?.AbilitySystem;
            if (!Execution.Allows(_query, unit) || !_struck.Add(unit)) return;
            Execution.WithTarget(unit, provider is Component component ? component.transform : collider.transform).Execute(_onHit);
            if (IsActive && _struck.Count >= _limit) Complete();
        }
        protected override void OnEnd(bool cancelled)
        { var visual = _visual; _visual = null; if (visual != null) Execution.Services.Objects.Release(visual); }
    }

    [Serializable]
    public sealed class CreateAreaAction : SkillAction
    {
        public GameObject prefab;
        public TargetQuery targets = new TargetQuery { kind = TargetQueryKind.Circle };
        public SkillNumber duration = new SkillNumber(3);
        public SkillNumber period = new SkillNumber(1);
        public GameplayEffectAsset enterEffect;
        public GameplayEffectAsset exitEffect;
        public bool removeOnExit = true;
        public SkillTaskOwnership ownership;
        [SerializeReference, FlowBranch("每个周期")] public SkillAction[] onPeriod = Array.Empty<SkillAction>();
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            var visual = prefab; var query = targets.Build(); var life = duration; var interval = period;
            builder.DependsOn(enterEffect); builder.DependsOn(exitEffect);
            var enter = enterEffect != null ? enterEffect.Definition : null;
            var exit = exitEffect != null ? exitEffect.Definition : null;
            bool remove = removeOnExit, independent = ownership == SkillTaskOwnership.Scene;
            if (!independent) builder.RequireAbilityTask(Id);
            var periodic = independent ? builder.BuildDetached(onPeriod) : builder.Build(onPeriod); var id = Id;
            return Define("创建持续区域", (context, deferred) =>
            {
                SkillSpawn.Validate(context, visual, false); query.Validate(context, !deferred);
                float seconds = life.Resolve(context.Values);
                if (independent || seconds != 0) SkillSpawn.Positive(seconds, "区域时长");
                if (interval.Resolve(context.Values) < 0) throw new InvalidOperationException("周期不能为负数。");
                if (enter != null) SkillActionBuildContext.Effect(enter, context, context.Target?.Attributes);
                if (exit != null) SkillActionBuildContext.Effect(exit, context, context.Target?.Attributes);
                SkillActionBuildContext.Validate(periodic, context, true);
            }, context =>
            {
                var center = query.Kind == TargetQueryKind.TargetPoint ? context.Services.RequireTargetPosition() : context.Position;
                var instance = visual != null ? SkillSpawn.Create(context, visual, center, Quaternion.LookRotation(context.Forward)) : null;
                if (!context.CanContinue) return;
                try { context.Start(new SkillAreaTask(context, id, instance, center, query, life.Resolve(context.Values),
                    interval.Resolve(context.Values), enter, exit, remove, periodic), independent); }
                catch { if (instance != null) context.Services.Objects.Release(instance); throw; }
            });
        }
    }
    internal sealed class SkillAreaTask : SkillActionTask
    {
        private struct Occupant { public AbilitySystemComponent Unit; public int Generation; public ActiveGameplayEffectHandle Handle; public SkillExecutionContext Context; }
        private readonly List<Occupant> _occupants = new(32);
        private readonly TargetQueryBuffer _buffer = new();
        private readonly TargetQueryDefinition _query;
        private readonly Vector3 _center;
        private readonly float _period;
        private float _remaining, _timer;
        private GameObject _visual;
        private readonly GameplayEffectDefinition _enter, _exit;
        private readonly bool _remove;
        private readonly SkillActionDefinition[] _periodic;
        public override string State => "区域持续中";
        public SkillAreaTask(SkillExecutionContext context, string id, GameObject visual, Vector3 center, TargetQueryDefinition query,
            float duration, float period, GameplayEffectDefinition enter, GameplayEffectDefinition exit, bool remove,
            SkillActionDefinition[] periodic) : base(context, id)
        { _visual = visual; _center = center; _query = query; _remaining = duration == 0 ? float.PositiveInfinity : duration; _period = period; _enter = enter; _exit = exit; _remove = remove; _periodic = periodic; }
        protected override void Tick(float dt)
        {
            // Admission is capped, but occupancy uses the complete query. Distance swaps must not reset effects.
            Execution.Services.Searcher.Query(_query, Execution, _buffer, _center, applyLimit: false);
            for (int i = _occupants.Count - 1; i >= 0; i--)
            {
                var old = _occupants[i]; bool present = false;
                for (int j = 0; j < _buffer.Count; j++) if (ReferenceEquals(old.Unit, _buffer.Units[j])) { present = true; break; }
                if (old.Unit.Generation != old.Generation || (!present && !_buffer.Saturated))
                { _occupants.RemoveAt(i); Leave(old); if (!IsActive) return; }
            }
            for (int i = 0; i < _buffer.Count; i++)
            {
                var unit = _buffer.Units[i]; bool present = false;
                for (int j = 0; j < _occupants.Count; j++) if (ReferenceEquals(unit, _occupants[j].Unit)) { present = true; break; }
                if (present) continue;
                if (_occupants.Count >= _query.MaxTargets) break;
                var item = new Occupant { Unit = unit, Generation = unit.Generation, Context = Execution.WithTarget(unit, _buffer.Transforms[i]) };
                if (_enter != null) unit.Effects.Apply(Execution.Effect(_enter), out item.Handle);
                _occupants.Add(item);
                if (!IsActive) { _occupants.Remove(item); Leave(item); return; }
            }
            float elapsed = Math.Min(dt, _remaining); _remaining -= dt; _timer += elapsed;
            if (_period > 0) while (_timer >= _period && IsActive)
            {
                _timer -= _period;
                for (int i = _occupants.Count - 1; i >= 0 && IsActive; i--)
                    if (i < _occupants.Count && _occupants[i].Unit.Generation == _occupants[i].Generation) _occupants[i].Context.Execute(_periodic);
            }
            if (IsActive && _remaining <= 0) Complete();
        }
        private void Leave(Occupant item)
        {
            if (item.Unit.Generation != item.Generation) return;
            if (_remove) item.Unit.Effects.Remove(item.Handle);
            if (_exit != null) item.Unit.Effects.Apply(Execution.Effect(_exit));
        }
        protected override void OnEnd(bool cancelled)
        {
            Exception error = null;
            try
            {
                while (_occupants.Count > 0)
                { var item = _occupants[_occupants.Count - 1]; _occupants.RemoveAt(_occupants.Count - 1); try { Leave(item); } catch (Exception exception) { error ??= exception; } }
            }
            finally { var visual = _visual; _visual = null; if (visual != null) Execution.Services.Objects.Release(visual); }
            if (error != null) throw error;
        }
    }
}
