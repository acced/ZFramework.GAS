using System;

namespace IronOath.Campaign
{
    // Public fields are intentional: Unity JsonUtility can serialize these without a Unity dependency here.
    [Serializable]
    public sealed class CampaignSave
    {
        public int Version = CampaignSession.SaveVersion;
        public Protagonist Protagonist;
        public int Coins;
        public int Day;
        public int ShopLevel;
        public int ExpeditionsToday;
        public CampaignState State;
        public int RandomState;
        public int NextItemId;
        public int NextTicketId;
        public int LastSettledTicketId;
        public int PaidRentWeek;
        public int CustomerIndex;
        public int ClueMask;
        public int NodeIndex;
        public int ActiveTicketId;
        public int ActiveNodeIndex = -1;
        public InventoryItemSave[] Items = Array.Empty<InventoryItemSave>();
        public CustomerSave Customer;
        public ExpeditionOutcome PendingOutcome;
    }
    [Serializable]
    public sealed class InventoryItemSave
    {
        public int Id;
        public ItemKind Kind;
        public int Condition;
        public bool Stolen;
        public bool Appraised;
        public int X;
        public int Y;
        public bool Rotated;
    }
    [Serializable]
    public sealed class CustomerSave
    {
        public int Id;
        public int AskingPrice;
        public bool Appraised;
        public bool BargainAttempted;
    }
}
