using GameplayTags;
using System;

namespace ZFramework.GAS
{
    public enum AbilityActivationResult
    {
        Activated, Blocked, OnCooldown, CannotAfford, AlreadyActive, InvalidConfiguration, Inactive
    }

    /// <summary>不可变能力定义；消耗为 Instant 效果，冷却为授予标签的 Duration 效果。</summary>
    public sealed class GameplayAbilityDefinition
    {
        private readonly GameplayTagRequirements _activationRequirements;

        public GameplayTagSet AbilityTags { get; }
        public GameplayTagSet ActivationRequiredTags { get; }
        public GameplayTagSet ActivationBlockedTags { get; }
        public ref readonly GameplayTagRequirements ActivationRequirements => ref _activationRequirements;
        public GameplayTagSet ActivationOwnedTags { get; }
        public GameplayTagSet CancelAbilitiesWithTags { get; }
        public GameplayEffectDefinition Cost { get; }
        public GameplayEffectDefinition Cooldown { get; }

        public GameplayAbilityDefinition(
            GameplayTagSet abilityTags = default,
            GameplayTagSet activationRequiredTags = default,
            GameplayTagSet activationBlockedTags = default,
            GameplayTagSet activationOwnedTags = default,
            GameplayTagSet cancelAbilitiesWithTags = default,
            GameplayEffectDefinition cost = null,
            GameplayEffectDefinition cooldown = null)
        {
            if (cost != null && cost.DurationPolicy != EffectDurationPolicy.Instant)
                throw new ArgumentException("Ability cost must be an Instant effect.", nameof(cost));
            if (cooldown != null && cooldown.Tags.Granted.IsEmpty)
                throw new ArgumentException("Ability cooldown must grant at least one tag.", nameof(cooldown));

            AbilityTags = abilityTags;
            ActivationRequiredTags = activationRequiredTags;
            ActivationBlockedTags = activationBlockedTags;
            _activationRequirements = GameplayTagRequirements.Create(activationBlockedTags.AsSpan(), activationRequiredTags.AsSpan());
            ActivationOwnedTags = activationOwnedTags;
            CancelAbilitiesWithTags = cancelAbilitiesWithTags;
            Cost = cost;
            Cooldown = cooldown;
        }
    }

    /// <summary>已授予的能力实例（由 <see cref="AbilitySystemComponent.GrantAbility"/> 创建）。</summary>
    public sealed class GameplayAbilitySpec
    {
        private EffectEvaluationContext _levelContext;

        public GameplayAbilityDefinition Definition { get; internal set; }
        public AbilitySystemComponent Owner { get; }
        public int ActivationVersion { get; internal set; }
        public int Level { get; set; }
        public EffectEvaluationContext EvaluationContext { get; internal set; }
        public bool IsActive { get; internal set; }
        public bool IsCommitted { get; internal set; }
        public bool IsGranted { get; internal set; } = true;

        internal GameplayEffectDefinition CommittedCooldown;
        internal EffectEvaluationContext PreparedDefaultContext;
        internal int OwnedTagCount;
        internal bool Committing;

        internal EffectEvaluationContext DefaultContext
        {
            get
            {
                int level = Math.Max(1, Level);
                if (PreparedDefaultContext != null && PreparedDefaultContext.Level == level) return PreparedDefaultContext;
                if (_levelContext == null || _levelContext.Level != level) _levelContext = EffectEvaluationContext.Live(level, Owner.Attributes);
                return _levelContext;
            }
        }

        internal GameplayAbilitySpec(GameplayAbilityDefinition definition, AbilitySystemComponent owner, int level)
        {
            Definition = definition; Owner = owner; Level = level;
        }

        public bool IsOnCooldown
        {
            get
            {
                GameplayEffectDefinition cooldown = Definition.Cooldown;
                if (cooldown != null && cooldown.Tags.Granted.HasAny(Owner.OwnedTags)) return true;
                GameplayEffectDefinition committed = CommittedCooldown;
                if (committed == null || ReferenceEquals(committed, cooldown)) return false;
                if (Owner.Effects.Find(committed).IsValid) return committed.Tags.Granted.HasAny(Owner.OwnedTags);
                CommittedCooldown = null;
                return false;
            }
        }

        public float CooldownRemaining => TryGetActiveCooldown(out float remaining, out _) ? remaining : 0f;

        public float CooldownDuration
        {
            get
            {
                if (TryGetActiveCooldown(out _, out float active)) return active;
                GameplayEffectDefinition d = Definition.Cooldown;
                if (d == null) return 0f;
                return d.DurationMagnitude.HasValue
                    ? new GameplayEffectSpec(d, Owner.Attributes, Owner, context: EvaluationContext ?? DefaultContext).ResolveDuration(Owner.Attributes)
                    : d.Duration;
            }
        }

        private bool TryGetActiveCooldown(out float remaining, out float duration)
        {
            remaining = duration = 0f;
            GameplayEffectDefinition d = Definition.Cooldown;
            bool found = d != null && d.Tags.Granted.HasAny(Owner.OwnedTags) &&
                Owner.Effects.TryGetLongestRemainingWithAnyTag(d.Tags.Granted, out remaining, out duration);

            GameplayEffectDefinition committed = CommittedCooldown;
            if (committed != null && !ReferenceEquals(committed, d))
            {
                if (Owner.Effects.TryGetInfo(Owner.Effects.Find(committed), out var info))
                {
                    float r = Math.Max(0f, info.RemainingTime);
                    if (!found || r > remaining) { remaining = r; duration = info.Duration; found = true; }
                }
                else CommittedCooldown = null;
            }
            return found;
        }
    }
}
