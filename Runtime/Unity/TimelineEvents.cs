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

    [Serializable]
    public sealed class AnimationTaskEventAsset
    {
        [SerializeField, HideInInspector] private string id;
        public string Id => id;
        [SerializeField] private float time;
        [SerializeReference] private TaskSpawner spawner;
        public AnimationTaskEvent Build() => new(time, spawner?.Snapshot(), id, spawner?.Build());
    }

    public readonly struct AnimationTaskEvent
    {
        public readonly float Time;
        public readonly TaskSpawner Spawner;
        public readonly string Id;
        public readonly SkillActionDefinition Action;
        public AnimationTaskEvent(float time, TaskSpawner spawner, string id = null, SkillActionDefinition action = null)
        {
            if (float.IsNaN(time) || float.IsInfinity(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
            Time = time; Spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            Id = id; Action = action;
        }
    }

    /// <summary>Host-owned scene services. The library does not create visual objects or own their pool.</summary>
    public sealed class TimelineTaskContext
    {
        public Transform Origin;
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
        public Func<GameObject, Transform> RentVisual;
        public Action<Transform> ReturnVisual;
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
            Origin = source.Origin; Target = source.Target; _targetPosition = source._targetPosition; HasTargetPosition = source.HasTargetPosition;
            Searcher = source.Searcher; RentVisual = source.RentVisual; ReturnVisual = source.ReturnVisual;
            TargetFilter = source.TargetFilter; FilterHasSnapshot = source.FilterHasSnapshot; Objects = source.Objects;
            TargetSelection = source.TargetSelection; Audio = source.Audio; Camera=source.Camera; Time=source.Time; Rig=source.Rig; SceneTasks = source.SceneTasks; Scene = source.Scene;
        }
    }

    [Serializable]
    public abstract class TaskSpawner
    {
        public abstract void Spawn(GameplayAbilitySpec ability, TimelineTaskContext context);
        /// <summary>Custom spawners with mutable child configuration should override and deep-copy it.</summary>
        public virtual TaskSpawner Snapshot() => (TaskSpawner)MemberwiseClone();
        public virtual SkillActionDefinition Build()
        {
            var frozen = Snapshot();
            return new DelegateSkillAction(null, "任务", null, context => frozen.Spawn(context.Ability, context.Services));
        }
    }

    [Serializable]
    public sealed class ProjectileSpawner : TaskSpawner
    {
        public GameObject visualPrefab;
        public GameplayEffectAsset impactEffect;
        [Min(.01f)] public float speed = 10;
        [Min(0)] public float radius = .1f;
        public LayerMask collisionMask = -1;
        public float curveHeight;
        public float maxDistance = 30;
        public LaunchProjectileAction ToAction() => new LaunchProjectileAction
        {
            prefab = visualPrefab, speed = new SkillNumber(speed), radius = radius, arcHeight = curveHeight,
            lifetime = new SkillNumber(maxDistance > 0 ? maxDistance / Math.Max(.01f, speed) : 10),
            ownership = SkillTaskOwnership.Ability,
            collision = new TargetQuery { kind = TargetQueryKind.Circle, layerMask = collisionMask },
            onHit = impactEffect != null ? new SkillAction[] { new ApplyEffectAction { effect = impactEffect } } : Array.Empty<SkillAction>()
        };
        public override SkillActionDefinition Build()=>ToAction().Build(new SkillActionBuildContext());
        public override void Spawn(GameplayAbilitySpec ability, TimelineTaskContext context)
        {
            if (context?.Execution != null) { context.Execution.ExecuteAction(Build()); return; }
            if (context?.Origin == null || context.Searcher == null || context.RentVisual == null || context.ReturnVisual == null)
                throw new InvalidOperationException("ProjectileSpawner requires Origin, Searcher and visual pool callbacks.");
            if (context.Target == null) context.RequireTargetPosition();
            var visual = context.RentVisual(visualPrefab);
            if (visual == null) throw new InvalidOperationException("The projectile visual pool returned null.");
            if (!ability.IsActive) { context.ReturnVisual(visual); return; }
            visual.position = context.Origin.position;
            try
            {
                var config = new ProjectileConfig { Visual = visual, Aim = context.Target != null ? ProjectileAim.Unit : ProjectileAim.Position,
                    TargetUnit = context.Target, TargetPosition = context.TargetPosition, Speed = speed, CollisionRadius = radius,
                    CollisionMask = collisionMask, CurveHeight = curveHeight, MaxDistance = maxDistance, ImpactEffect = impactEffect != null ? impactEffect.Definition : null };
                ability.Owner.StartTask(ability, new ProjectileTask(config, context.Searcher, ability.Owner.Attributes, ability.Owner,
                    task => context.ReturnVisual(task.Visual)));
            }
            catch { context.ReturnVisual(visual); throw; }
        }
    }

    [Serializable]
    public sealed class AreaEffectSpawner : TaskSpawner
    {
        public GameplayEffectAsset enterEffect;
        [Min(.01f)] public float radius = 3;
        public float duration = 3;
        public LayerMask collisionMask = -1;
        public bool removeOnExit = true;
        [Min(1)] public int maxOccupants = 32;
        public override SkillActionDefinition Build()
        {
            var frozen = (AreaEffectSpawner)Snapshot();
            var effect = enterEffect != null ? enterEffect.Definition : null;
            return new DelegateSkillAction(null, "区域任务（技能归属）", (execution, _) =>
            {
                if (effect == null || frozen.maxOccupants < 1) throw new InvalidOperationException("区域缺少效果或无效缓冲容量。");
                SkillSpawn.Positive(frozen.radius, "区域半径");
                if (!float.IsFinite(frozen.duration)) throw new InvalidOperationException("区域时长必须为有限数。");
                if (execution.Services.Origin == null) throw new InvalidOperationException("区域缺少 Origin。");
                SkillActionBuildContext.Effect(effect, execution, execution.Target?.Attributes);
            }, execution =>
            {
                var config = new AreaEffectConfig { Position = execution.Position, Radius = frozen.radius, Duration = frozen.duration,
                    CollisionMask = frozen.collisionMask, RemoveOnExit = frozen.removeOnExit, EnterEffect = effect };
                execution.Start(new AreaEffectTask(config, execution.Services.Searcher, execution.Source?.Attributes, execution.SourceIdentity,
                    frozen.maxOccupants, execution.Values), false);
            });
        }
        public override void Spawn(GameplayAbilitySpec ability, TimelineTaskContext context)
        {
            if (context?.Execution != null) { context.Execution.ExecuteAction(Build()); return; }
            if (context?.Origin == null || context.Searcher == null) throw new InvalidOperationException("AreaEffectSpawner requires Origin and Searcher.");
            var config = new AreaEffectConfig { Position = context.Origin.position, Radius = radius, Duration = duration,
                CollisionMask = collisionMask, RemoveOnExit = removeOnExit, EnterEffect = enterEffect != null ? enterEffect.Definition : null };
            ability.Owner.StartTask(ability, new AreaEffectTask(config, context.Searcher, ability.Owner.Attributes, ability.Owner, maxOccupants));
        }
    }
}
