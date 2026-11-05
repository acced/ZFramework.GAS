using System;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public enum TargetQueryKind { Self, FixedTarget, TargetPoint, Circle, Sector, Line }
    public interface ITargetFilter
    {
        bool Allows(AbilitySystemComponent source, AbilitySystemComponent target);
    }
    /// <summary>Implement for pooled casters: capture faction/team values once, without retaining mutable source state.</summary>
    public interface ITargetFilterSnapshot : ITargetFilter
    {
        ITargetFilter Capture(AbilitySystemComponent source);
    }

    [Serializable]
    public sealed class TargetQuery
    {
        public TargetQueryKind kind = TargetQueryKind.FixedTarget;
        public SkillSpawnPosition origin;
        public SkillNumber radius = new SkillNumber(3);
        public SkillNumber angle = new SkillNumber(90);
        public SkillNumber width = new SkillNumber(2);
        public SkillNumber height = new SkillNumber(3);
        public LayerMask layerMask = -1;
        public bool excludeSelf = true;
        [Min(1)] public int maxTargets = 32;
        public GameplayTag[] requiredTags = Array.Empty<GameplayTag>();
        public GameplayTag[] blockedTags = Array.Empty<GameplayTag>();
        public TargetQueryDefinition Build() => new TargetQueryDefinition(kind, radius, angle, width, height,
            layerMask, excludeSelf, maxTargets, TagAssetUtility.ToSet(requiredTags), TagAssetUtility.ToSet(blockedTags),origin);
    }

    public sealed class TargetQueryDefinition
    {
        public TargetQueryKind Kind { get; }
        public readonly SkillNumber Radius, Angle, Width, Height;
        public readonly LayerMask LayerMask;
        public readonly bool ExcludeSelf;
        public readonly int MaxTargets;
        public readonly SkillSpawnPosition Origin;
        internal Vector3 Center(SkillExecutionContext c)=>c.OriginAt(Kind==TargetQueryKind.TargetPoint?SkillSpawnPosition.TargetPoint:Origin);
        private readonly GameplayTagRequirements _requirements;
        public TargetQueryDefinition(TargetQueryKind kind, SkillNumber radius, SkillNumber angle, SkillNumber width,
            SkillNumber height, LayerMask mask, bool excludeSelf, int maxTargets,
            GameplayTagSet required = default, GameplayTagSet blocked = default)
            :this(kind,radius,angle,width,height,mask,excludeSelf,maxTargets,required,blocked,SkillSpawnPosition.Caster){}
        public TargetQueryDefinition(TargetQueryKind kind,SkillNumber radius,SkillNumber angle,SkillNumber width,
            SkillNumber height,LayerMask mask,bool excludeSelf,int maxTargets,GameplayTagSet required,GameplayTagSet blocked,SkillSpawnPosition origin)
        {
            Origin=origin;
            Kind = kind; Radius = radius; Angle = angle; Width = width; Height = height; LayerMask = mask;
            ExcludeSelf = excludeSelf; MaxTargets = Math.Max(1, maxTargets);
            _requirements = GameplayTagRequirements.Create(blocked.AsSpan(), required.AsSpan());
        }
        internal bool Allows(AbilitySystemComponent source, AbilitySystemComponent target, ITargetFilter filter)
            => target != null && (!(ExcludeSelf && Kind != TargetQueryKind.Self) || !ReferenceEquals(source, target))
                && _requirements.Matches(target.OwnedTags) && (filter == null || filter.Allows(source, target));
    }

    public sealed class TargetQueryBuffer
    {
        internal readonly Collider[] Colliders;
        public readonly AbilitySystemComponent[] Units;
        public readonly Transform[] Transforms;
        internal readonly Vector3[] Points;
        internal readonly int[] Generations;
        internal readonly float[] Distances;
        public int Count { get; internal set; }
        public bool Saturated { get; internal set; }
        public TargetQueryBuffer(int capacity = 256)
        {
            Colliders = new Collider[capacity]; Units = new AbilitySystemComponent[capacity];
            Transforms = new Transform[capacity]; Distances = new float[capacity]; Points=new Vector3[capacity]; Generations=new int[capacity];
        }
        internal void Clear()
        {
            Array.Clear(Units, 0, Count); Array.Clear(Transforms, 0, Count);
            Array.Clear(Colliders, 0, Colliders.Length); Count = 0; Saturated = false;
        }
    }
}
