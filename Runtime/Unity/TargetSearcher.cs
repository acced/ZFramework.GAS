using System;
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
        public SkillTargetResults(SkillExecutionContext cast, TargetQueryDefinition query, TargetQueryBuffer b)
        { _cast = cast; _query = query; _b = b; }
        public void Add(AbilitySystemComponent unit, Transform aim = null, Vector3? point = null)
            => AddNearest(unit, aim, point ?? (aim != null ? aim.position : (Vector3?)null), 0);
        public void MarkSaturated() => _b.Saturated = true;
        public void AddNearest(AbilitySystemComponent unit, Transform aim, Vector3? point, float distance)
        {
            if (unit == null) return;
            int generation = unit.Generation;
            if (!_cast.Allows(_query, unit) || unit.Generation != generation) return;
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
            _b.Distances[index] = distance; _b.Generations[index] = generation;
        }
    }

    /// <summary>Identity, filtering and ordering shared by every target source. Spatial queries are supplied by a host adapter.</summary>
    public class TargetSearcher
    {
        public virtual TargetQueryBuffer CreateBuffer(int capacity = 256) => new TargetQueryBuffer(capacity);

        protected virtual void Collect(TargetQueryDefinition query, SkillExecutionContext context,
            TargetQueryBuffer buffer, Vector3? center, Vector3 forward)
            => throw new InvalidOperationException("空间查询需要显式注入 TargetSearcher 实现，例如 PhysicsTargetSearcher。");

        /// <summary>Physical results are nearest first; project results retain the supplied priority.</summary>
        public int Query(TargetQueryDefinition query, SkillExecutionContext context, TargetQueryBuffer buffer,
            Vector3? centerOverride = null, Vector3? forwardOverride = null)
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
            else Collect(query, context, buffer, centerOverride, forwardOverride ?? context.Forward);
            int count = Mathf.Min(buffer.Count, query.MaxTargets);
            for (int i = count; i < buffer.Count; i++) { buffer.Units[i] = null; buffer.Transforms[i] = null; }
            buffer.Count = count;
            SkillActionDebug.Query(context,buffer); return count;
        }

    }
}
