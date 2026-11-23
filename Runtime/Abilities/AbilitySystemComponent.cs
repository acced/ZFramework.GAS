using GameplayTags;
using System;
using System.Buffers;
using System.Collections.Generic;

namespace ZFramework.GAS
{
    /// <summary>实体的 GAS 门面：属性、标签、生效效果、已授予能力与任务的统一入口。</summary>
    public sealed class AbilitySystemComponent
    {
        private readonly List<GameplayAbilitySpec> _abilities = new();
        private readonly GameplayTaskHost _tasks = new();
        private bool _clearing, _ticking;

        public AttributeSet Attributes { get; }
        public GameplayTagCountContainer OwnedTags { get; }
        public GameplayEffectContainer Effects { get; }
        public GameplayEventChannel Events { get; } = new();
        public int Generation { get; private set; }
        public AbilitySourceIdentity SourceIdentity { get; private set; }
        public GameplayCueRouter CueRouter { get; }

        public event Action<GameplayAbilitySpec> OnAbilityActivated;
        public event Action<GameplayAbilitySpec> OnAbilityCommitted;
        public event Action<GameplayAbilitySpec, bool> OnAbilityEnded;

        public AbilitySystemComponent(int attributeCapacity = 0, GameplayCueRouter cueRouter = null)
        {
            SourceIdentity = new AbilitySourceIdentity(this);
            Attributes = new AttributeSet(attributeCapacity);
            OwnedTags = new GameplayTagCountContainer();
            CueRouter = cueRouter;
            Effects = new GameplayEffectContainer(Attributes, OwnedTags, cueRouter, this);
        }

        public void Tick(float deltaTime)
        {
            if (_ticking) throw new InvalidOperationException("ASC.Tick cannot be called reentrantly.");
            _ticking = true;
            try
            {
                _tasks.CurrentTask = null;
                Effects.Tick(deltaTime);
                _tasks.Tick(deltaTime);
            }
            finally { _ticking = false; }
        }

        public GameplayAbilityTask CurrentTask => _tasks.CurrentTask;
        public int ActiveTaskCount => _tasks.Count;
        public GameplayAbilityTask GetActiveTask(int index) => _tasks[index];
        public int GrantedAbilityCount => _abilities.Count;
        public GameplayAbilitySpec GetGrantedAbility(int index) => _abilities[index];

        public void StartTask(GameplayAbilitySpec spec, GameplayAbilityTask task)
        {
            GasAssert.That(ReferenceEquals(spec.Owner, this) && spec.IsGranted, "Unknown grant.");
            if (_clearing || !spec.IsActive) throw new InvalidOperationException("Cannot start a task outside an active ability.");
            _tasks.Start(spec, task);
        }

        public GameplayAbilitySpec GrantAbility(GameplayAbilityDefinition definition, int level = 1)
        {
            var spec = new GameplayAbilitySpec(definition ?? throw new ArgumentNullException(nameof(definition)), this, level);
            _abilities.Add(spec);
            return spec;
        }

        public bool RemoveAbility(GameplayAbilitySpec spec)
        {
            if (spec == null || !_abilities.Remove(spec)) return false;
            spec.IsGranted = false;
            DeactivateInternal(spec, cancelled: true);
            return true;
        }

        public void UpdateAbilityDefinition(GameplayAbilitySpec spec, GameplayAbilityDefinition definition)
        {
            if (spec == null || !ReferenceEquals(spec.Owner, this) || !_abilities.Contains(spec))
                throw new ArgumentException("Unknown grant.", nameof(spec));
            if (spec.IsActive) throw new InvalidOperationException("Cannot replace an executing definition.");
            spec.Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            if (spec.CommittedCooldown != null && !ReferenceEquals(spec.CommittedCooldown, spec.Definition.Cooldown) && !Effects.Find(spec.CommittedCooldown).IsValid)
                spec.CommittedCooldown = null;
        }

        public EffectApplicationResult ApplyEffectToSelf(GameplayEffectDefinition definition, EffectEvaluationContext context = null)
            => Effects.Apply(new GameplayEffectSpec(definition, Attributes, this, context));

        public EffectApplicationResult ApplyEffectToTarget(AbilitySystemComponent target, GameplayEffectDefinition definition, EffectEvaluationContext context = null)
            => target.Effects.Apply(new GameplayEffectSpec(definition, Attributes, this, context));

        public AbilityActivationCheck TryActivateAbility(GameplayAbilitySpec spec, EffectEvaluationContext context = null, bool autoCommit = true)
        {
            context ??= spec.DefaultContext;
            if (autoCommit) return Commit(spec, context, activate: true);

            AbilityActivationCheck check = CheckActivation(spec, context);
            if (!check.Succeeded) return check;
            spec.Committing = true;
            try { return Activate(spec, context) ? default : new AbilityActivationCheck(AbilityActivationResult.Inactive, "技能在激活期间结束"); }
            finally { spec.Committing = false; }
        }

        public AbilityActivationCheck CommitAbility(GameplayAbilitySpec spec, int activationVersion)
            => spec.ActivationVersion == activationVersion
                ? Commit(spec, spec.EvaluationContext, activate: false)
                : new AbilityActivationCheck(AbilityActivationResult.Inactive, "施放版本已结束");

        public AbilityActivationCheck CheckActivation(GameplayAbilitySpec spec, EffectEvaluationContext context = null)
        {
            AbilityActivationCheck check = CheckGates(spec);
            return check.Succeeded ? CheckCost(spec.Definition.Cost, context ?? spec.DefaultContext) : check;
        }

        public AbilityActivationCheck CheckCommit(GameplayAbilitySpec spec)
        {
            AbilityActivationCheck check = CheckCommitGates(spec);
            return check.Succeeded && !spec.IsCommitted ? CheckCost(spec.Definition.Cost, spec.EvaluationContext) : check;
        }

        private AbilityActivationCheck Commit(GameplayAbilitySpec spec, EffectEvaluationContext context, bool activate)
        {
            AbilityActivationCheck check = activate ? CheckGates(spec) : CheckCommitGates(spec);
            if (!check.Succeeded || (!activate && spec.IsCommitted)) return check;

            GameplayEffectDefinition cost = spec.Definition.Cost, cooldown = spec.Definition.Cooldown;
            if (cost == null && cooldown == null)
            {
                spec.Committing = true;
                try
                {
                    if (activate && !Activate(spec, context)) return CompleteCommit(spec, spec.ActivationVersion);
                    spec.IsCommitted = true;
                    int version = spec.ActivationVersion;
                    OnAbilityCommitted?.Invoke(spec);
                    return CompleteCommit(spec, version);
                }
                finally { spec.Committing = false; }
            }

            int costCount = cost?.Modifiers.Length ?? 0, cdCount = cooldown?.Modifiers.Length ?? 0, total = costCount + cdCount;
            float[] rented = null;
            Span<float> values = total <= 128 ? stackalloc float[total] : (rented = ArrayPool<float>.Shared.Rent(total));
            try
            {
                GameplayEffectSpec costSpec = cost == null ? default : new GameplayEffectSpec(cost, Attributes, this, context: context);
                GameplayEffectSpec cdSpec = cooldown == null ? default : new GameplayEffectSpec(cooldown, Attributes, this, context: context);
                Span<float> costValues = values.Slice(0, costCount), cdValues = values.Slice(costCount, cdCount);

                for (int i = 0; i < costCount; i++) costValues[i] = costSpec.Resolve(cost.Modifiers[i].Magnitude, Attributes);
                check = CheckCost(cost, costValues);
                if (!check.Succeeded) return check;

                for (int i = 0; i < cdCount; i++) cdValues[i] = cdSpec.Resolve(cooldown.Modifiers[i].Magnitude, Attributes);
                float duration = cooldown != null ? cdSpec.ResolveDuration(Attributes) : 0f;
                float period = cooldown != null ? cdSpec.ResolvePeriod(Attributes) : 0f;

                spec.Committing = true;
                if (activate && !Activate(spec, context)) return CompleteCommit(spec, spec.ActivationVersion);
                spec.IsCommitted = true;
                int version = spec.ActivationVersion, generation = Generation;
                if (cost != null) Effects.ApplyResolved(costSpec, costValues, 0f, 0f);
                if (generation != Generation) return CompleteCommit(spec, version);
                if (cooldown != null) { spec.CommittedCooldown = cooldown; Effects.ApplyResolved(cdSpec, cdValues, duration, period); }
                if (generation == Generation && spec.ActivationVersion == version && spec.IsActive && spec.IsGranted)
                    OnAbilityCommitted?.Invoke(spec);
                return CompleteCommit(spec, version);
            }
            finally
            {
                spec.Committing = false;
                if (rented != null) ArrayPool<float>.Shared.Return(rented);
            }
        }

        private static AbilityActivationCheck CompleteCommit(GameplayAbilitySpec spec, int version)
            => spec.ActivationVersion == version && spec.IsActive && spec.IsGranted
                ? default : new AbilityActivationCheck(AbilityActivationResult.Inactive, "技能在提交期间结束");

        private bool Activate(GameplayAbilitySpec spec, EffectEvaluationContext context)
        {
            CancelAbilitiesWithTags(spec.Definition.CancelAbilitiesWithTags);
            if (!spec.IsGranted) return false;
            spec.EvaluationContext = context;
            spec.ActivationVersion++;
            spec.IsActive = true;
            spec.IsCommitted = false;
            GameplayTagSet tags = spec.Definition.ActivationOwnedTags;
            for (int i = 0; i < tags.Count && spec.IsActive; i++) { spec.OwnedTagCount++; OwnedTags.AddTag(tags[i]); }
            if (spec.IsActive) OnAbilityActivated?.Invoke(spec);
            return spec.IsActive;
        }

        private AbilityActivationCheck CheckCost(GameplayEffectDefinition cost, EffectEvaluationContext context)
        {
            int count = cost?.Modifiers.Length ?? 0;
            if (count == 0) return default;
            var effect = new GameplayEffectSpec(cost, Attributes, this, context: context);
            float[] rented = null;
            Span<float> values = count <= 128 ? stackalloc float[count] : (rented = ArrayPool<float>.Shared.Rent(count));
            try
            {
                for (int i = 0; i < count; i++) values[i] = effect.Resolve(cost.Modifiers[i].Magnitude, Attributes);
                return CheckCost(cost, values);
            }
            finally { if (rented != null) ArrayPool<float>.Shared.Return(rented); }
        }

        private AbilityActivationCheck CheckGates(GameplayAbilitySpec spec)
        {
            GasAssert.That(ReferenceEquals(spec.Owner, this), "Unknown grant.");
            if (_clearing || !spec.IsGranted) return new AbilityActivationCheck(AbilityActivationResult.Inactive, "技能已被撤销");
            if (spec.IsActive || spec.Committing) return new AbilityActivationCheck(AbilityActivationResult.AlreadyActive, "技能正在执行");

            GameplayAbilityDefinition d = spec.Definition;
            if (!d.ActivationRequirements.Matches(OwnedTags))
            {
                for (int i = 0; i < d.ActivationRequiredTags.Count; i++)
                    if (!OwnedTags.HasTag(d.ActivationRequiredTags[i]))
                        return new AbilityActivationCheck(AbilityActivationResult.Blocked, "缺少必需标签", d.ActivationRequiredTags[i]);
                for (int i = 0; i < d.ActivationBlockedTags.Count; i++)
                    if (OwnedTags.HasTag(d.ActivationBlockedTags[i]))
                        return new AbilityActivationCheck(AbilityActivationResult.Blocked, "被标签阻止", d.ActivationBlockedTags[i]);
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
            GameplayEffectDefinition cost = spec.Definition.Cost, cd = spec.Definition.Cooldown;
            return (cost != null && !cost.ApplicationRequirements.Matches(OwnedTags)) ||
                   (cd != null && !cd.ApplicationRequirements.Matches(OwnedTags))
                ? new AbilityActivationCheck(AbilityActivationResult.Blocked, "消耗或冷却效果被标签阻止") : default;
        }

        public bool EndAbility(GameplayAbilitySpec spec, bool cancelled = false)
        {
            if (spec == null || !ReferenceEquals(spec.Owner, this) || !spec.IsActive) return false;
            DeactivateInternal(spec, cancelled);
            return true;
        }

        public bool CancelAbility(GameplayAbilitySpec spec) => EndAbility(spec, cancelled: true);

        public int CancelAbilitiesWithTags(in GameplayTagSet tags) => tags.IsEmpty ? 0 : CancelMatching(tags, all: false);

        public int CancelAllAbilities() => CancelMatching(default, all: true);

        private int CancelMatching(in GameplayTagSet tags, bool all)
        {
            int count = _abilities.Count;
            if (count == 0) return 0;
            if (count == 1)
            {
                GameplayAbilitySpec s = _abilities[0];
                return s.IsActive && (all || s.Definition.AbilityTags.HasAnyMatching(tags)) && EndAbility(s, true) ? 1 : 0;
            }
            int cancelled = 0;
            var snapshot = ArrayPool<(GameplayAbilitySpec spec, int version)>.Shared.Rent(count);
            for (int i = 0; i < count; i++) snapshot[i] = (_abilities[i], _abilities[i].ActivationVersion);
            try
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    var (s, v) = snapshot[i];
                    if (s.ActivationVersion == v && s.IsActive && (all || s.Definition.AbilityTags.HasAnyMatching(tags)) && EndAbility(s, true))
                        cancelled++;
                }
            }
            finally { ArrayPool<(GameplayAbilitySpec, int)>.Shared.Return(snapshot, clearArray: true); }
            return cancelled;
        }

        public void Clear()
        {
            if (_clearing) return;
            _clearing = true;
            try
            {
                Generation++;
                SourceIdentity = new AbilitySourceIdentity(this);
                for (int i = _abilities.Count - 1; i >= 0; i--)
                    if (i < _abilities.Count) EndAbility(_abilities[i], true);
                for (int i = 0; i < _abilities.Count; i++) _abilities[i].IsGranted = false;
                _abilities.Clear();
                _tasks.Clear();
                Effects.Clear();
                OwnedTags.Clear();
                Events.Clear();
            }
            finally { _clearing = false; }
        }

        private AbilityActivationCheck CheckCost(GameplayEffectDefinition cost, ReadOnlySpan<float> values)
        {
            if (cost == null) return default;
            ReadOnlySpan<EffectModifier> mods = cost.Modifiers;
            for (int i = 0; i < mods.Length; i++)
            {
                ref readonly EffectModifier m = ref mods[i];
                if (!Attributes.TryGet(m.Target, out GameplayAttribute attr))
                    return new AbilityActivationCheck(AbilityActivationResult.InvalidConfiguration, "缺少消耗属性", attribute: m.Target);
                if (m.Operation != ModifierOperation.Add) continue;

                bool seen = false;
                for (int prev = 0; prev < i; prev++)
                    if (mods[prev].Target == m.Target && mods[prev].Operation == ModifierOperation.Add) { seen = true; break; }
                if (seen) continue;

                float sum = values[i];
                for (int next = i + 1; next < mods.Length; next++)
                    if (mods[next].Target == m.Target && mods[next].Operation == ModifierOperation.Add) sum += values[next];
                if (attr.CurrentValue + sum < 0f)
                    return new AbilityActivationCheck(AbilityActivationResult.CannotAfford, "资源不足", attribute: m.Target, amount: -(attr.CurrentValue + sum));
            }
            return default;
        }

        private void DeactivateInternal(GameplayAbilitySpec spec, bool cancelled)
        {
            if (!spec.IsActive) return;
            int version = spec.ActivationVersion;
            spec.IsActive = false;
            GameplayTagSet tags = spec.Definition.ActivationOwnedTags;
            int count = spec.OwnedTagCount;
            spec.OwnedTagCount = 0;
            for (int i = 0; i < count; i++) OwnedTags.RemoveTag(tags[i]);
            _tasks.Cancel(spec, version);
            OnAbilityEnded?.Invoke(spec, cancelled);
        }
    }
}
