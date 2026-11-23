using System;
using System.Collections.Generic;
using IronOath.Campaign;
using IronOath.Combat;

namespace IronOath.Tests
{
    internal static class IntegrationTests
    {
        public static void Run(Action<string, Action> test)
        {
            test("integration / both protagonists finish real six-encounter chapter and pay Sunday rent", CompleteChapter);
            test("integration / reward bridge rejects unfinished battles and discards defeat or draw spoils", TerminalBoundary);
            test("integration / corpse armor condition, cores and depth-specific relics enter the campaign", RewardMapping);
            test("integration / saved expedition checkpoint resumes without consuming another daily attempt", ResumeCheckpoint);
            test("integration / real battle rewards survive a full bag, save and atomic retry", PendingRewardRecovery);
        }

        private static void CompleteChapter()
        {
            foreach (Protagonist protagonist in new[] { Protagonist.Rayn, Protagonist.Chloe })
            {
                var campaign = new CampaignSession(protagonist, 17);
                int encounters = 0;
                bool firstRelicRecovered = false;
                while (campaign.CurrentNode != null)
                {
                    if (campaign.CurrentNode.Kind == DungeonNodeKind.Battle && !campaign.CanExpeditionToday)
                        Require(campaign.CloseDay().Succeeded, "next battle needs the next day");
                    Require(campaign.TryEnterDungeonNode(out ExpeditionTicket ticket).Succeeded, "dungeon node must open");
                    if (ticket == null) continue;
                    var battle = BattleCatalog.CreateDemo(protagonist == Protagonist.Chloe, ticket.EncounterId, 17);
                    Finish(battle);
                    Equal(BattleState.Victory, battle.State);
                    ExpeditionOutcome outcome = BattleRewards.FromBattle(ticket, battle);
                    Equal(35 + ticket.Depth * 15, outcome.Coins);
                    int coins = campaign.Coins;
                    Require(campaign.SettleExpedition(outcome).Succeeded, "battle reward transaction must fit");
                    Equal(coins + outcome.Coins, campaign.Coins);
                    Require(!campaign.SettleExpedition(outcome).Succeeded, "completed battle cannot pay twice");
                    if (encounters == 0)
                    {
                        InventoryEntry relic = FindKind(campaign, ItemKind.FathersSignet);
                        Require(relic != null, "first real battle must demonstrate recovering a family clue");
                        if (!relic.IsAppraised) Require(campaign.TryAppraiseItem(relic.InstanceId).Succeeded, "Rayn identifies recovered clue");
                        firstRelicRecovered = campaign.HasClue(1);
                    }
                    SellStock(campaign);
                    encounters++;
                }
                Equal(6, encounters); Equal(15, campaign.DungeonNodeIndex); Equal(6, campaign.Day);
                Equal(3, campaign.ClueCount); Equal(CampaignState.Completed, campaign.State);
                Require(firstRelicRecovered, "family clue must come from a real combat result");
                Equal(protagonist == Protagonist.Rayn ? Protagonist.Chloe : Protagonist.Rayn, campaign.Partner);
                Require(campaign.CloseDay().Succeeded && campaign.RentDue, "Sunday arrives after the six battle days");
                int beforeRent = campaign.Coins;
                Require(campaign.TryPayRent().Succeeded, "victories and actual shop sales must fund rent");
                Equal(beforeRent - campaign.RentAmount, campaign.Coins);
                Require(!campaign.TryPayRent().Succeeded, "no second rent deduction");
                Require(campaign.CloseDay().Succeeded, "paid Sunday advances");
                Equal(2, campaign.Week);
                Console.WriteLine("  " + protagonist + ": six victories, three clues, Sunday paid, " + campaign.Coins + " coins remaining");
            }
        }

        private static void TerminalBoundary()
        {
            var campaign = new CampaignSession(Protagonist.Rayn);
            Require(campaign.TryEnterDungeonNode(out ExpeditionTicket ticket).Succeeded, "ticket");
            var preparation = BattleCatalog.CreateDemo();
            Throws<ArgumentNullException>(() => BattleRewards.FromBattle(null, preparation));
            Throws<ArgumentNullException>(() => BattleRewards.FromBattle(ticket, null));
            Throws<InvalidOperationException>(() => BattleRewards.FromBattle(ticket, preparation));
            preparation.Start();
            Throws<InvalidOperationException>(() => BattleRewards.FromBattle(ticket, preparation));
            foreach (BattleState expected in new[] { BattleState.Defeat, BattleState.Draw })
            {
                BattleSimulation battle = SpoilsBeforeLoss(expected);
                Finish(battle);
                Equal(expected, battle.State);
                Require(battle.LootCount > 0, "fixture earns loot before the overall battle is lost");
                ExpeditionOutcome outcome = BattleRewards.FromBattle(ticket, battle);
                Require(!outcome.Victory, "only Victory pays"); Equal(0, outcome.Coins); Equal(0, outcome.Loot.Length);
            }
            Equal(240, campaign.Coins); Equal(0, campaign.DungeonNodeIndex);
        }

        private static void RewardMapping()
        {
            var battle = new BattleSimulation();
            battle.AddUnit(Unit("hero", BattleSide.Allies, 0, 100, 0, 100, 100, BattleSkillId.Backstab));
            BattleUnit carrier = Unit("carrier", BattleSide.Enemies, 0, 10, 100, 0, 1, BattleSkillId.SwordStrike, relic: true);
            carrier.Asc.Attributes.Get(CombatAttributes.Armor).BaseValue = 80.6f;
            battle.AddUnit(carrier);
            Finish(battle);
            Equal(BattleState.Victory, battle.State); Equal(2, battle.LootCount);
            for (int depth = 1; depth <= 3; depth++)
            {
                var ticket = new ExpeditionTicket(100 + depth, DungeonCatalog.Get((depth - 1) * 5));
                ExpeditionOutcome outcome = BattleRewards.FromBattle(ticket, battle);
                Equal(ticket.Id, outcome.TicketId); Equal(35 + depth * 15, outcome.Coins);
                LootAward armor = FindAward(outcome, ItemKind.IntactArmor);
                Require(armor != null, "preserved armor maps to the 2x3 campaign item"); Equal(81, armor.Condition);
                ItemKind relic = depth == 1 ? ItemKind.FathersSignet : depth == 2 ? ItemKind.MothersLens : ItemKind.ChurchDispatch;
                Require(FindAward(outcome, relic) != null, "family relic follows expedition depth");
            }
            var campaign = new CampaignSession(Protagonist.Chloe);
            campaign.TryEnterDungeonNode(out ExpeditionTicket active);
            Require(campaign.SettleExpedition(BattleRewards.FromBattle(active, battle)).Succeeded, "mapped rewards enter real campaign");
            Equal(81, FindKind(campaign, ItemKind.IntactArmor).Condition);
            Require(campaign.HasClue(1), "Chloe appraises the actual recovered family relic");

            var coreBattle = new BattleSimulation();
            coreBattle.AddUnit(Unit("breaker", BattleSide.Allies, 0, 100, 0, 100, 100, BattleSkillId.HammerCrush));
            coreBattle.AddUnit(Unit("construct", BattleSide.Enemies, 0, 1, 1, 0, 1, BattleSkillId.SwordStrike, kind: EnemyKind.Construct));
            Finish(coreBattle);
            ExpeditionOutcome coreReward = BattleRewards.FromBattle(active, coreBattle);
            Equal(1, coreReward.Loot.Length); Equal(ItemKind.MagicCore, coreReward.Loot[0].Kind); Equal(100, coreReward.Loot[0].Condition);
        }

        private static void ResumeCheckpoint()
        {
            var campaign = new CampaignSession(Protagonist.Chloe, 17);
            campaign.TryEnterDungeonNode(out ExpeditionTicket ticket);
            var unfinished = BattleCatalog.CreateDemo(true, ticket.EncounterId, 17);
            unfinished.Start(); unfinished.Step();
            Require(CampaignSession.TryRestore(campaign.CaptureSave(), out CampaignSession restored, out _), "active checkpoint restores");
            Equal(ticket.Id, restored.ActiveExpedition.Id); Equal(ticket.EncounterId, restored.ActiveExpedition.EncounterId);
            Equal(1, restored.ExpeditionsToday);
            Require(!restored.TryEnterDungeonNode(out _).Succeeded, "restore must not purchase a second daily attempt");
            var resumedBattle = BattleCatalog.CreateDemo(true, restored.ActiveExpedition.EncounterId, 17);
            Finish(resumedBattle);
            ExpeditionOutcome outcome = BattleRewards.FromBattle(restored.ActiveExpedition, resumedBattle);
            Require(restored.SettleExpedition(outcome).Succeeded, "existing ticket settles after encounter restart");
            Equal(1, restored.DungeonNodeIndex); Equal(1, restored.ExpeditionsToday); Require(restored.ActiveExpedition == null, "ticket closes");
            int coins = restored.Coins;
            Require(!restored.SettleExpedition(outcome).Succeeded, "restored ticket remains idempotent"); Equal(coins, restored.Coins);
            Require(restored.HasClue(1), "checkpoint continuation keeps the real clue reward");
        }

        private static void PendingRewardRecovery()
        {
            var initial = new CampaignSession(Protagonist.Rayn, 17);
            CampaignSave full = initial.CaptureSave();
            full.Items = new InventoryItemSave[30]; full.NextItemId = 31;
            for (int i = 0; i < full.Items.Length; i++) full.Items[i] = new InventoryItemSave {
                Id = i + 1, Kind = ItemKind.MagicCore, Condition = 100, Appraised = true, X = i % 6, Y = i / 6
            };
            Require(CampaignSession.TryRestore(full, out CampaignSession campaign, out _), "full bag fixture restores");
            campaign.TryEnterDungeonNode(out ExpeditionTicket ticket);
            var battle = BattleCatalog.CreateDemo(false, ticket.EncounterId, 17);
            Finish(battle);
            ExpeditionOutcome outcome = BattleRewards.FromBattle(ticket, battle);
            Require(outcome.Loot.Length > 0, "real first victory produces recoverable loot");
            Require(!campaign.SettleExpedition(outcome).Succeeded && campaign.HasPendingRewards, "full bag preserves pending reward batch");
            Equal(240, campaign.Coins); Equal(0, campaign.DungeonNodeIndex); Equal(30, campaign.Inventory.Count);
            Require(CampaignSession.TryRestore(campaign.CaptureSave(), out CampaignSession restored, out _), "pending rewards survive reload");
            int coinsFromSales = 0, sold = 0;
            while (!restored.TryClaimExpeditionRewards().Succeeded)
            {
                Require(sold < 30, "a cleared bag must fit one encounter's loot");
                InventoryEntry item = restored.Inventory.GetAt(0);
                coinsFromSales += restored.GetSalePrice(item);
                Require(restored.TrySellItem(item.InstanceId).Succeeded, "selling is available while rewards are pending");
                sold++;
                Equal(240 + coinsFromSales, restored.Coins);
            }
            Equal(240 + coinsFromSales + outcome.Coins, restored.Coins);
            Equal(30 - sold + outcome.Loot.Length, restored.Inventory.Count);
            Equal(1, restored.DungeonNodeIndex); Equal(1, restored.ExpeditionsToday);
            Require(!restored.HasPendingRewards && restored.ActiveExpedition == null, "reward transaction clears only after full claim");
            int settledCoins = restored.Coins;
            Require(!restored.SettleExpedition(outcome).Succeeded, "replayed original result cannot pay again"); Equal(settledCoins, restored.Coins);
            InventoryEntry relic = FindKind(restored, ItemKind.FathersSignet);
            Require(relic != null && restored.TryAppraiseItem(relic.InstanceId).Succeeded && restored.HasClue(1), "Rayn identifies saved combat relic");
            Require(CampaignSession.TryRestore(restored.CaptureSave(), out _, out _), "claimed inventory remains a valid save");
        }

        private static BattleSimulation SpoilsBeforeLoss(BattleState result)
        {
            var battle = new BattleSimulation();
            battle.AddUnit(Unit("hero", BattleSide.Allies, 0, 10, 0, 100, 100, BattleSkillId.Backstab));
            battle.AddUnit(Unit("carrier", BattleSide.Enemies, 0, 1, 100, 0, 1, BattleSkillId.SwordStrike, relic: true));
            battle.AddUnit(Unit("survivor", BattleSide.Enemies, 1, 1000, 0, result == BattleState.Defeat ? 1000 : 0, 50, BattleSkillId.SwordStrike));
            return battle;
        }
        private static BattleUnit Unit(string id, BattleSide side, int slot, float hp, float armor, float attack, float speed,
            BattleSkillId skill, EnemyKind kind = EnemyKind.Humanoid, bool relic = false)
        {
            var definition = new UnitDefinition(id, id, "integration fixture", "fixture weapon", hp, armor, attack, speed,
                1, 0, .5f, 0, 0, 1, kind, relic, new[] { skill }, new[] { new TacticRule(skill) });
            return new BattleUnit(definition, side, slot);
        }
        private static void Finish(BattleSimulation battle)
        {
            Require(battle.Start(), "battle starts");
            int guard = 0;
            while (battle.State == BattleState.Fighting)
            {
                Require(guard++ <= BattleSimulation.MaximumActions + 1, "simulation must terminate");
                Require(battle.Step(), "fighting simulation advances");
            }
        }
        private static void SellStock(CampaignSession campaign)
        {
            while (campaign.Inventory.Count > 0)
            {
                InventoryEntry item = campaign.Inventory.GetAt(0);
                if (!item.IsAppraised) Require(campaign.TryAppraiseItem(item.InstanceId).Succeeded, "stock appraisal");
                Require(campaign.TrySellItem(item.InstanceId).Succeeded, "sell actual recovered stock");
            }
        }
        private static InventoryEntry FindKind(CampaignSession campaign, ItemKind kind)
        {
            for (int i = 0; i < campaign.Inventory.Count; i++) if (campaign.Inventory.GetAt(i).Definition.Kind == kind) return campaign.Inventory.GetAt(i);
            return null;
        }
        private static LootAward FindAward(ExpeditionOutcome outcome, ItemKind kind)
        {
            for (int i = 0; i < outcome.Loot.Length; i++) if (outcome.Loot[i].Kind == kind) return outcome.Loot[i];
            return null;
        }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual);
        }
        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
    }
}
