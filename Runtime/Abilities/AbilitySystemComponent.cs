using GameplayTags;
using System;
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
        private bool _clearing;

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
            Effects.Tick(deltaTime);

            // 反向遍历 + 就地移除：任务的 OnTick 里可能启动新任务或结束能力。
            for (int i = _tasks.Count - 1; i >= 0; i--)
            {
                if (i >= _tasks.Count)
                {
                    continue;
                }

                GameplayAbilityTask task = _tasks[i];
                task.Advance(deltaTime);
                if (!task.IsActive)
                {
                    _tasks.Remove(task);
                }
            }
        }

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
            try { task.Begin(spec); }
            catch { _tasks.Remove(task); task.CancelInternal(); throw; }
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
            var check = CheckActivation(spec, context);
            spec.LastActivationCheck = check;
            if (!check.Succeeded) return check.Result;
            GameplayAbilityDefinition definition = spec.Definition;
            context ??= spec.DefaultContext;
            spec.EvaluationContext = context;
            CancelAbilitiesWithTags(definition.CancelAbilitiesWithTags);
            if (definition.Cost != null)
                Effects.Apply(new GameplayEffectSpec(definition.Cost, Attributes, this, context: context));
            if (definition.Cooldown != null)
            {
                Effects.Apply(new GameplayEffectSpec(definition.Cooldown, Attributes, this, context: context));
                spec.CommittedCooldown = definition.Cooldown;
            }
            definition.ActivationOwnedTags.AddTo(OwnedTags);
            spec.IsActive = true;
            spec.ActivationVersion++;
            return AbilityActivationResult.Activated;
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

            if (spec.IsActive)
            {
                return new AbilityActivationCheck(AbilityActivationResult.AlreadyActive, "技能正在执行");
            }

            GameplayAbilityDefinition definition = spec.Definition;

            if (!definition.ActivationRequirements.Matches(OwnedTags))
            {
                for (int i = 0; i < definition.ActivationRequiredTags.Count; i++)
                    if (!OwnedTags.HasTag(definition.ActivationRequiredTags[i]))
                        return new AbilityActivationCheck(AbilityActivationResult.Blocked, "缺少必需标签", definition.ActivationRequiredTags[i]);
                for (int i = 0; i < definition.ActivationBlockedTags.Count; i++)
                    if (OwnedTags.HasTag(definition.ActivationBlockedTags[i]))
                        return new AbilityActivationCheck(AbilityActivationResult.Blocked, "被标签阻止", definition.ActivationBlockedTags[i]);
                return new AbilityActivationCheck(AbilityActivationResult.Blocked, "标签条件不满足");
            }

            if (spec.IsOnCooldown)
            {
                return new AbilityActivationCheck(AbilityActivationResult.OnCooldown, "技能冷却中", amount: spec.CooldownRemaining);
            }

            context ??= spec.DefaultContext;
            try
            {
                if (definition.Cost != null)
                    new GameplayEffectSpec(definition.Cost, Attributes, this, context: context).Validate(Attributes);
                if (definition.Cooldown != null)
                    new GameplayEffectSpec(definition.Cooldown, Attributes, this, context: context).Validate(Attributes);
                return CheckCost(definition.Cost, context);
            }
            catch (InvalidOperationException error)
            { return new AbilityActivationCheck(AbilityActivationResult.InvalidConfiguration, error.Message); }
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

            int cancelled = 0;
            for (int i = _abilities.Count - 1; i >= 0; i--)
            {
                GameplayAbilitySpec spec = _abilities[i];
                if (spec.IsActive && spec.Definition.AbilityTags.HasAnyMatching(tags) && EndAbility(spec))
                {
                    cancelled++;
                }
            }

            return cancelled;
        }

        public int CancelAllAbilities()
        {
            int cancelled = 0;
            for (int i = _abilities.Count - 1; i >= 0; i--)
            {
                if (EndAbility(_abilities[i]))
                {
                    cancelled++;
                }
            }

            return cancelled;
        }

        /// <summary>回收复用前的清场：结束全部能力（连带取消其任务）、清空效果与标签。属性保留，由下次初始化覆写。</summary>
        public void Clear()
        {
            if (_clearing) return;
            _clearing = true;
            Exception error = null;
            try
            {
            Generation++;
            SourceIdentity = new AbilitySourceIdentity(this);
            for (int i = _abilities.Count - 1; i >= 0; i--)
                if (i < _abilities.Count) try { EndAbility(_abilities[i]); } catch (Exception exception) { error ??= exception; }
            _abilities.Clear();
            _tasks.Clear();
            try { Effects.Clear(); } catch (Exception exception) { error ??= exception; }
            try { OwnedTags.Clear(); } catch (Exception exception) { error ??= exception; }
            Events.Clear();
            }
            finally { _clearing = false; }
            if (error != null) throw error;
        }

        /// <summary>
        /// 消耗是否付得起。只有 Add 操作可判断（当前值加上负数后不得为负）；
        /// 其余操作无法在不实际结算的情况下判定，跳过检查。
        /// </summary>
        private AbilityActivationCheck CheckCost(GameplayEffectDefinition cost, EffectEvaluationContext context)
        {
            if (cost == null)
            {
                return default;
            }

            var costSpec = new GameplayEffectSpec(cost, Attributes, this, context: context);
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
                        sum += costSpec.Resolve(modifiers[j].Magnitude, Attributes);
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
            try { spec.Definition.ActivationOwnedTags.RemoveFrom(OwnedTags); }
            finally { CancelTasksOf(spec, version); }
        }

        /// <summary>能力结束即取消其全部在跑任务，不留无主的跨帧状态。</summary>
        private void CancelTasksOf(GameplayAbilitySpec spec, int version)
        {
            Exception error = null;
            for (int i = _tasks.Count - 1; i >= 0; i--)
            {
                if (i >= _tasks.Count)
                {
                    continue;
                }

                GameplayAbilityTask task = _tasks[i];
                if (!ReferenceEquals(task.Ability, spec) || task.ActivationVersion != version)
                {
                    continue;
                }

                _tasks.Remove(task);
                try { task.CancelInternal(); } catch (Exception exception) { error ??= exception; }
            }
            if (error != null) throw error;
        }
    }
}
