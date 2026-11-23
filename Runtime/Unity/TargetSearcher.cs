using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public interface IAbilitySystemProvider
    {
        AbilitySystemComponent AbilitySystem { get; }
    }

    public interface ISkillTargetSource
    {
        void Collect(string key, SkillExecutionContext cast, SkillTargetResults results);
    }

    public readonly ref struct SkillTargetResults
    {
        private readonly SkillExecutionContext _cast;
        private readonly TargetQueryDefinition _query;
        private readonly TargetQueryBuffer _buffer;
        private readonly int _limit;

        public SkillTargetResults(SkillExecutionContext cast, TargetQueryDefinition query, TargetQueryBuffer buffer)
        {
            _cast = cast; _query = query; _buffer = buffer;
            _limit = Math.Min(buffer.Units.Length, Math.Max(1, query.MaxTargets));
        }

        public void Add(AbilitySystemComponent unit, Transform aim = null, Vector3? point = null)
            => AddNearest(unit, aim, point ?? (aim != null ? aim.position : (Vector3?)null), 0f);

        public void MarkSaturated() => _buffer.Saturated = true;

        public void AddNearest(AbilitySystemComponent unit, Transform aim, Vector3? point, float distance)
        {
            if (unit == null) return;
            int count = _buffer.Count;
            if (count == _limit && distance >= _buffer.Distances[_limit - 1])
            {
                if (_limit == _buffer.Units.Length) _buffer.Saturated = true;
                return;
            }
            int generation = unit.Generation;
            if (!_cast.Allows(_query, unit) || unit.Generation != generation) return;

            for (int i = 0; i < count; i++)
            {
                if (!ReferenceEquals(_buffer.Units[i], unit)) continue;
                if (distance < _buffer.Distances[i]) BubbleUp(i, unit, aim, point, distance, generation);
                return;
            }

            int index = count < _limit ? _buffer.Count++ : _limit - 1;
            if (count == _limit && _limit == _buffer.Units.Length) _buffer.Saturated = true;
            BubbleUp(index, unit, aim, point, distance, generation);
        }

        private void BubbleUp(int index, AbilitySystemComponent unit, Transform aim, Vector3? point, float distance, int generation)
        {
            while (index > 0 && _buffer.Distances[index - 1] > distance)
            {
                _buffer.Units[index] = _buffer.Units[index - 1];
                _buffer.Transforms[index] = _buffer.Transforms[index - 1];
                _buffer.Distances[index] = _buffer.Distances[index - 1];
                _buffer.Generations[index] = _buffer.Generations[index - 1];
                _buffer.Points[index] = _buffer.Points[index - 1];
                index--;
            }
            _buffer.Units[index] = unit; _buffer.Transforms[index] = aim;
            _buffer.Points[index] = point; _buffer.Distances[index] = distance; _buffer.Generations[index] = generation;
        }
    }

    public class TargetSearcher
    {
        public virtual TargetQueryBuffer CreateBuffer(int capacity = 256) => new(capacity);

        protected virtual void Collect(TargetQueryDefinition query, SkillExecutionContext context,
            TargetQueryBuffer buffer, Vector3? center, Vector3 forward)
            => throw new InvalidOperationException("空间查询需要显式注入 TargetSearcher 实现，例如 PhysicsTargetSearcher。");

        public int Query(TargetQueryDefinition query, SkillExecutionContext context, TargetQueryBuffer buffer,
            Vector3? centerOverride = null, Vector3? forwardOverride = null)
        {
            buffer.Clear();
            var results = new SkillTargetResults(context, query, buffer);
            if (query.Kind is TargetQueryKind.Self or TargetQueryKind.FixedTarget)
            {
                bool self = query.Kind == TargetQueryKind.Self;
                results.Add(self ? context.Source : context.Target,
                    self ? context.Services.Origin : context.Services.Target,
                    !self && context.Services.HasTargetPosition ? context.Services.TargetPosition : (Vector3?)null);
            }
            else if (query.Kind == TargetQueryKind.Project)
            {
                (context.Services.Get<ISkillTargetSource>()
                    ?? throw new InvalidOperationException("没有接入项目目标来源：" + query.SourceKey))
                    .Collect(query.SourceKey, context, results);
            }
            else Collect(query, context, buffer, centerOverride, forwardOverride ?? context.Forward);

            SkillActionDebug.Query(context, buffer);
            return buffer.Count;
        }
    }
}
