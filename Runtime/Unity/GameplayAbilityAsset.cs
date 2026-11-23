using GameplayTags;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
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

        [NonSerialized] private SkillActionDefinition[] _actions;
        [NonSerialized] private GameplayAbilityDefinition _definition;

        public SkillParameterValues ResolveParameters(int level, SkillParameterOverride[] overrides = null)
        {
            int pCount = parameters?.Length ?? 0, oCount = overrides?.Length ?? 0;
            if (pCount == 0 && oCount == 0) return SkillParameterValues.Empty;

            var values = new Dictionary<string, SkillParameterValue>(pCount, StringComparer.Ordinal);
            for (int i = 0; i < pCount; i++)
            {
                SkillParameter p = parameters[i];
                if (string.IsNullOrEmpty(p.Id) || !values.TryAdd(p.Id, p.Resolve(level)))
                    throw new InvalidOperationException("参数 ID 缺失或重复: " + p.Id);
            }
            for (int i = 0; i < oCount; i++)
            {
                SkillParameterOverride o = overrides[i];
                if (string.IsNullOrEmpty(o.parameterId) || !values.TryGetValue(o.parameterId, out var declared))
                    throw new InvalidOperationException("未声明的技能参数覆盖: " + o.parameterId);
                SkillParameterValue value = o.Resolve(level);
                if (declared.Kind != value.Kind) throw new InvalidOperationException("参数类型不匹配: " + o.parameterId);
                values[o.parameterId] = value;
            }
            return values.Count == 0 ? SkillParameterValues.Empty : new SkillParameterValues(values, takeOwnership: true);
        }

        public SkillActionDefinition[] BuildActions()
            => _actions ??= new SkillActionBuildContext(this).Build(activationActions);

        [Header("标签")]
        [SerializeField] private GameplayTag[] abilityTags = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] activationRequiredTags = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] activationBlockedTags = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] activationOwnedTags = Array.Empty<GameplayTag>();
        [SerializeField] private GameplayTag[] cancelAbilitiesWithTags = Array.Empty<GameplayTag>();

        [Header("消耗与冷却")]
        public bool autoCommit = true;
        [SerializeField] private GameplayEffectAsset cost;
        [SerializeField] private GameplayEffectAsset cooldown;

        [Header("表现（由扩展解释）")]
        [SerializeField] private ScriptableObject animation;
        public ScriptableObject Presentation { get => animation; set => animation = value; }

        public GameplayAbilityDefinition Definition => _definition ??= Build();

        private GameplayAbilityDefinition Build()
        {
            DefinitionSource.DependsOn(this, cost);
            DefinitionSource.DependsOn(this, cooldown);
            var def = new GameplayAbilityDefinition(
                TagAssetUtility.ToSet(abilityTags), TagAssetUtility.ToSet(activationRequiredTags),
                TagAssetUtility.ToSet(activationBlockedTags), TagAssetUtility.ToSet(activationOwnedTags),
                TagAssetUtility.ToSet(cancelAbilitiesWithTags),
                cost != null ? cost.Definition : null, cooldown != null ? cooldown.Definition : null);
            DefinitionSource.Register(def, this);
            return def;
        }

        public void InvalidateDefinition() { _definition = null; _actions = null; DefinitionSource.InvalidateDependents(this); }
        private void OnValidate() => InvalidateDefinition();
    }
}
