using GameplayTags;
using System;

namespace ZFramework.GAS
{
    public enum AbilityActivationResult
    {
        Activated,
        /// <summary>标签门禁未通过。</summary>
        Blocked,
        /// <summary>冷却中。</summary>
        OnCooldown,
        /// <summary>消耗不足。</summary>
        CannotAfford,
        /// <summary>该能力已处于激活状态。</summary>
        AlreadyActive,
        InvalidConfiguration,
    }

    /// <summary>
    /// 能力**定义**：不可变配置，多个持有者共享同一实例。对应 UE 的 <c>UGameplayAbility</c>（CDO）。
    ///
    /// 消耗与冷却都是普通的 <see cref="GameplayEffectDefinition"/>：
    /// 消耗是 Instant 效果，冷却是授予冷却标签的 Duration 效果——
    /// 「是否在冷却」即「持有者是否拥有该冷却标签」，与 UE 一致。
    /// </summary>
    public sealed class GameplayAbilityDefinition
    {
        /// <summary>本能力的标识标签，供 <see cref="CancelAbilitiesWithTags"/> 之类的匹配使用。</summary>
        public GameplayTagSet AbilityTags { get; }

        /// <summary>持有者必须全部拥有才可激活。</summary>
        public GameplayTagSet ActivationRequiredTags { get; }

        /// <summary>持有者拥有任一即不可激活。</summary>
        public GameplayTagSet ActivationBlockedTags { get; }

        private readonly GameplayTagRequirements _activationRequirements;

        /// <summary>构造时预计算，以只读引用匹配持有者的整数标签存储。</summary>
        public ref readonly GameplayTagRequirements ActivationRequirements => ref _activationRequirements;

        /// <summary>激活期间授予持有者，结束时移除。</summary>
        public GameplayTagSet ActivationOwnedTags { get; }

        /// <summary>激活成功时，取消持有者身上带有其中任一标签的其他能力。</summary>
        public GameplayTagSet CancelAbilitiesWithTags { get; }

        /// <summary>消耗，应为 Instant 效果；可为 null。</summary>
        public GameplayEffectDefinition Cost { get; }

        /// <summary>冷却，应为授予冷却标签的 Duration 效果；可为 null。</summary>
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
            {
                throw new ArgumentException("Ability cost must be an Instant effect.", nameof(cost));
            }

            if (cooldown != null && cooldown.Tags.Granted.IsEmpty)
            {
                throw new ArgumentException(
                    "Ability cooldown must grant at least one tag; the granted tag is what marks the ability as on cooldown.",
                    nameof(cooldown));
            }

            AbilityTags = abilityTags;
            ActivationRequiredTags = activationRequiredTags;
            ActivationBlockedTags = activationBlockedTags;
            _activationRequirements = GameplayTagRequirements.Create(
                activationBlockedTags.AsSpan(), activationRequiredTags.AsSpan());
            ActivationOwnedTags = activationOwnedTags;
            CancelAbilitiesWithTags = cancelAbilitiesWithTags;
            Cost = cost;
            Cooldown = cooldown;
        }
    }

    /// <summary>
    /// 已授予的能力实例：定义 + 持有者 + 等级 + 激活状态。对应 UE 的 <c>FGameplayAbilitySpec</c>。
    /// 只由 <see cref="AbilitySystemComponent.GrantAbility"/> 创建。
    /// </summary>
    public sealed class GameplayAbilitySpec
    {
        public GameplayAbilityDefinition Definition { get; internal set; }
        public AbilitySystemComponent Owner { get; }
        public int ActivationVersion { get; internal set; }
        internal GameplayEffectDefinition CommittedCooldown;

        /// <summary>下一次施放的等级；执行中的求值上下文不随此值改变。</summary>
        public int Level { get; set; }
        private EffectEvaluationContext _levelContext;
        internal EffectEvaluationContext DefaultContext
        {
            get
            {
                int level = Math.Max(1, Level);
                if (_levelContext == null || _levelContext.Level != level) _levelContext = EffectEvaluationContext.Live(level, Owner.Attributes);
                return _levelContext;
            }
        }
        public EffectEvaluationContext EvaluationContext { get; internal set; }
        public AbilityActivationCheck LastActivationCheck { get; internal set; }

        public bool IsActive { get; internal set; }
        internal bool Committing;
        public bool IsGranted { get; internal set; } = true;

        internal GameplayAbilitySpec(GameplayAbilityDefinition definition, AbilitySystemComponent owner, int level)
        {
            Definition = definition;
            Owner = owner;
            Level = level;
        }

        /// <summary>持有者是否拥有本能力冷却效果所授予的标签。</summary>
        public bool IsOnCooldown
        {
            get
            {
                GameplayEffectDefinition cooldown = Definition.Cooldown;
                return cooldown != null && cooldown.Tags.Granted.HasAny(Owner.OwnedTags)
                    || CommittedCooldown != null && CommittedCooldown.Tags.Granted.HasAny(Owner.OwnedTags);
            }
        }

        /// <summary>冷却剩余秒数；不在冷却或冷却为无限时长时返回 0。</summary>
        public float CooldownRemaining
        {
            get
            {
                GameplayEffectDefinition cooldown = Definition.Cooldown;
                if (cooldown == null && CommittedCooldown == null)
                {
                    return 0f;
                }

                ActiveGameplayEffectHandle handle = Owner.Effects.Find(CommittedCooldown ?? cooldown);
                return Owner.Effects.TryGetRemainingTime(handle, out float remaining)
                    ? Math.Max(0f, remaining)
                    : 0f;
            }
        }
        public float CooldownDuration
        {
            get
            {
                var definition = CommittedCooldown ?? Definition.Cooldown;
                if (definition == null) return 0;
                if (Owner.Effects.TryGetInfo(Owner.Effects.Find(definition), out var info)) return info.Duration;
                return definition.DurationMagnitude.HasValue && EvaluationContext != null
                    ? new GameplayEffectSpec(definition, context: EvaluationContext).Resolve(definition.DurationMagnitude.Value, Owner.Attributes) : definition.Duration;
            }
        }
    }
}
