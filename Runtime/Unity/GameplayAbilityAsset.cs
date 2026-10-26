using GameplayTags;
using System;
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
        [Tooltip("技能动画与其时间轴；由宿主在施法时配上一个 IAnimationDriver 起 AnimationTask")]
        [SerializeField] private SkillAnimationAsset animation;

        [NonSerialized] private GameplayAbilityDefinition _definition;

        /// <summary>
        /// 技能动画；可为 null。<b>不进 <see cref="Definition"/></b>——
        /// 能力定义在纯 C# 核心程序集里，不认识任何引擎类型，动画绑定只挂在 Unity 层的资产上。
        /// </summary>
        public SkillAnimationAsset Animation => animation;

        /// <summary>构建（并缓存）运行期定义。同一资产始终返回同一实例。</summary>
        public GameplayAbilityDefinition Definition => _definition ??= new GameplayAbilityDefinition(
            TagAssetUtility.ToSet(abilityTags),
            TagAssetUtility.ToSet(activationRequiredTags),
            TagAssetUtility.ToSet(activationBlockedTags),
            TagAssetUtility.ToSet(activationOwnedTags),
            TagAssetUtility.ToSet(cancelAbilitiesWithTags),
            cost != null ? cost.Definition : null,
            cooldown != null ? cooldown.Definition : null);

        private void OnValidate() => _definition = null;
    }
}
