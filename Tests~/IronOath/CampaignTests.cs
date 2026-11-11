using System;
using System.Collections.Generic;
using IronOath.Campaign;

namespace IronOath.Tests
{
    internal static class CampaignTests
    {
        public static void Run(Action<string, Action> test)
        {
            test("Campaign: both siblings persist and starting economy is explicit", Siblings);
            test("Campaign: 6x5 bag placement, rotation and failed moves are atomic", BagGeometry);
            test("Campaign: insufficient space or coins cannot partially purchase", PurchaseAtomicity);
            test("Campaign: appraisal and single bargaining attempt respect protagonist perks", AppraisalAndBargaining);
            test("Campaign: owned relic appraisal unlocks a persistent family clue", FamilyClue);
            test("Campaign: armor repair and stolen mark work charge exactly once", RepairAndStolenMarks);
            test("Campaign: Sunday rent settles once and shortfall evicts", SundayRent);
            test("Campaign: weekly market and shop expansion change real prices", MarketAndExpansion);
            test("Campaign: expedition tickets reject mismatch and duplicate reward claims", RewardIdentity);
            test("Campaign: defeat pays no loot and uses the day's expedition attempt", DefeatAndDailyLimit);
            test("Campaign: full-bag rewards remain atomic, immutable and recoverable", OverflowRewards);
            test("Campaign: saves round-trip active tickets, pending rewards and negotiation", SaveRoundTrip);
            test("Campaign: malformed saves and outcomes are rejected without changing live state", SaveValidation);
            test("Campaign: all fifteen dungeon steps retain both partners and unlock three clues", FullChapter);
        }

        private static void Siblings()
        {
            var rayn = new CampaignSession(Protagonist.Rayn);
            var chloe = new CampaignSession(Protagonist.Chloe);
            Equal(Protagonist.Chloe, rayn.Partner); Equal(Protagonist.Rayn, chloe.Partner);
            Equal(240, rayn.Coins); Equal(320, rayn.RentAmount); Equal(1, rayn.Day); Equal("周一", rayn.DayName);
            Equal(6, rayn.DaysUntilRent); Equal(2, rayn.Inventory.Count); Equal(1, rayn.UnlockedDepth);
            Require(rayn.CanExpeditionToday && !rayn.RentDue);
        }

        private static void BagGeometry()
        {
            var bag = new GridInventory();
            Require(bag.TryPlace(new InventoryEntry(1, ItemKind.IntactArmor), 0, 0));
            Require(bag.TryPlace(new InventoryEntry(2, ItemKind.IntactArmor), 2, 0));
            Require(!bag.TryMove(1, 1, 0, false));
            Equal(1, bag.GetCell(0, 0)); Equal(2, bag.GetCell(2, 0)); Equal(0, bag.Find(1).X);
            var otherBag = new GridInventory(); Require(!otherBag.TryPlace(bag.Find(1), 0, 0)); Equal(0, otherBag.Count);
            Require(!bag.TryPlace(new InventoryEntry(1, ItemKind.MagicCore), 5, 4));
            Require(!bag.TryMove(1, -1, 0, false));
            Require(bag.TryPlace(new InventoryEntry(3, ItemKind.IntactArmor), 4, 0));
            Require(bag.TryPlace(new InventoryEntry(4, ItemKind.IntactArmor), 0, 3, true));
            Require(bag.TryPlace(new InventoryEntry(5, ItemKind.IntactArmor), 3, 3, true));
            Equal(30, bag.OccupiedCells); Equal(5, bag.Count);
            Require(!bag.TryAdd(new InventoryEntry(6, ItemKind.MagicCore))); Equal(5, bag.Count);
            Require(bag.TryRemove(2, out InventoryEntry removed)); Equal(2, removed.InstanceId); Equal(24, bag.OccupiedCells);
            Require(bag.TryMove(1, 2, 0, false)); Equal(0, bag.GetCell(0, 0)); Equal(1, bag.GetCell(2, 0));
            var rotate = new GridInventory();
            Require(rotate.TryPlace(new InventoryEntry(1, ItemKind.IntactArmor), 0, 0));
            Require(rotate.TryMove(1, 0, 0, true)); Equal(0, rotate.GetCell(0, 2)); Equal(1, rotate.GetCell(2, 1));
        }

        private static void PurchaseAtomicity()
        {
            CampaignSession full = FullBag();
            int coins = full.Coins; int offer = full.CurrentCustomer.Id;
            Require(!full.TryBuyCurrentOffer().Succeeded); Equal(coins, full.Coins); Equal(offer, full.CurrentCustomer.Id);
            CampaignSession poor = AlterSave(new CampaignSession(Protagonist.Rayn), save => save.Coins = 0);
            int count = poor.Inventory.Count;
            Require(!poor.TryBuyCurrentOffer().Succeeded); Equal(count, poor.Inventory.Count); Equal(0, poor.Coins);
            var buyer = new CampaignSession(Protagonist.Rayn);
            int price = buyer.CurrentCustomer.AskingPrice; int customer = buyer.CurrentCustomer.Id;
            Require(buyer.TryBuyCurrentOffer().Succeeded);
            Equal(240 - price, buyer.Coins); Equal(3, buyer.Inventory.Count); Require(customer != buyer.CurrentCustomer.Id);
        }

        private static void AppraisalAndBargaining()
        {
            var rayn = new CampaignSession(Protagonist.Rayn, 1);
            var chloe = new CampaignSession(Protagonist.Chloe, 1);
            Require(!rayn.CurrentCustomer.IsAppraised && chloe.CurrentCustomer.IsAppraised);
            Require(rayn.AppraiseCurrentCustomer().Succeeded); Equal(235, rayn.Coins);
            Require(!rayn.AppraiseCurrentCustomer().Succeeded); Equal(235, rayn.Coins);
            Require(rayn.TryBargain().Succeeded && chloe.TryBargain().Succeeded);
            Require(chloe.CurrentCustomer.AskingPrice < rayn.CurrentCustomer.AskingPrice);
            int price = rayn.CurrentCustomer.AskingPrice;
            Require(!rayn.TryBargain().Succeeded); Equal(price, rayn.CurrentCustomer.AskingPrice);
            Require(rayn.TryBuyCurrentOffer().Succeeded);
            Require(!rayn.CurrentCustomer.BargainAttempted);
        }

        private static void FamilyClue()
        {
            var rayn = new CampaignSession(Protagonist.Rayn);
            rayn.SkipCustomer(); rayn.SkipCustomer();
            Equal(ItemKind.FathersSignet, rayn.CurrentCustomer.Item.Kind);
            Require(rayn.TryBuyCurrentOffer().Succeeded); Equal(0, rayn.ClueCount);
            InventoryEntry ring = FindKind(rayn, ItemKind.FathersSignet);
            Require(!rayn.TrySellItem(ring.InstanceId).Succeeded);
            Require(rayn.TryAppraiseItem(ring.InstanceId).Succeeded); Equal(1, rayn.ClueCount); Equal(2, rayn.UnlockedDepth);
            Require(rayn.TrySellItem(ring.InstanceId).Succeeded); Equal(1, rayn.ClueCount);
            var chloe = new CampaignSession(Protagonist.Chloe);
            chloe.SkipCustomer(); chloe.SkipCustomer();
            Require(chloe.TryBuyCurrentOffer().Succeeded); Require(chloe.HasClue(1));
            Require(chloe.CurrentCustomer == null);
        }

        private static void RepairAndStolenMarks()
        {
            var rayn = new CampaignSession(Protagonist.Rayn);
            InventoryEntry armor = FindKind(rayn, ItemKind.DamagedArmor);
            int cost = rayn.GetRepairCost(armor); int coins = rayn.Coins; int oldPrice = rayn.GetSalePrice(armor);
            Require(rayn.TryRepairItem(armor.InstanceId).Succeeded); Equal(coins - cost, rayn.Coins); Equal(100, armor.Condition);
            Require(rayn.GetSalePrice(armor) > oldPrice);
            Require(!rayn.TryRepairItem(armor.InstanceId).Succeeded); Equal(coins - cost, rayn.Coins);
            rayn.SkipCustomer(); rayn.SkipCustomer(); rayn.TryBuyCurrentOffer();
            InventoryEntry ring = FindKind(rayn, ItemKind.FathersSignet); int before = rayn.Coins;
            Require(rayn.TryRemoveStolenMark(ring.InstanceId).Succeeded); Equal(before - 6, rayn.Coins); Require(!ring.IsStolen);
            Require(!rayn.TryRemoveStolenMark(ring.InstanceId).Succeeded); Equal(before - 6, rayn.Coins);
            var chloe = new CampaignSession(Protagonist.Chloe);
            chloe.SkipCustomer(); chloe.SkipCustomer(); chloe.TryBuyCurrentOffer(); before = chloe.Coins;
            Require(chloe.TryRemoveStolenMark(FindKind(chloe, ItemKind.FathersSignet).InstanceId).Succeeded); Equal(before - 12, chloe.Coins);
        }

        private static void SundayRent()
        {
            var session = new CampaignSession(Protagonist.Rayn);
            for (int i = 0; i < 6; i++) Require(session.CloseDay().Succeeded);
            Require(session.RentDue); Equal("周日", session.DayName);
            session = AlterSave(session, save => save.Coins = CampaignSession.BaseRent);
            Require(session.TryPayRent().Succeeded); Equal(0, session.Coins); Require(!session.RentDue);
            Require(!session.TryPayRent().Succeeded); Equal(0, session.Coins);
            Require(session.CloseDay().Succeeded); Equal(8, session.Day); Equal(2, session.Week); Equal(0, session.Coins);
            var insufficient = new CampaignSession(Protagonist.Rayn);
            for (int i = 0; i < 6; i++) insufficient.CloseDay();
            Require(!insufficient.CloseDay().Succeeded); Equal(CampaignState.Evicted, insufficient.State); Equal(7, insufficient.Day);
            Require(!insufficient.TrySellItem(insufficient.Inventory.GetAt(0).InstanceId).Succeeded);
            Require(!insufficient.TryEnterDungeonNode(out _).Succeeded);
            var auto = new CampaignSession(Protagonist.Chloe);
            for (int i = 0; i < 6; i++) auto.CloseDay();
            auto = AlterSave(auto, save => save.Coins = 500);
            Require(auto.CloseDay().Succeeded); Equal(180, auto.Coins); Equal(8, auto.Day);
        }

        private static void MarketAndExpansion()
        {
            var session = new CampaignSession(Protagonist.Chloe);
            InventoryEntry armor = session.Inventory.GetAt(0);
            int basePrice = session.GetSalePrice(armor);
            Require(session.TryUpgradeShop().Succeeded); Equal(1, session.ShopLevel); Equal(380, session.RentAmount);
            Require(session.GetSalePrice(armor) > basePrice); Equal(4, session.RemainingCustomers);
            Require(!session.TryUpgradeShop().Succeeded);
            session = AlterSave(session, save => save.Coins = 1000);
            for (int i = 0; i < 7; i++) Require(session.CloseDay().Succeeded);
            Equal(2, session.Newspaper.Week); Equal(85, session.Newspaper.ArmorPercent); Equal(130, session.Newspaper.CorePercent);
            Require(session.GetSalePrice(armor) < basePrice);
        }

        private static void RewardIdentity()
        {
            var session = new CampaignSession(Protagonist.Rayn);
            Require(session.TryEnterDungeonNode(out ExpeditionTicket ticket).Succeeded && ticket != null);
            Require(!session.CloseDay().Succeeded && !session.TryBuyCurrentOffer().Succeeded);
            Require(!session.SettleExpedition(new ExpeditionOutcome { TicketId = ticket.Id + 1, Victory = true, Coins = 100 }).Succeeded);
            Equal(240, session.Coins);
            var result = new ExpeditionOutcome { TicketId = ticket.Id, Victory = true, Coins = 40,
                Loot = new[] { new LootAward(ItemKind.IntactArmor, 80) } };
            Require(session.SettleExpedition(result).Succeeded); Equal(280, session.Coins); Equal(1, session.DungeonNodeIndex);
            Equal(100, FindKind(session, ItemKind.IntactArmor).Condition);
            Require(!session.SettleExpedition(result).Succeeded); Equal(280, session.Coins); Equal(1, session.DungeonNodeIndex);
            Require(session.TryEnterDungeonNode(out ticket).Succeeded && ticket == null); // Cache is a free exploration step.
            Require(!session.TryEnterDungeonNode(out _).Succeeded); Equal(2, session.DungeonNodeIndex);
            Require(session.CloseDay().Succeeded); Require(session.TryEnterDungeonNode(out _).Succeeded);
        }

        private static void DefeatAndDailyLimit()
        {
            var session = new CampaignSession(Protagonist.Chloe);
            session.TryEnterDungeonNode(out ExpeditionTicket first);
            Require(!session.SettleExpedition(new ExpeditionOutcome { TicketId = first.Id, Victory = false, Coins = 1 }).Succeeded);
            Require(session.AbandonExpedition().Succeeded); Equal(220, session.Coins); Equal(0, session.DungeonNodeIndex);
            Equal(1, session.ExpeditionsToday); Require(!session.CanExpeditionToday);
            Require(!session.TryEnterDungeonNode(out _).Succeeded);
            Require(session.CloseDay().Succeeded); Equal(0, session.ExpeditionsToday);
            Require(session.TryEnterDungeonNode(out ExpeditionTicket second).Succeeded); Require(second.Id > first.Id);
            Require(!session.SettleExpedition(new ExpeditionOutcome { TicketId = first.Id, Victory = true }).Succeeded);
        }

        private static void OverflowRewards()
        {
            CampaignSession session = FullBag();
            session.TryEnterDungeonNode(out ExpeditionTicket ticket);
            var outcome = new ExpeditionOutcome { TicketId = ticket.Id, Victory = true, Coins = 80,
                Loot = new[] { new LootAward(ItemKind.MagicCore), new LootAward(ItemKind.MagicCore) } };
            Require(!session.SettleExpedition(outcome).Succeeded); Require(session.HasPendingRewards);
            Equal(240, session.Coins); Equal(0, session.DungeonNodeIndex); Equal(30, session.Inventory.Count);
            outcome.Coins = 9999; outcome.Loot[0].Kind = ItemKind.IntactArmor; // Must not mutate retained rewards.
            Require(session.TrySellItem(session.Inventory.GetAt(0).InstanceId).Succeeded);
            int soldCoins = session.Coins;
            Require(!session.TryClaimExpeditionRewards().Succeeded); Equal(soldCoins, session.Coins);
            Require(session.TrySellItem(session.Inventory.GetAt(0).InstanceId).Succeeded); soldCoins = session.Coins;
            Require(session.TryClaimExpeditionRewards().Succeeded);
            Equal(soldCoins + 80, session.Coins); Equal(30, session.Inventory.Count); Equal(1, session.DungeonNodeIndex);
            Require(!session.HasPendingRewards && session.ActiveExpedition == null);
            Require(!session.TryClaimExpeditionRewards().Succeeded); Equal(soldCoins + 80, session.Coins);
        }

        private static void SaveRoundTrip()
        {
            var session = new CampaignSession(Protagonist.Rayn, 1);
            session.AppraiseCurrentCustomer(); session.TryBargain();
            CampaignSave save = session.CaptureSave();
            Require(CampaignSession.TryRestore(save, out CampaignSession restored, out _));
            Equal(session.Coins, restored.Coins); Equal(session.CurrentCustomer.AskingPrice, restored.CurrentCustomer.AskingPrice);
            Require(restored.CurrentCustomer.IsAppraised && restored.CurrentCustomer.BargainAttempted);
            save.Items[0].Condition = 1; Equal(65, session.Inventory.GetAt(0).Condition); Equal(65, restored.Inventory.GetAt(0).Condition);
            session.TryEnterDungeonNode(out ExpeditionTicket ticket);
            Require(CampaignSession.TryRestore(session.CaptureSave(), out restored, out _));
            Equal(ticket.Id, restored.ActiveExpedition.Id); Equal(1, restored.ExpeditionsToday);
            Require(restored.AbandonExpedition().Succeeded);
            CampaignSession full = FullBag(); full.TryEnterDungeonNode(out ticket);
            full.SettleExpedition(new ExpeditionOutcome { TicketId = ticket.Id, Victory = true, Coins = 55, Loot = new[] { new LootAward(ItemKind.MagicCore) } });
            CampaignSave pending = full.CaptureSave();
            Require(CampaignSession.TryRestore(pending, out restored, out _)); Require(restored.HasPendingRewards);
            pending.PendingOutcome.Coins = 0;
            Require(restored.TrySellItem(restored.Inventory.GetAt(0).InstanceId).Succeeded); int before = restored.Coins;
            Require(restored.TryClaimExpeditionRewards().Succeeded); Equal(before + 55, restored.Coins);
        }

        private static void SaveValidation()
        {
            var session = new CampaignSession(Protagonist.Rayn);
            RejectSave(session, save => save.Version = 999);
            RejectSave(session, save => save.Coins = -1);
            RejectSave(session, save => save.Items[1].Id = save.Items[0].Id);
            RejectSave(session, save => { save.Items[1].X = 0; save.Items[1].Y = 0; });
            RejectSave(session, save => save.Items[0].Condition = 101);
            RejectSave(session, save => save.RandomState = 0);
            RejectSave(session, save => save.PaidRentWeek = 1);
            RejectSave(session, save => save.State = CampaignState.Evicted);
            RejectSave(session, save => save.ExpeditionsToday = 2);
            RejectSave(session, save => save.Customer.AskingPrice = -1);
            RejectSave(session, save => save.NodeIndex = 8);
            RejectSave(session, save => save.PendingOutcome = new ExpeditionOutcome { TicketId = 1, Victory = true });
            Equal(240, session.Coins); Equal(2, session.Inventory.Count);
            session.TryEnterDungeonNode(out ExpeditionTicket ticket);
            Require(!session.SettleExpedition(new ExpeditionOutcome { TicketId = ticket.Id, Victory = true, Coins = int.MaxValue }).Succeeded);
            Require(!session.SettleExpedition(new ExpeditionOutcome { TicketId = ticket.Id, Victory = true,
                Loot = new[] { new LootAward((ItemKind)999) } }).Succeeded);
            Equal(240, session.Coins); Require(!session.HasPendingRewards);
        }

        private static void FullChapter()
        {
            var session = new CampaignSession(Protagonist.Chloe);
            int battles = 0;
            while (session.CurrentNode != null)
            {
                if (session.CurrentNode.Kind == DungeonNodeKind.Battle && !session.CanExpeditionToday) Require(session.CloseDay().Succeeded);
                Require(session.TryEnterDungeonNode(out ExpeditionTicket ticket).Succeeded);
                if (ticket != null)
                {
                    battles++;
                    Require(session.SettleExpedition(new ExpeditionOutcome { TicketId = ticket.Id, Victory = true, Coins = 100 }).Succeeded);
                }
            }
            Equal(6, battles); Equal(15, session.DungeonNodeIndex); Equal(3, session.ClueCount);
            Equal(3, session.UnlockedDepth); Equal(CampaignState.Completed, session.State); Equal(Protagonist.Rayn, session.Partner);
            Equal(6, session.Day); Require(session.CloseDay().Succeeded); Require(session.RentDue);
            Require(session.TryPayRent().Succeeded); Require(session.CloseDay().Succeeded); Equal(2, session.Week);
            Require(CampaignSession.TryRestore(session.CaptureSave(), out CampaignSession restored, out _));
            Equal(CampaignState.Completed, restored.State); Equal(3, restored.ClueCount);
        }

        private static CampaignSession FullBag()
        {
            return AlterSave(new CampaignSession(Protagonist.Rayn), save => {
                save.Items = new InventoryItemSave[30]; save.NextItemId = 31;
                for (int i = 0; i < 30; i++) save.Items[i] = new InventoryItemSave { Id = i + 1, Kind = ItemKind.MagicCore,
                    Condition = 100, Appraised = true, X = i % 6, Y = i / 6 };
            });
        }
        private static CampaignSession AlterSave(CampaignSession session, Action<CampaignSave> alter)
        {
            CampaignSave save = session.CaptureSave(); alter(save);
            if (!CampaignSession.TryRestore(save, out CampaignSession restored, out string error)) throw new Exception(error);
            return restored;
        }
        private static void RejectSave(CampaignSession session, Action<CampaignSave> alter)
        {
            CampaignSave save = session.CaptureSave(); alter(save);
            Require(!CampaignSession.TryRestore(save, out CampaignSession restored, out _)); Require(restored == null);
        }
        private static InventoryEntry FindKind(CampaignSession session, ItemKind kind)
        {
            for (int i = 0; i < session.Inventory.Count; i++) if (session.Inventory.GetAt(i).Definition.Kind == kind) return session.Inventory.GetAt(i);
            throw new Exception("Missing item: " + kind);
        }
        private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
        private static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual);
        }
    }
}
