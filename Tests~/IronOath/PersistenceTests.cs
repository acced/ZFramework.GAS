using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using IronOath.Campaign;
using IronOath.Combat;
using IronOath.Presentation;

namespace IronOath.Tests
{
    /// <summary>
    /// Portable tests of the production save store and real filesystem transactions.
    /// System.Text.Json supplies only the codec; actual JsonUtility compatibility is tested in Unity.
    /// </summary>
    internal static class PersistenceTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

        public static void Run(Action<string, Action> test)
        {
            test("persistence / v1 campaign, formation and tactics round-trip through real files", ProfileRoundTrip);
            test("persistence / recovering a corrupt primary preserves the last good backup", BackupRecovery);
            test("persistence / malformed whole profiles are rejected without partial restore", InvalidProfiles);
            test("persistence / invalid live profiles cannot overwrite either saved file", InvalidWrites);
            test("persistence / write and codec failures preserve previous saves", FailedWrites);
            test("persistence / empty pending rewards and absent inline objects survive the full profile", PendingCheckpoint);
        }

        private static void ProfileRoundTrip()
        {
            using var directory = new TemporaryDirectory();
            IronOathSaveStore store = CreateStore(directory.Path);
            var campaign = new CampaignSession(Protagonist.Rayn, 17);
            PrepareParty(out int[] formation, out TacticRule[][] tactics);
            Require(campaign.AppraiseCurrentCustomer().Succeeded, "appraise before save");
            Require(campaign.TryBargain().Succeeded, "bargain before save");
            Require(store.TrySave(campaign, formation, tactics, out string error), error);
            string original = File.ReadAllText(store.MainPath);
            Require(!File.Exists(store.BackupPath) && !File.Exists(store.MainPath + ".tmp"), "first save leaves only primary");
            Require(store.TryLoad(out IronOathLoadedSave loaded, out error), error);
            Require(!loaded.RecoveredFromBackup, "valid primary loads directly");
            Equal(campaign.Coins, loaded.Campaign.Coins); Equal(campaign.Day, loaded.Campaign.Day);
            Equal(campaign.CurrentCustomer.AskingPrice, loaded.Campaign.CurrentCustomer.AskingPrice);
            Require(loaded.Campaign.CurrentCustomer.IsAppraised && loaded.Campaign.CurrentCustomer.BargainAttempted, "customer state");
            for (int unit = 0; unit < Formation.MaxUnitsPerSide; unit++)
            {
                Equal(formation[unit], loaded.Formation[unit]);
                for (int row = 0; row < BattleUnit.TacticCount; row++)
                {
                    TacticRule expected = tactics[unit][row], actual = loaded.Tactics[unit][row];
                    Equal(expected.Skill, actual.Skill); Equal(expected.Condition, actual.Condition);
                    Equal(expected.Priority, actual.Priority); Equal(expected.Enabled, actual.Enabled);
                }
            }
            Require(campaign.CloseDay().Succeeded, "advance live campaign");
            Require(store.TrySave(campaign, formation, tactics, out error), error);
            Equal(original, File.ReadAllText(store.BackupPath));
            Require(store.TryLoad(out loaded, out error), error); Equal(2, loaded.Campaign.Day);

            // Original v1 profiles could contain null or empty arrays before the first formation visit.
            IronOathSaveData data = Deserialize(original);
            data.Formation = Array.Empty<int>(); data.Tactics = null;
            File.WriteAllText(store.MainPath, Serialize(data));
            Require(store.TryLoad(out loaded, out error), error);
            Require(!loaded.RecoveredFromBackup && loaded.Formation == null, "empty original formation remains unconfigured");
            for (int i = 0; i < loaded.Tactics.Length; i++) Require(loaded.Tactics[i] == null, "absent tactics remain default");
        }

        private static void BackupRecovery()
        {
            using var directory = new TemporaryDirectory();
            IronOathSaveStore store = CreateStore(directory.Path);
            var campaign = new CampaignSession(Protagonist.Chloe);
            Require(store.TrySave(campaign, null, null, out string error), error);
            string dayOne = File.ReadAllText(store.MainPath);
            Require(campaign.CloseDay().Succeeded, "day two");
            Require(store.TrySave(campaign, null, null, out error), error);
            Equal(dayOne, File.ReadAllText(store.BackupPath));
            File.WriteAllText(store.MainPath, "{truncated");
            Require(store.TryLoad(out IronOathLoadedSave recovered, out error), error);
            Require(recovered.RecoveredFromBackup, "fallback must be reported"); Equal(1, recovered.Campaign.Day);
            Require(recovered.Campaign.CloseDay().Succeeded, "continue recovered campaign");
            Require(store.TrySave(recovered.Campaign, recovered.Formation, recovered.Tactics, out error), error);
            Equal(dayOne, File.ReadAllText(store.BackupPath));
            Require(store.TryLoad(out IronOathLoadedSave loaded, out error), error);
            Require(!loaded.RecoveredFromBackup, "replacement primary is usable"); Equal(2, loaded.Campaign.Day);

            // An unreadable primary must not poison the backup on the next successful save.
            File.WriteAllText(store.MainPath, new string('x', 1024 * 1024 + 1));
            Require(store.TryLoad(out loaded, out error), error);
            Require(loaded.RecoveredFromBackup, "oversized primary is rejected before decode"); Equal(1, loaded.Campaign.Day);
            File.Delete(store.MainPath);
            Require(store.TryLoad(out loaded, out error), error);
            Require(loaded.RecoveredFromBackup, "missing primary also recovers backup"); Equal(1, loaded.Campaign.Day);
        }

        private static void InvalidProfiles()
        {
            using var directory = new TemporaryDirectory();
            IronOathSaveStore store = CreateStore(directory.Path);
            var campaign = new CampaignSession(Protagonist.Rayn);
            PrepareParty(out int[] formation, out TacticRule[][] tactics);
            Require(store.TrySave(campaign, formation, tactics, out string error), error);
            string valid = File.ReadAllText(store.MainPath);
            Action<IronOathSaveData>[] corruptions = {
                data => data.Version = 2,
                data => data.Campaign.Coins = -1,
                data => data.Formation[1] = data.Formation[0],
                data => data.Formation[0] = 9,
                data => data.Formation = new[] { 0, 1, 2, 3, 4 },
                data => { data.Tactics[0].Skill = BattleSkillId.Frost; data.Tactics[0].Enabled = true; },
                data => Array.Resize(ref data.Tactics, data.Tactics.Length - 1),
                data => data.Tactics[1].Row = data.Tactics[0].Row,
                data => data.Tactics[0].Unit = Formation.MaxUnitsPerSide,
                data => data.Tactics[0].Row = BattleUnit.TacticCount,
                data => data.Tactics[0].Skill = (BattleSkillId)999,
                data => data.Tactics[0].Condition = (TacticCondition)999,
                data => data.Tactics[0].Priority = (TargetPriority)999,
                data => data.Tactics[0] = null
            };
            for (int i = 0; i < corruptions.Length; i++)
            {
                IronOathSaveData malformed = Deserialize(valid);
                corruptions[i](malformed);
                string invalid = Serialize(malformed);
                File.WriteAllText(store.MainPath, invalid);
                Require(!store.TryLoad(out IronOathLoadedSave loaded, out error), "reject malformed profile " + i);
                Require(loaded == null && !string.IsNullOrEmpty(error), "failed load exposes no partial session");
                Equal(invalid, File.ReadAllText(store.MainPath));
                Equal(240, campaign.Coins); Equal(1, campaign.Day);
                Require(!File.Exists(store.BackupPath), "read cannot create backup");
            }
            File.WriteAllText(store.MainPath, valid);
            Require(store.TryLoad(out _, out error), error);
        }

        private static void InvalidWrites()
        {
            using var directory = new TemporaryDirectory();
            IronOathSaveStore store = CreateStore(directory.Path);
            var campaign = new CampaignSession(Protagonist.Rayn);
            PrepareParty(out int[] formation, out TacticRule[][] tactics);
            Require(store.TrySave(campaign, formation, tactics, out string error), error);
            Require(campaign.CloseDay().Succeeded, "day two");
            Require(store.TrySave(campaign, formation, tactics, out error), error);
            string primary = File.ReadAllText(store.MainPath), backup = File.ReadAllText(store.BackupPath);
            int[] duplicate = (int[])formation.Clone(); duplicate[1] = duplicate[0];
            Require(!store.TrySave(campaign, duplicate, tactics, out error), "duplicate positions cannot be saved");
            Require(!string.IsNullOrEmpty(error), "validation reason");
            Require(!store.TrySave(campaign, formation, new TacticRule[1][], out _), "wrong party size");
            var incomplete = new TacticRule[Formation.MaxUnitsPerSide][];
            incomplete[0] = new TacticRule[1];
            Require(!store.TrySave(campaign, formation, incomplete, out _), "incomplete unit rows");
            tactics[0][0] = new TacticRule(BattleSkillId.Frost);
            Require(!store.TrySave(campaign, formation, tactics, out _), "unavailable active skill");
            Require(!store.TrySave(null, null, null, out _), "null campaign");
            Equal(primary, File.ReadAllText(store.MainPath)); Equal(backup, File.ReadAllText(store.BackupPath));
            Require(!File.Exists(store.MainPath + ".tmp"), "validation failure leaves no temporary file");
        }

        private static void FailedWrites()
        {
            using var directory = new TemporaryDirectory();
            IronOathSaveStore store = CreateStore(directory.Path);
            var campaign = new CampaignSession(Protagonist.Chloe);
            Require(store.TrySave(campaign, null, null, out string error), error);
            Require(campaign.CloseDay().Succeeded, "day two");
            Require(store.TrySave(campaign, null, null, out error), error);
            string primary = File.ReadAllText(store.MainPath), backup = File.ReadAllText(store.BackupPath);
            Require(campaign.CloseDay().Succeeded, "unsaved day three");
            // A directory at the temporary filename reliably denies file creation even under an administrator account.
            Directory.CreateDirectory(store.MainPath + ".tmp");
            Require(!store.TrySave(campaign, null, null, out error), "filesystem write must fail");
            Require(!string.IsNullOrEmpty(error), "filesystem failure reported");
            Equal(primary, File.ReadAllText(store.MainPath)); Equal(backup, File.ReadAllText(store.BackupPath));
            Directory.Delete(store.MainPath + ".tmp");

            var failingCodec = new IronOathSaveStore(directory.Path,
                _ => throw new InvalidDataException("Injected serialization failure."), Deserialize);
            Require(!failingCodec.TrySave(campaign, null, null, out error), "codec failure is contained");
            var oversizedCodec = new IronOathSaveStore(directory.Path, _ => new string('x', 1024 * 1024 + 1), Deserialize);
            Require(!oversizedCodec.TrySave(campaign, null, null, out error), "oversized serialized payload is rejected");
            Equal(primary, File.ReadAllText(store.MainPath)); Equal(backup, File.ReadAllText(store.BackupPath));
            Require(!File.Exists(store.MainPath + ".tmp"), "no abandoned partial file");
            Require(store.TryLoad(out IronOathLoadedSave loaded, out error), error); Equal(2, loaded.Campaign.Day);
        }

        private static void PendingCheckpoint()
        {
            using var directory = new TemporaryDirectory();
            IronOathSaveStore store = CreateStore(directory.Path);
            CampaignSave full = new CampaignSession(Protagonist.Rayn).CaptureSave();
            full.Items = new InventoryItemSave[30]; full.NextItemId = 31;
            for (int i = 0; i < full.Items.Length; i++) full.Items[i] = new InventoryItemSave
            { Id = i + 1, Kind = ItemKind.MagicCore, Condition = 100, Appraised = true, X = i % 6, Y = i / 6 };
            Require(CampaignSession.TryRestore(full, out CampaignSession campaign, out string error), error);
            Require(campaign.TryEnterDungeonNode(out ExpeditionTicket ticket).Succeeded, "active expedition");
            Require(store.TrySave(campaign, null, null, out error), error);
            Require(store.TryLoad(out IronOathLoadedSave active, out error), error);
            Equal(ticket.Id, active.Campaign.ActiveExpedition.Id); Equal(1, active.Campaign.ExpeditionsToday);
            Require(!active.Campaign.HasPendingRewards, "active encounter has no fabricated result");

            Require(!campaign.SettleExpedition(new ExpeditionOutcome { TicketId = ticket.Id, Victory = true, Coins = 50,
                Loot = new[] { new LootAward(ItemKind.FathersSignet) } }).Succeeded, "reward remains pending");
            Require(campaign.DiscardPendingLoot(0).Succeeded, "discard without granting a clue");
            while (campaign.CurrentCustomer != null) Require(campaign.SkipCustomer().Succeeded, "exhaust customers");
            Require(store.TrySave(campaign, null, null, out error), error);
            Require(store.TryLoad(out IronOathLoadedSave loaded, out error), error);
            Require(loaded.Campaign.CurrentCustomer == null && loaded.Campaign.HasPendingRewards, "absence and empty result differ");
            Equal(0, loaded.Campaign.PendingLootCount); Equal(50, loaded.Campaign.PendingRewardCoins);
            Require(loaded.Campaign.TryClaimExpeditionRewards().Succeeded, "empty award batch still pays coins");
            Equal(290, loaded.Campaign.Coins); Equal(0, loaded.Campaign.ClueCount); Equal(1, loaded.Campaign.DungeonNodeIndex);
            Require(store.TrySave(loaded.Campaign, loaded.Formation, loaded.Tactics, out error), error);

            // Emulate inline default objects to exercise the production presence flags independently of the codec.
            IronOathSaveData materialized = Deserialize(File.ReadAllText(store.MainPath));
            Require(!materialized.HasCustomer && !materialized.HasPendingOutcome, "absence flags were saved");
            materialized.Campaign.Customer = new CustomerSave();
            materialized.Campaign.PendingOutcome = new ExpeditionOutcome();
            File.WriteAllText(store.MainPath, Serialize(materialized));
            Require(store.TryLoad(out loaded, out error), error);
            Require(!loaded.RecoveredFromBackup && loaded.Campaign.CurrentCustomer == null && !loaded.Campaign.HasPendingRewards,
                "presence flags normalize inline defaults");
            Require(!loaded.Campaign.TryClaimExpeditionRewards().Succeeded, "settled result cannot be claimed again");
            Equal(290, loaded.Campaign.Coins);
        }

        private static void PrepareParty(out int[] formation, out TacticRule[][] tactics)
        {
            BattleSimulation battle = BattleCatalog.CreateDemo();
            Require(battle.MoveUnit(0, 8), "custom formation");
            Require(battle.SetTactic(0, 0, new TacticRule(BattleSkillId.SwordStrike,
                TacticCondition.TargetArmored, TargetPriority.HighestArmor)), "custom valid tactic");
            formation = new int[Formation.MaxUnitsPerSide];
            tactics = new TacticRule[Formation.MaxUnitsPerSide][];
            for (int unit = 0; unit < formation.Length; unit++)
            {
                BattleUnit actor = battle.GetUnit(unit);
                formation[unit] = actor.Slot;
                tactics[unit] = new TacticRule[BattleUnit.TacticCount];
                for (int row = 0; row < BattleUnit.TacticCount; row++) tactics[unit][row] = actor.GetTactic(row);
            }
        }

        private static IronOathSaveStore CreateStore(string path) => new IronOathSaveStore(path, Serialize, Deserialize);
        private static string Serialize(IronOathSaveData data) => JsonSerializer.Serialize(data, JsonOptions);
        private static IronOathSaveData Deserialize(string text) => JsonSerializer.Deserialize<IronOathSaveData>(text, JsonOptions);
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual);
        }
        private sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iron-oath-persistence-" + Guid.NewGuid().ToString("N"));
            public TemporaryDirectory() { Directory.CreateDirectory(Path); }
            public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
        }
    }
}
