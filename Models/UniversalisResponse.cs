using System;

namespace AutomarketPro.Models
{
    public class UniversalisResponse
    {
        public Listing[] listings { get; set; } = Array.Empty<Listing>();
        public RecentHistory[] recentHistory { get; set; } = Array.Empty<RecentHistory>();
        public long lastUploadTime { get; set; }
    }
    
    public class Listing
    {
        public int pricePerUnit { get; set; }
        public int quantity { get; set; }
        public bool hq { get; set; }
        public string retainerName { get; set; } = "";
        public string worldName { get; set; } = "";
        public long lastReviewTime { get; set; }
    }
    
    public class RecentHistory
    {
        public int pricePerUnit { get; set; }
        public int quantity { get; set; }
        public bool hq { get; set; }
        public long timestamp { get; set; }
    }
}

