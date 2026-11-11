using System;

namespace IronOath.Campaign
{
    /// <summary>Owns shop and expedition progression. Battle rules remain in the combat module.</summary>
    public sealed class CampaignSession
    {
        public const int StartingCoins = 240;
        public const int BaseRent = 320;
        public const int SaveVersion = 1;
        private const int MaxCoins = 1000000;
        private static readonly string[] DayNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        private int _randomState;
        private int _nextItemId = 1;
        private int _nextTicketId = 1;
        private int _lastSettledTicketId;
        private int _paidRentWeek;
        private int _customerIndex;
        private int _clueMask;
        private CustomerOffer _customer;
        private ExpeditionOutcome _pendingOutcome;

        public Protagonist Protagonist { get; private set; }
        public Protagonist Partner => Protagonist == Protagonist.Rayn ? Protagonist.Chloe : Protagonist.Rayn;
        public string ProtagonistName => Protagonist == Protagonist.Rayn ? "雷恩" : "克萝伊";
        public string PartnerName => Partner == Protagonist.Rayn ? "雷恩" : "克萝伊";
        public int Coins { get; private set; }
        public int Day { get; private set; } = 1;
        public string DayName => DayNames[(Day - 1) % 7];
        public int Week => (Day - 1) / 7 + 1;
        public int RentAmount => BaseRent + ShopLevel * 60;
        public bool RentDue => Day % 7 == 0 && _paidRentWeek < Week;
        public int DaysUntilRent => 6 - (Day - 1) % 7;
        public int ShopLevel { get; private set; }
        public int ExpeditionsToday { get; private set; }
        public bool CanExpeditionToday => State != CampaignState.Evicted && ExpeditionsToday == 0 && ActiveExpedition == null;
        public int UpgradeCost => 180 * (ShopLevel + 1);
        public CampaignState State { get; private set; }
        public GridInventory Inventory { get; private set; } = new GridInventory();
        public WeeklyNewspaper Newspaper { get; private set; }
        public CustomerOffer CurrentCustomer => _customer;
        public int RemainingCustomers => Math.Max(0, 3 + ShopLevel - _customerIndex);
        public int DungeonNodeIndex { get; private set; }
        public DungeonNode CurrentNode => DungeonCatalog.Get(DungeonNodeIndex);
        public int ClueCount => ((_clueMask & 1) != 0 ? 1 : 0) + ((_clueMask & 2) != 0 ? 1 : 0) + ((_clueMask & 4) != 0 ? 1 : 0);
        public int UnlockedDepth => HasClue(2) ? 3 : HasClue(1) ? 2 : 1;
        public ExpeditionTicket ActiveExpedition { get; private set; }
        public bool HasPendingRewards => _pendingOutcome != null;
        public int PendingLootCount => _pendingOutcome?.Loot.Length ?? 0;
        public int PendingRewardCoins => _pendingOutcome?.Coins ?? 0;

        public CampaignSession(Protagonist protagonist, int seed = 1701)
        {
            if (protagonist != Protagonist.Rayn && protagonist != Protagonist.Chloe) throw new ArgumentOutOfRangeException(nameof(protagonist));
            Protagonist = protagonist; Coins = StartingCoins; _randomState = seed == 0 ? 1701 : seed;
            Newspaper = new WeeklyNewspaper(1);
            Inventory.TryAdd(new InventoryEntry(_nextItemId++, ItemKind.DamagedArmor, 65, appraised: true));
            Inventory.TryAdd(new InventoryEntry(_nextItemId++, ItemKind.AlchemyVial, appraised: true));
            CreateCustomer();
        }

        private CampaignSession() { }

        public bool HasClue(int clueId) => clueId >= 1 && clueId <= 3 && (_clueMask & 1 << (clueId - 1)) != 0;

        public CampaignResult AppraiseCurrentCustomer()
        {
            CampaignResult check = CheckCustomer(); if (!check.Succeeded) return check;
            if (_customer.IsAppraised) return CampaignResult.Fail("已经鉴定过这件典当品。");
            int cost = Protagonist == Protagonist.Chloe ? 0 : 5;
            if (Coins < cost) return CampaignResult.Fail("鉴定需要 5 金币。");
            Coins -= cost; _customer.IsAppraised = true;
            return CampaignResult.Ok(_customer.Item.IsFamilyRelic ? "发现铁誓探险队的印记！购入遗物后才能留下线索。" : "真价与品相已确认，可以决定是否收购。");
        }

        public CampaignResult TryBargain()
        {
            CampaignResult check = CheckCustomer(); if (!check.Succeeded) return check;
            if (_customer.BargainAttempted) return CampaignResult.Fail("这位客人已经还过一次价。");
            _customer.BargainAttempted = true;
            bool success = NextRandom(100) < (Protagonist == Protagonist.Chloe ? 80 : 55);
            if (!success) return CampaignResult.Ok("客人摇了摇头，坚持原来的报价。");
            int old = _customer.AskingPrice;
            _customer.AskingPrice = Math.Max(_customer.MinimumPrice, old * (Protagonist == Protagonist.Chloe ? 80 : 88) / 100);
            return CampaignResult.Ok("谈妥了！收购价减少 " + (old - _customer.AskingPrice) + " 金币。");
        }

        public CampaignResult TryBuyCurrentOffer()
        {
            CampaignResult check = CheckCustomer(); if (!check.Succeeded) return check;
            if (Coins < _customer.AskingPrice) return CampaignResult.Fail("零钱不足，先出售库存或放弃这笔交易。");
            var item = new InventoryEntry(_nextItemId, _customer.Item.Kind, _customer.Condition, _customer.IsStolen, _customer.IsAppraised);
            if (!Inventory.TryAdd(item)) return CampaignResult.Fail("背包放不下这件物品。先出售或调整库存。");
            _nextItemId++; Coins -= _customer.AskingPrice;
            DiscoverOwnedRelic(item);
            string name = item.Definition.Name;
            _customerIndex++; CreateCustomer();
            return CampaignResult.Ok("已收购「" + name + "」。");
        }

        public CampaignResult SkipCustomer()
        {
            CampaignResult check = CheckCustomer(); if (!check.Succeeded) return check;
            _customerIndex++; CreateCustomer();
            return CampaignResult.Ok(_customer == null ? "今日来客已接待完毕。" : "送走客人，下一笔生意到了。");
        }

        public int GetSalePrice(InventoryEntry item)
        {
            if (item == null) return 0;
            int price = FairValue(item.Definition, item.Condition);
            if (item.IsStolen) price = price * 55 / 100;
            return Math.Max(1, price * (100 + ShopLevel * 5) / 100);
        }

        public int GetRepairCost(InventoryEntry item)
        {
            if (item == null || item.Definition.Category != MarketCategory.Armor || item.Condition == 100) return 0;
            return Math.Max(1, (100 - item.Condition) * item.Definition.BaseValue / 400);
        }

        public CampaignResult TrySellItem(int instanceId)
        {
            CampaignResult check = CheckTrading(); if (!check.Succeeded) return check;
            InventoryEntry item = Inventory.Find(instanceId);
            if (item == null) return CampaignResult.Fail("物品已不在背包中。");
            if (item.Definition.IsFamilyRelic && !item.IsAppraised) return CampaignResult.Fail("这件遗物还未鉴定，先看看是否有父母的线索。");
            int value = GetSalePrice(item);
            if (Coins > MaxCoins - value) return CampaignResult.Fail("钱柜已满。");
            Inventory.TryRemove(instanceId, out _); Coins += value;
            return CampaignResult.Ok("售出「" + item.Definition.Name + "」，获得 " + value + " 金币。");
        }

        public CampaignResult TryRepairItem(int instanceId)
        {
            CampaignResult check = CheckTrading(); if (!check.Succeeded) return check;
            InventoryEntry item = Inventory.Find(instanceId);
            int cost = GetRepairCost(item);
            if (cost == 0) return CampaignResult.Fail("这件物品无需修补。");
            if (Coins < cost) return CampaignResult.Fail("修补需要 " + cost + " 金币。");
            Coins -= cost; item.Condition = 100;
            return CampaignResult.Ok("护甲已翻新，品相恢复至 100%。");
        }

        public CampaignResult TryRemoveStolenMark(int instanceId)
        {
            CampaignResult check = CheckTrading(); if (!check.Succeeded) return check;
            InventoryEntry item = Inventory.Find(instanceId);
            if (item == null || !item.IsStolen) return CampaignResult.Fail("物品没有需要处理的赃物印记。");
            int cost = Protagonist == Protagonist.Rayn ? 6 : 12;
            if (Coins < cost) return CampaignResult.Fail("处理印记需要 " + cost + " 金币。");
            Coins -= cost; item.IsStolen = false;
            return CampaignResult.Ok("旧印记已清理，可以按正常行情出售。");
        }

        public CampaignResult TryAppraiseItem(int instanceId)
        {
            CampaignResult check = CheckTrading(); if (!check.Succeeded) return check;
            InventoryEntry item = Inventory.Find(instanceId);
            if (item == null || item.IsAppraised) return CampaignResult.Fail("物品不存在，或已经鉴定。");
            int cost = Protagonist == Protagonist.Chloe ? 0 : 5;
            if (Coins < cost) return CampaignResult.Fail("鉴定需要 5 金币。");
            Coins -= cost; item.IsAppraised = true; DiscoverOwnedRelic(item);
            return CampaignResult.Ok(item.Definition.IsFamilyRelic ? item.Definition.Description : "鉴定完成，品相与来历已记录。");
        }

        public CampaignResult TryUpgradeShop()
        {
            CampaignResult check = CheckTrading(); if (!check.Succeeded) return check;
            if (ShopLevel >= 2) return CampaignResult.Fail("本章的铺面已经扩建完毕。");
            if (RentDue) return CampaignResult.Fail("先缴清本周租金，再讨论扩建。");
            if (Coins < UpgradeCost) return CampaignResult.Fail("扩建需要 " + UpgradeCost + " 金币。");
            Coins -= UpgradeCost; ShopLevel++;
            if (_customer == null) CreateCustomer();
            return CampaignResult.Ok("扩建完成：每日多一位客人，售价提高 5%，周租增加 60 金币。");
        }

        public CampaignResult TryPayRent()
        {
            if (State == CampaignState.Evicted) return CampaignResult.Fail("当铺已经被收回。请开始新的一轮经营。");
            if (!RentDue) return CampaignResult.Fail("今天没有未缴的租金。");
            if (Coins < RentAmount) return CampaignResult.Fail("周租还差 " + (RentAmount - Coins) + " 金币，关店前设法补齐。");
            Coins -= RentAmount; _paidRentWeek = Week;
            return CampaignResult.Ok("本周租金已缴清，铁誓当铺又守住了一周。");
        }

        public CampaignResult CloseDay()
        {
            if (State == CampaignState.Evicted) return CampaignResult.Fail("当铺已经被收回。");
            if (ActiveExpedition != null) return CampaignResult.Fail("先结束远征并领取战利品，再关店休息。");
            if (RentDue)
            {
                if (Coins < RentAmount)
                {
                    State = CampaignState.Evicted;
                    return CampaignResult.Fail("周日打烊，租金不足。商业行会收回铺面，本轮经营结束。");
                }
                Coins -= RentAmount; _paidRentWeek = Week;
            }
            if (Day >= 9999) return CampaignResult.Fail("这份存档已经到达日历上限。");
            int previousWeek = Week; Day++; _customerIndex = 0; ExpeditionsToday = 0;
            if (Week != previousWeek) Newspaper = new WeeklyNewspaper(Week);
            CreateCustomer();
            return CampaignResult.Ok("新的一天开始了。今天是第 " + Week + " 周，" + DayName + "。");
        }

        public CampaignResult TryEnterDungeonNode(out ExpeditionTicket ticket)
        {
            ticket = null;
            if (State == CampaignState.Evicted) return CampaignResult.Fail("先开始新的一轮冒险。");
            if (ActiveExpedition != null) return CampaignResult.Fail(HasPendingRewards ? "上一场的战利品尚未领走。" : "当前远征还未结束。");
            DungeonNode node = CurrentNode;
            if (node == null) return CampaignResult.Fail("本章探索完成。父母的线索将通往下一章。");
            if (node.RequiredClueCount > ClueCount || node.Depth > UnlockedDepth)
                return CampaignResult.Fail("还缺少通往这一层的父母线索。");
            if (node.Kind == DungeonNodeKind.Battle)
            {
                if (ExpeditionsToday != 0) return CampaignResult.Fail("今天已经远征过一次。先关店休息，明天再出发。");
                ExpeditionsToday = 1;
                ActiveExpedition = new ExpeditionTicket(_nextTicketId++, node);
                ticket = ActiveExpedition;
                return CampaignResult.Ok("进入「" + node.Name + "」。先编排阵型与战术，再开始战斗。");
            }
            if (node.Kind == DungeonNodeKind.Cache)
            {
                int reward = 25 + node.Depth * 15;
                Coins = Math.Min(MaxCoins, Coins + reward);
            }
            else if (node.Kind == DungeonNodeKind.Clue)
            {
                ItemKind kind = node.Depth == 1 ? ItemKind.FathersSignet : node.Depth == 2 ? ItemKind.MothersLens : ItemKind.ChurchDispatch;
                if (!HasClue(node.Depth))
                {
                    var relic = new InventoryEntry(_nextItemId, kind, appraised: true);
                    if (!Inventory.TryAdd(relic)) return CampaignResult.Fail("背包已满，腾出空间才能带走父母的遗物。");
                    _nextItemId++; DiscoverOwnedRelic(relic);
                }
            }
            else if (node.Kind == DungeonNodeKind.Camp)
            {
                for (int i = 0; i < Inventory.Count; i++)
                {
                    InventoryEntry item = Inventory.GetAt(i);
                    if (item.Definition.Category == MarketCategory.Armor) item.Condition = Math.Min(100, item.Condition + 10);
                }
            }
            DungeonNodeIndex++; MarkChapterCompletion();
            return CampaignResult.Ok(node.Kind == DungeonNodeKind.Cache ? "发现遗落的钱袋，获得 " + (25 + node.Depth * 15) + " 金币。" : node.Description);
        }

        public CampaignResult SettleExpedition(ExpeditionOutcome outcome)
        {
            if (outcome == null) return CampaignResult.Fail("缺少远征结算。");
            if (outcome.TicketId <= _lastSettledTicketId) return CampaignResult.Fail("这次远征已经结算过，不能重复领取。");
            if (ActiveExpedition == null || outcome.TicketId != ActiveExpedition.Id) return CampaignResult.Fail("结算与当前远征不匹配。");
            if (!ValidOutcome(outcome)) return CampaignResult.Fail("战利品数据无效。");
            if (_pendingOutcome == null) _pendingOutcome = CloneOutcome(outcome);
            // Once recorded, the first result is authoritative; changing a retry cannot alter rewards.
            return TryClaimExpeditionRewards();
        }

        public CampaignResult TryClaimExpeditionRewards()
        {
            if (_pendingOutcome == null || ActiveExpedition == null) return CampaignResult.Fail("没有待领取的战利品。");
            ExpeditionOutcome outcome = _pendingOutcome;
            if (!outcome.Victory)
            {
                Coins -= Math.Min(Coins, 15 + ActiveExpedition.Depth * 5);
                _lastSettledTicketId = ActiveExpedition.Id; ActiveExpedition = null; _pendingOutcome = null;
                return CampaignResult.Ok("伙伴带你撤回当铺，支付了救援费用。调整阵型后可再次挑战。");
            }
            if (!Inventory.CanFit(outcome.Loot)) return CampaignResult.Fail("战利品暂存中。先出售或整理库存，再领取整批奖励。");
            for (int i = 0; i < outcome.Loot.Length; i++)
            {
                LootAward reward = outcome.Loot[i];
                ItemDefinition definition = ItemCatalog.Get(reward.Kind);
                int condition = reward.Condition;
                if (Protagonist == Protagonist.Rayn && definition.Category == MarketCategory.Armor) condition = Math.Min(100, condition + 20);
                var item = new InventoryEntry(_nextItemId++, reward.Kind, condition, reward.Stolen, Protagonist == Protagonist.Chloe);
                Inventory.TryAdd(item); DiscoverOwnedRelic(item);
            }
            Coins = Math.Min(MaxCoins, Coins + outcome.Coins);
            _lastSettledTicketId = ActiveExpedition.Id; ActiveExpedition = null; _pendingOutcome = null;
            DungeonNodeIndex++; MarkChapterCompletion();
            return CampaignResult.Ok("远征胜利！获得 " + outcome.Coins + " 金币与 " + outcome.Loot.Length + " 件战利品。" + (Protagonist == Protagonist.Rayn ? "雷恩已免费修补战利护甲的 20% 品相。" : "克萝伊已鉴定全部战利品。"));
        }

        /// <summary>Returns a copy so inspecting a pending award cannot change the recorded battle result.</summary>
        public LootAward GetPendingLoot(int index)
        {
            if (_pendingOutcome == null || (uint)index >= (uint)_pendingOutcome.Loot.Length) return null;
            LootAward item = _pendingOutcome.Loot[index];
            return new LootAward(item.Kind, item.Condition, item.Stolen);
        }

        /// <summary>
        /// Removes only the selected unclaimed item. The ticket and coins remain pending until an explicit claim,
        /// including when no items remain. Discarded relics never become owned and therefore reveal no clue.
        /// </summary>
        public CampaignResult DiscardPendingLoot(int index)
        {
            if (_pendingOutcome == null || ActiveExpedition == null || !_pendingOutcome.Victory)
                return CampaignResult.Fail("没有待清点的战利品。");
            LootAward[] loot = _pendingOutcome.Loot;
            if ((uint)index >= (uint)loot.Length) return CampaignResult.Fail("这件战利品已不在待领列表中。");
            LootAward removed = loot[index];
            var remaining = new LootAward[loot.Length - 1];
            Array.Copy(loot, 0, remaining, 0, index);
            Array.Copy(loot, index + 1, remaining, index, loot.Length - index - 1);
            _pendingOutcome.Loot = remaining;
            return CampaignResult.Ok("已放弃「" + ItemCatalog.Get(removed.Kind).Name + "」。金币与保留物品仍待领取。");
        }

        public CampaignResult AbandonExpedition()
        {
            if (ActiveExpedition == null) return CampaignResult.Fail("当前没有进行中的远征。");
            if (HasPendingRewards) return CampaignResult.Fail("已完成的远征仍有战利品待领。");
            return SettleExpedition(new ExpeditionOutcome { TicketId = ActiveExpedition.Id, Victory = false });
        }

        private void MarkChapterCompletion()
        {
            if (DungeonNodeIndex == DungeonCatalog.Count) State = CampaignState.Completed;
        }
        private void DiscoverOwnedRelic(InventoryEntry item)
        {
            if (item.IsAppraised && item.Definition.ClueId > 0) _clueMask |= 1 << (item.Definition.ClueId - 1);
        }
        private CampaignResult CheckTrading()
        {
            if (State == CampaignState.Evicted) return CampaignResult.Fail("当铺已被收回，无法继续交易。");
            if (ActiveExpedition != null && !HasPendingRewards) return CampaignResult.Fail("正在远征，返回当铺后才能交易。");
            return CampaignResult.Ok(string.Empty);
        }
        private CampaignResult CheckCustomer()
        {
            CampaignResult check = CheckTrading();
            return !check.Succeeded ? check : _customer == null ? CampaignResult.Fail("今天的客人已经接待完了。") : check;
        }
        private int FairValue(ItemDefinition item, int condition) => Math.Max(1, item.BaseValue * condition * Newspaper.PercentFor(item.Category) / 10000);
        private void CreateCustomer()
        {
            if (_customerIndex >= 3 + ShopLevel) { _customer = null; return; }
            ItemKind kind;
            bool stolen;
            int condition;
            // The first day's last customer establishes the family mystery; subsequent stock rotates daily.
            if (Day == 1 && _customerIndex == 2) { kind = ItemKind.FathersSignet; stolen = true; condition = 100; }
            else
            {
                int cycle = (Day - 1 + _customerIndex) % 4;
                kind = cycle == 0 ? ItemKind.DamagedArmor : cycle == 1 ? ItemKind.MagicCore : cycle == 2 ? ItemKind.AlchemyVial : ItemKind.IntactArmor;
                stolen = cycle == 3;
                condition = kind == ItemKind.DamagedArmor ? 50 + Day % 4 * 10 : kind == ItemKind.IntactArmor ? 85 : 100;
            }
            ItemDefinition item = ItemCatalog.Get(kind);
            int fair = FairValue(item, condition);
            int minimum = Math.Max(1, fair * (stolen ? 35 : 50) / 100);
            _customer = new CustomerOffer {
                Id = Day * 10 + _customerIndex,
                Name = item.IsFamilyRelic ? "披斗篷的陌生人" : stolen ? "午夜搬运工" : _customerIndex % 2 == 0 ? "受伤的佣兵" : "炼金学徒",
                Dialogue = item.IsFamilyRelic ? "只是枚旧戒指。别问来路，今晚我就离港。" : stolen ? "这件甲没有主人。价钱好商量，别问太多。" : "小掌柜，给个实在价吧，我还要赶路。",
                Item = item, Condition = condition, IsStolen = stolen, FairValue = fair,
                AskingPrice = Math.Max(minimum, fair * (stolen ? 55 : 78) / 100), MinimumPrice = minimum,
                IsAppraised = Protagonist == Protagonist.Chloe
            };
        }
        private int NextRandom(int maximum)
        {
            uint value = unchecked((uint)_randomState);
            value ^= value << 13; value ^= value >> 17; value ^= value << 5;
            _randomState = unchecked((int)value);
            return (int)(value % (uint)maximum);
        }
        private static bool ValidOutcome(ExpeditionOutcome outcome)
        {
            if (outcome == null || outcome.TicketId <= 0 || outcome.Coins < 0 || outcome.Coins > 10000 || outcome.Loot == null || outcome.Loot.Length > 18) return false;
            if (!outcome.Victory && (outcome.Coins != 0 || outcome.Loot.Length != 0)) return false;
            for (int i = 0; i < outcome.Loot.Length; i++)
            {
                LootAward item = outcome.Loot[i];
                if (item == null || ItemCatalog.Get(item.Kind) == null || item.Condition < 1 || item.Condition > 100) return false;
            }
            return true;
        }
        private static ExpeditionOutcome CloneOutcome(ExpeditionOutcome outcome)
        {
            var clone = new ExpeditionOutcome { TicketId = outcome.TicketId, Victory = outcome.Victory, Coins = outcome.Coins, Loot = new LootAward[outcome.Loot.Length] };
            for (int i = 0; i < clone.Loot.Length; i++) clone.Loot[i] = new LootAward(outcome.Loot[i].Kind, outcome.Loot[i].Condition, outcome.Loot[i].Stolen);
            return clone;
        }

        public CampaignSave CaptureSave()
        {
            var save = new CampaignSave {
                Version = SaveVersion, Protagonist = Protagonist, Coins = Coins, Day = Day, ShopLevel = ShopLevel, State = State, ExpeditionsToday = ExpeditionsToday,
                RandomState = _randomState, NextItemId = _nextItemId, NextTicketId = _nextTicketId, LastSettledTicketId = _lastSettledTicketId,
                PaidRentWeek = _paidRentWeek, CustomerIndex = _customerIndex, ClueMask = _clueMask, NodeIndex = DungeonNodeIndex,
                ActiveTicketId = ActiveExpedition?.Id ?? 0, ActiveNodeIndex = ActiveExpedition?.NodeIndex ?? -1,
                PendingOutcome = _pendingOutcome == null ? null : CloneOutcome(_pendingOutcome), Items = new InventoryItemSave[Inventory.Count]
            };
            for (int i = 0; i < Inventory.Count; i++)
            {
                InventoryEntry item = Inventory.GetAt(i);
                save.Items[i] = new InventoryItemSave { Id = item.InstanceId, Kind = item.Definition.Kind, Condition = item.Condition,
                    Stolen = item.IsStolen, Appraised = item.IsAppraised, X = item.X, Y = item.Y, Rotated = item.Rotated };
            }
            if (_customer != null) save.Customer = new CustomerSave { Id = _customer.Id, AskingPrice = _customer.AskingPrice,
                Appraised = _customer.IsAppraised, BargainAttempted = _customer.BargainAttempted };
            return save;
        }

        public static bool TryRestore(CampaignSave save, out CampaignSession session, out string error)
        {
            session = null;
            error = "存档格式或数据无效。";
            if (save == null || save.Version != SaveVersion) { error = "不支持的存档版本。"; return false; }
            if ((save.Protagonist != Protagonist.Rayn && save.Protagonist != Protagonist.Chloe) || save.Coins < 0 || save.Coins > MaxCoins ||
                save.Day < 1 || save.Day > 9999 || save.ShopLevel < 0 || save.ShopLevel > 2 || save.ExpeditionsToday < 0 || save.ExpeditionsToday > 1 || save.RandomState == 0 ||
                save.NextItemId < 1 || save.NextItemId > 1000000 || save.NextTicketId < 1 || save.NextTicketId > 1000000 ||
                save.LastSettledTicketId < 0 || save.LastSettledTicketId >= save.NextTicketId || save.PaidRentWeek < 0 ||
                save.PaidRentWeek > (save.Day - 1) / 7 + 1 || save.CustomerIndex < 0 || save.CustomerIndex > 3 + save.ShopLevel ||
                save.ClueMask < 0 || save.ClueMask > 7 || save.NodeIndex < 0 || save.NodeIndex > DungeonCatalog.Count ||
                save.State < CampaignState.Active || save.State > CampaignState.Completed || save.Items == null || save.Items.Length > GridInventory.Capacity) return false;
            if (save.Day % 7 != 0 && save.PaidRentWeek == (save.Day - 1) / 7 + 1) return false;
            if (save.State == CampaignState.Evicted && (save.Day % 7 != 0 || save.Coins >= BaseRent + save.ShopLevel * 60 || save.PaidRentWeek == (save.Day - 1) / 7 + 1)) return false;
            if (save.State == CampaignState.Completed && save.NodeIndex != DungeonCatalog.Count) return false;
            if (save.NodeIndex == DungeonCatalog.Count && save.State == CampaignState.Active) return false;
            if (save.NodeIndex >= 5 && (save.ClueMask & 1) == 0 || save.NodeIndex >= 10 && (save.ClueMask & 2) == 0) return false;
            var candidate = new CampaignSession {
                Protagonist = save.Protagonist, Coins = save.Coins, Day = save.Day, ShopLevel = save.ShopLevel, State = save.State, ExpeditionsToday = save.ExpeditionsToday,
                _randomState = save.RandomState, _nextItemId = save.NextItemId, _nextTicketId = save.NextTicketId,
                _lastSettledTicketId = save.LastSettledTicketId, _paidRentWeek = save.PaidRentWeek, _customerIndex = save.CustomerIndex,
                _clueMask = save.ClueMask, DungeonNodeIndex = save.NodeIndex, Newspaper = new WeeklyNewspaper((save.Day - 1) / 7 + 1)
            };
            for (int i = 0; i < save.Items.Length; i++)
            {
                InventoryItemSave item = save.Items[i];
                if (item == null || item.Id <= 0 || item.Id >= save.NextItemId || ItemCatalog.Get(item.Kind) == null || item.Condition < 1 || item.Condition > 100) return false;
                if (item.Appraised && ItemCatalog.Get(item.Kind).ClueId > 0 && (save.ClueMask & 1 << (ItemCatalog.Get(item.Kind).ClueId - 1)) == 0) return false;
                if (!candidate.Inventory.TryPlace(new InventoryEntry(item.Id, item.Kind, item.Condition, item.Stolen, item.Appraised), item.X, item.Y, item.Rotated)) return false;
            }
            candidate.CreateCustomer();
            if (candidate._customer == null)
            {
                if (save.Customer != null) return false;
            }
            else
            {
                CustomerSave customer = save.Customer;
                if (customer == null || customer.Id != candidate._customer.Id || customer.AskingPrice < candidate._customer.MinimumPrice || customer.AskingPrice > candidate._customer.AskingPrice ||
                    (!customer.BargainAttempted && customer.AskingPrice != candidate._customer.AskingPrice) ||
                    (save.Protagonist == Protagonist.Chloe && !customer.Appraised)) return false;
                candidate._customer.AskingPrice = customer.AskingPrice; candidate._customer.IsAppraised = customer.Appraised; candidate._customer.BargainAttempted = customer.BargainAttempted;
            }
            if (save.ActiveTicketId != 0)
            {
                DungeonNode node = candidate.CurrentNode;
                if (save.State == CampaignState.Evicted || save.ExpeditionsToday != 1 || node == null || node.Kind != DungeonNodeKind.Battle || save.ActiveNodeIndex != node.Index ||
                    save.ActiveTicketId <= save.LastSettledTicketId || save.ActiveTicketId >= save.NextTicketId || node.Depth > candidate.UnlockedDepth) return false;
                candidate.ActiveExpedition = new ExpeditionTicket(save.ActiveTicketId, node);
            }
            else if (save.ActiveNodeIndex != -1 || save.PendingOutcome != null) return false;
            if (save.PendingOutcome != null)
            {
                if (!ValidOutcome(save.PendingOutcome) || save.PendingOutcome.TicketId != save.ActiveTicketId) return false;
                candidate._pendingOutcome = CloneOutcome(save.PendingOutcome);
            }
            session = candidate; error = string.Empty; return true;
        }
    }
}
