using IronOath.Campaign;
using UnityEngine;

namespace IronOath.Presentation
{
    public sealed partial class IronOathDemo
    {
        void BuildDungeon()
        {
            IronOathUi.Label(_screen, "深入城市的影子", 65, 145, 1120, 61, 39, IronOathUi.PaleGold);
            IronOathUi.Label(_screen, "步进探索  /  遗物坐标  /  每一场战斗，都是一笔投资", 68, 218, 1120, 35, 22, IronOathUi.Muted);
            var map = IronOathUi.Panel(_screen, "Abyss route", 64, 291, 1120, 636, new Color32(193, 174, 136, 245));
            IronOathUi.Art(map, "IronOath/Art/BlackstoneMap", 1, 1, 1118, 634);
            IronOathUi.Fill(map, "Map wash", 1, 1, 1118, 634, new Color(.84f, .76f, .59f, .21f));
            for (int depth = 0; depth < 3; depth++)
            {
                float y = 67 + depth * 184;
                IronOathUi.Label(map, "0" + (depth + 1) + " 层", 28, y + 31, 110, 37, 25, new Color32(69, 55, 35, 255));
                for (int nodeOffset = 0; nodeOffset < 5; nodeOffset++)
                {
                    int index = depth * 5 + nodeOffset;
                    DungeonNode node = DungeonCatalog.Get(index);
                    bool current = index == _campaign.DungeonNodeIndex;
                    bool cleared = index < _campaign.DungeonNodeIndex;
                    bool unlocked = node.RequiredClueCount <= _campaign.ClueCount;
                    float x = 156 + nodeOffset * 186;
                    if (nodeOffset < 4) IronOathUi.Fill(map, "Route", x + 81, y + 47, 186, 2, new Color(.6f, .5f, .32f, .28f));
                    var card = IronOathUi.Panel(map, node.Name, x, y, 162, 118,
                        current ? new Color32(87, 76, 49, 255) : new Color32(33, 45, 48, 245), false);
                    IronOathUi.Label(card, cleared ? "已探索" : !unlocked ? "待解锁" : NodeName(node.Kind),
                        10, 15, 142, 28, 17, current ? IronOathUi.PaleGold : IronOathUi.Muted, TextAnchor.MiddleCenter);
                    IronOathUi.Label(card, node.Name, 8, 55, 146, 53, 20,
                        unlocked ? IronOathUi.Ivory : IronOathUi.Muted, TextAnchor.MiddleCenter);
                }
            }
            var detail = IronOathUi.Panel(_screen, "Expedition ledger", 1225, 153, 630, 774);
            IronOathUi.Label(detail, "下一步", 35, 28, 560, 30, 20, IronOathUi.Gold);
            DungeonNode currentNode = _campaign.CurrentNode;
            IronOathUi.Label(detail, currentNode?.Name ?? "黎明之后", 35, 85, 560, 61, 36, IronOathUi.PaleGold);
            IronOathUi.Label(detail, currentNode?.Description ?? "这一章的线索已经集齐，父母仍在深处等待。",
                35, 171, 560, 124, 24, IronOathUi.Ivory);
            IronOathUi.Rule(detail, 35, 324, 560);
            IronOathUi.Label(detail, "已发现家族线索", 35, 350, 360, 38, 22, IronOathUi.Muted);
            IronOathUi.Label(detail, _campaign.ClueCount + " / 3", 405, 340, 190, 52, 34,
                IronOathUi.PaleGold, TextAnchor.MiddleRight);
            IronOathUi.Label(detail, "异魔 / 构装体：白条归零，取出魔核。\n人形敌：保住 ≥80% 白条，回收成衣。\n父母的遗物需要鉴定，才会揭示坐标。",
                35, 425, 560, 131, 21, IronOathUi.Muted);
            bool waitingForTomorrow = _campaign.ActiveExpedition == null && currentNode != null &&
                currentNode.Kind == DungeonNodeKind.Battle && !_campaign.CanExpeditionToday;
            IronOathUi.Label(detail, "今日远征 " + _campaign.ExpeditionsToday + " / 1 · 每次出发都要做好准备", 35, 562, 560, 28,
                17, IronOathUi.Gold);
            string action = waitingForTomorrow ? "今日已远征 · 明日继续" : _campaign.HasPendingRewards ? "领取待结算战利品" : _campaign.ActiveExpedition != null ? "继续未完成的遭遇" :
                currentNode != null ? "前往 · " + currentNode.Name : "本章探索完成";
            IronOathUi.Button(detail, action, 35, 603, 560, 61, EnterDungeon,
                true, !waitingForTomorrow && (currentNode != null || _campaign.HasPendingRewards || _campaign.ActiveExpedition != null));
            IronOathUi.Button(detail, "检查阵容与战术", 35, 690, 560, 48, () => ShowPage(Page.Formation));
        }

        static string NodeName(DungeonNodeKind kind)
            => kind == DungeonNodeKind.Battle ? "交战" : kind == DungeonNodeKind.Cache ? "藏物" : kind == DungeonNodeKind.Clue ? "家族遗物" : "营地";

        void EnterDungeon()
        {
            if (_campaign.HasPendingRewards)
            {
                CampaignResult claim = _campaign.TryClaimExpeditionRewards();
                Save(); ShowPage(Page.Shop); Toast(claim.Message, claim.Succeeded); return;
            }
            if (_campaign.ActiveExpedition != null)
            {
                _ticket = _campaign.ActiveExpedition;
                PrepareBattle(_ticket.EncounterId);
                _practice = false; ShowPage(Page.Battle); return;
            }
            CampaignResult result = _campaign.TryEnterDungeonNode(out ExpeditionTicket ticket);
            if (!result.Succeeded || ticket == null)
            {
                Save(); ShowPage(Page.Dungeon); Toast(result.Message, result.Succeeded); return;
            }
            _ticket = ticket;
            PrepareBattle(ticket.EncounterId);
            _practice = false;
            Save();
            ShowPage(Page.Battle);
        }

        void BuildChronicle()
        {
            var story = IronOathUi.Panel(_screen, "Family chronicle", 65, 153, 1090, 774,
                new Color32(218, 205, 174, 248));
            Color ink = new Color32(45, 45, 40, 255);
            IronOathUi.Label(story, "铁誓家的账本", 50, 36, 990, 63, 42, ink);
            IronOathUi.Label(story, "第一章  ·  黑石港的继承人", 53, 112, 980, 35, 21, new Color32(105, 83, 52, 255));
            IronOathUi.Rule(story, 50, 172, 990);
            IronOathUi.Label(story,
                "三个月前，父母收到一封没有署名的委托信。\n他们再次披挂进入深渊，只留下一间快要破产的店。\n\n" +
                "刚满十八岁的兄妹，第一次独自翻开了账本。\n每周日的租金、柜台后的旧物，以及顾客不愿说出的来历，\n都在把两条道路慢慢连接起来。\n\n" +
                "守住这间店，是为了有一个能等他们回来的地方。\n走进地下城，是因为再也不想只是等下去。",
                55, 207, 975, 400, 26, ink);
            IronOathUi.Label(story, _campaign.ClueCount >= 3 ?
                "最新线索：教会密令证明，他们仍然活着。\n下一章将从深井之下开始。" :
                _campaign.ClueCount == 2 ? "最新线索：母亲的星纹镜片指向深井。" :
                _campaign.ClueCount == 1 ? "最新线索：父亲的印戒指向旧水闸。" :
                "寻亲进度：鉴定可疑物品，找回父母遗失的装备。",
                55, 636, 975, 95, 24, new Color32(111, 70, 35, 255));
            var partner = IronOathUi.Panel(_screen, "Partners", 1210, 153, 645, 774);
            IronOathUi.Label(partner, "兄妹 · 合伙人", 35, 30, 570, 47, 30, IronOathUi.PaleGold);
            IronOathUi.Label(partner, "雷恩", 35, 115, 570, 43, 29, IronOathUi.Ivory);
            IronOathUi.Label(partner, "实战派少东家\n铁誓统领 · 十字护甲支援 · 巧手翻新", 35, 174, 570, 81, 21, IronOathUi.Muted);
            IronOathUi.Rule(partner, 35, 283, 570);
            IronOathUi.Label(partner, "克萝伊", 35, 318, 570, 43, 29, IronOathUi.Ivory);
            IronOathUi.Label(partner, "眼毒嘴甜的账房少当家\n秘宝猎手 · 识破甲缝 · 慧眼识珠", 35, 380, 570, 81, 21, IronOathUi.Muted);
            IronOathUi.Rule(partner, 35, 486, 570);
            IronOathUi.Label(partner, "我们不是在替谁看店。\n这里，也是我们的家。", 35, 531, 570, 109, 29,
                IronOathUi.PaleGold, TextAnchor.MiddleCenter);
            IronOathUi.Button(partner, "回到柜台", 35, 679, 570, 57, () => ShowPage(Page.Shop), true);
        }
    }
}
