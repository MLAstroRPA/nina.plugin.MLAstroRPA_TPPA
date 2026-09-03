using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MLAstro_Robotic_Polar_Alignment.Settings;
using MLAstro_Robotic_Polar_Alignment.Services;

namespace MLAstro_Robotic_Polar_Alignment.Dockables
{ 
    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class PolarAlignmentDockVM : DockableVM, IDisposable
    { 
        private readonly PluginSettings _settings;
        private readonly SerialConnectionService _serialService;
        private System.Timers.Timer? _jogWatchdogTimer;
        private string? _currentJogCommand = null;
        private bool _disposed = false;

        // Static instance for cleanup during plugin teardown
        private static PolarAlignmentDockVM? _instance;
        private static readonly object _instanceLock = new();

        /// <summary>
        /// Gets the current instance of PolarAlignmentDockVM for cleanup purposes.
        /// </summary>
        public static PolarAlignmentDockVM Instance => _instance!;

        // Header Properties
        private string _firmwareVersion = "unknown";
        private string _spiffsVersion = "1.0.118";
        private string _systemStatus = "Idle";
        private Brush _statusForeground = Brushes.White;
        private Brush _connectionStatusColor = Brushes.Gray;
        private string _connectionStatusText = "Disconnected";
        private Visibility _controlsVisibility = Visibility.Collapsed;

        // Manual Movement Properties
        private int _currentSpeed = 3;
        private bool _isRelativeMode = false;
        private Visibility _relativeOptionsVisibility = Visibility.Collapsed;
        private int _relativeDegrees = 0;
        private int _relativeMinutes = 0;
        private int _relativeSeconds = 1;

        // Position Properties
        private string _azPosition = "+0° 00' 00\"";
        private string _altPosition = "+0° 00' 00\"";
        private string _azSteps = "0";
        private string _altSteps = "0";
        private string _azOutSpeed = "0.000";
        private string _altOutSpeed = "0.000";
        private string _azMotorSpeed = "0.000";
        private string _altMotorSpeed = "0.000";
        private string _homedStatus = "No";

        // Alignment Properties
        private int _azErrorDeg = 0;
        private int _azErrorMin = 0;
        private int _azErrorSec = 0;
        private bool _azErrorRight = false;
        private int _altErrorDeg = 0;
        private int _altErrorMin = 0;
        private int _altErrorSec = 0;
        private bool _altErrorUp = false;

        // Flag to track if we're syncing from telemetry (prevents sending command back to hardware)
        private bool _isSyncingFromTelemetry = false;

        // Flag to enable/disable alignment input editing (when ON: user can edit, telemetry sync paused)
        private bool _isAlignmentModifyMode = false;

        // Flag for automated adjustment mode (disables manual controls and telemetry sync)
        private bool _isAutomatedAdjustment = false;

        // Flag to pause telemetry sync for relative values when user is editing
        private bool _isEditingRelativeValues = false;

        // Flag to pause telemetry sync for alignment error fields when the user is editing
        private bool _isEditingAlignment = false;

        // Alarm History (industrial HMI style): one row per driver error/warning code,
        // showing the activation time and (once cleared) the end time on the SAME row.
        private const int AlarmHistoryMaxEntries = 100;
        private readonly ObservableCollection<DriverAlarm> _alarmHistory = new();
        private bool _hasActiveErrors;
        private bool _hasActiveWarnings;
        private Visibility _alarmHistoryVisibility = Visibility.Collapsed;

        public override string ContentId => "MLAstroRPA+TPPA";

        #region Header Properties

        public string FirmwareVersion
        {
            get => _firmwareVersion;
            set => SetProperty(ref _firmwareVersion, value);
        }

        public string SpiffsVersion
        {
            get => _spiffsVersion;
            set => SetProperty(ref _spiffsVersion, value);
        }

        public string SystemStatus
        {
            get => _systemStatus;
            set
            {
                if (SetProperty(ref _systemStatus, value))
                {
                    UpdateStatusColor();
                    OnPropertyChanged(nameof(CanManualControl));
                    OnPropertyChanged(nameof(CanAutomaticControl));
                    OnPropertyChanged(nameof(CanAlign));
                    OnPropertyChanged(nameof(ResetErrorButtonVisibility));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>
        /// Visibility of the RESET ERROR button: only visible when the system status is ERROR.
        /// </summary>
        public Visibility ResetErrorButtonVisibility =>
            SystemStatus.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Brush StatusForeground
        {
            get => _statusForeground;
            private set => SetProperty(ref _statusForeground, value);
        }

        /// <summary>
        /// Industrial-HMI-style alarm history. Each row is a driver error/warning code that
        /// became active at <see cref="DriverAlarm.ActivatedAt"/> and, once cleared, shows
        /// the end time on the SAME row via <see cref="DriverAlarm.ClearedAt"/>.
        /// </summary>
        public ObservableCollection<DriverAlarm> AlarmHistory => _alarmHistory;

        /// <summary>
        /// True while at least one driver code is in ERROR state (value 2).
        /// Disables manual/automatic movement while the system is error-locked.
        /// </summary>
        public bool HasActiveErrors
        {
            get => _hasActiveErrors;
            private set
            {
                if (SetProperty(ref _hasActiveErrors, value))
                {
                    OnPropertyChanged(nameof(CanManualControl));
                    OnPropertyChanged(nameof(CanAutomaticControl));
                    OnPropertyChanged(nameof(CanAlign));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>
        /// True while at least one driver code is in WARNING state (value 1).
        /// </summary>
        public bool HasActiveWarnings
        {
            get => _hasActiveWarnings;
            private set => SetProperty(ref _hasActiveWarnings, value);
        }

        public Visibility AlarmHistoryVisibility
        {
            get => _alarmHistoryVisibility;
            private set => SetProperty(ref _alarmHistoryVisibility, value);
        }

        public Brush ConnectionStatusColor
        {
            get => _connectionStatusColor;
            private set => SetProperty(ref _connectionStatusColor, value);
        }

        public string ConnectionStatusText
        {
            get => _connectionStatusText;
            set => SetProperty(ref _connectionStatusText, value);
        }

        public Visibility ControlsVisibility
        {
            get => _controlsVisibility;
            private set => SetProperty(ref _controlsVisibility, value);
        }

        #endregion

        #region Manual Movement Properties

        public int CurrentSpeed
        {
            get => _currentSpeed;
            set => SetProperty(ref _currentSpeed, value);
        }

        public bool IsRelativeMode
        {
            get => _isRelativeMode;
            set
            {
                if (SetProperty(ref _isRelativeMode, value))
                {
                    RelativeOptionsVisibility = value ? Visibility.Visible : Visibility.Collapsed;

                    // Send mode switch command
                    SendCommand($"JoRe:{(value ? 1 : 0)}\n");
                }
            }
        }

        public Visibility RelativeOptionsVisibility
        {
            get => _relativeOptionsVisibility;
            private set => SetProperty(ref _relativeOptionsVisibility, value);
        }

        public int RelativeDegrees
        {
            get => _relativeDegrees;
            set => SetProperty(ref _relativeDegrees, Math.Max(0, Math.Min(2, value)));
        }

        public int RelativeMinutes
        {
            get => _relativeMinutes;
            set => SetProperty(ref _relativeMinutes, Math.Max(0, Math.Min(60, value)));
        }

        public int RelativeSeconds
        {
            get => _relativeSeconds;
            set => SetProperty(ref _relativeSeconds, Math.Max(0, Math.Min(60, value)));
        }

        /// <summary>
        /// Start editing relative values - pause telemetry sync
        /// </summary>
        public void StartEditingRelative()
        {
            _isEditingRelativeValues = true;
        }

        /// <summary>
        /// Send relative degrees to hardware immediately
        /// </summary>
        public void SendRelativeDegrees()
        {
            _isEditingRelativeValues = false;
            SendCommand($"ReDe:{_relativeDegrees}\n");
            Logger.Info($"[MLAstro] Sent ReDe:{_relativeDegrees}");
        }

        /// <summary>
        /// Send relative minutes to hardware immediately
        /// </summary>
        public void SendRelativeMinutes()
        {
            _isEditingRelativeValues = false;
            SendCommand($"ReAM:{_relativeMinutes}\n");
            Logger.Info($"[MLAstro] Sent ReAM:{_relativeMinutes}");
        }

        /// <summary>
        /// Send relative seconds to hardware immediately
        /// </summary>
        public void SendRelativeSeconds()
        {
            _isEditingRelativeValues = false;
            SendCommand($"ReAS:{_relativeSeconds}\n");
            Logger.Info($"[MLAstro] Sent ReAS:{_relativeSeconds}");
        }

        #endregion

        #region Position Properties

        public string AzPosition
        {
            get => _azPosition;
            set => SetProperty(ref _azPosition, value);
        }

        public string AltPosition
        {
            get => _altPosition;
            set => SetProperty(ref _altPosition, value);
        }

        // Moved position (relative to alignment start)
        private string _azMovedPosition = "+0° 00' 00\"";
        private string _altMovedPosition = "+0° 00' 00\"";

        public string AzMovedPosition
        {
            get => _azMovedPosition;
            set => SetProperty(ref _azMovedPosition, value);
        }

        public string AltMovedPosition
        {
            get => _altMovedPosition;
            set => SetProperty(ref _altMovedPosition, value);
        }

        public string AzSteps
        {
            get => _azSteps;
            set => SetProperty(ref _azSteps, value);
        }

        public string AltSteps
        {
            get => _altSteps;
            set => SetProperty(ref _altSteps, value);
        }

        public string AzOutSpeed
        {
            get => _azOutSpeed;
            set => SetProperty(ref _azOutSpeed, value);
        }

        public string AltOutSpeed
        {
            get => _altOutSpeed;
            set => SetProperty(ref _altOutSpeed, value);
        }

        public string AzMotorSpeed
        {
            get => _azMotorSpeed;
            set => SetProperty(ref _azMotorSpeed, value);
        }

        public string AltMotorSpeed
        {
            get => _altMotorSpeed;
            set => SetProperty(ref _altMotorSpeed, value);
        }

        public string HomedStatus
        {
            get => _homedStatus;
            set => SetProperty(ref _homedStatus, value);
        }

        #endregion

        #region Alignment Properties

        public int AzErrorDeg
        {
            get => _azErrorDeg;
            set => SetProperty(ref _azErrorDeg, value);
        }

        public int AzErrorMin
        {
            get => _azErrorMin;
            set => SetProperty(ref _azErrorMin, value);
        }

        public int AzErrorSec
        {
            get => _azErrorSec;
            set => SetProperty(ref _azErrorSec, value);
        }

        public bool AzErrorRight
        {
            get => _azErrorRight;
            set
            {
                if (SetProperty(ref _azErrorRight, value) && !_isSyncingFromTelemetry)
                {
                    // User changed - Send direction command to hardware immediately (1 = Right, 0 = Left)
                    SendCommand($"AzDi:{(value ? 1 : 0)}\n");
                    Logger.Info($"[MLAstro] User changed AzDi direction: {(value ? "Right" : "Left")}");
                }
            }
        }

        public int AltErrorDeg
        {
            get => _altErrorDeg;
            set => SetProperty(ref _altErrorDeg, value);
        }

        public int AltErrorMin
        {
            get => _altErrorMin;
            set => SetProperty(ref _altErrorMin, value);
        }

        public int AltErrorSec
        {
            get => _altErrorSec;
            set => SetProperty(ref _altErrorSec, value);
        }

        public bool AltErrorUp
        {
            get => _altErrorUp;
            set
            {
                if (SetProperty(ref _altErrorUp, value) && !_isSyncingFromTelemetry)
                {
                    // User changed - Send direction command to hardware immediately (1 = Up, 0 = Down)
                    SendCommand($"AlDi:{(value ? 1 : 0)}\n");
                    Logger.Info($"[MLAstro] User changed AlDi direction: {(value ? "Up" : "Down")}");
                }
            }
        }

        /// <summary>
        /// When ON (Modify): User can edit alignment values, telemetry sync is paused for these fields.
        /// When OFF (Done): Inputs are disabled, send all settings to hardware, telemetry updates continuously.
        /// </summary>
        public bool IsAlignmentModifyMode
        {
            get => _isAlignmentModifyMode;
            set
            {
                // Cannot modify when in automated adjustment mode
                if (_isAutomatedAdjustment && value)
                {
                    return;
                }

                var wasModifying = _isAlignmentModifyMode;
                if (SetProperty(ref _isAlignmentModifyMode, value))
                {
                    // When switching from Modify (ON) to Done (OFF), send all alignment settings
                    if (wasModifying && !value)
                    {
                        SendAlignmentSettings();
                    }
                    Logger.Info($"[MLAstro] Alignment modify mode: {(value ? "Modify" : "Done")}");
                }
            }
        }

        /// <summary>
        /// When ON: Disables Modify button and all Align buttons, stops telemetry sync for Polar Alignment.
        /// Used when external automation (e.g., plate solving) is controlling the alignment.
        /// </summary>
        public bool IsAutomatedAdjustment
        {
            get => _isAutomatedAdjustment;
            set
            {
                if (SetProperty(ref _isAutomatedAdjustment, value))
                {
                    // If turning on automated mode, force modify mode off
                    if (value && _isAlignmentModifyMode)
                    {
                        _isAlignmentModifyMode = false;
                        OnPropertyChanged(nameof(IsAlignmentModifyMode));
                    }
                    // Notify CanModify, CanAlign and CanManualControl changed for button enable/disable
                    OnPropertyChanged(nameof(CanModify));
                    OnPropertyChanged(nameof(CanAlign));
                    OnPropertyChanged(nameof(CanManualControl));
                    OnPropertyChanged(nameof(CanAutomaticControl));
                    Logger.Info($"[MLAstro] Automated adjustment mode: {(value ? "ON" : "OFF")}");
                }
            }
        }

        /// <summary>
        /// Returns true if Modify button should be enabled (not in automated mode)
        /// </summary>
        public bool CanModify => !_isAutomatedAdjustment && !IsExternalLocked;

        /// <summary>
        /// Returns true while firmware telemetry reports manual movement or an idle state.
        /// Automated workflows own both axes and therefore disable every movement start control.
        /// </summary>
        public bool CanManualControl => !_isAutomatedAdjustment && !IsAutomaticMotion && !HasActiveErrors && !IsExternalLocked;

        /// <summary>
        /// Returns true only when the firmware reports both motors are idle.
        /// Manual MOVING telemetry permits manual control but prevents starting an automatic workflow.
        /// </summary>
        public bool CanAutomaticControl => !_isAutomatedAdjustment && !IsMotionActive && !HasActiveErrors && !IsExternalLocked;

        public bool CanAlign => CanAutomaticControl;

        /// <summary>TPPA (plugin ngoài) đang GIỮ quyền điều khiển -> khoá hầu hết điều khiển/cài đặt
        /// (chỉ chừa nút STOP/E-STOP và tab CONNECTION).</summary>
        public bool IsExternalLocked
        {
            get => _externalLocked;
            private set
            {
                if (_externalLocked == value) return;
                _externalLocked = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanModify));
                OnPropertyChanged(nameof(CanManualControl));
                OnPropertyChanged(nameof(CanAutomaticControl));
                OnPropertyChanged(nameof(CanAlign));
            }
        }
        private bool _externalLocked;

        private bool IsAutomaticMotion => SystemStatus.Equals("HOMING", StringComparison.OrdinalIgnoreCase) ||
                          SystemStatus.Equals("ALIGNING", StringComparison.OrdinalIgnoreCase) ||
                          SystemStatus.Equals("CALIBRATING", StringComparison.OrdinalIgnoreCase) ||
                          SystemStatus.Equals("TUNING", StringComparison.OrdinalIgnoreCase);

        private bool IsMotionActive => IsAutomaticMotion ||
                           SystemStatus.Equals("MOVING", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Send all alignment settings to hardware in one command
        /// </summary>
        private void SendAlignmentSettings()
        {
            var azDir = _azErrorRight ? 1 : 0;
            var alDir = _altErrorUp ? 1 : 0;
            var command = $"AzED:{_azErrorDeg},AzEM:{_azErrorMin},AzES:{_azErrorSec},AzDi:{azDir}," +
                          $"AlED:{_altErrorDeg},AlEM:{_altErrorMin},AlES:{_altErrorSec},AlDi:{alDir}\n";
            SendCommand(command);
            Logger.Info($"[MLAstro] Sent alignment settings: {command.TrimEnd()}");
        }

        /// <summary>
        /// Toggle between Modify and Done modes
        /// </summary>
        private void OnToggleModify()
        {
            IsAlignmentModifyMode = !IsAlignmentModifyMode;
        }

        /// <summary>
        /// Start editing alignment values - pause telemetry sync for these fields.
        /// </summary>
        public void StartEditingAlignment()
        {
            _isEditingAlignment = true;
        }

        /// <summary>
        /// End editing alignment values - resume telemetry sync for these fields.
        /// </summary>
        public void EndEditingAlignment()
        {
            _isEditingAlignment = false;
        }

        #endregion

        #region Commands

        // Speed Commands
        public ICommand SetSpeedCommand { get; }

        // Relative Step Commands
        public ICommand IncRelativeDegreesCommand { get; }
        public ICommand DecRelativeDegreesCommand { get; }
        public ICommand IncRelativeMinutesCommand { get; }
        public ICommand DecRelativeMinutesCommand { get; }
        public ICommand IncRelativeSecondsCommand { get; }
        public ICommand DecRelativeSecondsCommand { get; }

        // Movement Commands
        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand MoveLeftCommand { get; }
        public ICommand MoveRightCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ForceStopCommand { get; }
        public ICommand ResetErrorCommand { get; }

        // Home Commands
        public ICommand SetHomeCommand { get; }
        public ICommand ReturnHomeCommand { get; }
        public ICommand ResetHomeCommand { get; }

        // Alignment Commands
        public ICommand AlignAzCommand { get; }
        public ICommand AlignAltCommand { get; }
        public ICommand AlignAllCommand { get; }
        public ICommand ToggleModifyCommand { get; }

        #endregion

        [ImportingConstructor]
        public PolarAlignmentDockVM(IProfileService profileService, PluginSettings settings, SerialConnectionService serialService)
            : base(profileService)
        {
            Title = "MLAstro RPA Control";
            Logger.Info("[MLAstro] PolarAlignmentDockVM created");

            // Register this instance for cleanup during plugin teardown
            lock (_instanceLock)
            {
                _instance = this;
                Logger.Info($"[MLAstro] PolarAlignmentDockVM Instance registered: {this.GetHashCode()}");
            }

            _settings = settings;

            // Use singleton instance to ensure we subscribe to the correct instance
            // MEF creates separate instances for different components, so we must use the singleton
            _serialService = SerialConnectionService.Instance;
            Logger.Info($"[MLAstro] Using singleton SerialConnectionService (injected: {serialService.GetHashCode()}, singleton: {_serialService.GetHashCode()})");

            // Initialize Commands
#pragma warning disable CS0618 // NINA.RelayCommand is obsolete, but intentionally kept: it hooks CommandManager.RequerySuggested
            SetSpeedCommand = new RelayCommand(OnSetSpeed);

            IncRelativeDegreesCommand = new RelayCommand(_ => RelativeDegrees++);
            DecRelativeDegreesCommand = new RelayCommand(_ => RelativeDegrees--);
            IncRelativeMinutesCommand = new RelayCommand(_ => RelativeMinutes += 5);
            DecRelativeMinutesCommand = new RelayCommand(_ => RelativeMinutes -= 5);
            IncRelativeSecondsCommand = new RelayCommand(_ => RelativeSeconds += 5);
            DecRelativeSecondsCommand = new RelayCommand(_ => RelativeSeconds -= 5);

            // Movement commands are handled via Mouse events in code-behind
            MoveUpCommand = new RelayCommand(_ => { }); // Placeholder
            MoveDownCommand = new RelayCommand(_ => { });
            MoveLeftCommand = new RelayCommand(_ => { });
            MoveRightCommand = new RelayCommand(_ => { });
            StopCommand = new RelayCommand(_ => StopAllMovement());
            ForceStopCommand = new RelayCommand(_ => ForceStop());
            ResetErrorCommand = new RelayCommand(_ => SendCommand("ReER:1\n"));

            SetHomeCommand = new RelayCommand(_ => SendCommand("SetH:1\n"));
            ReturnHomeCommand = new RelayCommand(_ => SendCommand("RetH:1\n"));
            ResetHomeCommand = new RelayCommand(_ => SendCommand("RstH:1\n"));

            AlignAzCommand = new RelayCommand(_ => OnAlignAz(), _ => CanAlign);
            AlignAltCommand = new RelayCommand(_ => OnAlignAlt(), _ => CanAlign);
            AlignAllCommand = new RelayCommand(_ => OnAlignAll(), _ => CanAlign);
            ToggleModifyCommand = new RelayCommand(_ => OnToggleModify(), _ => CanModify);
#pragma warning restore CS0618

            // Subscribe to serial service events (using singleton)
            _serialService.PropertyChanged += OnSerialServicePropertyChanged;
            _serialService.TelemetryDataReceived += OnTelemetryDataReceived;
            _serialService.CompletionReceived += OnCompletionReceived;
            _serialService.ErrorStateChanged += OnErrorStateChanged;
            // Khoá/mở khoá UI khi TPPA (plugin ngoài) giữ/thả quyền điều khiển.
            _serialService.AddExternalControlListener(active => IsExternalLocked = active);

            FirmwareVersion = _serialService.FirmwareVersion;

            Logger.Info($"[MLAstro] ViewModel subscribed to SerialConnectionService singleton (instance: {_serialService.GetHashCode()})");
        }

        private void OnTelemetryDataReceived(object? sender, TelemetryDataEventArgs e)
        {
            if (e?.Data == null)
            {
                Logger.Warning("[MLAstro] OnTelemetryDataReceived: event data is null");
                return;
            }

            Logger.Info($"[MLAstro] ViewModel received telemetry - Status: {e.Data.Status}, AzPos: {e.Data.AzPosition}");

            // Update positions from home (AzPH/AlPH)
            AzPosition = e.Data.AzPosition;
            AltPosition = e.Data.AltPosition;

            // Update moved positions (Mpos - relative to alignment start)
            AzMovedPosition = e.Data.AzMovedPosition ?? "+0° 00' 00\"";
            AltMovedPosition = e.Data.AltMovedPosition ?? "+0° 00' 00\"";

            // Update system status with color
            SystemStatus = e.Data.Status;
            StatusForeground = e.Data.Status switch
            {
                "MOVING" => Brushes.Yellow,
                "HOMING" => Brushes.Cyan,
                "ALIGNING" => Brushes.Orange,
                "ALIGN_COMPLETED" => Brushes.LimeGreen,
                "HOME_COMPLETED" => Brushes.LimeGreen,
                "ERROR" => Brushes.Red,
                "READY" => Brushes.LimeGreen,
                _ => Brushes.White
            };

            Logger.Info($"[MLAstro] ViewModel updated - SystemStatus: {SystemStatus}, StatusForeground: {StatusForeground}");

            // Update current speed level (sync from hardware)
            if (e.Data.SpeedLevel > 0 && e.Data.SpeedLevel <= 5)
            {
                CurrentSpeed = e.Data.SpeedLevel;
            }

            // Update relative mode settings (sync from hardware)
            // Don't trigger command send by using backing field
            if (_isRelativeMode != e.Data.IsRelativeMode)
            {
                _isRelativeMode = e.Data.IsRelativeMode;
                RelativeOptionsVisibility = _isRelativeMode ? Visibility.Visible : Visibility.Collapsed;
                OnPropertyChanged(nameof(IsRelativeMode));
            }

            // Update relative values only when changed (skip if user is editing)
            if (e.Data.IsRelativeMode && !_isEditingRelativeValues)
            {
                RelativeDegrees = e.Data.RelativeDegrees;
                RelativeMinutes = e.Data.RelativeMinutes;
                RelativeSeconds = e.Data.RelativeSeconds;
            }

            // Update homed status from hardware (Read-Only)
            HomedStatus = e.Data.IsHomed ? "Yes" : "No";

            // Skip alignment sync if the user is editing OR modify mode is ON OR automated adjustment is ON
            if (!_isEditingAlignment && !_isAlignmentModifyMode && !_isAutomatedAdjustment)
            {
                // Sync alignment directions from hardware (using flag to prevent sending command back)
                _isSyncingFromTelemetry = true;
                try
                {
                    // Use property setters to ensure UI binding updates
                    AzErrorRight = e.Data.AzDirection;
                    AltErrorUp = e.Data.AltDirection;
                }
                finally
                {
                    _isSyncingFromTelemetry = false;
                }

                // Sync alignment error values from hardware (only notifies UI when the value changes)
                AzErrorDeg = e.Data.AzErrorDegrees;
                AzErrorMin = e.Data.AzErrorMinutes;
                AzErrorSec = e.Data.AzErrorSeconds;
                AltErrorDeg = e.Data.AltErrorDegrees;
                AltErrorMin = e.Data.AltErrorMinutes;
                AltErrorSec = e.Data.AltErrorSeconds;
            }

            // Update steps display (calculate from position in degrees and steps/degree)
            if (e.Data.AzStepsPerDegree > 0)
            {
                var azSteps = (long)(e.Data.AzPositionDegrees * e.Data.AzStepsPerDegree);
                AzSteps = azSteps.ToString("N0");
            }
            else
            {
                AzSteps = "0";
            }

            if (e.Data.AltStepsPerDegree > 0)
            {
                var altSteps = (long)(e.Data.AltPositionDegrees * e.Data.AltStepsPerDegree);
                AltSteps = altSteps.ToString("N0");
            }
            else
            {
                AltSteps = "0";
            }

            // Speed values would need to be calculated from motor data
            // For now, keep placeholder values
            // AzOutSpeed, AltOutSpeed, AzMotorSpeed, AltMotorSpeed remain as initialized

            // Update WiFi indicator in firmware version (optional)
            if (!string.IsNullOrWhiteSpace(e.Data.StationIP))
            {
                // Could show WiFi status in header if needed
            }
        }

        private void OnCompletionReceived(object? sender, string completionType)
        {
            switch (completionType)
            {
                case "AzAN":
                    Logger.Info("[MLAstro] Azimuth alignment completed");
                    break;
                case "AlAN":
                    Logger.Info("[MLAstro] Altitude alignment completed");
                    break;
                case "AAll":
                    Logger.Info("[MLAstro] All alignment completed");
                    break;
                case "HOME":
                    Logger.Info("[MLAstro] Home return completed");
                    // HomedStatus is now updated from telemetry (Home field)
                    break;
            }
        }

        private void OnErrorStateChanged(object? sender, DriverErrorState state)
        {
            if (state == null)
            {
                return;
            }

            // Guard: event may be raised from a background thread; marshal to UI thread once.
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => OnErrorStateChanged(sender, state)));
                return;
            }

            // Codes currently active (value 1 = WARNING, 2 = ERROR)
            var activeNow = new HashSet<string>(
                state.Codes.Where(kv => kv.Value == 1 || kv.Value == 2).Select(kv => kv.Key),
                StringComparer.OrdinalIgnoreCase);

            // Close rows whose code is no longer active -> mark the END time on the same row
            foreach (var row in _alarmHistory.Where(a => a.IsActive).ToList())
            {
                if (!activeNow.Contains(row.Code))
                {
                    row.ClearedAt = DateTime.Now;
                }
            }

            // Add a new row for each code that just became active
            foreach (var kv in state.Codes)
            {
                if (kv.Value != 1 && kv.Value != 2)
                {
                    continue;
                }

                // "Sys" is an aggregate indicator - skip it so we do not create a generic
                // "System error" row next to the specific driver code that actually caused it.
                if (kv.Key.Equals("Sys", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var alreadyActive = _alarmHistory.Any(a => a.Code.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) && a.IsActive);
                if (alreadyActive)
                {
                    continue;
                }

                var alarm = new DriverAlarm(kv.Key, DriverErrorState.Describe(kv.Key), kv.Value);
                _alarmHistory.Add(alarm);
                NotifyAlarm(alarm);
            }

            // Keep history bounded (trim oldest first)
            while (_alarmHistory.Count > AlarmHistoryMaxEntries)
            {
                _alarmHistory.RemoveAt(0);
            }

            HasActiveErrors = state.HasErrors;
            HasActiveWarnings = state.HasWarnings;
            AlarmHistoryVisibility = _alarmHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void NotifyAlarm(DriverAlarm alarm)
        {
            try
            {
                if (alarm.Severity == 2)
                {
                    Notification.ShowError($"MLAstro RPA: {alarm.Description}");
                }
                else
                {
                    Notification.ShowWarning($"MLAstro RPA: {alarm.Description}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Failed to show alarm notification: {ex.Message}");
            }
        }

        private void ClearAlarmHistory()
        {
            // Guard: may be called from a background thread (property-changed event)
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(ClearAlarmHistory));
                return;
            }

            _alarmHistory.Clear();
            HasActiveErrors = false;
            HasActiveWarnings = false;
            AlarmHistoryVisibility = Visibility.Collapsed;
        }

        private void OnSerialServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SerialConnectionService.IsConnected))
            {
                UpdateConnectionStatus();
            }
            else if (e.PropertyName == nameof(SerialConnectionService.HandshakeStatus))
            {
                UpdateConnectionStatus();
            }
            else if (e.PropertyName == nameof(SerialConnectionService.FirmwareVersion))
            {
                FirmwareVersion = _serialService.FirmwareVersion;
            }
        }

        private void UpdateConnectionStatus()
        {
            if (!_serialService.IsConnected)
            {
                ClearAlarmHistory();
            }

            if (_serialService.IsConnected && _serialService.HandshakeStatus == "OK!")
            {
                ConnectionStatusColor = Brushes.LimeGreen;
                ConnectionStatusText = "Connected";
                ControlsVisibility = Visibility.Visible;
            }
            else if (_serialService.IsConnected && _serialService.HandshakeStatus == "NO ANSWER")
            {
                ConnectionStatusColor = Brushes.Red;
                ConnectionStatusText = "Disconnected";
                SystemStatus = "DISCONNECTED";
                StatusForeground = Brushes.Red;
                ControlsVisibility = Visibility.Collapsed;
            }
            else if (_serialService.IsConnected)
            {
                ConnectionStatusColor = Brushes.Yellow;
                ConnectionStatusText = "Connecting...";
                ControlsVisibility = Visibility.Collapsed;
            }
            else
            {
                ConnectionStatusColor = Brushes.Gray;
                ConnectionStatusText = "Disconnected";
                SystemStatus = "DISCONNECTED";
                StatusForeground = Brushes.Gray;
                ControlsVisibility = Visibility.Collapsed;
            }
        }

        private void UpdateStatusColor()
        {
            StatusForeground = SystemStatus.ToLower() switch
            {
                "error" => Brushes.Red,
                "moving" => Brushes.Yellow,
                "aligning" => Brushes.Cyan,
                "homing" => Brushes.Orange,
                _ => Brushes.White
            };
        }

        #region Command Implementations

        private void OnSetSpeed(object parameter)
        {
            if (parameter is int speed || int.TryParse(parameter?.ToString(), out speed))
            {
                CurrentSpeed = speed;
                SendCommand($"SLvl:{speed}\n");
            }
        }

        public void StartMoveUp()
        {
            if (IsRelativeMode)
            {
                SendRelativeMove("MAlU");
            }
            else
            {
                StartJogWatchdog("MAlU:1\n");
            }
        }

        public void StartMoveDown()
        {
            if (IsRelativeMode)
            {
                SendRelativeMove("MAlD");
            }
            else
            {
                StartJogWatchdog("MAlD:1\n");
            }
        }

        public void StartMoveLeft()
        {
            if (IsRelativeMode)
            {
                SendRelativeMove("MAzL");
            }
            else
            {
                StartJogWatchdog("MAzL:1\n");
            }
        }

        public void StartMoveRight()
        {
            if (IsRelativeMode)
            {
                SendRelativeMove("MAzR");
            }
            else
            {
                StartJogWatchdog("MAzR:1\n");
            }
        }

        public void StopAllMovement()
        {
            StopJogMovement();
            SendCommand("STOP:1\n");
            // Nếu TPPA đang giữ quyền điều khiển (external control) thì báo TPPA dừng PA ngay.
            if (_serialService.IsExternalControlActive) _serialService.NotifyExternalStop("MLAstro STOP pressed");
        }

        public void ForceStop()
        {
            SendCommand("ESTOP:1\n");
            if (_serialService.IsExternalControlActive) _serialService.NotifyExternalStop("MLAstro E-STOP pressed");
        }

        public void StopJogMovement()
        {
            if (!IsRelativeMode)
            {
                StopJogWatchdog();
            }
        }

        private void StartJogWatchdog(string command)
        {
            _currentJogCommand = command;

            if (_jogWatchdogTimer == null)
            {
                _jogWatchdogTimer = new System.Timers.Timer(250); // Send every 250ms
                _jogWatchdogTimer.Elapsed += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(_currentJogCommand) && _serialService.IsConnected)
                    {
                        _serialService.Send(_currentJogCommand);
                    }
                };
            }

            _jogWatchdogTimer.Start();
            SendCommand(command); // Send immediately first time
            Logger.Info($"[MLAstro] Started Jog watchdog: {command.TrimEnd()}");
        }

        private void StopJogWatchdog()
        {
            _jogWatchdogTimer?.Stop();

            if (!string.IsNullOrEmpty(_currentJogCommand))
            {
                // Send stop command (change :1 to :0)
                var stopCmd = _currentJogCommand.Replace(":1", ":0");
                SendCommand(stopCmd);
                Logger.Info($"[MLAstro] Stopped Jog: {stopCmd.TrimEnd()}");
                _currentJogCommand = null;
            }
        }

        private void SendRelativeMove(string axis)
        {
            // First, send relative angle setup
            SendCommand($"ReDe:{RelativeDegrees}\n");
            SendCommand($"ReAM:{RelativeMinutes}\n");
            SendCommand($"ReAS:{RelativeSeconds}\n");

            // Then send move command (just once, no watchdog needed)
            SendCommand($"{axis}:1\n");
            Logger.Info($"[MLAstro] Relative move: {axis} - {RelativeDegrees}° {RelativeMinutes}' {RelativeSeconds}\"");
        }

        private void SendMoveCommand(string command, bool start)
        {
            // Deprecated - now using StartMove* methods
            var cmd = $"{command}:{(start ? 1 : 0)}\n";
            SendCommand(cmd);
        }

        private void SendCommand(string command)
        {
            if (_serialService.IsConnected)
            {
                _serialService.Send(command);
                Logger.Info($"[MLAstro] Sent command: {command.TrimEnd()}");
            }
        }

        private void OnAlignAz()
        {
            EndEditingAlignment();

            var direction = AzErrorRight ? 1 : 0;
            var command = $"AzED:{AzErrorDeg},AzEM:{AzErrorMin},AzES:{AzErrorSec},AzAN:1\n";
            SendCommand(command);
        }

        private void OnAlignAlt()
        {
            EndEditingAlignment();

            var direction = AltErrorUp ? 1 : 0;
            var command = $"AlED:{AltErrorDeg},AlEM:{AltErrorMin},AlES:{AltErrorSec},AlAN:1\n";
            SendCommand(command);
        }

        private void OnAlignAll()
        {
            EndEditingAlignment();

            var command = $"AzED:{AzErrorDeg},AzEM:{AzErrorMin},AzES:{AzErrorSec}," +
                         $"AlED:{AltErrorDeg},AlEM:{AltErrorMin},AlES:{AltErrorSec},AAll:1\n";
            SendCommand(command);
        }

        #endregion

        #region Cleanup / Dispose

        public void Cleanup()
        {
            Dispose();
        }

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
                Logger.Info("[MLAstro] PolarAlignmentDockVM disposing...");

                // Stop jog watchdog timer
                StopJogWatchdog();
                if (_jogWatchdogTimer != null)
                {
                    _jogWatchdogTimer.Dispose();
                    _jogWatchdogTimer = null;
                }

                // Unsubscribe from serial service events
                if (_serialService != null)
                {
                    _serialService.PropertyChanged -= OnSerialServicePropertyChanged;
                    _serialService.TelemetryDataReceived -= OnTelemetryDataReceived;
                    _serialService.CompletionReceived -= OnCompletionReceived;
                    _serialService.ErrorStateChanged -= OnErrorStateChanged;
                }

                // Clear static instance
                lock (_instanceLock)
                {
                    if (ReferenceEquals(_instance, this))
                    {
                        _instance = null;
                    }
                }

                Logger.Info("[MLAstro] PolarAlignmentDockVM disposed");
            }

            _disposed = true;
        }

        ~PolarAlignmentDockVM()
        {
            Dispose(false);
        }

        #endregion
    }
}
