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

    /// <summary>Host-owned scene services. The library does not create visual objects or own their pool.</summary>
    public sealed class TimelineTaskContext
    {
        public Transform Origin;
        public Transform LaunchPoint;
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
        public TargetSearcher Searcher;
        public ITargetFilter TargetFilter;
        internal bool FilterHasSnapshot;
        public ISkillObjectService Objects;
        public ISkillTargetSelection TargetSelection;
        public ISkillAudioService Audio;
        public ISkillCameraService Camera;
        public ISkillTimeService Time;
        public ISkillRigService Rig;
        public GameplayTaskHost SceneTasks;
        public UnityEngine.SceneManagement.Scene Scene;
        public SkillExecutionContext Execution { get; internal set; }
        public TimelineTaskContext Snapshot() => (TimelineTaskContext)MemberwiseClone();
        internal void RebindTarget(Transform target)
        { Target = target; if (target != null) TargetPosition = target.position; }
        internal void CopyFrom(TimelineTaskContext source)
        {
            Origin = source.Origin; LaunchPoint=source.LaunchPoint; Target = source.Target; _targetPosition = source._targetPosition; HasTargetPosition = source.HasTargetPosition;
            Searcher = source.Searcher;
            TargetFilter = source.TargetFilter; FilterHasSnapshot = source.FilterHasSnapshot; Objects = source.Objects;
            TargetSelection = source.TargetSelection; Audio = source.Audio; Camera=source.Camera; Time=source.Time; Rig=source.Rig; SceneTasks = source.SceneTasks; Scene = source.Scene;
        }
    }

}
