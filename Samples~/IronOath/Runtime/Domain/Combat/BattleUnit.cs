using System;
using ZFramework.GAS;

namespace IronOath.Combat
{
    public static class CombatAttributes
    {
        public static readonly AttributeId Health = new AttributeId(1100);
        public static readonly AttributeId MaxHealth = new AttributeId(1101);
        public static readonly AttributeId Armor = new AttributeId(1102);
        public static readonly AttributeId MaxArmor = new AttributeId(1103);
        public static readonly AttributeId Attack = new AttributeId(1104);
        public static readonly AttributeId Speed = new AttributeId(1105);
        public static readonly AttributeId AP = new AttributeId(1106);
        public static readonly AttributeId PP = new AttributeId(1107);
        public static readonly AttributeId Buffer = new AttributeId(1108);
        public static readonly AttributeId Status = new AttributeId(1109);
        public static readonly AttributeId Guarding = new AttributeId(1110);
        public static readonly AttributeId Mass = new AttributeId(1111);
        public static readonly AttributeId ImpactArmor = new AttributeId(1112);
    }

    public sealed class BattleUnit
    {
        public const int TacticCount = 4;
        public const CombatStatus GapStatuses = CombatStatus.Unbalanced | CombatStatus.KnockedDown |
            CombatStatus.Frozen | CombatStatus.Paralyzed | CombatStatus.Rooted | CombatStatus.Slipped | CombatStatus.Exposed;
        public const CombatStatus HardStatuses = CombatStatus.KnockedDown | CombatStatus.Frozen | CombatStatus.Paralyzed | CombatStatus.Slipped;
        public int Id { get; internal set; } = -1;
        public UnitDefinition Definition { get; }
        public string Name => Definition.Name;
        public BattleSide Side { get; }
        public int Slot { get; internal set; }
        public AbilitySystemComponent Asc { get; }
        public float Health => Asc.Attributes.GetCurrent(CombatAttributes.Health);
        public float MaxHealth => Asc.Attributes.GetCurrent(CombatAttributes.MaxHealth);
        public float Armor => Asc.Attributes.GetCurrent(CombatAttributes.Armor);
        public float MaxArmor => Asc.Attributes.GetCurrent(CombatAttributes.MaxArmor);
        public float Attack => Asc.Attributes.GetCurrent(CombatAttributes.Attack);
        public float Speed => Asc.Attributes.GetCurrent(CombatAttributes.Speed);
        public int AP => (int)Asc.Attributes.GetCurrent(CombatAttributes.AP);
        public int PP => (int)Asc.Attributes.GetCurrent(CombatAttributes.PP);
        public int MaxAP => Definition.MaxAP;
        public int MaxPP => Definition.MaxPP;
        public CombatStatus Status => (CombatStatus)(int)Asc.Attributes.GetCurrent(CombatAttributes.Status);
        public bool IsAlive => Health > 0;
        public bool HasGap => Armor <= 0 || (Status & GapStatuses) != 0;
        public bool PassiveSealed => (Status & (CombatStatus.Unbalanced | HardStatuses)) != 0;
        public float EffectiveDodge => (Status & GapStatuses) != 0 ? 0 : Definition.Dodge;
        public float EffectiveBlock => (Status & GapStatuses) != 0 ? 0 : Definition.Block;
        public float EffectiveBuffer => HasGap ? 0 : Asc.Attributes.GetCurrent(CombatAttributes.Buffer);
        private readonly TacticRule[] _tactics = new TacticRule[TacticCount];
        private readonly GameplayAbilitySpec[] _abilities = new GameplayAbilitySpec[(int)BattleSkillId.Pass + 1];
        private readonly int[] _statusOpportunities = new int[10];
        internal bool LootCollected;

        public BattleUnit(UnitDefinition definition, BattleSide side, int slot)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            if (side != BattleSide.Allies && side != BattleSide.Enemies) throw new ArgumentOutOfRangeException(nameof(side));
            if (!Formation.IsValid(slot)) throw new ArgumentOutOfRangeException(nameof(slot));
            Side = side; Slot = slot;
            Asc = new AbilitySystemComponent(13);
            Asc.Attributes.Add(CombatAttributes.Health, definition.MaxHealth);
            Asc.Attributes.Add(CombatAttributes.MaxHealth, definition.MaxHealth);
            Asc.Attributes.Add(CombatAttributes.Armor, definition.MaxArmor);
            Asc.Attributes.Add(CombatAttributes.MaxArmor, definition.MaxArmor);
            Asc.Attributes.Add(CombatAttributes.Attack, definition.Attack);
            Asc.Attributes.Add(CombatAttributes.Speed, definition.Speed);
            Asc.Attributes.Add(CombatAttributes.AP, definition.MaxAP);
            Asc.Attributes.Add(CombatAttributes.PP, definition.MaxPP);
            Asc.Attributes.Add(CombatAttributes.Buffer, definition.Buffer);
            Asc.Attributes.Add(CombatAttributes.Status);
            Asc.Attributes.Add(CombatAttributes.Guarding);
            Asc.Attributes.Add(CombatAttributes.Mass, definition.Mass);
            Asc.Attributes.Add(CombatAttributes.ImpactArmor, definition.MaxArmor);
            for (int i = 0; i < TacticCount; i++) _tactics[i] = definition.GetTactic(i);
            for (int i = 0; i < definition.SkillCount; i++)
            {
                BattleSkillId id = definition.GetSkill(i);
                if (_abilities[(int)id] == null) _abilities[(int)id] = Asc.GrantAbility(BattleCatalog.GetSkill(id).Ability);
            }
            _abilities[(int)BattleSkillId.Pass] = Asc.GrantAbility(BattleCatalog.GetSkill(BattleSkillId.Pass).Ability);
        }

        public TacticRule GetTactic(int row)
        { if ((uint)row >= TacticCount) throw new ArgumentOutOfRangeException(nameof(row)); return _tactics[row]; }

        internal bool SetTactic(int row, TacticRule tactic)
        {
            if ((uint)row >= TacticCount || !Enum.IsDefined(typeof(TacticCondition), tactic.Condition) ||
                !Enum.IsDefined(typeof(TargetPriority), tactic.Priority)) return false;
            if (tactic.Enabled && (!Definition.HasSkill(tactic.Skill) || tactic.Skill == BattleSkillId.Guard || tactic.Skill == BattleSkillId.Assist)) return false;
            _tactics[row] = tactic;
            return true;
        }

        internal bool CanUse(BattleSkillId skill)
        {
            if ((uint)skill >= (uint)_abilities.Length) return false;
            GameplayAbilitySpec ability = _abilities[(int)skill];
            BattleSkillDefinition definition = BattleCatalog.GetSkill(skill);
            return ability != null && IsAlive && (definition.PpCost == 0 || !PassiveSealed) && Asc.CheckActivation(ability).Succeeded;
        }

        internal bool Spend(BattleSkillId skill)
        {
            if (!CanUse(skill)) return false;
            GameplayAbilitySpec ability = _abilities[(int)skill];
            bool succeeded = Asc.TryActivateAbility(ability).Succeeded;
            Asc.EndAbility(ability);
            return succeeded;
        }

        internal void ApplyStatus(CombatStatus status, int opportunities)
        {
            CombatStatus current = Status;
            for (int bit = 0; bit < _statusOpportunities.Length; bit++)
                if (((int)status & (1 << bit)) != 0) _statusOpportunities[bit] = Math.Max(_statusOpportunities[bit], opportunities);
            Asc.Attributes.Get(CombatAttributes.Status).BaseValue = (int)(current | status);
        }

        internal void RemoveStatus(CombatStatus status)
        {
            for (int bit = 0; bit < _statusOpportunities.Length; bit++)
                if (((int)status & (1 << bit)) != 0) _statusOpportunities[bit] = 0;
            Asc.Attributes.Get(CombatAttributes.Status).BaseValue = (int)(Status & ~status);
        }

        internal CombatStatus AdvanceStatusOpportunity()
        {
            CombatStatus expired = CombatStatus.None;
            for (int bit = 0; bit < _statusOpportunities.Length; bit++)
                if (_statusOpportunities[bit] > 0 && --_statusOpportunities[bit] == 0) expired |= (CombatStatus)(1 << bit);
            RemoveStatus(expired);
            return expired;
        }
    }
}
