using System;
using System.IO;
using IronOath.Campaign;
using IronOath.Combat;
using IronOath.Editor;
using IronOath.Presentation;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace IronOath.UnityTests
{
    public sealed class IronOathImportAndSaveTests
    {
        string _directory;
        IronOathSaveStore _store;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "IronOathAcceptance", Guid.NewGuid().ToString("N"));
            _store = new IronOathSaveStore(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [Test]
        public void ImportedSceneResourcesAndActorBindingsPassWithoutLeakingPreviewScenes()
        {
            int before = EditorSceneManager.previewSceneCount;
            CollectionAssert.IsEmpty(IronOathValidation.CollectErrors());
            Assert.AreEqual(before, EditorSceneManager.previewSceneCount);
            Assert.IsNotNull(Resources.Load<Shader>("IronOath/WorldUnlit"));
        }

        [Test]
        public void BoardFeetAreMirroredAndRemainVisibleAtSupportedAspects()
        {
            foreach (float aspect in new[] { 4f / 3, 16f / 9, 21f / 9 })
            {
                float halfHeight = BattleStageLayout.GetOrthographicSize(aspect);
                float halfWidth = halfHeight * aspect;
                for (int slot = 0; slot < 9; slot++)
                {
                    Vector3 left = BattleStageLayout.GetFootPoint(BattleSide.Allies, slot);
                    Vector3 right = BattleStageLayout.GetFootPoint(BattleSide.Enemies, slot);
                    Assert.AreEqual(-left.x, right.x, .0001f);
                    Assert.AreEqual(left.y, right.y, .0001f);
                    Assert.Less(Mathf.Abs(left.x) + BattleStageLayout.FigureWidth * .5f, halfWidth);
                    Assert.Less(Mathf.Abs(left.y) + BattleStageLayout.FigureHeight, halfHeight);
                }
                Assert.Less(Mathf.Abs(BattleStageLayout.GetFootPoint(BattleSide.Allies, 3).x),
                    Mathf.Abs(BattleStageLayout.GetFootPoint(BattleSide.Allies, 5).x));
            }
        }

        [Test]
        public void ActualJsonUtilityRoundTripPreservesCampaignFormationAndEveryTacticField()
        {
            var campaign = new CampaignSession(Protagonist.Chloe, 1701);
            BattleSimulation battle = BattleCatalog.CreateDemo(true);
            Assert.IsTrue(battle.MoveUnit(0, 8));
            Assert.IsTrue(battle.SetTactic(1, 0,
                new TacticRule(BattleSkillId.Backstab, TacticCondition.TargetArmored, TargetPriority.Backmost, false)));
            CapturePreferences(battle, out int[] formation, out TacticRule[][] tactics);
            Assert.IsTrue(_store.TrySave(campaign, formation, tactics, out string error), error);
            Assert.IsTrue(_store.TryLoad(out IronOathLoadedSave loaded, out error), error);
            Assert.AreEqual(Protagonist.Chloe, loaded.Campaign.Protagonist);
            Assert.AreEqual(Protagonist.Rayn, loaded.Campaign.Partner);
            Assert.AreEqual(campaign.Coins, loaded.Campaign.Coins);
            Assert.AreEqual(campaign.CurrentCustomer.Id, loaded.Campaign.CurrentCustomer.Id);
            CollectionAssert.AreEqual(formation, loaded.Formation);
            for (int unit = 0; unit < Formation.MaxUnitsPerSide; unit++)
                for (int row = 0; row < BattleUnit.TacticCount; row++)
                {
                    TacticRule expected = tactics[unit][row], actual = loaded.Tactics[unit][row];
                    Assert.AreEqual(expected.Skill, actual.Skill);
                    Assert.AreEqual(expected.Condition, actual.Condition);
                    Assert.AreEqual(expected.Priority, actual.Priority);
                    Assert.AreEqual(expected.Enabled, actual.Enabled);
                }
            Assert.IsFalse(loaded.RecoveredFromBackup);
            Assert.IsFalse(File.Exists(_store.MainPath + ".tmp"));
        }

        [Test]
        public void ActualJsonUtilityDoesNotResurrectAbsentCustomerOrPendingRewards()
        {
            var campaign = new CampaignSession(Protagonist.Rayn);
            while (campaign.CurrentCustomer != null) Assert.IsTrue(campaign.SkipCustomer().Succeeded);
            Assert.IsTrue(_store.TrySave(campaign, null, null, out string error), error);
            string json = File.ReadAllText(_store.MainPath);
            IronOathSaveData dto = JsonUtility.FromJson<IronOathSaveData>(json);
            Assert.IsFalse(dto.HasCustomer);
            Assert.IsFalse(dto.HasPendingOutcome);
            Assert.IsTrue(_store.TryLoad(out IronOathLoadedSave loaded, out error), error);
            Assert.IsNull(loaded.Campaign.CurrentCustomer);
            Assert.IsFalse(loaded.Campaign.HasPendingRewards);
            Assert.IsNull(loaded.Campaign.ActiveExpedition);
            Assert.IsNull(loaded.Formation);
            Assert.AreEqual(0, loaded.Campaign.RemainingCustomers);
        }

        [Test]
        public void PendingRewardTicketSurvivesActualUnitySerializationAndCannotPayTwice()
        {
            var original = new CampaignSession(Protagonist.Rayn);
            CampaignSave filled = original.CaptureSave();
            filled.Items = new InventoryItemSave[30];
            for (int i = 0; i < filled.Items.Length; i++)
                filled.Items[i] = new InventoryItemSave { Id = i + 1, Kind = ItemKind.MagicCore,
                    Condition = 100, Appraised = true, X = i % 6, Y = i / 6 };
            filled.NextItemId = 31;
            Assert.IsTrue(CampaignSession.TryRestore(filled, out CampaignSession campaign, out string error), error);
            Assert.IsTrue(campaign.TryEnterDungeonNode(out ExpeditionTicket ticket).Succeeded);
            var outcome = new ExpeditionOutcome { TicketId = ticket.Id, Victory = true, Coins = 50,
                Loot = new[] { new LootAward(ItemKind.IntactArmor) } };
            Assert.IsFalse(campaign.SettleExpedition(outcome).Succeeded);
            Assert.IsTrue(campaign.HasPendingRewards);
            Assert.IsTrue(_store.TrySave(campaign, null, null, out error), error);
            Assert.IsTrue(_store.TryLoad(out IronOathLoadedSave loaded, out error), error);
            Assert.IsTrue(loaded.Campaign.HasPendingRewards);
            Assert.AreEqual(ticket.Id, loaded.Campaign.ActiveExpedition.Id);
            int before = loaded.Campaign.Coins;
            Assert.IsFalse(loaded.Campaign.SettleExpedition(outcome).Succeeded);
            Assert.AreEqual(before, loaded.Campaign.Coins);
            Assert.IsTrue(loaded.Campaign.HasPendingRewards);
        }

        [Test]
        public void CorruptPrimaryRecoveryNeverRotatesCorruptionOverLastGoodBackup()
        {
            var campaign = new CampaignSession(Protagonist.Rayn);
            Assert.IsTrue(_store.TrySave(campaign, null, null, out string error), error);
            Assert.IsTrue(campaign.CloseDay().Succeeded);
            Assert.IsTrue(_store.TrySave(campaign, null, null, out error), error);
            string goodBackup = File.ReadAllText(_store.BackupPath);
            File.WriteAllText(_store.MainPath, "{broken json");
            Assert.IsTrue(_store.TryLoad(out IronOathLoadedSave recovered, out error), error);
            Assert.IsTrue(recovered.RecoveredFromBackup);
            Assert.AreEqual(1, recovered.Campaign.Day);
            Assert.IsTrue(_store.TrySave(recovered.Campaign, recovered.Formation, recovered.Tactics, out error), error);
            Assert.AreEqual(goodBackup, File.ReadAllText(_store.BackupPath));
            Assert.IsTrue(_store.TryLoad(out IronOathLoadedSave reloaded, out error), error);
            Assert.IsFalse(reloaded.RecoveredFromBackup);
            Assert.AreEqual(1, reloaded.Campaign.Day);
        }

        [TestCase("future version")]
        [TestCase("duplicate formation")]
        [TestCase("unowned skill")]
        [TestCase("duplicate tactic")]
        [TestCase("incomplete tactics")]
        public void InvalidWholeProfileIsRejectedWithoutPartiallyRestoringPreferences(string corruption)
        {
            var campaign = new CampaignSession(Protagonist.Rayn);
            CapturePreferences(BattleCatalog.CreateDemo(), out int[] formation, out TacticRule[][] tactics);
            Assert.IsTrue(_store.TrySave(campaign, formation, tactics, out string error), error);
            IronOathSaveData data = JsonUtility.FromJson<IronOathSaveData>(File.ReadAllText(_store.MainPath));
            switch (corruption)
            {
                case "future version": data.Version = 2; break;
                case "duplicate formation": data.Formation[1] = data.Formation[0]; break;
                case "unowned skill": data.Tactics[0].Skill = BattleSkillId.Firebomb; break;
                case "duplicate tactic": data.Tactics[1].Row = data.Tactics[0].Row; break;
                case "incomplete tactics": Array.Resize(ref data.Tactics, data.Tactics.Length - 1); break;
            }
            File.WriteAllText(_store.MainPath, JsonUtility.ToJson(data));
            Assert.IsFalse(_store.TryLoad(out IronOathLoadedSave loaded, out error));
            Assert.IsNull(loaded);
            Assert.IsNotEmpty(error);
        }

        static void CapturePreferences(BattleSimulation battle, out int[] formation, out TacticRule[][] tactics)
        {
            formation = new int[Formation.MaxUnitsPerSide];
            tactics = new TacticRule[Formation.MaxUnitsPerSide][];
            for (int unit = 0; unit < Formation.MaxUnitsPerSide; unit++)
            {
                BattleUnit actor = battle.GetUnit(unit);
                formation[unit] = actor.Slot;
                tactics[unit] = new TacticRule[BattleUnit.TacticCount];
                for (int row = 0; row < BattleUnit.TacticCount; row++) tactics[unit][row] = actor.GetTactic(row);
            }
        }
    }
}
