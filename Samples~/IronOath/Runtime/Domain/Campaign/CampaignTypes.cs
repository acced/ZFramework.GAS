using System;

namespace IronOath.Campaign
{
    public enum Protagonist { Rayn, Chloe }
    public enum CampaignState { Active, Evicted, Completed }
    public enum ItemKind { MagicCore, IntactArmor, DamagedArmor, AlchemyVial, FathersSignet, MothersLens, ChurchDispatch }
    public enum MarketCategory { Core, Armor, Supplies, Relic }
    public enum DungeonNodeKind { Battle, Cache, Clue, Camp }

    public readonly struct CampaignResult
    {
        public readonly bool Succeeded;
        public readonly string Message;
        public CampaignResult(bool succeeded, string message) { Succeeded = succeeded; Message = message; }
        public static CampaignResult Ok(string message) => new CampaignResult(true, message);
        public static CampaignResult Fail(string message) => new CampaignResult(false, message);
    }

    public sealed class ItemDefinition
    {
        public ItemKind Kind { get; }
        public string Name { get; }
        public string Description { get; }
        public int Width { get; }
        public int Height { get; }
        public int BaseValue { get; }
        public MarketCategory Category { get; }
        public int ClueId { get; }
        public bool IsFamilyRelic => ClueId > 0;
        internal ItemDefinition(ItemKind kind, string name, string description, int width, int height,
            int value, MarketCategory category, int clueId = 0)
        {
            Kind = kind; Name = name; Description = description; Width = width; Height = height;
            BaseValue = value; Category = category; ClueId = clueId;
        }
    }

    public static class ItemCatalog
    {
        private static readonly ItemDefinition[] Items = {
            new ItemDefinition(ItemKind.MagicCore, "裂隙魔核", "砸空异魔护甲后取出的完整魔核。", 1, 1, 85, MarketCategory.Core),
            new ItemDefinition(ItemKind.IntactArmor, "黑铁佣兵甲", "保甲击杀收获的成衣；完好率越高，卖价越高。", 2, 3, 150, MarketCategory.Armor),
            new ItemDefinition(ItemKind.DamagedArmor, "受损鳞甲", "仍能翻新的战利品；先修补再卖更划算。", 2, 3, 110, MarketCategory.Armor),
            new ItemDefinition(ItemKind.AlchemyVial, "炼金药匣", "边境冒险者常备的药剂与试剂。", 1, 2, 36, MarketCategory.Supplies),
            new ItemDefinition(ItemKind.FathersSignet, "父亲的铁誓印戒", "戒内刻着父亲的探险队暗号，指向旧水闸。", 1, 1, 120, MarketCategory.Relic, 1),
            new ItemDefinition(ItemKind.MothersLens, "母亲的星纹镜片", "镜片背面藏着深渊观测站的坐标。", 1, 1, 180, MarketCategory.Relic, 2),
            new ItemDefinition(ItemKind.ChurchDispatch, "封蜡密令", "教会将探险队送往深井的命令；父母仍可能活着。", 1, 2, 240, MarketCategory.Relic, 3)
        };
        public static int Count => Items.Length;
        public static ItemDefinition Get(ItemKind kind)
        {
            int index = (int)kind;
            return index >= 0 && index < Items.Length ? Items[index] : null;
        }
    }

    public sealed class InventoryEntry
    {
        internal GridInventory Owner { get; set; }
        public int InstanceId { get; }
        public ItemDefinition Definition { get; }
        public int Condition { get; internal set; }
        public bool IsStolen { get; internal set; }
        public bool IsAppraised { get; internal set; }
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public bool Rotated { get; internal set; }
        public int Width => Rotated ? Definition.Height : Definition.Width;
        public int Height => Rotated ? Definition.Width : Definition.Height;
        public InventoryEntry(int instanceId, ItemKind kind, int condition = 100, bool stolen = false, bool appraised = false)
        {
            if (instanceId <= 0) throw new ArgumentOutOfRangeException(nameof(instanceId));
            if (condition < 1 || condition > 100) throw new ArgumentOutOfRangeException(nameof(condition));
            InstanceId = instanceId; Definition = ItemCatalog.Get(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind));
            Condition = condition; IsStolen = stolen; IsAppraised = appraised;
        }
    }

    public sealed class WeeklyNewspaper
    {
        public int Week { get; }
        public string Headline { get; }
        public string Rumor { get; }
        public int ArmorPercent { get; }
        public int CorePercent { get; }
        public int RelicPercent { get; }
        internal WeeklyNewspaper(int week)
        {
            Week = week;
            int season = (week - 1) % 3;
            ArmorPercent = season == 0 ? 120 : season == 1 ? 85 : 105;
            CorePercent = season == 0 ? 90 : season == 1 ? 130 : 110;
            RelicPercent = season == 0 ? 115 : season == 1 ? 100 : 135;
            Headline = season == 0 ? "城防征募！完好护甲涨价两成" : season == 1 ? "炼金工坊缺货，魔核收购价上涨" : "教会重金征集旧探险队遗物";
            Rumor = season == 0 ? "有人在旧水闸见过铁誓探险队的徽记。" : season == 1 ? "运货人说，深层观测站仍有灯火。" : "教会封锁了深井，却不肯公布失踪者名单。";
        }
        public int PercentFor(MarketCategory category)
            => category == MarketCategory.Armor ? ArmorPercent : category == MarketCategory.Core ? CorePercent : category == MarketCategory.Relic ? RelicPercent : 100;
    }

    public sealed class CustomerOffer
    {
        public int Id { get; internal set; }
        public string Name { get; internal set; }
        public string Dialogue { get; internal set; }
        public ItemDefinition Item { get; internal set; }
        public int Condition { get; internal set; }
        public bool IsStolen { get; internal set; }
        public int AskingPrice { get; internal set; }
        public int FairValue { get; internal set; }
        public int MinimumPrice { get; internal set; }
        public bool IsAppraised { get; internal set; }
        public bool BargainAttempted { get; internal set; }
    }

    public sealed class DungeonNode
    {
        public int Index { get; }
        public int Depth { get; }
        public string Name { get; }
        public string Description { get; }
        public DungeonNodeKind Kind { get; }
        public int EncounterId { get; }
        public int RequiredClueCount { get; }
        internal DungeonNode(int index, string name, string description, DungeonNodeKind kind, int encounter, int clues)
        {
            Index = index; Depth = index / 5 + 1; Name = name; Description = description;
            Kind = kind; EncounterId = encounter; RequiredClueCount = clues;
        }
    }

    public static class DungeonCatalog
    {
        private static readonly DungeonNode[] Nodes = {
            new DungeonNode(0, "港下旧栈道", "挡路的走私者穿着完好的佣兵甲。", DungeonNodeKind.Battle, 0, 0),
            new DungeonNode(1, "潮湿货仓", "墙根藏着一只无人认领的钱袋。", DungeonNodeKind.Cache, 0, 0),
            new DungeonNode(2, "破碎水闸", "构装守卫的魔核仍在铁壳中搏动。", DungeonNodeKind.Battle, 1, 0),
            new DungeonNode(3, "铁誓营火", "父亲留下的印戒压着一张水闸地图。", DungeonNodeKind.Clue, 0, 0),
            new DungeonNode(4, "地下旅舍", "短暂休整，伙伴帮你清点装备。", DungeonNodeKind.Camp, 0, 0),
            new DungeonNode(5, "沉默回廊", "潮气与生锈的教会铠甲让雷电格外危险。", DungeonNodeKind.Battle, 2, 1),
            new DungeonNode(6, "炼金工坊", "密封药匣幸免于塌方。", DungeonNodeKind.Cache, 0, 1),
            new DungeonNode(7, "结霜的守望台", "冰封构装体守着观测站的入口。", DungeonNodeKind.Battle, 3, 1),
            new DungeonNode(8, "星纹观测站", "母亲的镜片刻着第三层坐标。", DungeonNodeKind.Clue, 0, 1),
            new DungeonNode(9, "旧哨所", "伙伴为下一次远征修补护甲。", DungeonNodeKind.Camp, 0, 1),
            new DungeonNode(10, "封印桥", "教会卫队守着通往深井的桥。", DungeonNodeKind.Battle, 4, 2),
            new DungeonNode(11, "禁库", "遗落的金锭压住半截探险日志。", DungeonNodeKind.Cache, 0, 2),
            new DungeonNode(12, "深井门庭", "重装异魔占据最后一道门。", DungeonNodeKind.Battle, 5, 2),
            new DungeonNode(13, "密令书房", "父母没有葬身深渊，他们被送往了更深处。", DungeonNodeKind.Clue, 0, 2),
            new DungeonNode(14, "黎明归途", "带着新的线索回到当铺。寻亲之路还在继续。", DungeonNodeKind.Camp, 0, 2)
        };
        public static int Count => Nodes.Length;
        public static DungeonNode Get(int index) => index >= 0 && index < Nodes.Length ? Nodes[index] : null;
    }

    public sealed class ExpeditionTicket
    {
        public int Id { get; }
        public int NodeIndex { get; }
        public int EncounterId { get; }
        public int Depth { get; }
        internal ExpeditionTicket(int id, DungeonNode node)
        { Id = id; NodeIndex = node.Index; EncounterId = node.EncounterId; Depth = node.Depth; }
    }

    [Serializable]
    public sealed class LootAward
    {
        public ItemKind Kind;
        public int Condition = 100;
        public bool Stolen;
        public LootAward() { }
        public LootAward(ItemKind kind, int condition = 100, bool stolen = false)
        { Kind = kind; Condition = condition; Stolen = stolen; }
    }

    [Serializable]
    public sealed class ExpeditionOutcome
    {
        public int TicketId;
        public bool Victory;
        public int Coins;
        public LootAward[] Loot = Array.Empty<LootAward>();
    }
}
