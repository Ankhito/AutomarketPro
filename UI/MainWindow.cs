using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using ImGui = Dalamud.Bindings.ImGui.ImGui;
using AutomarketPro.Models;
using AutomarketPro.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using InventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager;

namespace AutomarketPro.UI
{
    public class MainWindow : Window, IDisposable
    {
        private readonly AutomarketPro.AutomarketProPlugin Plugin;
        private readonly MarketScanner Scanner;
        private readonly RetainerAutomation Automation;
        private string AutomationStatus = "Ready";
        private string LastScanStatus = "No scan yet";
        private bool forceVisiblePlacement = false;
        public bool ShowSettingsTab = false;
        
        // Ignore tab - inventory items cache
        private List<IgnoreItemInfo> IgnoreTabItems = new List<IgnoreItemInfo>();
        private bool IgnoreTabScanning = false;
        private DateTime LastIgnoreTabScan = DateTime.MinValue;
        private const int IgnoreTabScanIntervalSeconds = 5; // Scan every 5 seconds
        private bool IgnoreAllMode = false; // false = "Ignore All", true = "Include All"
        
        // Debug log system - thread-safe collection for messages from anywhere
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> DebugLogMessages = new();
        private const int MaxDebugLogMessages = 500; // Limit log size
        
        // Helper method to log to both debug tab and dalamud.log
        public void Log(string message)
        {
            if (!Plugin?.Configuration?.DebugLogsEnabled ?? false)
                return;
                
            DebugLog(message);
            Plugin?.PluginLog?.Info(message);
        }
        
        public void LogError(string message, Exception? ex = null)
        {
            if (!Plugin?.Configuration?.DebugLogsEnabled ?? false)
                return;
                
            DebugLog($"[ERROR] {message}");
            if (ex != null)
                Plugin?.PluginLog?.Error(ex, message);
            else
                Plugin?.PluginLog?.Error(message);
        }
        
        public void LogWarning(string message)
        {
            if (!Plugin?.Configuration?.DebugLogsEnabled ?? false)
                return;
                
            DebugLog($"[WARN] {message}");
            Plugin?.PluginLog?.Warning(message);
        }
        
        public void DebugLog(string message)
        {
            if (!Plugin?.Configuration?.DebugLogsEnabled ?? false)
                return;
                
            try
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] {message}";
                
                // Add to thread-safe queue
                DebugLogMessages.Enqueue(logEntry);
                
                // Trim queue if it gets too large
                while (DebugLogMessages.Count > MaxDebugLogMessages)
                {
                    DebugLogMessages.TryDequeue(out _);
                }
            }
            catch
            {
            }
        }
        
        private List<string> GetDebugLogMessages()
        {
            return DebugLogMessages.ToList();
        }
        
        public void ClearDebugLog()
        {
            while (DebugLogMessages.TryDequeue(out _)) { }
        }
        
        private void DrawDebugTab()
        {
            SafeText("Debug Log");
            ImGui.Separator();
            
            // Button row
            if (ImGui.Button("Clear Log", new Vector2(120, 25)))
            {
                ClearDebugLog();
            }
            
            ImGui.SameLine();
            
            // Copy to clipboard button
            if (ImGui.Button("Copy to Clipboard", new Vector2(150, 25)))
            {
                try
                {
                    var messages = GetDebugLogMessages();
                    if (messages != null && messages.Count > 0)
                    {
                        string logText = string.Join("\n", messages);
                        ImGui.SetClipboardText(logText);
                        Log("[AutoMarket] Debug log copied to clipboard");
                    }
                    else
                    {
                        Log("[AutoMarket] No debug messages to copy");
                    }
                }
                catch (Exception ex)
                {
                    LogError("[AutoMarket] Error copying to clipboard", ex);
                }
            }
            
            ImGui.SameLine();
            
            // Count of messages
            var count = DebugLogMessages.Count;
            SafeText($"({count} messages)");
            
            ImGui.Separator();
            
            // Scrollable area for log messages
            var windowHeight = ImGui.GetContentRegionAvail().Y - 50; // Leave space for controls
            if (windowHeight < 100) windowHeight = 100; // Minimum height
            
            if (ImGui.BeginChild("DebugLogScroll", new Vector2(-1, windowHeight), true, ImGuiWindowFlags.HorizontalScrollbar))
            {
                var messages = GetDebugLogMessages();
                
                if (messages.Count == 0)
                {
                    SafeTextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No debug messages yet. Debug messages will appear here.");
                }
                else
                {
                    // Show messages (newest at bottom, scroll to bottom)
                    foreach (var msg in messages)
                    {
                        SafeText(msg);
                    }
                    
                    // Auto-scroll to bottom
                    if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1)
                    {
                        ImGui.SetScrollHereY(1.0f);
                    }
                }
                
                ImGui.EndChild();
            }
        }
        
        // Safe ImGui text helpers - use TextUnformatted like SamplePlugin does
        // TextUnformatted is safer for dynamic/interpolated strings
        private static void SafeText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                ImGui.TextUnformatted(""); // Empty string is safe
                return;
            }
            
            // Ensure string is valid - replace any null chars and sanitize
            var safeText = text.Replace('\0', ' ').Trim();
            if (string.IsNullOrEmpty(safeText))
            {
                ImGui.TextUnformatted("");
                return;
            }
            
            // Use TextUnformatted like the official SamplePlugin does
            ImGui.TextUnformatted(safeText);
        }
        
        private static void SafeTextColored(Vector4 color, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                ImGui.TextColored(color, "");
                return;
            }
            
            var safeText = text.Replace('\0', ' ').Trim();
            if (string.IsNullOrEmpty(safeText))
            {
                ImGui.TextColored(color, "");
                return;
            }
            
            // For colored text, we still use TextColored (SamplePlugin doesn't use colored text)
            ImGui.TextColored(color, safeText);
        }
        
        private static void SafeTextWrapped(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                ImGui.TextWrapped("");
                return;
            }
            
            var safeText = text.Replace('\0', ' ').Trim();
            if (string.IsNullOrEmpty(safeText))
            {
                ImGui.TextWrapped("");
                return;
            }
            
            ImGui.TextWrapped(safeText);
        }
        
        public MainWindow(AutomarketPro.AutomarketProPlugin plugin, MarketScanner scanner, RetainerAutomation automation) 
            : base("AutoMarket Pro")
        {
            Plugin = plugin;
            Scanner = scanner;
            Automation = automation;
            
            Size = new Vector2(800, 600);
            SizeCondition = Dalamud.Bindings.ImGui.ImGuiCond.FirstUseEver;
            Flags = ImGuiWindowFlags.NoCollapse;
            
            Automation.StatusUpdate += (status) => AutomationStatus = string.IsNullOrEmpty(status) ? "Ready" : status;
            
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(700, 500),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };
        }

        public void OpenVisible(bool settingsTab = false)
        {
            ShowSettingsTab = settingsTab;
            forceVisiblePlacement = true;
            IsOpen = true;
            Plugin?.PluginLog?.Info($"[AutoMarket] MainWindow.OpenVisible(settingsTab={settingsTab})");
        }

        public override void PreDraw()
        {
            if (!forceVisiblePlacement)
                return;

            forceVisiblePlacement = false;

            try
            {
                var viewport = ImGui.GetMainViewport();
                var workPos = viewport.WorkPos;
                var workSize = viewport.WorkSize;
                var size = new Vector2(MathF.Min(900f, MathF.Max(700f, workSize.X - 80f)), MathF.Min(650f, MathF.Max(500f, workSize.Y - 80f)));
                var pos = workPos + new Vector2(MathF.Max(20f, (workSize.X - size.X) * 0.5f), MathF.Max(20f, (workSize.Y - size.Y) * 0.5f));

                ImGui.SetNextWindowSize(size, ImGuiCond.Always);
                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                Plugin?.PluginLog?.Info($"[AutoMarket] Forced window visible at {pos.X:N0},{pos.Y:N0} size {size.X:N0}x{size.Y:N0}");
            }
            catch (Exception ex)
            {
                Plugin?.PluginLog?.Error(ex, "[AutoMarket] Failed to force visible window placement");
            }
        }

        public void DrawDirect()
        {
            if (!IsOpen)
                return;

            try
            {
                PreDraw();

                var open = IsOpen;
                if (ImGui.Begin("AutoMarket Pro##AutoMarketProMain", ref open, Flags))
                {
                    Draw();
                }
                ImGui.End();
                IsOpen = open;
            }
            catch (Exception ex)
            {
                Plugin?.PluginLog?.Error(ex, "[AutoMarket] Direct window draw failed");
            }
        }
        
        public override void Draw()
        {
            // Step 1: Basic header (working)
            ImGui.TextUnformatted("AutoMarket Pro - Automated Market Board & Vendor Sales");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "v1.0");
            ImGui.Separator();
            
            // Step 2: Restore DrawControlBar (with try-catch for safety)
            try
            {
                DrawControlBar();
            }
            catch (Exception ex)
            {
                ImGui.TextUnformatted($"Error in ControlBar: {ex?.Message ?? "Unknown"}");
                if (Plugin?.PluginLog != null)
                    Plugin.PluginLog.Error(ex, "DrawControlBar error");
            }
            
            ImGui.Separator();
            
            // Add spacing before tabs
            ImGui.Spacing();
            
            // Step 3: Restore tab bar structure
            if (ImGui.BeginTabBar("MainTabs"))
            {
                // Dashboard tab
                if (ImGui.BeginTabItem("Dashboard"))
                {
                    ImGui.Spacing(); // Add spacing inside tab
                    try
                    {
                        DrawDashboard();
                    }
                    catch (Exception ex)
                    {
                        ImGui.TextUnformatted($"Error in Dashboard: {ex?.Message ?? "Unknown"}");
                    }
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Scan Results"))
                {
                    ImGui.Spacing();
                    try
                    {
                        DrawResultsTable();
                    }
                    catch (Exception ex)
                    {
                        ImGui.TextUnformatted($"Error in ResultsTable: {ex?.Message ?? "Unknown"}");
                    }
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Automation"))
                {
                    ImGui.Spacing();
                    try
                    {
                        DrawAutomationTab();
                    }
                    catch (Exception ex)
                    {
                        ImGui.TextUnformatted($"Error in AutomationTab: {ex?.Message ?? "Unknown"}");
                    }
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Ignore"))
                {
                    ImGui.Spacing();
                    try
                    {
                        DrawIgnoreTab();
                    }
                    catch (Exception ex)
                    {
                        ImGui.TextUnformatted($"Error in IgnoreTab: {ex?.Message ?? "Unknown"}");
                    }
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Clear"))
                {
                    ImGui.Spacing();
                    try
                    {
                        DrawClearTab();
                    }
                    catch (Exception ex)
                    {
                        ImGui.TextUnformatted($"Error in ClearTab: {ex?.Message ?? "Unknown"}");
                    }
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Settings"))
                {
                    if (ShowSettingsTab)
                        ShowSettingsTab = false;

                    ImGui.Spacing();
                    try
                    {
                        DrawSettingsTab();
                    }
                    catch (Exception ex)
                    {
                        ImGui.TextUnformatted($"Error in SettingsTab: {ex?.Message ?? "Unknown"}");
                    }
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Debug"))
                {
                    ImGui.Spacing();
                    try
                    {
                        DrawDebugTab();
                    }
                    catch (Exception ex)
                    {
                        ImGui.TextUnformatted($"Error in DebugTab: {ex?.Message ?? "Unknown"}");
                    }
                    ImGui.EndTabItem();
                }
                
                ImGui.EndTabBar();
            }
        }
        
        private void DrawControlBar()
        {
            try
            {
                if (Automation == null || Scanner == null)
                {
                    ImGui.Text("Components not initialized yet...");
                    return;
                }
                
                // Quick action buttons
                if (!Automation.Running)
                {
                    if (ImGui.Button("[>] Start Full Cycle", new Vector2(130, 25)))
                    {
                        Task.Run(async () => await Automation.StartFullCycle());
                    }
                    
                    ImGui.SameLine();
                    if (ImGui.Button("[S] Scan Only", new Vector2(100, 25)))
                    {
                        StartScanOnly();
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("[C] Clear", new Vector2(75, 25)))
                    {
                        Task.Run(async () => await Automation.StartClearCycle());
                    }
                }
                else
                {
                    if (ImGui.Button("[X] Stop", new Vector2(130, 25)))
                    {
                        Automation.StopAutomation();
                        Scanner.StopScanning();
                    }
                    
                    ImGui.SameLine();
                    if (ImGui.Button("[S] Scan Only", new Vector2(100, 25)))
                    {
                        StartScanOnly();
                    }
                }
            
            ImGui.SameLine();
            ImGui.Text("|");
            ImGui.SameLine();
            
            // Status display
            if (Scanner.Scanning)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), "[*] Scanning...");
            }
            else if (Automation.Running)
            {
                ImGui.TextColored(new Vector4(0, 1, 0, 1), "[>] Running");
                
                ImGui.SameLine();
                var safeStatus = string.IsNullOrEmpty(AutomationStatus) ? "Ready" : AutomationStatus;
                SafeText($"- {safeStatus}");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "[_] Idle");
                ImGui.SameLine();
                SafeText($"- {LastScanStatus}");
            }
            }
            catch (Exception ex)
            {
                SafeTextColored(new Vector4(1, 0, 0, 1), $"Error: {ex?.Message ?? "Unknown"}");
                LogError("[AutoMarket] DrawControlBar error", ex);
            }
        }
        
        private void DrawDashboard()
        {
            try
            {
                if (Scanner == null)
                {
                    ImGui.Text("Scanner not initialized yet...");
                    return;
                }
                
                var items = Scanner.Items ?? new List<ScannedItem>();
                var profitable = items.Count(i => i.IsProfitable);
                var unprofitable = items.Count - profitable;
                var totalProfit = items.Sum(i => i.TotalProfit);
                var totalMarketValue = items.Sum(i => i.ListingPrice * i.Quantity);
                var totalVendorValue = items.Sum(i => i.VendorPrice * i.Quantity);
            
            // Stats component - NO scrollable child, just regular layout
            // This prevents overlap issues
            ImGui.Text("Statistics Summary");
            ImGui.Separator();
            
            ImGui.Columns(4, "StatsColumns", false);
            
            ImGui.Text("Total Items");
            SafeText($"{items.Count}");
            ImGui.NextColumn();
            
            ImGui.Text("Profitable (MB)");
            SafeTextColored(new Vector4(0, 1, 0, 1), $"{profitable}");
            ImGui.NextColumn();
            
            ImGui.Text("Unprofitable (Vendor)");
            SafeTextColored(new Vector4(1, 0.5f, 0, 1), $"{unprofitable}");
            ImGui.NextColumn();
            
            ImGui.Text("Potential Profit");
            SafeTextColored(new Vector4(0, 1, 0, 1), $"{totalProfit:N0} gil");
            
            ImGui.Columns(1);
            ImGui.Separator();
            
            SafeText($"Market Value: {totalMarketValue:N0} gil");
            SafeText($"Vendor Value: {totalVendorValue:N0} gil");
            SafeText($"Difference: +{totalMarketValue - totalVendorValue:N0} gil");

            ImGui.Separator();
            ImGui.Dummy(new Vector2(0, 10));
            ImGui.Text("Posted Marketboard Value");
            ImGui.Separator();

            var postedGil = Automation.GetPostedGilSummary();
            if (postedGil.HasValue)
            {
                SafeTextColored(new Vector4(0, 1, 0, 1), $"{postedGil.PostedGil:N0} gil waiting to sell");
                SafeText($"{postedGil.Listings} listing(s) across {postedGil.RetainersChecked} retainer(s)");
                SafeText($"Last refreshed: {postedGil.LastUpdated:g}");
                if (postedGil.RetainersFailed > 0)
                {
                    SafeTextColored(new Vector4(1, 0.5f, 0, 1), $"{postedGil.RetainersFailed} retainer(s) could not be read");
                }
            }
            else
            {
                SafeTextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "Not refreshed yet");
            }

            var refreshDisabled = Automation.Running || Automation.PostedGilRefreshRunning || Scanner.Scanning;
            if (refreshDisabled)
            {
                ImGui.BeginDisabled();
            }

            var refreshLabel = Automation.PostedGilRefreshRunning ? "Refreshing posted gil..." : "Refresh posted gil";
            if (ImGui.Button(refreshLabel, new Vector2(180, 25)))
            {
                Task.Run(async () => await Automation.RefreshPostedGilSummary());
            }

            if (refreshDisabled)
            {
                ImGui.EndDisabled();
            }
            
            // Clear separation before next section
            ImGui.Separator();
            ImGui.Dummy(new Vector2(0, 20)); // Large vertical spacing
            ImGui.Separator();
            
            // Last run summary - with proper spacing
            ImGui.Dummy(new Vector2(0, 10)); // Explicit vertical spacing
            ImGui.Text("Last Run Summary");
            ImGui.Separator();
            
            var summary = Automation.GetLastRunSummary();
            if (summary.TotalItems > 0)
            {
                ImGui.BulletText($"Items Listed: {summary.ItemsListed}");
                ImGui.BulletText($"Items Vendored: {summary.ItemsVendored}");
                ImGui.BulletText($"Estimated Revenue: {summary.EstimatedRevenue:N0} gil");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No automation runs yet");
            }
            }
            catch (Exception ex)
            {
                SafeTextColored(new Vector4(1, 0, 0, 1), $"Error in DrawDashboard: {ex?.Message ?? "Unknown"}");
                LogError("[AutoMarket] DrawDashboard error", ex);
            }
        }
        
        private void DrawResultsTable()
        {
            try
            {
                if (Scanner == null)
                {
                    ImGui.Text("Scanner not initialized yet...");
                    return;
                }
                
                // Display current world at the top
                var worldName = Scanner.CurrentWorldName;
                if (string.IsNullOrEmpty(worldName))
                {
                    SafeTextColored(new Vector4(0.8f, 0.8f, 0.2f, 1), "Current World: Loading...");
                }
                else
                {
                    SafeText($"Current World: {worldName}");
                }
                
                ImGui.Separator();
                
                var items = Scanner.Items ?? new List<ScannedItem>();
                
                SafeText($"Inventory / Retainer Scan Results ({items.Count} item stacks)");
                ImGui.Separator();
                
                if (items.Count == 0)
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No items scanned yet. Click 'Scan Only' to begin.");
                    return;
                }
                
                if (ImGui.BeginTabBar("ScanResultSources"))
                {
                    if (ImGui.BeginTabItem($"All ({items.Count})"))
                    {
                        DrawResultsItemsTable(items, "All");
                        ImGui.EndTabItem();
                    }

                    var inventoryItems = items.Where(item => item.SourceRetainerIndex == null).ToList();
                    if (ImGui.BeginTabItem($"Inventory ({inventoryItems.Count})"))
                    {
                        DrawResultsItemsTable(inventoryItems, "Inventory");
                        ImGui.EndTabItem();
                    }

                    foreach (var group in items
                        .Where(item => item.SourceRetainerIndex != null)
                        .GroupBy(item => item.SourceRetainerIndex!.Value)
                        .OrderBy(group => group.Key))
                    {
                        var sourceItems = group.ToList();
                        var sourceName = sourceItems.FirstOrDefault()?.SourceName ?? $"Retainer {group.Key + 1}";
                        if (ImGui.BeginTabItem($"{sourceName} ({sourceItems.Count})"))
                        {
                            DrawResultsItemsTable(sourceItems, sourceName);
                            ImGui.EndTabItem();
                        }
                    }

                    ImGui.EndTabBar();
                }
            }
            catch (Exception ex)
            {
                SafeTextColored(new Vector4(1, 0, 0, 1), $"Error in DrawResultsTable: {ex?.Message ?? "Unknown"}");
                LogError("[AutoMarket] DrawResultsTable error", ex);
            }
        }

        private void StartScanOnly()
        {
            Log("[AutoMarket] [SCAN] Button clicked - starting scan...");
            LastScanStatus = "Starting scan...";

            _ = Task.Run(async () =>
            {
                try
                {
                    Log("[AutoMarket] [SCAN] Task.Run lambda started");
                    var result = await Scanner.StartScanning();
                    var itemCount = Scanner.Items?.Count ?? 0;
                    LastScanStatus = result
                        ? $"Last scan: {itemCount} item stack(s)"
                        : "Last scan failed";
                    Log($"[AutoMarket] [SCAN] StartScanning() returned: {result}; items={itemCount}");
                }
                catch (Exception ex)
                {
                    LastScanStatus = "Last scan errored";
                    LogError("[AutoMarket] Scan button error", ex);
                }
            });
        }

        private void DrawResultsItemsTable(IReadOnlyList<ScannedItem> items, string tableIdSuffix)
        {
            if (items.Count == 0)
            {
                SafeTextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No item stacks in this source.");
                return;
            }

            if (ImGui.BeginTable($"ItemsTable##{tableIdSuffix}", 12,
                ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 200);
                ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 30);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("Vendor", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Market", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("List Price", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Sales 24h", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Score", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Health", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Profit/Item", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Total Profit", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableHeadersRow();

                var displayItems = ApplyResultsSort(items);

                foreach (var item in displayItems)
                {
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    SafeText(item.SourceName);

                    ImGui.TableSetColumnIndex(1);
                    SafeText(string.IsNullOrEmpty(item.ItemName) ? $"Item#{item.ItemId}" : item.ItemName);

                    ImGui.TableSetColumnIndex(2);
                    if (item.IsHQ)
                        ImGui.TextColored(new Vector4(1, 0.8f, 0.4f, 1), "[HQ]");

                    ImGui.TableSetColumnIndex(3);
                    SafeText($"{item.Quantity}");

                    ImGui.TableSetColumnIndex(4);
                    SafeText($"{item.VendorPrice:N0}");

                    ImGui.TableSetColumnIndex(5);
                    SafeText($"{item.MarketPrice:N0}");

                    ImGui.TableSetColumnIndex(6);
                    SafeText($"{item.ListingPrice:N0}");

                    ImGui.TableSetColumnIndex(7);
                    SafeText($"{item.Sales24h:N0}");

                    ImGui.TableSetColumnIndex(8);
                    SafeText($"{item.SellabilityScore:0.00}");

                    ImGui.TableSetColumnIndex(9);
                    SafeText(item.MarketHealth);

                    ImGui.TableSetColumnIndex(10);
                    if (item.ProfitPerItem > 0)
                        SafeTextColored(new Vector4(0, 1, 0, 1), $"+{item.ProfitPerItem:N0}");
                    else if (item.ProfitPerItem < 0)
                        SafeTextColored(new Vector4(1, 0, 0, 1), $"{item.ProfitPerItem:N0}");
                    else
                        ImGui.Text("0");

                    ImGui.TableSetColumnIndex(11);
                    if (item.TotalProfit > 0)
                        SafeTextColored(new Vector4(0, 1, 0, 1), $"+{item.TotalProfit:N0}");
                    else if (item.TotalProfit < 0)
                        SafeTextColored(new Vector4(1, 0, 0, 1), $"{item.TotalProfit:N0}");
                    else
                        ImGui.Text("0");
                }

                ImGui.EndTable();
            }
        }

        private static List<ScannedItem> ApplyResultsSort(IReadOnlyList<ScannedItem> items)
        {
            var sorted = items.ToList();
            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsCount > 0)
            {
                var spec = sortSpecs.Specs;
                var columnIndex = spec.ColumnIndex;
                var descending = spec.SortDirection == ImGuiSortDirection.Descending;

                sorted = columnIndex switch
                {
                    0 => SortByText(sorted, item => item.SourceName, descending),
                    1 => SortByText(sorted, item => string.IsNullOrEmpty(item.ItemName) ? $"Item#{item.ItemId}" : item.ItemName, descending),
                    2 => SortBy(sorted, item => item.IsHQ ? 1 : 0, descending),
                    3 => SortBy(sorted, item => item.Quantity, descending),
                    4 => SortBy(sorted, item => item.VendorPrice, descending),
                    5 => SortBy(sorted, item => item.MarketPrice, descending),
                    6 => SortBy(sorted, item => item.ListingPrice, descending),
                    7 => SortBy(sorted, item => item.Sales24h, descending),
                    8 => SortBy(sorted, item => item.SellabilityScore, descending),
                    9 => SortByText(sorted, item => item.MarketHealth, descending),
                    10 => SortBy(sorted, item => item.ProfitPerItem, descending),
                    11 => SortBy(sorted, item => item.TotalProfit, descending),
                    _ => sorted
                };

                if (sortSpecs.SpecsDirty)
                    sortSpecs.SpecsDirty = false;
            }

            return sorted;
        }

        private static List<ScannedItem> SortBy<TKey>(IEnumerable<ScannedItem> items, Func<ScannedItem, TKey> keySelector, bool descending)
        {
            return descending
                ? items.OrderByDescending(keySelector).ToList()
                : items.OrderBy(keySelector).ToList();
        }

        private static List<ScannedItem> SortByText(IEnumerable<ScannedItem> items, Func<ScannedItem, string> keySelector, bool descending)
        {
            return descending
                ? items.OrderByDescending(keySelector, StringComparer.OrdinalIgnoreCase).ToList()
                : items.OrderBy(keySelector, StringComparer.OrdinalIgnoreCase).ToList();
        }
        
        private void DrawAutomationTab()
        {
            try
            {
                if (Automation == null || Scanner == null)
                {
                    ImGui.Text("Components not initialized yet...");
                    return;
                }
                
                ImGui.Text("Retainer Automation Control");
                ImGui.Separator();
                
                ImGui.TextWrapped("This automation will:");
                ImGui.Indent();
                ImGui.BulletText("Scan your inventory for all sellable items");
                ImGui.BulletText("Check current market prices via Universalis");
                ImGui.BulletText("List profitable items on the Market Board");
                ImGui.BulletText("Have retainers vendor-sell unprofitable items");
                ImGui.BulletText("Cycle through all available retainers");
                ImGui.Unindent();
                
                ImGui.Separator();
                
                // Automation status
                if (Automation.Running)
                {
                    SafeTextColored(new Vector4(0, 1, 0, 1), "[*] Automation Active");
                    var safeStatus = string.IsNullOrEmpty(AutomationStatus) ? "Ready" : AutomationStatus;
                    SafeText($"Status: {safeStatus}");
                    
                    if (ImGui.Button("Stop", new Vector2(100, 30)))
                    {
                        Automation.StopAutomation();
                        Scanner.StopScanning();
                    }
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "[_] Automation Inactive");
                    SafeText("Use the buttons in the control bar at the top to start automation.");
                }
                
                ImGui.Separator();
                ImGui.Text("Automation Options");
                
                var listOnlyMode = Plugin.Configuration.ListOnlyMode;
                if (ImGui.Checkbox("List Only Mode", ref listOnlyMode))
                {
                    Plugin.Configuration.ListOnlyMode = listOnlyMode;
                    // If List Only Mode is enabled, disable Vendor Only Mode
                    if (listOnlyMode && Plugin.Configuration.VendorOnlyMode)
                    {
                        Plugin.Configuration.VendorOnlyMode = false;
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("When enabled, only lists items that pass the profitability and sellability checks; skips vendoring.");
                    
                var vendorOnlyMode = Plugin.Configuration.VendorOnlyMode;
                if (ImGui.Checkbox("Vendor Only Mode", ref vendorOnlyMode))
                {
                    Plugin.Configuration.VendorOnlyMode = vendorOnlyMode;
                    // If Vendor Only Mode is enabled, disable List Only Mode
                    if (vendorOnlyMode && Plugin.Configuration.ListOnlyMode)
                    {
                        Plugin.Configuration.ListOnlyMode = false;
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("When enabled, only vendors items that fail the profitability or sellability checks; skips market listings.");
            }
            catch (Exception ex)
            {
                SafeTextColored(new Vector4(1, 0, 0, 1), $"Error in DrawAutomationTab: {ex?.Message ?? "Unknown"}");
                LogError("[AutoMarket] DrawAutomationTab error", ex);
            }
        }
        
        private void DrawClearTab()
        {
            try
            {
                ImGui.TextWrapped("Pulls listed market items back from each retainer. By default, items are returned to your inventory. Retainers that don't exist are skipped. If the destination is full, clearing stops early with a chat message.");
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                var config = Plugin.Configuration;

                // Return destination option
                bool returnToRetainer = config.ClearReturnToRetainerInventory;
                if (ImGui.Checkbox("Return to Retainer Inventory##clearMode", ref returnToRetainer))
                {
                    config.ClearReturnToRetainerInventory = returnToRetainer;
                    config.Save();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
                    returnToRetainer
                        ? "(items go to retainer's bag)"
                        : "(default: items go to your inventory)");

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                ImGui.TextUnformatted("Exclude retainers from clearing (checked = skip):");
                ImGui.Spacing();

                if (ImGui.BeginTable("ClearRetainerTable", 2, ImGuiTableFlags.None))
                {
                    ImGui.TableSetupColumn("Col1", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Col2", ImGuiTableColumnFlags.WidthStretch);

                    for (int row = 0; row < 5; row++)
                    {
                        ImGui.TableNextRow();

                        // Left column: retainer (row+1), 0-based index = row
                        ImGui.TableSetColumnIndex(0);
                        int leftIndex = row;
                        bool leftExcluded = config.ClearExcludedRetainers.Contains(leftIndex);
                        if (ImGui.Checkbox($"Retainer {leftIndex + 1}##clearExclude{leftIndex}", ref leftExcluded))
                        {
                            if (leftExcluded) config.ClearExcludedRetainers.Add(leftIndex);
                            else config.ClearExcludedRetainers.Remove(leftIndex);
                            config.Save();
                        }

                        // Right column: retainer (row+6), 0-based index = row+5
                        ImGui.TableSetColumnIndex(1);
                        int rightIndex = row + 5;
                        bool rightExcluded = config.ClearExcludedRetainers.Contains(rightIndex);
                        if (ImGui.Checkbox($"Retainer {rightIndex + 1}##clearExclude{rightIndex}", ref rightExcluded))
                        {
                            if (rightExcluded) config.ClearExcludedRetainers.Add(rightIndex);
                            else config.ClearExcludedRetainers.Remove(rightIndex);
                            config.Save();
                        }
                    }

                    ImGui.EndTable();
                }
            }
            catch (Exception ex)
            {
                SafeTextColored(new Vector4(1, 0, 0, 1), $"Error in DrawClearTab: {ex?.Message ?? "Unknown"}");
                LogError("[AutoMarket] DrawClearTab error", ex);
            }
        }

        private void DrawSettingsTab()
        {
            try
            {
                // Add spacing before scrollable settings panel
                ImGui.Spacing();
                if (ImGui.BeginChild("SettingsPanel"))
                {
                    ImGui.Spacing(); // Spacing inside the child
                    ImGui.Text("Market Board Settings");
                    ImGui.Separator();
                    
                    var undercutAmount = Plugin.Configuration.UndercutAmount;
                    if (ImGui.DragInt("Undercut Amount", ref undercutAmount, 1, 0, 10000))
                        Plugin.Configuration.UndercutAmount = undercutAmount;
                        
                    var minProfitThreshold = Plugin.Configuration.MinProfitThreshold;
                    if (ImGui.DragInt("Min Profit Threshold", ref minProfitThreshold, 1, 0, 100000))
                        Plugin.Configuration.MinProfitThreshold = minProfitThreshold;
                        
                    var autoUndercut = Plugin.Configuration.AutoUndercut;
                    if (ImGui.Checkbox("Auto-undercut lowest price", ref autoUndercut))
                        Plugin.Configuration.AutoUndercut = autoUndercut;
                    
                    var dataCenterScan = Plugin.Configuration.DataCenterScan;
                    if (ImGui.Checkbox("Data Center Scan", ref dataCenterScan))
                        Plugin.Configuration.DataCenterScan = dataCenterScan;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("When enabled, scans the entire data center for lowest prices instead of just your world");
                    
                    var manageListedItems = Plugin.Configuration.ManageListedItems;
                    if (ImGui.Checkbox("Manage Listed Items", ref manageListedItems))
                        Plugin.Configuration.ManageListedItems = manageListedItems;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("When enabled, automatically adjusts prices of currently listed items by undercutting the cheapest market price before processing inventory");
                    
                    ImGui.Separator();
                    ImGui.Text("Automation Settings");
                    ImGui.Separator();
                    
                    var actionDelay = Plugin.Configuration.ActionDelay;
                    if (ImGui.DragInt("Action Delay (ms)", ref actionDelay, 10, 100, 5000))
                        Plugin.Configuration.ActionDelay = actionDelay;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Delay between automation actions");
                        
                    var retainerDelay = Plugin.Configuration.RetainerDelay;
                    if (ImGui.DragInt("Retainer Delay (ms)", ref retainerDelay, 10, 500, 10000))
                        Plugin.Configuration.RetainerDelay = retainerDelay;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Delay between switching retainers");
                    
                    ImGui.Separator();
                    ImGui.Text("Filter Settings");
                    ImGui.Separator();
                    
                    var skipHQItems = Plugin.Configuration.SkipHQItems;
                    if (ImGui.Checkbox("Skip HQ Items", ref skipHQItems))
                        Plugin.Configuration.SkipHQItems = skipHQItems;
                        
                    var skipCollectables = Plugin.Configuration.SkipCollectables;
                    if (ImGui.Checkbox("Skip Collectables", ref skipCollectables))
                        Plugin.Configuration.SkipCollectables = skipCollectables;
                        
                    var skipGear = Plugin.Configuration.SkipGear;
                    if (ImGui.Checkbox("Skip Gear", ref skipGear))
                        Plugin.Configuration.SkipGear = skipGear;
                    
                    ImGui.Separator();
                    ImGui.Text("Debug Settings");
                    ImGui.Separator();
                    
                    var debugLogsEnabled = Plugin.Configuration.DebugLogsEnabled;
                    if (ImGui.Checkbox("Enable Debug Logs", ref debugLogsEnabled))
                        Plugin.Configuration.DebugLogsEnabled = debugLogsEnabled;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("When disabled, all debug logging will be suppressed");
                    
                    ImGui.Separator();
                    
                    if (ImGui.Button("Save Settings", new Vector2(120, 30)))
                    {
                        Plugin.Configuration.Save();
                        Log("[AutoMarket] Settings saved!");
                    }
                    
                    ImGui.SameLine();
                    if (ImGui.Button("Reset to Defaults", new Vector2(140, 30)))
                    {
                        Plugin.Configuration.ResetToDefaults();
                        Plugin.Configuration.Save();
                    }
                    
                    ImGui.EndChild();
                    // Add spacing after scrollable settings panel
                    ImGui.Spacing();
                }
            }
            catch (Exception ex)
            {
                SafeTextColored(new Vector4(1, 0, 0, 1), $"Error in DrawSettingsTab: {ex?.Message ?? "Unknown"}");
                LogError("[AutoMarket] DrawSettingsTab error", ex);
            }
        }
        
        // Helper class for Ignore tab items
        private class IgnoreItemInfo
        {
            public uint ItemId { get; set; }
            public string ItemName { get; set; } = "";
            public bool IsHQ { get; set; }
            public int Quantity { get; set; }
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

        private void ScanInventoryForIgnoreTab()
        {
            if (IgnoreTabScanning) return;
            if ((DateTime.Now - LastIgnoreTabScan).TotalSeconds < IgnoreTabScanIntervalSeconds) return;
            
            // Check if we're logged in (use fallback to Svc.ClientState if Plugin.ClientState is null)
            // ClientState can be null during transitions, so we check both
            bool isLoggedIn = false;
            if (Plugin?.ClientState != null)
            {
                isLoggedIn = Plugin.ClientState.IsLoggedIn;
            }
            else if (ECommons.DalamudServices.Svc.ClientState != null)
            {
                isLoggedIn = ECommons.DalamudServices.Svc.ClientState.IsLoggedIn;
            }
            
            if (!isLoggedIn)
            {
                // Log warning but continue - inventory might still be accessible
                LogWarning("[AutoMarket] Player may not be logged in - attempting Ignore tab scan anyway");
            }
            
            IgnoreTabScanning = true;
            try
            {
                unsafe
                {
                    var inventoryManager = GetInventoryManagerSafe();
                    if (inventoryManager == null) return;
                    
                    if (Plugin.DataManager == null) return;
                    
                    var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                    if (itemSheet == null) return;
                    
                    var itemsDict = new Dictionary<uint, IgnoreItemInfo>();
                    
                    // Scan all 4 inventory bags
                    var bagTypes = new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
                    
                    foreach (var bagType in bagTypes)
                    {
                        var container = inventoryManager->GetInventoryContainer(bagType);
                        if (container == null) continue;
                        
                        for (int i = 0; i < container->Size; i++)
                        {
                            var slot = container->GetInventorySlot(i);
                            if (slot == null || slot->ItemId == 0) continue;
                            
                            var itemRow = itemSheet.GetRow(slot->ItemId);
                            if (itemRow.RowId == 0) continue;
                            
                            // Only include tradeable items
                            if (itemRow.IsUntradable || itemRow.ItemSearchCategory.RowId == 0) continue;
                            
                            // Note: We show all items in the ignore tab, even if they're already ignored
                            // This allows users to uncheck items to remove them from ignore list
                            
                            var itemId = slot->ItemId;
                            var isHQ = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                            
                            // Use HQ flag as part of key if needed, or just use base item ID
                            if (!itemsDict.ContainsKey(itemId))
                            {
                                itemsDict[itemId] = new IgnoreItemInfo
                                {
                                    ItemId = itemId,
                                    ItemName = itemRow.Name.ToString(),
                                    IsHQ = isHQ,
                                    Quantity = slot->Quantity
                                };
                            }
                            else
                            {
                                itemsDict[itemId].Quantity += slot->Quantity;
                            }
                        }
                    }
                    
                    IgnoreTabItems = itemsDict.Values.OrderBy(i => i.ItemName).ToList();
                    LastIgnoreTabScan = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                LogError("[AutoMarket] Error scanning inventory for Ignore tab", ex);
            }
            finally
            {
                IgnoreTabScanning = false;
            }
        }
        
        private void DrawIgnoreTab()
        {
            try
            {
                SafeText("Item Ignore List");
                ImGui.Separator();
                SafeTextWrapped("Check items below to exclude them from market scanning and automation.");
                ImGui.Spacing();
                
                // Check if we should scan
                var shouldScan = (DateTime.Now - LastIgnoreTabScan).TotalSeconds >= IgnoreTabScanIntervalSeconds;
                var isScanning = IgnoreTabScanning || Scanner.Scanning;
                
                if (isScanning)
                {
                    SafeTextColored(new Vector4(1, 1, 0, 1), "[*] Scanning inventory... Please wait.");
                    ImGui.Spacing();
                }
                
                // Trigger scan if needed (non-blocking)
                if (shouldScan && !isScanning)
                {
                    // Run on framework thread to avoid race conditions
                    _ = Task.Run(async () =>
                    {
                        await Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            try
                            {
                                ScanInventoryForIgnoreTab();
                            }
                            catch (Exception ex)
                            {
                                LogError("[AutoMarket] Error scanning inventory for Ignore tab", ex);
                            }
                        });
                    });
                }
                
                // Manual refresh button
                if (ImGui.Button("Refresh Inventory", new Vector2(150, 25)))
                {
                    if (!isScanning)
                    {
                        LastIgnoreTabScan = DateTime.MinValue;
                        // Run on framework thread to avoid race conditions
                        _ = Task.Run(async () =>
                        {
                            await Plugin.Framework.RunOnFrameworkThread(() =>
                            {
                                try
                                {
                                    ScanInventoryForIgnoreTab();
                                }
                                catch (Exception ex)
                                {
                                    LogError("[AutoMarket] Error scanning inventory for Ignore tab", ex);
                                }
                            });
                        });
                    }
                }
                
                ImGui.SameLine();
                
                // Toggle button: "Ignore All" or "Include All"
                string toggleButtonText = IgnoreAllMode ? "Include All" : "Ignore All";
                if (ImGui.Button(toggleButtonText, new Vector2(150, 25)))
                {
                    if (!isScanning && IgnoreTabItems.Count > 0)
                    {
                        if (IgnoreAllMode)
                        {
                            // Include All: Remove all items from ignore list
                            foreach (var item in IgnoreTabItems)
                            {
                                Plugin.Configuration.IgnoredItemIds.Remove(item.ItemId);
                            }
                            IgnoreAllMode = false; // Switch back to "Ignore All" mode
                        }
                        else
                        {
                            // Ignore All: Add all items to ignore list
                            foreach (var item in IgnoreTabItems)
                            {
                                Plugin.Configuration.IgnoredItemIds.Add(item.ItemId);
                            }
                            IgnoreAllMode = true; // Switch to "Include All" mode
                        }
                        Plugin.Configuration.Save();
                    }
                }
                
                ImGui.Spacing();
                ImGui.Separator();
                
                // Show item count
                SafeText($"Items in inventory: {IgnoreTabItems.Count}");
                ImGui.Spacing();
                
                // Table with checkboxes
                if (IgnoreTabItems.Count == 0)
                {
                    if (isScanning)
                    {
                        SafeTextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "Scanning inventory...");
                    }
                    else
                    {
                        SafeTextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No items found. Click 'Refresh Inventory' to scan.");
                    }
                    return;
                }
                
                if (ImGui.BeginTable("IgnoreItemsTable", 4,
                    ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Ignore", ImGuiTableColumnFlags.WidthFixed, 60);
                    ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 40);
                    ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableHeadersRow();
                    
                    foreach (var item in IgnoreTabItems)
                    {
                        ImGui.TableNextRow();
                        
                        ImGui.TableSetColumnIndex(0);
                        bool isIgnored = Plugin.Configuration.IgnoredItemIds.Contains(item.ItemId);
                        if (ImGui.Checkbox($"##{item.ItemId}", ref isIgnored))
                        {
                            if (isIgnored)
                            {
                                Plugin.Configuration.IgnoredItemIds.Add(item.ItemId);
                            }
                            else
                            {
                                Plugin.Configuration.IgnoredItemIds.Remove(item.ItemId);
                            }
                            Plugin.Configuration.Save();
                        }
                        
                        ImGui.TableSetColumnIndex(1);
                        SafeText(item.ItemName);
                        
                        ImGui.TableSetColumnIndex(2);
                        if (item.IsHQ)
                            ImGui.TextColored(new Vector4(1, 0.8f, 0.4f, 1), "[HQ]");
                        
                        ImGui.TableSetColumnIndex(3);
                        SafeText($"{item.Quantity}");
                    }
                    
                    ImGui.EndTable();
                }
            }
            catch (Exception ex)
            {
                SafeTextColored(new Vector4(1, 0, 0, 1), $"Error in DrawIgnoreTab: {ex?.Message ?? "Unknown"}");
                LogError("[AutoMarket] DrawIgnoreTab error", ex);
            }
        }
        
        public void Dispose()
        {
            Automation.StatusUpdate -= (status) => AutomationStatus = status;
        }
    }
}
