using UnityEngine;

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

    /// <summary>
    /// 基于 Unity 3D 物理的目标查询。通过 <see cref="TargetQueryBuffer"/> 缓冲碰撞体，
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
        /// <summary>Unified query. Each nesting level leases its own buffer; order is nearest first.</summary>
        public int Query(TargetQueryDefinition query, SkillExecutionContext context, TargetQueryBuffer buffer,
            Vector3? centerOverride = null, bool applyLimit = true)
            =>Query(query,context,buffer,centerOverride,applyLimit,false);

        internal int Query(TargetQueryDefinition query,SkillExecutionContext context,TargetQueryBuffer buffer,
            Vector3? centerOverride,bool applyLimit,bool captureAim)
        {
            buffer.Clear();
            var source = context.Source;
            if (query.Kind == TargetQueryKind.Self || query.Kind == TargetQueryKind.FixedTarget)
            {
                var unit = query.Kind == TargetQueryKind.Self ? source : context.Target;
                if (context.Allows(query, unit))
                {
                    buffer.Units[0]=unit;buffer.Transforms[0]=query.Kind==TargetQueryKind.Self?context.Services.Origin:context.Services.Target;buffer.Count=1;
                    if(captureAim){buffer.Points[0]=buffer.Transforms[0]!=null?buffer.Transforms[0].position:context.Position;buffer.Generations[0]=unit.Generation;}
                }
                SkillActionDebug.Query(context,buffer); return buffer.Count;
            }
            var center=centerOverride??query.Center(context);
            var shape = query.ResolveShape(context.Values);
            var forward = context.Forward;
            int hits = query.Kind == TargetQueryKind.Line
                ? Physics.OverlapBoxNonAlloc(center + forward * shape.HalfExtents.z, shape.HalfExtents,
                    buffer.Colliders, Quaternion.LookRotation(forward), query.LayerMask, QueryTriggerInteraction.Collide)
                : Physics.OverlapSphereNonAlloc(center, shape.Radius, buffer.Colliders, query.LayerMask, QueryTriggerInteraction.Collide);
            buffer.Saturated = hits == buffer.Colliders.Length;
            var flatForward = new Vector2(forward.x, forward.z).normalized;
            for (int i = 0; i < hits; i++)
            {
                var collider = buffer.Colliders[i];
                var provider = collider.GetComponentInParent<IAbilitySystemProvider>();
                var unit = provider?.AbilitySystem;
                if (!context.Allows(query, unit) || Contains(buffer.Units, buffer.Count, unit)) continue;
                var transform = provider is Component component ? component.transform : collider.transform;
                var delta = transform.position - center;
                if (!shape.IncludesDirection(flatForward, delta)) continue;
                float distance = delta.sqrMagnitude;
                int index = buffer.Count;
                while (index > 0 && buffer.Distances[index - 1] > distance)
                {
                    buffer.Units[index] = buffer.Units[index - 1]; buffer.Transforms[index] = buffer.Transforms[index - 1];
                    buffer.Distances[index] = buffer.Distances[index - 1];
                    if(captureAim){buffer.Points[index]=buffer.Points[index-1];buffer.Generations[index]=buffer.Generations[index-1];}
                    index--;
                }
                buffer.Units[index] = unit; buffer.Transforms[index] = transform; buffer.Distances[index] = distance;
                if(captureAim){buffer.Points[index]=collider.bounds.center;buffer.Generations[index]=unit.Generation;}
                buffer.Count++;
            }
            int count = applyLimit ? Mathf.Min(buffer.Count, query.MaxTargets) : buffer.Count;
            for (int i = count; i < buffer.Count; i++) { buffer.Units[i] = null; buffer.Transforms[i] = null; }
            buffer.Count = count;
            SkillActionDebug.Query(context,buffer); return count;
        }

        /// <param name="maxCandidates">Legacy parameter, kept for source compatibility. Buffer capacity is now per-query.</param>
        public TargetSearcher(int maxCandidates = 64) { }

        /// <summary>
        /// 一个实体可能挂多个碰撞体，去重按 <see cref="AbilitySystemComponent"/> 引用进行。
        /// 目标数量在个位到几十量级，线性扫描比建哈希集合更快且不分配。
        /// </summary>
        private static bool Contains(AbilitySystemComponent[] results, int count, AbilitySystemComponent asc)
        {
            for (int i = 0; i < count; i++)
                if (ReferenceEquals(results[i], asc))
                    return true;
            return false;
        }
    }
}
