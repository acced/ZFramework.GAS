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
        private readonly List<GameplayAbilityTask> _tasks = new();
        private readonly List<(GameplayAbilityTask task, long run)> _frame = new();
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
                CurrentTask = null;
                Effects.Tick(deltaTime);
                for (int i = _tasks.Count - 1; i >= 0; i--) _frame.Add((_tasks[i], _tasks[i].RunVersion));
                foreach (var item in _frame)
                {
                    var task = item.task;
                    if (task.RunVersion != item.run) continue;
                    CurrentTask = task;
                    if(task.IsActive)task.Advance(deltaTime);
                    if (task.RunVersion == item.run && !task.IsActive) _tasks.Remove(task);
                }
                CurrentTask = null;
            }
            finally { _frame.Clear(); _ticking = false; }
        }

        /// <summary>When a task throws during Tick, the host can end just that cast.</summary>
        public GameplayAbilityTask CurrentTask { get; private set; }
        public int ActiveTaskCount => _tasks.Count;

        /// <summary>按索引只读访问当前任务；不要在枚举期间推进或取消能力。</summary>
        public GameplayAbilityTask GetActiveTask(int index) => _tasks[index];

        /// <summary>
        /// 启动一个属于 <paramref name="spec"/> 的延迟动作。能力必须处于激活状态——
        /// 任务的存活期是能力存活期的子集，给未激活的能力挂任务没有意义。
        /// </summary>
        public void StartTask(GameplayAbilitySpec spec, GameplayAbilityTask task)
        {
            if (_clearing) throw new InvalidOperationException("Owner is being recycled.");
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            if (!ReferenceEquals(spec.Owner, this))
            {
                throw new ArgumentException("Ability was granted by a different AbilitySystemComponent.", nameof(spec));
            }

            if (!spec.IsActive)
            {
                throw new InvalidOperationException("Cannot start a task on an ability that is not active.");
            }

            if (task.IsActive) throw new InvalidOperationException("Task is already running.");
            _tasks.Add(task);
            task.Begin(spec, spec.ActivationVersion);
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

        /// <summary>
        /// 尝试激活。检查顺序：归属 → 是否已激活 → 标签门禁 → **冷却** → 消耗，
        /// 全部通过后才提交（扣消耗、上冷却、授予激活标签）。
        ///
        /// 冷却检查是显式的一步——旧实现的 <c>TryActivateAbility</c> 完全不查冷却，
        /// 只能靠冷却效果授予的标签恰好落进 <c>activationBlockedTags</c> 才间接生效。
        /// </summary>
        public AbilityActivationResult TryActivateAbility(GameplayAbilitySpec spec, EffectEvaluationContext context = null)
        {
            context ??= spec.DefaultContext;
            var check = CheckGates(spec);
            spec.LastActivationCheck = check;
            if (!check.Succeeded) return check.Result;
            var definition = spec.Definition;
            var cost = definition.Cost;
            var cooldown = definition.Cooldown;
            int costCount = cost?.Modifiers.Length ?? 0, cooldownCount = cooldown?.Modifiers.Length ?? 0;
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
                spec.LastActivationCheck = check;
                if (!check.Succeeded) return check.Result;
                for (int i = 0; i < cooldownCount; i++) cooldownValues[i] = cooldownSpec.Resolve(cooldown.Modifiers[i].Magnitude, Attributes);
                float duration = cooldown != null ? cooldownSpec.ResolveDuration(Attributes) : 0;
                float period = cooldown != null ? cooldownSpec.ResolvePeriod(Attributes) : 0;
                spec.Committing = true;
                CancelAbilitiesWithTags(definition.CancelAbilitiesWithTags);
                spec.EvaluationContext = context;
                spec.ActivationVersion++;
                // Resolve once above; project callbacks must not invalidate configuration during commit.
                if (cost != null) Effects.ApplyResolved(costSpec, costValues, 0, 0);
                if (cooldown != null)
                {
                    Effects.ApplyResolved(cooldownSpec, cooldownValues, duration, period);
                    spec.CommittedCooldown = cooldown;
                }
                spec.IsActive = true;
                definition.ActivationOwnedTags.AddTo(OwnedTags);
                return AbilityActivationResult.Activated;
            }
            finally
            {
                spec.Committing = false;
                if (rented != null) ArrayPool<float>.Shared.Return(rented);
            }
        }

        /// <summary>The same read-only decision used by activation and the debugger.</summary>
        public AbilityActivationCheck CheckActivation(GameplayAbilitySpec spec, EffectEvaluationContext context = null)
        {
            if (_clearing) return new AbilityActivationCheck(AbilityActivationResult.InvalidConfiguration, "单位正在回收");
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            if (!ReferenceEquals(spec.Owner, this))
            {
                throw new ArgumentException("Ability was granted by a different AbilitySystemComponent.", nameof(spec));
            }

            if (!spec.IsGranted) throw new ArgumentException("Ability is no longer granted.", nameof(spec));

            var check = CheckGates(spec);
            if (!check.Succeeded) return check;
            var cost = spec.Definition.Cost;
            if (cost == null) return default;
            var effect = new GameplayEffectSpec(cost, Attributes, this, context: context ?? spec.DefaultContext);
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
            GasAssert.That(spec != null && ReferenceEquals(spec.Owner, this) && spec.IsGranted && !_clearing, "Unknown grant or clearing owner.");
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
            return spec.IsOnCooldown ? new AbilityActivationCheck(AbilityActivationResult.OnCooldown, "技能冷却中", amount: spec.CooldownRemaining) : default;
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

            EffectModifier[] modifiers = cost.Modifiers;
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
            for (int i = 0; i < tags.Count; i++)
                OwnedTags.RemoveTag(tags[i]);
            CancelTasksOf(spec, version);
        }

        /// <summary>能力结束即取消其全部在跑任务，不留无主的跨帧状态。</summary>
        private void CancelTasksOf(GameplayAbilitySpec spec, int version)
        {
            int count = _tasks.Count;
            if (count == 0) return;
            var snapshot = ArrayPool<(GameplayAbilityTask task, long run)>.Shared.Rent(count);
            for (int i = 0; i < count; i++) snapshot[i] = (_tasks[i], _tasks[i].RunVersion);
            try
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    var item = snapshot[i]; var task = item.task;
                    if (task.RunVersion != item.run || !ReferenceEquals(task.Ability, spec) || task.ActivationVersion != version || !_tasks.Remove(task)) continue;
                    task.CancelInternal();
                }
            }
            finally { ArrayPool<(GameplayAbilityTask, long)>.Shared.Return(snapshot, true); }
        }
    }
}
