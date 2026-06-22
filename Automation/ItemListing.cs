using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ECommons;
using AutomarketPro.Models;
using AutomarketPro.Services;
using InventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager;
using AgentInventoryContext = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace AutomarketPro.Automation
{
    /// <summary>
    /// Handles item listing automation - opening context menus, clicking buttons, getting prices, etc.
    /// </summary>
    public class ItemListing
    {
        // AutomarketProPlugin is in AutomarketPro namespace (will be moved to Core later)
        private readonly AutomarketPro.AutomarketProPlugin Plugin;
        
        // Logging delegates - will be set by RetainerAutomation
        public Action<string>? Log { get; set; }
        public Action<string, Exception?>? LogError { get; set; }
        
        // Callback to check retainer listing count (set by RetainerAutomation)
        public Func<int, int>? GetRetainerListingCount { get; set; }

        // Tracks listings made this retainer session. Set to the retainer's initial listing count
        // by RetainerAutomation before listing begins, then incremented per successful batch here.
        // Game memory (MarketItemCount) does not update mid-session, so we must track locally.
        public int SessionListingCount { get; set; } = 0;
        
        public ItemListing(AutomarketPro.AutomarketProPlugin plugin)
        {
            Plugin = plugin;
        }

        /// <summary>
        /// Safely executes UI operations on the framework thread with proper exception handling.
        /// </summary>
        private async Task<bool> RunOnFrameworkThreadAsync(Func<bool> action)
        {
            try
            {
                bool result = false;
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    try
                    {
                        result = action();
                    }
                    catch (System.AccessViolationException ex)
                    {
                        LogError?.Invoke("Access violation in UI operation", ex);
                        result = false;
                    }
                    catch (Exception ex)
                    {
                        LogError?.Invoke("Error in UI operation", ex);
                        result = false;
                    }
                });
                return result;
            }
            catch (Exception ex)
            {
                LogError?.Invoke("Error running on framework thread", ex);
                return false;
            }
        }

        /// <summary>
        /// Safely executes UI operations on the framework thread (void return).
        /// </summary>
        private async Task RunOnFrameworkThreadAsync(Action action)
        {
            try
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (System.AccessViolationException ex)
                    {
                        LogError?.Invoke("Access violation in UI operation", ex);
                    }
                    catch (Exception ex)
                    {
                        LogError?.Invoke("Error in UI operation", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                LogError?.Invoke("Error running on framework thread", ex);
            }
        }

        /// <summary>
        /// Safely gets InventoryManager with retry logic (up to 5 attempts).
        /// Returns null if all attempts fail.
        /// </summary>
        private unsafe InventoryManager* GetInventoryManagerSafe()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var manager = InventoryManager.Instance();
                    if (manager != null)
                    {
                        return manager;
                    }
                }
                catch
                {
                    // Continue to next attempt
                }
                
                if (attempt < 4)
                {
                    System.Threading.Thread.Sleep(10); // Small delay between attempts
                }
            }
            return null;
        }

        /// <summary>
        /// Safely gets AgentInventoryContext with retry logic (up to 5 attempts).
        /// Returns null if all attempts fail.
        /// </summary>
        private unsafe AgentInventoryContext* GetAgentInventoryContextSafe()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var agent = AgentInventoryContext.Instance();
                    if (agent != null)
                    {
                        return agent;
                    }
                }
                catch
                {
                    // Continue to next attempt
                }
                
                if (attempt < 4)
                {
                    System.Threading.Thread.Sleep(10); // Small delay between attempts
                }
            }
            return null;
        }



        /// <summary>
        /// Safely gets an inventory container with re-validation. Re-validates InventoryManager before each attempt.
        /// Use this when the operation happens after a delay or in a loop where the pointer might become stale.
        /// </summary>
        private unsafe FFXIVClientStructs.FFXIV.Client.Game.InventoryContainer* GetInventoryContainerSafe(InventoryType inventoryType, int maxAttempts = 5)
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    var inventoryManager = GetInventoryManagerSafe();
                    if (inventoryManager != null)
                    {
                        var container = inventoryManager->GetInventoryContainer(inventoryType);
                        if (container != null)
                        {
                            return container;
                        }
                    }
                }
                catch
                {
                    // Continue to next attempt
                }
                
                if (attempt < maxAttempts - 1)
                {
                    System.Threading.Thread.Sleep(10);
                }
            }
            return null;
        }

        /// <summary>
        /// Safely gets an inventory slot with re-validation. Re-validates InventoryManager and Container before each attempt.
        /// Use this when the operation happens after a delay or in a loop where the pointer might become stale.
        /// </summary>
        private unsafe FFXIVClientStructs.FFXIV.Client.Game.InventoryItem* GetInventorySlotSafe(InventoryType inventoryType, int slot, int maxAttempts = 5)
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    var container = GetInventoryContainerSafe(inventoryType, 1); // Get container with single attempt (we're already retrying)
                    if (container != null && slot >= 0 && slot < container->Size)
                    {
                        var slotItem = container->GetInventorySlot(slot);
                        if (slotItem != null)
                        {
                            return slotItem;
                        }
                    }
                }
                catch
                {
                    // Continue to next attempt
                }
                
                if (attempt < maxAttempts - 1)
                {
                    System.Threading.Thread.Sleep(10);
                }
            }
            return null;
        }


        public async Task<bool> ListItemOnMarket(ScannedItem item, CancellationToken token, int? retainerIndex = null, int maxListings = 20)
        {
            try
            {
                uint lowestPrice = item.ListingPrice;
                bool skipComparePrices = Plugin.Configuration.DataCenterScan;
                
                // Handle items with quantity > 99 by listing in batches
                int startingQuantity = item.Quantity; // Track starting quantity for debugging
                int remainingQuantity = item.Quantity;
                int totalListed = 0; // Track total actually listed
                bool firstBatch = true;
                bool anyBatchSucceeded = false;
                
                // Track which inventory slot we're currently working with
                // Start with the slot from the scanned item, but may need to find new stacks as we deplete them
                InventoryType currentInventoryType = item.InventoryType;
                int currentInventorySlot = item.InventorySlot;
                
                while (remainingQuantity > 0 && !token.IsCancellationRequested)
                {
                    // Check limit using SessionListingCount — game memory (MarketItemCount) is stale
                    // mid-session and cannot be re-read reliably after listings are made.
                    if (retainerIndex.HasValue && SessionListingCount >= maxListings)
                    {
                        Log?.Invoke($"[AutoMarket] Retainer {retainerIndex.Value} reached max listings ({SessionListingCount}/{maxListings}). Cannot list more batches of {item.ItemName}. Remaining quantity: {remainingQuantity}");
                        item.Quantity = remainingQuantity;
                        item.InventoryType = currentInventoryType;
                        item.InventorySlot = currentInventorySlot;
                        return anyBatchSucceeded;
                    }
                    
                    // Check actual quantity in current slot before calculating batch quantity
                    // Use safe wrapper to re-validate pointer (may have changed since last check)
                    int actualSlotQuantity = 0;
                    unsafe
                    {
                        var slotItem = GetInventorySlotSafe(currentInventoryType, currentInventorySlot);
                        if (slotItem != null && slotItem->ItemId == item.ItemId)
                        {
                            actualSlotQuantity = slotItem->Quantity;
                        }
                    }
                    
                    // If current slot is empty, find the next stack
                    if (actualSlotQuantity <= 0)
                    {
                        Log?.Invoke($"[AutoMarket] Current slot ({currentInventoryType} slot {currentInventorySlot}) is empty. Finding next stack of {item.ItemName}...");
                        var (foundType, foundSlot) = FindNextStackOfItem(item, currentInventoryType, currentInventorySlot);
                        if (foundSlot >= 0)
                        {
                            currentInventoryType = foundType;
                            currentInventorySlot = foundSlot;
                            Log?.Invoke($"[AutoMarket] Found next stack at {currentInventoryType} slot {currentInventorySlot}");
                            
                            // Re-check quantity in the new slot - use safe wrapper to re-validate
                            unsafe
                            {
                                var slotItem = GetInventorySlotSafe(currentInventoryType, currentInventorySlot);
                                if (slotItem != null && slotItem->ItemId == item.ItemId)
                                {
                                    actualSlotQuantity = slotItem->Quantity;
                                }
                            }
                        }
                        else
                        {
                            LogError?.Invoke($"[AutoMarket] No more stacks found for {item.ItemName}. Expected {remainingQuantity} remaining but no stacks available.", null);
                            break; // Exit loop - no more stacks
                        }
                    }
                    
                    // Calculate quantity for this batch: min of (99 max per listing, remaining quantity needed, actual quantity in slot)
                    int batchQuantity = Math.Min(99, Math.Min(remainingQuantity, actualSlotQuantity));
                    
                    if (batchQuantity <= 0)
                    {
                        LogError?.Invoke($"[AutoMarket] Cannot list batch: batchQuantity={batchQuantity}, remainingQuantity={remainingQuantity}, actualSlotQuantity={actualSlotQuantity}", null);
                        break;
                    }
                    
                    if (!firstBatch)
                    {
                        Log?.Invoke($"[AutoMarket] Listing remaining {remainingQuantity} of {item.ItemName} (batch: {batchQuantity} from slot with {actualSlotQuantity})");
                    }
                    
                    // Add delay before opening context menu to ensure UI is stable
                    await Task.Delay(110, token);
                    
                    // Open context menu for the item
                    bool menuOpened = await OpenItemContextMenuForSlot(item, currentInventoryType, currentInventorySlot, token);
                    
                    if (!menuOpened)
                    {
                        LogError?.Invoke($"[AutoMarket] Failed to open context menu for {item.ItemName} at {currentInventoryType} slot {currentInventorySlot}", null);
                        break; // Exit loop if we can't open the menu
                    }
                    
                    // Add delay after opening context menu to ensure it's fully ready
                    await Task.Delay(110, token);
                    
                    if (!await ClickPutUpForSale(item, token))
                    {
                        LogError?.Invoke($"[AutoMarket] Failed to click 'Put Up for Sale' for {item.ItemName}", null);
                        break;
                    }
                    
                    await Task.Delay(66, token);
                    
                    // Only do price comparison on first batch (or if not using Data Center Scan)
                    bool priceFound = false;
                    if (firstBatch && !skipComparePrices)
                    {
                        // Only compare prices if Data Center Scan is not enabled and this is the first batch
                        for (int compareAttempt = 0; compareAttempt < 2; compareAttempt++)
                        {
                            if (compareAttempt > 0)
                            {
                                await Task.Delay(198, token);
                            }
                            
                            bool clickedCompare = false;
                            await RunOnFrameworkThreadAsync(() =>
                            {
                                unsafe
                                {
                                    try
                                    {
                                        // Re-validate before accessing
                                        if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonRetainerSell>("RetainerSell", out var retainerSellForCompare))
                                        {
                                            return;
                                        }
                                        
                                        if (!ECommons.GenericHelpers.IsAddonReady(&retainerSellForCompare->AtkUnitBase))
                                        {
                                            return;
                                        }
                                        
                                        ECommons.Automation.Callback.Fire(&retainerSellForCompare->AtkUnitBase, true, 4);
                                        clickedCompare = true;
                                    }
                                    catch (System.AccessViolationException ex)
                                    {
                                        LogError?.Invoke("Access violation clicking compare prices", ex);
                                    }
                                    catch (Exception ex)
                                    {
                                        LogError?.Invoke($"Error clicking compare prices: {ex.Message}", ex);
                                    }
                                }
                            });
                            
                            if (clickedCompare)
                            {
                                await Task.Delay(198, token);
                                var price = await GetLowestPriceFromComparePrices(item, token);
                                if (price > 0)
                                {
                                    var undercutAmount = Plugin.Configuration.UndercutAmount;
                                    lowestPrice = price > undercutAmount ? (uint)(price - undercutAmount) : 1;
                                    priceFound = true;
                                    break;
                                }
                            }
                        }
                    }
                    else if (firstBatch && skipComparePrices)
                    {
                        // Data Center Scan is enabled - use the cached price from EvaluateProfitability
                        priceFound = true; // Mark as found since we're using the pre-calculated price
                        Log?.Invoke($"[AutoMarket] Using cached data center price for {item.ItemName}: {lowestPrice} (skipping compare prices)");
                    }
                    else
                    {
                        // Subsequent batches - use the same price as first batch
                        priceFound = true;
                    }
                    
                    await Task.Delay(66, token);
                    
                    nint retainerSellPtr = nint.Zero;
                    bool retainerSellReady = false;
                    
                    for (int attempts = 0; attempts < 30; attempts++)
                    {
                        await Task.Delay(66, token);
                        
                        bool ready = await RunOnFrameworkThreadAsync(() =>
                        {
                            unsafe
                            {
                                try
                                {
                                    if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonRetainerSell>("RetainerSell", out var tempRetainerSell) 
                                        && tempRetainerSell != null
                                        && ECommons.GenericHelpers.IsAddonReady(&tempRetainerSell->AtkUnitBase))
                                    {
                                        retainerSellPtr = (nint)tempRetainerSell;
                                        return true;
                                    }
                                    return false;
                                }
                                catch (System.AccessViolationException)
                                {
                                    return false;
                                }
                                catch
                                {
                                    return false;
                                }
                            }
                        });
                        
                        if (ready)
                        {
                            retainerSellReady = true;
                            Log?.Invoke($"[AutoMarket] RetainerSell addon is ready (attempt {attempts + 1})");
                            break;
                        }
                    }
                    
                    if (!retainerSellReady)
                    {
                        LogError?.Invoke("[AutoMarket] RetainerSell addon not found or not ready after waiting 3 seconds", null);
                        break;
                    }
                    
                    bool success = await RunOnFrameworkThreadAsync(() =>
                    {
                        unsafe
                        {
                            try
                            {
                                // Re-validate RetainerSell addon before use (pointer may have become stale after delays)
                                if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonRetainerSell>("RetainerSell", out var retainerSell)
                                    || retainerSell == null
                                    || !ECommons.GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
                                {
                                    LogError?.Invoke("[AutoMarket] RetainerSell addon not found or not ready when trying to set price", null);
                                    return false;
                                }
                                
                                // Close ItemSearchResult if open
                                if (ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var itemSearchAddon)
                                    && itemSearchAddon != null
                                    && ECommons.GenericHelpers.IsAddonReady(itemSearchAddon))
                                {
                                    itemSearchAddon->Close(true);
                                }
                                
                                var ui = &retainerSell->AtkUnitBase;
                                if (ui == null)
                                {
                                    LogError?.Invoke("[AutoMarket] RetainerSell AtkUnitBase is null", null);
                                    return false;
                                }
                                
                                if (lowestPrice > 0)
                                {
                                    // Null check for AskingPrice before SetValue
                                    if (retainerSell->AskingPrice == null)
                                    {
                                        LogError?.Invoke("[AutoMarket] RetainerSell AskingPrice is null", null);
                                        return false;
                                    }
                                    
                                    retainerSell->AskingPrice->SetValue((int)lowestPrice);
                                    
                                    // Set quantity for this batch (only if > 1)
                                    if (batchQuantity > 1)
                                    {
                                        // Null check for Quantity before SetValue
                                        if (retainerSell->Quantity == null)
                                        {
                                            LogError?.Invoke("[AutoMarket] RetainerSell Quantity is null", null);
                                            return false;
                                        }
                                        
                                        retainerSell->Quantity->SetValue(batchQuantity);
                                    }
                                    
                                    ECommons.Automation.Callback.Fire(ui, true, 0);
                                    ui->Close(true);
                                    
                                    // Successfully listed this batch
                                    // Subtract the actual quantity we listed (batchQuantity, which is already capped by slot quantity)
                                    remainingQuantity -= batchQuantity;
                                    totalListed += batchQuantity;
                                    anyBatchSucceeded = true;
                                    firstBatch = false;
                                    
                                    Log?.Invoke($"[AutoMarket] Listed {batchQuantity} of {item.ItemName} from slot {currentInventoryType}:{currentInventorySlot} (slot had {actualSlotQuantity}, remaining total: {remainingQuantity}, total listed so far: {totalListed})");
                                    return true;
                                }
                                else
                                {
                                    LogError?.Invoke("[AutoMarket] No price to set", null);
                                    if (ui != null)
                                    {
                                        ECommons.Automation.Callback.Fire(ui, true, 1); // cancel
                                        ui->Close(true);
                                    }
                                    return false;
                                }
                            }
                            catch (System.AccessViolationException ex)
                            {
                                LogError?.Invoke("Access violation setting price", ex);
                                return false;
                            }
                            catch (Exception ex)
                            {
                                LogError?.Invoke($"[AutoMarket] Exception setting price and confirming: {ex.Message}", ex);
                                return false;
                            }
                        }
                    });
                    
                    if (!success)
                    {
                        break;
                    }

                    // Each successful batch uses exactly one listing slot — increment the shared counter
                    // so subsequent batches (and other items) see the updated count.
                    SessionListingCount++;

                    // Add delay after UI operations
                    await Task.Delay(100, token);

                    // If there's more to list, check if current slot is depleted and find next stack if needed
                    if (remainingQuantity > 0)
                    {
                        await Task.Delay(330, token); // Delay between batches
                        
                        // Verify current slot still has items, if not find next stack
                        // Use safe wrapper to re-validate pointer after delay
                        bool slotDepleted = false;
                        unsafe
                        {
                            var slotItem = GetInventorySlotSafe(currentInventoryType, currentInventorySlot);
                            if (slotItem == null || slotItem->ItemId != item.ItemId || slotItem->Quantity <= 0)
                            {
                                slotDepleted = true;
                            }
                        }
                        
                        if (slotDepleted)
                        {
                            // Current slot is depleted, find next stack
                            Log?.Invoke($"[AutoMarket] Current slot ({currentInventoryType} slot {currentInventorySlot}) is depleted. Finding next stack...");
                            var (foundType, foundSlot) = FindNextStackOfItem(item, currentInventoryType, currentInventorySlot);
                            if (foundSlot >= 0)
                            {
                                currentInventoryType = foundType;
                                currentInventorySlot = foundSlot;
                                Log?.Invoke($"[AutoMarket] Moving to next stack at {currentInventoryType} slot {currentInventorySlot}");
                            }
                            else
                            {
                                LogError?.Invoke($"[AutoMarket] No more stacks found for {item.ItemName}. Expected {remainingQuantity} remaining but no stacks available.", null);
                                break; // Exit loop - no more stacks
                            }
                        }
                    }
                }
                
                // Update item quantity to reflect what's remaining (should be 0 if all listed successfully)
                item.Quantity = remainingQuantity;
                // Update inventory location in case we moved to a different stack
                item.InventoryType = currentInventoryType;
                item.InventorySlot = currentInventorySlot;
                
                // Log summary for debugging
                if (remainingQuantity > 0)
                {
                    Log?.Invoke($"[AutoMarket] Listing complete for {item.ItemName}: {remainingQuantity} items remaining (started with {startingQuantity}, listed {totalListed}, math check: {startingQuantity} - {totalListed} = {startingQuantity - totalListed})");
                }
                else
                {
                    Log?.Invoke($"[AutoMarket] Successfully listed all {item.ItemName} (started with {startingQuantity}, listed {totalListed})");
                }
                
                return anyBatchSucceeded;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error listing item {item.ItemName}: {ex.Message}", ex);
                return false;
            }
        }
        
        /// <summary>
        /// Closes any existing context menu to prevent conflicts when opening a new one.
        /// Runs on framework thread for safety.
        /// </summary>
        private async Task<bool> CloseExistingContextMenu()
        {
            return await RunOnFrameworkThreadAsync(() =>
            {
                unsafe
                {
                    try
                    {
                        // Re-validate before accessing
                        if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon))
                        {
                            return false;
                        }
                        
                        if (!ECommons.GenericHelpers.IsAddonReady(&contextMenuAddon->AtkUnitBase))
                        {
                            return false;
                        }
                        
                        // Close the existing context menu by pressing Escape or clicking outside
                        contextMenuAddon->AtkUnitBase.Close(true);
                        return true;
                    }
                    catch (System.AccessViolationException)
                    {
                        // Ignore access violations when trying to close context menu
                        return false;
                    }
                    catch
                    {
                        // Ignore errors when trying to close context menu
                        return false;
                    }
                }
            });
        }

        /// <summary>
        /// Verifies that the retainer UI is in a valid state before opening context menus.
        /// Runs on framework thread for safety.
        /// </summary>
        private async Task<bool> IsRetainerUIReady()
        {
            return await RunOnFrameworkThreadAsync(() =>
            {
                unsafe
                {
                    try
                    {
                        // Check if RetainerSellList is open and ready (the main retainer inventory window)
                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerSellList", out var retainerSellList))
                        {
                            if (retainerSellList != null && ECommons.GenericHelpers.IsAddonReady(retainerSellList) && retainerSellList->IsVisible)
                            {
                                return true;
                            }
                        }
                        
                        // Also check if RetainerSell is open (the listing window)
                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerSell", out var retainerSell))
                        {
                            if (retainerSell != null && ECommons.GenericHelpers.IsAddonReady(retainerSell) && retainerSell->IsVisible)
                            {
                                return true;
                            }
                        }
                        
                        return false;
                    }
                    catch (System.AccessViolationException)
                    {
                        return false;
                    }
                    catch
                    {
                        return false;
                    }
                }
            });
        }

        /// <summary>
        /// Synchronous version of IsRetainerUIReady for use within framework thread context.
        /// </summary>
        private unsafe bool IsRetainerUIReadySync()
        {
            try
            {
                // Check if RetainerSellList is open and ready (the main retainer inventory window)
                if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerSellList", out var retainerSellList))
                {
                    if (retainerSellList != null && ECommons.GenericHelpers.IsAddonReady(retainerSellList) && retainerSellList->IsVisible)
                    {
                        return true;
                    }
                }
                
                // Also check if RetainerSell is open (the listing window)
                if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerSell", out var retainerSell))
                {
                    if (retainerSell != null && ECommons.GenericHelpers.IsAddonReady(retainerSell) && retainerSell->IsVisible)
                    {
                        return true;
                    }
                }
                
                return false;
            }
            catch (System.AccessViolationException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Opens context menu for a specific inventory slot. Verifies the slot has the correct item and quantity.
        /// Includes safety checks to prevent crashes.
        /// </summary>
        private async Task<bool> OpenItemContextMenuForSlot(ScannedItem item, InventoryType inventoryType, int slot, CancellationToken token)
        {
            try
            {
                // Step 1: Verify retainer UI is ready
                bool uiReady = await IsRetainerUIReady();
                if (!uiReady)
                {
                    LogError?.Invoke("[AutoMarket] Retainer UI is not ready - cannot open context menu", null);
                    return false;
                }
                
                // Step 2: Close any existing context menu to prevent conflicts
                bool closedMenu = await CloseExistingContextMenu();
                if (closedMenu)
                {
                    Log?.Invoke("[AutoMarket] Closed existing context menu before opening new one");
                    await Task.Delay(110, token); // Small delay after closing
                }
                
                // Step 3: Verify inventory state
                bool inventoryValid = false;
                unsafe
                {
                    var inventoryManager = GetInventoryManagerSafe();
                    if (inventoryManager == null)
                    {
                        LogError?.Invoke("[AutoMarket] InventoryManager is null after retries", null);
                        return false;
                    }
                    
                    // Verify the slot has the correct item
                    var container = inventoryManager->GetInventoryContainer(inventoryType);
                    if (container == null)
                    {
                        LogError?.Invoke($"[AutoMarket] Container {inventoryType} is null", null);
                        return false;
                    }
                    
                    if (slot < 0 || slot >= container->Size)
                    {
                        LogError?.Invoke($"[AutoMarket] Invalid slot {slot} for {inventoryType}", null);
                        return false;
                    }
                    
                    var slotItem = container->GetInventorySlot(slot);
                    if (slotItem == null || slotItem->ItemId != item.ItemId)
                    {
                        // Slot doesn't have the item (may have been depleted)
                        return false;
                    }
                    
                    // Check if slot has enough quantity (at least 1)
                    if (slotItem->Quantity <= 0)
                    {
                        return false;
                    }
                    
                    inventoryValid = true;
                }
                
                if (!inventoryValid)
                {
                    return false;
                }
                
                // Step 4: Verify AgentInventoryContext is in a valid state and open context menu
                bool agentValid = false;
                nint agentPtr = nint.Zero;
                unsafe
                {
                    var agent = GetAgentInventoryContextSafe();
                    if (agent == null)
                    {
                        LogError?.Invoke("[AutoMarket] AgentInventoryContext is null after retries", null);
                        return false;
                    }
                    agentPtr = (nint)agent;
                    agentValid = true;
                }
                
                // Step 5: Add a small delay to ensure UI is stable (before opening)
                await Task.Delay(55, token);
                
                // Step 6: Double-check that no context menu is open (race condition check)
                await RunOnFrameworkThreadAsync(() =>
                    {
                        unsafe
                        {
                            try
                            {
                                if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var existingMenu))
                                {
                                    if (ECommons.GenericHelpers.IsAddonReady(&existingMenu->AtkUnitBase))
                                    {
                                        Log?.Invoke("[AutoMarket] Context menu still open, closing it...");
                                        existingMenu->AtkUnitBase.Close(true);
                                    }
                                }
                            }
                            catch (System.AccessViolationException)
                            {
                                // Ignore access violations
                            }
                            catch { }
                        }
                    });
                    
                    // Step 7: Open context menu using AgentInventoryContext
                    await RunOnFrameworkThreadAsync(() =>
                    {
                        unsafe
                        {
                            try
                            {
                                var inventoryManager = InventoryManager.Instance();
                                if (inventoryManager != null)
                                {
                                    var container = inventoryManager->GetInventoryContainer(inventoryType);
                                    if (container != null)
                                    {
                                        var slotItem = container->GetInventorySlot(slot);
                                        if (slotItem != null)
                                        {
                                            Log?.Invoke($"[AutoMarket] Opening context menu for {item.ItemName} at {inventoryType} slot {slot} (quantity: {slotItem->Quantity})");
                                        }
                                    }
                                }
                                if (agentPtr == nint.Zero) return;
                                var agent = (AgentInventoryContext*)agentPtr.ToPointer();
                                if (agent == null) return;
                                agent->OpenForItemSlot(inventoryType, slot, 0, 0);
                            }
                            catch (System.AccessViolationException ex)
                            {
                                LogError?.Invoke("Access violation opening context menu", ex);
                            }
                            catch (Exception ex)
                            {
                                LogError?.Invoke($"[AutoMarket] Error opening context menu: {ex.Message}", ex);
                            }
                        }
                    });
                
                if (!agentValid)
                {
                    return false;
                }
                
                // Step 5: Add a small delay to ensure UI is stable (after opening)
                await Task.Delay(55, token);
                
                // Step 6 delay: If we closed a menu, wait a bit
                // (Already handled in Step 6 above, but add delay here too)
                await Task.Delay(55, token);
                
                // Step 8: Wait for context menu to appear with validation
                for (int attempts = 0; attempts < 10; attempts++)
                {
                    await Task.Delay(33, token);
                    
                    bool menuReady = await RunOnFrameworkThreadAsync(() =>
                    {
                        unsafe
                        {
                            try
                            {
                                if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon))
                                {
                                    if (contextMenuAddon != null && ECommons.GenericHelpers.IsAddonReady(&contextMenuAddon->AtkUnitBase))
                                    {
                                        Log?.Invoke($"[AutoMarket] Context menu opened successfully (attempt {attempts + 1})");
                                        return true;
                                    }
                                }
                                return false;
                            }
                            catch (System.AccessViolationException)
                            {
                                return false;
                            }
                            catch
                            {
                                return false;
                            }
                        }
                    });
                    
                    if (menuReady)
                    {
                        return true;
                    }
                }
                
                LogError?.Invoke("[AutoMarket] Context menu did not appear after opening", null);
                return false;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error opening context menu for slot: {ex.Message}", ex);
                return false;
            }
        }
        
        /// <summary>
        /// Finds the next stack of the same item after the given slot. Returns (InventoryType, slot) or (-1, -1) if not found.
        /// </summary>
        private static InventoryType[] GetInventoryTypesForItem(ScannedItem item) =>
            item.SourceRetainerIndex.HasValue
                ?
                [
                    InventoryType.RetainerPage1,
                    InventoryType.RetainerPage2,
                    InventoryType.RetainerPage3,
                    InventoryType.RetainerPage4,
                    InventoryType.RetainerPage5,
                    InventoryType.RetainerPage6,
                    InventoryType.RetainerPage7
                ]
                :
                [
                    InventoryType.Inventory1,
                    InventoryType.Inventory2,
                    InventoryType.Inventory3,
                    InventoryType.Inventory4
                ];

        private (InventoryType type, int slot) FindNextStackOfItem(ScannedItem item, InventoryType afterType, int afterSlot)
        {
            try
            {
                unsafe
                {
                    var inventoryTypes = GetInventoryTypesForItem(item);
                    
                    bool foundAfterSlot = false;
                    
                    foreach (var type in inventoryTypes)
                    {
                        // Use safe wrapper to re-validate container pointer in each loop iteration
                        var container = GetInventoryContainerSafe(type, 1); // Single attempt per iteration (we're already in a loop)
                        if (container == null) continue;
                        
                        int startSlot = 0;
                        // If we're in the same container, start searching after the current slot
                        if (type == afterType)
                        {
                            startSlot = afterSlot + 1;
                            foundAfterSlot = true;
                        }
                        // If we've already passed the afterType container, search from the beginning
                        else if (foundAfterSlot)
                        {
                            startSlot = 0;
                        }
                        // Otherwise, skip this container (we haven't reached afterType yet)
                        else
                        {
                            continue;
                        }
                        
                        for (int slot = startSlot; slot < container->Size; slot++)
                        {
                            var slotItem = container->GetInventorySlot(slot);
                            if (slotItem != null && slotItem->ItemId == item.ItemId && slotItem->Quantity > 0)
                            {
                                // Found a stack with the same item
                                return (type, slot);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error finding next stack: {ex.Message}", null);
            }
            
            return (InventoryType.Inventory1, -1);
        }
        
        public async Task<bool> OpenItemContextMenu(ScannedItem item, CancellationToken token)
        {
            try
            {
                Log?.Invoke($"[AutoMarket] Finding item {item.ItemName} (ID: {item.ItemId}) in inventory...");
                
                // Use the specific slot from the scanned item if available
                if (item.InventoryType != InventoryType.Inventory1 || item.InventorySlot >= 0)
                {
                    bool opened = await OpenItemContextMenuForSlot(item, item.InventoryType, item.InventorySlot, token);
                    if (opened)
                    {
                        return true;
                    }
                }
                
                // Fallback: Find the item in inventory (for backwards compatibility)
                InventoryType foundType = InventoryType.Inventory1;
                int foundSlot = -1;
                
                unsafe
                {
                    var inventoryManager = GetInventoryManagerSafe();
                    if (inventoryManager == null)
                    {
                        LogError?.Invoke("[AutoMarket] InventoryManager is null after retries", null);
                        return false;
                    }
                    
                    var inventoryTypes = GetInventoryTypesForItem(item);
                    
                    foreach (var type in inventoryTypes)
                    {
                        // Use safe wrapper to re-validate container pointer in each loop iteration
                        var container = GetInventoryContainerSafe(type, 1); // Single attempt per iteration
                        if (container == null) continue;
                        
                        for (int slot = 0; slot < container->Size; slot++)
                        {
                            var slotItem = container->GetInventorySlot(slot);
                            if (slotItem != null && slotItem->ItemId == item.ItemId && slotItem->Quantity > 0)
                            {
                                foundType = type;
                                foundSlot = slot;
                                Log?.Invoke($"[AutoMarket] Found item at {type} slot {slot}");
                                break;
                            }
                        }
                        
                        if (foundSlot >= 0) break;
                    }
                }
                
                if (foundSlot < 0)
                {
                LogError?.Invoke($"[AutoMarket] Item {item.ItemName} not found in {item.SourceName}", null);
                return false;
            }
                
                // Verify retainer UI is ready before opening context menu
                bool uiReady = await IsRetainerUIReady();
                if (!uiReady)
                {
                    LogError?.Invoke("[AutoMarket] Retainer UI is not ready - cannot open context menu", null);
                    return false;
                }
                
                // Close any existing context menu
                bool closedMenu = await CloseExistingContextMenu();
                if (closedMenu)
                {
                    await Task.Delay(110, token);
                }
                
                // Open context menu using AgentInventoryContext
                bool agentValid = false;
                nint agentPtr = nint.Zero;
                unsafe
                {
                    var agent = GetAgentInventoryContextSafe();
                    if (agent == null)
                    {
                        LogError?.Invoke("[AutoMarket] AgentInventoryContext is null after retries", null);
                        return false;
                    }
                    agentPtr = (nint)agent;
                    agentValid = true;
                }
                
                // Double-check no context menu is open
                await RunOnFrameworkThreadAsync(() =>
                {
                    unsafe
                    {
                        try
                        {
                            if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var existingMenu)
                                && existingMenu != null
                                && ECommons.GenericHelpers.IsAddonReady(&existingMenu->AtkUnitBase))
                            {
                                existingMenu->AtkUnitBase.Close(true);
                            }
                        }
                        catch (System.AccessViolationException) { }
                        catch { }
                    }
                });
                
                var foundTypeLocal = foundType;
                var foundSlotLocal = foundSlot;
                await RunOnFrameworkThreadAsync(() =>
                {
                    unsafe
                    {
                        try
                        {
                            if (agentPtr == nint.Zero)
                            {
                                return;
                            }
                            
                            var agent = (AgentInventoryContext*)agentPtr.ToPointer();
                            if (agent == null)
                            {
                                return;
                            }
                            
                            Log?.Invoke($"[AutoMarket] Opening context menu for item at {foundTypeLocal} slot {foundSlotLocal}");
                            agent->OpenForItemSlot(foundTypeLocal, foundSlotLocal, 0, 0);
                        }
                        catch (System.AccessViolationException ex)
                        {
                            LogError?.Invoke("Access violation opening context menu", ex);
                        }
                        catch (Exception ex)
                        {
                            LogError?.Invoke($"Error opening context menu: {ex.Message}", ex);
                        }
                    }
                });
                
                if (!agentValid)
                {
                    return false;
                }
                
                // Add delay to ensure UI is stable (after opening)
                await Task.Delay(55, token);
                await Task.Delay(55, token);
                
                // Wait for context menu to appear with validation
                for (int attempts = 0; attempts < 10; attempts++)
                {
                    await Task.Delay(33, token);
                    
                    bool menuReady = await RunOnFrameworkThreadAsync(() =>
                    {
                        unsafe
                        {
                            try
                            {
                                if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon)
                                    && contextMenuAddon != null
                                    && ECommons.GenericHelpers.IsAddonReady(&contextMenuAddon->AtkUnitBase))
                                {
                                    return true;
                                }
                                return false;
                            }
                            catch (System.AccessViolationException)
                            {
                                return false;
                            }
                            catch
                            {
                                return false;
                            }
                        }
                    });
                    
                    if (menuReady)
                    {
                        return true;
                    }
                }
                
                LogError?.Invoke("[AutoMarket] Context menu did not appear after opening", null);
                return false;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error opening context menu: {ex.Message}", null);
                return false;
            }
        }
        
        public async Task<bool> ClickPutUpForSale(ScannedItem item, CancellationToken token)
        {
            try
            {
                // Wait for context menu to appear - use RaptureAtkUnitManager like SelectString
                ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu contextMenu = null;
                nint contextMenuPtr = nint.Zero;
                
                // First, verify retainer UI is still ready
                bool uiReady = await IsRetainerUIReady();
                if (!uiReady)
                {
                    LogError?.Invoke("[AutoMarket] Retainer UI is not ready when trying to click Put Up for Sale", null);
                    return false;
                }
                
                for (int attempts = 0; attempts < 30; attempts++)
                {
                    await Task.Delay(66, token);
                    try
                    {
                        unsafe
                        {
                            if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon))
                            {
                                if (ECommons.GenericHelpers.IsAddonReady(&contextMenuAddon->AtkUnitBase))
                                {
                                    contextMenuPtr = (nint)contextMenuAddon;
                                }
                            }
                        }
                        
                        if (contextMenuPtr != nint.Zero)
                        {
                            try
                            {
                                contextMenu = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu(contextMenuPtr);
                                break;
                            }
                            catch (Exception ex)
                            {
                                LogError?.Invoke($"[AutoMarket] Error creating ContextMenu wrapper: {ex.Message}", null);
                            }
                        }
                    }
                    catch { }
                }
                
                if (contextMenu == null || contextMenuPtr == nint.Zero)
                {
                    LogError?.Invoke("[AutoMarket] Context menu not found", null);
                    return false;
                }
                
                // Get "Put Up for Sale" text from Addon sheet row 99
                string putUpForSaleText = "Put Up for Sale";
                try
                {
                    if (Plugin?.DataManager != null)
                    {
                        var addonSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
                        var row99Text = addonSheet?.GetRow(99).Text.ToString();
                        if (!string.IsNullOrEmpty(row99Text))
                        {
                            putUpForSaleText = row99Text;
                        }
                    }
                }
                catch { }
                
                // Access entries with defensive checks
                ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu.Entry[] entries = null;
                try
                {
                    entries = contextMenu.Entries;
                    if (entries == null)
                    {
                        LogError?.Invoke("[AutoMarket] Context menu has no entries", null);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    LogError?.Invoke($"[AutoMarket] Error accessing context menu entries: {ex.Message}", null);
                    return false;
                }
                
                // Find "Put Up for Sale" entry
                int foundIndex = -1;
                for (int i = 0; i < entries.Length; i++)
                        {
                            try
                            {
                        var entry = entries[i];
                        if (!entry.Enabled) continue;
                        
                        var entryText = entry.Text;
                        if (entryText != null && 
                            (entryText.Equals(putUpForSaleText, StringComparison.OrdinalIgnoreCase) ||
                             entryText.Contains("Put Up for Sale", StringComparison.OrdinalIgnoreCase) ||
                             entryText.Contains("Sell items", StringComparison.OrdinalIgnoreCase)))
                        {
                            foundIndex = entry.Index;
                            break;
                        }
                    }
                    catch { continue; }
                }
                
                if (foundIndex < 0)
                {
                    LogError?.Invoke("[AutoMarket] Could not find 'Put Up for Sale' option in context menu", null);
                    return false;
                }
                
                // Click the entry using FireCallback
                unsafe
                {
                    // Re-validate context menu is still ready before clicking
                    if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon))
                    {
                        LogError?.Invoke("[AutoMarket] ContextMenu addon not found when trying to click", null);
                        return false;
                    }
                    
                    var atkUnitBase = &contextMenuAddon->AtkUnitBase;
                    if (atkUnitBase == null || !ECommons.GenericHelpers.IsAddonReady(atkUnitBase))
                    {
                        LogError?.Invoke("[AutoMarket] ContextMenu addon is not ready", null);
                        return false;
                    }
                    
                    // Verify retainer UI is still ready (synchronous check since we're already on framework thread)
                    if (!IsRetainerUIReadySync())
                    {
                        LogError?.Invoke("[AutoMarket] Retainer UI is not ready when clicking context menu", null);
                        return false;
                    }
                    
                    // Based on ECommons ContextMenu.Entry.Select(): values [0, Index, 0]
                    var values = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[3]
                    {
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = 0 },
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = foundIndex },
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = 0 }
                    };
                    atkUnitBase->FireCallback(3, values, true);
                }
                await Task.Delay(198, token);
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error clicking 'Put Up for Sale': {ex.Message}", null);
                return false;
            }
        }
        
        public async Task<bool> VendorItem(ScannedItem item, CancellationToken token)
        {
            try
            {
                Log?.Invoke($"[AutoMarket] Attempting to vendor item: {item.ItemName} (ID: {item.ItemId})");
                
                // Step 1: Find item in inventory and open context menu
                if (!await OpenItemContextMenu(item, token))
                {
                    LogError?.Invoke($"[AutoMarket] Failed to open context menu for {item.ItemName}", null);
                    return false;
                }
                
                // Step 2: Click "Have Retainer Sell Items" from context menu
                if (!await ClickHaveRetainerSellItems(item, token))
                {
                    LogError?.Invoke($"[AutoMarket] Failed to click 'Have Retainer Sell Items' for {item.ItemName}", null);
                    return false;
                }
                
                // Step 3: Wait for and confirm any confirmation dialog
                await Task.Delay(330, token); // Give time for confirmation dialog to appear
                
                // Check for confirmation dialog (SelectYesno)
                bool confirmationClicked = false;
                unsafe
                {
                    if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var yesnoAddon)
                        && yesnoAddon->IsVisible
                        && ECommons.GenericHelpers.IsAddonReady(yesnoAddon))
                    {
                        Log?.Invoke("[AutoMarket] Found confirmation dialog, clicking Yes...");
                        ECommons.Automation.Callback.Fire(yesnoAddon, true, 0);
                        confirmationClicked = true;
                    }
                }
                
                if (confirmationClicked)
                {
                    await Task.Delay(198, token);
                }
                
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error vendoring item {item.ItemName}: {ex.Message}", ex);
                return false;
            }
        }

        public async Task<bool> WithdrawRetainerBagItemToInventory(ScannedItem item, CancellationToken token)
        {
            try
            {
                if (!item.SourceRetainerIndex.HasValue)
                {
                    LogError?.Invoke($"[AutoMarket] Cannot stage {item.ItemName}: item is not retainer-sourced", null);
                    return false;
                }

                if (!await CheckInventorySpaceAsync())
                {
                    Log?.Invoke("[AutoMarket] Player inventory is full; cannot withdraw another staged item right now");
                    return false;
                }

                if (!await OpenItemContextMenu(item, token))
                {
                    LogError?.Invoke($"[AutoMarket] Failed to open retainer bag context menu for {item.ItemName}", null);
                    return false;
                }

                if (!await ClickWithdrawToInventory(token))
                {
                    LogError?.Invoke($"[AutoMarket] Failed to click withdraw-to-inventory for {item.ItemName}", null);
                    return false;
                }

                await Task.Delay(330, token);

                bool confirmationClicked = false;
                unsafe
                {
                    if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var yesnoAddon)
                        && yesnoAddon->IsVisible
                        && ECommons.GenericHelpers.IsAddonReady(yesnoAddon))
                    {
                        ECommons.Automation.Callback.Fire(yesnoAddon, true, 0);
                        confirmationClicked = true;
                    }
                }

                if (confirmationClicked)
                    await Task.Delay(198, token);

                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error withdrawing retainer bag item {item.ItemName}: {ex.Message}", ex);
                return false;
            }
        }

        private async Task<bool> ClickWithdrawToInventory(CancellationToken token)
        {
            try
            {
                ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu contextMenu = null;
                nint contextMenuPtr = nint.Zero;

                for (int attempts = 0; attempts < 30; attempts++)
                {
                    await Task.Delay(66, token);
                    unsafe
                    {
                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon)
                            && ECommons.GenericHelpers.IsAddonReady(&contextMenuAddon->AtkUnitBase))
                        {
                            contextMenuPtr = (nint)contextMenuAddon;
                        }
                    }

                    if (contextMenuPtr != nint.Zero)
                    {
                        contextMenu = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu(contextMenuPtr);
                        break;
                    }
                }

                if (contextMenu == null || contextMenuPtr == nint.Zero)
                {
                    LogError?.Invoke("[AutoMarket] Context menu not found for retainer bag withdrawal", null);
                    return false;
                }

                var entries = contextMenu.Entries;
                if (entries == null)
                {
                    LogError?.Invoke("[AutoMarket] Context menu has no entries for retainer bag withdrawal", null);
                    return false;
                }

                string[] searchTexts =
                {
                    "Retrieve item",
                    "Retrieve",
                    "Withdraw",
                    "Move to inventory",
                    "Return to inventory",
                    "Entrust or withdraw"
                };

                int foundIndex = -1;
                foreach (var entry in entries)
                {
                    try
                    {
                        if (!entry.Enabled) continue;
                        var entryText = entry.Text;
                        if (!string.IsNullOrEmpty(entryText) &&
                            searchTexts.Any(text => entryText.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                                                    entryText.Contains(text, StringComparison.OrdinalIgnoreCase)))
                        {
                            foundIndex = entry.Index;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }

                if (foundIndex < 0)
                {
                    LogError?.Invoke("[AutoMarket] Could not find a retainer bag withdraw option in context menu", null);
                    return false;
                }

                unsafe
                {
                    if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon))
                    {
                        return false;
                    }

                    var atkUnitBase = &contextMenuAddon->AtkUnitBase;
                    var values = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[3]
                    {
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = 0 },
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = foundIndex },
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = 0 }
                    };
                    atkUnitBase->FireCallback(3, values, true);
                }

                await Task.Delay(198, token);
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error clicking withdraw-to-inventory: {ex.Message}", ex);
                return false;
            }
        }
        
        public async Task<bool> ClickHaveRetainerSellItems(ScannedItem item, CancellationToken token)
        {
            try
            {
                // Wait for context menu to appear - use RaptureAtkUnitManager like SelectString
                ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu contextMenu = null;
                nint contextMenuPtr = nint.Zero;
                
                // First, verify retainer UI is still ready
                bool uiReady = await IsRetainerUIReady();
                if (!uiReady)
                {
                    LogError?.Invoke("[AutoMarket] Retainer UI is not ready when trying to click Have Retainer Sell Items", null);
                    return false;
                }
                
                for (int attempts = 0; attempts < 30; attempts++)
                {
                    await Task.Delay(66, token);
                    try
                    {
                        unsafe
                        {
                            if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon))
                            {
                                if (ECommons.GenericHelpers.IsAddonReady(&contextMenuAddon->AtkUnitBase))
                                {
                                    contextMenuPtr = (nint)contextMenuAddon;
                                }
                            }
                        }
                        
                        if (contextMenuPtr != nint.Zero)
                        {
                            try
                            {
                                contextMenu = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu(contextMenuPtr);
                                break;
                            }
                            catch (Exception ex)
                            {
                                LogError?.Invoke($"[AutoMarket] Error creating ContextMenu wrapper: {ex.Message}", null);
                            }
                        }
                    }
                    catch { }
                }
                
                if (contextMenu == null || contextMenuPtr == nint.Zero)
                {
                    LogError?.Invoke("[AutoMarket] Context menu not found", null);
                    return false;
                }
                
                // Get "Have Retainer Sell Items" text from Addon sheet row 5480
                string retainerSellText = "Have Retainer Sell Items";
                try
                {
                    if (Plugin?.DataManager != null)
                    {
                        var addonSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
                        var row5480Text = addonSheet?.GetRow(5480).Text.ToString();
                        if (!string.IsNullOrEmpty(row5480Text))
                        {
                            retainerSellText = row5480Text;
                        }
                    }
                }
                catch { }
                
                // Access entries with defensive checks
                ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu.Entry[] entries = null;
                try
                {
                    entries = contextMenu.Entries;
                    if (entries == null)
                    {
                        LogError?.Invoke("[AutoMarket] Context menu has no entries", null);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    LogError?.Invoke($"[AutoMarket] Error accessing context menu entries: {ex.Message}", null);
                    return false;
                }
                
                // Find "Have Retainer Sell Items" entry
                int foundIndex = -1;
                for (int i = 0; i < entries.Length; i++)
                {
                    try
                    {
                        var entry = entries[i];
                        if (!entry.Enabled) continue;
                        
                        var entryText = entry.Text;
                        if (entryText != null && 
                            (entryText.Equals(retainerSellText, StringComparison.OrdinalIgnoreCase) ||
                             entryText.Contains("Have Retainer Sell Items", StringComparison.OrdinalIgnoreCase) ||
                             entryText.Contains("Sell Items", StringComparison.OrdinalIgnoreCase)))
                        {
                            foundIndex = entry.Index;
                            break;
                        }
                    }
                    catch { continue; }
                }
                
                if (foundIndex < 0)
                {
                    LogError?.Invoke("[AutoMarket] Could not find 'Have Retainer Sell Items' option in context menu", null);
                    return false;
                }
                
                // Click the entry using FireCallback
                unsafe
                {
                    // Re-validate context menu is still ready before clicking
                    if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon))
                    {
                        LogError?.Invoke("[AutoMarket] ContextMenu addon not found when trying to click", null);
                        return false;
                    }
                    
                    var atkUnitBase = &contextMenuAddon->AtkUnitBase;
                    if (atkUnitBase == null || !ECommons.GenericHelpers.IsAddonReady(atkUnitBase))
                    {
                        LogError?.Invoke("[AutoMarket] ContextMenu addon is not ready", null);
                        return false;
                    }
                    
                    // Verify retainer UI is still ready (synchronous check since we're already on framework thread)
                    if (!IsRetainerUIReadySync())
                    {
                        LogError?.Invoke("[AutoMarket] Retainer UI is not ready when clicking context menu", null);
                        return false;
                    }
                    
                    // Based on ECommons ContextMenu.Entry.Select(): values [0, Index, 0]
                    var values = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[3]
                    {
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = 0 },
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = foundIndex },
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = 0 }
                    };
                    atkUnitBase->FireCallback(3, values, true);
                }
                await Task.Delay(198, token);
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error clicking 'Have Retainer Sell Items': {ex.Message}", null);
                return false;
            }
        }
        
        public async Task<uint> GetLowestPriceFromComparePrices(ScannedItem item, CancellationToken token)
        {
            Log?.Invoke("[AutoMarket] Waiting for ItemSearchResult window to appear...");
            nint itemSearchPtr = nint.Zero;
            
            for (int attempts = 0; attempts < 40; attempts++)
            {
                await Task.Delay(66, token);
                
                if (token.IsCancellationRequested) break;
                
                try
                {
                    unsafe
                    {
                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("ItemSearchResult", out var itemSearchAddon))
                        {
                            if (itemSearchAddon != null && ECommons.GenericHelpers.IsAddonReady(itemSearchAddon))
                            {
                                itemSearchPtr = (nint)itemSearchAddon;
                                Log?.Invoke("[AutoMarket] Found ItemSearchResult window");
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogError?.Invoke($"[AutoMarket] Error checking ItemSearchResult (attempt {attempts}): {ex.Message}", ex);
                    await Task.Delay(33, token);
                    continue;
                }
            }
            
            if (itemSearchPtr == nint.Zero || token.IsCancellationRequested)
            {
                LogError?.Invoke("[AutoMarket] ItemSearchResult window did not appear", null);
                return 0;
            }
            
            await Task.Delay(132, token);
            if (token.IsCancellationRequested) return 0;
            
            ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ItemSearchResult itemSearch = null;
            for (int attempts = 0; attempts < 40; attempts++)
            {
                await Task.Delay(66, token);
                
                if (token.IsCancellationRequested) break;
                
                try
                {
                    unsafe
                    {
                        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)itemSearchPtr;
                        if (addon == null)
                        {
                            continue;
                        }
                        
                        if (!ECommons.GenericHelpers.IsAddonReady(addon))
                        {
                            continue;
                        }
                    }
                    
                    try
                    {
                        itemSearch = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ItemSearchResult(itemSearchPtr);
                        
                        if (itemSearch?.Entries != null && itemSearch.Entries.Length > 0)
                        {
                            Log?.Invoke($"[AutoMarket] ItemSearchResult has {itemSearch.Entries.Length} entries");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError?.Invoke($"[AutoMarket] Error creating ItemSearchResult wrapper (attempt {attempts}): {ex.Message}", ex);
                        await Task.Delay(33, token);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    LogError?.Invoke($"[AutoMarket] Error checking ItemSearchResult (attempt {attempts}): {ex.Message}", ex);
                    await Task.Delay(33, token);
                    continue;
                }
            }
            
            if (itemSearch == null)
            {
                LogError?.Invoke("[AutoMarket] Could not create ItemSearchResult wrapper", null);
                return 0;
            }
            
            if (itemSearch.Entries == null || itemSearch.Entries.Length == 0)
            {
                LogError?.Invoke("[AutoMarket] ItemSearchResult window has no entries after waiting", null);
                return 0;
            }
            
            Log?.Invoke($"[AutoMarket] ItemSearchResult window has {itemSearch.Entries.Length} entries, parsing prices...");
            
            // Find lowest price (considering HQ/NQ)
            uint lowestPrice = uint.MaxValue;
            int parsedCount = 0;
            var parsedPrices = new List<uint>();
            
            // Parse entries with defensive checks
            var entries = itemSearch.Entries;
            if (entries == null || entries.Length == 0)
            {
                LogError?.Invoke("[AutoMarket] ItemSearchResult.Entries is null or empty", null);
                return 0;
            }
            
            for (int i = 0; i < entries.Length; i++)
            {
                try
                {
                    var entry = entries[i];
                    
                    unsafe
                    {
                        AtkTextNode* priceTextNode = null;
                        try
                        {
                            priceTextNode = entry.PriceTextNode;
                        }
                        catch
                        {
                            continue;
                        }
                        
                        if (priceTextNode == null) continue;
                        
                        var nodeTextPtr = &priceTextNode->NodeText;
                        if (nodeTextPtr == null) continue;
                        
                        try
                        {
                            var seString = ECommons.GenericHelpers.ReadSeString(nodeTextPtr);
                            var priceText = seString.TextValue;
                            
                            if (!string.IsNullOrEmpty(priceText))
                            {
                                // Remove commas and parse price (e.g., "1,234,567" -> 1234567)
                                var cleanPrice = priceText.Replace(",", "").Replace(" ", "").Trim();
                                
                                // Try to extract just the number part (in case there's "gil" or other text)
                                var match = System.Text.RegularExpressions.Regex.Match(cleanPrice, @"(\d+)");
                                if (match.Success && uint.TryParse(match.Groups[1].Value, out uint price))
                                {
                                    // Check if this is HQ (entry.HQImageNode would be non-null if HQ)
                                    bool isHQ = false;
                                    try
                                    {
                                        unsafe
                                        {
                                            var hqNode = entry.HQImageNode;
                                            isHQ = hqNode != null;
                                        }
                                    }
                                    catch { }
                                    
                                    Log?.Invoke($"[AutoMarket]   Entry {i} price: {price:N0} gil ({(isHQ ? "HQ" : "NQ")})");
                                    
                                    parsedPrices.Add(price);
                                    if (price < lowestPrice)
                                    {
                                        lowestPrice = price;
                                    }
                                    parsedCount++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log?.Invoke($"[AutoMarket] Error parsing price from entry {i}: {ex.Message}");
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[AutoMarket] Error processing entry {i}: {ex.Message}");
                    continue;
                }
            }
            
            if (parsedCount == 0)
            {
                Log?.Invoke("[AutoMarket] Could not parse any prices from ItemSearchResult entries");
                return 0;
            }
            
            if (lowestPrice == uint.MaxValue)
            {
                Log?.Invoke("[AutoMarket] No valid prices found");
                return 0;
            }
            
            // Basic outlier guard: if the lowest price is a steep outlier (<50% of next lowest), ignore it
            uint adjustedLowest = lowestPrice;
            if (parsedPrices.Count >= 2)
            {
                parsedPrices.Sort();
                uint secondLowest = parsedPrices[1];
                if (parsedPrices[0] * 2 < secondLowest)
                {
                    Log?.Invoke($"[AutoMarket] Ignoring outlier lowest price {parsedPrices[0]:N0} gil; using second lowest {secondLowest:N0} gil");
                    adjustedLowest = secondLowest;
                }
            }
            
            Log?.Invoke($"[AutoMarket] Found lowest price: {adjustedLowest:N0} gil (parsed {parsedCount} entries)");
            return adjustedLowest;
        }
        
        public async Task<bool> CloseRetainerWindow(bool weVendored, CancellationToken token)
        {
            try
            {
                nint sellAddonPtr = nint.Zero;
                nint selectAddonPtr = nint.Zero;
                
                unsafe
                {
                    // Try to close RetainerSell window if open
                    FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* tempSellPtr = null;
                    if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerSell", out var tempSellAddon))
                    {
                        tempSellPtr = tempSellAddon;
                        sellAddonPtr = (nint)tempSellPtr;
                    }
                    
                    // Try to close SelectString if still open
                    FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* tempSelectPtr = null;
                    if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectString", out var tempSelectAddon))
                    {
                        tempSelectPtr = tempSelectAddon;
                        selectAddonPtr = (nint)tempSelectPtr;
                    }
                }
                
                // Close windows outside unsafe block
                if (sellAddonPtr != nint.Zero)
                {
                    var retainerSell = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.RetainerSell(sellAddonPtr);
                    retainerSell.Cancel();
                    await Task.Delay(550, token);
                }
                
                if (selectAddonPtr != nint.Zero)
                {
                    unsafe
                    {
                        var selectAddon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)selectAddonPtr;
                        if (selectAddon != null)
                        {
                            selectAddon->Close(true);
                        }
                    }
                    await Task.Delay(550, token);
                }
                
                // Handle confirmation dialog that appears when leaving retainer after vendoring
                // Only check for dialog if we actually vendored items
                if (weVendored)
                {
                    await HandleRetainerLeaveConfirmation(token);
                }
                
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error closing retainer window: {ex.Message}", ex);
                return false;
            }
        }
        
        /// <summary>
        /// Closes the retainer UI windows to return to RetainerList (retainer selection).
        /// Closes market and inventory retainer views, but keeps RetainerList open for next retainer selection.
        /// </summary>
        public async Task<bool> CloseRetainerList(bool weVendored, CancellationToken token)
        {
            try
            {
                // Close retainer sub-windows. Market selling and inventory/entrust use different addons.
                bool closedRetainerSubview = false;
                string[] retainerSubviewNames =
                {
                    "RetainerSellList",
                    "RetainerItemTransferList",
                    "RetainerItemTransferProgress",
                    "RetainerInventory",
                    "InventoryRetainer"
                };

                unsafe
                {
                    foreach (var addonName in retainerSubviewNames)
                    {
                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(addonName, out var addon)
                            && addon != null
                            && addon->IsVisible)
                        {
                            addon->Close(true);
                            closedRetainerSubview = true;
                        }
                    }
                }
                
                if (closedRetainerSubview)
                {
                    await Task.Delay(550, token);
                }
                
                // Close SelectString if it's still open
                bool closedSelectString = false;
                unsafe
                {
                    if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectString", out var selectStringAddon))
                    {
                        selectStringAddon->Close(true);
                        closedSelectString = true;
                    }
                }
                
                if (closedSelectString)
                {
                    await Task.Delay(550, token);
                }
                
                // Handle confirmation dialog that appears when leaving retainer after vendoring
                // Only check for dialog if we actually vendored items
                if (weVendored)
                {
                    await HandleRetainerLeaveConfirmation(token);
                }
                
                // Verify RetainerList is open and ready (we need it to select the next retainer)
                bool retainerListReady = false;
                unsafe
                {
                    if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerList", out var retainerListAddon)
                        && ECommons.GenericHelpers.IsAddonReady(retainerListAddon))
                    {
                        retainerListReady = true;
                    }
                }
                
                // Wait for RetainerList to appear if it's not ready yet
                if (!retainerListReady)
                {
                    for (int attempts = 0; attempts < 30; attempts++)
                    {
                        await Task.Delay(66, token);
                        
                        unsafe
                        {
                            if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerList", out var retainerListAddon)
                                && ECommons.GenericHelpers.IsAddonReady(retainerListAddon))
                            {
                                retainerListReady = true;
                                break;
                            }
                        }
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error closing retainer windows: {ex.Message}", ex);
                return false;
            }
        }
        
        /// <summary>
        /// Handles the confirmation dialog that appears when leaving a retainer after vendoring items.
        /// The dialog says "Your retainer will be unable to process item buyback..." and requires clicking "Yes".
        /// </summary>
        private async Task<bool> HandleRetainerLeaveConfirmation(CancellationToken token)
        {
            try
            {
                // Wait a bit for the dialog to appear after closing the retainer window
                await Task.Delay(330, token);
                
                bool confirmationClicked = false;

                // Wait for the dialog to appear (up to 2 seconds)
                for (int attempts = 0; attempts < 20; attempts++)
                {
                    await Task.Delay(66, token);

                    unsafe
                    {
                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var yesnoAddon)
                            && yesnoAddon->IsVisible
                            && ECommons.GenericHelpers.IsAddonReady(yesnoAddon))
                        {
                            Log?.Invoke("[AutoMarket] Found retainer leave confirmation dialog, clicking Yes...");
                            ECommons.Automation.Callback.Fire(yesnoAddon, true, 0);
                            confirmationClicked = true;
                        }
                    }

                    if (confirmationClicked)
                    {
                        break;
                    }
                }
                
                if (confirmationClicked)
                {
                    await Task.Delay(330, token); // Wait for dialog to close
                    Log?.Invoke("[AutoMarket] Confirmed leaving retainer");
                }
                
                return confirmationClicked;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error handling retainer leave confirmation: {ex.Message}", ex);
                return false;
            }
        }
        
        // ============================================
        // Listed Item Management Methods
        // ============================================
        
        /// <summary>
        /// Gets all items currently listed on the retainer's market board from RetainerManager.
        /// Returns a list of ScannedItem objects representing the listed items.
        /// </summary>
        public unsafe List<ScannedItem> GetListedItems(int retainerIndex)
        {
            var listedItems = new List<ScannedItem>();
            
            try
            {
                var retainerManager = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
                if (retainerManager == null)
                {
                    Log?.Invoke("[AutoMarket] RetainerManager is null");
                    return listedItems;
                }
                
                // Find the retainer by index
                int validRetainerIndex = 0;
                FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Retainer* retainer = null;
                for (int i = 0; i < retainerManager->Retainers.Length; i++)
                {
                    var r = retainerManager->Retainers[i];
                    if (r.RetainerId != 0 && r.Name[0] != 0)
                    {
                        if (validRetainerIndex == retainerIndex)
                        {
                            retainer = &r;
                            break;
                        }
                        validRetainerIndex++;
                    }
                }
                
                if (retainer == null)
                {
                    LogError?.Invoke($"[AutoMarket] Could not find retainer at index {retainerIndex}", null);
                    return listedItems;
                }
                
                // Access market items from RetainerManager
                // Note: Market items are stored in RetainerManager, but we need to access them through the retainer's market data
                // For now, we'll use the market item count and access items through the UI addon
                // This is a simplified approach - in practice, we may need to access the RetainerSellList addon's component list
                
                int marketItemCount = retainer->MarketItemCount;
                if (marketItemCount == 0)
                {
                    Log?.Invoke("[AutoMarket] Retainer has no market items");
                    return listedItems;
                }
                
                // Access market items through RetainerSellList addon structure
                // Try to get the addon and access its component list
                if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerSellList", out var retainerSellListAddon))
                {
                    Log?.Invoke("[AutoMarket] RetainerSellList addon not found - cannot read listed items");
                    return listedItems;
                }
                
                if (!ECommons.GenericHelpers.IsAddonReady(retainerSellListAddon) || !retainerSellListAddon->IsVisible)
                {
                    Log?.Invoke("[AutoMarket] RetainerSellList addon not ready - cannot read listed items");
                    return listedItems;
                }
                
                // Access the addon's component list to get listed items
                // RetainerSellList uses a component list structure to display items
                // We'll iterate through the visible items in the list
                if (Plugin?.DataManager == null)
                {
                    LogError?.Invoke("[AutoMarket] DataManager is null", null);
                    return listedItems;
                }
                
                var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                if (itemSheet == null)
                {
                    LogError?.Invoke("[AutoMarket] Item sheet is null", null);
                    return listedItems;
                }
                
                unsafe
                {
                    var addonBase = retainerSellListAddon;
                    if (addonBase != null && addonBase->RootNode != null)
                    {
                        FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentList* componentList = null;
                        
                        if (addonBase->UldManager.NodeListCount > 10)
                        {
                            var node10 = addonBase->UldManager.NodeList[10];
                            if (node10 != null)
                            {
                                var componentNode = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentNode*)node10;
                                if (componentNode != null && componentNode->Component != null)
                                {
                                    var list = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentList*)componentNode->Component;
                                    if (list != null && list->ListLength > 0)
                                    {
                                        componentList = list;
                                        Log?.Invoke($"[AutoMarket] Found component list at node 10 with {list->ListLength} entries");
                                    }
                                }
                            }
                        }
                        
                        if (componentList == null)
                        {
                            for (uint nodeId = 0; nodeId < addonBase->UldManager.NodeListCount && nodeId <= 20; nodeId++)
                            {
                                if (nodeId == 10) continue;
                                var node = addonBase->UldManager.NodeList[nodeId];
                                if (node == null) continue;
                                
                                var componentNode = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentNode*)node;
                                if (componentNode != null && componentNode->Component != null)
                                {
                                    var list = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentList*)componentNode->Component;
                                    if (list != null && list->ListLength > 0)
                                    {
                                        componentList = list;
                                        Log?.Invoke($"[AutoMarket] Found component list at node {nodeId} with {list->ListLength} entries");
                                        break;
                                    }
                                }
                            }
                        }
                        
                        if (componentList != null && componentList->ListLength > 0)
                        {
                            // Iterate through the list entries
                            int entryCount = Math.Min((int)componentList->ListLength, marketItemCount);
                            
                            for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
                            {
                                try
                                {
                                    // Get the item renderer for this entry
                                    // ItemRendererList is an array of ListItem structs
                                    if (entryIndex >= (int)componentList->ListLength) break;
                                    
                                    var listItem = componentList->ItemRendererList[entryIndex];
                                    var itemRenderer = listItem.AtkComponentListItemRenderer;
                                    if (itemRenderer == null) continue;
                                    
                                    // Extract item data from the renderer's child nodes
                                    var extractedItem = ExtractItemDataFromListRenderer(itemRenderer, itemSheet, entryIndex);
                                    
                                    if (extractedItem != null && (extractedItem.ItemId > 0 || extractedItem.ListingPrice > 0))
                                    {
                                        extractedItem.InventorySlot = entryIndex; // Store the market slot index
                                        listedItems.Add(extractedItem);
                                        Log?.Invoke($"[AutoMarket] Extracted item {entryIndex + 1}: {extractedItem.ItemName} (ID: {extractedItem.ItemId}, Qty: {extractedItem.Quantity}, Price: {extractedItem.ListingPrice:N0}, HQ: {extractedItem.IsHQ})");
                                    }
                                    else
                                    {
                                        Log?.Invoke($"[AutoMarket] Could not extract complete data for entry {entryIndex + 1}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogError?.Invoke($"[AutoMarket] Error extracting data from list entry {entryIndex}: {ex.Message}", ex);
                                }
                            }
                        }
                        else
                        {
                            Log?.Invoke("[AutoMarket] Could not find component list in RetainerSellList addon");
                        }
                    }
                }
                
                // If we couldn't extract items from the component list, create placeholders
                if (listedItems.Count == 0 && marketItemCount > 0)
                {
                    Log?.Invoke("[AutoMarket] Component list parsing failed, creating placeholder entries");
                    for (int slotIndex = 0; slotIndex < marketItemCount && slotIndex < 20; slotIndex++)
                    {
                        var placeholderItem = new ScannedItem
                        {
                            ItemId = 0,
                            ItemName = $"Market Item Slot {slotIndex + 1}",
                            Quantity = 0,
                            IsHQ = false,
                            VendorPrice = 0,
                            StackSize = 0,
                            CanBeListedOnMarketBoard = true,
                            ListingPrice = 0,
                            InventoryType = InventoryType.Inventory1,
                            InventorySlot = slotIndex
                        };
                        listedItems.Add(placeholderItem);
                    }
                }
                
                Log?.Invoke($"[AutoMarket] Successfully extracted {listedItems.Count} items from component list (expected {marketItemCount})");
                
                Log?.Invoke($"[AutoMarket] Found {listedItems.Count} items listed on retainer market board");
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error getting listed items: {ex.Message}", ex);
            }
            
            return listedItems;
        }
        
        /// <summary>
        /// Extracts item data from a component list item renderer.
        /// Traverses the renderer's child nodes to find ItemId, Quantity, Price, and HQ status.
        /// </summary>
        private unsafe ScannedItem? ExtractItemDataFromListRenderer(
            FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentListItemRenderer* itemRenderer,
            Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Item> itemSheet,
            int entryIndex)
        {
            if (itemRenderer == null) return null;
            
            var item = new ScannedItem
            {
                ItemId = 0,
                ItemName = $"Market Item {entryIndex + 1}",
                Quantity = 0,
                IsHQ = false,
                VendorPrice = 0,
                StackSize = 0,
                CanBeListedOnMarketBoard = true,
                ListingPrice = 0,
                InventoryType = InventoryType.Inventory1,
                InventorySlot = entryIndex
            };
            
            try
            {
                // Traverse the item renderer's child nodes
                // RetainerSellList entries typically have:
                // - Node 0: Item icon (contains ItemId)
                // - Node 1: Item name text
                // - Node 2: Quantity text
                // - Node 3: Price text
                // - Node 4+: Other nodes (HQ indicator, etc.)
                
                // Access the component base from the item renderer
                // AtkComponentListItemRenderer extends AtkComponentBase, so we can cast it
                var componentBase = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentBase*)itemRenderer;
                if (componentBase == null || componentBase->OwnerNode == null) return null;
                
                var uldManager = &componentBase->UldManager;
                if (uldManager == null) return null;
                
                // Try to find item icon node and text nodes
                // RetainerSellList entries have nodes in the UldManager
                for (int i = 0; i < uldManager->NodeListCount && i < 20; i++)
                {
                    var node = uldManager->NodeList[i];
                    if (node == null) continue;
                    
                    // Check if this is an image node (item icon)
                    if (node->Type == FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Image)
                    {
                        var imageNode = node->GetAsAtkImageNode();
                        if (imageNode != null)
                        {
                            // Item icon nodes have the ItemId in the ImageId
                            // For RetainerSellList, we need to extract ItemId differently
                            // The ItemId might be stored in the node's data or we need to use a different approach
                            
                            // Alternative: Try to get ItemId from the node's parent or from the renderer's data
                            // For now, we'll try to extract from other nodes
                        }
                    }
                    
                    // Check if this is a text node that might contain item name, quantity, or price
                    if (node->Type == FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Text)
                    {
                        var textNode = node->GetAsAtkTextNode();
                        if (textNode != null && (byte*)textNode->NodeText.StringPtr != null)
                        {
                            try
                            {
                                var seString = ECommons.GenericHelpers.ReadSeString(&textNode->NodeText);
                                var text = seString.TextValue;
                                
                                if (!string.IsNullOrEmpty(text))
                                {
                                    // Try to parse as quantity (usually a number)
                                    if (uint.TryParse(text.Replace(",", "").Replace(" ", "").Trim(), out uint qty) && qty > 0 && qty < 10000)
                                    {
                                        if (item.Quantity == 0) // Only set if not already set
                                        {
                                            item.Quantity = (int)qty;
                                        }
                                    }
                                    
                                    // Try to parse as price (usually a large number with commas)
                                    if (text.Contains(",") || (text.Length > 3 && uint.TryParse(text.Replace(",", "").Replace(" ", "").Trim(), out uint price) && price > 100))
                                    {
                                        var cleanPrice = text.Replace(",", "").Replace(" ", "").Replace("gil", "").Trim();
                                        if (uint.TryParse(cleanPrice, out uint parsedPrice) && parsedPrice > 0)
                                        {
                                            if (item.ListingPrice == 0 || parsedPrice < item.ListingPrice) // Take the first or smallest (might be multiple price nodes)
                                            {
                                                item.ListingPrice = parsedPrice;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                
                // Try to extract ItemId from image nodes
                // Search through nodes again specifically for ItemId extraction
                for (int i = 0; i < uldManager->NodeListCount && i < 20; i++)
                {
                    var node = uldManager->NodeList[i];
                    if (node == null) continue;
                    
                    if (node->Type == FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Image)
                    {
                        var imageNode = node->GetAsAtkImageNode();
                        if (imageNode != null)
                        {
                            // Try to extract ItemId from image node
                            // The ItemId might be stored in the component's data or we need to use icon ID lookup
                            // For RetainerSellList, we might need to access the renderer's internal data
                            // This is complex and may require memory offsets or additional research
                            
                            // For now, we'll try to get ItemId from the component's user data if available
                            // The actual implementation may need to use memory offsets to access the ItemId
                            // stored in the RetainerSellList component's internal structure
                        }
                    }
                }
                
                // If we still don't have ItemId, try to match by name from item sheet
                // This is a fallback but less reliable
                if (item.ItemId == 0 && !string.IsNullOrEmpty(item.ItemName) && item.ItemName != $"Market Item {entryIndex + 1}")
                {
                    // Try to find item by name in item sheet
                    // This is expensive but might work as a fallback
                }
                
                // Check for HQ indicator
                // HQ items typically have a special node or flag
                // We can check for HQ text in the name or a specific indicator node
                if (item.ItemName.Contains("HQ") || item.ItemName.Contains("High Quality"))
                {
                    item.IsHQ = true;
                    item.ItemName = item.ItemName.Replace("HQ", "").Replace("High Quality", "").Trim();
                }
                
                // If we have at least quantity or price, return the item (even without ItemId)
                // The ItemId can be extracted later when clicking on the item
                if (item.Quantity > 0 || item.ListingPrice > 0)
                {
                    return item;
                }
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error extracting data from list renderer: {ex.Message}", ex);
            }
            
            return null;
        }
        /// <summary>
        /// Clicks on a listed item in RetainerSellList to open its context menu.
        /// </summary>
        private async Task<bool> ClickListedItem(int itemIndex, CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested) return false;
                
                unsafe
                {
                    if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerSellList", out var retainerSellList))
                    {
                        LogError?.Invoke("[AutoMarket] RetainerSellList addon not found", null);
                        return false;
                    }
                    
                    if (retainerSellList == null || !ECommons.GenericHelpers.IsAddonReady(retainerSellList) || !retainerSellList->IsVisible)
                    {
                        LogError?.Invoke("[AutoMarket] RetainerSellList addon not ready", null);
                        return false;
                    }
                    
                    if (itemIndex < 0)
                    {
                        LogError?.Invoke($"[AutoMarket] Invalid item index: {itemIndex}", null);
                        return false;
                    }
                    
                    ECommons.Automation.Callback.Fire(retainerSellList, true, 0, itemIndex, 1);
                }
                
                await Task.Delay(220, token);
                if (token.IsCancellationRequested) return false;
                
                bool contextMenuFound = false;
                for (int attempts = 0; attempts < 10 && !token.IsCancellationRequested; attempts++)
                {
                    await Task.Delay(55, token);
                    unsafe
                    {
                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenu))
                        {
                            if (contextMenu != null && ECommons.GenericHelpers.IsAddonReady(&contextMenu->AtkUnitBase))
                            {
                                contextMenuFound = true;
                                break;
                            }
                        }
                    }
                }
                
                if (!contextMenuFound)
                {
                    LogError?.Invoke("[AutoMarket] Context menu did not appear after clicking listed item", null);
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error clicking listed item: {ex.Message}", ex);
                return false;
            }
        }
        
        /// <summary>
        /// Clicks "Adjust Price" from the context menu of a listed item.
        /// </summary>
        private async Task<bool> ClickAdjustPrice(CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested) return false;
                
                ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu contextMenu = null;
                nint contextMenuPtr = nint.Zero;
                
                for (int attempts = 0; attempts < 30 && !token.IsCancellationRequested; attempts++)
                {
                    await Task.Delay(66, token);
                    if (token.IsCancellationRequested) return false;
                    
                    try
                    {
                        unsafe
                        {
                            if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon))
                            {
                                if (contextMenuAddon != null && ECommons.GenericHelpers.IsAddonReady(&contextMenuAddon->AtkUnitBase))
                                {
                                    contextMenuPtr = (nint)contextMenuAddon;
                                }
                            }
                        }
                        
                        if (contextMenuPtr != nint.Zero)
                        {
                            try
                            {
                                contextMenu = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu(contextMenuPtr);
                                break;
                            }
                            catch (Exception ex)
                            {
                                LogError?.Invoke($"[AutoMarket] Error creating ContextMenu wrapper: {ex.Message}", null);
                            }
                        }
                    }
                    catch { }
                }
                
                if (contextMenu == null || contextMenuPtr == nint.Zero)
                {
                    LogError?.Invoke("[AutoMarket] Context menu not found for Adjust Price", null);
                    return false;
                }
                
                if (token.IsCancellationRequested) return false;
                
                // Get "Adjust Price" text from Addon sheet
                string adjustPriceText = "Adjust Price";
                try
                {
                    if (Plugin?.DataManager != null)
                    {
                        try
                        {
                            var addonSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
                            if (addonSheet != null)
                            {
                                var row = addonSheet.GetRow(5481); // Common ID for adjust price
                                if (row.RowId != 0)
                                {
                                    var text = row.Text.ToString();
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        adjustPriceText = text;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                
                // Find "Adjust Price" entry
                var entries = contextMenu.Entries;
                if (entries == null)
                {
                    LogError?.Invoke("[AutoMarket] Context menu has no entries", null);
                    return false;
                }
                
                int foundIndex = -1;
                for (int i = 0; i < entries.Length; i++)
                {
                    try
                    {
                        var entry = entries[i];
                        if (!entry.Enabled) continue;
                        
                        var entryText = entry.Text;
                        if (entryText != null && 
                            (entryText.Equals(adjustPriceText, StringComparison.OrdinalIgnoreCase) ||
                             entryText.Contains("Adjust Price", StringComparison.OrdinalIgnoreCase) ||
                             entryText.Contains("Adjust", StringComparison.OrdinalIgnoreCase)))
                        {
                            foundIndex = entry.Index;
                            break;
                        }
                    }
                    catch { continue; }
                }
                
                if (foundIndex < 0)
                {
                    LogError?.Invoke("[AutoMarket] Could not find 'Adjust Price' option in context menu", null);
                    return false;
                }
                
                unsafe
                {
                    if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var contextMenuAddon))
                    {
                        LogError?.Invoke("[AutoMarket] ContextMenu addon not found when trying to click", null);
                        return false;
                    }
                    
                    if (contextMenuAddon == null)
                    {
                        LogError?.Invoke("[AutoMarket] ContextMenu addon is null", null);
                        return false;
                    }
                    
                    var atkUnitBase = &contextMenuAddon->AtkUnitBase;
                    if (atkUnitBase == null || !ECommons.GenericHelpers.IsAddonReady(atkUnitBase))
                    {
                        LogError?.Invoke("[AutoMarket] ContextMenu addon is not ready", null);
                        return false;
                    }
                    
                    ECommons.Automation.Callback.Fire(atkUnitBase, true, 0, 0, 0, 0, 0);
                }
                
                await Task.Delay(220, token);
                if (token.IsCancellationRequested) return false;
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error clicking Adjust Price: {ex.Message}", ex);
                return false;
            }
        }
        
        /// <summary>
        /// Waits for the context menu to appear and returns its wrapper, or null on failure.
        /// </summary>
        private async Task<(ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu contextMenu, nint ptr)> WaitForContextMenu(CancellationToken token)
        {
            nint contextMenuPtr = nint.Zero;
            ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu contextMenu = null;

            for (int attempts = 0; attempts < 30; attempts++)
            {
                await Task.Delay(66, token);
                try
                {
                    unsafe
                    {
                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var addon)
                            && ECommons.GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
                        {
                            contextMenuPtr = (nint)addon;
                        }
                    }
                    if (contextMenuPtr != nint.Zero)
                    {
                        contextMenu = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu(contextMenuPtr);
                        break;
                    }
                }
                catch { }
            }

            return (contextMenu, contextMenuPtr);
        }

        /// <summary>
        /// Fires the context menu callback for the entry at the given index.
        /// </summary>
        private bool FireContextMenuEntry(int entryIndex)
        {
            unsafe
            {
                if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonContextMenu>("ContextMenu", out var addon))
                    return false;
                var atk = &addon->AtkUnitBase;
                if (atk == null || !ECommons.GenericHelpers.IsAddonReady(atk))
                    return false;
                var values = stackalloc AtkValue[3]
                {
                    new() { Type = AtkValueType.Int, Int = 0 },
                    new() { Type = AtkValueType.Int, Int = entryIndex },
                    new() { Type = AtkValueType.Int, Int = 0 }
                };
                atk->FireCallback(3, values, true);
                return true;
            }
        }

        /// <summary>
        /// Clicks "Return Items to Inventory" (returns listed item to the player's inventory).
        /// Uses strict text matching — no broad fallback — to avoid hitting the wrong menu entry.
        /// </summary>
        private async Task<bool> ClickReturnToPlayerInventory(CancellationToken token)
        {
            try
            {
                var (contextMenu, ptr) = await WaitForContextMenu(token);
                if (contextMenu == null || ptr == nint.Zero)
                {
                    LogError?.Invoke("[AutoMarket] Context menu not found for return-to-player", null);
                    return false;
                }

                // Build the exact text from Addon sheet, fall back only to a literal exact string
                string returnText = "Return Items to Inventory";
                try
                {
                    if (Plugin?.DataManager != null)
                    {
                        var addonSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
                        if (addonSheet != null)
                        {
                            var row = addonSheet.GetRow(5482);
                            if (row.RowId != 0)
                            {
                                var text = row.Text.ToString();
                                if (!string.IsNullOrEmpty(text))
                                    returnText = text;
                            }
                        }
                    }
                }
                catch { }

                var entries = contextMenu.Entries;
                if (entries == null)
                {
                    LogError?.Invoke("[AutoMarket] Context menu has no entries (return-to-player)", null);
                    return false;
                }

                int foundIndex = -1;
                for (int i = 0; i < entries.Length; i++)
                {
                    try
                    {
                        var entry = entries[i];
                        if (!entry.Enabled) continue;
                        var entryText = entry.Text;
                        if (entryText != null &&
                            (entryText.Equals(returnText, StringComparison.OrdinalIgnoreCase) ||
                             entryText.Equals("Return Items to Inventory", StringComparison.OrdinalIgnoreCase)))
                        {
                            foundIndex = entry.Index;
                            break;
                        }
                    }
                    catch { continue; }
                }

                if (foundIndex < 0)
                {
                    LogError?.Invoke("[AutoMarket] Could not find exact 'Return Items to Inventory' in context menu", null);
                    return false;
                }

                if (!FireContextMenuEntry(foundIndex))
                {
                    LogError?.Invoke("[AutoMarket] Failed to fire context menu for return-to-player", null);
                    return false;
                }

                await Task.Delay(550, token);
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error in ClickReturnToPlayerInventory: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Clicks the first context menu entry that contains "Return" — moves the listed item to
        /// the retainer's bag (the confirmed-working behavior the user observed).
        /// </summary>
        private async Task<bool> ClickMoveToRetainerBag(CancellationToken token)
        {
            try
            {
                var (contextMenu, ptr) = await WaitForContextMenu(token);
                if (contextMenu == null || ptr == nint.Zero)
                {
                    LogError?.Invoke("[AutoMarket] Context menu not found for move-to-retainer-bag", null);
                    return false;
                }

                string returnText = "Return Items to Inventory";
                try
                {
                    if (Plugin?.DataManager != null)
                    {
                        var addonSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
                        if (addonSheet != null)
                        {
                            var row = addonSheet.GetRow(5482);
                            if (row.RowId != 0)
                            {
                                var text = row.Text.ToString();
                                if (!string.IsNullOrEmpty(text))
                                    returnText = text;
                            }
                        }
                    }
                }
                catch { }

                var entries = contextMenu.Entries;
                if (entries == null)
                {
                    LogError?.Invoke("[AutoMarket] Context menu has no entries (move-to-retainer-bag)", null);
                    return false;
                }

                int foundIndex = -1;
                for (int i = 0; i < entries.Length; i++)
                {
                    try
                    {
                        var entry = entries[i];
                        if (!entry.Enabled) continue;
                        var entryText = entry.Text;
                        if (entryText != null &&
                            (entryText.Equals(returnText, StringComparison.OrdinalIgnoreCase) ||
                             entryText.Contains("Return Items to Inventory", StringComparison.OrdinalIgnoreCase) ||
                             entryText.Contains("Return to Inventory", StringComparison.OrdinalIgnoreCase) ||
                             entryText.Contains("Return", StringComparison.OrdinalIgnoreCase)))
                        {
                            foundIndex = entry.Index;
                            break;
                        }
                    }
                    catch { continue; }
                }

                if (foundIndex < 0)
                {
                    LogError?.Invoke("[AutoMarket] Could not find a 'Return' option in context menu (move-to-retainer-bag)", null);
                    return false;
                }

                if (!FireContextMenuEntry(foundIndex))
                {
                    LogError?.Invoke("[AutoMarket] Failed to fire context menu for move-to-retainer-bag", null);
                    return false;
                }

                await Task.Delay(550, token);
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error in ClickMoveToRetainerBag: {ex.Message}", ex);
                return false;
            }
        }
        
        /// <summary>
        /// Checks if inventory has space for items.
        /// </summary>
        private unsafe bool HasInventorySpace(int requiredSlots = 1)
        {
            try
            {
                var inventoryManager = GetInventoryManagerSafe();
                if (inventoryManager == null) return false;

                InventoryType[] inventoryTypes = {
                    InventoryType.Inventory1,
                    InventoryType.Inventory2,
                    InventoryType.Inventory3,
                    InventoryType.Inventory4
                };

                int freeSlots = 0;
                foreach (var type in inventoryTypes)
                {
                    var container = inventoryManager->GetInventoryContainer(type);
                    if (container == null) continue;

                    for (int i = 0; i < container->Size; i++)
                    {
                        var slot = container->GetInventorySlot(i);
                        if (slot == null || slot->ItemId == 0)
                            freeSlots++;
                    }
                }

                return freeSlots >= requiredSlots;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error checking player inventory space: {ex.Message}", ex);
                return false;
            }
        }

        private unsafe bool HasRetainerBagSpace(int requiredSlots = 1)
        {
            try
            {
                var inventoryManager = GetInventoryManagerSafe();
                if (inventoryManager == null) return false;

                InventoryType[] retainerTypes = {
                    InventoryType.RetainerPage1,
                    InventoryType.RetainerPage2,
                    InventoryType.RetainerPage3,
                    InventoryType.RetainerPage4,
                    InventoryType.RetainerPage5,
                    InventoryType.RetainerPage6,
                    InventoryType.RetainerPage7
                };

                int freeSlots = 0;
                foreach (var type in retainerTypes)
                {
                    var container = inventoryManager->GetInventoryContainer(type);
                    if (container == null) continue;

                    for (int i = 0; i < container->Size; i++)
                    {
                        var slot = container->GetInventorySlot(i);
                        if (slot == null || slot->ItemId == 0)
                            freeSlots++;
                    }
                }

                return freeSlots >= requiredSlots;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error checking retainer bag space: {ex.Message}", ex);
                return false;
            }
        }
        
        /// <summary>
        /// Manages listed items on the retainer's market board.
        /// Adjusts prices for each listed item by undercutting the cheapest market price using Compare Price.
        /// </summary>
        public async Task ManageListedItems(int retainerIndex, CancellationToken token)
        {
            try
            {
                if (!Plugin.Configuration.ManageListedItems)
                {
                    return;
                }
                
                Log?.Invoke("[AutoMarket] Starting price adjustment for listed items...");
                
                // Wait for RetainerSellList to be ready
                bool retainerSellListReady = false;
                for (int attempts = 0; attempts < 30; attempts++)
                {
                    await Task.Delay(110, token);
                    
                    unsafe
                    {
                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerSellList", out var retainerSellList))
                        {
                            if (ECommons.GenericHelpers.IsAddonReady(retainerSellList) && retainerSellList->IsVisible)
                            {
                                retainerSellListReady = true;
                                break;
                            }
                        }
                    }
                }
                
                if (!retainerSellListReady)
                {
                    LogError?.Invoke("[AutoMarket] RetainerSellList not ready for listed item management", null);
                    return;
                }
                
                // Get all listed items
                var listedItems = GetListedItems(retainerIndex);
                if (listedItems.Count == 0)
                {
                    Log?.Invoke("[AutoMarket] No items listed on retainer market board");
                    return;
                }
                
                listedItems = listedItems.Where(item => item.ItemId > 0).ToList();
                Log?.Invoke($"[AutoMarket] Adjusting prices for {listedItems.Count} listed items with resolved item IDs...");
                
                // Process each listed item
                for (int i = 0; i < listedItems.Count && !token.IsCancellationRequested; i++)
                {
                    var listedItem = listedItems[i];
                    
                    Log?.Invoke($"[AutoMarket] Processing listed item {i + 1}/{listedItems.Count} (slot {i})");
                    
                    if (token.IsCancellationRequested) break;
                    if (!await ClickListedItem(i, token))
                    {
                        LogError?.Invoke($"[AutoMarket] Failed to click listed item at slot {i + 1}", null);
                        continue;
                    }
                    
                    if (!await ClickAdjustPrice(token))
                    {
                        LogError?.Invoke($"[AutoMarket] Failed to click Adjust Price for slot {i + 1}", null);
                        continue;
                    }
                    
                    await Task.Delay(220, token);
                    if (token.IsCancellationRequested) break;
                    
                    bool uiReady = await IsRetainerUIReady();
                    if (!uiReady)
                    {
                        LogError?.Invoke("[AutoMarket] Retainer UI is not ready before Compare Price", null);
                        continue;
                    }
                    
                    uint cheapestPrice = 0;
                    var dummyItem = new ScannedItem { ItemName = $"Slot {i + 1}" };
                    
                    try
                    {
                        for (int compareAttempt = 0; compareAttempt < 2 && !token.IsCancellationRequested; compareAttempt++)
                        {
                            if (compareAttempt > 0)
                            {
                                await Task.Delay(198, token);
                                if (token.IsCancellationRequested) break;
                            }
                            
                            bool closedItemSearch = false;
                            unsafe
                            {
                                if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("ItemSearchResult", out var itemSearchAddon))
                                {
                                    if (itemSearchAddon != null && ECommons.GenericHelpers.IsAddonReady(itemSearchAddon))
                                    {
                                        itemSearchAddon->Close(true);
                                        closedItemSearch = true;
                                    }
                                }
                            }
                            
                            if (closedItemSearch)
                            {
                                await Task.Delay(110, token);
                                if (token.IsCancellationRequested) break;
                            }
                            
                            bool clickedCompare = false;
                            bool uiReadyCheck = await IsRetainerUIReady();
                            if (!uiReadyCheck)
                            {
                                LogError?.Invoke("[AutoMarket] Retainer UI is not ready when clicking Compare Price", null);
                                break;
                            }
                            
                            unsafe
                            {
                                
                                if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonRetainerSell>("RetainerSell", out var retainerSell))
                                {
                                    if (retainerSell != null && ECommons.GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
                                    {
                                        var ui = &retainerSell->AtkUnitBase;
                                        if (ui != null)
                                        {
                                            ECommons.Automation.Callback.Fire(ui, true, 4);
                                            clickedCompare = true;
                                        }
                                    }
                                }
                            }
                            
                            if (clickedCompare)
                            {
                                await Task.Delay(198, token);
                                if (token.IsCancellationRequested) break;
                                
                                cheapestPrice = await GetLowestPriceFromComparePrices(dummyItem, token);
                                if (cheapestPrice > 0)
                                {
                                    break;
                                }
                            }
                            else
                            {
                                LogError?.Invoke($"[AutoMarket] RetainerSell not ready for Compare Price attempt {compareAttempt + 1}", null);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError?.Invoke($"[AutoMarket] Error during Compare Price retry logic: {ex.Message}", ex);
                        cheapestPrice = 0;
                    }
                    
                    if (cheapestPrice == 0)
                    {
                        LogError?.Invoke($"[AutoMarket] Could not get market price for slot {i + 1} after retries, canceling and skipping...", null);
                        
                        try
                        {
                            unsafe
                            {
                                if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("ItemSearchResult", out var itemSearchAddon))
                                {
                                    if (itemSearchAddon != null && ECommons.GenericHelpers.IsAddonReady(itemSearchAddon))
                                    {
                                        itemSearchAddon->Close(true);
                                    }
                                }
                                
                                if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonRetainerSell>("RetainerSell", out var retainerSell))
                                {
                                    if (retainerSell != null && ECommons.GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
                                    {
                                        var ui = &retainerSell->AtkUnitBase;
                                        if (ui != null)
                                        {
                                            ECommons.Automation.Callback.Fire(ui, true, 1);
                                            ui->Close(true);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError?.Invoke($"[AutoMarket] Error cleaning up windows: {ex.Message}", ex);
                        }
                        
                        await Task.Delay(220, token);
                        continue;
                    }
                    
                    if (token.IsCancellationRequested) break;
                    
                    uint newPrice = (uint)Math.Max(1, cheapestPrice - Plugin.Configuration.UndercutAmount);
                    Log?.Invoke($"[AutoMarket] Cheapest market price: {cheapestPrice:N0}, new undercut price: {newPrice:N0}");
                    
                    await SetPriceInRetainerSell(newPrice, 1, token);
                    Log?.Invoke($"[AutoMarket] Successfully adjusted price to {newPrice:N0}");
                }
                
                Log?.Invoke("[AutoMarket] Listed item price adjustment complete.");
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error managing listed items: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Sets price in RetainerSell window (reused from existing listing logic).
        /// </summary>
        private async Task<bool> SetPriceInRetainerSell(uint price, int quantity, CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested) return false;
                
                nint retainerSellPtr = nint.Zero;
                bool retainerSellReady = false;
                
                for (int attempts = 0; attempts < 30 && !token.IsCancellationRequested; attempts++)
                {
                    await Task.Delay(66, token);
                    if (token.IsCancellationRequested) return false;
                    
                    unsafe
                    {
                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonRetainerSell>("RetainerSell", out var tempRetainerSell))
                        {
                            if (tempRetainerSell != null && ECommons.GenericHelpers.IsAddonReady(&tempRetainerSell->AtkUnitBase))
                            {
                                retainerSellPtr = (nint)tempRetainerSell;
                                retainerSellReady = true;
                                break;
                            }
                        }
                    }
                }
                
                if (!retainerSellReady || token.IsCancellationRequested)
                {
                    LogError?.Invoke("[AutoMarket] RetainerSell addon not found or not ready when trying to set price", null);
                    return false;
                }
                
                unsafe
                {
                    if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Client.UI.AddonRetainerSell>("RetainerSell", out var retainerSell))
                    {
                        LogError?.Invoke("[AutoMarket] RetainerSell addon not found when trying to set price", null);
                        return false;
                    }
                    
                    if (retainerSell == null || !ECommons.GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
                    {
                        LogError?.Invoke("[AutoMarket] RetainerSell addon not ready when trying to set price", null);
                        return false;
                    }
                    
                    if (retainerSell->AskingPrice == null)
                    {
                        LogError?.Invoke("[AutoMarket] RetainerSell AskingPrice is null", null);
                        return false;
                    }
                    
                    retainerSell->AskingPrice->SetValue((int)price);
                    
                    if (quantity > 1 && retainerSell->Quantity != null)
                    {
                        retainerSell->Quantity->SetValue(quantity);
                    }
                    
                    var ui = &retainerSell->AtkUnitBase;
                    if (ui == null)
                    {
                        LogError?.Invoke("[AutoMarket] RetainerSell AtkUnitBase is null", null);
                        return false;
                    }
                    
                    ECommons.Automation.Callback.Fire(ui, true, 0);
                    ui->Close(true);
                }
                
                await Task.Delay(330, token);
                if (token.IsCancellationRequested) return false;
                return true;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error setting price in RetainerSell: {ex.Message}", ex);
                return false;
            }
        }
        
        /// <summary>
        /// Finds an item in inventory by ItemId and HQ status.
        /// </summary>
        private Task<ScannedItem?> FindItemInInventory(uint itemId, bool isHQ, CancellationToken token)
        {
            try
            {
                unsafe
                {
                    var inventoryManager = GetInventoryManagerSafe();
                    if (inventoryManager == null) return null;
                    
                    if (Plugin?.DataManager == null) return null;
                    
                    var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                    if (itemSheet == null) return null;
                    
                    InventoryType[] inventoryTypes = {
                        InventoryType.Inventory1,
                        InventoryType.Inventory2,
                        InventoryType.Inventory3,
                        InventoryType.Inventory4
                    };
                    
                    foreach (var type in inventoryTypes)
                    {
                        var container = inventoryManager->GetInventoryContainer(type);
                        if (container == null) continue;
                        
                        for (int slot = 0; slot < container->Size; slot++)
                        {
                            var slotItem = container->GetInventorySlot(slot);
                            if (slotItem == null || slotItem->ItemId != itemId) continue;
                            
                            bool slotIsHQ = slotItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                            if (slotIsHQ != isHQ) continue;
                            
                            var itemData = itemSheet.GetRow(itemId);
                            if (itemData.RowId == 0) continue;
                            
                            var itemName = itemData.Name.ToString();
                            if (string.IsNullOrWhiteSpace(itemName))
                            {
                                itemName = $"Item#{itemId}";
                            }
                            
                            return Task.FromResult<ScannedItem?>(new ScannedItem
                            {
                                ItemId = itemId,
                                ItemName = itemName,
                                Quantity = slotItem->Quantity,
                                IsHQ = isHQ,
                                VendorPrice = itemData.PriceLow,
                                StackSize = itemData.StackSize,
                                InventoryType = type,
                                InventorySlot = slot,
                                CanBeListedOnMarketBoard = itemData.ItemSearchCategory.RowId != 0
                            });
                        }
                    }
                }
                
                return Task.FromResult<ScannedItem?>(null);
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error finding item in inventory: {ex.Message}", ex);
                return Task.FromResult<ScannedItem?>(null);
            }
        }

        public async Task<bool> CheckInventorySpaceAsync(int requiredSlots = 1)
        {
            return await RunOnFrameworkThreadAsync(() => HasInventorySpace(requiredSlots));
        }

        public async Task<bool> CheckRetainerBagSpaceAsync(int requiredSlots = 1)
        {
            return await RunOnFrameworkThreadAsync(() => HasRetainerBagSpace(requiredSlots));
        }

        /// <summary>
        /// Opens the context menu for a listed item and clicks the appropriate "return" entry.
        /// </summary>
        /// <param name="itemIndex">Slot index in RetainerSellList (always pass 0 when iterating from top).</param>
        /// <param name="toRetainerBag">True = move to retainer's bag; false = return to player's inventory.</param>
        public async Task<bool> WithdrawListedItem(int itemIndex, bool toRetainerBag, CancellationToken token)
        {
            if (!await ClickListedItem(itemIndex, token)) return false;
            return toRetainerBag
                ? await ClickMoveToRetainerBag(token)
                : await ClickReturnToPlayerInventory(token);
        }

        public async Task<(int itemsWithdrawn, bool spaceFull)> ClearAllListedItems(int retainerIndex, bool toRetainerBag, CancellationToken token)
        {
            int itemsWithdrawn = 0;

            // Wait for RetainerSellList to be visible and ready
            bool ready = false;
            for (int attempts = 0; attempts < 30; attempts++)
            {
                await Task.Delay(110, token);
                unsafe
                {
                    if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RetainerSellList", out var addon)
                        && ECommons.GenericHelpers.IsAddonReady(addon) && addon->IsVisible)
                    {
                        ready = true;
                        break;
                    }
                }
            }

            if (!ready)
            {
                LogError?.Invoke("[AutoMarket] RetainerSellList not ready for clearing", null);
                return (0, false);
            }

            // Read market item count — accurate at session start (just opened retainer)
            int totalToWithdraw = 0;
            unsafe
            {
                var retainerManager = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
                if (retainerManager != null)
                {
                    int validIndex = 0;
                    for (int i = 0; i < retainerManager->Retainers.Length; i++)
                    {
                        var r = retainerManager->Retainers[i];
                        if (r.RetainerId != 0 && r.Name[0] != 0)
                        {
                            if (validIndex == retainerIndex)
                            {
                                totalToWithdraw = r.MarketItemCount;
                                break;
                            }
                            validIndex++;
                        }
                    }
                }
            }

            if (totalToWithdraw == 0)
            {
                Log?.Invoke($"[AutoMarket] Retainer {retainerIndex} has no listed market items to withdraw");
                return (0, false);
            }

            Log?.Invoke($"[AutoMarket] Withdrawing {totalToWithdraw} listed item(s) from retainer {retainerIndex} → {(toRetainerBag ? "retainer bag" : "player inventory")}...");

            for (int i = 0; i < totalToWithdraw && !token.IsCancellationRequested; i++)
            {
                bool hasSpace = toRetainerBag
                    ? await CheckRetainerBagSpaceAsync()
                    : await CheckInventorySpaceAsync();

                if (!hasSpace)
                {
                    string destination = toRetainerBag ? "retainer bag" : "player inventory";
                    Log?.Invoke($"[AutoMarket] {destination} is full — stopping clear early");
                    return (itemsWithdrawn, true);
                }

                // Always withdraw index 0 — items shift up after each withdrawal
                if (!await WithdrawListedItem(0, toRetainerBag, token))
                {
                    LogError?.Invoke($"[AutoMarket] Failed to withdraw item {i + 1}/{totalToWithdraw}", null);
                    break;
                }

                itemsWithdrawn++;
                Log?.Invoke($"[AutoMarket] Withdrew item {itemsWithdrawn}/{totalToWithdraw}");

                await Task.Delay(Plugin.Configuration.ActionDelay, token);
            }

            return (itemsWithdrawn, false);
        }
    }
}
