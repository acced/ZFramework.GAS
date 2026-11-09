using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// 挂在实体 GameObject 上，让空间查询能从碰撞体反查到它的 <see cref="AbilitySystemComponent"/>。
    /// 由使用方的单位组件实现——库不规定单位类型。
    /// </summary>
    public interface IAbilitySystemProvider
    {
        AbilitySystemComponent AbilitySystem { get; }
    }

    /// <summary>A unit service for targets supplied by a board, a 2D physics system, a server, etc.
    /// Report candidates synchronously in priority order. No Transform or Collider is required.</summary>
    public interface ISkillTargetSource
    {
        void Collect(string key, SkillExecutionContext cast, SkillTargetResults results);
    }

    /// <summary>Borrowed for one query. All producers use the same identity, filtering and deduplication rules.</summary>
    public readonly ref struct SkillTargetResults
    {
        private readonly SkillExecutionContext _cast;
        private readonly TargetQueryDefinition _query;
        private readonly TargetQueryBuffer _b;
        internal SkillTargetResults(SkillExecutionContext cast, TargetQueryDefinition query, TargetQueryBuffer b)
        { _cast = cast; _query = query; _b = b; }
        public void Add(AbilitySystemComponent unit, Transform aim = null, Vector3? point = null)
            => Insert(unit, aim, point ?? (aim != null ? aim.position : (Vector3?)null), 0);
        internal void Insert(AbilitySystemComponent unit, Transform aim, Vector3? point, float distance)
        {
            if (!_cast.Allows(_query, unit)) return;
            for (int i = 0; i < _b.Count; i++) if (ReferenceEquals(_b.Units[i], unit)) return;
            if (_b.Count == _b.Units.Length) { _b.Saturated = true; return; }
            int index = _b.Count++;
            while (index > 0 && _b.Distances[index - 1] > distance)
            {
                _b.Units[index] = _b.Units[index - 1]; _b.Transforms[index] = _b.Transforms[index - 1];
                _b.Distances[index] = _b.Distances[index - 1]; _b.Generations[index] = _b.Generations[index - 1];
                _b.Points[index] = _b.Points[index - 1]; index--;
            }
            _b.Units[index] = unit; _b.Transforms[index] = aim; _b.Points[index] = point;
            _b.Distances[index] = distance; _b.Generations[index] = unit.Generation;
        }
    }

    /// <summary>
    /// 指定对象、物理或项目目标来源的统一入口。通过 <see cref="TargetQueryBuffer"/> 缓冲结果，
    /// 嵌套查询各持自己的 buffer 即可避免重入问题。
    ///
    /// 扇形的角度判定在 XZ 平面上进行（忽略高度差），以适配地面战斗；距离与重叠检测仍是三维的。
    ///
    /// 查询看到的是上一次物理同步时的碰撞体位置。项目若关闭了
    /// <see cref="Physics.autoSyncTransforms"/>（Unity 默认关），当帧内挪动的 Transform
    /// 不会反映到结果里，直到下一次 FixedUpdate 或显式 <see cref="Physics.SyncTransforms"/>。
    /// </summary>
    public sealed class TargetSearcher
    {
        /// <summary>Physical results are nearest first; project results retain the supplied priority.</summary>
        public int Query(TargetQueryDefinition query, SkillExecutionContext context, TargetQueryBuffer buffer,
            Vector3? centerOverride = null, bool applyLimit = true)
            =>Query(query,context,buffer,centerOverride,applyLimit,false);

        internal int Query(TargetQueryDefinition query,SkillExecutionContext context,TargetQueryBuffer buffer,
            Vector3? centerOverride,bool applyLimit,bool captureAim,Vector3? forwardOverride = null)
        {
            buffer.Clear();
            var results = new SkillTargetResults(context, query, buffer);
            if (query.Kind == TargetQueryKind.Self || query.Kind == TargetQueryKind.FixedTarget)
            {
                bool self = query.Kind == TargetQueryKind.Self;
                results.Add(self ? context.Source : context.Target, self ? context.Services.Origin : context.Services.Target,
                    !self && context.Services.HasTargetPosition ? context.Services.TargetPosition : (Vector3?)null);
            }
            else if (query.Kind == TargetQueryKind.Project)
            {
                var service = context.Services.Get<ISkillTargetSource>() ?? throw new InvalidOperationException("没有接入项目目标来源：" + query.SourceKey);
                service.Collect(query.SourceKey, context, results);
            }
            else
            {
                Vector3 center; int hits; Collider weapon = null;
                var forward = forwardOverride ?? context.Forward;
                var shape = query.ResolveShape(context.Values);
                if (query.Kind == TargetQueryKind.Hitbox)
                {
                    var origin = context.Source != null ? context.Services.Origin : null;
                    weapon = origin != null ? origin.GetComponent<SkillHitboxes>()?.Find(query.SourceKey) : null;
                    if (origin != null && weapon == null) throw new InvalidOperationException("单位未绑定碰撞体槽位：" + query.SourceKey);
                    if (weapon != null && weapon.enabled && weapon.gameObject.activeInHierarchy)
                    {
                        if (!SkillColliderVolume.TryRead(weapon, out var volume)) throw new InvalidOperationException("碰撞体槽位需要 Box、Sphere 或 Capsule Collider：" + query.SourceKey);
                        center = volume.Center;
                        hits = volume.Overlap(weapon.gameObject.scene.GetPhysicsScene(), buffer.Colliders, query.LayerMask);
                    }
                    else { center = default; hits = 0; }
                }
                else
                {
                    center = centerOverride ?? query.Center(context);
                    hits = query.Kind == TargetQueryKind.Line
                        ? Physics.OverlapBoxNonAlloc(center + forward * shape.HalfExtents.z, shape.HalfExtents,
                            buffer.Colliders, Quaternion.LookRotation(forward), query.LayerMask, QueryTriggerInteraction.Collide)
                        : Physics.OverlapSphereNonAlloc(center, shape.Radius, buffer.Colliders, query.LayerMask, QueryTriggerInteraction.Collide);
                }
                buffer.Saturated = hits == buffer.Colliders.Length;
                var flatForward = new Vector2(forward.x, forward.z).normalized;
                for (int i = 0; i < hits; i++)
                {
                    var collider = buffer.Colliders[i];
                    if (collider == weapon || collider.GetComponentInParent<IAbilitySystemProvider>() is not { } provider) continue;
                    var t = provider is Component component ? component.transform : collider.transform;
                    var delta = t.position - center;
                    if (shape.IncludesDirection(flatForward, delta))
                        results.Insert(provider.AbilitySystem, t, captureAim ? collider.bounds.center : (Vector3?)null, delta.sqrMagnitude);
                }
            }
            int count = applyLimit ? Mathf.Min(buffer.Count, query.MaxTargets) : buffer.Count;
            for (int i = count; i < buffer.Count; i++) { buffer.Units[i] = null; buffer.Transforms[i] = null; }
            buffer.Count = count;
            SkillActionDebug.Query(context,buffer); return count;
        }

        /// <summary>Query an oriented hit volume and capture identities for safe processing across hit callbacks.</summary>
        public int QueryAt(TargetQueryDefinition query, SkillExecutionContext context, TargetQueryBuffer buffer, Vector3 center, Vector3 forward)
            => Query(query, context, buffer, center, true, false, forward);

        /// <param name="maxCandidates">Legacy parameter, kept for source compatibility. Buffer capacity is now per-query.</param>
        public TargetSearcher(int maxCandidates = 64) { }

    }
}
