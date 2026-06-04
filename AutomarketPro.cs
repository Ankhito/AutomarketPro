using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.Automation;
using ECommons;
using System;
using System.Linq;
using System.Threading.Tasks;
using AutomarketPro.Core;
using AutomarketPro.Services;
using AutomarketPro.UI;

namespace AutomarketPro
{
    public sealed class AutomarketProPlugin : IDalamudPlugin
    {
        public string Name => "AutoMarket Pro";
        
        private const string CommandName = "/automarket";
        
        [PluginService] public IDalamudPluginInterface PluginInterface
        {
            get => _pluginInterface!;
            private set
            {
                _pluginInterface = value;
                ECommonsMain.Init(value, this);
                TryRegisterCallbacks();
            }
        }
        private IDalamudPluginInterface? _pluginInterface;
        
        private void InitializeOnFirstFrame(IFramework framework)
        {
            Initialize();
            if (_initialized)
            {
                Framework.Update -= InitializeOnFirstFrame;
            }
        }
        
        private void Log(string message)
        {
            if (!Configuration?.DebugLogsEnabled ?? false)
                return;
                
            try
            {
                if (PluginLog != null)
                {
                    PluginLog.Info(message);
                }
            }
            catch (Exception)
            {
            }
        }
        
        private void LogError(string message, Exception? ex = null)
        {
            if (!Configuration?.DebugLogsEnabled ?? false)
                return;
                
            try
            {
                if (PluginLog != null)
                {
                    if (ex != null)
                        PluginLog.Error(ex, message);
                    else
                        PluginLog.Error(message);
                }
            }
            catch { }
        }
        
        private void LogWarning(string message)
        {
            if (!Configuration?.DebugLogsEnabled ?? false)
                return;
                
            try
            {
                if (PluginLog != null)
                {
                    PluginLog.Warning(message);
                }
            }
            catch { }
        }
        
        private void TryInitializeImmediately()
        {
            if (!_initialized && _pluginInterface != null && _commandManager != null && _framework != null)
            {
                Initialize();
            }
        }
        [PluginService] public ICommandManager CommandManager
        {
            get => _commandManager!;
            private set
            {
                _commandManager = value;
                if (_commandManager != null && _pluginInterface != null && _framework != null && !_initialized)
                {
                    TryInitializeImmediately();
                }
            }
        }
        private ICommandManager? _commandManager;
        [PluginService] public IChatGui ChatGui 
        { 
            get => _chatGui!;
            private set 
            {
                _chatGui = value;
            }
        }
        private IChatGui? _chatGui;
        [PluginService] public IClientState ClientState { get; private set; } = null!;
        
        /// <summary>
        /// Helper method to print messages to chat. Can be called from any thread.
        /// ChatGui.Print is thread-safe according to Dalamud documentation.
        /// </summary>
        public void PrintChat(string message)
        {
            try
            {
                // Validate input
                if (string.IsNullOrEmpty(message))
                {
                    return;
                }
                
                // Use backing field directly to avoid potential null reference issues
                var chatGui = _chatGui;
                if (chatGui == null)
                {
                    return;
                }
                
                // Sanitize message to prevent issues with special characters or very long messages
                var safeMessage = message.Length > 500 ? message.Substring(0, 500) + "..." : message;
                
                // Call directly like PennyPincher does - ChatGui.Print is thread-safe
                chatGui.Print(safeMessage);
            }
            catch (Exception ex)
            {
                // Use try-catch to prevent logging from causing another crash
                try
                {
                    var errorMsg = ex?.Message ?? "Unknown error";
                    PluginLog?.Warning($"Failed to print chat message: {errorMsg}");
                }
                catch
                {
                    // Silently fail if even logging fails
                }
            }
        }
        [PluginService] public IDataManager? DataManager 
        { 
            get => _dataManager;
            private set 
            {
                _dataManager = value;
                if (_dataManager != null)
                {
                    try
                    {
                        PluginLog?.Info("[AutoMarket] DataManager service injected successfully");
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        PluginLog?.Warning("[AutoMarket] DataManager service is null - may not be injected yet");
                    }
                    catch { }
                }
            }
        }
        private IDataManager? _dataManager;
        [PluginService] public IFramework Framework
        {
            get => _framework!;
            private set
            {
                _framework = value;
                if (_framework != null && _pluginInterface != null && !_initialized)
                {
                    _framework.Update += InitializeOnFirstFrame;
                    TryInitializeImmediately();
                }
            }
        }
        private IFramework? _framework;
        [PluginService] public IGameGui GameGui { get; private set; } = null!;
        [PluginService] public IPluginLog PluginLog { get; private set; } = null!;
        
        public Configuration Configuration { get; private set; } = null!;
        private WindowSystem WindowSystem = new("AutoMarketPro");
        public MainWindow MainWindow { get; private set; } = null!;
        private MarketScanner Scanner { get; set; } = null!;
        private RetainerAutomation Automation { get; set; } = null!;
        private bool _initialized = false;
        private bool _callbacksRegistered = false;
        
        private Action? drawUI;
        private Action? openConfigUI;
        private Action? openMainUI;
        
        public AutomarketProPlugin()
        {
            drawUI = DrawUI;
            openConfigUI = OpenConfigUI;
            openMainUI = OpenMainUI;
            
            TryRegisterCallbacks();
        }
        
        private void TryRegisterCallbacks()
        {
            if (_pluginInterface != null && drawUI != null && openConfigUI != null && openMainUI != null && !_callbacksRegistered)
            {
                _pluginInterface.UiBuilder.Draw -= DrawUI;
                _pluginInterface.UiBuilder.OpenConfigUi -= openConfigUI;
                _pluginInterface.UiBuilder.OpenMainUi -= openMainUI;
                
                _pluginInterface.UiBuilder.Draw += DrawUI;
                _pluginInterface.UiBuilder.OpenConfigUi += openConfigUI;
                _pluginInterface.UiBuilder.OpenMainUi += openMainUI;
                _callbacksRegistered = true;
            }
        }
        
        private void Initialize()
        {
            if (_initialized)
            {
                return;
            }
            
            if (PluginInterface == null || CommandManager == null || Framework == null)
            {
                return;
            }
            
            if (DataManager == null)
            {
                return;
            }
            
            Framework.Update += Tick;
            
            try
            {
                Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
                Configuration.Initialize(PluginInterface);
                
                if (Scanner == null)
                {
                    Scanner = new MarketScanner(this);
                }
                
                if (Automation == null)
                {
                    Automation = new RetainerAutomation(this, Scanner);
                }
                
                if (MainWindow == null)
                {
                    MainWindow = new MainWindow(this, Scanner, Automation);
                }
                
                if (WindowSystem == null)
                {
                    WindowSystem = new WindowSystem("AutoMarketPro");
                }
                
                if (MainWindow == null)
                {
                    throw new Exception("MainWindow is null after creation");
                }
                
                try
                {
                    if (!WindowSystem.Windows.Any(w => w == MainWindow))
                    {
                        WindowSystem.AddWindow(MainWindow);
                    }
                }
                catch (ArgumentException)
                {
                }
                catch (Exception addWindowEx)
                {
                    LogError("[AutoMarket] WindowSystem.AddWindow() failed", addWindowEx);
                }
                
                try
                {
                    _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
                    {
                        HelpMessage = "AutoMarket Pro - /automarket [start|stop|pause|config|summary]"
                    });
                }
                catch (Exception)
                {
                }
                
                _initialized = true;
            }
            catch (Exception ex)
            {
                LogError("[AutoMarket] Initialization failed", ex);
            }
        }
        
        private unsafe void Tick(IFramework framework)
        {
            try
            {
                if (Automation != null && Automation.Running
                    && ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("Talk", out var talkAddon)
                    && talkAddon->IsVisible
                    && ECommons.GenericHelpers.IsAddonReady(talkAddon))
                {
                    var talkMaster = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Talk((nint)talkAddon);
                    talkMaster.Click();
                }
            }
            catch (System.AccessViolationException) { }
            catch { }
        }
        
        public void Dispose()
        {
            ECommonsMain.Dispose();
            
            if (Framework != null)
            {
                Framework.Update -= InitializeOnFirstFrame;
                Framework.Update -= Tick;
            }
            
            if (_callbacksRegistered && PluginInterface != null)
            {
                PluginInterface.UiBuilder.Draw -= DrawUI;
                if (openConfigUI != null)
                    PluginInterface.UiBuilder.OpenConfigUi -= openConfigUI;
                if (openMainUI != null)
                    PluginInterface.UiBuilder.OpenMainUi -= openMainUI;
            }
            
            if (_initialized)
            {
                WindowSystem.RemoveAllWindows();
                if (_commandManager != null)
                    _commandManager.RemoveHandler(CommandName);
            }
            
            Automation?.Dispose();
            Scanner?.Dispose();
            MainWindow?.Dispose();
        }
        
        private void OnCommand(string command, string args)
        {
            if (!_initialized) Initialize();
            
            if (string.IsNullOrEmpty(args))
            {
                MainWindow.IsOpen = true;
                return;
            }
            
            var parts = args.Split(' ');
            var subCommand = parts[0].ToLower();
            
            switch (subCommand)
            {
                case "start":
                    Task.Run(() => Automation.StartFullCycle());
                    break;
                case "stop":
                    Automation.StopAutomation();
                    break;
                case "pause":
                    Automation.PauseAutomation();
                    break;
                case "summary":
                    ShowSummary();
                    break;
                case "config":
                    MainWindow.IsOpen = true;
                    MainWindow.ShowSettingsTab = true;
                    break;
                default:
                    PluginLog?.Warning($"[AutoMarket] Unknown command: {subCommand}");
                    PluginLog?.Info("[AutoMarket] Available: start, stop, pause, summary, config");
                    break;
            }
        }
        
        private void ShowSummary()
        {
            var summary = Automation.GetLastRunSummary();
            PluginLog?.Info("[AutoMarket] Last Run Summary:");
            PluginLog?.Info($"  Total items processed: {summary.TotalItems}");
            PluginLog?.Info($"  Items listed on MB: {summary.ItemsListed}");
            PluginLog?.Info($"  Items vendored: {summary.ItemsVendored}");
            PluginLog?.Info($"  Estimated revenue: {summary.EstimatedRevenue:N0} gil");
        }
        
        private void DrawUI()
        {
            if (!_initialized)
            {
                Initialize();
                if (!_initialized) return;
            }
            
            if (!_initialized || WindowSystem == null || MainWindow == null) return;
            
            try
            {
                WindowSystem.Draw();
            }
            catch (Exception ex)
            {
                LogError($"[AutoMarket] Error in WindowSystem.Draw()", ex);
            }
        }
        
        private void OpenConfigUI()
        {
            Initialize();
            if (_initialized)
            {
                MainWindow.IsOpen = true;
            }
        }
        
        private void OpenMainUI()
        {
            Initialize();
            if (_initialized)
            {
                MainWindow.IsOpen = true;
            }
        }
    }
}

