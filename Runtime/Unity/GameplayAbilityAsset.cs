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
    public sealed class GameplayAbilityAsset : ScriptableObject, IDefinitionAsset
    {
        [Header("展示")]
        public string displayName;
        [TextArea] public string description;
        public Sprite icon;
        public string DisplayName => string.IsNullOrEmpty(displayName) ? name : displayName;
        [Header("参数")]
        public SkillParameter[] parameters = Array.Empty<SkillParameter>();
        [Header("激活动作（按顺序执行）")]
        [SerializeReference] public SkillAction[] activationActions = Array.Empty<SkillAction>();
        public SkillParameterValues ResolveParameters(int level, SkillParameterOverride[] overrides = null)
        {
            var values = new Dictionary<string, SkillParameterValue>(parameters.Length);
            foreach (var parameter in parameters) values.Add(parameter.Id, parameter.Resolve(level));
            if (overrides != null)
                foreach (var entry in overrides)
                {
                    var value = entry.Resolve(level);
                    if (values[entry.parameterId].Kind != value.Kind) throw new InvalidOperationException("参数类型不匹配: " + entry.parameterId);
                    values[entry.parameterId] = value;
                }
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
        [Tooltip("激活时提交消耗与冷却。关闭后由「提交消耗与冷却」动作或 CommitAbility 显式提交。")]
        public bool autoCommit = true;
        [Tooltip("必须是 Instant 效果")]
        [SerializeField] private GameplayEffectAsset cost;
        [Tooltip("必须至少授予一个标签——那个标签就是「在冷却中」的标志")]
        [SerializeField] private GameplayEffectAsset cooldown;

        [Header("表现（由扩展解释）")]
        [SerializeField] private ScriptableObject animation;
        public ScriptableObject Presentation { get => animation; set => animation = value; }

        [NonSerialized] private GameplayAbilityDefinition _definition;

        /// <summary>构建（并缓存）运行期定义。同一资产始终返回同一实例。</summary>
        public GameplayAbilityDefinition Definition => _definition ??= Build();

        private GameplayAbilityDefinition Build()
        {
            DefinitionSource.DependsOn(this, cost); DefinitionSource.DependsOn(this, cooldown);
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
