using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ECommons;
using RetainerManager = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager;
using AddonSelectString = FFXIVClientStructs.FFXIV.Client.UI.AddonSelectString;

namespace AutomarketPro.Automation
{
    /// <summary>
    /// Handles retainer interaction automation - opening and selecting retainers
    /// </summary>
    public class RetainerInteraction
    {
        // AutomarketProPlugin is in AutomarketPro namespace (will be moved to Core later)
        private readonly AutomarketPro.AutomarketProPlugin Plugin;
        
        // Logging delegates - will be set by RetainerAutomation
        public Action<string>? Log { get; set; }
        public Action<string, Exception?>? LogError { get; set; }
        
        public RetainerInteraction(AutomarketProPlugin plugin)
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
        /// Safely gets RetainerManager with retry logic (up to 5 attempts).
        /// Returns null if all attempts fail.
        /// </summary>
        private unsafe RetainerManager* GetRetainerManagerSafe()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var manager = RetainerManager.Instance();
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
        /// Safely gets RaptureAtkUnitManager with retry logic (up to 5 attempts).
        /// Returns null if all attempts fail.
        /// </summary>
        private unsafe FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager* GetRaptureAtkUnitManagerSafe()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var manager = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance();
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
        
        public unsafe int GetRetainerCount()
        {
            try
            {
                // Use RetainerManager.Instance() to access retainer data
                var retainerManager = GetRetainerManagerSafe();
                if (retainerManager == null)
                {
                    LogError?.Invoke("[AutoMarket] RetainerManager is null after retries", null);
                    return 0;
                }
                
                Log?.Invoke($"[AutoMarket] RetainerManager found, Retainers array length: {retainerManager->Retainers.Length}");
                
                // Count retainers that have valid IDs and names
                int count = 0;
                for (int i = 0; i < retainerManager->Retainers.Length; i++)
                {
                    var retainer = retainerManager->Retainers[i];
                    // Valid retainer has RetainerId != 0 and Name[0] != 0
                    if (retainer.RetainerId != 0 && retainer.Name[0] != 0)
                    {
                        // Read name - Name is a SeString span
                        string name = "Unknown";
                        try
                        {
                            unsafe
                            {
                                fixed (byte* ptr = retainer.Name)
                                {
                                    name = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)ptr).ToString();
                                }
                            }
                        }
                        catch
                        {
                            // Fallback: try to read as UTF8 string
                            name = System.Text.Encoding.UTF8.GetString(retainer.Name.ToArray()).TrimEnd('\0');
                        }
                        Log?.Invoke($"[AutoMarket] Found retainer {i}: ID={retainer.RetainerId}, Name='{name}', Available={retainer.Available}");
                        count++;
                    }
                }
                
                Log?.Invoke($"[AutoMarket] Total valid retainers found: {count}");
                return count;
            }
            catch (Exception ex)
            {
                LogError?.Invoke("[AutoMarket] Error getting retainer count", ex);
                return 0;
            }
        }
        
        /// <summary>
        /// Gets the current number of items listed on the market board for a specific retainer.
        /// Uses RetainerManager.MarketItemCount.
        /// </summary>
        public unsafe int GetRetainerMarketItemCount(int retainerIndex)
        {
            try
            {
                var retainerManager = GetRetainerManagerSafe();
                if (retainerManager == null)
                {
                    LogError?.Invoke("[AutoMarket] RetainerManager is null after retries", null);
                    return 0;
                }
                
                // Find the retainer by index (matching the order we use in OpenAndSelectRetainer)
                int validRetainerIndex = 0;
                for (int i = 0; i < retainerManager->Retainers.Length; i++)
                {
                    var retainer = retainerManager->Retainers[i];
                    if (retainer.RetainerId != 0 && retainer.Name[0] != 0)
                    {
                        if (validRetainerIndex == retainerIndex)
                        {
                            int count = retainer.MarketItemCount;
                            Log?.Invoke($"[AutoMarket] Retainer {retainerIndex} has {count} items currently listed on market board");
                            return count;
                        }
                        validRetainerIndex++;
                    }
                }
                
                LogError?.Invoke($"[AutoMarket] Could not find retainer at index {retainerIndex}", null);
                return 0;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error getting retainer market item count: {ex.Message}", ex);
                return 0;
            }
        }
        
        public async Task<bool> OpenAndSelectRetainer(int retainerIndex, CancellationToken token)
        {
            try
            {
                if (Plugin == null)
                {
                    LogError?.Invoke("[AutoMarket] Plugin is null!", null);
                    return false;
                }
                
                await Task.Delay(66, token);
                
                bool eventSent = false;
                nint retainerListNamePtr = nint.Zero;
                nint addonPtr = nint.Zero;
                int foundIndex = -1;
                
                try
                {
                    unsafe
                    {
                        var raptureMgr = GetRaptureAtkUnitManagerSafe();
                        if (raptureMgr == null)
                        {
                            LogError?.Invoke("[AutoMarket] RaptureAtkUnitManager is null after retries", null);
                            return false;
                        }
                        
                        retainerListNamePtr = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("RetainerList");
                        if (retainerListNamePtr == nint.Zero)
                        {
                            LogError?.Invoke("[AutoMarket] Failed to allocate memory for RetainerList", null);
                            return false;
                        }
                        
                        var retainerListBytes = (byte*)retainerListNamePtr.ToPointer();
                        if (retainerListBytes == null)
                        {
                            LogError?.Invoke("[AutoMarket] Failed to get pointer for RetainerList", null);
                            return false;
                        }
                        
                        // Find the addon first
                        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon = null;
                        
                        for (int i = 1; i <= 10; i++)
                        {
                            try
                            {
                                var testAddon = raptureMgr->GetAddonByName(retainerListBytes, i);
                                if (testAddon != null && ECommons.GenericHelpers.IsAddonReady(testAddon))
                                {
                                    addon = testAddon;
                                    foundIndex = i;
                                    addonPtr = (nint)addon;
                                    Log?.Invoke($"[AutoMarket] Found RetainerList at index {i}");
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                LogError?.Invoke($"[AutoMarket] Error checking index {i}: {ex.Message}", ex);
                                continue;
                            }
                        }
                    }
                    
                    // Now run FireCallback on framework thread (outside unsafe block)
                    if (addonPtr != nint.Zero && foundIndex >= 0)
                    {
                        var retainerIdx = retainerIndex;
                        var addonPtrLocal = addonPtr; // Copy for closure
                        
                        Log?.Invoke($"[AutoMarket] Attempting to FireCallback for retainer {retainerIdx} (addonPtr: {addonPtrLocal})");
                        
                        // Use our async helper to properly await the framework thread execution
                        eventSent = await RunOnFrameworkThreadAsync(() =>
                        {
                            unsafe
                            {
                                try
                                {
                                    // Re-validate addon is still valid
                                    var addonToUse = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addonPtrLocal.ToPointer();
                                    if (addonToUse == null)
                                    {
                                        LogError?.Invoke("[AutoMarket] Addon pointer is null when trying to FireCallback", null);
                                        return false;
                                    }
                                    
                                    if (!ECommons.GenericHelpers.IsAddonReady(addonToUse))
                                    {
                                        LogError?.Invoke("[AutoMarket] Addon is not ready when trying to FireCallback", null);
                                        return false;
                                    }
                                    
                                    var v = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[4];
                                    v[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                                    v[0].Int = 2;
                                    v[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt;
                                    v[1].UInt = (uint)retainerIdx;
                                    v[2].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                                    v[2].Int = 0;
                                    v[3].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                                    v[3].Int = 0;
                                    
                                    addonToUse->FireCallback(4, v);
                                    Log?.Invoke($"[AutoMarket] Successfully used FireCallback for retainer {retainerIdx}");
                                    return true;
                                }
                                catch (System.AccessViolationException ex)
                                {
                                    LogError?.Invoke("Access violation in FireCallback", ex);
                                    return false;
                                }
                                catch (Exception ex)
                                {
                                    LogError?.Invoke($"FireCallback failed: {ex.Message}", ex);
                                    return false;
                                }
                            }
                        });
                        
                        Log?.Invoke($"[AutoMarket] FireCallback result: {eventSent}");
                    }
                    else
                    {
                        LogError?.Invoke($"[AutoMarket] Addon not found or invalid (addonPtr: {addonPtr}, foundIndex: {foundIndex})", null);
                    }
                }
                catch (Exception ex)
                {
                    LogError?.Invoke($"[AutoMarket] RaptureAtkUnitManager access failed: {ex.Message}", ex);
                }
                finally
                {
                    if (retainerListNamePtr != nint.Zero)
                    {
                        try
                        {
                            System.Runtime.InteropServices.Marshal.FreeHGlobal(retainerListNamePtr);
                        }
                        catch { }
                    }
                }
                
                // Wait a bit for the callback to process (outside unsafe block)
                if (!eventSent)
                {
                    LogError?.Invoke("[AutoMarket] Failed to select retainer - RetainerList addon not found or FireCallback failed", null);
                    return false;
                }
                
                Log?.Invoke($"[AutoMarket] FireCallback succeeded, waiting for UI to process...");
                await Task.Delay(100, token);
                Log?.Invoke($"[AutoMarket] Successfully sent click event for retainer {retainerIndex}");
                
                // After clicking retainer, wait for SelectString to appear
                // Talk addon clicking is handled by Tick() method running every frame
                Log?.Invoke("[AutoMarket] Waiting for SelectString to appear after clicking retainer...");
                
                bool selectStringFound = false;
                nint selectStringNamePtr = nint.Zero;
                
                try
                {
                    selectStringNamePtr = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("SelectString");
                    if (selectStringNamePtr == nint.Zero)
                    {
                        LogError?.Invoke("[AutoMarket] Failed to allocate memory for SelectString", null);
                        return false;
                    }
                    
                    for (int attempts = 0; attempts < 100; attempts++)
                    {
                        await Task.Delay(110, token);
                        
                        if (token.IsCancellationRequested) break;
                        
                        try
                        {
                            bool selectStringFoundCheck = await RunOnFrameworkThreadAsync(() =>
                            {
                                unsafe
                                {
                                    try
                                    {
                                        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectString", out var selectStringAddon))
                                        {
                                            if (selectStringAddon != null && ECommons.GenericHelpers.IsAddonReady(selectStringAddon))
                                            {
                                                Log?.Invoke("[AutoMarket] Found SelectString");
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
                            
                            if (selectStringFoundCheck)
                            {
                                selectStringFound = true;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError?.Invoke($"[AutoMarket] Error checking SelectString (attempt {attempts}): {ex.Message}", ex);
                            await Task.Delay(55, token);
                            continue;
                        }
                    }
                }
                finally
                {
                    if (selectStringNamePtr != nint.Zero)
                    {
                        try
                        {
                            System.Runtime.InteropServices.Marshal.FreeHGlobal(selectStringNamePtr);
                        }
                        catch { }
                    }
                }
                
                if (!selectStringFound)
                {
                    Log?.Invoke("[AutoMarket] WARNING: SelectString not found after clicking retainer");
                    return false;
                }
                
                // Now select "Sell items in your inventory on the market" option directly
                // Use the SelectString we already found instead of searching again
                Log?.Invoke("[AutoMarket] SelectString found, now selecting 'Sell items in your inventory on the market' option...");
                await Task.Delay(330, token); // Small delay to ensure SelectString is fully ready
                
                await Task.Delay(220, token);
                
                bool optionFound = await RunOnFrameworkThreadAsync(() =>
                {
                    unsafe
                    {
                        try
                        {
                            // Re-validate SelectString addon before accessing
                            if (!ECommons.GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var addon))
                            {
                                LogError?.Invoke("[AutoMarket] SelectString addon not found", null);
                                return false;
                            }
                            
                            if (addon == null)
                            {
                                return false;
                            }
                            
                            var atkUnitBasePtr = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon;
                            if (atkUnitBasePtr == null || !ECommons.GenericHelpers.IsAddonReady(atkUnitBasePtr))
                            {
                                LogError?.Invoke("[AutoMarket] SelectString addon not ready", null);
                                return false;
                            }
                            
                            ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectString selectString = null;
                            try
                            {
                                selectString = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectString(addon);
                            }
                            catch (System.AccessViolationException ex)
                            {
                                LogError?.Invoke("Access violation creating SelectString wrapper", ex);
                                return false;
                            }
                            catch (Exception ex)
                            {
                                LogError?.Invoke($"[AutoMarket] Failed to create SelectString wrapper: {ex.Message}", ex);
                                return false;
                            }
                            
                            if (selectString == null || selectString.Entries == null)
                            {
                                LogError?.Invoke("[AutoMarket] SelectString.Entries is null", null);
                                return false;
                            }
                            
                            string[] searchTexts = { 
                                "Sell items in your inventory on the market",
                                "Sell items in your inventory",
                                "Sell items",
                                "inventory on the market"
                            };
                            
                            try
                            {
                                if (Plugin?.DataManager != null)
                                {
                                    var addonSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
                                    if (addonSheet != null)
                                    {
                                        var row99 = addonSheet.GetRow(99);
                                        if (row99.RowId != 0)
                                        {
                                            var row99Text = row99.Text.ToString();
                                            if (!string.IsNullOrEmpty(row99Text))
                                            {
                                                var additionalTexts = new List<string>(searchTexts) { row99Text, "Put Up for Sale" };
                                                searchTexts = additionalTexts.ToArray();
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                LogError?.Invoke($"[AutoMarket] Error getting Addon sheet text: {ex.Message}", ex);
                            }
                            
                            int foundEntryIndex = -1;
                            string foundEntryText = null;
                            
                            foreach (var entry in selectString.Entries)
                            {
                                try
                                {
                                    // Safely access entry.Text - this accesses text nodes which can be null
                                    string entryText = null;
                                    try
                                    {
                                        entryText = entry.Text;
                                    }
                                    catch (System.AccessViolationException)
                                    {
                                        // Text node is invalid, skip this entry
                                        continue;
                                    }
                                    catch (Exception)
                                    {
                                        // Error accessing text, skip this entry
                                        continue;
                                    }
                                    
                                    if (string.IsNullOrEmpty(entryText)) continue;
                                    
                                    bool matches = searchTexts.Any(searchText => 
                                        entryText.Equals(searchText, StringComparison.OrdinalIgnoreCase) ||
                                        entryText.Contains(searchText, StringComparison.OrdinalIgnoreCase));
                                    
                                    if (matches)
                                    {
                                        foundEntryIndex = entry.Index;
                                        foundEntryText = entryText;
                                        break;
                                    }
                                }
                                catch (System.AccessViolationException)
                                {
                                    // Access violation accessing entry, skip it
                                    continue;
                                }
                                catch (Exception ex)
                                {
                                    LogError?.Invoke($"[AutoMarket] Error processing SelectString entry: {ex.Message}", ex);
                                    continue;
                                }
                            }
                            
                            if (foundEntryIndex < 0)
                            {
                                LogError?.Invoke("[AutoMarket] Could not find 'Sell items in your inventory on the market' option in SelectString", null);
                                return false;
                            }
                            
                            // Now fire the callback
                            try
                            {
                                // Re-validate addon is still valid before FireCallback
                                if (atkUnitBasePtr == null || !ECommons.GenericHelpers.IsAddonReady(atkUnitBasePtr))
                                {
                                    LogError?.Invoke("[AutoMarket] SelectString addon no longer ready when clicking", null);
                                    return false;
                                }
                                
                                var values = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[1]
                                {
                                    new()
                                    {
                                        Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int,
                                        Int = foundEntryIndex
                                    }
                                };
                                atkUnitBasePtr->FireCallback(0, values);
                                Log?.Invoke($"[AutoMarket] Selected '{foundEntryText}' from SelectString");
                                return true;
                            }
                            catch (System.AccessViolationException ex)
                            {
                                LogError?.Invoke("Access violation clicking SelectString", ex);
                                return false;
                            }
                            catch (Exception ex)
                            {
                                LogError?.Invoke($"Error clicking SelectString: {ex.Message}", ex);
                                return false;
                            }
                        }
                        catch (System.AccessViolationException ex)
                        {
                            LogError?.Invoke("Access violation in SelectString processing", ex);
                            return false;
                        }
                        catch (Exception ex)
                        {
                            LogError?.Invoke($"[AutoMarket] Error selecting option from SelectString: {ex.Message}", ex);
                            return false;
                        }
                    }
                });
                
                // Wait for callback to process (outside unsafe block)
                if (!optionFound)
                {
                    return false;
                }
                
                await Task.Delay(100, token);
                await Task.Delay(330, token);
                return true;
            }
            catch (NullReferenceException nre)
            {
                LogError?.Invoke($"[AutoMarket] NullReferenceException selecting retainer {retainerIndex}: {nre.Message}", nre);
                LogError?.Invoke($"[AutoMarket] Stack trace: {nre.StackTrace}", null);
                if (nre.TargetSite != null)
                {
                    LogError?.Invoke($"[AutoMarket] TargetSite: {nre.TargetSite}", null);
                }
                return false;
            }
            catch (Exception ex)
            {
                LogError?.Invoke($"[AutoMarket] Error selecting retainer {retainerIndex}: {ex.Message}", ex);
                LogError?.Invoke($"[AutoMarket] Stack trace: {ex.StackTrace}", null);
                return false;
            }
        }
    }
}

