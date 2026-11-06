using System;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public enum TargetQueryKind { Self, FixedTarget, TargetPoint, Circle, Sector, Line }
    public enum TargetRelation { [InspectorName("不限")] Any, [InspectorName("友方")] Ally, [InspectorName("敌方")] Enemy }
    /// <summary>The source is the identity of the original cast, even after its unit is recycled.</summary>
    public delegate bool SkillTargetRelationFilter(AbilitySourceIdentity source, AbilitySystemComponent target, TargetRelation relation);
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
        [InspectorName("目标关系"), Tooltip("敌方和友方由单位接入的阵营服务判断。未接入时，只能使用「不限」。")]
        public TargetRelation relation;
        public SkillSpawnPosition origin;
        public SkillNumber radius = new SkillNumber(3);
        public SkillNumber angle = new SkillNumber(90);
        public SkillNumber width = new SkillNumber(2);
        public SkillNumber height = new SkillNumber(3);
        public LayerMask layerMask = -1;
        public bool excludeSelf = true;
        [Min(1)] public int maxTargets = 32;
        public GameplayTag[] anyTags = Array.Empty<GameplayTag>();
        public GameplayTag[] requiredTags = Array.Empty<GameplayTag>();
        public GameplayTag[] blockedTags = Array.Empty<GameplayTag>();
        public TargetQueryDefinition Build() => new TargetQueryDefinition(kind, radius, angle, width, height,
            layerMask, excludeSelf, maxTargets, TagAssetUtility.ToSet(requiredTags), TagAssetUtility.ToSet(blockedTags),origin,TagAssetUtility.ToSet(anyTags),relation);
    }

    public sealed class TargetQueryDefinition
    {
        public TargetQueryKind Kind { get; }
        public readonly SkillNumber Radius, Angle, Width, Height;
        public readonly LayerMask LayerMask;
        public readonly bool ExcludeSelf;
        public readonly int MaxTargets;
        public readonly SkillSpawnPosition Origin;
        public readonly TargetRelation Relation;
        public SkillSpawnPosition EffectiveOrigin => Kind == TargetQueryKind.TargetPoint ? SkillSpawnPosition.TargetPoint : Origin;
        public Vector3 Center(SkillExecutionContext c) => c.OriginAt(EffectiveOrigin);
        private readonly GameplayTagRequirements _requirements;
        private readonly GameplayTagSet _any;
        public TargetQueryDefinition(TargetQueryKind kind, SkillNumber radius, SkillNumber angle, SkillNumber width,
            SkillNumber height, LayerMask mask, bool excludeSelf, int maxTargets,
            GameplayTagSet required = default, GameplayTagSet blocked = default)
            :this(kind,radius,angle,width,height,mask,excludeSelf,maxTargets,required,blocked,SkillSpawnPosition.Caster){}
        public TargetQueryDefinition(TargetQueryKind kind,SkillNumber radius,SkillNumber angle,SkillNumber width,
            SkillNumber height,LayerMask mask,bool excludeSelf,int maxTargets,GameplayTagSet required,GameplayTagSet blocked,SkillSpawnPosition origin, GameplayTagSet any = default)
            : this(kind,radius,angle,width,height,mask,excludeSelf,maxTargets,required,blocked,origin,any,TargetRelation.Any) { }
        public TargetQueryDefinition(TargetQueryKind kind, SkillNumber radius, SkillNumber angle, SkillNumber width,
            SkillNumber height, LayerMask mask, bool excludeSelf, int maxTargets, GameplayTagSet required, GameplayTagSet blocked, SkillSpawnPosition origin, GameplayTagSet any, TargetRelation relation)
        {
            _any=any; Origin=origin; Relation=relation;
            Kind = kind; Radius = radius; Angle = angle; Width = width; Height = height; LayerMask = mask;
            ExcludeSelf = excludeSelf; MaxTargets = Math.Max(1, maxTargets);
            _requirements = GameplayTagRequirements.Create(blocked.AsSpan(), required.AsSpan());
        }
        public bool MatchesTags(GameplayTagCountContainer tags)
            => (_any.IsEmpty || _any.HasAny(tags)) && _requirements.Matches(tags);
        public TargetShape ResolveShape(EffectEvaluationContext values) => new TargetShape(this, values);
        internal bool Allows(AbilitySourceIdentity source, AbilitySystemComponent target, SkillUnitServices services)
            => target != null && (!(ExcludeSelf && Kind != TargetQueryKind.Self) || source != target.SourceIdentity)
                && MatchesTags(target.OwnedTags)
                && (Relation == TargetRelation.Any || services.TargetRelations?.Invoke(source, target, Relation) == true)
                && (services.TargetFilter == null || services.TargetFilter.Allows(source.Owner, target));
    }

    /// <summary>Local coordinates: +Z forward. Radius is length for a line; a sector uses the unit's XZ direction.</summary>
    public readonly struct TargetShape
    {
        public readonly TargetQueryKind Kind;
        public readonly float Radius, Angle;
        public readonly Vector3 HalfExtents;
        private readonly float _cosHalfAngle;
        internal TargetShape(TargetQueryDefinition query, EffectEvaluationContext values)
        {
            Kind = query.Kind;
            Radius = Kind >= TargetQueryKind.TargetPoint ? query.Radius.Resolve(values) : 0;
            Angle = Kind == TargetQueryKind.Sector ? query.Angle.Resolve(values) : 360;
            _cosHalfAngle = Kind == TargetQueryKind.Sector ? Mathf.Cos(Angle * .5f * Mathf.Deg2Rad) : -1;
            HalfExtents = Kind == TargetQueryKind.Line
                ? new Vector3(query.Width.Resolve(values), query.Height.Resolve(values), Radius) * .5f : default;
        }
        public bool IncludesDirection(Vector2 facing, Vector3 offset)
        {
            if (Kind != TargetQueryKind.Sector) return true;
            var flat = new Vector2(offset.x, offset.z);
            return flat.sqrMagnitude <= 1e-8f || Vector2.Dot(flat.normalized, facing) >= _cosHalfAngle;
        }
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
