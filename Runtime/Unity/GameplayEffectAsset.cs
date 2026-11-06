using GameplayTags;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// 数值来源的可序列化镜像。运行期类型 <see cref="EffectMagnitude"/> 是 readonly struct，
    /// Unity 不序列化只读字段，因此授权侧需要这一层。
    /// </summary>
    [Serializable]
    public sealed class MagnitudeAsset
    {
        [SerializeField] private MagnitudeKind kind = MagnitudeKind.Fixed;
        [SerializeField] private float constant;
        [SerializeField] private float coefficient = 1f;
        [SerializeField, AttributeIdField] private int captureAttributeId;
        [SerializeField] private MagnitudeCapture captureFrom = MagnitudeCapture.Source;
        [SerializeField, CallerKey] private string callerKey;
        [SerializeField, SkillParameterReference] private string parameterId;
        [SerializeField] private float[] levels = new[] { 0f };
        [SerializeField, AttributeIdField] private int targetAttributeId;
        [SerializeField] private float targetCoefficient = 1;
        [SerializeField] private float minimum;
        [SerializeField] private float maximum = float.MaxValue;
        [SerializeField, SkillParameterReference] private string multiplierParameterId;
        [SerializeField] private float resultScale = -1;

        public EffectMagnitude Build() => kind switch
        {
            MagnitudeKind.Fixed => EffectMagnitude.Fixed(constant),
            MagnitudeKind.AttributeScaled => EffectMagnitude.FromAttribute(
                new AttributeId(captureAttributeId), coefficient, captureFrom, constant),
            MagnitudeKind.SetByCaller => EffectMagnitude.FromCaller(callerKey),
            MagnitudeKind.Parameter => EffectMagnitude.FromParameter(parameterId, coefficient, constant),
            MagnitudeKind.LevelTable => EffectMagnitude.ByLevel(levels),
            MagnitudeKind.Calculation => EffectMagnitude.FromCalculation(new AttributeFormula(new AttributeId(captureAttributeId),
                new AttributeId(targetAttributeId), coefficient, targetCoefficient, constant, parameterId, minimum, maximum, resultScale,multiplierParameterId)),
            _ => throw new InvalidOperationException($"Unhandled magnitude kind {kind}."),
        };
    }

    [Serializable]
    public sealed class ModifierAsset
    {
        [SerializeField, AttributeIdField] private int targetAttributeId;
        [SerializeField] private ModifierOperation operation = ModifierOperation.Add;
        [SerializeField] private MagnitudeAsset magnitude = new();

        public EffectModifier Build()
            => new(new AttributeId(targetAttributeId), operation, magnitude.Build());
    }

    [Serializable]
    public sealed class EffectTagsAsset
    {
        [SerializeField] private GameplayTag[] granted = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] applicationRequired = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] applicationBlocked = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] ongoingBlocked = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] removeEffectsWith = Array.Empty<GameplayTag>();

        public EffectTags Build() => new(
            TagAssetUtility.ToSet(granted),
            TagAssetUtility.ToSet(applicationRequired),
            TagAssetUtility.ToSet(applicationBlocked),
            TagAssetUtility.ToSet(ongoingBlocked),
            TagAssetUtility.ToSet(removeEffectsWith));
    }

    /// <summary>
    /// 效果定义的授权资产。在 Inspector 里配置，运行期经 <see cref="Definition"/> 得到不可变定义。
    ///
    /// 与 UE 一致：GameplayEffect 本身就是数据资产，不需要节点图。
    ///
    /// <see cref="Definition"/> **缓存并复用同一个实例**——堆叠身份按定义引用判定，
    /// 每次返回新实例会让同一资产施加的效果互不堆叠。
    /// </summary>
    [CreateAssetMenu(menuName = "ZFramework GAS/Gameplay Effect", fileName = "NewGameplayEffect")]
    public sealed class GameplayEffectAsset : ScriptableObject, IDefinitionAsset
    {
        [Header("时长")]
        [SerializeField] private EffectDurationPolicy durationPolicy = EffectDurationPolicy.Instant;
        [SerializeField] private float duration;
        [SerializeField] private bool useDurationMagnitude;
        [SerializeField] private MagnitudeAsset durationMagnitude = new();
        [Tooltip("大于 0 时改从来源的该属性读取时长，覆盖上面的固定值")]
        [SerializeField, AttributeIdField] private int durationAttributeId = -1;

        [Header("周期")]
        [SerializeField] private float period;
        [SerializeField] private bool usePeriodMagnitude;
        [SerializeField] private MagnitudeAsset periodMagnitude = new();
        [Tooltip("大于等于 0 时改从来源的该属性读取周期，覆盖上面的固定值")]
        [SerializeField, AttributeIdField] private int periodAttributeId = -1;
        [SerializeField] private bool executePeriodicOnApplication;

        [Header("修改器与标签")]
        [SerializeField] private ModifierAsset[] modifiers = Array.Empty<ModifierAsset>();
        [SerializeField] private EffectTagsAsset tags = new();
        [SerializeField] private GameplayTag[] cueTags = Array.Empty<GameplayTag>();

        [Header("堆叠")]
        [SerializeField] private EffectStacking stackingMode = EffectStacking.None;
        [SerializeField] private int stackLimit;
        [SerializeField] private StackOverflowPolicy overflowPolicy = StackOverflowPolicy.Deny;

        [Header("子效果")]
        [SerializeField] private ConditionalEffectAsset[] conditionalEffects = Array.Empty<ConditionalEffectAsset>();

        [NonSerialized] private GameplayEffectDefinition _definition;

        /// <summary>构建（并缓存）运行期定义。同一资产始终返回同一实例。</summary>
        private bool _building;
        public GameplayEffectDefinition Definition
        {
            get
            {
                if (_definition != null) return _definition;
                if (_building) throw new InvalidOperationException(name + ": 效果循环引用。");
                _building = true; try { return _definition = Build(); } finally { _building = false; }
            }
        }

        private GameplayEffectDefinition Build()
        {
            var builtModifiers = new EffectModifier[modifiers.Length];
            for (int i = 0; i < modifiers.Length; i++)
            {
                builtModifiers[i] = modifiers[i].Build();
            }

            var children = new List<ConditionalEffect>(conditionalEffects.Length);
            for (int i = 0; i < conditionalEffects.Length; i++)
            {
                ConditionalEffect? child = conditionalEffects[i].Build(this);
                if (child.HasValue)
                {
                    children.Add(child.Value);
                }
            }

            var definition = new GameplayEffectDefinition(
                durationPolicy,
                duration,
                builtModifiers,
                tags.Build(),
                new EffectStackingRule(stackingMode, stackLimit, overflowPolicy),
                period,
                periodAttributeId >= 0 ? new AttributeId(periodAttributeId) : null,
                executePeriodicOnApplication,
                TagAssetUtility.ToSet(cueTags),
                durationAttributeId >= 0 ? new AttributeId(durationAttributeId) : null,
                children.Count == 0 ? null : children.ToArray(),
                useDurationMagnitude ? durationMagnitude.Build() : null,
                usePeriodMagnitude ? periodMagnitude.Build() : null);
            DefinitionSource.Register(definition, this);
            return definition;
        }

        /// <summary>编辑期修改后清掉缓存，避免继续用旧定义。</summary>
        public void InvalidateDefinition() { _definition = null; DefinitionSource.InvalidateDependents(this); }
        private void OnValidate() => InvalidateDefinition();
    }

    [Serializable]
    public sealed class ConditionalEffectAsset
    {
        [SerializeField] private GameplayEffectAsset effect;
        [SerializeField] private EffectTrigger trigger = EffectTrigger.Applied;

        /// <summary>未配置子效果资产时返回 null 并告警——静默丢弃会让配置错误难以察觉。</summary>
        public ConditionalEffect? Build(GameplayEffectAsset owner)
        {
            if (effect == null)
            {
                Debug.LogWarning($"[{owner.name}] A conditional effect entry has no effect asset assigned; it was skipped.", owner);
                return null;
            }

            if (effect == owner)
            {
                Debug.LogError($"[{owner.name}] A conditional effect references its own asset, which would recurse; it was skipped.", owner);
                return null;
            }

            DefinitionSource.DependsOn(owner, effect);
            return new ConditionalEffect(effect.Definition, trigger);
        }
    }
}
