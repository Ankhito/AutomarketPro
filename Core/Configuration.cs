using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace AutomarketPro.Core
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;
        
        // Market settings
        public int UndercutAmount { get; set; } = 1;
        public int MinProfitThreshold { get; set; } = 100;
        public bool AutoUndercut { get; set; } = true;
        
        // Automation settings
        public int ActionDelay { get; set; } = 300;
        public int RetainerDelay { get; set; } = 1200;
        public bool ListOnlyMode { get; set; } = false;
        public bool VendorOnlyMode { get; set; } = false;
        
        // Filter settings
        public bool SkipHQItems { get; set; } = false;
        public bool SkipCollectables { get; set; } = true;
        public bool SkipGear { get; set; } = false;
        
        // Scanning settings
        public bool DataCenterScan { get; set; } = false;
        
        // Listed item management settings
        public bool ManageListedItems { get; set; } = false;
        
        // Ignored items (item IDs that should be excluded from processing)
        public HashSet<uint> IgnoredItemIds { get; set; } = new HashSet<uint>();

        // Clear feature — 0-based retainer indices to SKIP when clearing (opt-out, all cleared by default)
        public HashSet<int> ClearExcludedRetainers { get; set; } = new HashSet<int>();

        // Clear feature — when true, withdrawn items go to retainer's bag instead of player's inventory
        public bool ClearReturnToRetainerInventory { get; set; } = false;
        
        // Debug settings
        public bool DebugLogsEnabled { get; set; } = false;
        
        [NonSerialized]
        public IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
        }
        
        public void Save()
        {
            pluginInterface?.SavePluginConfig(this);
        }
        
        public void ResetToDefaults()
        {
            UndercutAmount = 1;
            MinProfitThreshold = 100;
            AutoUndercut = true;
            ActionDelay = 300;
            RetainerDelay = 1200;
            ListOnlyMode = false;
            VendorOnlyMode = false;
            SkipHQItems = false;
            SkipCollectables = true;
            SkipGear = false;
            DataCenterScan = false;
            ManageListedItems = false;
            DebugLogsEnabled = false;
        }
    }
}

