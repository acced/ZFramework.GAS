using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [Serializable]
    [System.ComponentModel.DisplayName("生成预制体")]
    public sealed class SpawnPrefabAction : SkillAction
    {
        [FlowField("预制体")] public GameObject prefab;
        public SkillSpawnPosition position;
        public Vector3 offset;
        public Vector3 rotation;
        public string anchor;
        public bool follow;
        public SkillNumber lifetime = new SkillNumber(5);
        public SkillNumber count = new SkillNumber(1);
        public Vector3 spacing;
        [FlowField("归属")] public SkillTaskOwnership ownership;
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            if(prefab==null)throw new InvalidOperationException("缺少生成 Prefab。");
            builder.Number(lifetime,0);builder.Number(count,1,256,true);
            var visual = prefab; var where = position; var localOffset = offset; var euler = rotation;
            var path = anchor; bool follows = follow; var owner = ownership;
            if (owner == SkillTaskOwnership.Ability) builder.RequireAbilityTask(Id);
            var duration = lifetime; var id = Id;
            var quantity = count; var separation = spacing;
            return Define("生成预制体", context =>
            {
                var parent = where == SkillSpawnPosition.Target ? (context.Target != null ? context.Services.Target : null) : (context.Source != null ? context.Services.Origin : null);
                if (!string.IsNullOrEmpty(path)) parent = parent != null ? parent.Find(path) : null;
                if (where == SkillSpawnPosition.Target && parent == null || !string.IsNullOrEmpty(path) && parent == null) throw new InvalidOperationException("生成位置已失效。");
                var facing = parent != null ? parent.rotation : Quaternion.LookRotation(context.Forward);
                var point = (where==SkillSpawnPosition.Caster || where==SkillSpawnPosition.Target) && parent!=null?parent.position:context.OriginAt(where);
                int amount = (int)quantity.Resolve(context.Values);
                for (int i = 0; i < amount && context.CanContinue; i++)
                {
                    var instance = SkillSpawn.Create(context, visual, point + facing * (localOffset + separation * i), facing * Quaternion.Euler(euler));
                    if (instance == null) return;
                    context.Start(new SpawnLifetimeTask(context, id, instance, duration.Resolve(context.Values), follows ? parent : null,
                        localOffset + separation * i, Quaternion.Euler(euler), where == SkillSpawnPosition.Target), owner);
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
        protected override void EndAction(bool cancelled)
        { var instance = _instance; _instance = null; if (instance != null) Execution.Services.Objects.Release(instance); }
    }

    [Serializable]
    [System.ComponentModel.DisplayName("发射投射物")]
    public sealed class LaunchProjectileAction : SkillAction
    {
        [FlowField("预制体")] public GameObject prefab;
        public SkillNumber speed = new SkillNumber(8);
        public SkillNumber lifetime = new SkillNumber(5);
        public float radius = .15f;
        public float arcHeight;
        public bool tracking = true;
        public int maxHits = 1;
        public SkillSpawnPosition origin;
        public bool onlySelectedTarget;
        public TargetQuery collision = new TargetQuery { kind = TargetQueryKind.Circle };
        [FlowField("归属")] public SkillTaskOwnership ownership;
        [SerializeReference, FlowBranch("命中时")] public SkillAction[] onHit = Array.Empty<SkillAction>();
        public override bool IsDetachedBranch(string field) => field == nameof(onHit) && ownership == SkillTaskOwnership.Scene;
        public static float ArcOffset(float height, float traveled, float initialDistance)
        {
            float t = initialDistance > .001f ? Mathf.Clamp01(traveled / initialDistance) : 1;
            return height * 4 * t * (1 - t);
        }
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            var visual = prefab; var velocity = speed; var duration = lifetime; float size = radius, arc = arcHeight;
            bool track = tracking; var owner = ownership;
            if (owner == SkillTaskOwnership.Ability) builder.RequireAbilityTask(Id);
            int limit = Math.Max(1, maxHits); var query = collision.Build(); var hit = BuildBranch(builder, nameof(onHit), onHit); var id = Id;var start=origin;bool targeted=onlySelectedTarget;
            if (visual == null) throw new InvalidOperationException("投射物缺少 Prefab。");
            builder.Query(collision); builder.Number(speed,float.Epsilon); builder.Number(lifetime,float.Epsilon);
            if (!float.IsFinite(radius) || radius<=0 || !float.IsFinite(arcHeight) || maxHits<1) throw new InvalidOperationException("投射物形状或命中上限无效。");
            return Define("发射投射物", context =>
            {
                var instance = SkillSpawn.Create(context, visual, context.OriginAt(start), Quaternion.LookRotation(context.Forward));
                if (instance == null) return;
                context.Start(new SkillProjectileTask(context, id, instance, velocity.Resolve(context.Values),
                    duration.Resolve(context.Values), size, arc, track, limit, query, hit,targeted), owner);
            });
        }
    }
    internal sealed class SkillProjectileTask : SkillActionTask
    {
        private GameObject _visual;
        private readonly float _speed, _radius, _arc, _initialDistance;
        private float _remaining, _traveled;
        private readonly bool _tracking,_targeted;
        private readonly int _limit;
        private readonly TargetQueryDefinition _query;
        private readonly SkillActionDefinition[] _onHit;
        private readonly RaycastHit[] _hits = new RaycastHit[64];
        private readonly Collider[] _overlaps = new Collider[64];
        private readonly HashSet<AbilitySystemComponent> _struck = new();
        private Vector3 _flat, _destination;
        public override string State => "飞行中";
        public SkillProjectileTask(SkillExecutionContext context, string id, GameObject visual, float speed, float duration,
            float radius, float arc, bool tracking, int limit, TargetQueryDefinition query, SkillActionDefinition[] onHit,bool targeted=false) : base(context, id)
        {
            _visual = visual; _speed = speed; _remaining = duration; _radius = radius; _arc = arc; _tracking = tracking;
            _targeted=targeted; _limit = limit; _query = query; _onHit = onHit; _flat = visual.transform.position;
            _destination = context.Services.Target != null ? context.Services.Target.position+(context.AimOffset??Vector3.zero) : context.Services.RequireTargetPosition();
            _initialDistance = Vector3.Distance(_flat, _destination);
        }
        protected override void Tick(float dt)
        {
            if (_visual == null || _targeted && (Execution.Target==null || Execution.Services.Target==null)) { Complete(); return; }
            _remaining -= dt;
            if (_tracking && Execution.Target != null && Execution.Services.Target != null) _destination = Execution.Services.Target.position+(Execution.AimOffset??Vector3.zero);
            var previous = _visual.transform.position;
            var next = Vector3.MoveTowards(_flat, _destination, _speed * dt);
            _traveled += Vector3.Distance(next, _flat); _flat = next;
            next.y += LaunchProjectileAction.ArcOffset(_arc, _traveled, _initialDistance);
            var delta = next - previous; float distance = delta.magnitude;
            _visual.transform.position = next;
            if (distance > .0001f) _visual.transform.rotation = Quaternion.LookRotation(delta);
            int overlaps = Physics.OverlapSphereNonAlloc(previous, _radius, _overlaps, _query.LayerMask, QueryTriggerInteraction.Collide);
            if (overlaps == _overlaps.Length) throw new InvalidOperationException("投射物碰撞缓冲已满，请收紧碰撞层或减少重叠碰撞体。");
            for (int i = 0; i < overlaps && IsActive; i++) Hit(_overlaps[i],_overlaps[i].ClosestPoint(previous));
            if (!IsActive) return;
            int count = distance > .0001f ? Physics.SphereCastNonAlloc(previous, _radius, delta / distance, _hits,
                distance, _query.LayerMask, QueryTriggerInteraction.Collide) : 0;
            if (count == _hits.Length) throw new InvalidOperationException("投射物扫掠缓冲已满，请收紧碰撞层。");
            for (int i = 1; i < count; i++)
            {
                var hit = _hits[i]; int j = i;
                while (j > 0 && _hits[j - 1].distance > hit.distance) { _hits[j] = _hits[j - 1]; j--; }
                _hits[j] = hit;
            }
            for (int i = 0; i < count && IsActive; i++) Hit(_hits[i].collider,_hits[i].point);
            if (IsActive && (_remaining <= 0 || (_flat - _destination).sqrMagnitude < .00001f)) Complete();
        }
        private void Hit(Collider collider,Vector3 point)
        {
            var provider = collider.GetComponentInParent<IAbilitySystemProvider>(); var unit = provider?.AbilitySystem;
            if (_targeted && !ReferenceEquals(unit,Execution.Target) || !Execution.Allows(_query, unit) || !_struck.Add(unit)) return;
            var hit=Execution.WithTarget(unit, provider is Component component ? component.transform : collider.transform);
            hit.HitPoint=point;hit.Execute(_onHit);
            if (IsActive && _struck.Count >= _limit) Complete();
        }
        protected override void EndAction(bool cancelled)
        { var visual = _visual; _visual = null; if (visual != null) Execution.Services.Objects.Release(visual); }
    }

    [Serializable]
    [System.ComponentModel.DisplayName("创建区域")]
    public sealed class CreateAreaAction : SkillAction
    {
        [FlowField("预制体")] public GameObject prefab;
        public TargetQuery targets = new TargetQuery { kind = TargetQueryKind.Circle };
        public SkillNumber duration = new SkillNumber(3);
        public SkillNumber period = new SkillNumber(1);
        public GameplayEffectAsset enterEffect;
        public GameplayEffectAsset exitEffect;
        public bool removeOnExit = true;
        [FlowField("归属")] public SkillTaskOwnership ownership;
        [SerializeReference, FlowBranch("每个周期")] public SkillAction[] onPeriod = Array.Empty<SkillAction>();
        public override bool IsDetachedBranch(string field) => field == nameof(onPeriod) && ownership == SkillTaskOwnership.Scene;
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            builder.Query(targets);builder.Number(duration,0);builder.Number(period,0);
            var visual = prefab; var query = targets.Build(); var life = duration; var interval = period;
            builder.DependsOn(enterEffect); builder.DependsOn(exitEffect);
            var enter = enterEffect != null ? enterEffect.Definition : null;
            var exit = exitEffect != null ? exitEffect.Definition : null;
            bool remove = removeOnExit; var owner = ownership;
            if (owner == SkillTaskOwnership.Ability) builder.RequireAbilityTask(Id);
            var periodic = BuildBranch(builder, nameof(onPeriod), onPeriod); var id = Id;
            return Define("创建持续区域", context =>
            {
                var center = query.Center(context);
                var instance = visual != null ? SkillSpawn.Create(context, visual, center, Quaternion.LookRotation(context.Forward)) : null;
                if (!context.CanContinue) return;
                context.Start(new SkillAreaTask(context, id, instance, center, query, life.Resolve(context.Values),
                    interval.Resolve(context.Values), enter, exit, remove, periodic), owner);
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
        protected override void EndAction(bool cancelled)
        {
            while (_occupants.Count > 0)
            { var item = _occupants[_occupants.Count - 1]; _occupants.RemoveAt(_occupants.Count - 1); Leave(item); }
            var visual = _visual; _visual = null; if (visual != null) Execution.Services.Objects.Release(visual);
        }
    }

    [Serializable] [System.ComponentModel.DisplayName("显示目标连线")]
    public sealed class ShowTargetLinkAction : SkillAction
    {
        [Tooltip("包含 LineRenderer 的预制体。")] [FlowField("连线特效")] public GameObject prefab;
        public SkillSpawnPosition from=SkillSpawnPosition.HitPoint;
        public SkillNumber duration=new(.25f);
        public override SkillActionDefinition Build(SkillActionBuildContext builder){
            builder.Number(duration,float.Epsilon);
            var visual=prefab;var origin=from;var life=duration;string id=Id;
            if(visual==null || visual.GetComponentInChildren<LineRenderer>(true)==null)throw new InvalidOperationException("连接特效需要含 LineRenderer 的预制体。");
            return Define("显示目标连线",c=>{
                var start=c.OriginAt(origin);var end=c.Services.RequireTargetPosition();
                var go=SkillSpawn.Create(c,visual,start,Quaternion.identity);if(go==null)return;
                var line=go.GetComponentInChildren<LineRenderer>(true);line.useWorldSpace=true;
                var side=Vector3.Cross(end-start,Vector3.up).normalized*Mathf.Min(.12f,(end-start).magnitude*.09f);
                line.positionCount=5;line.SetPosition(0,start);line.SetPosition(1,Vector3.Lerp(start,end,.27f)+side);
                line.SetPosition(2,Vector3.Lerp(start,end,.55f)-side);line.SetPosition(3,Vector3.Lerp(start,end,.78f)+side*.5f);line.SetPosition(4,end);
                c.Start(new SpawnLifetimeTask(c,id,go,life.Resolve(c.Values),null,default,Quaternion.identity,false),SkillTaskOwnership.Scene);
            });
        }
    }
}