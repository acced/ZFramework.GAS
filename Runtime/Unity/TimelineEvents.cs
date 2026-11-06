using System;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [Serializable]
    public sealed class AnimationTagWindowAsset
    {
        [SerializeField, HideInInspector] private string id;
        public string Id => id;
        [SerializeField] private float start;
        [SerializeField] private float end = -1;
        [SerializeField] private GameplayTag[] tags = Array.Empty<GameplayTag>();
        public AnimationTagWindow Build() => new(start, end, TagAssetUtility.ToSet(tags));
    }

    public readonly struct AnimationTagWindow
    {
        public readonly float Start, End;
        public readonly GameplayTagSet Tags;
        public AnimationTagWindow(float start, float end, GameplayTagSet tags)
        {
            if (float.IsNaN(start) || float.IsInfinity(start) || start < 0) throw new ArgumentOutOfRangeException(nameof(start));
            if (float.IsNaN(end) || float.IsInfinity(end) || end >= 0 && end < start) throw new ArgumentOutOfRangeException(nameof(end));
            Start = start; End = end < 0 ? -1 : end; Tags = tags;
        }
    }

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
        public ISkillAudioService Audio;
        public ISkillCameraService Camera;
        public ISkillTimeService Time;
        public ISkillRigService Rig;
        public GameplayTaskHost SceneTasks;
        public UnityEngine.SceneManagement.Scene Scene;

        internal void CopyServicesFrom(SkillUnitServices source)
        {
            Origin = source.Origin; LaunchPoint = source.LaunchPoint; Searcher = source.Searcher;
            TargetFilter = source.TargetFilter; TargetRelations = source.TargetRelations; Objects = source.Objects;
            TargetSelection = source.TargetSelection; Audio = source.Audio; Camera = source.Camera;
            Time = source.Time; Rig = source.Rig; SceneTasks = source.SceneTasks; Scene = source.Scene;
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
        internal static SkillCastTarget From(AbilitySystemComponent unit, TimelineTaskContext context)
            => new(unit, context?.Target, context?.HasTargetPosition == true ? context.TargetPosition : (Vector3?)null);
    }

    /// <summary>Services and aim for a single execution branch; also accepted by the original casting API.</summary>
    public sealed class TimelineTaskContext : SkillUnitServices
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
        public TimelineTaskContext Snapshot() => (TimelineTaskContext)MemberwiseClone();
        internal void RebindTarget(Transform target)
        { Target = target; if (target != null) TargetPosition = target.position; }
        internal void CopyFrom(TimelineTaskContext source)
        {
            CopyServicesFrom(source);
            Target = source.Target; _targetPosition = source._targetPosition;
            HasTargetPosition = source.HasTargetPosition; FilterHasSnapshot = source.FilterHasSnapshot;
        }
    }

}
