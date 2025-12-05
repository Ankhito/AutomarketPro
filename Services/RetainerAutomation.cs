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
        private readonly AutomarketPro.AutomarketProPlugin Plugin;
        private readonly MarketScanner Scanner;
        private readonly RetainerInteraction RetainerInteraction;
        private readonly ItemListing ItemListing;
        
        private bool IsRunning = false;
        private bool IsPaused = false;
        private CancellationTokenSource? AutomationToken;
        private RunSummary LastRunSummary = new();
        
        public bool Running => IsRunning;
        public bool Paused => IsPaused;
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
                    // List Only Mode: List ALL items (profitable and unprofitable)
                    itemsToList = Scanner.Items.ToList();
                    Log($"[AutoMarket] List Only Mode enabled - will list all {itemsToList.Count} items");
                }
                else if (config.VendorOnlyMode)
                {
                    // Vendor Only Mode: Vendor ALL items (profitable and unprofitable)
                    itemsToVendor = Scanner.Items.ToList();
                    Log($"[AutoMarket] Vendor Only Mode enabled - will vendor all {itemsToVendor.Count} items");
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
                Log($"[AutoMarket] Opening Retainer {retainerIndex + 1}");
                
                await SimulateRetainerInteraction(retainerIndex, profitableQueue, unprofitableQueue, token);
                
                await Task.Delay((int)(Plugin.Configuration.RetainerDelay * 1.1), token);
            }
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
            var maxListings = 20;
            
            // Get current listing count from retainer (may already have items listed)
            int currentListings = RetainerInteraction.GetRetainerMarketItemCount(retainerIndex);
            Log($"[AutoMarket] Retainer {retainerIndex} currently has {currentListings} items listed on market board");
            
            // Calculate how many items we can actually list (better approach - only list what we can)
            int availableSlots = maxListings - currentListings;
            int itemsToAttempt = Math.Min(profitable.Count, availableSlots);
            
            if (availableSlots <= 0)
            {
                Log($"[AutoMarket] Retainer {retainerIndex} is already at max listings ({currentListings}/{maxListings}). Moving to next retainer.");
            }
            else
            {
                Log($"[AutoMarket] Can list {itemsToAttempt} items on retainer {retainerIndex} ({availableSlots} slots available, {profitable.Count} items in queue)");
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
            while (profitable.Count > 0 && itemsListedThisRetainer < itemsToAttempt && !token.IsCancellationRequested)
            {
                var item = profitable.Peek(); // Peek to check before dequeueing
                
                // Double-check current listings before attempting (in case something changed)
                currentListings = RetainerInteraction.GetRetainerMarketItemCount(retainerIndex);
                if (currentListings >= maxListings)
                {
                    Log($"[AutoMarket] Retainer {retainerIndex} reached max listings ({currentListings}/{maxListings}) during listing. Moving to next retainer.");
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
                
                // Refresh current listings count after successful listing
                await Task.Delay(330, token);
                currentListings = RetainerInteraction.GetRetainerMarketItemCount(retainerIndex);
                
                // Delay between listings - make it longer if we already have items listed
                if (currentListings > 1)
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
            while (unprofitable.Count > 0 && !token.IsCancellationRequested)
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
            
            // Close retainer windows if we have more items for next retainer OR if retainer is full
            bool needsNextRetainer = (profitable.Count > 0 || unprofitable.Count > 0) || currentListings >= maxListings;
            
            if (needsNextRetainer)
            {
                Log($"[AutoMarket] Closing retainer {retainerIndex} - {profitable.Count} profitable, {unprofitable.Count} unprofitable items remaining, {currentListings}/{maxListings} listings");
                
                // Close RetainerSell window first (pass weVendored flag)
                await ItemListing.CloseRetainerWindow(weVendored, token);
                
                // Close RetainerList window to return to retainer selection (pass weVendored flag)
                await ItemListing.CloseRetainerList(weVendored, token);
            }
            
            LastRunSummary.TotalItems = LastRunSummary.ItemsListed + LastRunSummary.ItemsVendored;
        }
        
        public void StopAutomation()
        {
            AutomationToken?.Cancel();
            IsRunning = false;
            IsPaused = false;
            StatusUpdate?.Invoke("Automation stopped");
        }
        
        public void PauseAutomation()
        {
            IsPaused = !IsPaused;
            StatusUpdate?.Invoke(IsPaused ? "Automation paused" : "Automation resumed");
        }
        
        public RunSummary GetLastRunSummary() => LastRunSummary;
        
        public void Dispose()
        {
            AutomationToken?.Cancel();
            AutomationToken?.Dispose();
        }
    }
}
