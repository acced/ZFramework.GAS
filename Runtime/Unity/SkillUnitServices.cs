using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>Bind once per unit. Each cast copies these service references into its own context.</summary>
    public class SkillUnitServices
    {
        public Transform Origin;
        public Transform LaunchPoint;
        public TargetSearcher Searcher;
        public ITargetFilter TargetFilter;
        public SkillTargetRelationFilter TargetRelations;
        public ISkillObjectService Objects;
        public ISkillTargetSelection TargetSelection;
        public GameplayTaskHost SceneTasks;
        public UnityEngine.SceneManagement.Scene Scene;

        // 扩展服务（镜头、音效、回合队列……）按接口类型存取，基础层不需要认识它们。
        // 写时复制：施放分支覆盖某个服务时，不会改到单位共享的那一份，也不必在每次分支时分配。
        private System.Collections.Generic.Dictionary<Type, object> _extensions;
        private bool _extensionsShared;
        public T Get<T>() where T : class
            => _extensions != null && _extensions.TryGetValue(typeof(T), out var service) ? (T)service : null;
        public void Set<T>(T service) where T : class
        {
            if (_extensions == null) _extensions = new System.Collections.Generic.Dictionary<Type, object>();
            else if (_extensionsShared) _extensions = new System.Collections.Generic.Dictionary<Type, object>(_extensions);
            _extensionsShared = false;
            if (service == null) _extensions.Remove(typeof(T)); else _extensions[typeof(T)] = service;
        }
        internal void ShareExtensionsWith(SkillUnitServices copy)
        {
            copy._extensions = _extensions;
            copy._extensionsShared = _extensionsShared = _extensions != null;
        }

        internal void CopyServicesFrom(SkillUnitServices source)
        {
            Origin = source.Origin; LaunchPoint = source.LaunchPoint; Searcher = source.Searcher;
            TargetFilter = source.TargetFilter; TargetRelations = source.TargetRelations; Objects = source.Objects;
            TargetSelection = source.TargetSelection; SceneTasks = source.SceneTasks; Scene = source.Scene;
            source.ShareExtensionsWith(this);
        }
    }

    /// <summary>Per-cast input. An explicit point, including world zero, takes precedence over the aim transform.</summary>
    public readonly struct SkillCastTarget
    {
        public readonly AbilitySystemComponent Unit;
        public readonly Transform Aim;
        public readonly Vector3? Point;
        public SkillCastTarget(AbilitySystemComponent unit, Transform aim = null, Vector3? point = null)
        { Unit = unit; Aim = aim; Point = point; }
        public static SkillCastTarget Ground(Vector3 point) => new(null, point: point);
        internal static SkillCastTarget From(AbilitySystemComponent unit, SkillExecutionServices context)
            => new(unit, context?.Target, context?.HasTargetPosition == true ? context.TargetPosition : (Vector3?)null);
    }

    /// <summary>Services and target input captured by one execution branch.</summary>
    public sealed class SkillExecutionServices : SkillUnitServices
    {
        public Transform Target;
        private Vector3 _targetPosition;
        public bool HasTargetPosition { get; private set; }
        public Vector3 TargetPosition { get => _targetPosition; set { _targetPosition = value; HasTargetPosition = true; } }
        public void ClearTargetPosition() { _targetPosition = default; HasTargetPosition = false; }
        public Vector3 RequireTargetPosition()
        {
            if (!HasTargetPosition || !float.IsFinite(_targetPosition.x) || !float.IsFinite(_targetPosition.y) || !float.IsFinite(_targetPosition.z))
                throw new InvalidOperationException("缺少有效的目标点（需要显式设置 TargetPosition）。");
            return _targetPosition;
        }
        internal bool FilterHasSnapshot;
        public SkillExecutionContext Execution { get; internal set; }
        public SkillExecutionServices Snapshot()
        {
            var copy = (SkillExecutionServices)MemberwiseClone();
            ShareExtensionsWith(copy);
            return copy;
        }
        internal void RebindTarget(Transform target)
        { Target = target; if (target != null) TargetPosition = target.position; }
        public void CopyFrom(SkillExecutionServices source, bool includeExecution = false)
        {
            CopyServicesFrom(source);
            Target = source.Target; _targetPosition = source._targetPosition;
            HasTargetPosition = source.HasTargetPosition; FilterHasSnapshot = source.FilterHasSnapshot;
            if (includeExecution) Execution = source.Execution;
        }
    }

}
