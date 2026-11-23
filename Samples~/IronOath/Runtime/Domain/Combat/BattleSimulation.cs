using System;
using ZFramework.GAS;

namespace IronOath.Combat
{
    /// <summary>
    /// Deterministic bounded host for GAS. No Unity clock, animations, strings, LINQ, or allocations in Step.
    /// AP is an encounter budget. Soft setups last two owner action opportunities; hard control skips one.
    /// </summary>
    public sealed class BattleSimulation
    {
        public const int MaximumActions = 256;
        private readonly BattleUnit[] _units = new BattleUnit[12];
        private readonly BattleUnit[] _board = new BattleUnit[18];
        private readonly int[] _initiative = new int[12];
        private readonly BattleEvent[] _events = new BattleEvent[128];
        private readonly BattleLoot[] _loot = new BattleLoot[24];
        private readonly SurfaceKind[] _surfaces = new SurfaceKind[18];
        private readonly int[] _surfaceTurns = new int[18];
        private uint _random;
        private int _cursor;
        private bool _stepping;
        public int UnitCount { get; private set; }
        public int EventCount { get; private set; }
        public int LootCount { get; private set; }
        public int ActionNumber { get; private set; }
        public BattleState State { get; private set; } = BattleState.Formation;
        private static readonly GameplayEffectDefinition CommandArmor = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[]
            {
                new EffectModifier(CombatAttributes.MaxArmor, ModifierOperation.Add, EffectMagnitude.Fixed(18)),
                new EffectModifier(CombatAttributes.Armor, ModifierOperation.Add, EffectMagnitude.Fixed(18))
            });

        public BattleSimulation(uint seed = 1) { _random = seed == 0 ? 1 : seed; }
        public BattleUnit GetUnit(int index)
        { if ((uint)index >= (uint)UnitCount) throw new ArgumentOutOfRangeException(nameof(index)); return _units[index]; }
        public BattleEvent GetEvent(int index)
        { if ((uint)index >= (uint)EventCount) throw new ArgumentOutOfRangeException(nameof(index)); return _events[index]; }
        public BattleLoot GetLoot(int index)
        { if ((uint)index >= (uint)LootCount) throw new ArgumentOutOfRangeException(nameof(index)); return _loot[index]; }
        public BattleUnit GetOccupant(BattleSide side, int slot)
            => ValidSide(side) && Formation.IsValid(slot) ? _board[(int)side * 9 + slot] : null;
        public SurfaceKind GetSurface(BattleSide side, int slot)
            => ValidSide(side) && Formation.IsValid(slot) ? _surfaces[(int)side * 9 + slot] : SurfaceKind.None;

        public bool AddUnit(BattleUnit unit)
        {
            if (_stepping || State != BattleState.Formation || unit == null || unit.Id >= 0 || UnitCount == _units.Length ||
                GetOccupant(unit.Side, unit.Slot) != null) return false;
            int sideCount = 0;
            for (int i = 0; i < UnitCount; i++) if (_units[i].Side == unit.Side) sideCount++;
            if (sideCount == Formation.MaxUnitsPerSide) return false;
            unit.Id = UnitCount;
            _units[UnitCount++] = unit;
            _board[(int)unit.Side * 9 + unit.Slot] = unit;
            return true;
        }

        public bool MoveUnit(int unitId, int slot)
        {
            if (_stepping || State != BattleState.Formation || (uint)unitId >= (uint)UnitCount || !Formation.IsValid(slot)) return false;
            BattleUnit unit = _units[unitId];
            BattleUnit other = GetOccupant(unit.Side, slot);
            int previous = unit.Slot;
            _board[(int)unit.Side * 9 + previous] = other;
            _board[(int)unit.Side * 9 + slot] = unit;
            unit.Slot = slot;
            if (other != null) other.Slot = previous;
            return true;
        }

        public bool SetTactic(int unitId, int row, TacticRule rule)
            => !_stepping && State == BattleState.Formation && (uint)unitId < (uint)UnitCount && _units[unitId].SetTactic(row, rule);

        public bool SetSurface(BattleSide side, int slot, SurfaceKind surface)
        {
            if (_stepping || State != BattleState.Formation || !ValidSide(side) || !Formation.IsValid(slot) ||
                !Enum.IsDefined(typeof(SurfaceKind), surface)) return false;
            WriteSurface(side, slot, surface);
            return true;
        }

        public bool Start()
        {
            if (_stepping || State != BattleState.Formation) return false;
            int allies = 0, enemies = 0;
            for (int i = 0; i < UnitCount; i++) if (_units[i].Side == BattleSide.Allies) allies++; else enemies++;
            if (allies == 0 || enemies == 0) return false;
            _stepping = true;
            try
            {
                State = BattleState.Fighting;
                EventCount = 0;
                Emit(new BattleEvent(BattleEventKind.BattleStarted));
                for (int i = 0; i < UnitCount; i++)
                {
                    int insert = i;
                    while (insert > 0 && _units[_initiative[insert - 1]].Speed < _units[i].Speed)
                    { _initiative[insert] = _initiative[insert - 1]; insert--; }
                    _initiative[insert] = i;
                }
                for (int i = 0; i < UnitCount; i++)
                {
                    BattleUnit unit = _units[i];
                    if (unit.Definition.Id == "rayn")
                        for (int j = 0; j < UnitCount; j++)
                        {
                            BattleUnit ally = _units[j];
                            if (ally.Side == unit.Side && Formation.AreAdjacent(unit.Slot, ally.Slot))
                                ally.Asc.Effects.Apply(new GameplayEffectSpec(CommandArmor, unit.Asc.Attributes, unit.Asc));
                        }
                    if (unit.Definition.Id == "chloe")
                    {
                        BattleUnit target = FindTarget(unit, new TacticRule(BattleSkillId.PreciseThrust, priority: TargetPriority.HighestArmor));
                        if (target != null) AddStatus(unit, target, CombatStatus.Exposed);
                    }
                    EnterSurface(unit);
                }
                return true;
            }
            finally { _stepping = false; }
        }

        /// <summary>Emits a fresh per-step event batch, including bounded guards, assists, and collision damage.</summary>
        public bool Step()
        {
            if (_stepping) throw new InvalidOperationException("A battle step cannot run reentrantly.");
            if (State != BattleState.Fighting) return false;
            _stepping = true;
            try
            {
                EventCount = 0;
                if (CheckFinished()) return true;
                if (ActionNumber >= MaximumActions) { End(BattleState.Draw); return true; }
                BattleUnit actor = NextActor();
                if (actor == null) { End(BattleState.Draw); return true; }
                ActionNumber++;
                if ((actor.Status & CombatStatus.Burning) != 0) DealDamage(actor, actor, BattleSkillId.Burn, false);
                if (!actor.IsAlive) { CheckFinished(); return true; }

                if ((actor.Status & BattleUnit.HardStatuses) != 0)
                {
                    actor.Spend(BattleSkillId.Pass);
                    Emit(new BattleEvent(BattleEventKind.Skipped, actor.Id, actor.Id, BattleSkillId.Pass, status: actor.Status & BattleUnit.HardStatuses));
                }
                else
                {
                    bool acted = false;
                    for (int row = 0; row < BattleUnit.TacticCount; row++)
                    {
                        TacticRule rule = actor.GetTactic(row);
                        if (!rule.Enabled || !actor.CanUse(rule.Skill)) continue;
                        BattleUnit target = FindTarget(actor, rule);
                        if (target == null || !actor.Spend(rule.Skill)) continue;
                        Emit(new BattleEvent(BattleEventKind.Action, actor.Id, target.Id, rule.Skill));
                        ResolveSkill(actor, target, rule.Skill);
                        acted = true;
                        break;
                    }
                    if (!acted)
                    {
                        actor.Spend(BattleSkillId.Pass);
                        Emit(new BattleEvent(BattleEventKind.Skipped, actor.Id, actor.Id, BattleSkillId.Pass));
                    }
                }
                CombatStatus expired = actor.AdvanceStatusOpportunity();
                if (expired != CombatStatus.None) Emit(new BattleEvent(BattleEventKind.StatusExpired, actor.Id, actor.Id, status: expired));
                int square = (int)actor.Side * 9 + actor.Slot;
                if (_surfaceTurns[square] > 0 && --_surfaceTurns[square] == 0) _surfaces[square] = SurfaceKind.None;
                CheckFinished();
                return true;
            }
            finally { _stepping = false; }
        }

        private BattleUnit NextActor()
        {
            for (int i = 0; i < UnitCount; i++)
            {
                BattleUnit unit = _units[_initiative[_cursor]];
                _cursor = (_cursor + 1) % UnitCount;
                if (unit.IsAlive && unit.AP > 0) return unit;
            }
            return null;
        }

        private BattleUnit FindTarget(BattleUnit actor, TacticRule rule)
        {
            bool friendly = rule.Skill == BattleSkillId.Heal;
            BattleUnit best = null;
            for (int i = 0; i < UnitCount; i++)
            {
                BattleUnit candidate = _units[i];
                if (!candidate.IsAlive || (candidate.Side == actor.Side) != friendly || !Matches(candidate, rule.Condition)) continue;
                if (friendly && candidate.Health >= candidate.MaxHealth) continue;
                if (best == null || CompareTarget(actor, candidate, best, rule.Priority) < 0) best = candidate;
            }
            return best;
        }

        private static bool Matches(BattleUnit target, TacticCondition condition)
        {
            switch (condition)
            {
                case TacticCondition.Always: return true;
                case TacticCondition.TargetArmored: return target.Armor > 0;
                case TacticCondition.TargetUnbalanced: return (target.Status & CombatStatus.Unbalanced) != 0;
                case TacticCondition.TargetGap: return target.HasGap;
                case TacticCondition.TargetWet: return (target.Status & CombatStatus.Wet) != 0;
                case TacticCondition.TargetFrozen: return (target.Status & CombatStatus.Frozen) != 0;
                case TacticCondition.TargetLowHealth: return target.Health <= target.MaxHealth * .35f;
                case TacticCondition.AllyWounded: return target.Health <= target.MaxHealth * .75f;
                case TacticCondition.TargetOiled: return (target.Status & CombatStatus.Oiled) != 0;
                case TacticCondition.TargetRooted: return (target.Status & CombatStatus.Rooted) != 0;
                default: return false;
            }
        }

        private static int CompareTarget(BattleUnit actor, BattleUnit a, BattleUnit b, TargetPriority priority)
        {
            int result;
            switch (priority)
            {
                case TargetPriority.LowestHealth: result = (a.Health / a.MaxHealth).CompareTo(b.Health / b.MaxHealth); break;
                case TargetPriority.HighestArmor: result = b.Armor.CompareTo(a.Armor); break;
                case TargetPriority.Backmost: result = Formation.Depth(b.Slot).CompareTo(Formation.Depth(a.Slot)); break;
                case TargetPriority.SameRow: result = Math.Abs(Formation.Row(a.Slot) - Formation.Row(actor.Slot)).CompareTo(Math.Abs(Formation.Row(b.Slot) - Formation.Row(actor.Slot))); break;
                default: result = Formation.Depth(a.Slot).CompareTo(Formation.Depth(b.Slot)); break;
            }
            if (result != 0) return result;
            result = Formation.Depth(a.Slot).CompareTo(Formation.Depth(b.Slot));
            return result != 0 ? result : a.Slot.CompareTo(b.Slot);
        }

        private void ResolveSkill(BattleUnit source, BattleUnit target, BattleSkillId skill)
        {
            if (skill == BattleSkillId.Heal)
            {
                float before = target.Health;
                target.Asc.Effects.Apply(new GameplayEffectSpec(BattleCatalog.GetSkill(skill).Effect, source.Asc.Attributes, source.Asc));
                Emit(new BattleEvent(BattleEventKind.Healed, source.Id, target.Id, skill, healthDamage: before - target.Health));
                return;
            }
            bool wet = (target.Status & CombatStatus.Wet) != 0;
            bool burningCombo = (target.Status & (CombatStatus.Oiled | CombatStatus.Rooted)) != 0;
            bool impact = skill == BattleSkillId.ShieldBash || skill == BattleSkillId.HammerCrush || skill == BattleSkillId.Hook;
            // Control attacks cannot be dodged or blocked by armor. Their control opens the gap immediately.
            if (impact)
            {
                if (target.Armor <= 0 || (target.Status & CombatStatus.Unbalanced) != 0)
                { target.RemoveStatus(CombatStatus.Unbalanced); AddStatus(source, target, CombatStatus.KnockedDown); }
                else AddStatus(source, target, CombatStatus.Unbalanced);
            }
            if (skill == BattleSkillId.Net) AddStatus(source, target, CombatStatus.Rooted);
            if (skill == BattleSkillId.Soak) { AddStatus(source, target, CombatStatus.Wet); WriteSurface(target.Side, target.Slot, SurfaceKind.Water); }
            if (skill == BattleSkillId.OilFlask) { AddStatus(source, target, CombatStatus.Oiled); WriteSurface(target.Side, target.Slot, SurfaceKind.Oil); }
            if (skill == BattleSkillId.Frost)
            {
                WriteSurface(target.Side, target.Slot, SurfaceKind.Ice);
                if (wet) { target.RemoveStatus(CombatStatus.Wet); AddStatus(source, target, CombatStatus.Frozen); }
            }
            if (skill == BattleSkillId.Lightning && wet)
            { target.RemoveStatus(CombatStatus.Wet); AddStatus(source, target, CombatStatus.Paralyzed); }
            target.Asc.Attributes.Get(CombatAttributes.ImpactArmor).BaseValue = target.Armor;
            bool hit = DealDamage(source, target, skill, !impact && skill != BattleSkillId.Net);
            if (!hit) return;
            if (skill == BattleSkillId.Firebomb)
            {
                if (burningCombo) target.RemoveStatus(CombatStatus.Oiled | CombatStatus.Rooted);
                WriteSurface(target.Side, target.Slot, SurfaceKind.Fire);
                if (target.IsAlive) AddStatus(source, target, CombatStatus.Burning);
            }
            if (skill == BattleSkillId.Lightning && wet)
                for (int i = 0; i < UnitCount; i++)
                {
                    BattleUnit neighbor = _units[i];
                    if (neighbor.IsAlive && neighbor.Side == target.Side && Formation.AreAdjacent(neighbor.Slot, target.Slot))
                        DealDamage(target, neighbor, BattleSkillId.Conduction, false);
                }
            if (target.IsAlive && skill == BattleSkillId.ShieldBash) Displace(source, target, 1);
            if (target.IsAlive && skill == BattleSkillId.Hook) Displace(source, target, -1);
            if (target.IsAlive) TryAssist(source, target);
        }

        private bool DealDamage(BattleUnit source, BattleUnit target, BattleSkillId id, bool allowDefense)
        {
            if (!target.IsAlive) return false;
            BattleSkillDefinition skill = BattleCatalog.GetSkill(id);
            if (allowDefense && skill.Physical && !skill.Backstab && target.EffectiveDodge > 0 && NextFloat() < target.EffectiveDodge)
            { Emit(new BattleEvent(BattleEventKind.Miss, source.Id, target.Id, id)); return false; }
            bool guard = allowDefense && skill.Physical && !skill.Backstab && target.EffectiveBlock > 0 &&
                target.CanUse(BattleSkillId.Guard) && NextFloat() < target.EffectiveBlock && target.Spend(BattleSkillId.Guard);
            if (guard)
            {
                target.Asc.Attributes.Get(CombatAttributes.Guarding).BaseValue = 1;
                Emit(new BattleEvent(BattleEventKind.Guarded, target.Id, source.Id, BattleSkillId.Guard));
            }
            float armor = target.Armor, health = target.Health;
            try { target.Asc.Effects.Apply(new GameplayEffectSpec(skill.Effect, source.Asc.Attributes, source.Asc)); }
            finally { if (guard) target.Asc.Attributes.Get(CombatAttributes.Guarding).BaseValue = 0; }
            Emit(new BattleEvent(BattleEventKind.Hit, source.Id, target.Id, id, health - target.Health, armor - target.Armor));
            if (armor > 0 && target.Armor <= 0) Emit(new BattleEvent(BattleEventKind.ArmorBroken, source.Id, target.Id, id));
            if (skill.Blunt && (target.Status & CombatStatus.Frozen) != 0)
            {
                target.RemoveStatus(CombatStatus.Frozen);
                Emit(new BattleEvent(BattleEventKind.StatusExpired, source.Id, target.Id, id, status: CombatStatus.Frozen));
            }
            if (!target.IsAlive)
            {
                _board[(int)target.Side * 9 + target.Slot] = null;
                Emit(new BattleEvent(BattleEventKind.Defeated, source.Id, target.Id, id));
                CollectLoot(target);
            }
            return true;
        }

        private void TryAssist(BattleUnit actor, BattleUnit target)
        {
            for (int i = 0; i < UnitCount; i++)
            {
                BattleUnit helper = _units[i];
                if (helper == actor || helper.Side != actor.Side || !helper.IsAlive || !Formation.AreAdjacent(helper.Slot, actor.Slot) ||
                    !helper.Spend(BattleSkillId.Assist)) continue;
                Emit(new BattleEvent(BattleEventKind.Action, helper.Id, target.Id, BattleSkillId.Assist));
                DealDamage(helper, target, BattleSkillId.Assist, false);
                break; // One bounded assist, never recursive.
            }
        }

        private void Displace(BattleUnit source, BattleUnit target, int depthDelta)
        {
            int depth = Formation.Depth(target.Slot) + depthDelta;
            BattleUnit blocker = depth >= 0 && depth < 3 ? GetOccupant(target.Side, Formation.Row(target.Slot) * 3 + depth) : target;
            if (blocker != null)
            {
                target.Asc.Attributes.Get(CombatAttributes.ImpactArmor).BaseValue = target.Armor;
                Emit(new BattleEvent(BattleEventKind.Collision, target.Id, blocker.Id, BattleSkillId.Collision));
                if (blocker != target) DealDamage(target, blocker, BattleSkillId.Collision, false);
                DealDamage(target, target, BattleSkillId.Collision, false);
                return;
            }
            int old = target.Slot;
            _board[(int)target.Side * 9 + old] = null;
            target.Slot = Formation.Row(old) * 3 + depth;
            _board[(int)target.Side * 9 + target.Slot] = target;
            Emit(new BattleEvent(BattleEventKind.Moved, source.Id, target.Id, fromSlot: old, toSlot: target.Slot));
            EnterSurface(target);
        }

        private void EnterSurface(BattleUnit unit)
        {
            switch (GetSurface(unit.Side, unit.Slot))
            {
                case SurfaceKind.Water: AddStatus(unit, unit, CombatStatus.Wet); break;
                case SurfaceKind.Oil: AddStatus(unit, unit, CombatStatus.Oiled); break;
                case SurfaceKind.Ice: AddStatus(unit, unit, CombatStatus.Slipped); break;
                case SurfaceKind.Fire: AddStatus(unit, unit, CombatStatus.Burning); break;
            }
        }

        private void WriteSurface(BattleSide side, int slot, SurfaceKind surface)
        {
            int index = (int)side * 9 + slot;
            _surfaces[index] = surface;
            _surfaceTurns[index] = surface == SurfaceKind.None ? 0 : 3;
        }

        private void AddStatus(BattleUnit source, BattleUnit target, CombatStatus status)
        {
            if (!target.IsAlive) return;
            target.ApplyStatus(status, (status & BattleUnit.HardStatuses) != 0 ? 1 : 2);
            Emit(new BattleEvent(BattleEventKind.StatusApplied, source.Id, target.Id, status: status));
        }

        private void CollectLoot(BattleUnit unit)
        {
            if (unit.LootCollected || unit.Side != BattleSide.Enemies) return;
            unit.LootCollected = true;
            if (unit.Definition.EnemyKind != EnemyKind.Humanoid)
            {
                if (unit.MaxArmor > 0 && unit.Armor <= 0)
                    AddLoot(new BattleLoot(BattleLootKind.Core, unit.Definition.EnemyKind == EnemyKind.Construct ? "构装魔核" : "深渊宝石", 1, 1, 95 + (int)unit.MaxArmor, unit.Id));
                return;
            }
            if (unit.MaxArmor <= 0 || unit.Armor * 5 < unit.MaxArmor * 4) return;
            AddLoot(new BattleLoot(BattleLootKind.IntactArmor, unit.Definition.ArmorLootName, 2, 3, 100 + (int)(unit.MaxArmor * 2), unit.Id));
            if (unit.Definition.CarriesFamilyRelic)
                AddLoot(new BattleLoot(BattleLootKind.FamilyRelic, unit.Definition.WeaponName, 1, 1, 0, unit.Id));
        }

        private void AddLoot(BattleLoot item)
        {
            if (LootCount == _loot.Length) throw new InvalidOperationException("Battle loot capacity exceeded.");
            _loot[LootCount++] = item;
            Emit(new BattleEvent(BattleEventKind.LootGranted, targetId: item.SourceUnitId));
        }

        private bool CheckFinished()
        {
            bool allies = false, enemies = false;
            for (int i = 0; i < UnitCount; i++)
                if (_units[i].IsAlive) { if (_units[i].Side == BattleSide.Allies) allies = true; else enemies = true; }
            if (allies && enemies) return false;
            End(!allies && !enemies ? BattleState.Draw : allies ? BattleState.Victory : BattleState.Defeat);
            return true;
        }

        private void End(BattleState state) { State = state; Emit(new BattleEvent(BattleEventKind.BattleEnded)); }
        private void Emit(BattleEvent item)
        {
            if (EventCount == _events.Length) throw new InvalidOperationException("Battle event capacity exceeded.");
            _events[EventCount++] = item;
        }
        private float NextFloat()
        { uint n = _random; n ^= n << 13; n ^= n >> 17; n ^= n << 5; _random = n; return (n & 0xffffff) / 16777216f; }
        private static bool ValidSide(BattleSide side) => side == BattleSide.Allies || side == BattleSide.Enemies;
    }
}
