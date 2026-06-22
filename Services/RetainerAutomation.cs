using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutomarketPro.Models;
using AutomarketPro.Automation;

namespace AutomarketPro.Services
{
    public class RetainerAutomation : IDisposable
    {
        private const int MaxMarketListings = 20;
        private readonly AutomarketPro.AutomarketProPlugin Plugin;
        private readonly MarketScanner Scanner;
        private readonly RetainerInteraction RetainerInteraction;
        private readonly ItemListing ItemListing;
        
        private bool IsRunning = false;
        private bool IsPaused = false;
        private CancellationTokenSource? AutomationToken;
        private CancellationTokenSource? PostedGilToken;
        private RunSummary LastRunSummary = new();
        private PostedGilSummary LastPostedGilSummary = new();
        
        public bool Running => IsRunning;
        public bool Paused => IsPaused;
        public bool PostedGilRefreshRunning { get; private set; }
        public event Action<string>? StatusUpdate;
        
        public RetainerAutomation(AutomarketPro.AutomarketProPlugin plugin, MarketScanner scanner)
        {
            Plugin = plugin;
            Scanner = scanner;
            
            // Initialize automation helpers
            RetainerInteraction = new RetainerInteraction(plugin);
            ItemListing = new ItemListing(plugin);
            
            // Wire up logging delegates
            RetainerInteraction.Log = (msg) => Plugin?.MainWindow?.Log(msg);
            RetainerInteraction.LogError = (msg, ex) => Plugin?.MainWindow?.LogError(msg, ex);
            ItemListing.Log = (msg) => Plugin?.MainWindow?.Log(msg);
            ItemListing.LogError = (msg, ex) => Plugin?.MainWindow?.LogError(msg, ex);
            
            // Set up callback for checking retainer listing count (used during batching)
            ItemListing.GetRetainerListingCount = (index) => RetainerInteraction.GetRetainerMarketItemCount(index);
        }
        
        // Helper methods for logging
        private void Log(string message)
        {
            Plugin?.MainWindow?.Log(message);
        }
        
        private void LogError(string message, Exception? ex = null)
        {
            Plugin?.MainWindow?.LogError(message, ex);
        }
        
        public async Task StartFullCycle()
        {
            if (IsRunning) return;
            
            IsRunning = true;
            IsPaused = false;
            AutomationToken = new CancellationTokenSource();
            LastRunSummary = new RunSummary();
            
            // Send chat message when automation starts
            Plugin?.PrintChat("[AutoMarket] Automation started...");
            
            try
            {
                StatusUpdate?.Invoke("Starting automation cycle...");
                Log("[AutoMarket] Starting full automation cycle");
                
                Log("[AutoMarket] Proceeding with automation...");
                
                var scanSuccess = await Scanner.StartScanning();
                if (!scanSuccess)
                {
                    StatusUpdate?.Invoke("Scan failed!");
                    return;
                }
                
                // Wait for scan to complete
                while (Scanner.Scanning && !AutomationToken.Token.IsCancellationRequested)
                {
                    await Task.Delay(66);
                }
                
                var config = Plugin.Configuration;
                
                // Handle mode-specific item routing
                List<ScannedItem> itemsToList = new();
                List<ScannedItem> itemsToVendor = new();
                
                if (config.ListOnlyMode)
                {
                    // List Only Mode: list only items that passed profitability/sellability checks.
                    itemsToList = Scanner.GetProfitableItems();
                    Log($"[AutoMarket] List Only Mode enabled - will list {itemsToList.Count} profitable item(s) and skip vendoring");
                }
                else if (config.VendorOnlyMode)
                {
                    // Vendor Only Mode: vendor only items that failed profitability/sellability checks.
                    itemsToVendor = Scanner.GetUnprofitableItems();
                    Log($"[AutoMarket] Vendor Only Mode enabled - will vendor {itemsToVendor.Count} unprofitable item(s) and skip listing");
                }
                else
                {
                    // Normal mode: List profitable, vendor unprofitable
                    itemsToList = Scanner.GetProfitableItems();
                    itemsToVendor = Scanner.GetUnprofitableItems();
                }
                
                if (itemsToList.Count == 0 && itemsToVendor.Count == 0)
                {
                    Log("[AutoMarket] No items to process!");
                    StatusUpdate?.Invoke("No items to process");
                    return;
                }
                
                StatusUpdate?.Invoke($"Processing {itemsToList.Count} to list, {itemsToVendor.Count} to vendor...");
                
                // Process retainers
                await ProcessRetainers(itemsToList, itemsToVendor, AutomationToken.Token);
                
                // Summary
                Log($"[AutoMarket] Cycle complete!");
                Log($"  → Listed {LastRunSummary.ItemsListed} items on MB");
                Log($"  → Vendored {LastRunSummary.ItemsVendored} items");
                Log($"  → Estimated revenue: {LastRunSummary.EstimatedRevenue:N0} gil");
                
                // Send chat message when automation completes successfully
                if (LastRunSummary != null)
                {
                    Plugin?.PrintChat($"[AutoMarket] Automation complete! Listed {LastRunSummary.ItemsListed} items, vendored {LastRunSummary.ItemsVendored} items.");
                }
                else
                {
                    Plugin?.PrintChat("[AutoMarket] Automation complete!");
                }
                
                StatusUpdate?.Invoke("Automation complete!");
            }
            catch (Exception ex)
            {
                LogError("[AutoMarket] Automation error", ex);
                StatusUpdate?.Invoke($"Error: {ex.Message}");
                
                // Send chat message if automation failed
                var errorMessage = ex?.Message ?? "Unknown error";
                Plugin?.PrintChat($"[AutoMarket] Automation failed: {errorMessage}");
            }
            finally
            {
                IsRunning = false;
                IsPaused = false;
                AutomationToken?.Dispose();
                AutomationToken = null;
            }
        }
        
        private async Task ProcessRetainers(List<ScannedItem> profitable, List<ScannedItem> unprofitable, CancellationToken token)
        {
            // Get number of retainers from game
            int retainerCount = RetainerInteraction.GetRetainerCount();
            
            if (retainerCount == 0)
            {
                LogError("[AutoMarket] No retainers found or unable to access retainer list!");
                return;
            }
            
            Log($"[AutoMarket] Found {retainerCount} retainers to process");
            
            var profitableQueue = new Queue<ScannedItem>(profitable);
            var unprofitableQueue = new Queue<ScannedItem>(unprofitable);
            
            for (int retainerIndex = 0; retainerIndex < retainerCount && !token.IsCancellationRequested; retainerIndex++)
            {
                if (profitableQueue.Count == 0 && unprofitableQueue.Count == 0)
                    break;
                
                StatusUpdate?.Invoke($"Processing Retainer {retainerIndex + 1}...");
                int currentListings = RetainerInteraction.GetRetainerMarketItemCount(retainerIndex);
                if (currentListings >= MaxMarketListings)
                {
                    Log($"[AutoMarket] Retainer {retainerIndex + 1} is already at max listings ({currentListings}/{MaxMarketListings}); skipping sell flow to avoid full-retainer modal.");
                    DropCurrentRetainerItems(profitableQueue, retainerIndex);
                    DropCurrentRetainerItems(unprofitableQueue, retainerIndex);
                    continue;
                }

                Log($"[AutoMarket] Opening Retainer {retainerIndex + 1}");
                
                await SimulateRetainerInteraction(retainerIndex, profitableQueue, unprofitableQueue, token);
                
                await Task.Delay((int)(Plugin.Configuration.RetainerDelay * 1.1), token);
            }
        }

        public async Task<int> ScanAllRetainerInventoriesForMarketScan(CancellationToken token)
        {
            int totalItems = 0;
            int retainerCount = RetainerInteraction.GetRetainerCount();

            if (retainerCount == 0)
            {
                Log("[AutoMarket] Retainer scan skipped: no retainers found. Open a summoning bell if retainers are unavailable.");
                return 0;
            }

            Log($"[AutoMarket] Scanning {retainerCount} retainer(s) for market scan");

            for (int retainerIndex = 0; retainerIndex < retainerCount && !token.IsCancellationRequested; retainerIndex++)
            {
                StatusUpdate?.Invoke($"Scanning Retainer {retainerIndex + 1}...");
                int currentListings = RetainerInteraction.GetRetainerMarketItemCount(retainerIndex);
                if (currentListings >= MaxMarketListings)
                {
                    Log($"[AutoMarket] Retainer {retainerIndex + 1} scan skipped: market listings are full ({currentListings}/{MaxMarketListings}), and the sell flow can hang on full retainers.");
                    continue;
                }

                Log($"[AutoMarket] Opening Retainer {retainerIndex + 1} for scan");

                var success = await RetainerInteraction.OpenAndSelectRetainer(retainerIndex, token);
                if (!success)
                {
                    LogError($"[AutoMarket] Failed to open retainer {retainerIndex + 1} for scan");
                    continue;
                }

                await Task.Delay(Math.Max(500, Plugin.Configuration.RetainerDelay), token);

                var retainerItems = await Scanner.ScanCurrentRetainerInventory(retainerIndex, token, fetchMarketData: false);
                totalItems += retainerItems.Count;
                Log($"[AutoMarket] Retainer {retainerIndex + 1} scan found {retainerItems.Count} item stack(s)");

                await ReturnToRetainerListAfterRetainerWork(false, token);
                await Task.Delay(Math.Max(500, Plugin.Configuration.RetainerDelay), token);
            }

            StatusUpdate?.Invoke("Ready");
            return totalItems;
        }
        
        private async Task SimulateRetainerInteraction(int retainerIndex, Queue<ScannedItem> profitable, Queue<ScannedItem> unprofitable, CancellationToken token)
        {
            // Step 1: Open and select the retainer from the RetainerList
            var success = await RetainerInteraction.OpenAndSelectRetainer(retainerIndex, token);
            if (!success)
            {
                LogError($"[AutoMarket] Failed to open retainer {retainerIndex}");
                return;
            }
            
            // Step 2: Wait for RetainerSell window and list items
            var maxListings = MaxMarketListings;

            var retainerInventoryItems = await Scanner.ScanCurrentRetainerInventory(retainerIndex, token);

            if (retainerInventoryItems.Count > 0)
            {
                var queuedProfitable = profitable.Count(item => item.SourceRetainerIndex == retainerIndex);
                var queuedUnprofitable = unprofitable.Count(item => item.SourceRetainerIndex == retainerIndex);
                Log($"[AutoMarket] Retainer {retainerIndex + 1} inventory scan found {retainerInventoryItems.Count} item stack(s); queued from full scan: {queuedProfitable} to list, {queuedUnprofitable} to vendor");
            }

            // Read the retainer's current listing count ONCE — this initial read from RetainerManager
            // is accurate (loaded when the retainer bell was accessed). Mid-session reads are stale
            // because MarketItemCount doesn't update as items are listed. We seed SessionListingCount
            // here and ItemListing increments it per successful batch.
            int currentListings = RetainerInteraction.GetRetainerMarketItemCount(retainerIndex);
            ItemListing.SessionListingCount = currentListings;
            Log($"[AutoMarket] Retainer {retainerIndex} currently has {currentListings} items listed on market board");

            int availableSlots = maxListings - currentListings;
            int eligibleProfitable = CountEligibleForRetainer(profitable, retainerIndex);
            int itemsToAttempt = Math.Min(eligibleProfitable, availableSlots);

            if (availableSlots <= 0)
            {
                Log($"[AutoMarket] Retainer {retainerIndex} is already at max listings ({currentListings}/{maxListings}). Moving to next retainer.");
                DropCurrentRetainerItems(unprofitable, retainerIndex);
                await ReturnToRetainerListAfterRetainerWork(false, token);
                LastRunSummary.TotalItems = LastRunSummary.ItemsListed + LastRunSummary.ItemsVendored;
                return;
            }
            else
            {
                Log($"[AutoMarket] Can list {itemsToAttempt} items on retainer {retainerIndex} ({availableSlots} slots available, {eligibleProfitable} eligible items in queue)");
            }
            
            // Add delay after selecting "Sell items" before starting to list items
            await Task.Delay(660, token);
            
            // Step 2.5: Manage listed items if enabled (before processing inventory items)
            // This adjusts prices for currently listed items by undercutting the market
            if (Plugin.Configuration.ManageListedItems)
            {
                Log($"[AutoMarket] Adjusting prices for listed items on retainer {retainerIndex}...");
                await ItemListing.ManageListedItems(retainerIndex, token);
            }
            
            int itemsListedThisRetainer = 0;
            while (TryMoveNextEligibleToFront(profitable, retainerIndex) && itemsListedThisRetainer < itemsToAttempt && !token.IsCancellationRequested)
            {
                var item = profitable.Peek(); // Peek to check before dequeueing
                
                // Use SessionListingCount (locally tracked) — game memory is stale mid-session
                if (ItemListing.SessionListingCount >= maxListings)
                {
                    Log($"[AutoMarket] Retainer {retainerIndex} reached max listings ({ItemListing.SessionListingCount}/{maxListings}) during listing. Moving to next retainer.");
                    break;
                }
                
                // Store original quantity before listing (in case item is listed in batches)
                int originalQuantity = item.Quantity;
                
                // Try to list the item (pass retainer index and max listings for limit checking during batching)
                success = await ItemListing.ListItemOnMarket(item, token, retainerIndex, maxListings);
                if (!success)
                {
                    LogError($"[AutoMarket] Failed to list item {item.ItemName} (ID: {item.ItemId})");
                    // Remove failed item and continue
                    profitable.Dequeue();
                    continue;
                }
                
                // Successfully listed - calculate how many were actually listed
                int itemsListed = originalQuantity - item.Quantity; // item.Quantity now contains remaining quantity
                
                // Check if item still has remaining quantity (hit retainer limit during batching)
                if (item.Quantity > 0)
                {
                    // Item was partially listed - keep it in queue for next retainer
                    Log($"[AutoMarket] Partially listed {item.ItemName}: {itemsListed} listed, {item.Quantity} remaining (retainer {retainerIndex} at limit)");
                    // Don't dequeue - item stays in queue for next retainer
                    // Don't increment itemsListedThisRetainer since we're not done with this item
                }
                else
                {
                    // Fully listed - remove from queue
                    profitable.Dequeue();
                    itemsListedThisRetainer++;
                }
                
                StatusUpdate?.Invoke($"Listed {itemsListed} of {item.ItemName ?? "Item#" + item.ItemId} for {item.ListingPrice:N0} gil each");
                
                LastRunSummary.ItemsListed += itemsListed;
                LastRunSummary.EstimatedRevenue += item.ListingPrice * itemsListed;
                
                // If item still has remaining quantity, we hit the retainer limit - move to next retainer
                if (item.Quantity > 0)
                {
                    Log($"[AutoMarket] Retainer {retainerIndex} reached listing limit. Moving to next retainer to continue listing {item.ItemName}");
                    break; // Exit loop to move to next retainer
                }
                
                // Delay between listings — use SessionListingCount (locally tracked, not stale game memory)
                await Task.Delay(330, token);
                if (ItemListing.SessionListingCount > 1)
                {
                    await Task.Delay((int)(Plugin.Configuration.ActionDelay * 2 * 1.1), token);
                }
                else
                {
                    await Task.Delay((int)(Plugin.Configuration.ActionDelay * 1.1), token);
                }
                
                // Check for pause
                while (IsPaused && !token.IsCancellationRequested)
                {
                    await Task.Delay(66);
                }
            }
            
            // Process unprofitable items (vendor them)
            bool weVendored = false; // Track if we vendored any items during this retainer session
            while (TryMoveNextEligibleToFront(unprofitable, retainerIndex) && !token.IsCancellationRequested)
            {
                var item = unprofitable.Peek(); // Peek to check before dequeueing
                
                // Try to vendor the item
                success = await ItemListing.VendorItem(item, token);
                if (!success)
                {
                    LogError($"[AutoMarket] Failed to vendor item {item.ItemName} (ID: {item.ItemId})");
                    // Remove failed item and continue
                    unprofitable.Dequeue();
                    continue;
                }
                
                // Successfully vendored
                unprofitable.Dequeue();
                weVendored = true; // Mark that we vendored at least one item
                StatusUpdate?.Invoke($"Vendored {item.ItemName ?? "Item#" + item.ItemId} for {item.VendorPrice:N0} gil");
                
                LastRunSummary.ItemsVendored++;
                LastRunSummary.EstimatedRevenue += item.VendorPrice * item.Quantity;
                
                // Delay between vendoring actions
                await Task.Delay(Plugin.Configuration.ActionDelay, token);
                
                // Check for pause
                while (IsPaused && !token.IsCancellationRequested)
                {
                    await Task.Delay(66);
                }
            }

            DropCurrentRetainerItems(unprofitable, retainerIndex);
            
            // Close retainer windows if we have more items for next retainer OR if retainer is full
            bool needsNextRetainer = (profitable.Count > 0 || unprofitable.Count > 0) || ItemListing.SessionListingCount >= maxListings;

            if (needsNextRetainer)
            {
                Log($"[AutoMarket] Closing retainer {retainerIndex} - {profitable.Count} profitable, {unprofitable.Count} unprofitable items remaining, {ItemListing.SessionListingCount}/{maxListings} listings");
                await ReturnToRetainerListAfterRetainerWork(weVendored, token);
            }
            
            LastRunSummary.TotalItems = LastRunSummary.ItemsListed + LastRunSummary.ItemsVendored;
        }

        private async Task ReturnToRetainerListAfterRetainerWork(bool weVendored, CancellationToken token)
        {
            await ItemListing.CloseRetainerWindow(weVendored, token);
            await ItemListing.CloseRetainerList(weVendored, token);
        }

        private static void DropCurrentRetainerItems(Queue<ScannedItem> queue, int retainerIndex)
        {
            if (queue.Count == 0)
                return;

            var kept = queue.Where(item => item.SourceRetainerIndex != retainerIndex).ToList();
            queue.Clear();
            foreach (var item in kept)
                queue.Enqueue(item);
        }

        private static bool IsEligibleForRetainer(ScannedItem item, int retainerIndex)
        {
            return item.SourceRetainerIndex == null || item.SourceRetainerIndex == retainerIndex;
        }

        private static int CountEligibleForRetainer(Queue<ScannedItem> queue, int retainerIndex)
        {
            return queue.Count(item => IsEligibleForRetainer(item, retainerIndex));
        }

        private static bool TryMoveNextEligibleToFront(Queue<ScannedItem> queue, int retainerIndex)
        {
            if (queue.Count == 0)
                return false;

            var count = queue.Count;
            for (var i = 0; i < count; i++)
            {
                var item = queue.Peek();
                if (IsEligibleForRetainer(item, retainerIndex))
                    return true;

                queue.Enqueue(queue.Dequeue());
            }

            return false;
        }
        
        public async Task StartClearCycle()
        {
            if (IsRunning) return;

            IsRunning = true;
            IsPaused = false;
            AutomationToken = new CancellationTokenSource();

            var config = Plugin.Configuration;
            bool toRetainerBag = config.ClearReturnToRetainerInventory;
            string destination = toRetainerBag ? "retainer bag" : "player inventory";

            Plugin?.PrintChat($"[AutoMarket] Starting retainer clear (destination: {destination})...");

            try
            {
                StatusUpdate?.Invoke("Starting clear...");
                Log("[AutoMarket] Starting clear cycle");

                int retainerCount = RetainerInteraction.GetRetainerCount();
                if (retainerCount == 0)
                {
                    LogError("[AutoMarket] No retainers found");
                    return;
                }

                int totalWithdrawn = 0;
                bool stoppedEarly = false;

                for (int retainerIndex = 0; retainerIndex < retainerCount && !AutomationToken.Token.IsCancellationRequested; retainerIndex++)
                {
                    // Opt-out: skip retainers the user has explicitly excluded
                    if (config.ClearExcludedRetainers.Contains(retainerIndex)) continue;

                    StatusUpdate?.Invoke($"Clearing Retainer {retainerIndex + 1}...");
                    Log($"[AutoMarket] Opening Retainer {retainerIndex + 1} for clear");

                    var success = await RetainerInteraction.OpenAndSelectRetainer(retainerIndex, AutomationToken.Token);
                    if (!success)
                    {
                        LogError($"[AutoMarket] Failed to open retainer {retainerIndex + 1} for clearing");
                        continue;
                    }

                    await Task.Delay(660, AutomationToken.Token);

                    var (withdrawn, spaceFull) = await ItemListing.ClearAllListedItems(retainerIndex, toRetainerBag, AutomationToken.Token);
                    totalWithdrawn += withdrawn;

                    if (spaceFull)
                    {
                        Plugin?.PrintChat($"[AutoMarket] {(toRetainerBag ? "Retainer bag" : "Inventory")} is full — stopped clearing early.");
                        stoppedEarly = true;
                    }

                    // Close windows if there are more retainers to process, or we stopped early
                    bool moreRetainers = !stoppedEarly && retainerIndex < retainerCount - 1
                        && Enumerable.Range(retainerIndex + 1, retainerCount - retainerIndex - 1)
                                     .Any(r => !config.ClearExcludedRetainers.Contains(r));
                    if (moreRetainers || stoppedEarly)
                    {
                        await ItemListing.CloseRetainerWindow(false, AutomationToken.Token);
                        await ItemListing.CloseRetainerList(false, AutomationToken.Token);
                    }

                    if (stoppedEarly) break;

                    await Task.Delay((int)(Plugin.Configuration.RetainerDelay * 1.1), AutomationToken.Token);
                }

                if (!stoppedEarly)
                {
                    Plugin?.PrintChat($"[AutoMarket] Clear complete! Withdrew {totalWithdrawn} item(s) to {destination}.");
                }

                StatusUpdate?.Invoke("Clear complete!");
                Log($"[AutoMarket] Clear cycle done — withdrew {totalWithdrawn} item(s) total");
            }
            catch (OperationCanceledException)
            {
                StatusUpdate?.Invoke("Clear stopped");
            }
            catch (Exception ex)
            {
                LogError("[AutoMarket] Clear error", ex);
                StatusUpdate?.Invoke($"Error: {ex.Message}");
                Plugin?.PrintChat($"[AutoMarket] Clear failed: {ex.Message}");
            }
            finally
            {
                IsRunning = false;
                IsPaused = false;
                AutomationToken?.Dispose();
                AutomationToken = null;
            }
        }

        public void StopAutomation()
        {
            AutomationToken?.Cancel();
            PostedGilToken?.Cancel();
            IsRunning = false;
            IsPaused = false;
            PostedGilRefreshRunning = false;
            StatusUpdate?.Invoke("Automation stopped");
        }
        
        public void PauseAutomation()
        {
            IsPaused = !IsPaused;
            StatusUpdate?.Invoke(IsPaused ? "Automation paused" : "Automation resumed");
        }
        
        public RunSummary GetLastRunSummary() => LastRunSummary;

        public PostedGilSummary GetPostedGilSummary() => LastPostedGilSummary;

        public async Task RefreshPostedGilSummary()
        {
            if (IsRunning || PostedGilRefreshRunning)
                return;

            PostedGilRefreshRunning = true;
            PostedGilToken = new CancellationTokenSource();
            var token = PostedGilToken.Token;
            var summary = new PostedGilSummary { LastUpdated = DateTime.Now };

            try
            {
                StatusUpdate?.Invoke("Refreshing posted gil...");
                Log("[AutoMarket] Refreshing posted marketboard gil");

                int retainerCount = RetainerInteraction.GetRetainerCount();
                summary.RetainersChecked = retainerCount;

                for (int retainerIndex = 0; retainerIndex < retainerCount && !token.IsCancellationRequested; retainerIndex++)
                {
                    StatusUpdate?.Invoke($"Reading listings {retainerIndex + 1}/{retainerCount}...");
                    int currentListings = RetainerInteraction.GetRetainerMarketItemCount(retainerIndex);
                    if (currentListings >= MaxMarketListings)
                    {
                        summary.RetainersFailed++;
                        Log($"[AutoMarket] Posted gil refresh skipped Retainer {retainerIndex + 1}: market listings are full ({currentListings}/{MaxMarketListings}), and the sell flow can hang on full retainers.");
                        continue;
                    }

                    Log($"[AutoMarket] Opening Retainer {retainerIndex + 1} to total listed market value");

                    var success = await RetainerInteraction.OpenAndSelectRetainer(retainerIndex, token);
                    if (!success)
                    {
                        summary.RetainersFailed++;
                        LogError($"[AutoMarket] Failed to open retainer {retainerIndex + 1} for posted gil refresh");
                        continue;
                    }

                    await Task.Delay(Math.Max(500, Plugin.Configuration.RetainerDelay), token);

                    var listedItems = ItemListing.GetListedItems(retainerIndex);
                    summary.Listings += listedItems.Count;
                    summary.PostedGil += listedItems.Sum(item => (long)item.ListingPrice * Math.Max(1, item.Quantity));

                    await ReturnToRetainerListAfterRetainerWork(false, token);
                    await Task.Delay(Math.Max(500, Plugin.Configuration.RetainerDelay), token);
                }

                LastPostedGilSummary = summary;
                StatusUpdate?.Invoke("Ready");
                Plugin?.PrintChat($"[AutoMarket] Posted marketboard value: {summary.PostedGil:N0} gil across {summary.Listings} listing(s).");
            }
            catch (OperationCanceledException)
            {
                StatusUpdate?.Invoke("Posted gil refresh stopped");
            }
            catch (Exception ex)
            {
                LogError("[AutoMarket] Posted gil refresh error", ex);
                StatusUpdate?.Invoke($"Error: {ex.Message}");
            }
            finally
            {
                PostedGilRefreshRunning = false;
                PostedGilToken?.Dispose();
                PostedGilToken = null;
            }
        }
        
        public void Dispose()
        {
            AutomationToken?.Cancel();
            AutomationToken?.Dispose();
            PostedGilToken?.Cancel();
            PostedGilToken?.Dispose();
        }
    }

    public class PostedGilSummary
    {
        public long PostedGil { get; set; }
        public int Listings { get; set; }
        public int RetainersChecked { get; set; }
        public int RetainersFailed { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public bool HasValue => LastUpdated != DateTime.MinValue;
    }
}
