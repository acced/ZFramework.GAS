using GameplayTags;
using System;

namespace ZFramework.GAS
{
    public enum EffectDurationPolicy
    {
        /// <summary>立即结算并结束，直接改写 BaseValue，不授予标签。</summary>
        Instant,
        /// <summary>持续固定时长后自动过期。</summary>
        Duration,
        /// <summary>持续到被显式移除。</summary>
        Infinite,
    }

    public enum EffectStacking
    {
        None,
        /// <summary>同一目标上的同定义效果合并堆叠，不论来源。</summary>
        AggregateByTarget,
        /// <summary>同一来源施加的同定义效果才合并堆叠。</summary>
        AggregateBySource,
    }

    public enum StackOverflowPolicy
    {
        /// <summary>已达上限时拒绝新应用，仅刷新既有效果。</summary>
        Deny,
        /// <summary>已达上限时移除既有效果，由调用方决定后续（如触发溢出分支）。</summary>
        RemoveExisting,
    }

    public enum MagnitudeKind
    {
        Fixed,
        /// <summary><c>Constant + Coefficient × 属性当前值</c>。</summary>
        AttributeScaled,
        /// <summary>由施加方在 Spec 上按键名提供。</summary>
        SetByCaller,
    }

    public enum MagnitudeCapture
    {
        Source,
        Target,
    }

    /// <summary>
    /// 数值来源。**不解析字符串公式**——公式应在编辑期编译成本结构，运行期只做算术。
    ///
    /// 实测旧资产中出现的全部公式形态（<c>Caster.Attack * 1.5</c>、<c>1 - Caster.SlowRate</c>、
    /// <c>Target.Health * 0.3</c>）都可由 <c>Constant + Coefficient × 属性</c> 表达。
    /// </summary>
    public readonly struct EffectMagnitude
    {
        public readonly MagnitudeKind Kind;
        public readonly float Constant;
        public readonly float Coefficient;
        public readonly AttributeId CaptureAttribute;
        public readonly MagnitudeCapture CaptureFrom;
        public readonly string CallerKey;

        private EffectMagnitude(
            MagnitudeKind kind, float constant, float coefficient,
            AttributeId captureAttribute, MagnitudeCapture captureFrom, string callerKey)
        {
            Kind = kind;
            Constant = constant;
            Coefficient = coefficient;
            CaptureAttribute = captureAttribute;
            CaptureFrom = captureFrom;
            CallerKey = callerKey;
        }

        public static EffectMagnitude Fixed(float value)
            => new(MagnitudeKind.Fixed, value, 0f, default, default, null);

        public static EffectMagnitude FromAttribute(
            AttributeId attribute, float coefficient, MagnitudeCapture captureFrom, float constant = 0f)
            => new(MagnitudeKind.AttributeScaled, constant, coefficient, attribute, captureFrom, null);

        public static EffectMagnitude FromCaller(string key)
            => new(MagnitudeKind.SetByCaller, 0f, 0f, default, default,
                key ?? throw new ArgumentNullException(nameof(key)));
    }

    public readonly struct EffectModifier
    {
        public readonly AttributeId Target;
        public readonly ModifierOperation Operation;
        public readonly EffectMagnitude Magnitude;

        public EffectModifier(AttributeId target, ModifierOperation operation, in EffectMagnitude magnitude)
        {
            Target = target;
            Operation = operation;
            Magnitude = magnitude;
        }
    }

    /// <summary>效果涉及的五组标签，作为一个整体随定义传递。</summary>
    public readonly struct EffectTags
    {
        /// <summary>效果生效期间授予目标的标签（Instant 效果不授予）。</summary>
        public readonly GameplayTagSet Granted;
        /// <summary>目标必须全部拥有，否则无法应用。</summary>
        public readonly GameplayTagSet ApplicationRequired;
        /// <summary>目标拥有其中任一即免疫本效果。</summary>
        public readonly GameplayTagSet ApplicationBlocked;
        /// <summary>生效期间目标获得其中任一，本效果立即移除。</summary>
        public readonly GameplayTagSet OngoingBlocked;
        /// <summary>应用成功时，移除目标身上授予了其中任一标签的其他效果。</summary>
        public readonly GameplayTagSet RemoveEffectsWith;

        public EffectTags(
            GameplayTagSet granted = default,
            GameplayTagSet applicationRequired = default,
            GameplayTagSet applicationBlocked = default,
            GameplayTagSet ongoingBlocked = default,
            GameplayTagSet removeEffectsWith = default)
        {
            Granted = granted;
            ApplicationRequired = applicationRequired;
            ApplicationBlocked = applicationBlocked;
            OngoingBlocked = ongoingBlocked;
            RemoveEffectsWith = removeEffectsWith;
        }
    }

    public readonly struct EffectStackingRule
    {
        public readonly EffectStacking Mode;
        /// <summary>层数上限；<c>&lt;= 0</c> 表示不限。</summary>
        public readonly int Limit;
        public readonly StackOverflowPolicy Overflow;

        public EffectStackingRule(EffectStacking mode, int limit = 0, StackOverflowPolicy overflow = StackOverflowPolicy.Deny)
        {
            Mode = mode;
            Limit = limit;
            Overflow = overflow;
        }
    }

    /// <summary>子效果的触发时机。</summary>
    public enum EffectTrigger
    {
        /// <summary>父效果成功应用时（Instant 结算完毕、或持续效果开始生效）。</summary>
        Applied,
        /// <summary>父效果每次周期跳动时。</summary>
        Periodic,
        /// <summary>父效果被移除时（到期、被清除、被覆盖）。</summary>
        Removed,
    }

    /// <summary>
    /// 挂在父效果上的子效果：在指定时机施加到**同一目标**，来源沿用父效果的来源。
    ///
    /// 这取代了旧实现中「效果节点的六个图端口连出子节点」的机制——
    /// 子效果自身的 <c>ApplicationRequired/Blocked</c> 标签就是条件判定，无需独立的条件节点。
    /// </summary>
    public readonly struct ConditionalEffect
    {
        public readonly GameplayEffectDefinition Effect;
        public readonly EffectTrigger Trigger;

        public ConditionalEffect(GameplayEffectDefinition effect, EffectTrigger trigger)
        {
            Effect = effect ?? throw new ArgumentNullException(nameof(effect));
            Trigger = trigger;
        }
    }

    /// <summary>
    /// 效果**定义**：不可变的配置，多个目标共享同一实例。
    /// 对应 UE 的 <c>UGameplayEffect</c>（CDO）——定义本身不持有任何运行时状态。
    ///
    /// 堆叠身份即定义的引用相等：同一个定义实例施加的效果才可能相互堆叠。
    /// </summary>
    public sealed class GameplayEffectDefinition
    {
        private static readonly EffectModifier[] NoModifiers = Array.Empty<EffectModifier>();
        private static readonly ConditionalEffect[] NoConditionalEffects = Array.Empty<ConditionalEffect>();

        public EffectDurationPolicy DurationPolicy { get; }
        /// <summary>仅 <see cref="EffectDurationPolicy.Duration"/> 有效，单位秒。</summary>
        public float Duration { get; }
        /// <summary>设置后时长改从来源的该属性读取，覆盖 <see cref="Duration"/>（如冷却时长取攻击间隔）。</summary>
        public AttributeId? DurationAttribute { get; }
        /// <summary>周期间隔秒；<c>&lt;= 0</c> 表示非周期效果。</summary>
        public float Period { get; }
        /// <summary>设置后周期改从来源的该属性读取，覆盖 <see cref="Period"/>（如按攻击间隔跳伤害）。</summary>
        public AttributeId? PeriodAttribute { get; }
        /// <summary>应用当帧是否立即结算一次周期。</summary>
        public bool ExecutePeriodicOnApplication { get; }
        public EffectModifier[] Modifiers { get; }
        public EffectTags Tags { get; }
        private readonly GameplayTagRequirements _applicationRequirements;
        private readonly GameplayTagRequirements _ongoingRequirements;
        public ref readonly GameplayTagRequirements ApplicationRequirements => ref _applicationRequirements;
        public ref readonly GameplayTagRequirements OngoingRequirements => ref _ongoingRequirements;
        public EffectStackingRule Stacking { get; }

        /// <summary>
        /// 表现层标签。Instant 应用与每次周期跳动派发 <see cref="GameplayCueEvent.Executed"/>；
        /// 持续效果生效与移除分别派发 <c>OnActive</c> 与 <c>Removed</c>。
        /// </summary>
        public GameplayTagSet CueTags { get; }

        /// <summary>在本效果的特定时机施加到同一目标的子效果。</summary>
        public ConditionalEffect[] ConditionalEffects { get; }

        public bool IsPeriodic => Period > 0f || PeriodAttribute.HasValue;

        public GameplayEffectDefinition(
            EffectDurationPolicy durationPolicy,
            float duration = 0f,
            EffectModifier[] modifiers = null,
            EffectTags tags = default,
            EffectStackingRule stacking = default,
            float period = 0f,
            AttributeId? periodAttribute = null,
            bool executePeriodicOnApplication = false,
            GameplayTagSet cueTags = default,
            AttributeId? durationAttribute = null,
            ConditionalEffect[] conditionalEffects = null)
        {
            DurationPolicy = durationPolicy;
            Duration = duration;
            DurationAttribute = durationAttribute;
            Modifiers = modifiers ?? NoModifiers;
            Tags = tags;
            _applicationRequirements = GameplayTagRequirements.Create(
                tags.ApplicationBlocked.AsSpan(), tags.ApplicationRequired.AsSpan());
            _ongoingRequirements = GameplayTagRequirements.Create(tags.OngoingBlocked.AsSpan(), default);
            Stacking = stacking;
            Period = period;
            PeriodAttribute = periodAttribute;
            ExecutePeriodicOnApplication = executePeriodicOnApplication;
            CueTags = cueTags;
            ConditionalEffects = conditionalEffects ?? NoConditionalEffects;
        }
    }
}
