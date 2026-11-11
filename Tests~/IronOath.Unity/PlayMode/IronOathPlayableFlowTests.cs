using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using IronOath.Campaign;
using IronOath.Combat;
using IronOath.Presentation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace IronOath.UnityTests
{
    public sealed class IronOathPlayableFlowTests
    {
        GameObject _owner;
        IronOathDemo _demo;
        string _directory;

        [SetUp]
        public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "IronOathAcceptance", Guid.NewGuid().ToString("N"));

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_owner != null) Object.Destroy(_owner);
            yield return null;
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [UnityTest]
        public IEnumerator NewGamePracticeExpeditionSettlementAndReloadUseTheActualInterface()
        {
            CreateDemo();
            yield return null;
            Assert.IsNotNull(EventSystem.current);
            Assert.IsNotNull(EventSystem.current.currentInputModule, "The scene must have a working UI input module.");
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            Assert.AreEqual("UnityEngine.InputSystem.UI.InputSystemUIInputModule", EventSystem.current.currentInputModule.GetType().FullName);
#endif
            Click("以雷恩的视角开始");
            yield return null;
            CampaignSession campaign = Read<CampaignSession>("_campaign");
            Assert.AreEqual(Protagonist.Rayn, campaign.Protagonist);
            Assert.AreEqual(Protagonist.Chloe, campaign.Partner);
            Assert.AreEqual(CampaignSession.StartingCoins, campaign.Coins);
            Assert.IsTrue(File.Exists(Path.Combine(_directory, IronOathSaveStore.FileName)));
            Click("战前编排");
            yield return null;
            Click("演练当前阵容");
            yield return null;
            BattleStage practiceStage = Read<BattleStage>("_stage");
            Assert.IsNull(practiceStage.WorldRoot.GetComponentInParent<Canvas>());
            GameObject practiceWorld = practiceStage.WorldRoot.gameObject;
            Click("单步");
            yield return null;
            Assert.Greater(Read<BattleSimulation>("_battle").ActionNumber, 0);
            Click("返回编排");
            yield return null;
            Assert.IsTrue(practiceWorld == null);
            Assert.AreEqual(CampaignSession.StartingCoins, campaign.Coins);
            Assert.AreEqual(0, campaign.ExpeditionsToday);
            Click("带队探索");
            yield return null;
            Click("前往 · 港下旧栈道");
            yield return null;
            Assert.AreEqual(1, campaign.ExpeditionsToday);
            Assert.IsNotNull(campaign.ActiveExpedition);
            BattleSimulation battle = Read<BattleSimulation>("_battle");
            int guard = 0;
            while ((battle.State == BattleState.Formation || battle.State == BattleState.Fighting) && guard++ <= BattleSimulation.MaximumActions)
            {
                Click("单步");
                yield return null;
            }
            Assert.AreEqual(BattleState.Victory, battle.State);
            yield return new WaitForSecondsRealtime(.8f);
            Click("清点战利品 · 返回当铺");
            yield return null;
            Assert.IsNull(campaign.ActiveExpedition);
            Assert.AreEqual(1, campaign.DungeonNodeIndex);
            Assert.AreEqual(CampaignSession.StartingCoins + 50, campaign.Coins);
            int itemCount = campaign.Inventory.Count;
            Assert.Greater(itemCount, 2);
            Object.Destroy(_owner);
            yield return null;
            CreateDemo();
            yield return null;
            CampaignSession restored = Read<CampaignSession>("_campaign");
            Assert.IsNotNull(restored);
            Assert.AreEqual(campaign.Coins, restored.Coins);
            Assert.AreEqual(itemCount, restored.Inventory.Count);
            Assert.AreEqual(1, restored.DungeonNodeIndex);
            Assert.AreEqual(1, restored.ExpeditionsToday);
            Assert.IsNull(restored.ActiveExpedition);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator FullBagCanConfirmDiscardThenClaimCoinsThroughTheRecoveryInterface()
        {
            CampaignSave snapshot = new CampaignSession(Protagonist.Rayn).CaptureSave();
            snapshot.Items = new InventoryItemSave[30];
            for (int i = 0; i < snapshot.Items.Length; i++)
                snapshot.Items[i] = new InventoryItemSave { Id = i + 1, Kind = ItemKind.MagicCore,
                    Condition = 100, Appraised = true, X = i % 6, Y = i / 6 };
            snapshot.NextItemId = 31;
            Assert.IsTrue(CampaignSession.TryRestore(snapshot, out CampaignSession campaign, out string error), error);
            Assert.IsTrue(campaign.TryEnterDungeonNode(out ExpeditionTicket ticket).Succeeded);
            Assert.IsFalse(campaign.SettleExpedition(new ExpeditionOutcome { TicketId = ticket.Id,
                Victory = true, Coins = 50, Loot = new[] { new LootAward(ItemKind.IntactArmor) } }).Succeeded);
            var store = new IronOathSaveStore(_directory);
            Assert.IsTrue(store.TrySave(campaign, null, null, out error), error);
            CreateDemo();
            yield return null;
            campaign = Read<CampaignSession>("_campaign");
            Click("清点暂存战利品 · 1 件");
            yield return null;
            Click("舍弃");
            yield return null;
            Assert.AreEqual(1, campaign.PendingLootCount, "The first click requests confirmation and cannot destroy a reward.");
            Click("确认舍弃");
            yield return null;
            Assert.AreEqual(0, campaign.PendingLootCount);
            Assert.AreEqual(CampaignSession.StartingCoins, campaign.Coins);
            Click("领取保留物品与金币");
            yield return null;
            Assert.AreEqual(CampaignSession.StartingCoins + 50, campaign.Coins);
            Assert.AreEqual(30, campaign.Inventory.Count);
            Assert.AreEqual(1, campaign.DungeonNodeIndex);
            Assert.IsFalse(campaign.HasPendingRewards);
            Assert.IsNull(campaign.ActiveExpedition);
            Assert.IsTrue(store.TryLoad(out IronOathLoadedSave loaded, out error), error);
            Assert.AreEqual(campaign.Coins, loaded.Campaign.Coins);
            Assert.IsFalse(loaded.Campaign.TryClaimExpeditionRewards().Succeeded);
            Assert.AreEqual(campaign.Coins, loaded.Campaign.Coins);
        }

        void CreateDemo()
        {
            _owner = new GameObject("Iron Oath acceptance demo");
            _owner.SetActive(false);
            _demo = _owner.AddComponent<IronOathDemo>();
            _demo.SaveDirectoryOverride = _directory;
            _owner.SetActive(true);
        }

        T Read<T>(string name)
        {
            FieldInfo field = typeof(IronOathDemo).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, name);
            return (T)field.GetValue(_demo);
        }

        void Click(string label)
        {
            Button found = null;
            foreach (Button button in _demo.GetComponentsInChildren<Button>())
            {
                Text text = button.GetComponentInChildren<Text>();
                if (text == null || text.text != label) continue;
                Assert.IsNull(found, "Ambiguous visible button label: " + label);
                found = button;
            }
            Assert.IsNotNull(found, "Missing visible button: " + label);
            Assert.IsTrue(found.interactable, label);
            Canvas.ForceUpdateCanvases();
            var rect = (RectTransform)found.transform;
            Vector2 point = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
            var pointer = new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Left };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, hits);
            Assert.IsNotEmpty(hits, "Button is not reachable by the UI raycaster: " + label);
            Assert.IsTrue(hits[0].gameObject.transform == found.transform || hits[0].gameObject.transform.IsChildOf(found.transform),
                "Another UI element is covering: " + label);
            ExecuteEvents.Execute(found.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        }
    }
}
