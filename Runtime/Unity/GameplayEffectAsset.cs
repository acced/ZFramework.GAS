using GameplayTags;
using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [Serializable]
    public sealed class MagnitudeAsset
    {
        [SerializeField] private MagnitudeKind kind = MagnitudeKind.Fixed;
        [SerializeField] private float constant;
        [SerializeField] private float coefficient = 1f;
        [SerializeField, AttributeIdField] private int captureAttributeId;
        [SerializeField] private MagnitudeCapture captureFrom = MagnitudeCapture.Source;
        [SerializeField, SkillParameterReference] private string parameterId;
        [SerializeField] private float[] levels = new[] { 0f };
        [SerializeReference] private IEffectCalculation calculation;

        public EffectMagnitude Build() => kind switch
        {
            MagnitudeKind.Fixed => EffectMagnitude.Fixed(constant),
            MagnitudeKind.AttributeScaled => EffectMagnitude.FromAttribute(new AttributeId(captureAttributeId), coefficient, captureFrom, constant),
            MagnitudeKind.Parameter => EffectMagnitude.FromParameter(parameterId, coefficient, constant),
            MagnitudeKind.LevelTable => EffectMagnitude.ByLevel(levels),
            MagnitudeKind.Calculation => EffectMagnitude.FromCalculation(calculation),
            _ => throw new InvalidOperationException($"Unhandled magnitude kind {kind}."),
        };
    }

    [Serializable]
    public sealed class ModifierAsset
    {
        [SerializeField, AttributeIdField] private int targetAttributeId;
        [SerializeField] private ModifierOperation operation = ModifierOperation.Add;
        [SerializeField] private MagnitudeAsset magnitude = new();

        public EffectModifier Build() => new(new AttributeId(targetAttributeId), operation, magnitude.Build());
    }

    [Serializable]
    public sealed class EffectTagsAsset
    {
        [SerializeField] private GameplayTag[] granted = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] applicationRequired = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] applicationBlocked = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] ongoingRequired = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] ongoingBlocked = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] removeEffectsWith = Array.Empty<GameplayTag>();

        public EffectTags Build() => new(
            TagAssetUtility.ToSet(granted), TagAssetUtility.ToSet(applicationRequired),
            TagAssetUtility.ToSet(applicationBlocked), TagAssetUtility.ToSet(ongoingBlocked),
            TagAssetUtility.ToSet(removeEffectsWith), TagAssetUtility.ToSet(ongoingRequired));
    }

    [CreateAssetMenu(menuName = "ZFramework GAS/Gameplay Effect", fileName = "NewGameplayEffect")]
    public sealed class GameplayEffectAsset : ScriptableObject, IDefinitionAsset
    {
        [Header("时长")]
        [SerializeField] private EffectDurationPolicy durationPolicy = EffectDurationPolicy.Instant;
        [SerializeField] private float duration;
        [SerializeField] private bool useDurationMagnitude;
        [SerializeField] private MagnitudeAsset durationMagnitude = new();

        [Header("周期")]
        [SerializeField] private float period;
        [SerializeField] private bool usePeriodMagnitude;
        [SerializeField] private MagnitudeAsset periodMagnitude = new();
        [SerializeField] private bool executePeriodicOnApplication;

        [Header("修改器与标签")]
        [SerializeField] private ModifierAsset[] modifiers = Array.Empty<ModifierAsset>();
        [SerializeField] private EffectTagsAsset tags = new();
        [SerializeField] private GameplayTag[] cueTags = Array.Empty<GameplayTag>();

        [Header("堆叠")]
        [SerializeField] private EffectStacking stackingMode = EffectStacking.None;
        [SerializeField] private int stackLimit;
        [SerializeField] private StackOverflowPolicy overflowPolicy = StackOverflowPolicy.Deny;
        [SerializeField] private StackDurationRefreshPolicy stackDurationRefresh = StackDurationRefreshPolicy.RefreshOnApply;
        [SerializeField] private StackPeriodResetPolicy stackPeriodReset = StackPeriodResetPolicy.KeepProgress;

        [Header("子效果")]
        [SerializeField] private ConditionalEffectAsset[] conditionalEffects = Array.Empty<ConditionalEffectAsset>();

        [NonSerialized] private GameplayEffectDefinition _definition;
        [NonSerialized] private bool _building;

        public GameplayEffectDefinition Definition
        {
            get
            {
                if (_definition != null) return _definition;
                if (_building) throw new InvalidOperationException(name + ": 效果循环引用。");
                _building = true;
                try { return _definition = Build(); } finally { _building = false; }
            }
        }

        private GameplayEffectDefinition Build()
        {
            var mods = modifiers.Length == 0 ? Array.Empty<EffectModifier>() : new EffectModifier[modifiers.Length];
            for (int i = 0; i < mods.Length; i++) mods[i] = modifiers[i].Build();
            var children = conditionalEffects.Length == 0 ? Array.Empty<ConditionalEffect>() : new ConditionalEffect[conditionalEffects.Length];
            for (int i = 0; i < children.Length; i++) children[i] = conditionalEffects[i].Build(this);

            var def = new GameplayEffectDefinition(
                durationPolicy, duration, mods, tags.Build(),
                new EffectStackingRule(stackingMode, stackLimit, overflowPolicy, stackDurationRefresh, stackPeriodReset),
                period, executePeriodicOnApplication, TagAssetUtility.ToSet(cueTags), children,
                useDurationMagnitude ? durationMagnitude.Build() : null,
                usePeriodMagnitude ? periodMagnitude.Build() : null);
            DefinitionSource.Register(def, this);
            return def;
        }

        public void InvalidateDefinition() { _definition = null; DefinitionSource.InvalidateDependents(this); }
        private void OnValidate() => InvalidateDefinition();
    }

    [Serializable]
    public sealed class ConditionalEffectAsset
    {
        [SerializeField] private GameplayEffectAsset effect;
        [SerializeField] private EffectTrigger trigger = EffectTrigger.Applied;

        public ConditionalEffect Build(GameplayEffectAsset owner)
        {
            if (effect == null) throw new InvalidOperationException(owner.name + ": 子效果缺少效果资产。");
            DefinitionSource.DependsOn(owner, effect);
            return new ConditionalEffect(effect.Definition, trigger);
        }
    }
}
