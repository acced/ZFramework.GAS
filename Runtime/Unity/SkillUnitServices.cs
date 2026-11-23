using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    /// <summary>单位级服务配置；单次施放复制引用，扩展服务写时复制。</summary>
    public class SkillUnitServices
    {
        public Transform Origin;
        public TargetSearcher Searcher;
        public ITargetFilter TargetFilter;
        public SkillTargetRelationFilter TargetRelations;
        public ISkillTargetSelection TargetSelection;
        public GameplayTaskHost SceneTasks;
        public Scene Scene;

        private Dictionary<Type, object> _extensions;
        private bool _extensionsShared;

        public T Get<T>() where T : class
            => _extensions != null && _extensions.TryGetValue(typeof(T), out object service) ? (T)service : null;

        public void Set<T>(T service) where T : class
        {
            if (_extensions == null) _extensions = new Dictionary<Type, object>();
            else if (_extensionsShared) _extensions = new Dictionary<Type, object>(_extensions);
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
            Origin = source.Origin; Searcher = source.Searcher; TargetFilter = source.TargetFilter;
            TargetRelations = source.TargetRelations; TargetSelection = source.TargetSelection;
            SceneTasks = source.SceneTasks; Scene = source.Scene;
            source.ShareExtensionsWith(this);
        }
    }

    public readonly struct SkillCastTarget
    {
        public readonly AbilitySystemComponent Unit;
        public readonly Transform Aim;
        public readonly Vector3? Point;
        public readonly Vector3? Direction;

        public SkillCastTarget(AbilitySystemComponent unit, Transform aim = null, Vector3? point = null, Vector3? direction = null)
        {
            Unit = unit; Aim = aim; Point = point; Direction = direction;
        }

        public static SkillCastTarget Ground(Vector3 point) => new(null, point: point);
        public static SkillCastTarget Directional(Vector3 direction) => new(null, direction: direction);
    }

    public sealed class SkillExecutionServices : SkillUnitServices
    {
        public Transform Target;
        private Vector3 _targetPosition;
        public bool HasTargetPosition { get; private set; }
        public SkillExecutionContext Execution { get; internal set; }

        public Vector3 TargetPosition
        {
            get => _targetPosition;
            set { _targetPosition = value; HasTargetPosition = true; }
        }

        public void ClearTargetPosition() { _targetPosition = default; HasTargetPosition = false; }

        public Vector3 RequireTargetPosition() => HasTargetPosition &&
            float.IsFinite(_targetPosition.x) && float.IsFinite(_targetPosition.y) && float.IsFinite(_targetPosition.z)
            ? _targetPosition : throw new InvalidOperationException("缺少有效的目标点（需要显式设置 TargetPosition）。");

        internal void RebindTarget(Transform target)
        {
            Target = target;
            ClearTargetPosition();
            if (target != null) TargetPosition = target.position;
        }

        internal void CopyFrom(SkillExecutionServices source)
        {
            CopyServicesFrom(source);
            Target = source.Target;
            _targetPosition = source._targetPosition;
            HasTargetPosition = source.HasTargetPosition;
        }
    }
}
