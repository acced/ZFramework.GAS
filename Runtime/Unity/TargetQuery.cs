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
            layerMask, excludeSelf, maxTargets, TagAssetUtility.ToSet(requiredTags), TagAssetUtility.ToSet(blockedTags));
    }

    public sealed class TargetQueryDefinition
    {
        public TargetQueryKind Kind { get; }
        public readonly SkillNumber Radius, Angle, Width, Height;
        public readonly LayerMask LayerMask;
        public readonly bool ExcludeSelf;
        public readonly int MaxTargets;
        private readonly GameplayTagRequirements _requirements;
        public TargetQueryDefinition(TargetQueryKind kind, SkillNumber radius, SkillNumber angle, SkillNumber width,
            SkillNumber height, LayerMask mask, bool excludeSelf, int maxTargets,
            GameplayTagSet required = default, GameplayTagSet blocked = default)
        {
            Kind = kind; Radius = radius; Angle = angle; Width = width; Height = height; LayerMask = mask;
            ExcludeSelf = excludeSelf; MaxTargets = Math.Max(1, maxTargets);
            _requirements = GameplayTagRequirements.Create(blocked.AsSpan(), required.AsSpan());
        }
        internal bool Allows(AbilitySystemComponent source, AbilitySystemComponent target, ITargetFilter filter)
            => target != null && (!(ExcludeSelf && Kind != TargetQueryKind.Self) || !ReferenceEquals(source, target))
                && _requirements.Matches(target.OwnedTags) && (filter == null || filter.Allows(source, target));
        public void Validate(SkillExecutionContext context, bool requireFixed = true)
        {
            if (Kind == TargetQueryKind.FixedTarget && requireFixed && context.Target == null)
                throw new InvalidOperationException("缺少固定目标");
            if (Kind == TargetQueryKind.TargetPoint && requireFixed) context.Services.RequireTargetPosition();
            if (Kind >= TargetQueryKind.TargetPoint && context.Services.Origin == null)
                throw new InvalidOperationException("范围查询缺少施法位置服务");
            if (Radius.Resolve(context.Values) < 0 || Width.Resolve(context.Values) < 0 || Height.Resolve(context.Values) < 0)
                throw new InvalidOperationException("查询范围不能为负数");
            Angle.Resolve(context.Values);
        }
    }

    public sealed class TargetQueryBuffer
    {
        internal readonly Collider[] Colliders;
        public readonly AbilitySystemComponent[] Units;
        public readonly Transform[] Transforms;
        internal readonly float[] Distances;
        public int Count { get; internal set; }
        public bool Saturated { get; internal set; }
        public TargetQueryBuffer(int capacity = 256)
        {
            Colliders = new Collider[capacity]; Units = new AbilitySystemComponent[capacity];
            Transforms = new Transform[capacity]; Distances = new float[capacity];
        }
        internal void Clear()
        {
            Array.Clear(Units, 0, Count); Array.Clear(Transforms, 0, Count);
            Array.Clear(Colliders, 0, Colliders.Length); Count = 0; Saturated = false;
        }
    }
}
