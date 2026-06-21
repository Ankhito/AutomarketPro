using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutomarketPro.Models
{
    public class ScannedItem
    {
        public uint ItemId { get; set; }
        public string ItemName { get; set; } = "";
        public int Quantity { get; set; }
        public bool IsHQ { get; set; }
        public uint VendorPrice { get; set; }
        public uint MarketPrice { get; set; }
        public uint ListingPrice { get; set; }
        public uint RecentSalePrice { get; set; }
        public bool IsProfitable { get; set; }
        public int ProfitPerItem { get; set; }
        public long TotalProfit { get; set; }
        public uint StackSize { get; set; }
        public InventoryType InventoryType { get; set; }
        public int InventorySlot { get; set; }
        public bool CanBeListedOnMarketBoard { get; set; } = true;
        public int? SourceRetainerIndex { get; set; }
        public string SourceName { get; set; } = "Inventory";
        public double SellabilityScore { get; set; }
        public int Sales24h { get; set; }
        public int UnitsSold24h { get; set; }
        public int ActiveListings { get; set; }
        public int ActiveListedQuantity { get; set; }
        public double? LastSaleAgeHours { get; set; }
        public string MarketHealth { get; set; } = "Unknown";
    }
}

