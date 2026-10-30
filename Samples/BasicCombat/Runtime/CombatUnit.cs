using System;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Samples.BasicCombat
{
    /// <summary>
    /// 示例单位：持有一个 ASC 与四项属性。实现 <see cref="IAbilitySystemProvider"/>，
    /// 让投射物、区域查询能从碰撞体反查到它。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CombatUnit : MonoBehaviour, IAbilitySystemProvider
    {
        public static readonly AttributeId Health = new(1), MaxHealth = new(2), Attack = new(3), Stamina = new(4);

        [SerializeField] private string displayName = "单位";
        [SerializeField] private float maxHealth = 300;
        [SerializeField] private float attack = 20;
        [SerializeField] private float maxStamina = 100;
        [Tooltip("血条与跳字的挂点")]
        [SerializeField] private Transform hudAnchor;

        public string DisplayName => displayName;
        public AbilitySystemComponent AbilitySystem { get; private set; }
        public SkillRunner SkillRunner { get; private set; }
        public float MaxStamina => maxStamina;
        public Vector3 HudPosition => hudAnchor != null ? hudAnchor.position : transform.position + Vector3.up * 2.1f;

        /// <summary>生命发生变化（正数为治疗，负数为伤害）。</summary>
        public event Action<CombatUnit, float> HealthChanged;

        public void Initialize(GameplayCueRouter router)
        {
            AbilitySystem = new AbilitySystemComponent(4, router);
            GetComponent<CueVfxPlayer>()?.Bind(AbilitySystem);
            SkillRunner = new SkillRunner(AbilitySystem, () =>
            {
                var animator = GetComponent<SkillAnimator>();
                return animator != null ? animator.Driver : null;
            });
            var health = AbilitySystem.Attributes.Add(Health, maxHealth);
            AbilitySystem.Attributes.Add(MaxHealth, maxHealth);
            AbilitySystem.Attributes.Add(Attack, attack);
            AbilitySystem.Attributes.Add(Stamina, maxStamina);
            float reported = maxHealth;
            health.OnCurrentValueChanged += (attribute, before, after) =>
            {
                // 治疗不超过上限：在回调里改同一属性，两次迭代内收敛。
                // 差值相对上次上报的值计算，钳制回落那一次不会被误报成伤害。
                float cap = AbilitySystem.Attributes.GetCurrent(MaxHealth);
                if (after > cap) { attribute.BaseValue = cap; return; }
                float delta = after - reported;
                reported = after;
                if (Mathf.Abs(delta) > .01f) HealthChanged?.Invoke(this, delta);
            };
        }

        public float Get(AttributeId id) => AbilitySystem.Attributes.GetCurrent(id);

        public void SetBase(AttributeId id, float value)
        {
            var attribute = AbilitySystem.Attributes.Get(id);
            if (attribute != null) attribute.BaseValue = value;
        }
    }
}
