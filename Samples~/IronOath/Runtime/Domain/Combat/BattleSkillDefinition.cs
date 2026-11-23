using System;
using ZFramework.GAS;

namespace IronOath.Combat
{
    /// <summary>Immutable definitions are shared. GAS owns AP/PP payment and all damage/healing arithmetic.</summary>
    public sealed class BattleSkillDefinition
    {
        public BattleSkillId Id { get; }
        public string Name { get; }
        public string Description { get; }
        public int ApCost { get; }
        public int PpCost { get; }
        public bool Physical { get; }
        public bool Precision { get; }
        public bool Blunt { get; }
        public bool Backstab { get; }
        public float Power { get; }
        public float ArmorMultiplier { get; }
        public float Penetration { get; }
        public GameplayAbilityDefinition Ability { get; }
        public GameplayEffectDefinition Effect { get; }

        internal BattleSkillDefinition(BattleSkillId id, string name, string description, int ap, int pp,
            float power, float armorMultiplier, float penetration, bool physical = true,
            bool precision = false, bool blunt = false, bool backstab = false)
        {
            Id = id; Name = name; Description = description; ApCost = ap; PpCost = pp;
            Power = power; ArmorMultiplier = armorMultiplier; Penetration = penetration;
            Physical = physical; Precision = precision; Blunt = blunt; Backstab = backstab;
            EffectModifier[] costs = ap > 0 && pp > 0 ? new[] { Cost(CombatAttributes.AP, ap), Cost(CombatAttributes.PP, pp) }
                : ap > 0 ? new[] { Cost(CombatAttributes.AP, ap) } : pp > 0 ? new[] { Cost(CombatAttributes.PP, pp) } : Array.Empty<EffectModifier>();
            Ability = new GameplayAbilityDefinition(cost: costs.Length == 0 ? null : new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: costs));
            Effect = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: id == BattleSkillId.Heal
                ? new[] { new EffectModifier(CombatAttributes.Health, ModifierOperation.Add, EffectMagnitude.FromCalculation(new HealCalculation(power))) }
                : new[]
                {
                    new EffectModifier(CombatAttributes.Armor, ModifierOperation.Add, EffectMagnitude.FromCalculation(new DamageCalculation(this, 0))),
                    new EffectModifier(CombatAttributes.Health, ModifierOperation.Add, EffectMagnitude.FromCalculation(new DamageCalculation(this, 1))),
                    new EffectModifier(CombatAttributes.PP, ModifierOperation.Add, EffectMagnitude.FromCalculation(new DamageCalculation(this, 2)))
                });
        }

        private static EffectModifier Cost(AttributeId attribute, int amount)
            => new EffectModifier(attribute, ModifierOperation.Add, EffectMagnitude.Fixed(-amount));

        private sealed class HealCalculation : IEffectCalculation
        {
            private readonly float _power;
            public HealCalculation(float power) { _power = power; }
            public float Evaluate(in GameplayEffectSpec spec, AttributeSet target)
                => Math.Max(0, Math.Min(spec.SourceValue(CombatAttributes.Attack) * _power,
                    target.GetCurrent(CombatAttributes.MaxHealth) - target.GetCurrent(CombatAttributes.Health)));
        }

        private sealed class DamageCalculation : IEffectCalculation
        {
            private readonly BattleSkillDefinition _skill;
            private readonly int _component;
            public DamageCalculation(BattleSkillDefinition skill, int component) { _skill = skill; _component = component; }

            public float Evaluate(in GameplayEffectSpec spec, AttributeSet target)
            {
                float armor = target.GetCurrent(CombatAttributes.Armor);
                CombatStatus status = (CombatStatus)(int)target.GetCurrent(CombatAttributes.Status);
                bool gap = armor <= 0 || _skill.Backstab || (status & BattleUnit.GapStatuses) != 0;
                float power = spec.SourceValue(CombatAttributes.Attack) * _skill.Power;
                if (_skill.Id == BattleSkillId.Collision)
                    power = 6 + spec.SourceValue(CombatAttributes.ImpactArmor) * .15f + spec.SourceValue(CombatAttributes.Mass) * 3;
                else if (_skill.Id == BattleSkillId.Conduction)
                    power = 4 + spec.SourceValue(CombatAttributes.ImpactArmor) * .18f;
                else if (_skill.Id == BattleSkillId.Burn) power = 6;
                if (_skill.Id == BattleSkillId.Firebomb && (status & (CombatStatus.Oiled | CombatStatus.Rooted)) != 0) power *= 2;

                bool guardDisabled = _skill.Backstab || (status & BattleUnit.GapStatuses) != 0;
                float guard = target.GetCurrent(CombatAttributes.Guarding) > 0 && !guardDisabled ? .7f : 1;
                float armorDamage = power * _skill.ArmorMultiplier * guard;
                if (_skill.Precision && gap) armorDamage = 0;
                if (_skill.Blunt && (status & CombatStatus.Frozen) != 0) armorDamage *= 2.5f;
                if (_component == 0) return -Math.Min(armor, armorDamage);
                if (_component == 2)
                    return armor > 0 && armorDamage >= armor ? -Math.Min(1, target.GetCurrent(CombatAttributes.PP)) : 0;

                // All three modifiers resolve before the first write: the breaking hit observes the old armor.
                float buffer = gap || !_skill.Physical ? 0 : target.GetCurrent(CombatAttributes.Buffer);
                float healthDamage = _skill.Physical && armor <= 0 ? power : power * _skill.Penetration * (1 - buffer);
                return -Math.Min(target.GetCurrent(CombatAttributes.Health), healthDamage * guard);
            }
        }
    }
}
