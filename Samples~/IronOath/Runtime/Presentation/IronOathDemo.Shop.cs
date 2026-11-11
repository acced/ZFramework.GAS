using IronOath.Campaign;
using UnityEngine;
using UnityEngine.UI;

namespace IronOath.Presentation
{
    public sealed partial class IronOathDemo
    {
        void BuildShop()
        {
            if (_campaign.HasPendingRewards)
            {
                IronOathUi.Button(_screen, "清点暂存战利品 · " + _campaign.PendingLootCount + " 件", 617, 459, 614, 59,
                    () => { _discardReward = -1; ShowPendingRewards(); }, true);
            }
            else if (_campaign.ActiveExpedition != null)
            {
                IronOathUi.Button(_screen, "继续未完成的远征", 617, 459, 614, 59,
                    () => { _page = Page.Dungeon; EnterDungeon(); }, true);
            }
            IronOathUi.Label(_screen, "黑石港 · 商人街", 635, 153, 570, 32, 20, IronOathUi.PaleGold, TextAnchor.MiddleCenter);
            IronOathUi.Label(_screen, "旧物有价，誓言无价。", 600, 198, 640, 64, 35, IronOathUi.Ivory, TextAnchor.MiddleCenter);
            IronOathUi.Label(_screen, "三个月前，他们走进深渊。\n今天，留在这里的我们继续营业。", 670, 277, 500, 82,
                21, new Color32(228, 220, 204, 255), TextAnchor.MiddleCenter);
            var customer = IronOathUi.Panel(_screen, "Pawn counter", 64, 153, 510, 684);
            IronOathUi.Label(customer, "柜 台 来 客", 32, 24, 440, 35, 24, IronOathUi.PaleGold);
            IronOathUi.Label(customer, "PAWN & APPRAISAL", 33, 64, 440, 23, 12, IronOathUi.Muted);
            IronOathUi.Rule(customer, 30, 105, 450);
            CustomerOffer offer = _campaign.CurrentCustomer;
            if (offer == null)
            {
                IronOathUi.Label(customer, "今日的客人都已离去", 35, 156, 440, 45, 28, IronOathUi.Ivory);
                IronOathUi.Label(customer, "可以整理货架、探索地下城，\n或打烊迎接新的一天。", 35, 225, 440, 120, 23, IronOathUi.Muted);
                IronOathUi.Button(customer, "前往深渊", 35, 526, 440, 58, () => ShowPage(Page.Dungeon), true);
            }
            else
            {
                IronOathUi.Label(customer, offer.Name, 35, 134, 440, 44, 30, IronOathUi.Ivory);
                IronOathUi.Label(customer, "“" + offer.Dialogue + "”", 35, 192, 440, 94, 22, IronOathUi.Muted);
                var item = IronOathUi.Panel(customer, "Offered item", 30, 307, 450, 135, new Color32(38, 46, 47, 255), false);
                IronOathUi.Label(item, offer.Item.Name, 22, 17, 405, 38, 26, IronOathUi.PaleGold);
                string appraisal = offer.IsAppraised
                    ? "实价 " + offer.FairValue + " G  ·  底价 " + offer.MinimumPrice + " G\n" +
                      "完好 " + offer.Condition + "%" + (offer.IsStolen ? "  ·  带有赃物印记" : "  ·  来源正常")
                    : "品相与来历尚未鉴定\n" + offer.Item.Width + " × " + offer.Item.Height + " 格 · " + "藏着什么秘密？";
                IronOathUi.Label(item, appraisal, 22, 64, 405, 65, 18, IronOathUi.Muted);
                IronOathUi.Label(customer, "开价", 35, 465, 160, 35, 21, IronOathUi.Muted);
                IronOathUi.Label(customer, offer.AskingPrice + " G", 240, 457, 230, 45, 32,
                    IronOathUi.PaleGold, TextAnchor.MiddleRight);
                IronOathUi.Button(customer, "鉴定物品", 35, 519, 210, 50, () => Apply(_campaign.AppraiseCurrentCustomer()), false, !offer.IsAppraised);
                IronOathUi.Button(customer, "讨价还价", 263, 519, 210, 50, () => Apply(_campaign.TryBargain()), false, !offer.BargainAttempted);
                IronOathUi.Button(customer, "收购 · " + offer.AskingPrice + " G", 35, 590, 285, 56,
                    () => Apply(_campaign.TryBuyCurrentOffer()), true);
                IronOathUi.Button(customer, "婉拒", 338, 590, 135, 56, () => Apply(_campaign.SkipCustomer()));
            }
            var newspaper = IronOathUi.Panel(_screen, "Blackstone Gazette", 611, 623, 626, 300,
                new Color32(219, 207, 176, 246));
            IronOathUi.Label(newspaper, "黑 石 港 周 报", 30, 22, 566, 37, 25, IronOathUi.Ink, TextAnchor.MiddleCenter, true);
            IronOathUi.Label(newspaper, "THE BLACKSTONE GAZETTE  ·  第 " + _campaign.Week + " 期", 25, 66, 576, 23,
                13, new Color32(103, 89, 63, 255), TextAnchor.MiddleCenter);
            IronOathUi.Rule(newspaper, 30, 104, 566);
            IronOathUi.Label(newspaper, _campaign.Newspaper.Headline, 35, 123, 556, 58, 24, IronOathUi.Ink, TextAnchor.MiddleCenter, true);
            IronOathUi.Label(newspaper, "护甲 " + _campaign.Newspaper.ArmorPercent + "%     魔核 " + _campaign.Newspaper.CorePercent +
                "%     遗物 " + _campaign.Newspaper.RelicPercent + "%", 30, 190, 566, 30, 19,
                new Color32(93, 66, 36, 255), TextAnchor.MiddleCenter);
            IronOathUi.Label(newspaper, _campaign.Newspaper.Rumor, 38, 234, 550, 47, 18,
                new Color32(94, 83, 65, 255), TextAnchor.MiddleCenter);
            BuildInventory();
            IronOathUi.Button(_screen, "打烊 · 下一天", 64, 866, 246, 57, () => Apply(_campaign.CloseDay()), true);
            IronOathUi.Button(_screen, "缴纳租金", 328, 866, 246, 57, () => Apply(_campaign.TryPayRent()));
        }

        void BuildInventory()
        {
            var panel = IronOathUi.Panel(_screen, "Shop inventory", 1285, 153, 570, 770);
            IronOathUi.Label(panel, "当 铺 货 架", 32, 24, 385, 36, 24, IronOathUi.PaleGold);
            IronOathUi.Label(panel, _campaign.Inventory.OccupiedCells + " / 30 格", 390, 29, 145, 28,
                18, IronOathUi.Muted, TextAnchor.MiddleRight);
            IronOathUi.Label(panel, "选中物品后，点空格移动；可旋转成衣。", 32, 73, 500, 27, 17, IronOathUi.Muted);
            const float left = 44, top = 122, cw = 80, ch = 65;
            for (int y = 0; y < GridInventory.Rows; y++)
                for (int x = 0; x < GridInventory.Columns; x++)
                {
                    int cellX = x, cellY = y;
                    var cell = IronOathUi.Rect(panel, "Bag " + x + "," + y, left + x * cw, top + y * ch, cw - 4, ch - 4);
                    var image = cell.gameObject.AddComponent<Image>();
                    image.color = new Color32(39, 47, 49, 240);
                    var button = cell.gameObject.AddComponent<Button>();
                    button.targetGraphic = image;
                    button.onClick.AddListener(() => MoveInventoryItem(cellX, cellY));
                }
            for (int i = 0; i < _campaign.Inventory.Count; i++)
            {
                InventoryEntry entry = _campaign.Inventory.GetAt(i);
                int id = entry.InstanceId;
                bool selected = id == _selectedItem;
                Color color = entry.Definition.IsFamilyRelic ? new Color32(102, 77, 48, 255)
                    : entry.Definition.Category == MarketCategory.Core ? new Color32(47, 85, 89, 255) : new Color32(66, 75, 83, 255);
                var item = IronOathUi.Panel(panel, entry.Definition.Name, left + entry.X * cw, top + entry.Y * ch,
                    entry.Width * cw - 4, entry.Height * ch - 4, selected ? color * 1.25f : color, false);
                var hit = item.gameObject.AddComponent<Image>();
                hit.color = Color.clear;
                var button = item.gameObject.AddComponent<Button>();
                button.targetGraphic = hit;
                button.onClick.AddListener(() => { _selectedItem = id; _rotateItem = entry.Rotated; ShowPage(Page.Shop); });
                string name = entry.Definition.Name;
                if (entry.Width == 1 && entry.Height == 1) name = name.Length > 2 ? name.Substring(name.Length - 2) : name;
                IronOathUi.Label(item, name, 7, 7, entry.Width * cw - 18, entry.Height * ch - 19,
                    entry.Width == 1 ? 16 : 21, selected ? IronOathUi.PaleGold : IronOathUi.Ivory,
                    TextAnchor.MiddleCenter);
            }
            InventoryEntry selectedItem = _campaign.Inventory.Find(_selectedItem);
            IronOathUi.Rule(panel, 32, 473, 506);
            if (selectedItem == null)
            {
                IronOathUi.Label(panel, "每一格，都有它的价值。", 35, 510, 500, 44, 25, IronOathUi.Ivory);
                IronOathUi.Label(panel, "魔核占 1×1 格，完好护甲占 2×3 格。\n保甲、破甲与背包空间，决定这趟是否值得。",
                    35, 571, 500, 83, 20, IronOathUi.Muted);
                IronOathUi.Button(panel, "扩建 · " + _campaign.UpgradeCost + " G  /  Lv." + _campaign.ShopLevel, 35, 687, 500, 49,
                    () => Apply(_campaign.TryUpgradeShop()));
                return;
            }
            IronOathUi.Label(panel, selectedItem.Definition.Name, 35, 495, 355, 38, 25, IronOathUi.PaleGold);
            IronOathUi.Button(panel, _rotateItem ? "竖 / 横" : "旋转", 418, 494, 118, 38,
                () => { _rotateItem = !_rotateItem; Toast("已切换方向，请点目标空格放置。", true); });
            string detail = "完好 " + selectedItem.Condition + "%  ·  售价 " + _campaign.GetSalePrice(selectedItem) + " G" +
                (selectedItem.IsStolen ? "  ·  赃物" : "") + (selectedItem.IsAppraised ? "" : "  ·  未鉴定");
            IronOathUi.Label(panel, detail, 35, 547, 500, 32, 18, IronOathUi.Muted);
            int selectedId = selectedItem.InstanceId;
            IronOathUi.Button(panel, "出售", 35, 600, 240, 48, () => Apply(_campaign.TrySellItem(selectedId)), true);
            IronOathUi.Button(panel, "鉴定", 294, 600, 240, 48, () => Apply(_campaign.TryAppraiseItem(selectedId)), false, !selectedItem.IsAppraised);
            IronOathUi.Button(panel, "修补 · " + _campaign.GetRepairCost(selectedItem) + " G", 35, 669, 240, 48,
                () => Apply(_campaign.TryRepairItem(selectedId)), false, selectedItem.Condition < 100);
            IronOathUi.Button(panel, "去除赃物印记", 294, 669, 240, 48,
                () => Apply(_campaign.TryRemoveStolenMark(selectedId)), false, selectedItem.IsStolen);
        }

        void MoveInventoryItem(int x, int y)
        {
            if (_selectedItem <= 0) { Toast("先选择要移动的物品。", false); return; }
            bool moved = _campaign.Inventory.TryMove(_selectedItem, x, y, _rotateItem);
            if (moved) { Save(); ShowPage(Page.Shop); }
            Toast(moved ? "货架已整理。" : "这里放不下；原位置保持不变。", moved);
        }
    }
}
