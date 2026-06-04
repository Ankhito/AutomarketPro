using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using AutomarketPro.Core;
using AutomarketPro.Models;
using Newtonsoft.Json;
using ECommons.DalamudServices;
using InventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager;

namespace AutomarketPro.Services
{
    public class MarketScanner : IDisposable
    {
        private readonly AutomarketProPlugin Plugin;
        private readonly HttpClient HttpClient;
        private List<ScannedItem> ScannedItems = new();
        private bool IsScanning = false;
        private CancellationTokenSource? CancelToken;
        private string? CachedWorldName = null;
        // Hashmap to cache lowest data center prices per item ID + HQ status (only used when DataCenterScan is enabled)
        // Key: (ItemId, IsHQ) -> Value: lowest price
        private Dictionary<(uint ItemId, bool IsHQ), uint> DataCenterPriceCache = new Dictionary<(uint ItemId, bool IsHQ), uint>();
        
        public IReadOnlyList<ScannedItem> Items => ScannedItems.AsReadOnly();
        public bool Scanning => IsScanning;
        public event Action? ScanComplete;
        
        /// <summary>
        /// Gets the current world name. Returns null if not available yet.
        /// </summary>
        public string? CurrentWorldName
        {
            get
            {
                if (!string.IsNullOrEmpty(CachedWorldName))
                    return CachedWorldName;
                    
                // Try to get it fresh if not cached
                var worldName = GetWorldNameOnMainThread();
                if (!string.IsNullOrEmpty(worldName))
                {
                    CachedWorldName = worldName;
                }
                return worldName;
            }
        }
        
        public MarketScanner(AutomarketProPlugin plugin)
        {
            Plugin = plugin;
            HttpClient = new HttpClient();
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "AutoMarketPro/1.0");
        }
        
        private void Log(string message)
        {
            Plugin?.MainWindow?.Log(message);
        }
        
        private void LogError(string message, Exception? ex = null)
        {
            Plugin?.MainWindow?.LogError(message, ex);
        }
        
        private void LogWarning(string message)
        {
            Plugin?.MainWindow?.LogWarning(message);
        }
        
        public void CacheWorldName()
        {
            CachedWorldName = GetWorldNameOnMainThread();
        }
        
        public async Task<bool> StartScanning()
        {
            try
            {
                if (IsScanning)
                {
                    LogWarning("[AutoMarket] [SCAN] Already scanning! Please wait for current scan to finish.");
                    return false;
                }
                
                if (Plugin == null)
                {
                    LogError("[AutoMarket] [SCAN] Plugin reference is null");
                    return false;
                }
                
                if (Plugin.DataManager == null)
                {
                    LogError("[AutoMarket] [SCAN] DataManager is null");
                    return false;
                }
                
                // Check if we're logged in (use fallback to Svc.ClientState if Plugin.ClientState is null)
                // ClientState can be null during transitions, so we check both
                bool isLoggedIn = false;
                if (Plugin.ClientState != null)
                {
                    isLoggedIn = Plugin.ClientState.IsLoggedIn;
                }
                else if (Svc.ClientState != null)
                {
                    isLoggedIn = Svc.ClientState.IsLoggedIn;
                }
                
                if (!isLoggedIn)
                {
                    LogWarning("[AutoMarket] [SCAN] Player may not be logged in - attempting scan anyway");
                    // Don't return false - let the scan attempt proceed, it will fail gracefully if inventory isn't accessible
                }
                
                if (string.IsNullOrEmpty(CachedWorldName))
                {
                    CacheWorldName();
                }
                
                IsScanning = true;
                ScannedItems.Clear();
                CancelToken = new CancellationTokenSource();
                
                // Reset data center price cache on every scan
                DataCenterPriceCache.Clear();
                
                // Send chat message when scanning starts
                Plugin?.PrintChat("[AutoMarket] Scanning inventory started...");
                
                // Small delay before first scan to let game fully initialize (prevents crashes on first use)
                await Task.Delay(550, CancelToken.Token);
                
                // Run inventory scan on framework thread to avoid race conditions with UI rendering
                // This is critical - accessing unsafe memory from background threads can cause crashes
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    try
                    {
                        ScanInventory();
                    }
                    catch (Exception ex)
                    {
                        LogError("[AutoMarket] [SCAN] Error during inventory scan", ex);
                    }
                });
                
                if (ScannedItems.Count == 0)
                {
                    // Send chat message when scanning completes with no items
                    Plugin?.PrintChat("[AutoMarket] Scanning complete! No items found.");
                    return true;
                }
                
                await FetchMarketPrices(CancelToken.Token);
                EvaluateProfitability();
                
                // Send chat message when scanning completes successfully
                if (ScannedItems != null)
                {
                    Plugin?.PrintChat($"[AutoMarket] Scanning complete! Found {ScannedItems.Count} items.");
                }
                else
                {
                    Plugin?.PrintChat("[AutoMarket] Scanning complete!");
                }
                
                ScanComplete?.Invoke();
                
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                LogError("[AutoMarket] Scan failed", ex);
                return false;
            }
            finally
            {
                IsScanning = false;
                CancelToken?.Dispose();
                CancelToken = null;
            }
        }
        
        public void StopScanning()
        {
            CancelToken?.Cancel();
            IsScanning = false;
        }
        
        /// <summary>
        /// Safely gets InventoryManager with retry logic (up to 5 attempts).
        /// Returns null if all attempts fail.
        /// </summary>
        private unsafe InventoryManager* GetInventoryManagerSafe()
        {
            try
            {
                return InventoryManager.Instance();
            }
            catch
            {
                return null;
            }
        }

        private unsafe void ScanInventory()
        {
            var inventoryManager = GetInventoryManagerSafe();
            if (inventoryManager == null || Plugin.DataManager == null)
            {
                LogError("[AutoMarket] [SCAN] InventoryManager is null after retries or DataManager is null");
                return;
            }
            
            Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Item>? itemSheet = null;
            try
            {
                itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            }
            catch (Exception ex)
            {
                LogError("[AutoMarket] [SCAN] Exception while getting Item sheet", ex);
                return;
            }
            
            if (itemSheet == null)
            {
                LogError("[AutoMarket] [SCAN] Item sheet is null");
                return;
            }
            var inventoryTypes = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4
            };
            
            foreach (var type in inventoryTypes)
            {
                var container = inventoryManager->GetInventoryContainer(type);
                if (container == null) continue;
                
                for (int i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;
                    
                    var itemData = itemSheet.GetRow(slot->ItemId);
                    if (itemData.RowId == 0) continue;
                    
                    // Skip untradable items entirely (they can't be sold at all)
                    if (itemData.IsUntradable) continue;
                    
                    // Skip ignored items
                    if (Plugin.Configuration.IgnoredItemIds.Contains(slot->ItemId)) continue;
                    
                    // Check if item can be listed on market board
                    // ItemSearchCategory.RowId == 0 means it can't be listed on MB
                    bool canBeListedOnMB = itemData.ItemSearchCategory.RowId != 0;
                    
                    var itemName = itemData.Name.ToString();
                    if (string.IsNullOrWhiteSpace(itemName))
                    {
                        itemName = $"Item#{slot->ItemId}";
                    }
                    
                    var item = new ScannedItem
                    {
                        ItemId = slot->ItemId,
                        ItemName = itemName,
                        Quantity = slot->Quantity,
                        IsHQ = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                        VendorPrice = itemData.PriceLow,
                        StackSize = itemData.StackSize,
                        InventoryType = type,
                        InventorySlot = i,
                        CanBeListedOnMarketBoard = canBeListedOnMB
                    };
                    
                    var existing = ScannedItems.FirstOrDefault(x => x.ItemId == item.ItemId && x.IsHQ == item.IsHQ);
                    if (existing != null)
                    {
                        existing.Quantity += item.Quantity;
                        // Preserve the market board listing capability (should be same for same item)
                        existing.CanBeListedOnMarketBoard = item.CanBeListedOnMarketBoard;
                    }
                    else
                    {
                        ScannedItems.Add(item);
                    }
                }
            }
        }
        
        private string? GetWorldNameOnMainThread()
        {
            try
            {
                unsafe
                {
                    var localPlayer = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)
                        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
                    if (localPlayer != null && localPlayer->CurrentWorld != 0 && Plugin.DataManager != null)
                    {
                        var worldSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
                        if (worldSheet != null)
                        {
                            var world = worldSheet.GetRow(localPlayer->CurrentWorld);
                            if (world.RowId != 0)
                                return world.Name.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"[AutoMarket] [SCAN] Error getting world: {ex.Message}", ex);
            }

            return "Excalibur";
        }
        
        /// <summary>
        /// Gets the data center name from a world name.
        /// Returns the data center name for Universalis API usage.
        /// </summary>
        private string GetDataCenterFromWorld(string worldName)
        {
            // Mapping of world names to data center names (as used by Universalis API)
            var worldToDataCenter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Aether (NA)
                { "Adamantoise", "Aether" }, { "Cactuar", "Aether" }, { "Faerie", "Aether" },
                { "Gilgamesh", "Aether" }, { "Jenova", "Aether" }, { "Midgardsormr", "Aether" },
                { "Sargatanas", "Aether" }, { "Siren", "Aether" },
                
                // Primal (NA)
                { "Behemoth", "Primal" }, { "Excalibur", "Primal" }, { "Exodus", "Primal" },
                { "Famfrit", "Primal" }, { "Hyperion", "Primal" }, { "Lamia", "Primal" },
                { "Leviathan", "Primal" }, { "Ultros", "Primal" },
                
                // Crystal (NA)
                { "Balmung", "Crystal" }, { "Brynhildr", "Crystal" }, { "Coeurl", "Crystal" },
                { "Diabolos", "Crystal" }, { "Goblin", "Crystal" }, { "Malboro", "Crystal" },
                { "Mateus", "Crystal" }, { "Zalera", "Crystal" },
                
                // Chaos (EU)
                { "Cerberus", "Chaos" }, { "Louisoix", "Chaos" }, { "Moogle", "Chaos" },
                { "Omega", "Chaos" }, { "Phantom", "Chaos" }, { "Ragnarok", "Chaos" },
                { "Sagittarius", "Chaos" }, { "Spriggan", "Chaos" },
                
                // Light (EU)
                { "Alpha", "Light" }, { "Lich", "Light" }, { "Odin", "Light" },
                { "Phoenix", "Light" }, { "Raiden", "Light" }, { "Shiva", "Light" },
                { "Twintania", "Light" }, { "Zodiark", "Light" },
                
                // Elemental (JP)
                { "Aegis", "Elemental" }, { "Atomos", "Elemental" }, { "Carbuncle", "Elemental" },
                { "Garuda", "Elemental" }, { "Gungnir", "Elemental" }, { "Kujata", "Elemental" },
                { "Ramuh", "Elemental" }, { "Tonberry", "Elemental" }, { "Typhon", "Elemental" },
                { "Unicorn", "Elemental" },
                
                // Gaia (JP)
                { "Alexander", "Gaia" }, { "Bahamut", "Gaia" }, { "Durandal", "Gaia" },
                { "Fenrir", "Gaia" }, { "Ifrit", "Gaia" }, { "Ridill", "Gaia" },
                { "Tiamat", "Gaia" }, { "Ultima", "Gaia" }, { "Valefor", "Gaia" },
                { "Yojimbo", "Gaia" }, { "Zeromus", "Gaia" },
                
                // Mana (JP)
                { "Anima", "Mana" }, { "Asura", "Mana" }, { "Chocobo", "Mana" },
                { "Hades", "Mana" }, { "Ixion", "Mana" }, { "Masamune", "Mana" },
                { "Pandaemonium", "Mana" }, { "Shinryu", "Mana" }, { "Titan", "Mana" },
                
                // Meteor (JP)
                { "Belias", "Meteor" }, { "Mandragora", "Meteor" },
                
                // Materia (OCE)
                { "Bismarck", "Materia" }, { "Ravana", "Materia" }, { "Sephirot", "Materia" },
                { "Sophia", "Materia" }, { "Zurvan", "Materia" },
                
                // Dynamis (NA)
                { "Halicarnassus", "Dynamis" }, { "Maduin", "Dynamis" }, { "Marilith", "Dynamis" },
                { "Seraph", "Dynamis" }
            };
            
            if (worldToDataCenter.TryGetValue(worldName, out var dataCenter))
            {
                return dataCenter;
            }
            
            // Fallback: if world not found, try to return a default based on common patterns
            // For unknown worlds, default to "Aether" (most common NA data center)
            LogWarning($"[AutoMarket] Unknown world '{worldName}', defaulting to 'Aether' data center");
            return "Aether";
        }
        
        private async Task FetchMarketPrices(CancellationToken cancelToken)
        {
            string world = CachedWorldName ?? "Excalibur";
            bool useDataCenter = Plugin.Configuration.DataCenterScan;
            string location = useDataCenter ? GetDataCenterFromWorld(world) : world;
            
            if (useDataCenter)
            {
                Log($"[AutoMarket] Using data center scan mode: {location}");
            }
            
            foreach (var item in ScannedItems)
            {
                if (cancelToken.IsCancellationRequested) break;
                
                // Skip fetching market prices for items that can't be listed on market board
                // They will be automatically set to vendor in EvaluateProfitability
                if (!item.CanBeListedOnMarketBoard)
                {
                    item.MarketPrice = 0;
                    continue;
                }
                
                try
                {
                    
                    var hqParam = item.IsHQ ? "?hq=1" : "";
                    var url = $"https://universalis.app/api/v2/{location}/{item.ItemId}{hqParam}";
                    
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    
                    var response = await HttpClient.GetStringAsync(url, cts.Token);
                    var data = JsonConvert.DeserializeObject<UniversalisResponse>(response);
                    
                    if (data?.listings?.Length > 0)
                    {
                        // Find the lowest price across the data center (or world)
                        uint lowestPrice = (uint)data.listings
                            .OrderBy(l => l.pricePerUnit)
                            .First().pricePerUnit;
                        
                        item.MarketPrice = lowestPrice;
                        
                        // Cache the lowest data center price if Data Center Scan is enabled
                        if (useDataCenter)
                        {
                            // Store the lowest price for this ItemId + HQ combination (update if we find a lower price)
                            var cacheKey = (item.ItemId, item.IsHQ);
                            if (!DataCenterPriceCache.ContainsKey(cacheKey) || 
                                DataCenterPriceCache[cacheKey] > lowestPrice)
                            {
                                DataCenterPriceCache[cacheKey] = lowestPrice;
                            }
                        }
                        
                        if (data.recentHistory?.Length > 0)
                        {
                            item.RecentSalePrice = (uint)data.recentHistory
                                .OrderByDescending(h => h.timestamp)
                                .First().pricePerUnit;
                        }
                    }
                    else
                    {
                        item.MarketPrice = (uint)(item.VendorPrice * 1.5);
                    }
                    
                    await Task.Delay(132, cancelToken);
                }
                catch (OperationCanceledException)
                {
                    item.MarketPrice = item.VendorPrice;
                }
                catch (Exception ex)
                {
                    LogError($"Failed to fetch price for item {item.ItemId}", ex);
                    item.MarketPrice = item.VendorPrice;
                }
            }
        }
        
        private void EvaluateProfitability()
        {
            var config = Plugin.Configuration;
            bool useDataCenter = config.DataCenterScan;
            
            foreach (var item in ScannedItems)
            {
                // If item cannot be listed on market board, automatically set to vendor
                if (!item.CanBeListedOnMarketBoard)
                {
                    item.MarketPrice = 0;
                    item.ListingPrice = 0;
                    item.ProfitPerItem = -(int)item.VendorPrice; // Negative since we can only vendor
                    item.TotalProfit = -(long)(item.VendorPrice * item.Quantity);
                    item.IsProfitable = false; // Always vendor items that can't be listed on MB
                    continue;
                }
                
                // If Data Center Scan is enabled and we have a cached price, use it directly
                var cacheKey = (item.ItemId, item.IsHQ);
                if (useDataCenter && DataCenterPriceCache.ContainsKey(cacheKey))
                {
                    uint cachedPrice = DataCenterPriceCache[cacheKey];
                    item.MarketPrice = cachedPrice;
                    
                    // Skip price comparison, use cached value directly and apply undercut
                    if (config.AutoUndercut && cachedPrice > 0)
                    {
                        item.ListingPrice = (uint)Math.Max(1, cachedPrice - config.UndercutAmount);
                    }
                    else
                    {
                        item.ListingPrice = cachedPrice;
                    }
                    
                    var cachedProfitMargin = (int)item.ListingPrice - (int)item.VendorPrice;
                    item.ProfitPerItem = cachedProfitMargin;
                    item.TotalProfit = cachedProfitMargin * item.Quantity;
                    item.IsProfitable = item.TotalProfit > config.MinProfitThreshold;
                    continue;
                }
                
                // Normal price evaluation (when not using Data Center Scan or cache miss)
                uint priceForProfitability = item.MarketPrice;
                bool usingRecentSale = false;
                
                if (item.RecentSalePrice > 0)
                {
                    bool condition1 = item.RecentSalePrice > item.MarketPrice * 2;
                    bool condition2 = item.MarketPrice < item.VendorPrice * 2 && item.RecentSalePrice > item.VendorPrice + config.MinProfitThreshold;
                    
                    if (condition1 || condition2)
                    {
                        priceForProfitability = item.RecentSalePrice;
                        usingRecentSale = true;
                    }
                }
                
                if (config.AutoUndercut && item.MarketPrice > 0)
                {
                    item.ListingPrice = (uint)Math.Max(1, item.MarketPrice - config.UndercutAmount);
                }
                else
                {
                    item.ListingPrice = item.MarketPrice;
                }
                
                var expectedSalePrice = usingRecentSale ? priceForProfitability : item.ListingPrice;
                var profitMargin = (int)expectedSalePrice - (int)item.VendorPrice;
                
                item.ProfitPerItem = profitMargin;
                item.TotalProfit = profitMargin * item.Quantity;
                item.IsProfitable = item.TotalProfit > config.MinProfitThreshold;
            }
            
            // Sort by total profit descending
            ScannedItems = ScannedItems.OrderByDescending(i => i.TotalProfit).ToList();
        }
        
        public List<ScannedItem> GetProfitableItems() => ScannedItems.Where(i => i.IsProfitable).ToList();
        public List<ScannedItem> GetUnprofitableItems() => ScannedItems.Where(i => !i.IsProfitable).ToList();
        
        /// <summary>
        /// Fetches market price for a single item (used for listed item management).
        /// </summary>
        public async Task FetchMarketPriceForItem(ScannedItem item, CancellationToken cancelToken)
        {
            try
            {
                // Skip fetching market prices for items that can't be listed on market board
                if (!item.CanBeListedOnMarketBoard)
                {
                    item.MarketPrice = 0;
                    return;
                }
                
                string world = CachedWorldName ?? "Excalibur";
                bool useDataCenter = Plugin.Configuration.DataCenterScan;
                string location = useDataCenter ? GetDataCenterFromWorld(world) : world;
                
                var hqParam = item.IsHQ ? "?hq=1" : "";
                var url = $"https://universalis.app/api/v2/{location}/{item.ItemId}{hqParam}";
                
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                
                var response = await HttpClient.GetStringAsync(url, cts.Token);
                var data = JsonConvert.DeserializeObject<UniversalisResponse>(response);
                
                if (data?.listings?.Length > 0)
                {
                    uint lowestPrice = (uint)data.listings
                        .OrderBy(l => l.pricePerUnit)
                        .First().pricePerUnit;
                    
                    item.MarketPrice = lowestPrice;
                    
                    // Cache the lowest data center price if Data Center Scan is enabled
                    if (useDataCenter)
                    {
                        var cacheKey = (item.ItemId, item.IsHQ);
                        if (!DataCenterPriceCache.ContainsKey(cacheKey) || 
                            DataCenterPriceCache[cacheKey] > lowestPrice)
                        {
                            DataCenterPriceCache[cacheKey] = lowestPrice;
                        }
                    }
                    
                    if (data.recentHistory?.Length > 0)
                    {
                        item.RecentSalePrice = (uint)data.recentHistory
                            .OrderByDescending(h => h.timestamp)
                            .First().pricePerUnit;
                    }
                }
                else
                {
                    item.MarketPrice = (uint)(item.VendorPrice * 1.5);
                }
                
                await Task.Delay(132, cancelToken);
            }
            catch (OperationCanceledException)
            {
                item.MarketPrice = item.VendorPrice;
            }
            catch (Exception ex)
            {
                LogError($"Failed to fetch price for item {item.ItemId}", ex);
                item.MarketPrice = item.VendorPrice;
            }
        }
        
        /// <summary>
        /// Evaluates profitability for a single item (used for listed item management).
        /// </summary>
        public void EvaluateProfitabilityForItem(ScannedItem item)
        {
            var config = Plugin.Configuration;
            bool useDataCenter = config.DataCenterScan;
            
            // If item cannot be listed on market board, automatically set to vendor
            if (!item.CanBeListedOnMarketBoard)
            {
                item.MarketPrice = 0;
                item.ListingPrice = 0;
                item.ProfitPerItem = -(int)item.VendorPrice;
                item.TotalProfit = -(long)(item.VendorPrice * item.Quantity);
                item.IsProfitable = false;
                return;
            }
            
            // If Data Center Scan is enabled and we have a cached price, use it directly
            var cacheKey = (item.ItemId, item.IsHQ);
            if (useDataCenter && DataCenterPriceCache.ContainsKey(cacheKey))
            {
                uint cachedPrice = DataCenterPriceCache[cacheKey];
                item.MarketPrice = cachedPrice;
                
                if (config.AutoUndercut && cachedPrice > 0)
                {
                    item.ListingPrice = (uint)Math.Max(1, cachedPrice - config.UndercutAmount);
                }
                else
                {
                    item.ListingPrice = cachedPrice;
                }
                
                var cachedProfitMargin = (int)item.ListingPrice - (int)item.VendorPrice;
                item.ProfitPerItem = cachedProfitMargin;
                item.TotalProfit = cachedProfitMargin * item.Quantity;
                item.IsProfitable = item.TotalProfit > config.MinProfitThreshold;
                return;
            }
            
            // Normal price evaluation
            uint priceForProfitability = item.MarketPrice;
            bool usingRecentSale = false;
            
            if (item.RecentSalePrice > 0)
            {
                bool condition1 = item.RecentSalePrice > item.MarketPrice * 2;
                bool condition2 = item.MarketPrice < item.VendorPrice * 2 && item.RecentSalePrice > item.VendorPrice + config.MinProfitThreshold;
                
                if (condition1 || condition2)
                {
                    priceForProfitability = item.RecentSalePrice;
                    usingRecentSale = true;
                }
            }
            
            if (config.AutoUndercut && item.MarketPrice > 0)
            {
                item.ListingPrice = (uint)Math.Max(1, item.MarketPrice - config.UndercutAmount);
            }
            else
            {
                item.ListingPrice = item.MarketPrice;
            }
            
            var expectedSalePrice = usingRecentSale ? priceForProfitability : item.ListingPrice;
            var profitMargin = (int)expectedSalePrice - (int)item.VendorPrice;
            
            item.ProfitPerItem = profitMargin;
            item.TotalProfit = profitMargin * item.Quantity;
            item.IsProfitable = item.TotalProfit > config.MinProfitThreshold;
        }
        
        public void Dispose()
        {
            CancelToken?.Cancel();
            HttpClient?.Dispose();
        }
    }
}

