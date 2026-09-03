using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq; 
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MLAstro_Robotic_Polar_Alignment.Dockables;
using MLAstro_Robotic_Polar_Alignment.Services;
using MLAstro_Robotic_Polar_Alignment.Settings;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
 
namespace MLAstro_Robotic_Polar_Alignment.Plugin
{
    /// <summary>
    /// Merged MLAstroRPA+TPPA plugin: this is no longer a NINA plugin manifest. It is the MLAstro
    /// options/state controller that backs the CONTROL / CONNECTION / CONFIGURATION tabs of the
    /// combined plugin's options page. Owned (and disposed) by
    /// NINA.Plugins.PolarAlignment.PolarizationPlugin (the single IPluginManifest of this assembly).
    /// </summary>
    public class MLAstroController : INotifyPropertyChanged, IDisposable
    {
        private static readonly int[] DefaultBaudRates = { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

        private readonly SerialConnectionService _serialConnectionService;
        private readonly PolarAlignmentDockVM _polarAlignmentDockVM;
        private ResourceDictionary? _pluginResourceDictionary;
        private FileSystemWatcher? _pluginFolderWatcher;
        private bool _disposed = false;
        private bool _isHexInputEnabled;
        private string _serialTerminalInput = string.Empty;
        private bool _isModifyMode;
        private bool _hasUserSettingsEdits;
        private string _autoReconnectStatus = string.Empty;
        private bool _isHandshakeSuccessful;
        private string? _savedSettingsSnapshot;
        private bool _showApPassword;
        private bool _showStaPassword;
        private bool _apPasswordEdited;
        private bool _staPasswordEdited;

        public PluginSettings Settings { get; }

        public PolarAlignmentDockVM PolarAlignmentVM => _polarAlignmentDockVM;

        public PolarAlignmentDataSourceMode[] AvailableDataSourceModes { get; } = Enum.GetValues<PolarAlignmentDataSourceMode>();

        public event PropertyChangedEventHandler? PropertyChanged;

        public string[] AvailableComPorts => string.IsNullOrWhiteSpace(Settings.ComPort)
            ? _serialConnectionService.AvailablePorts
            : _serialConnectionService.AvailablePorts.Contains(Settings.ComPort, StringComparer.OrdinalIgnoreCase)
                ? _serialConnectionService.AvailablePorts
                : _serialConnectionService.AvailablePorts.Concat(new[] { Settings.ComPort }).ToArray();

        /// <summary>
        /// COM ports with driver names for the dropdown (e.g. "COM4 - USB-SERIAL CH340").
        /// The currently selected port is always included even if it is momentarily
        /// not enumerated by the OS.
        /// </summary>
        public ComPortInfo[] AvailableComPortItems
        {
            get
            {
                var infos = _serialConnectionService.AvailableComPortInfos;
                if (string.IsNullOrWhiteSpace(Settings.ComPort))
                {
                    return infos;
                }

                if (infos.Any(i => string.Equals(i.PortName, Settings.ComPort, StringComparison.OrdinalIgnoreCase)))
                {
                    return infos;
                }

                return infos.Concat(new[] { new ComPortInfo(Settings.ComPort, string.Empty) }).ToArray();
            }
        }

        public int[] AvailableBaudRates => DefaultBaudRates;

        public string SerialConnectionStatus => _serialConnectionService.ConnectionStatus;

        public string SerialHandshakeStatus => _serialConnectionService.HandshakeStatus;

        public bool IsSerialConnected => _serialConnectionService.IsConnected;

        /// <summary>TPPA đang giữ quyền điều khiển -> khoá tab CONFIGURATION.</summary>
        public bool IsExternalLocked => _serialConnectionService.IsExternalControlActive;

        public bool IsExternalUnlocked => !IsExternalLocked;

        public string SerialConnectButtonText => IsSerialConnected ? "Disconnect" : "Connect";

        public bool IsHexDisplay
        {
            get => _serialConnectionService.HexDisplay;
            set
            {
                if (_serialConnectionService.HexDisplay != value)
                {
                    _serialConnectionService.HexDisplay = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<SerialTerminalEntry> SerialTerminalEntries => _serialConnectionService.TerminalEntries;

        public bool IsHexInputEnabled
        {
            get => _isHexInputEnabled;
            set
            {
                if (_isHexInputEnabled != value)
                {
                    _isHexInputEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SerialInputMaxLength));
                }
            }
        }

        public int SerialInputMaxLength => IsHexInputEnabled ? 16 : 0;

        public string SerialTerminalInput
        {
            get => _serialTerminalInput;
            set
            {
                if (_serialTerminalInput != value)
                {
                    _serialTerminalInput = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsModifyMode
        {
            get => _isModifyMode;
            set
            {
                if (_isModifyMode != value)
                {
                    var wasModifyMode = _isModifyMode;
                    _isModifyMode = value;
                    _serialConnectionService.PauseTelemetryUpdates = value;
                    OnPropertyChanged();

                    if (value)
                    {
                        // Entering modify mode - save current settings snapshot
                        _savedSettingsSnapshot = _serialConnectionService.BuildConfigurationCommand(Settings);
                        Logger.Info("[MLAstro] Entering modify mode - settings snapshot saved");
                    }
                    else if (wasModifyMode)
                    {
                        // Exiting modify mode - check if settings changed
                        var currentSettings = _serialConnectionService.BuildConfigurationCommand(Settings);
                        if (currentSettings != _savedSettingsSnapshot)
                        {
                            Logger.Info("[MLAstro] Settings changed - saving to device");
                            SaveAllSettingsInternal();
                        }
                        else
                        {
                            Logger.Info("[MLAstro] No settings changed - skipping save");
                        }
                        _savedSettingsSnapshot = null;
                    }
                }
            }
        }

        public bool IsPauseQuery
        {
            get => SerialConnectionService.PauseQueryGlobal;
            set
            {
                if (SerialConnectionService.PauseQueryGlobal != value)
                {
                    SerialConnectionService.PauseQueryGlobal = value;
                    OnPropertyChanged();
                }
            }
        }

        public int HandshakeTimeoutMilliseconds
        {
            get => _serialConnectionService.HandshakeTimeoutMilliseconds;
            set => _serialConnectionService.HandshakeTimeoutMilliseconds = value;
        }

        public int PollingIntervalMilliseconds
        {
            get => _serialConnectionService.PollingIntervalMilliseconds;
            set => _serialConnectionService.PollingIntervalMilliseconds = value;
        }

        public string AutoReconnectStatus
        {
            get => _autoReconnectStatus;
            set
            {
                if (_autoReconnectStatus != value)
                {
                    _autoReconnectStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsHandshakeSuccessful
        {
            get => _isHandshakeSuccessful;
            set
            {
                if (_isHandshakeSuccessful != value)
                {
                    _isHandshakeSuccessful = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool ShowApPassword
        {
            get => _showApPassword;
            private set
            {
                if (_showApPassword == value)
                {
                    return;
                }

                _showApPassword = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ApPassDisplay));
            }
        }

        public bool ShowStaPassword
        {
            get => _showStaPassword;
            private set
            {
                if (_showStaPassword == value)
                {
                    return;
                }

                _showStaPassword = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WifiPassDisplay));
            }
        }

        public string ApPassDisplay
        {
            get => ShowApPassword ? Settings.ApPass : "********";
            set
            {
                if (ShowApPassword && value != Settings.ApPass)
                {
                    Settings.ApPass = value;
                    _apPasswordEdited = true;
                    OnPropertyChanged();
                }
            }
        }

        public string WifiPassDisplay
        {
            get => ShowStaPassword ? Settings.WifiPass : "********";
            set
            {
                if (ShowStaPassword && value != Settings.WifiPass)
                {
                    Settings.WifiPass = value;
                    _staPasswordEdited = true;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand RefreshComPortsCommand { get; }

        public ICommand ToggleSerialConnectionCommand { get; }

        public ICommand SendSerialCommand { get; }

        public ICommand ClearSerialTerminalCommand { get; }

        public ICommand SaveAllSettingsCommand { get; }

        public ICommand ApplySettingsCommand { get; }

        public ICommand RebootCommand { get; }

        public ICommand ResetEsp32Command { get; }

        public ICommand ToggleShowApPasswordCommand { get; }

        public ICommand ToggleShowStaPasswordCommand { get; }

        public MLAstroController(PluginSettings settings, SerialConnectionService serialConnectionService, PolarAlignmentDockVM polarAlignmentDockVM)
        {
            Settings = settings;
            _serialConnectionService = serialConnectionService;
            _polarAlignmentDockVM = polarAlignmentDockVM;

            RefreshComPortsCommand = new RelayCommand(RefreshComPorts);
            ToggleSerialConnectionCommand = new RelayCommand(ToggleSerialConnection);
            SendSerialCommand = new RelayCommand(SendSerial);
            ClearSerialTerminalCommand = new RelayCommand(ClearSerialTerminal);
            SaveAllSettingsCommand = new RelayCommand(SaveAllSettings);
            ApplySettingsCommand = new RelayCommand(ApplySettings);
            RebootCommand = new RelayCommand(ResetEsp32);
            ResetEsp32Command = new RelayCommand(ResetEsp32);
            ToggleShowApPasswordCommand = new RelayCommand(ToggleShowApPassword);
            ToggleShowStaPasswordCommand = new RelayCommand(ToggleShowStaPassword);

            Settings.PropertyChanged += OnSettingsPropertyChanged;
            _serialConnectionService.PropertyChanged += OnSerialConnectionServicePropertyChanged;
            _serialConnectionService.AddExternalControlListener(_ =>
            {
                OnPropertyChanged(nameof(IsExternalLocked));
                OnPropertyChanged(nameof(IsExternalUnlocked));
            });
            RefreshComPorts();

            // Hook into application exit to ensure cleanup - must run on UI thread
            if (Application.Current != null)
            {
                try
                {
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Exit += OnApplicationExit);
                }
                catch { }
            }

            // Setup FileSystemWatcher to detect when plugin is being uninstalled
            // NINA moves plugin folder to DeletionFolder when user clicks Uninstall
            SetupPluginFolderWatcher();

            try
            {
                if (Application.Current != null)
                {
                    try
                    {
                        // Create and add ResourceDictionary on UI thread because ResourceDictionary/DependencyObject
                        // must be owned by the UI thread's Dispatcher.
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _pluginResourceDictionary = new ResourceDictionary
                            {
                                Source = new Uri("pack://application:,,,/NINA.Plugins.PolarAlignment;component/MLAstro/Dockview/Dockable.xaml", UriKind.Absolute)
                            };
                            Application.Current.Resources.MergedDictionaries.Add(_pluginResourceDictionary);
                        });
                    }
                    catch { }
                }

                var iconLocatorType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => {
                        try { return a.GetTypes(); } catch { return Type.EmptyTypes; }
                    })
                    .FirstOrDefault(t => t.Name == "IconLocator");

                if (iconLocatorType != null)
                {
                    var registerMethod = iconLocatorType.GetMethod("Register", new[] { typeof(Uri) });
                    if (registerMethod != null)
                    {
                        var uri = new Uri("pack://application:,,,/NINA.Plugins.PolarAlignment;component/MLAstro/Resources/MLAstroIcons.xaml", UriKind.Absolute);
                        try
                        {
                            // Ensure any UI-related registration runs on the UI thread
                            Application.Current?.Dispatcher?.Invoke(() => registerMethod.Invoke(null, new object[] { uri }));
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            Logger.Info("[MLAstro] Application exiting, disposing plugin resources...");
            Dispose();
        }

        /// <summary>
        /// Setup a FileSystemWatcher to detect when our plugin folder is being moved/deleted.
        /// NINA moves plugin folder to DeletionFolder when user clicks Uninstall.
        /// This allows us to close the dockable before the uninstall completes.
        /// </summary>
        private void SetupPluginFolderWatcher()
        {
            try
            {
                // Get the plugin assembly's directory
                // Plugin is at: %LOCALAPPDATA%\NINA\Plugins\3.0.0\MLAstro_Robotic_Polar_Alignment
                var assemblyLocation = GetType().Assembly.Location;
                var pluginFolder = Path.GetDirectoryName(assemblyLocation);

                Logger.Info($"[MLAstro] Plugin assembly location: {assemblyLocation}");
                Logger.Info($"[MLAstro] Plugin folder: {pluginFolder}");

                // Only watch OUR plugin folder for file deletions
                // This ensures we only trigger when MLAstro RPA plugin is being uninstalled
                // NOT when other plugins are installed/uninstalled
                if (!string.IsNullOrEmpty(pluginFolder) && Directory.Exists(pluginFolder))
                {
                    // Use a flag to only trigger notification once
                    bool pluginFolderNotificationShown = false;

                    _pluginFolderWatcher = new FileSystemWatcher(pluginFolder)
                    {
                        NotifyFilter = NotifyFilters.FileName,
                        Filter = "*.dll",
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true
                    };

                    _pluginFolderWatcher.Deleted += (s, e) =>
                    {
                        Logger.Info($"[MLAstro] File deleted/moved from plugin folder: {e.Name}");
                        // Only trigger on OUR plugin's DLL deletion
                        if (e.Name?.Contains("MLAstro", StringComparison.OrdinalIgnoreCase) == true ||
                            e.Name?.Equals("System.IO.Ports.dll", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            if (!pluginFolderNotificationShown)
                            {
                                pluginFolderNotificationShown = true;
                                Logger.Info($"[MLAstro] MLAstro plugin DLL moved: {e.Name} - triggering uninstall cleanup");
                                OnPluginUninstalling();
                            }
                        }
                    };

                    Logger.Info("[MLAstro] Plugin folder watcher setup complete");
                }
                else
                {
                    Logger.Warning("[MLAstro] Plugin folder not found - cannot setup watcher");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Failed to setup plugin folder watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the plugin is being uninstalled (folder moved to DeletionFolder).
        /// Shows a notification to user that NINA restart is required.
        /// </summary>
        private void OnPluginUninstalling()
        {
            try
            {
                Logger.Info("[MLAstro] Plugin uninstalling detected");

                // Show notification to user - specific to MLAstro RPA plugin
                Notification.ShowWarning(
                    "MLAstro RPA: Plugin is being uninstalled. Please RESTART NINA to complete the removal and close the control panel.",
                    TimeSpan.FromMinutes(5));

                // Must run on UI thread
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    try
                    {
                        var dockableVM = _polarAlignmentDockVM;
                        if (dockableVM != null)
                        {
                            Logger.Info("[MLAstro] Closing dockable panel...");
                            dockableVM.IsVisible = false;
                            dockableVM.IsClosed = true;
                            dockableVM.Dispose();
                            Logger.Info("[MLAstro] Dockable panel closed successfully");
                        }

                        // Disconnect serial
                        if (_serialConnectionService?.IsConnected == true)
                        {
                            _serialConnectionService.Disconnect();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[MLAstro] Error closing dockable on uninstall: {ex.Message}");
                    }
                });

                // Try to delete the plugin folder after a short delay (allow NINA to finish moving files)
                Task.Run(async () =>
                {
                    try
                    {
                        // Wait for NINA to finish moving files
                        await Task.Delay(2000);

                        var assemblyLocation = GetType().Assembly.Location;
                        var pluginFolder = Path.GetDirectoryName(assemblyLocation);

                        if (!string.IsNullOrEmpty(pluginFolder) && Directory.Exists(pluginFolder))
                        {
                            Logger.Info($"[MLAstro] Attempting to delete plugin folder: {pluginFolder}");

                            // Try to delete remaining files first
                            foreach (var file in Directory.GetFiles(pluginFolder, "*.*", SearchOption.AllDirectories))
                            {
                                try
                                {
                                    File.Delete(file);
                                    Logger.Info($"[MLAstro] Deleted file: {file}");
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warning($"[MLAstro] Could not delete file {file}: {ex.Message}");
                                }
                            }

                            // Try to delete empty subdirectories
                            foreach (var dir in Directory.GetDirectories(pluginFolder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                            {
                                try
                                {
                                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                                    {
                                        Directory.Delete(dir);
                                        Logger.Info($"[MLAstro] Deleted empty directory: {dir}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warning($"[MLAstro] Could not delete directory {dir}: {ex.Message}");
                                }
                            }

                            // Try to delete the plugin folder itself
                            try
                            {
                                if (!Directory.EnumerateFileSystemEntries(pluginFolder).Any())
                                {
                                    Directory.Delete(pluginFolder);
                                    Logger.Info($"[MLAstro] Plugin folder deleted successfully");
                                }
                                else
                                {
                                    Logger.Info($"[MLAstro] Plugin folder not empty, will be cleaned up on NINA restart");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"[MLAstro] Could not delete plugin folder: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[MLAstro] Error during plugin folder cleanup: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Error in OnPluginUninstalling: {ex.Message}");
            }
        }

        private void RefreshComPorts()
        {
            _serialConnectionService.RefreshPorts();
            OnPropertyChanged(nameof(AvailableComPorts));
            OnPropertyChanged(nameof(AvailableComPortItems));
        }

        private async void ToggleSerialConnection()
        {
            if (IsSerialConnected)
            {
                _serialConnectionService.Disconnect();
                return;
            }

            RefreshComPorts();
            await _serialConnectionService.ConnectAsync(Settings.ComPort, Settings.BaudRate);
        }

        private void SendSerial()
        {
            var input = SerialTerminalInput;
            var sent = IsHexInputEnabled
                ? _serialConnectionService.SendHex(input)
                : _serialConnectionService.Send(input.EndsWith('\n') ? input : input + "\n");

            if (sent)
            {
                SerialTerminalInput = string.Empty;
            }
        }

        private void ToggleShowApPassword()
        {
            ShowApPassword = !ShowApPassword;

            if (ShowApPassword)
            {
                _serialConnectionService.QueryApPassword();
            }
        }

        private void ToggleShowStaPassword()
        {
            ShowStaPassword = !ShowStaPassword;

            if (ShowStaPassword)
            {
                _serialConnectionService.QueryStaPassword();
            }
        }

        private void ClearSerialTerminal()
        {
            _serialConnectionService.ClearTerminal();
        }

        private async void ResetEsp32()
        {
            if (!_serialConnectionService.ResetEsp32())
            {
                return;
            }

            await AutoReconnectAsync(3);
        }

        private async void SaveAllSettingsInternal()
        {
            if (!IsSerialConnected)
            {
                return;
            }

            try
            {
                if (_apPasswordEdited && !await _serialConnectionService.SendCommandAndAwaitOkAsync($"APpa:{Settings.ApPass}\n"))
                {
                    Logger.Warning("[MLAstro] AP password update was not acknowledged");
                    return;
                }
                _apPasswordEdited = false;

                if (_staPasswordEdited && !await _serialConnectionService.SendCommandAndAwaitOkAsync($"STAp:{Settings.WifiPass}\n"))
                {
                    Logger.Warning("[MLAstro] Station password update was not acknowledged");
                    return;
                }
                _staPasswordEdited = false;

                // Password updates are sent separately above; send the remaining configuration last.
                var configCommand = _serialConnectionService.BuildConfigurationCommand(Settings);

                // Send to device
                var sent = _serialConnectionService.Send(configCommand);
                if (!sent)
                {
                    Logger.Warning("[MLAstro] Failed to send configuration command");
                    return;
                }

                // Wait for device to process and reboot
                await System.Threading.Tasks.Task.Delay(1000);

                // Disconnect
                _serialConnectionService.Disconnect();

                // Start countdown and auto-reconnect
                await AutoReconnectAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro] Save settings failed: {ex.Message}");
            }
        }

        private void ApplySettings()
        {
            if (!IsSerialConnected)
            {
                Logger.Warning("[MLAstro] Apply settings skipped - not connected");
                return;
            }

            var configCommand = _serialConnectionService.BuildConfigurationCommand(Settings, includeSaveAndReboot: false);
            var sent = _serialConnectionService.Send(configCommand);
            if (sent)
            {
                Logger.Info("[MLAstro] Settings applied to device memory");
                ResumeSettingsSync();
            }
        }

        private void ResumeSettingsSync()
        {
            if (_hasUserSettingsEdits)
            {
                _hasUserSettingsEdits = false;
                _serialConnectionService.SuspendSettingsSync = false;
                Logger.Info("[MLAstro] Telemetry settings sync resumed after apply/save");
            }
        }

        private void SaveAllSettings()
        {
            SaveAllSettingsInternal();
        }

        private async System.Threading.Tasks.Task AutoReconnectAsync(int countdownSeconds = 5)
        {
            try
            {
                // Countdown before first reconnect attempt (firmware needs time to reboot)
                for (int i = countdownSeconds; i > 0; i--)
                {
                    AutoReconnectStatus = $"Reconnecting in {i}s...";
                    await System.Threading.Tasks.Task.Delay(1000);
                }

                var connected = false;
                var handshakeSuccess = false;

                // Firmware reboot takes ~5s and the COM port may re-enumerate, so retry
                // connect + handshake several times instead of failing after a single attempt.
                const int maxConnectAttempts = 10;
                const int maxHandshakeAttempts = 5;

                for (int attempt = 0; attempt < maxConnectAttempts; attempt++)
                {
                    if (attempt > 0)
                    {
                        AutoReconnectStatus = $"Waiting for device... (retry {attempt + 1}/{maxConnectAttempts})";
                        await System.Threading.Tasks.Task.Delay(1000);
                    }

                    RefreshComPorts();

                    AutoReconnectStatus = $"Connecting... (attempt {attempt + 1}/{maxConnectAttempts})";
                    connected = await _serialConnectionService.ConnectAsync(Settings.ComPort, Settings.BaudRate);
                    if (!connected)
                    {
                        continue;
                    }

                    // Retry handshake until the firmware is ready to answer
                    handshakeSuccess = false;
                    for (int h = 0; h < maxHandshakeAttempts; h++)
                    {
                        if (_serialConnectionService.HandshakeStatus == "OK!")
                        {
                            handshakeSuccess = true;
                            break;
                        }

                        var ok = await _serialConnectionService.SendHandshakeAsync();
                        if (ok)
                        {
                            handshakeSuccess = true;
                            break;
                        }

                        await System.Threading.Tasks.Task.Delay(500);
                    }

                    if (handshakeSuccess)
                    {
                        break;
                    }

                    // Firmware not ready on this port yet - disconnect and retry
                    _serialConnectionService.Disconnect();
                }

                if (connected && handshakeSuccess)
                {
                    AutoReconnectStatus = "Connected";
                    await System.Threading.Tasks.Task.Delay(2000);
                    AutoReconnectStatus = string.Empty;
                }
                else
                {
                    AutoReconnectStatus = string.Empty;
                    ShowConnectionError();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro] Auto reconnect failed: {ex.Message}");
                AutoReconnectStatus = "Error";
                ShowConnectionError();
            }
        }

        private void ShowConnectionError()
        {
            try
            {
                if (Application.Current?.Dispatcher != null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show(
                            "CANNOT CONNECTED TO MLAstroRPA HARDWARE",
                            "Connection Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);

                        AutoReconnectStatus = string.Empty;
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro] Failed to show error dialog: {ex.Message}");
            }
        }

        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Detect user edits (not telemetry-driven) while connected and pause telemetry settings
            // sync so polling does not overwrite values the user is typing before Apply.
            // HandshakeTimeout/PollingInterval are NOT synced from telemetry, so they don't count as dirty.
            if (_serialConnectionService.IsConnected
                && !_serialConnectionService.IsApplyingTelemetrySettings
                && !_hasUserSettingsEdits
                && e.PropertyName != nameof(PluginSettings.HandshakeTimeoutMilliseconds)
                && e.PropertyName != nameof(PluginSettings.PollingIntervalMilliseconds))
            {
                _hasUserSettingsEdits = true;
                _serialConnectionService.SuspendSettingsSync = true;
                Logger.Info("[MLAstro] User settings edit detected - telemetry settings sync suspended");
            }

            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PluginSettings.ComPort))
            {
                OnPropertyChanged(nameof(AvailableComPorts));
                OnPropertyChanged(nameof(AvailableComPortItems));
            }

            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PluginSettings.ApPass))
            {
                OnPropertyChanged(nameof(ApPassDisplay));
            }

            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PluginSettings.WifiPass))
            {
                OnPropertyChanged(nameof(WifiPassDisplay));
            }
        }

        private void OnSerialConnectionServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.AvailablePorts))
            {
                OnPropertyChanged(nameof(AvailableComPorts));
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.AvailableComPortInfos))
            {
                OnPropertyChanged(nameof(AvailableComPortItems));
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.ConnectionStatus))
            {
                OnPropertyChanged(nameof(SerialConnectionStatus));
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.HandshakeStatus))
            {
                OnPropertyChanged(nameof(SerialHandshakeStatus));

                // Update IsHandshakeSuccessful based on HandshakeStatus
                var isSuccess = _serialConnectionService.HandshakeStatus == "OK!";
                IsHandshakeSuccessful = isSuccess;

                // Reset IsModifyMode when handshake fails
                if (!isSuccess && IsModifyMode)
                {
                    Logger.Info("[MLAstro] Handshake failed - resetting modify mode");
                    _isModifyMode = false; // Direct set to avoid triggering save
                    _serialConnectionService.PauseTelemetryUpdates = false;
                    _savedSettingsSnapshot = null;
                    OnPropertyChanged(nameof(IsModifyMode));
                }

                // Reset user-edit suspension when handshake fails
                if (!isSuccess)
                {
                    _hasUserSettingsEdits = false;
                    _serialConnectionService.SuspendSettingsSync = false;
                }
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.IsConnected))
            {
                OnPropertyChanged(nameof(IsSerialConnected));
                OnPropertyChanged(nameof(SerialConnectButtonText));

                // Reset states when disconnected
                if (!_serialConnectionService.IsConnected)
                {
                    IsHandshakeSuccessful = false;
                    ShowApPassword = false;
                    ShowStaPassword = false;

                    // Reset IsModifyMode when disconnected
                    if (IsModifyMode)
                    {
                        Logger.Info("[MLAstro] Disconnected - resetting modify mode");
                        _isModifyMode = false; // Direct set to avoid triggering save
                        _serialConnectionService.PauseTelemetryUpdates = false;
                        _savedSettingsSnapshot = null;
                        OnPropertyChanged(nameof(IsModifyMode));
                    }

                    // Reset user-edit suspension when disconnected
                    _hasUserSettingsEdits = false;
                    _serialConnectionService.SuspendSettingsSync = false;
                }
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.HandshakeTimeoutMilliseconds))
            {
                OnPropertyChanged(nameof(HandshakeTimeoutMilliseconds));
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.PollingIntervalMilliseconds))
            {
                OnPropertyChanged(nameof(PollingIntervalMilliseconds));
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.HexDisplay))
            {
                OnPropertyChanged(nameof(IsHexDisplay));
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null!)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                Logger.Info("[MLAstro] MLAstroController disposing...");

                // Dispose the polar alignment view model first
                try
                {
                    var dockableVM = _polarAlignmentDockVM;
                    Logger.Info($"[MLAstro] PolarAlignmentDockVM is {(dockableVM != null ? "not null (hash: " + dockableVM.GetHashCode() + ")" : "NULL")}");

                    if (dockableVM != null)
                    {
                        Logger.Info("[MLAstro] Disposing polar alignment VM - setting IsVisible=false, IsClosed=true");
                        // Hide the view first
                        dockableVM.IsVisible = false;
                        // Then mark it as closed
                        dockableVM.IsClosed = true;
                        // Dispose resources
                        dockableVM.Dispose();
                        Logger.Info("[MLAstro] Polar alignment VM hidden, closed and disposed");
                    }
                    else
                    {
                        Logger.Warning("[MLAstro] PolarAlignmentDockVM is null - cannot dispose polar alignment VM");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[MLAstro] Failed to dispose polar alignment VM: {ex.Message}");
                }

                // Remove ResourceDictionary from Application to release assembly reference
                try
                {
                    if (Application.Current != null && _pluginResourceDictionary != null)
                    {
                        Application.Current.Resources.MergedDictionaries.Remove(_pluginResourceDictionary);
                        _pluginResourceDictionary = null;
                        Logger.Info("[MLAstro] ResourceDictionary removed from Application");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[MLAstro] Failed to remove ResourceDictionary: {ex.Message}");
                }

                // Unsubscribe from application exit event
                if (Application.Current != null)
                {
                    Application.Current.Exit -= OnApplicationExit;
                }

                // Dispose FileSystemWatcher
                if (_pluginFolderWatcher != null)
                {
                    _pluginFolderWatcher.EnableRaisingEvents = false;
                    _pluginFolderWatcher.Dispose();
                    _pluginFolderWatcher = null;
                    Logger.Info("[MLAstro] Plugin folder watcher disposed");
                }

                // Unsubscribe from settings events
                if (Settings != null)
                {
                    Settings.PropertyChanged -= OnSettingsPropertyChanged;
                }

                // Unsubscribe from serial connection service events
                if (_serialConnectionService != null)
                {
                    _serialConnectionService.PropertyChanged -= OnSerialConnectionServicePropertyChanged;

                    // Disconnect and dispose serial service
                    _serialConnectionService.Disconnect();
                    _serialConnectionService.Dispose();
                }

                // Clear static singleton instances to allow GC
                PluginSettings.ClearInstance();

                Logger.Info("[MLAstro] MLAstroController disposed");
            }

            _disposed = true;
        }
         
        ~MLAstroController()
        {
            Dispose(false);
        }

        #endregion

        private class RelayCommand : ICommand
        {
            private readonly Action _execute;

            public RelayCommand(Action execute)
            {
                _execute = execute;
            }

            public bool CanExecute(object? parameter) => true;

            public event EventHandler? CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }

            public void Execute(object? parameter) => _execute();
        }

    }

}