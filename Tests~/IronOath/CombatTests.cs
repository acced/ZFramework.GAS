using System;
using IronOath.Combat;
using ZFramework.GAS;

namespace IronOath.Tests
{
    internal static class CombatTests
    {
        public static void Run(Action<string, Action> test)
        {
            test("combat / formation limits, mirrored depth, swaps and phase gates", FormationGuards);
            test("combat / invalid definitions reject non-finite and out-of-range stats", InvalidDefinitions);
            test("combat / simultaneous dual damage snapshots pre-break buffer", DualDamage);
            test("combat / precision gap and backstab preserve armor", PrecisionGap);
            test("combat / frozen blunt armor damage is exactly 250 percent", FrozenBlunt);
            test("combat / full armor does not prevent two-stage physical control", PhysicalControl);
            test("combat / armorless impact immediately knocks down", ArmorlessControl);
            test("combat / wet plus ice or lightning consumes wet and skips one action", ElementalControl);
            test("combat / oil and net ignite into a double-power burst", FireCombos);
            test("combat / conductivity uses pre-impact armor and affects adjacent allies", Conductivity);
            test("combat / displacement collision and ice surface resolve on board", CollisionAndIce);
            test("combat / tactic priority and GAS AP payment choose valid targets", TacticsAndCost);
            test("combat / guard and assist spend real PP and respect passive sealing", PassiveCosts);
            test("combat / loot preserves exact eighty-percent boundary and relics", HumanoidLoot);
            test("combat / core requires armor destruction before death", CoreLoot);
            test("combat / deterministic demo encounters terminate for both protagonists", ReplayAndTermination);
            test("combat / no-action and action-cap draws cannot hang", DrawBoundaries);
            test("combat / reentrant step is rejected without duplicate spending", StepReentry);
            test("combat / warmed simulation step has zero managed allocation", HotPathAllocation);
        }

        private static void FormationGuards()
        {
            var battle = new BattleSimulation();
            for (int i = 0; i < 6; i++) Check(battle.AddUnit(Unit("a" + i, BattleSide.Allies, i)), "six allies fit");
            Check(!battle.AddUnit(Unit("seventh", BattleSide.Allies, 6)), "seventh ally rejected");
            Check(!battle.Start(), "a missing side cannot start");
            Check(battle.AddUnit(Unit("enemy", BattleSide.Enemies, 0)), "opposite front slot is independent");
            Check(!battle.AddUnit(Unit("overlap", BattleSide.Enemies, 0)), "occupied cell rejected");
            Check(Formation.Depth(battle.GetUnit(0).Slot) == 0 && Formation.Depth(battle.GetUnit(6).Slot) == 0, "front depth zero on both boards");
            Check(battle.MoveUnit(0, 8) && battle.GetOccupant(BattleSide.Allies, 0) == null, "move to vacant cell");
            Check(battle.MoveUnit(1, 2) && battle.GetUnit(1).Slot == 2 && battle.GetUnit(2).Slot == 1, "occupied destination swaps");
            Check(!battle.MoveUnit(-1, 0) && !battle.MoveUnit(0, 9), "invalid indices rejected");
            Check(battle.Start() && !battle.MoveUnit(0, 0) && !battle.Start(), "phase transitions are one-way");
        }

        private static void InvalidDefinitions()
        {
            Throws<ArgumentOutOfRangeException>(() => Definition("nan", health: float.NaN));
            Throws<ArgumentOutOfRangeException>(() => Definition("inf", armor: float.PositiveInfinity));
            Throws<ArgumentOutOfRangeException>(() => Definition("negative", attack: -1));
            Throws<ArgumentOutOfRangeException>(() => Definition("buffer", buffer: 1.01f));
            Throws<ArgumentOutOfRangeException>(() => new BattleUnit(Definition("slot"), BattleSide.Allies, 9));
            Throws<ArgumentOutOfRangeException>(() => new BattleUnit(Definition("side"), (BattleSide)3, 0));
        }

        private static void DualDamage()
        {
            BattleUnit source = Unit("source", BattleSide.Allies, 0, attack: 40);
            BattleUnit target = Unit("target", BattleSide.Enemies, 0, health: 100, armor: 30, buffer: .5f);
            Apply(source, target, BattleSkillId.SwordStrike);
            Near(target.Armor, 0); Near(target.Health, 87); Check(target.PP == 1, "break loses one PP");
            Apply(source, target, BattleSkillId.SwordStrike);
            Near(target.Health, 47); Check(target.PP == 1, "no repeated PP loss on empty armor");
            Apply(source, target, BattleSkillId.SwordStrike);
            Apply(source, target, BattleSkillId.SwordStrike);
            Near(target.Health, 0); Near(target.Armor, 0);
        }

        private static void PrecisionGap()
        {
            BattleUnit source = Unit("source", BattleSide.Allies, 0, attack: 40);
            BattleUnit target = Unit("target", BattleSide.Enemies, 0, armor: 100, buffer: .5f);
            target.ApplyStatus(CombatStatus.Unbalanced, 2);
            Apply(source, target, BattleSkillId.PreciseThrust);
            Near(target.Armor, 100); Near(target.Health, 58);
            target.RemoveStatus(CombatStatus.Unbalanced);
            Set(target, CombatAttributes.Health, 100);
            Apply(source, target, BattleSkillId.Backstab);
            Near(target.Armor, 100); Near(target.Health, 64);
            Set(target, CombatAttributes.Health, 100);
            Apply(source, target, BattleSkillId.PreciseThrust);
            Near(target.Armor, 94.96f); Near(target.Health, 79);
        }

        private static void FrozenBlunt()
        {
            BattleUnit source = Unit("source", BattleSide.Allies, 0, attack: 40);
            BattleUnit target = Unit("target", BattleSide.Enemies, 0, armor: 200, buffer: .6f);
            target.ApplyStatus(CombatStatus.Frozen, 1);
            Apply(source, target, BattleSkillId.HammerCrush);
            Near(target.Armor, 35); Near(target.Health, 91.2f);
            Check(target.PP == 2, "not broken yet");
            Apply(source, target, BattleSkillId.PreciseThrust);
            Near(target.Armor, 35); Check((target.Status & CombatStatus.Frozen) != 0, "precision preserves ice");
        }

        private static void PhysicalControl()
        {
            var battle = Pair(BattleSkillId.ShieldBash, targetArmor: 500, targetDodge: 1, targetBlock: 1, targetGuard: true);
            BattleUnit source = battle.GetUnit(0), target = battle.GetUnit(1);
            battle.Start(); battle.Step();
            Check((target.Status & CombatStatus.Unbalanced) != 0, "full armor guaranteed control");
            Near(target.EffectiveDodge, 0); Near(target.EffectiveBlock, 0); Near(target.EffectiveBuffer, 0);
            Check(target.PassiveSealed && target.PP == 2, "seal does not consume PP");
            Check(target.Slot == 1, "push moves to empty middle column");
            battle.Step(); Check((target.Status & CombatStatus.Unbalanced) != 0, "soft control survives first opportunity");
            battle.Step(); Check((target.Status & CombatStatus.KnockedDown) != 0, "second impact synthesizes knockdown");
            int ap = target.AP;
            battle.Step(); Check(target.AP == ap - 1 && HasEvent(battle, BattleEventKind.Skipped), "hard control consumes one scheduled opportunity");
            Check((target.Status & BattleUnit.HardStatuses) == 0, "hard control expires after skip");
            Check(source.AP == source.MaxAP - 2, "GAS owns AP cost");
        }

        private static void ArmorlessControl()
        {
            var battle = Pair(BattleSkillId.ShieldBash, targetArmor: 0, targetDodge: 1, targetBlock: 1, targetGuard: true);
            Near(battle.GetUnit(1).EffectiveDodge, 1); // Empty armor alone does not erase natural evasion.
            Near(battle.GetUnit(1).EffectiveBlock, 1);
            battle.Start(); battle.Step();
            Check((battle.GetUnit(1).Status & CombatStatus.KnockedDown) != 0, "no armor skips first setup stage");
        }

        private static void ElementalControl()
        {
            foreach (BattleSkillId finisher in new[] { BattleSkillId.Frost, BattleSkillId.Lightning })
            {
                var battle = new BattleSimulation();
                battle.AddUnit(Unit("water", BattleSide.Allies, 0, skill: BattleSkillId.Soak, speed: 30));
                battle.AddUnit(Unit("element", BattleSide.Allies, 1, skill: finisher, speed: 20));
                battle.AddUnit(Unit("enemy", BattleSide.Enemies, 0, health: 1000, armor: 500, speed: 10));
                battle.Start(); battle.Step(); battle.Step();
                BattleUnit target = battle.GetUnit(2);
                CombatStatus expected = finisher == BattleSkillId.Frost ? CombatStatus.Frozen : CombatStatus.Paralyzed;
                Check((target.Status & expected) != 0 && (target.Status & CombatStatus.Wet) == 0, "wet consumed by combination");
                int ap = target.AP;
                battle.Step(); Check(target.AP == ap - 1 && (target.Status & expected) == 0, "one skipped action");
            }
        }

        private static void FireCombos()
        {
            foreach (CombatStatus setup in new[] { CombatStatus.Oiled, CombatStatus.Rooted })
            {
                BattleUnit source = Unit("source", BattleSide.Allies, 0, attack: 20);
                BattleUnit target = Unit("target", BattleSide.Enemies, 0, armor: 200);
                target.ApplyStatus(setup, 2);
                Apply(source, target, BattleSkillId.Firebomb);
                Near(target.Health, 73); Near(target.Armor, 176.6f);
                var battle = Pair(BattleSkillId.Firebomb, targetArmor: 500);
                battle.GetUnit(1).ApplyStatus(setup, 2);
                battle.Start(); battle.Step();
                Check((battle.GetUnit(1).Status & setup) == 0 && (battle.GetUnit(1).Status & CombatStatus.Burning) != 0, "setup consumed, burn applied");
            }
        }

        private static void Conductivity()
        {
            var battle = new BattleSimulation();
            battle.AddUnit(Unit("lightning", BattleSide.Allies, 0, skill: BattleSkillId.Lightning, attack: 20, speed: 100));
            battle.AddUnit(Unit("conductor", BattleSide.Enemies, 3, health: 1000, armor: 200, speed: 1));
            battle.AddUnit(Unit("neighbor", BattleSide.Enemies, 0, health: 1000, armor: 20, speed: 1));
            battle.AddUnit(Unit("distant", BattleSide.Enemies, 8, health: 1000, armor: 20, speed: 1));
            battle.SetTactic(0, 0, new TacticRule(BattleSkillId.Lightning, priority: TargetPriority.HighestArmor));
            battle.GetUnit(1).ApplyStatus(CombatStatus.Wet, 2);
            battle.Start(); battle.Step();
            Near(battle.GetUnit(2).Health, 968); // (4 + 200 * .18) * .8, before primary armor damage
            Near(battle.GetUnit(3).Health, 1000);
            BattleUnit conductor = Unit("heavy", BattleSide.Allies, 0, armor: 400);
            BattleUnit target = Unit("target", BattleSide.Enemies, 0, health: 1000);
            Apply(conductor, target, BattleSkillId.Conduction);
            Near(target.Health, 939.2f);
        }

        private static void CollisionAndIce()
        {
            var battle = Pair(BattleSkillId.ShieldBash, targetArmor: 100);
            battle.AddUnit(Unit("blocker", BattleSide.Enemies, 1, health: 1000, armor: 100, speed: 0));
            battle.Start(); battle.Step();
            Check(HasEvent(battle, BattleEventKind.Collision), "occupied push collides");
            Check(battle.GetUnit(1).Slot == 0 && battle.GetUnit(2).Slot == 1, "collision does not overlap slots");
            Check(battle.GetUnit(2).Health < 1000 && battle.GetUnit(2).Armor < 100, "collision dual damage");
            var ice = Pair(BattleSkillId.ShieldBash, targetArmor: 100);
            ice.SetSurface(BattleSide.Enemies, 1, SurfaceKind.Ice);
            ice.Start(); ice.Step();
            Check(ice.GetUnit(1).Slot == 1 && (ice.GetUnit(1).Status & CombatStatus.Slipped) != 0, "entering ice slips");
            ice.Step(); Check(HasEvent(ice, BattleEventKind.Skipped), "slip skips action");
        }

        private static void TacticsAndCost()
        {
            var battle = new BattleSimulation();
            battle.AddUnit(Unit("caster", BattleSide.Allies, 0, skill: BattleSkillId.Lightning, speed: 100));
            battle.AddUnit(Unit("front", BattleSide.Enemies, 0, armor: 100));
            battle.AddUnit(Unit("wet", BattleSide.Enemies, 8, armor: 200));
            battle.GetUnit(2).ApplyStatus(CombatStatus.Wet, 2);
            Check(battle.SetTactic(0, 0, new TacticRule(BattleSkillId.Lightning, TacticCondition.TargetWet, TargetPriority.HighestArmor)), "valid rule accepted");
            Check(!battle.SetTactic(0, 4, new TacticRule(BattleSkillId.Lightning)), "bad row rejected");
            Check(!battle.SetTactic(0, 0, new TacticRule(BattleSkillId.Heal)), "unavailable skill rejected");
            battle.Start(); int ap = battle.GetUnit(0).AP; battle.Step();
            Check(battle.GetEvent(0).Kind == BattleEventKind.Action && battle.GetEvent(0).TargetId == 2, "conditions filter before targeting");
            Check(battle.GetUnit(0).AP == ap - 1, "AP paid once");
            Check(!battle.SetTactic(0, 0, new TacticRule(BattleSkillId.Lightning)), "cannot edit running tactics");
        }

        private static void PassiveCosts()
        {
            var battle = Pair(BattleSkillId.SwordStrike, targetArmor: 500, targetBlock: 1, targetGuard: true);
            battle.Start(); battle.Step();
            Check(battle.GetUnit(1).PP == 1 && HasEvent(battle, BattleEventKind.Guarded), "guard spends PP");
            Near(battle.GetUnit(1).Health, 997.725f); // 10 * .65 * (1-.5) * .7
            var sealedBattle = Pair(BattleSkillId.SwordStrike, targetArmor: 500, targetBlock: 1, targetGuard: true);
            sealedBattle.GetUnit(1).ApplyStatus(CombatStatus.Unbalanced, 2);
            sealedBattle.Start(); sealedBattle.Step();
            Check(sealedBattle.GetUnit(1).PP == 2 && !HasEvent(sealedBattle, BattleEventKind.Guarded), "sealed PP cannot guard");
            var armorlessGuard = Pair(BattleSkillId.SwordStrike, targetArmor: 0, targetBlock: 1, targetGuard: true);
            armorlessGuard.Start(); armorlessGuard.Step();
            Near(armorlessGuard.GetUnit(1).Health, 993);
            Check(armorlessGuard.GetUnit(1).PP == 1 && HasEvent(armorlessGuard, BattleEventKind.Guarded), "armorless guard remains effective and spends PP");

            var assist = new BattleSimulation();
            assist.AddUnit(Unit("striker", BattleSide.Allies, 4, attack: 10, speed: 100));
            assist.AddUnit(new BattleUnit(BattleCatalog.Rayn(), BattleSide.Allies, 3));
            assist.AddUnit(Unit("target", BattleSide.Enemies, 0, health: 1000, armor: 1000));
            assist.Start(); assist.Step();
            Check(assist.GetUnit(1).PP == 2, "assist consumes one PP");
            int assists = 0;
            for (int i = 0; i < assist.EventCount; i++) if (assist.GetEvent(i).Kind == BattleEventKind.Action && assist.GetEvent(i).SkillId == BattleSkillId.Assist) assists++;
            Check(assists == 1, "no recursive assist");
        }

        private static void HumanoidLoot()
        {
            foreach (float armor in new[] { 80f, 79.99f })
            {
                var battle = new BattleSimulation();
                battle.AddUnit(Unit("assassin", BattleSide.Allies, 0, skill: BattleSkillId.Backstab, attack: 100, speed: 100));
                battle.AddUnit(Unit("relic", BattleSide.Enemies, 0, health: 20, armor: 100, relic: true));
                Set(battle.GetUnit(1), CombatAttributes.Armor, armor);
                battle.Start(); battle.Step();
                Check(battle.State == BattleState.Victory, "victory on last death");
                Check(battle.LootCount == (armor == 80 ? 2 : 0), "exact 80% preserves armor and family clue");
                if (armor == 80)
                {
                    BattleLoot loot = battle.GetLoot(0);
                    Check(loot.Kind == BattleLootKind.IntactArmor && loot.Width == 2 && loot.Height == 3, "armor occupies 2x3");
                    Check(battle.GetLoot(1).Kind == BattleLootKind.FamilyRelic, "clue preserved");
                }
                int count = battle.LootCount;
                Check(!battle.Step() && battle.LootCount == count, "no duplicate corpse rewards");
            }
        }

        private static void CoreLoot()
        {
            foreach (BattleSkillId skill in new[] { BattleSkillId.HammerCrush, BattleSkillId.Backstab })
            {
                var battle = new BattleSimulation();
                battle.AddUnit(Unit("killer", BattleSide.Allies, 0, skill: skill, attack: 100, speed: 100));
                battle.AddUnit(Unit("demon", BattleSide.Enemies, 0, health: 10, armor: 30, kind: EnemyKind.Demon));
                battle.Start(); battle.Step();
                Check(battle.LootCount == (skill == BattleSkillId.HammerCrush ? 1 : 0), "core requires shattered armor");
                if (battle.LootCount > 0) Check(battle.GetLoot(0).Kind == BattleLootKind.Core && battle.GetLoot(0).Width == 1 && battle.GetLoot(0).Height == 1, "core occupies 1x1");
            }
        }

        private static void ReplayAndTermination()
        {
            foreach (uint seed in new uint[] { 1, 17, 1701 })
            for (int encounter = 0; encounter < 6; encounter++)
            for (int protagonist = 0; protagonist < 2; protagonist++)
            {
                var a = BattleCatalog.CreateDemo(protagonist == 1, encounter, seed);
                var b = BattleCatalog.CreateDemo(protagonist == 1, encounter, seed);
                a.Start(); b.Start();
                int steps = 0;
                while (a.State == BattleState.Fighting && steps++ <= BattleSimulation.MaximumActions + 1)
                {
                    a.Step(); b.Step();
                    Check(a.State == b.State && a.EventCount == b.EventCount, "same seed/state/event count");
                    for (int i = 0; i < a.UnitCount; i++)
                    {
                        BattleUnit x = a.GetUnit(i), y = b.GetUnit(i);
                        Check(x.Health == y.Health && x.Armor == y.Armor && x.AP == y.AP && x.PP == y.PP && x.Status == y.Status && x.Slot == y.Slot, "deterministic unit state");
                        Check(x.Health >= 0 && x.Armor >= 0 && x.AP >= 0 && x.PP >= 0, "nonnegative resource bounds");
                    }
                    for (int i = 0; i < a.EventCount; i++)
                    {
                        BattleEvent x = a.GetEvent(i), y = b.GetEvent(i);
                        Check(x.Kind == y.Kind && x.SourceId == y.SourceId && x.TargetId == y.TargetId && x.SkillId == y.SkillId && x.ArmorDamage == y.ArmorDamage && x.HealthDamage == y.HealthDamage, "deterministic events");
                    }
                }
                Check(a.State != BattleState.Fighting && a.LootCount == b.LootCount, "demo completes within cap");
                Check(a.State == BattleState.Victory, "default demo tactics should win");
                if (encounter == 0) Check(a.LootCount >= 2, "tutorial demonstrates intact armor and a family clue");
                Console.WriteLine("  seed " + seed + " encounter " + encounter + " leader " + protagonist + ": " + a.State + ", " + a.ActionNumber + " actions, " + a.LootCount + " loot");
            }
        }

        private static void DrawBoundaries()
        {
            var noAction = Pair(BattleSkillId.SwordStrike, targetArmor: 1000);
            noAction.SetTactic(0, 0, new TacticRule(BattleSkillId.SwordStrike, enabled: false));
            noAction.SetTactic(1, 0, new TacticRule(BattleSkillId.SwordStrike, enabled: false));
            noAction.Start(); while (noAction.Step()) { }
            Check(noAction.State == BattleState.Draw, "no enabled tactic drains AP into draw");
            var cap = new BattleSimulation();
            for (int i = 0; i < 6; i++) cap.AddUnit(Unit("zero" + i, i < 3 ? BattleSide.Allies : BattleSide.Enemies, i % 3, attack: 0, ap: 99));
            cap.Start(); int count = 0;
            while (cap.Step()) Check(++count <= BattleSimulation.MaximumActions + 1, "bounded steps");
            Check(cap.State == BattleState.Draw && cap.ActionNumber == BattleSimulation.MaximumActions, "hard cap terminates valid long encounter");
        }

        private static void StepReentry()
        {
            var battle = Pair(BattleSkillId.SwordStrike, targetArmor: 1000);
            int rejected = 0;
            battle.GetUnit(0).Asc.Attributes.Get(CombatAttributes.AP).OnCurrentValueChanged += (_, __, ___) =>
            { try { battle.Step(); } catch (InvalidOperationException) { rejected++; } };
            battle.Start(); battle.Step();
            Check(rejected == 1 && battle.ActionNumber == 1 && battle.GetUnit(0).AP == 2, "nested callback cannot double step");
        }

        private static void HotPathAllocation()
        {
            var battle = new BattleSimulation();
            battle.AddUnit(Unit("longA", BattleSide.Allies, 0, health: 100000, armor: 100000, attack: 1, ap: 99));
            battle.AddUnit(Unit("longB", BattleSide.Enemies, 0, health: 100000, armor: 100000, attack: 1, ap: 99));
            battle.Start(); for (int i = 0; i < 10; i++) battle.Step();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) battle.Step();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(allocated == 0, "100 warmed Step calls allocated " + allocated + " bytes");
        }

        private static BattleSimulation Pair(BattleSkillId skill, float targetArmor, float targetDodge = 0, float targetBlock = 0, bool targetGuard = false)
        {
            var battle = new BattleSimulation(12);
            battle.AddUnit(Unit("source", BattleSide.Allies, 0, skill: skill, health: 1000, attack: 10, speed: 100));
            battle.AddUnit(Unit("target", BattleSide.Enemies, 0, health: 1000, armor: targetArmor, buffer: .5f, dodge: targetDodge, block: targetBlock, guard: targetGuard));
            return battle;
        }
        private static BattleUnit Unit(string id, BattleSide side, int slot, BattleSkillId skill = BattleSkillId.SwordStrike,
            float health = 100, float armor = 0, float attack = 10, float speed = 1, int ap = 3, float buffer = 0,
            float dodge = 0, float block = 0, bool guard = false, EnemyKind kind = EnemyKind.Humanoid, bool relic = false)
            => new BattleUnit(Definition(id, skill, health, armor, attack, speed, ap, buffer, dodge, block, guard, kind, relic), side, slot);
        private static UnitDefinition Definition(string id, BattleSkillId skill = BattleSkillId.SwordStrike,
            float health = 100, float armor = 0, float attack = 10, float speed = 1, int ap = 3, float buffer = 0,
            float dodge = 0, float block = 0, bool guard = false, EnemyKind kind = EnemyKind.Humanoid, bool relic = false)
            => new UnitDefinition(id, id, "test", "family relic", health, armor, attack, speed, ap, 2, buffer, dodge, block, 1,
                kind, relic, guard ? new[] { skill, BattleSkillId.Guard } : new[] { skill }, new[] { new TacticRule(skill) });
        private static void Apply(BattleUnit source, BattleUnit target, BattleSkillId skill)
            => target.Asc.Effects.Apply(new GameplayEffectSpec(BattleCatalog.GetSkill(skill).Effect, source.Asc.Attributes, source.Asc));
        private static void Set(BattleUnit unit, AttributeId id, float value) => unit.Asc.Attributes.Get(id).BaseValue = value;
        private static bool HasEvent(BattleSimulation battle, BattleEventKind kind)
        { for (int i = 0; i < battle.EventCount; i++) if (battle.GetEvent(i).Kind == kind) return true; return false; }
        private static void Near(float actual, float expected)
        { if (Math.Abs(actual - expected) > .001f) throw new Exception("Expected " + expected + ", got " + actual); }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    }
}
