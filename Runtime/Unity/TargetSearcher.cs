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
    /// 基于 Unity 3D 物理的目标查询。
    ///
    /// 实例持有碰撞体缓冲区，因此查询过程零分配；同时避免了静态缓冲区的重入问题——
    /// 需要嵌套查询时各自持有一个 searcher 即可。
    ///
    /// 扇形的角度判定在 **XZ 平面**上进行（忽略高度差），以适配地面战斗；
    /// 距离与重叠检测仍是三维的。
    ///
    /// 查询看到的是**上一次物理同步时**的碰撞体位置。项目若关闭了
    /// <see cref="Physics.autoSyncTransforms"/>（Unity 的默认值就是关），
    /// 当帧内用代码挪动的 Transform 不会反映到查询结果里，直到下一次
    /// <c>FixedUpdate</c> 或显式 <see cref="Physics.SyncTransforms"/>。
    /// 这是 Unity 所有物理查询的统一行为，本类不代为同步——那会让整个项目为每次查询付出代价。
    /// </summary>
    public sealed class TargetSearcher
    {
        private readonly Collider[] _candidates;

        public TargetSearcher(int maxCandidates = 64)
        {
            if (maxCandidates <= 0)
            {
                throw new System.ArgumentOutOfRangeException(nameof(maxCandidates));
            }

            _candidates = new Collider[maxCandidates];
        }

        /// <summary>
        /// 圆形（球形）范围查询。返回写入 <paramref name="results"/> 的目标数量，
        /// 上限为 <c>results.Length</c> 与构造时的候选上限二者中的较小值。
        /// </summary>
        public int Circle(
            Vector3 center,
            float radius,
            LayerMask layerMask,
            AbilitySystemComponent[] results,
            AbilitySystemComponent exclude = null)
        {
            int hits = Physics.OverlapSphereNonAlloc(center, radius, _candidates, layerMask, QueryTriggerInteraction.Collide);
            return Collect(hits, results, exclude);
        }

        /// <summary>扇形范围查询。<paramref name="angleDegrees"/> 是**全角**，即前方左右各占一半。</summary>
        public int Sector(
            Vector3 center,
            Vector3 forward,
            float radius,
            float angleDegrees,
            LayerMask layerMask,
            AbilitySystemComponent[] results,
            AbilitySystemComponent exclude = null)
        {
            int hits = Physics.OverlapSphereNonAlloc(center, radius, _candidates, layerMask, QueryTriggerInteraction.Collide);

            Vector2 facing = new Vector2(forward.x, forward.z);
            if (facing.sqrMagnitude < 1e-8f)
            {
                return 0;
            }

            facing.Normalize();
            float cosHalf = Mathf.Cos(angleDegrees * 0.5f * Mathf.Deg2Rad);

            int count = 0;
            for (int i = 0; i < hits && count < results.Length; i++)
            {
                AbilitySystemComponent asc = Resolve(_candidates[i]);
                if (asc == null || ReferenceEquals(asc, exclude) || Contains(results, count, asc))
                {
                    continue;
                }

                Vector3 position = _candidates[i].transform.position;
                Vector2 offset = new Vector2(position.x - center.x, position.z - center.z);
                // 圆心处的目标没有方向可言，视为落在扇形内。
                if (offset.sqrMagnitude > 1e-8f && Vector2.Dot(offset.normalized, facing) < cosHalf)
                {
                    continue;
                }

                results[count++] = asc;
            }

            return count;
        }

        /// <summary>
        /// 线形（矩形）范围查询，<paramref name="width"/> 是总宽度，以 <c>start→end</c> 为中轴。
        /// </summary>
        public int Line(
            Vector3 start,
            Vector3 end,
            float width,
            float height,
            LayerMask layerMask,
            AbilitySystemComponent[] results,
            AbilitySystemComponent exclude = null)
        {
            Vector3 delta = end - start;
            float length = delta.magnitude;
            if (length < 1e-6f)
            {
                return 0;
            }

            Vector3 center = start + delta * 0.5f;
            var halfExtents = new Vector3(width * 0.5f, height * 0.5f, length * 0.5f);
            Quaternion orientation = Quaternion.LookRotation(delta / length, Vector3.up);

            int hits = Physics.OverlapBoxNonAlloc(
                center, halfExtents, _candidates, orientation, layerMask, QueryTriggerInteraction.Collide);
            return Collect(hits, results, exclude);
        }

        private int Collect(int hits, AbilitySystemComponent[] results, AbilitySystemComponent exclude)
        {
            int count = 0;
            for (int i = 0; i < hits && count < results.Length; i++)
            {
                AbilitySystemComponent asc = Resolve(_candidates[i]);
                if (asc == null || ReferenceEquals(asc, exclude) || Contains(results, count, asc))
                {
                    continue;
                }

                results[count++] = asc;
            }

            return count;
        }

        /// <summary>
        /// 一个实体可能挂多个碰撞体，去重按 <see cref="AbilitySystemComponent"/> 引用进行。
        /// 目标数量在个位到几十量级，线性扫描比建哈希集合更快且不分配。
        /// </summary>
        private static bool Contains(AbilitySystemComponent[] results, int count, AbilitySystemComponent asc)
        {
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(results[i], asc))
                {
                    return true;
                }
            }

            return false;
        }

        private static AbilitySystemComponent Resolve(Collider collider)
        {
            if (collider == null)
            {
                return null;
            }

            // 碰撞体常挂在子物体上，沿父链向上找第一个提供者。
            var provider = collider.GetComponentInParent<IAbilitySystemProvider>();
            return provider?.AbilitySystem;
        }
    }
}
