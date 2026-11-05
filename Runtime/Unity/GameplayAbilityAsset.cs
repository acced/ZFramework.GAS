using GameplayTags;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// 能力定义的授权资产。消耗与冷却直接引用两个效果资产——
    /// 与 UE 的 <c>CostGameplayEffectClass</c> / <c>CooldownGameplayEffectClass</c> 同构。
    ///
    /// <see cref="Definition"/> 同样缓存复用同一实例。
    /// </summary>
    [CreateAssetMenu(menuName = "ZFramework GAS/Gameplay Ability", fileName = "NewGameplayAbility")]
    public sealed class GameplayAbilityAsset : ScriptableObject
    {
        [Header("展示")]
        public string displayName;
        [TextArea] public string description;
        public Sprite icon;
        public string DisplayName => string.IsNullOrEmpty(displayName) ? name : displayName;
        [Header("参数")]
        public SkillParameterSetAsset parameterSet;
        public SkillParameter[] parameters = Array.Empty<SkillParameter>();
        public SkillParameterOverride[] parameterOverrides = Array.Empty<SkillParameterOverride>();
        [Header("激活动作（按顺序执行）")]
        public SkillClipOverrideSet clipOverrides;
        [SerializeReference] public SkillAction[] activationActions = Array.Empty<SkillAction>();
        public SkillParameterValues ResolveParameters(int level, SkillParameterOverride[] castOverrides = null)
            => ResolveParameters(level, null, castOverrides);
        internal SkillParameterValues ResolveParameters(int level, SkillParameterOverride[] grant, SkillParameterOverride[] cast)
        {
            var values = new Dictionary<string, SkillParameterValue>();
            if (parameterSet != null) SkillParameterSetAsset.AddTo(values, parameterSet.parameters, level);
            SkillParameterSetAsset.AddTo(values, parameters, level);
            SkillParameterSetAsset.Apply(values, parameterOverrides, level);
            SkillParameterSetAsset.Apply(values, grant, level);
            SkillParameterSetAsset.Apply(values, cast, level);
            return values.Count == 0 ? SkillParameterValues.Empty : new SkillParameterValues(values);
        }
        [NonSerialized] private SkillActionDefinition[] _actions;
        public SkillActionDefinition[] BuildActions() => _actions ??= new SkillActionBuildContext(this).Build(activationActions);
        [Header("标签")]
        [Tooltip("本能力的标识标签，供「取消带某标签的能力」匹配")]
        [SerializeField] private GameplayTag[] abilityTags = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] activationRequiredTags = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] activationBlockedTags = Array.Empty<GameplayTag>();
        [Tooltip("激活期间授予持有者，结束时移除")]
        [SerializeField] private GameplayTag[] activationOwnedTags = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] cancelAbilitiesWithTags = Array.Empty<GameplayTag>();

        [Header("消耗与冷却")]
        [Tooltip("必须是 Instant 效果")]
        [SerializeField] private GameplayEffectAsset cost;
        [Tooltip("必须至少授予一个标签——那个标签就是「在冷却中」的标志")]
        [SerializeField] private GameplayEffectAsset cooldown;

        [Header("动画")]
        [Tooltip("技能动画与其时间轴；通过 SkillRunner 统一施放，也可由宿主直接创建 AnimationTask")]
        [SerializeField] private SkillAnimationAsset animation;

        [Header("调用方参数（声明，不自动注入运行期）")]
        [SerializeField] private CallerParameter[] callerParameters = Array.Empty<CallerParameter>();

        [NonSerialized] private GameplayAbilityDefinition _definition;

        /// <summary>
        /// 技能动画；可为 null。<b>不进 <see cref="Definition"/></b>——
        /// 能力定义在纯 C# 核心程序集里，不认识任何引擎类型，动画绑定只挂在 Unity 层的资产上。
        /// </summary>
        public SkillAnimationAsset Animation => animation;

        /// <summary>构建（并缓存）运行期定义。同一资产始终返回同一实例。</summary>
        public GameplayAbilityDefinition Definition => _definition ??= Build();

        private GameplayAbilityDefinition Build()
        {
            DefinitionSource.DependsOn(this, cost); DefinitionSource.DependsOn(this, cooldown); DefinitionSource.DependsOn(this, parameterSet);
            var definition = new GameplayAbilityDefinition(
                TagAssetUtility.ToSet(abilityTags),
                TagAssetUtility.ToSet(activationRequiredTags),
                TagAssetUtility.ToSet(activationBlockedTags),
                TagAssetUtility.ToSet(activationOwnedTags),
                TagAssetUtility.ToSet(cancelAbilitiesWithTags),
                cost != null ? cost.Definition : null,
                cooldown != null ? cooldown.Definition : null);
            DefinitionSource.Register(definition, this);
            return definition;
        }

        public void InvalidateDefinition() { _definition = null; _actions = null; DefinitionSource.InvalidateDependents(this); }
        private void OnValidate() => InvalidateDefinition();
    }
}
