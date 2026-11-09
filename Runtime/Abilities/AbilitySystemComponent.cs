using GameplayTags;
using System;
using System.Buffers;
using System.Collections.Generic;

namespace ZFramework.GAS
{
    /// <summary>
    /// 一个实体的 GAS 门面：属性、标签、生效效果、已授予能力的聚合体。
    ///
    /// 宿主自行持有实例并调用 <see cref="Tick"/>。标签词表在域内注册一次后冻结，
    /// 标签入参由调用方保证已经解析；没有隐式 tick 源。
    /// </summary>
    public sealed class AbilitySystemComponent
    {
        private readonly List<GameplayAbilitySpec> _abilities = new();
        private readonly GameplayTaskHost _tasks = new();
        private bool _clearing, _ticking;
        public AttributeSet Attributes { get; }
        public GameplayTagCountContainer OwnedTags { get; }
        public GameplayEffectContainer Effects { get; }
        public GameplayEventChannel Events { get; } = new GameplayEventChannel();
        /// <summary>Changes when a pooled owner is cleared; delayed work must not target its next incarnation.</summary>
        public int Generation { get; private set; }
        public AbilitySourceIdentity SourceIdentity { get; private set; }

        /// <summary>构造时传入的 Cue 路由；可为 null。效果之外的表现时机（动画时间轴的区间等）据此派发。</summary>
        public GameplayCueRouter CueRouter { get; }

        public AbilitySystemComponent(int attributeCapacity = 0, GameplayCueRouter cueRouter = null)
        {
            SourceIdentity = new AbilitySourceIdentity(this);
            Attributes = new AttributeSet(attributeCapacity);
            OwnedTags = new GameplayTagCountContainer();
            CueRouter = cueRouter;
            Effects = new GameplayEffectContainer(Attributes, OwnedTags, cueRouter, this);
        }

        /// <summary>
        /// 推进本实体的时间。这是整个系统唯一的 tick 入口，由宿主显式驱动。
        /// 先推进效果再推进任务：任务观察到的冷却与增益状态是本帧最新的。
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (_ticking) throw new InvalidOperationException("ASC.Tick cannot be called reentrantly. Advance each owner once from the host loop.");
            _ticking = true;
            try
            {
                _tasks.CurrentTask = null;
                Effects.Tick(deltaTime);
                _tasks.Tick(deltaTime);
            }
            finally { _ticking = false; }
        }

        /// <summary>When a task throws during Tick, the host can end just that cast.</summary>
        public GameplayAbilityTask CurrentTask => _tasks.CurrentTask;
        public int ActiveTaskCount => _tasks.Count;
        public GameplayAbilityTask GetActiveTask(int index) => _tasks[index];

        /// <summary>The task belongs to this activation and is cancelled when it ends.</summary>
        public void StartTask(GameplayAbilitySpec spec, GameplayAbilityTask task)
        {
            GasAssert.That(ReferenceEquals(spec.Owner, this) && spec.IsGranted, "Unknown grant.");
            if (_clearing || !spec.IsActive) throw new InvalidOperationException("Cannot start a task outside an active ability.");
            _tasks.Start(spec, task);
        }

        public int GrantedAbilityCount => _abilities.Count;

        public GameplayAbilitySpec GetGrantedAbility(int index) => _abilities[index];

        public GameplayAbilitySpec GrantAbility(GameplayAbilityDefinition definition, int level = 1)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            var spec = new GameplayAbilitySpec(definition, this, level);
            _abilities.Add(spec);
            return spec;
        }

        public bool RemoveAbility(GameplayAbilitySpec spec)
        {
            if (spec == null || !_abilities.Remove(spec))
            {
                return false;
            }

            spec.IsGranted = false;
            DeactivateInternal(spec);
            return true;
        }

        /// <summary>Refresh an inactive grant after authoring changes. Existing effects keep their original definitions.</summary>
        public void UpdateAbilityDefinition(GameplayAbilitySpec spec, GameplayAbilityDefinition definition)
        {
            if (spec == null || !ReferenceEquals(spec.Owner, this) || !_abilities.Contains(spec)) throw new ArgumentException("Unknown grant.", nameof(spec));
            if (spec.IsActive) throw new InvalidOperationException("Cannot replace an executing definition.");
            spec.Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        }

        /// <summary>Activate after checking tags, cooldown and cost. Set autoCommit to false for aiming or charging.</summary>
        public AbilityActivationResult TryActivateAbility(GameplayAbilitySpec spec,
            EffectEvaluationContext context = null, bool autoCommit = true)
        {
            context ??= spec.DefaultContext;
            if (autoCommit) return Commit(spec, context, activate: true);
            var check = CheckActivation(spec, context);
            spec.LastActivationCheck = check;
            if (!check.Succeeded) return check.Result;
            spec.Committing = true;
            try { return Activate(spec, context) ? AbilityActivationResult.Activated : AbilityActivationResult.Inactive; }
            finally { spec.Committing = false; }
        }

        /// <summary>Charge cost and start cooldown once for the current activation.</summary>
        public AbilityActivationResult CommitAbility(GameplayAbilitySpec spec)
            => CommitAbility(spec, spec.ActivationVersion);

        /// <summary>Delayed work passes its captured version so an old task cannot commit a later activation.</summary>
        public AbilityActivationResult CommitAbility(GameplayAbilitySpec spec, int activationVersion)
            => spec.ActivationVersion == activationVersion
                ? Commit(spec, spec.EvaluationContext, activate: false)
                : AbilityActivationResult.Inactive;

        public AbilityActivationCheck CheckActivation(GameplayAbilitySpec spec, EffectEvaluationContext context = null)
        {
            var check = CheckGates(spec);
            return check.Succeeded ? CheckCost(spec.Definition.Cost, context ?? spec.DefaultContext) : check;
        }

        public AbilityActivationCheck CheckCommit(GameplayAbilitySpec spec)
        {
            var check = CheckCommitGates(spec);
            return check.Succeeded && !spec.IsCommitted ? CheckCost(spec.Definition.Cost, spec.EvaluationContext) : check;
        }

        private AbilityActivationResult Commit(GameplayAbilitySpec spec, EffectEvaluationContext context, bool activate)
        {
            var check = activate ? CheckGates(spec) : CheckCommitGates(spec);
            if (activate) spec.LastActivationCheck = check;
            else spec.LastCommitCheck = check;
            if (!check.Succeeded || !activate && spec.IsCommitted) return check.Result;
            var definition = spec.Definition;
            var cost = definition.Cost;
            var cooldown = definition.Cooldown;
            int costCount = cost == null ? 0 : cost.Modifiers.Length;
            int cooldownCount = cooldown == null ? 0 : cooldown.Modifiers.Length;
            float[] rented = null;
            Span<float> values = costCount + cooldownCount <= 128 ? stackalloc float[costCount + cooldownCount]
                : (rented = ArrayPool<float>.Shared.Rent(costCount + cooldownCount));
            try
            {
                var costSpec = cost == null ? default : new GameplayEffectSpec(cost, Attributes, this, context: context);
                var cooldownSpec = cooldown == null ? default : new GameplayEffectSpec(cooldown, Attributes, this, context: context);
                var costValues = values.Slice(0, costCount);
                var cooldownValues = values.Slice(costCount, cooldownCount);
                for (int i = 0; i < costCount; i++) costValues[i] = costSpec.Resolve(cost.Modifiers[i].Magnitude, Attributes);
                check = CheckCost(cost, costValues);
                spec.LastCommitCheck = check;
                if (activate) spec.LastActivationCheck = check;
                if (!check.Succeeded) return check.Result;
                for (int i = 0; i < cooldownCount; i++) cooldownValues[i] = cooldownSpec.Resolve(cooldown.Modifiers[i].Magnitude, Attributes);
                float duration = cooldown != null ? cooldownSpec.ResolveDuration(Attributes) : 0;
                float period = cooldown != null ? cooldownSpec.ResolvePeriod(Attributes) : 0;
                spec.Committing = true;
                if (activate && !Activate(spec, context)) return CompleteCommit(spec, spec.ActivationVersion, activate);
                spec.IsCommitted = true;
                int version = spec.ActivationVersion;
                int generation = Generation;
                if (cost != null) Effects.ApplyResolved(costSpec, costValues, 0, 0);
                if (generation != Generation) return CompleteCommit(spec, version, activate);
                if (cooldown != null)
                {
                    spec.CommittedCooldown = cooldown;
                    Effects.ApplyResolved(cooldownSpec, cooldownValues, duration, period);
                }
                return CompleteCommit(spec, version, activate);
            }
            finally
            {
                spec.Committing = false;
                if (rented != null) ArrayPool<float>.Shared.Return(rented);
            }
        }

        private static AbilityActivationResult CompleteCommit(GameplayAbilitySpec spec, int version, bool activate)
        {
            if (spec.ActivationVersion != version) return AbilityActivationResult.Inactive;
            var check = spec.IsActive && spec.IsGranted ? default
                : new AbilityActivationCheck(AbilityActivationResult.Inactive, "技能在提交期间结束");
            spec.LastCommitCheck = check;
            if (activate) spec.LastActivationCheck = check;
            return check.Result;
        }

        private bool Activate(GameplayAbilitySpec spec, EffectEvaluationContext context)
        {
            CancelAbilitiesWithTags(spec.Definition.CancelAbilitiesWithTags);
            if (!spec.IsGranted) return false;
            spec.EvaluationContext = context;
            spec.ActivationVersion++;
            spec.IsActive = true;
            spec.IsCommitted = false;
            spec.LastCommitCheck = default;
            var tags = spec.Definition.ActivationOwnedTags;
            for (int i = 0; i < tags.Count && spec.IsActive; i++)
            {
                spec.OwnedTagCount++;
                OwnedTags.AddTag(tags[i]);
            }
            return spec.IsActive;
        }

        private AbilityActivationCheck CheckCost(GameplayEffectDefinition cost, EffectEvaluationContext context)
        {
            if (cost == null) return default;
            var effect = new GameplayEffectSpec(cost, Attributes, this, context: context);
            float[] rented = null;
            Span<float> values = cost.Modifiers.Length <= 128 ? stackalloc float[cost.Modifiers.Length]
                : (rented = ArrayPool<float>.Shared.Rent(cost.Modifiers.Length));
            try
            {
                for (int i = 0; i < cost.Modifiers.Length; i++) values[i] = effect.Resolve(cost.Modifiers[i].Magnitude, Attributes);
                return CheckCost(cost, values);
            }
            finally { if (rented != null) ArrayPool<float>.Shared.Return(rented); }
        }

        private AbilityActivationCheck CheckGates(GameplayAbilitySpec spec)
        {
            GasAssert.That(ReferenceEquals(spec.Owner, this), "Unknown grant.");
            if (_clearing || !spec.IsGranted) return new AbilityActivationCheck(AbilityActivationResult.Inactive, "技能已被撤销");
            if (spec.IsActive || spec.Committing) return new AbilityActivationCheck(AbilityActivationResult.AlreadyActive, "技能正在执行");
            var definition = spec.Definition;
            if (!definition.ActivationRequirements.Matches(OwnedTags))
            {
                for (int i = 0; i < definition.ActivationRequiredTags.Count; i++)
                    if (!OwnedTags.HasTag(definition.ActivationRequiredTags[i]))
                        return new AbilityActivationCheck(AbilityActivationResult.Blocked, "缺少必需标签", definition.ActivationRequiredTags[i]);
                for (int i = 0; i < definition.ActivationBlockedTags.Count; i++)
                    if (OwnedTags.HasTag(definition.ActivationBlockedTags[i]))
                        return new AbilityActivationCheck(AbilityActivationResult.Blocked, "被标签阻止", definition.ActivationBlockedTags[i]);
            }
            return CheckPaymentGates(spec);
        }

        private AbilityActivationCheck CheckCommitGates(GameplayAbilitySpec spec)
        {
            GasAssert.That(ReferenceEquals(spec.Owner, this), "Unknown grant.");
            if (_clearing || !spec.IsActive) return new AbilityActivationCheck(AbilityActivationResult.Inactive, "技能未激活");
            if (spec.IsCommitted) return default;
            if (spec.Committing) return new AbilityActivationCheck(AbilityActivationResult.AlreadyActive, "技能正在提交");
            return CheckPaymentGates(spec);
        }

        private AbilityActivationCheck CheckPaymentGates(GameplayAbilitySpec spec)
        {
            if (spec.IsOnCooldown) return new AbilityActivationCheck(AbilityActivationResult.OnCooldown, "技能冷却中", amount: spec.CooldownRemaining);
            var cost = spec.Definition.Cost;
            var cooldown = spec.Definition.Cooldown;
            return cost != null && !cost.ApplicationRequirements.Matches(OwnedTags) ||
                   cooldown != null && !cooldown.ApplicationRequirements.Matches(OwnedTags)
                ? new AbilityActivationCheck(AbilityActivationResult.Blocked, "消耗或冷却效果被标签阻止") : default;
        }

        /// <summary>结束激活状态，撤下激活期标签。已结束或非本组件授予的返回 false。</summary>
        public bool EndAbility(GameplayAbilitySpec spec)
        {
            if (spec == null || !ReferenceEquals(spec.Owner, this) || !spec.IsActive)
            {
                return false;
            }

            DeactivateInternal(spec);
            return true;
        }

        /// <summary>结束所有「能力标签」命中给定集合的激活中能力，返回结束数量。</summary>
        public int CancelAbilitiesWithTags(in GameplayTagSet tags)
        {
            if (tags.IsEmpty)
            {
                return 0;
            }

            return CancelMatching(tags, false);
        }

        public int CancelAllAbilities()
        {
            return CancelMatching(default, true);
        }

        private int CancelMatching(in GameplayTagSet tags, bool all)
        {
            int count = _abilities.Count, cancelled = 0;
            if (count == 0) return 0;
            var snapshot = ArrayPool<(GameplayAbilitySpec spec, int version)>.Shared.Rent(count);
            for (int i = 0; i < count; i++) snapshot[i] = (_abilities[i], _abilities[i].ActivationVersion);
            try
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    var item = snapshot[i]; var spec = item.spec;
                    if (spec.ActivationVersion == item.version && spec.IsActive && (all || spec.Definition.AbilityTags.HasAnyMatching(tags)) && EndAbility(spec)) cancelled++;
                }
            }
            finally { ArrayPool<(GameplayAbilitySpec, int)>.Shared.Return(snapshot, true); }
            return cancelled;
        }

        /// <summary>回收复用前的清场：结束全部能力（连带取消其任务）、清空效果与标签。属性保留，由下次初始化覆写。</summary>
        public void Clear()
        {
            if (_clearing) return;
            _clearing = true;
            try
            {
            Generation++;
            SourceIdentity = new AbilitySourceIdentity(this);
            for (int i = _abilities.Count - 1; i >= 0; i--)
                if (i < _abilities.Count) EndAbility(_abilities[i]);
            foreach (var spec in _abilities) spec.IsGranted = false;
            _abilities.Clear();
            _tasks.Clear();
            Effects.Clear();
            OwnedTags.Clear();
            Events.Clear();
            }
            finally { _clearing = false; }
        }

        /// <summary>
        /// 消耗是否付得起。只有 Add 操作可判断（当前值加上负数后不得为负）；
        /// 其余操作无法在不实际结算的情况下判定，跳过检查。
        /// </summary>
        private AbilityActivationCheck CheckCost(GameplayEffectDefinition cost, ReadOnlySpan<float> values)
        {
            if (cost == null)
            {
                return default;
            }

            ReadOnlySpan<EffectModifier> modifiers = cost.Modifiers;
            for (int i = 0; i < modifiers.Length; i++)
            {
                ref readonly EffectModifier modifier = ref modifiers[i];
                if (!Attributes.TryGet(modifier.Target, out GameplayAttribute attribute))
                {
                    return new AbilityActivationCheck(AbilityActivationResult.InvalidConfiguration, "缺少消耗属性", attribute: modifier.Target);
                }
                if (modifier.Operation != ModifierOperation.Add) continue;

                float sum = 0;
                for (int j = 0; j <= i; j++)
                    if (modifiers[j].Target == modifier.Target && modifiers[j].Operation == ModifierOperation.Add)
                        sum += values[j];
                if (attribute.CurrentValue + sum < 0f)
                {
                    return new AbilityActivationCheck(AbilityActivationResult.CannotAfford, "资源不足", attribute: modifier.Target,
                        amount: -(attribute.CurrentValue + sum));
                }
            }

            return default;
        }

        private void DeactivateInternal(GameplayAbilitySpec spec)
        {
            if (!spec.IsActive)
            {
                return;
            }

            int version = spec.ActivationVersion;
            spec.IsActive = false;
            var tags = spec.Definition.ActivationOwnedTags;
            int count = spec.OwnedTagCount;
            spec.OwnedTagCount = 0;
            for (int i = 0; i < count; i++) OwnedTags.RemoveTag(tags[i]);
            _tasks.Cancel(spec, version);
        }

    }
}
