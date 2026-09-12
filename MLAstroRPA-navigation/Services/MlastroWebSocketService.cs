using MLAstro_Robotic_Polar_Alignment.Dockables;
using MLAstro_Robotic_Polar_Alignment.Settings;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MLAstro_Robotic_Polar_Alignment.Services
{
    /// <summary>
    /// Transport WIRELESS cho MLAstroRPA: kết nối tới thiết bị qua WebSocket
    /// (mặc định ws://MLAstroRPA.local/ws, có thể nhập IP trực tiếp khi mDNS không hoạt động).
    ///
    /// Đặc điểm:
    ///  - Dùng CÙNG endpoint /ws như Web UI, phân biệt bằng từ khóa handshake "MLAstroRPA-TC"
    ///    (giống serial) → firmware trao quyền điều khiển + monitor cho PC và khóa điều khiển Web.
    ///  - Telemetry JSON được chuyển ngược thành ĐÚNG định dạng text của firmware serial rồi bơm
    ///    vào <see cref="SerialConnectionService.InjectIncomingText"/> → toàn bộ parser/UI hiện có
    ///    (CONTROL + CONFIGURATION + dock TPPA) hoạt động y như khi dùng cổng COM.
    ///  - Lệnh dạng text của firmware serial được dịch sang JSON của WebSocket API.
    /// </summary>
    public sealed class MlastroWebSocketService : INotifyPropertyChanged, IDisposable
    {
        public const string HandshakeKey = "MLAstroRPA-TC";

        private const int ReceiveChunkSize = 16 * 1024;
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ConfigAckTimeout = TimeSpan.FromSeconds(8);

        private readonly PluginSettings _settings;
        private readonly SerialConnectionService _serial;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _stateLock = new();

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _cts;
        private Task? _receiveLoop;
        private TaskCompletionSource<bool>? _handshakeTcs;
        private TaskCompletionSource<string>? _configAckTcs;
        private readonly Dictionary<string, Dictionary<string, object>> _snapshotSections = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastAlignSentUtc = DateTime.MinValue;
        private bool _alignInFlight;
        private bool _sawBusySinceAlign;
        private int _speedLevelFromSnapshot = 3;

        // Trạng thái "chế độ" của dock (JoRe/ReDe/ReAM/ReAS chỉ có ở giao thức serial):
        // WebSocket không có lệnh tương đương nên phải ghi nhớ để dịch đúng arrow-press
        // thành `move` (jog liên tục) hay `moveRelative` (dịch một góc).
        private bool _relativeMode;
        private int _relativeDegrees;
        private int _relativeMinutes;
        private int _relativeSeconds;

        // Jog đang chạy: dock gửi lại lệnh mỗi 250 ms (watchdog) nhưng firmware WS từ chối
        // mọi lệnh motion khi đang chạy → phải bỏ các lần gửi lặp để tránh spam alert.
        private string? _activeJogAxis;
        private int _activeJogDirection;

        /// <summary>Singleton để controller và driver TPPA dùng CHUNG một phiên WS (firmware chỉ cho 1 PC).</summary>
        public static MlastroWebSocketService? Instance { get; private set; }

        /// <summary>Mọi dòng text đã tổng hợp (telemetry / ok / AAll:COMPLETED): cho adapter ISerialLink của TPPA.</summary>
        public event Action<string>? LineReceived;

        /// <summary>Trạng thái kết nối đổi (arg = IsConnected).</summary>
        public event Action<bool>? StateChanged;

        /// <summary>Nhấn STOP/E-STOP bên MLAstro (hoặc mất kết nối) → TPPA phải dừng PA.</summary>
        public event Action<string>? StopRequested;

        public event PropertyChangedEventHandler? PropertyChanged;

        public MlastroWebSocketService(PluginSettings settings, SerialConnectionService serial)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _serial = serial ?? throw new ArgumentNullException(nameof(serial));

            // Trở thành facade cho UI/dock: từ giờ SerialConnectionService báo trạng thái + gửi lệnh
            // qua chính phiên WebSocket này khi người dùng chọn Wireless connection.
            _serial.WirelessProxy = this;

            Instance = this;
        }

        // ==================================================================
        // Trạng thái
        // ==================================================================
        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (_isConnected == value) return;
                _isConnected = value;
                OnPropertyChanged();
                try { StateChanged?.Invoke(value); } catch { }
            }
        }

        private string _connectionStatus = "Disconnected";
        public string ConnectionStatus
        {
            get => _connectionStatus;
            private set
            {
                if (_connectionStatus == value) return;
                _connectionStatus = value;
                OnPropertyChanged();
            }
        }

        private string _handshakeStatus = string.Empty;
        public string HandshakeStatus
        {
            get => _handshakeStatus;
            private set
            {
                if (_handshakeStatus == value) return;
                _handshakeStatus = value;
                OnPropertyChanged();
            }
        }

        public string ConfiguredAddress => string.IsNullOrWhiteSpace(_settings.MlaHost) ? "MLAstroRPA.local" : _settings.MlaHost;

        private bool _externalControlActive;
        public bool IsExternalControlActive
        {
            get { lock (_stateLock) return _externalControlActive; }
        }

        private readonly List<Action<bool>> _externalControlListeners = new();
        private readonly List<Action<string>> _externalStopListeners = new();

        public void AddExternalControlListener(Action<bool> listener)
        {
            if (listener == null) return;
            lock (_stateLock) { if (!_externalControlListeners.Contains(listener)) _externalControlListeners.Add(listener); }
        }

        public void RemoveExternalControlListener(Action<bool> listener)
        {
            if (listener == null) return;
            lock (_stateLock) { _externalControlListeners.Remove(listener); }
        }

        public void AddExternalStopListener(Action<string> listener)
        {
            if (listener == null) return;
            lock (_stateLock) { if (!_externalStopListeners.Contains(listener)) _externalStopListeners.Add(listener); }
        }

        public void RemoveExternalStopListener(Action<string> listener)
        {
            if (listener == null) return;
            lock (_stateLock) { _externalStopListeners.Remove(listener); }
        }

        public void NotifyExternalStop(string reason)
        {
            Logger.Info($"[MLAstro][WS] NotifyExternalStop: {reason}");
            List<Action<string>>? copy;
            lock (_stateLock) { copy = _externalStopListeners.Count > 0 ? _externalStopListeners.ToList() : null; }
            if (copy == null) return;
            foreach (var l in copy)
            {
                try { l(reason); } catch { }
            }
            try { StopRequested?.Invoke(reason); } catch { }
        }

        public void SetExternalPauseQuery(bool pause)
        {
            // Wireless: telemetry do firmware đẩy định kỳ, không có poll "?" → không cần pause.
            Logger.Info($"[MLAstro][WS] SetExternalPauseQuery({pause}) ignored (fw push telemetry).");
        }

        // ==================================================================
        // Kết nối / ngắt
        // ==================================================================
        public async Task<bool> ConnectAsync(CancellationToken token = default)
        {
            if (IsConnected) return true;

            // Phiên mới: xoá bảng log của phiên trước (giống refresh trang Web UI) để bảng chỉ
            // hiển thị sự kiện của lần kết nối này. Firmware cũng không replay log cũ cho PC nữa.
            ClearSystemLog();

            var host = ConfiguredAddress.Trim();
            var port = _settings.MlaPort <= 0 ? 80 : _settings.MlaPort;
            var path = string.IsNullOrWhiteSpace(_settings.MlaPath) ? "/ws" : _settings.MlaPath;
            if (!path.StartsWith("/")) path = "/" + path;

            ConnectionStatus = $"Resolving {host}...";
            HandshakeStatus = string.Empty;
            AppendLog($"Resolving {host} ...");

            string endpointHost;
            if (IPAddress.TryParse(host, out _))
            {
                endpointHost = host;
            }
            else
            {
                var resolved = await ResolveMdnsAsync(host, token).ConfigureAwait(false);
                if (resolved == null)
                {
                    ConnectionStatus = $"Cannot resolve {host} (mDNS failed). Enter the device IP instead.";
                    AppendLog($"ERROR: mDNS resolution failed for {host}. Use the IP address field fallback.");
                    return false;
                }
                endpointHost = resolved.ToString();
                AppendLog($"mDNS {host} -> {endpointHost}");
            }

            var uri = new Uri($"ws://{endpointHost}:{port}{path}");
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

            try
            {
                ConnectionStatus = $"Connecting to {uri.Host}:{port}...";
                await socket.ConnectAsync(uri, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                ConnectionStatus = $"Connect failed: {ex.Message}";
                AppendLog($"ERROR: {ex.Message}");
                Logger.Error($"[MLAstro][WS] Connect failed: {ex.Message}");
                return false;
            }

            _socket = socket;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _snapshotSections.Clear();

            // Đọc init snapshot + chờ handshake
            _handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token));

            AppendLog($"Connected to {uri.Host}:{port}{path}. Sending handshake '{HandshakeKey}'...");
            await SendJsonAsync(BuildHandshake(), _cts.Token).ConfigureAwait(false);

            var completed = await Task.WhenAny(_handshakeTcs.Task, Task.Delay(HandshakeTimeout, _cts.Token)).ConfigureAwait(false);
            if (completed != _handshakeTcs.Task || !_handshakeTcs.Task.Result)
            {
                var reason = HandshakeStatus;
                ConnectionStatus = string.IsNullOrWhiteSpace(reason)
                    ? "Handshake failed (no answer from device)."
                    : $"Handshake refused: {reason}";
                AppendLog($"ERROR: handshake failed - {reason}");
                AbortSocket();
                return false;
            }

            IsConnected = true;
            HandshakeStatus = "OK!";
            ConnectionStatus = $"Connected (wireless) - {uri.Host}";

            // Phiên mới: xoá trạng thái lỗi còn sót (wireless không nhận dòng ERROR: như serial).
            try { _serial.ResetErrorStateForNewSession(); } catch { }

            AppendLog($"Handshake: OK! PC has control; Web UI locked (monitoring only).");
            Logger.Info($"[MLAstro][WS] Connected and handshaked on {uri.Host}:{port}{path}");
            return true;
        }

        private static async Task<IPAddress?> ResolveMdnsAsync(string host, CancellationToken token)
        {
            try
            {
                var task = Dns.GetHostAddressesAsync(host, token);
                var addresses = await task.ConfigureAwait(false);
                return addresses?.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                       ?? addresses?.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][WS] Resolve '{host}' failed: {ex.Message}");
                return null;
            }
        }

        private string BuildHandshake()
        {
            var payload = new
            {
                cmd = "handshake",
                data = new
                {
                    key = HandshakeKey,
                    client = "NINA-MLAstroRPA+TPPA"
                }
            };
            return JsonSerializer.Serialize(payload);
        }

        public void Disconnect()
        {
            try
            {
                if (IsConnected)
                {
                    // Nhả quyền êm để Web UI mở khóa ngay (không cần F5)
                    try { SendJsonAsync("{\"cmd\":\"releaseControl\"}", CancellationToken.None).GetAwaiter().GetResult(); } catch { }
                    AppendLog("Release control sent. Disconnecting...");
                }
            }
            catch { }

            AbortSocket();
            IsConnected = false;
            HandshakeStatus = string.Empty;
            ConnectionStatus = "Disconnected";
            AppendLog("Disconnected (wireless).");
        }

        private void AbortSocket()
        {
            _activeJogAxis = null;
            _activeJogDirection = 0;
            _alignInFlight = false;
            _sawBusySinceAlign = false;

            try { _cts?.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
            try { _socket?.Dispose(); } catch { }
            _socket = null;
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _receiveLoop = null;
            _handshakeTcs = null;
            _configAckTcs = null;
            lock (_stateLock)
            {
                if (_externalControlActive)
                {
                    _externalControlActive = false;
                    foreach (var l in _externalControlListeners.ToList()) { try { l(false); } catch { } }
                }
            }
        }

        public async Task<bool> EnsureExternalConnectedAsync()
        {
            if (IsConnected) return true;
            return await ConnectAsync().ConfigureAwait(false);
        }

        public async Task<bool> BeginExternalControlAsync()
        {
            if (!await EnsureExternalConnectedAsync().ConfigureAwait(false)) return false;
            lock (_stateLock)
            {
                _externalControlActive = true;
                foreach (var l in _externalControlListeners.ToList()) { try { l(true); } catch { } }
            }
            return true;
        }

        public void EndExternalControl()
        {
            lock (_stateLock)
            {
                if (!_externalControlActive) return;
                _externalControlActive = false;
                foreach (var l in _externalControlListeners.ToList()) { try { l(false); } catch { } }
            }
        }

        public bool ResetEsp32()
        {
            if (!IsConnected) return false;
            Send("reboot");
            AppendLog("Reboot command sent (wireless).");
            return true;
        }

        public bool QueryTelemetry() => IsConnected; // firmware tự đẩy telemetry ~250 ms

        // ==================================================================
        // Nhận dữ liệu
        // ==================================================================
        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[ReceiveChunkSize];
            var message = new StringBuilder();

            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    var text = message.ToString();
                    message.Clear();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        HandleIncoming(text);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][WS] Receive loop ended: {ex.Message}");
            }
            finally
            {
                var wasConnected = IsConnected;
                IsConnected = false;
                HandshakeStatus = string.Empty;
                ConnectionStatus = "Disconnected";
                if (wasConnected)
                {
                    AppendLog("Connection lost (wireless).");
                    // Không còn điều khiển được thiết bị → TPPA phải dừng PA đang chạy.
                    try { StopRequested?.Invoke("Wireless connection closed."); } catch { }
                }
            }
        }

        private void HandleIncoming(string json)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][WS] JSON parse failed: {ex.Message}");
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;

                // Log/alert do firmware đẩy lên
                if (TryGetString(root, "log", out var logMsg))
                {
                    AddSystemLog(logMsg); // nội dung chính của bảng System log (giống Web UI)
                }
                if (TryGetString(root, "alert", out var alert))
                {
                    // Web UI hiện alert bằng modal chứ không đưa vào System log → ở plugin hiện bằng toast NINA.
                    AppendLog($"ALERT: {alert}");
                    try { Notification.ShowWarning($"MLAstro RPA: {alert}"); } catch { }
                }

                // 1) Handshake result
                if (TryGetString(root, "cmd", out var cmd))
                {
                    if (cmd == "handshakeResult")
                    {
                        var ok = TryGetBool(root, "result", out var r) && r;
                        if (!ok && TryGetString(root, "reason", out var reason))
                        {
                            HandshakeStatus = reason;
                        }
                        if (ok && TryGetString(root, "fw_ver", out var fwVer))
                        {
                            _serial.SetWirelessFirmwareVersion(fwVer);
                        }
                        _handshakeTcs?.TrySetResult(ok);
                        return;
                    }

                    if (cmd == "connectionRejected")
                    {
                        var reason = TryGetString(root, "reason", out var r2) ? r2 : "Connection rejected by device.";
                        HandshakeStatus = reason;
                        ConnectionStatus = reason;
                        AppendLog($"ERROR: {reason}");
                        _handshakeTcs?.TrySetResult(false);
                        return;
                    }

                    if (cmd == "controlReleased" || cmd == "controlTakenBySerial")
                    {
                        // Web UI làm đúng như vậy: appendLog(data.reason) — KHÔNG thêm tiền tố.
                        var reason = TryGetString(root, "reason", out var r3) ? r3 : cmd;
                        AddSystemLog(reason);
                        return;
                    }

                    if (cmd == "releaseControlResult")
                    {
                        return;
                    }
                }

                // 2) Ack của config
                if (TryGetString(root, "status", out var status))
                {
                    if (status == "configSaved" || status == "configApplied")
                    {
                        _configAckTcs?.TrySetResult(status);
                        return;
                    }
                }

                // 3) Init snapshot (chỉ có ở lần gửi đầu) → cache các section cấu hình
                if (TryGetInt(root, "speedLevel", out _) || TryGetString(root, "fw_ver", out _))
                {
                    CacheSnapshotSections(root);
                    ApplyWifiCredentialsFromSnapshot(root);
                    if (TryGetString(root, "fw_ver", out var fw))
                    {
                        _serial.SetWirelessFirmwareVersion(fw);
                        AppendLog($"Firmware: {fw}");
                    }
                    if (TryGetBool(root, "serial_locked", out var locked) && locked && !IsConnected)
                    {
                        AppendLog("NOTE: device reports serial control active.");
                    }
                    return;
                }

                // 4) Telemetry định kỳ: KHÔNG ghi vào System log (250 ms/lần sẽ làm ngập log),
                //    chỉ chuyển thành telemetry text cho pipeline UI + driver TPPA.
                //    Chỉ coi là telemetry khi có trường vị trí (các frame chỉ có sys_status như
                //    STOPPED/REBOOTING không được phép ghi đè vị trí hiển thị).
                if (root.TryGetProperty("pos_az", out _))
                {
                    var line = BuildSerialTelemetryLine(root);
                    if (!string.IsNullOrEmpty(line))
                    {
                        // Cho adapter ISerialLink của TPPA (ReadLine/ReadExisting)
                        try { LineReceived?.Invoke(line); } catch { }

                        // Cho pipeline UI dùng chung với serial (TelemetryParser + dock)
                        _serial.InjectIncomingText(line + "\n");

                        // Chuyển tiếp sự kiện hoàn tất align cho controller UI + driver TPPA.
                        // Chỉ coi là xong khi: firmware báo ALIGN_COMPLETED, HOẶC READY mà trước đó
                        // đã thấy trạng thái đang chạy (tránh READY thoáng qua ngay sau khi gửi lệnh),
                        // HOẶC READY sau 1.5 s (các bước dịch rất nhỏ không có pha ALIGNING).
                        if (TryGetString(root, "sys_status", out var sysStatus))
                        {
                            var busy = sysStatus == "ALIGNING" || sysStatus == "MOVING" || sysStatus == "HOMING" || sysStatus == "CALIBRATING";
                            if (_alignInFlight && busy)
                            {
                                _sawBusySinceAlign = true;
                            }

                            var completed = sysStatus == "ALIGN_COMPLETED"
                                || (sysStatus == "READY" && _alignInFlight
                                    && (_sawBusySinceAlign || (DateTime.UtcNow - _lastAlignSentUtc) > TimeSpan.FromSeconds(1.5)));

                            if (completed)
                            {
                                _alignInFlight = false;
                                _sawBusySinceAlign = false;
                                _serial.InjectIncomingText("AAll:COMPLETED\n");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Snapshot chứa SSID/password của AP và STA → đồng bộ về settings (như đường serial).</summary>
        private void ApplyWifiCredentialsFromSnapshot(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("wifi_ap", out var ap) && ap.ValueKind == JsonValueKind.Object
                    && TryGetString(ap, "pass", out var apPass)
                    && !string.IsNullOrWhiteSpace(apPass) && apPass != "***")
                {
                    _settings.ApPass = apPass;
                }

                if (TryGetString(root, "pass", out var staPass)
                    && !string.IsNullOrWhiteSpace(staPass) && staPass != "***")
                {
                    _settings.WifiPass = staPass;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][WS] WiFi credential sync failed: {ex.Message}");
            }
        }

        private void CacheSnapshotSections(JsonElement root)
        {
            if (TryGetInt(root, "speedLevel", out var speedLevel))
            {
                _speedLevelFromSnapshot = speedLevel;
            }

            foreach (var name in new[] { "limits", "motor", "backlash", "wifi_ap" })
            {
                if (!root.TryGetProperty(name, out var section) || section.ValueKind != JsonValueKind.Object) continue;
                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in section.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        _ => string.Empty
                    };
                }
                _snapshotSections[name] = dict;
            }
        }

        /// <summary>Tổng hợp telemetry JSON thành đúng định dạng text của firmware serial.</summary>
        private string BuildSerialTelemetryLine(JsonElement root)
        {
            var status = TryGetString(root, "sys_status", out var s) ? s : "READY";
            var movedAz = TryGetDouble(root, "align_moved_az", out var az) ? az : 0;
            var movedAlt = TryGetDouble(root, "align_moved_alt", out var alt) ? alt : 0;

            var tokens = new List<string>();
            AddToken(tokens, "SLvl", _speedLevelFromSnapshot.ToString(CultureInfo.InvariantCulture));
            AddToken(tokens, "WSta", TryGetDouble(root, "rssi", out var rssi) && rssi > -1000 ? "1" : "0");
            AddToken(tokens, "Home", TryGetBool(root, "homed", out var homed) && homed ? "1" : "0");

            if (TryGetDouble(root, "pos_az", out var posAz))
                AddToken(tokens, "AzPH", posAz.ToString("0.#####", CultureInfo.InvariantCulture));
            if (TryGetDouble(root, "pos_alt", out var posAlt))
                AddToken(tokens, "AlPH", posAlt.ToString("0.#####", CultureInfo.InvariantCulture));

            AppendSnapshotTokens(tokens);

            return $"<{status}|Mpos:{movedAz.ToString("0.#####", CultureInfo.InvariantCulture)},{movedAlt.ToString("0.#####", CultureInfo.InvariantCulture)}|>{string.Join(",", tokens)}";
        }

        private void AppendSnapshotTokens(List<string> tokens)
        {
            if (_snapshotSections.TryGetValue("limits", out var limits))
            {
                AddToken(tokens, "AzL1", Get(limits, "az_min"));
                AddToken(tokens, "AzL2", Get(limits, "az_max"));
                AddToken(tokens, "AlL1", Get(limits, "alt_min"));
                AddToken(tokens, "AlL2", Get(limits, "alt_max"));
            }

            if (_snapshotSections.TryGetValue("motor", out var motor))
            {
                AddToken(tokens, "AzRD", BoolToken(Get(motor, "az_reverse")));
                AddToken(tokens, "AzIR", Get(motor, "az_run_ma"));
                AddToken(tokens, "AzIH", Get(motor, "az_hold_ma"));
                AddToken(tokens, "AzSB", Get(motor, "az_boost_pct"));
                AddToken(tokens, "AzSC", Get(motor, "az_soft_cs_pct"));
                AddToken(tokens, "AzMS", Get(motor, "az_microsteps"));
                AddToken(tokens, "AzAc", Get(motor, "az_accel"));
                AddToken(tokens, "AzDec", Get(motor, "az_decel"));
                AddToken(tokens, "AzSD", Get(motor, "az_spd"));
                AddToken(tokens, "AzRM", BoolToken(Get(motor, "az_spread_cycle")));
                AddToken(tokens, "AlRD", BoolToken(Get(motor, "alt_reverse")));
                AddToken(tokens, "AlIR", Get(motor, "alt_run_ma"));
                AddToken(tokens, "AlIH", Get(motor, "alt_hold_ma"));
                AddToken(tokens, "AlSB", Get(motor, "alt_boost_pct"));
                AddToken(tokens, "AlSC", Get(motor, "alt_soft_cs_pct"));
                AddToken(tokens, "AlMS", Get(motor, "alt_microsteps"));
                AddToken(tokens, "AlAc", Get(motor, "alt_accel"));
                AddToken(tokens, "AlDe", Get(motor, "alt_decel"));
                AddToken(tokens, "AlSD", Get(motor, "alt_spd"));
                AddToken(tokens, "AlRM", BoolToken(Get(motor, "alt_spread_cycle")));
            }

            if (_snapshotSections.TryGetValue("backlash", out var backlash))
            {
                AddToken(tokens, "Back", BoolToken(Get(backlash, "enable")));
                AddToken(tokens, "AzBl", Get(backlash, "az_steps"));
                AddToken(tokens, "AlBl", Get(backlash, "alt_steps"));
                AddToken(tokens, "Over", BoolToken(Get(backlash, "overshoot")));
                AddToken(tokens, "OvUp", BoolToken(Get(backlash, "overshoot_up")));
                AddToken(tokens, "OvDn", BoolToken(Get(backlash, "overshoot_down")));
                AddToken(tokens, "OvD", Get(backlash, "overshoot_d"));
                AddToken(tokens, "OvM", Get(backlash, "overshoot_m"));
                AddToken(tokens, "OvS", Get(backlash, "overshoot_s"));
            }

            if (_snapshotSections.TryGetValue("wifi_ap", out var ap))
            {
                AddToken(tokens, "APss", Get(ap, "ssid"));
                AddToken(tokens, "APip", Get(ap, "ip"));
                AddToken(tokens, "APsu", Get(ap, "subnet"));
            }
        }

        private static string BoolToken(object? value) => value switch
        {
            bool b => b ? "1" : "0",
            long l => l != 0 ? "1" : "0",
            string s => (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) ? "1" : "0",
            _ => "0"
        };

        private static string Get(Dictionary<string, object> d, string key)
            => d.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

        private static void AddToken(List<string> tokens, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            tokens.Add($"{key}:{value}");
        }

        // ==================================================================
        // Gửi lệnh (text protocol của firmware -> JSON WebSocket API)
        // ==================================================================
        public bool Send(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (!IsConnected)
            {
                AppendLog($"ERROR: not connected - dropped: {line.Trim()}");
                return false;
            }

            try
            {
                var tokens = ParseCommandLine(line);
                if (tokens.Count == 0) return false;

                var outgoing = Translate(tokens);
                if (outgoing.Count == 0)
                {
                    // Lệnh không cần gửi (vd "?" vì telemetry do firmware đẩy)
                    return true;
                }

                foreach (var json in outgoing)
                {
                    SendJsonAsync(json, _cts?.Token ?? CancellationToken.None).GetAwaiter().GetResult();
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro][WS] Send failed: {ex.Message}");
                AppendLog($"ERROR: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SendCommandAndAwaitOkAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!IsConnected) return false;

            var tokens = ParseCommandLine(text);
            if (tokens.Count == 0) return false;

            var isConfig = tokens.Keys.Any(k => ConfigKeyMap.ContainsKey(k));
            var hasSaveAndReboot = tokens.Keys.Any(k => k.Equals("Save&Reboot", StringComparison.OrdinalIgnoreCase));

            if (!isConfig)
            {
                foreach (var json in Translate(tokens))
                {
                    await SendJsonAsync(json, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                }
                return true;
            }

            var payload = BuildConfigPayload(tokens);
            if (payload.Count == 0)
            {
                AppendLog("NOTE: no WS-supported config keys in payload (WiFi/AP settings require Serial).");
                return false;
            }

            var command = hasSaveAndReboot ? "saveConfig" : "applyConfig";
            var jsonBody = JsonSerializer.Serialize(new { cmd = command, data = payload });

            _configAckTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendJsonAsync(jsonBody, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

            var ackTask = _configAckTcs.Task;
            var completed = await Task.WhenAny(ackTask, Task.Delay(ConfigAckTimeout)).ConfigureAwait(false);
            if (completed != ackTask)
            {
                AppendLog($"WARNING: no {command} confirmation within {ConfigAckTimeout.TotalSeconds:0}s.");
                return false;
            }

            AppendLog($"Device confirmed: {ackTask.Result}");

            if (hasSaveAndReboot)
            {
                // Firmware không tự reboot khi lưu config qua WS → gửi reboot như giao thức serial.
                await SendJsonAsync("{\"cmd\":\"reboot\"}", CancellationToken.None).ConfigureAwait(false);
                AppendLog("Reboot command sent after save.");
            }
            return true;
        }

        private static readonly Dictionary<string, (string Section, string Field, bool IsBool)> ConfigKeyMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AzL1"] = ("limits", "az_min", false),
            ["AzL2"] = ("limits", "az_max", false),
            ["AlL1"] = ("limits", "alt_min", false),
            ["AlL2"] = ("limits", "alt_max", false),
            ["AzRD"] = ("motor", "az_reverse", true),
            ["AzIR"] = ("motor", "az_run_ma", false),
            ["AzIH"] = ("motor", "az_hold_ma", false),
            ["AzSB"] = ("motor", "az_boost_pct", false),
            ["AzSC"] = ("motor", "az_soft_cs_pct", false),
            ["AzMS"] = ("motor", "az_microsteps", false),
            ["AzAc"] = ("motor", "az_accel", false),
            ["AzDec"] = ("motor", "az_decel", false),
            ["AzSD"] = ("motor", "az_spd", false),
            ["AzRM"] = ("motor", "az_spread_cycle", true),
            ["AlRD"] = ("motor", "alt_reverse", true),
            ["AlIR"] = ("motor", "alt_run_ma", false),
            ["AlIH"] = ("motor", "alt_hold_ma", false),
            ["AlSB"] = ("motor", "alt_boost_pct", false),
            ["AlSC"] = ("motor", "alt_soft_cs_pct", false),
            ["AlMS"] = ("motor", "alt_microsteps", false),
            ["AlAc"] = ("motor", "alt_accel", false),
            ["AlDe"] = ("motor", "alt_decel", false),
            ["AlSD"] = ("motor", "alt_spd", false),
            ["AlRM"] = ("motor", "alt_spread_cycle", true),
            ["Back"] = ("backlash", "enable", true),
            ["AzBl"] = ("backlash", "az_steps", false),
            ["AlBl"] = ("backlash", "alt_steps", false),
            ["Over"] = ("backlash", "overshoot", true),
            ["OvUp"] = ("backlash", "overshoot_up", true),
            ["OvDn"] = ("backlash", "overshoot_down", true),
            ["OvD"] = ("backlash", "overshoot_d", false),
            ["OvM"] = ("backlash", "overshoot_m", false),
            ["OvS"] = ("backlash", "overshoot_s", false),
        };

        private Dictionary<string, object> BuildConfigPayload(Dictionary<string, string> tokens)
        {
            var sections = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in tokens)
            {
                if (!ConfigKeyMap.TryGetValue(kv.Key, out var map)) continue;
                if (!sections.TryGetValue(map.Section, out var section))
                {
                    section = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    sections[map.Section] = section;
                }
                section[map.Field] = map.IsBool ? (object)(kv.Value == "1") : ParseNumber(kv.Value);
            }

            return sections.ToDictionary(
                s => s.Key,
                s => (object)s.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        private static object ParseNumber(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return value;
        }

        private static Dictionary<string, string> ParseCommandLine(string line)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in line.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;
                var idx = token.IndexOf(':');
                if (idx <= 0) continue;
                result[token.Substring(0, idx).Trim()] = token.Substring(idx + 1).Trim();
            }
            return result;
        }

        private List<string> Translate(Dictionary<string, string> tokens)
        {
            var outgoing = new List<string>();
            if (tokens.Count == 0) return outgoing;

            // Telemetry query: firmware tự đẩy, không cần gửi
            if (tokens.Count == 1 && tokens.ContainsKey("?")) return outgoing;

            // Handshake text của serial: transport WS đã handshake khi kết nối
            if (tokens.ContainsKey("[MLAstroRPA-TC]")) return outgoing;

            if (tokens.TryGetValue("Disconnect", out _))
            {
                outgoing.Add("{\"cmd\":\"releaseControl\"}");
                return outgoing;
            }

            if (tokens.ContainsKey("Save&Reboot"))
            {
                return outgoing; // xử lý ở SendCommandAndAwaitOkAsync
            }

            // STOP:0 / ESTOP:0 = sự kiện nhả nút → firmware serial bỏ qua, WS cũng vậy.
            if (tokens.TryGetValue("ESTOP", out var estopState) && estopState == "0") return outgoing;
            if (tokens.TryGetValue("STOP", out var stopState) && stopState == "0") return outgoing;

            if (tokens.ContainsKey("ESTOP"))
            {
                _activeJogAxis = null;
                _activeJogDirection = 0;
                outgoing.Add("{\"cmd\":\"forceStop\",\"data\":{}}");
                return outgoing;
            }

            if (tokens.ContainsKey("STOP"))
            {
                _activeJogAxis = null;
                _activeJogDirection = 0;
                outgoing.Add("{\"cmd\":\"stop\",\"data\":{}}");
                return outgoing;
            }

            if (tokens.ContainsKey("ReER"))
            {
                outgoing.Add("{\"cmd\":\"resetError\",\"data\":{}}");
                return outgoing;
            }

            if (tokens.ContainsKey("SetH")) { outgoing.Add("{\"cmd\":\"setHome\",\"data\":{}}"); return outgoing; }
            if (tokens.ContainsKey("RetH")) { outgoing.Add("{\"cmd\":\"returnHome\",\"data\":{}}"); return outgoing; }
            if (tokens.ContainsKey("RstH")) { outgoing.Add("{\"cmd\":\"resetHome\",\"data\":{}}"); return outgoing; }
            if (tokens.TryGetValue("SLvl", out var level))
            {
                if (int.TryParse(level, out var parsedLevel) && parsedLevel > 0)
                {
                    _speedLevelFromSnapshot = parsedLevel;
                }
                outgoing.Add($"{{\"cmd\":\"speedLevel\",\"data\":{{\"level\":{ParseNumber(level)}}}}}");
                return outgoing;
            }

            // ---- Chế độ / tham số dịch chuyển tương đối (chỉ có ở giao thức serial) ----
            if (tokens.TryGetValue("JoRe", out var joRe))
            {
                _relativeMode = joRe != "0";
                return outgoing; // WebSocket dùng move/moveRelative riêng, không cần lệnh mode
            }
            if (tokens.TryGetValue("ReDe", out var reDe))
            {
                _relativeDegrees = ParseIntOrZero(reDe);
                return outgoing;
            }
            if (tokens.TryGetValue("ReAM", out var reAm))
            {
                _relativeMinutes = ParseIntOrZero(reAm);
                return outgoing;
            }
            if (tokens.TryGetValue("ReAS", out var reAs))
            {
                _relativeSeconds = ParseIntOrZero(reAs);
                return outgoing;
            }

            // ---- Nút mũi tên: jog liên tục (move) hoặc dịch một góc (moveRelative) ----
            if (tokens.TryGetValue("MAzL", out var azL)) return HandleArrow("az", -1, azL);
            if (tokens.TryGetValue("MAzR", out var azR)) return HandleArrow("az", 1, azR);
            if (tokens.TryGetValue("MAlU", out var alU)) return HandleArrow("alt", 1, alU);
            if (tokens.TryGetValue("MAlD", out var alD)) return HandleArrow("alt", -1, alD);

            // ---- Truy vấn password WiFi: WebSocket không có lệnh query → trả lời từ snapshot ----
            if (tokens.TryGetValue("APpa", out var apPassQuery) && apPassQuery == "?")
            {
                InjectDeviceLine($"APpa:{_settings.ApPass}");
                return outgoing;
            }
            if (tokens.TryGetValue("STAp", out var staPassQuery) && staPassQuery == "?")
            {
                InjectDeviceLine($"STAp:{_settings.WifiPass}");
                return outgoing;
            }

            // ALIGN (AzED/AzEM/AzES/AzDi + AlED/... + AAll:1|AzAN:1|AlAN:1)
            var isAlign = tokens.ContainsKey("AAll") || tokens.ContainsKey("AzAN") || tokens.ContainsKey("AlAN")
                          || tokens.ContainsKey("AzED") || tokens.ContainsKey("AlED");
            if (isAlign)
            {
                var azArcSec = ToArcSeconds(tokens, "Az");
                var altArcSec = ToArcSeconds(tokens, "Al");
                var simultaneous = tokens.ContainsKey("AAll") || (azArcSec != 0 && altArcSec != 0);
                var json = $"{{\"cmd\":\"align\",\"data\":{{\"ra_error\":{azArcSec.ToString("0.###", CultureInfo.InvariantCulture)}," +
                           $"\"dec_error\":{altArcSec.ToString("0.###", CultureInfo.InvariantCulture)}," +
                           $"\"simultaneous\":{(simultaneous ? "true" : "false")}}}}}";
                outgoing.Add(json);
                _lastAlignSentUtc = DateTime.UtcNow;
                _alignInFlight = true;
                _sawBusySinceAlign = false;
                return outgoing;
            }

            // Cấu hình: gửi thẳng applyConfig (không chờ ack ở đường Send đồng bộ)
            if (tokens.Keys.Any(k => ConfigKeyMap.ContainsKey(k)))
            {
                var payload = BuildConfigPayload(tokens);
                if (payload.Count > 0)
                {
                    outgoing.Add(JsonSerializer.Serialize(new { cmd = "applyConfig", data = payload }));
                }
                return outgoing;
            }

            AppendLog($"WARNING: command not supported over Wireless: {string.Join(",", tokens.Keys)}");
            return outgoing;
        }

        /// <summary>
        /// Dịch một lần nhấn/nhả nút mũi tên của dock thành lệnh WebSocket.
        /// - Chế độ Jog: nhấn = `move` (liên tục, KHÔNG gửi lặp vì firmware từ chối khi đang chạy),
        ///   nhả = `stop`.
        /// - Chế độ Relative: nhấn = `moveRelative` (một góc), nhả = không làm gì (khớp firmware serial).
        /// </summary>
        private List<string> HandleArrow(string axis, int direction, string state)
        {
            var outgoing = new List<string>();

            if (state == "0")
            {
                var wasActive = _activeJogAxis != null;
                _activeJogAxis = null;
                _activeJogDirection = 0;
                if (!_relativeMode && wasActive)
                {
                    outgoing.Add("{\"cmd\":\"stop\",\"data\":{}}");
                }
                return outgoing;
            }

            if (_relativeMode)
            {
                var angle = _relativeDegrees + (_relativeMinutes / 60.0) + (_relativeSeconds / 3600.0);
                if (angle < 0) angle = 0;
                outgoing.Add($"{{\"cmd\":\"moveRelative\",\"data\":{{\"axis\":\"{axis}\",\"direction\":{direction}," +
                             $"\"angle\":{angle.ToString("0.####", CultureInfo.InvariantCulture)},\"speed\":{_speedLevelFromSnapshot}}}}}");
                return outgoing;
            }

            // Jog liên tục: bỏ qua các lần gửi lặp của watchdog (250 ms) cho cùng hướng.
            if (_activeJogAxis == axis && _activeJogDirection == direction)
            {
                return outgoing;
            }

            _activeJogAxis = axis;
            _activeJogDirection = direction;
            outgoing.Add($"{{\"cmd\":\"move\",\"data\":{{\"axis\":\"{axis}\",\"direction\":{direction},\"speed\":{_speedLevelFromSnapshot}}}}}");
            return outgoing;
        }

        private static int ParseIntOrZero(string value)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        /// <summary>Đưa một dòng trả lời "như từ thiết bị" vào pipeline (và cho adapter ISerialLink).</summary>
        private void InjectDeviceLine(string line)
        {
            try { LineReceived?.Invoke(line); } catch { }
            _serial.InjectIncomingText(line + "\n");
        }

        private static double ToArcSeconds(Dictionary<string, string> tokens, string prefix)        {
            var deg = GetDouble(tokens, prefix + "ED");
            var min = GetDouble(tokens, prefix + "EM");
            var sec = GetDouble(tokens, prefix + "ES");
            var magnitude = (deg * 3600.0) + (min * 60.0) + sec;
            var dirPositive = true;
            if (tokens.TryGetValue(prefix + "Di", out var dir))
            {
                dirPositive = dir != "0";
            }
            return dirPositive ? magnitude : -magnitude;
        }

        private static double GetDouble(Dictionary<string, string> tokens, string key)
        {
            if (!tokens.TryGetValue(key, out var value)) return 0;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        private async Task SendJsonAsync(string json, CancellationToken token)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket is not open.");
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ==================================================================
        // SYSTEM LOG (giống bảng System Log của Web UI)
        // Chỉ ghi thông báo của firmware/plugin: timestamp + tô màu theo từ khóa,
        // dòng mới nhất lên trên, tối đa 50 dòng. KHÔNG ghi frame TX/RX thô.
        // ==================================================================
        private const int SystemLogMaxEntries = 50;

        public ObservableCollection<SystemLogEntry> SystemLog { get; } = new();

        /// <summary>
        /// Ghi chẩn đoán của PLUGIN vào NINA log — KHÔNG đưa vào bảng System log.
        /// Bảng System log chỉ chứa thông báo do THIẾT BỊ gửi (giống hệt bảng System Log của Web UI);
        /// nếu thêm chữ của plugin vào đây thì nội dung sẽ khác web.
        /// </summary>
        private void AppendLog(string text)
        {
            try
            {
                Logger.Info($"[MLAstro][WS] {text}");
            }
            catch { }
        }

        /// <summary>Thêm một dòng vào System log (đã lọc nhiễu + tô màu như Web UI).</summary>
        public void AddSystemLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            // Web UI bỏ dòng này vì nó xuất hiện quá nhiều và không hữu ích.
            if (message.Contains("Manual stop sequence completed. Hardlimit re-enabled."))
            {
                return;
            }

            var entry = new SystemLogEntry(message, ClassifyLogLevel(message), DateTime.Now);

            void Add()
            {
                SystemLog.Insert(0, entry); // mới nhất lên trên
                while (SystemLog.Count > SystemLogMaxEntries)
                {
                    SystemLog.RemoveAt(SystemLog.Count - 1);
                }
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(Add));
            }
            else
            {
                Add();
            }
        }

        public void ClearSystemLog()
        {
            void Clear() => SystemLog.Clear();

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(Clear));
            }
            else
            {
                Clear();
            }
        }

        /// <summary>Nội dung CSV của System log (cũ nhất trước) để Export CSV.</summary>
        public string BuildSystemLogCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,Level,Message");
            foreach (var entry in SystemLog.Reverse())
            {
                var message = entry.Message.Replace("\"", "\"\"");
                sb.AppendLine($"{entry.Time},{entry.Level},\"{message}\"");
            }
            return sb.ToString();
        }

        /// <summary>Phân loại màu đúng theo quy tắc của Web UI (appendLog).</summary>
        private static SystemLogLevel ClassifyLogLevel(string message)
        {
            if (message.Contains("[SAVE&REBOOT required]")) return SystemLogLevel.RebootRequired;
            if (message.Contains("[Apply]")) return SystemLogLevel.Apply;
            if (message.Contains("Reset by User")) return SystemLogLevel.Reset;

            if (message.Contains("CRITICAL") || message.Contains("ERROR") || message.Contains("Error") || message.Contains("failed")
                || message.Contains("Short to Ground") || message.Contains("Over Temperature") || message.Contains("Hardlimit reached"))
                return SystemLogLevel.Critical;

            if (message.Contains("WARNING") || message.Contains("limit") || message.Contains("Limit") || message.Contains("Hit")
                || message.Contains("Pre-Warn") || message.Contains("ALIGN ERROR"))
                return SystemLogLevel.Warning;

            if (message.Contains("COMPLETED") || message.Contains("Success") || message.Contains("saved"))
                return SystemLogLevel.Success;

            return SystemLogLevel.Info;
        }

        private static bool TryGetString(JsonElement root, string name, out string value)
        {
            value = string.Empty;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop)) return false;
            if (prop.ValueKind != JsonValueKind.String) return false;
            value = prop.GetString() ?? string.Empty;
            return true;
        }

        private static bool TryGetBool(JsonElement root, string name, out bool value)
        {
            value = false;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop)) return false;
            if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
            return false;
        }

        private static bool TryGetInt(JsonElement root, string name, out int value)
        {
            value = 0;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop)) return false;
            if (prop.ValueKind != JsonValueKind.Number) return false;
            return prop.TryGetInt32(out value);
        }

        private static bool TryGetDouble(JsonElement root, string name, out double value)
        {
            value = 0;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop)) return false;
            if (prop.ValueKind == JsonValueKind.Number) return prop.TryGetDouble(out value);
            if (prop.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }
            return false;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null!)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public void Dispose()
        {
            try { Disconnect(); } catch { }
            try
            {
                if (ReferenceEquals(_serial.WirelessProxy, this))
                {
                    _serial.WirelessProxy = null;
                }
            }
            catch { }
            if (Instance == this) Instance = null;
        }
    }
}
