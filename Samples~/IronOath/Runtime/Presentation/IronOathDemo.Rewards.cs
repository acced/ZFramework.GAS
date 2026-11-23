using IronOath.Campaign;
using UnityEngine;
using UnityEngine.UI;

namespace IronOath.Presentation
{
    public sealed partial class IronOathDemo
    {
        RectTransform _rewardDialog;
        int _rewardPage, _discardReward = -1;
        const int RewardsPerPage = 6;

        void ShowPendingRewards()
        {
            CloseRewardDialog();
            if (!_campaign.HasPendingRewards) return;
            _rewardDialog = IronOathUi.Rect(_screen, "Pending expedition rewards", 0, 0, 1920, 1080);
            var blocker = IronOathUi.Fill(_rewardDialog, "Reward input shield", 0, 0, 1920, 1080, new Color(0, 0, 0, .65f));
            blocker.raycastTarget = true;
            var panel = IronOathUi.Panel(_rewardDialog, "Recovery ledger", 385, 140, 1150, 790);
            IronOathUi.Label(panel, "清 点 战 利 品", 40, 26, 1070, 48, 32, IronOathUi.PaleGold);
            IronOathUi.Label(panel, "待结算金币 " + _campaign.PendingRewardCoins + " G    ·    货架 " +
                _campaign.Inventory.OccupiedCells + " / 30 格", 42, 88, 1066, 30, 21, IronOathUi.Ivory);
            IronOathUi.Label(panel, "可以回店整理，或舍弃不带走的物品；领取后结算金币并完成本次远征。",
                42, 130, 1066, 40, 19, IronOathUi.Muted);
            int pageCount = Mathf.Max(1, (_campaign.PendingLootCount + RewardsPerPage - 1) / RewardsPerPage);
            _rewardPage = Mathf.Clamp(_rewardPage, 0, pageCount - 1);
            for (int row = 0; row < RewardsPerPage; row++)
            {
                int index = _rewardPage * RewardsPerPage + row;
                LootAward award = _campaign.GetPendingLoot(index);
                if (award == null) break;
                ItemDefinition item = ItemCatalog.Get(award.Kind);
                float y = 190 + row * 70;
                var entry = IronOathUi.Panel(panel, item.Name, 40, y, 1070, 60, IronOathUi.Ink, false);
                IronOathUi.Label(entry, item.Width + "×" + item.Height, 15, 11, 70, 36, 22, IronOathUi.Gold, TextAnchor.MiddleCenter);
                IronOathUi.Label(entry, item.Name, 105, 11, 330, 36, 23,
                    item.IsFamilyRelic ? IronOathUi.PaleGold : IronOathUi.Ivory);
                string detail = "完好 " + award.Condition + "%" + (award.Stolen ? " · 赃物" : "") +
                    (item.IsFamilyRelic ? " · 家族线索" : "");
                IronOathUi.Label(entry, detail, 445, 15, 390, 32, 18, IronOathUi.Muted);
                bool confirming = _discardReward == index;
                IronOathUi.Button(entry, confirming ? "确认舍弃" : "舍弃", 868, 9, 185, 42, () =>
                {
                    if (!confirming) { _discardReward = index; ShowPendingRewards(); return; }
                    CampaignResult result = _campaign.DiscardPendingLoot(index);
                    bool saved = Save();
                    _discardReward = -1;
                    ShowPendingRewards();
                    Toast(result.Message + (saved ? "" : "（尚未保存）"), result.Succeeded);
                }, confirming);
            }
            if (_campaign.PendingLootCount == 0)
                IronOathUi.Label(panel, "没有需要带走的物品。仍可领取金币，完成这次远征。", 65, 314, 1020, 80,
                    26, IronOathUi.Ivory, TextAnchor.MiddleCenter);
            IronOathUi.Button(panel, "上一页", 40, 624, 140, 42,
                () => { _rewardPage--; _discardReward = -1; ShowPendingRewards(); }, false, _rewardPage > 0);
            IronOathUi.Label(panel, (_rewardPage + 1) + " / " + pageCount + "    ·    " + _campaign.PendingLootCount + " 件保留",
                210, 629, 650, 32, 18, IronOathUi.Muted, TextAnchor.MiddleCenter);
            IronOathUi.Button(panel, "下一页", 970, 624, 140, 42,
                () => { _rewardPage++; _discardReward = -1; ShowPendingRewards(); }, false, _rewardPage + 1 < pageCount);
            IronOathUi.Rule(panel, 40, 685, 1070);
            IronOathUi.Button(panel, "返回整理货架", 40, 710, 460, 54,
                () => { _discardReward = -1; CloseRewardDialog(); });
            IronOathUi.Button(panel, "领取保留物品与金币", 535, 710, 575, 54, ClaimPendingRewards, true);
        }

        void ClaimPendingRewards()
        {
            CampaignResult result = _campaign.TryClaimExpeditionRewards();
            bool saved = Save();
            _discardReward = -1;
            ShowPage(Page.Shop);
            if (_campaign.HasPendingRewards) ShowPendingRewards();
            Toast(result.Message + (saved ? "" : "（尚未保存）"), result.Succeeded);
        }

        void CloseRewardDialog()
        {
            if (_rewardDialog == null) return;
            _rewardDialog.gameObject.SetActive(false);
            Destroy(_rewardDialog.gameObject);
            _rewardDialog = null;
        }
    }
}
