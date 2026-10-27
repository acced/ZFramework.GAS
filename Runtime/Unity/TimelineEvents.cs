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
        public AnimationTaskEvent Build() => new(time, spawner);
    }

    public readonly struct AnimationTaskEvent
    {
        public readonly float Time;
        public readonly TaskSpawner Spawner;
        public AnimationTaskEvent(float time, TaskSpawner spawner)
        {
            if (float.IsNaN(time) || float.IsInfinity(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
            Time = time; Spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
        }
    }

    /// <summary>Host-owned scene services. The library does not create visual objects or own their pool.</summary>
    public sealed class TimelineTaskContext
    {
        public Transform Origin;
        public Transform Target;
        public Vector3 TargetPosition;
        public TargetSearcher Searcher;
        public Func<GameObject, Transform> RentVisual;
        public Action<Transform> ReturnVisual;
    }

    [Serializable]
    public abstract class TaskSpawner
    {
        public abstract void Spawn(GameplayAbilitySpec ability, TimelineTaskContext context);
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
        public override void Spawn(GameplayAbilitySpec ability, TimelineTaskContext context)
        {
            if (context?.Origin == null || context.Searcher == null || context.RentVisual == null || context.ReturnVisual == null)
                throw new InvalidOperationException("ProjectileSpawner requires Origin, Searcher and visual pool callbacks.");
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
        public override void Spawn(GameplayAbilitySpec ability, TimelineTaskContext context)
        {
            if (context?.Origin == null || context.Searcher == null) throw new InvalidOperationException("AreaEffectSpawner requires Origin and Searcher.");
            var config = new AreaEffectConfig { Position = context.Origin.position, Radius = radius, Duration = duration,
                CollisionMask = collisionMask, RemoveOnExit = removeOnExit, EnterEffect = enterEffect != null ? enterEffect.Definition : null };
            ability.Owner.StartTask(ability, new AreaEffectTask(config, context.Searcher, ability.Owner.Attributes, ability.Owner, maxOccupants));
        }
    }
}
