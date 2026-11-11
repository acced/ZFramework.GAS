using System;

namespace IronOath.Combat
{
    public enum BattleSide { Allies, Enemies }
    public enum BattleState { Formation, Fighting, Victory, Defeat, Draw }
    public enum EnemyKind { Humanoid, Demon, Construct }
    public enum SurfaceKind { None, Water, Ice, Oil, Fire }
    public enum BattleLootKind { Core, IntactArmor, FamilyRelic }
    public enum BattleEventKind
    {
        BattleStarted, Action, Hit, Miss, StatusApplied, StatusExpired, ArmorBroken,
        Moved, Collision, Skipped, Defeated, BattleEnded, Healed, Guarded, LootGranted
    }
    public enum BattleSkillId
    {
        SwordStrike, ShieldBash, HammerCrush, Hook, PreciseThrust, Soak, Frost, Lightning,
        OilFlask, Firebomb, Net, Heal, Guard, Assist, Backstab, Collision, Conduction, Burn, Pass
    }
    [Flags]
    public enum CombatStatus
    {
        None = 0, Unbalanced = 1, KnockedDown = 2, Wet = 4, Frozen = 8, Paralyzed = 16,
        Oiled = 32, Rooted = 64, Slipped = 128, Burning = 256, Exposed = 512
    }
    public enum TacticCondition
    {
        Always, TargetArmored, TargetUnbalanced, TargetGap, TargetWet, TargetFrozen,
        TargetLowHealth, AllyWounded, TargetOiled, TargetRooted
    }
    public enum TargetPriority { Frontmost, LowestHealth, HighestArmor, Backmost, SameRow }

    public readonly struct TacticRule
    {
        public readonly BattleSkillId Skill;
        public readonly TacticCondition Condition;
        public readonly TargetPriority Priority;
        public readonly bool Enabled;
        public TacticRule(BattleSkillId skill, TacticCondition condition = TacticCondition.Always,
            TargetPriority priority = TargetPriority.Frontmost, bool enabled = true)
        { Skill = skill; Condition = condition; Priority = priority; Enabled = enabled; }
    }

    /// <summary>Slots are row * 3 + depth. Depth zero faces the opponent on BOTH boards.</summary>
    public static class Formation
    {
        public const int SlotCount = 9;
        public const int MaxUnitsPerSide = 6;
        public static bool IsValid(int slot) => (uint)slot < SlotCount;
        public static int Row(int slot) => slot / 3;
        public static int Depth(int slot) => slot % 3;
        public static bool AreAdjacent(int a, int b)
            => Math.Abs(Row(a) - Row(b)) + Math.Abs(Depth(a) - Depth(b)) == 1;
    }

    public readonly struct BattleEvent
    {
        public readonly BattleEventKind Kind;
        public readonly int SourceId, TargetId;
        public readonly BattleSkillId SkillId;
        public readonly float HealthDamage, ArmorDamage;
        public readonly CombatStatus Status;
        public readonly int FromSlot, ToSlot;
        public BattleEvent(BattleEventKind kind, int sourceId = -1, int targetId = -1,
            BattleSkillId skillId = BattleSkillId.SwordStrike, float healthDamage = 0,
            float armorDamage = 0, CombatStatus status = CombatStatus.None, int fromSlot = -1, int toSlot = -1)
        {
            Kind = kind; SourceId = sourceId; TargetId = targetId; SkillId = skillId;
            HealthDamage = healthDamage; ArmorDamage = armorDamage; Status = status;
            FromSlot = fromSlot; ToSlot = toSlot;
        }
    }

    public readonly struct BattleLoot
    {
        public readonly BattleLootKind Kind;
        public readonly string Name;
        public readonly int Width, Height, Value, SourceUnitId;
        public BattleLoot(BattleLootKind kind, string name, int width, int height, int value, int sourceUnitId)
        { Kind = kind; Name = name; Width = width; Height = height; Value = value; SourceUnitId = sourceUnitId; }
    }

    public sealed class UnitDefinition
    {
        public string Id { get; }
        public string Name { get; }
        public string Role { get; }
        public string WeaponName { get; }
        public string ArmorLootName { get; }
        public float MaxHealth { get; }
        public float MaxArmor { get; }
        public float Attack { get; }
        public float Speed { get; }
        public int MaxAP { get; }
        public int MaxPP { get; }
        public float Buffer { get; }
        public float Dodge { get; }
        public float Block { get; }
        public float Mass { get; }
        public EnemyKind EnemyKind { get; }
        public bool CarriesFamilyRelic { get; }
        private readonly BattleSkillId[] _skills;
        private readonly TacticRule[] _tactics;
        public int SkillCount => _skills.Length;
        public BattleSkillId GetSkill(int index) => _skills[index];
        public TacticRule GetTactic(int row) => _tactics[row];

        public UnitDefinition(string id, string name, string role, string weaponName,
            float health, float armor, float attack, float speed, int ap, int pp,
            float buffer, float dodge, float block, float mass, EnemyKind enemyKind,
            bool carriesFamilyRelic, BattleSkillId[] skills, TacticRule[] tactics)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Unit id and name are required.");
            RequireFinite(health, nameof(health), 1, 1000000); RequireFinite(armor, nameof(armor), 0, 1000000);
            RequireFinite(attack, nameof(attack), 0, 100000); RequireFinite(speed, nameof(speed), 0, 100000);
            RequireFinite(buffer, nameof(buffer), 0, 1); RequireFinite(dodge, nameof(dodge), 0, 1);
            RequireFinite(block, nameof(block), 0, 1); RequireFinite(mass, nameof(mass), 0, 100);
            if (ap < 1 || ap > 99 || pp < 0 || pp > 99) throw new ArgumentOutOfRangeException(nameof(ap));
            if (!Enum.IsDefined(typeof(EnemyKind), enemyKind)) throw new ArgumentOutOfRangeException(nameof(enemyKind));
            if (skills == null || skills.Length == 0) throw new ArgumentException("At least one skill is required.");
            Id = id; Name = name; Role = role ?? ""; WeaponName = weaponName ?? "";
            ArmorLootName = name + "的完好护甲";
            MaxHealth = health; MaxArmor = armor; Attack = attack; Speed = speed; MaxAP = ap; MaxPP = pp;
            Buffer = buffer; Dodge = dodge; Block = block; Mass = mass; EnemyKind = enemyKind;
            CarriesFamilyRelic = carriesFamilyRelic;
            _skills = (BattleSkillId[])skills.Clone();
            _tactics = new TacticRule[BattleUnit.TacticCount];
            for (int i = 0; i < _skills.Length; i++)
                if ((uint)_skills[i] >= (uint)BattleSkillId.Collision) throw new ArgumentException("Internal reactions are not authored active skills.");
            for (int i = 0; i < _tactics.Length; i++)
                _tactics[i] = tactics != null && i < tactics.Length ? tactics[i] : new TacticRule(_skills[0], enabled: false);
            for (int i = 0; i < _tactics.Length; i++)
            {
                TacticRule tactic = _tactics[i];
                if (!Enum.IsDefined(typeof(TacticCondition), tactic.Condition) || !Enum.IsDefined(typeof(TargetPriority), tactic.Priority))
                    throw new ArgumentException("Tactic condition and priority must be defined values.");
                if (tactic.Enabled && (!HasSkill(tactic.Skill) || tactic.Skill == BattleSkillId.Guard || tactic.Skill == BattleSkillId.Assist))
                    throw new ArgumentException("An enabled tactic must use an available active skill.");
            }
        }

        public bool HasSkill(BattleSkillId skill)
        { for (int i = 0; i < _skills.Length; i++) if (_skills[i] == skill) return true; return false; }

        private static void RequireFinite(float value, string name, float min, float max)
        { if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max) throw new ArgumentOutOfRangeException(name); }
    }
}
