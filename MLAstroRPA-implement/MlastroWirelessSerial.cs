using MLAstro_Robotic_Polar_Alignment.Services;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace NINA.Plugins.PolarAlignment
{
    /// <summary>
    /// <see cref="ISerialLink"/> cho transport WIRELESS (WebSocket).
    ///
    /// Driver TPPA (<c>UniversalPolarAlignmentMLAstroRPA</c>) làm việc với giao thức text
    /// của firmware serial; lớp này dịch sang WebSocket qua <see cref="MlastroWebSocketService"/>:
    ///  - WriteLine(text)  -> dịch lệnh text sang JSON và gửi qua WS.
    ///  - ReadLine/ReadExisting -> đọc các dòng status/ack do service tổng hợp từ telemetry JSON.
    ///
    /// Handshake "[MLAstroRPA-TC]" đã được service thực hiện ngay khi kết nối (qua JSON), nên
    /// lệnh này được trả lời giả lập để driver chạy nguyên luồng cũ.
    /// </summary>
    public sealed class MlastroWirelessSerial : ISerialLink
    {
        private readonly MlastroWebSocketService service;
        private readonly Queue<string> incoming = new();
        private readonly object sync = new();
        private bool open;
        // Chỉ hiện ĐÚNG 1 notification "ngắt do MLAstro" cho mỗi phiên (kênh State và kênh STOP có
        // thể báo cùng một sự kiện ngắt) — giống SharedMlastroSerial.
        private volatile bool disconnectNotified;

        /// <summary>Báo cho driver TPPA dừng khẩn cấp khi link wireless đứt (thiết bị cũng đã tự dừng motor).</summary>
        public event Action<string>? StopRequested;

        public MlastroWirelessSerial(MlastroWebSocketService service)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            NewLine = "\n";
            ReadTimeout = 300;
            WriteTimeout = 300;
        }

        public string NewLine { get; set; }
        public int ReadTimeout { get; set; }
        public int WriteTimeout { get; set; }
        public bool IsOpen => open && service.IsConnected;

        public int BytesToRead
        {
            get
            {
                lock (sync)
                {
                    var total = 0;
                    foreach (var line in incoming)
                    {
                        total += line.Length + 1;
                    }
                    return total;
                }
            }
        }

        public void Open()
        {
            disconnectNotified = false;
            service.LineReceived += OnLineReceived;
            service.StopRequested += OnServiceStopRequested;
            // Theo dõi MLAstro NGẮT phiên (bấm Disconnect bên plugin MLAstro, hoặc WS đứt) để báo cho
            // user: trước đây đường wireless ngắt âm thầm, chỉ đường Serial có thông báo.
            service.StateChanged += OnServiceStateChanged;

            // PHẢI LẤY QUYỀN điều khiển (không chỉ "mở kết nối"): trước đây chỉ gọi
            // EnsureExternalConnectedAsync ⇒ TPPA vào bằng Wireless mà plugin MLAstro vẫn MỞ KHOÁ
            // CONTROL/CONFIGURATION (Serial thì khoá bình thường) — bug user báo 2026-09-16.
            bool ok = false;
            try
            {
                // (1) Mở phiên WebSocket (nếu chưa) + đánh dấu đang điều khiển ở tầng WebSocket.
                ok = service.BeginExternalControlAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstroRPA] BeginExternalControl (wireless) failed: {ex.Message}");
            }

            if (ok)
            {
                // (2) Đồng bộ cờ external-control với SerialConnectionService (facade dùng chung):
                //     nó set _externalControlActive + bắn listener cho MLAstroController ⇒ UI MLAstro
                //     khoá CONTROL/CONFIGURATION ngay. Chỉ khi WS đã Connected thì nhánh này mới đi
                //     theo transport wireless (không đụng tới cổng COM).
                try
                {
                    var serialService = MLAstro_Robotic_Polar_Alignment.Services.SerialConnectionService.Instance;
                    if (serialService != null && !serialService.BeginExternalControlAsync().GetAwaiter().GetResult())
                    {
                        Logger.Warning("[MLAstroRPA] Wireless: MLAstro external-control flag was not set (its UI may stay unlocked).");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[MLAstroRPA] Wireless: could not sync external control with MLAstro service: {ex.Message}");
                }
            }

            if (!ok)
            {
                // Không giữ được quyền: nhả mọi cờ để MLAstro không bị kẹt khoá UI / dừng poll.
                try { service.EndExternalControl(); } catch { }
                service.LineReceived -= OnLineReceived;
                service.StopRequested -= OnServiceStopRequested;
                service.StateChanged -= OnServiceStateChanged;
                throw new Exception($"Unable to connect to MLAstroRPA over wireless ({service.ConnectionStatus}).");
            }

            open = true;
            Logger.Info("[MLAstroRPA] Wireless (WebSocket) transport opened for TPPA (external control active).");
        }

        /// <summary>
        /// Bên MLAstro nhấn STOP/FORCE-STOP giữa chừng - TPPA phải DỪNG PA ngay (giống đường Serial).
        /// </summary>
        private void OnServiceStopRequested(string reason)
        {
            Logger.Info($"[MLAstroRPA] STOP requested by MLAstro (wireless): {reason}");
            ShowExternalStopNotification(reason);
            // Dừng toàn bộ routine PA của TPPA (driver + executeCTS của Dockable).
            try { PolarAlignmentPlugin.RequestStopFromExternal(reason); }
            catch (Exception ex) { Logger.Error($"[MLAstroRPA] RequestStopFromExternal failed: {ex.Message}"); }
            try { StopRequested?.Invoke(reason); } catch { }
        }

        /// <summary>
        /// MLAstro đóng phiên wireless (bấm Disconnect bên plugin MLAstro / WS đứt): báo RÕ nguyên nhân
        /// cho user (trước đây hoàn toàn âm thầm) rồi dọn listener để lần Connect sau không lọt sự kiện cũ.
        /// </summary>
        private void OnServiceStateChanged(bool connected)
        {
            if (connected) { return; }

            var wasOpen = open;
            open = false;
            Logger.Info("[MLAstroRPA] Wireless session marked closed (MLAstro disconnected).");

            // 1 notification / phiên, tránh trùng với kênh STOP (cũng có thể báo "disconnect").
            if (wasOpen && !disconnectNotified)
            {
                disconnectNotified = true;
                try { Notification.ShowWarning("Disconnected by MLAstro plugin - TPPA session closed."); }
                catch (Exception ex) { Logger.Error($"[MLAstroRPA] Notification failed: {ex.Message}"); }
            }

            service.LineReceived -= OnLineReceived;
            service.StopRequested -= OnServiceStopRequested;
            service.StateChanged -= OnServiceStateChanged;
            lock (sync)
            {
                incoming.Clear();
            }
        }

        /// <summary>Notification nêu rõ NGUYÊN NHÂN dừng/ngắt đến từ plugin MLAstro (giống SharedMlastroSerial).</summary>
        private void ShowExternalStopNotification(string reason)
        {
            try
            {
                string message;
                if (reason?.IndexOf("FORCE-STOP", StringComparison.OrdinalIgnoreCase) >= 0
                    || reason?.IndexOf("E-STOP", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    message = "FORCE-STOP pressed on MLAstro plugin - TPPA PA stopped.";
                }
                else if (reason?.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    message = "STOP pressed on MLAstro plugin - TPPA PA stopped.";
                }
                else if (reason?.IndexOf("disconnect", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Kênh State(false) sẽ hiện notification (1 lần) - tránh 2 toast cho cùng sự kiện.
                    message = "Disconnected by MLAstro plugin - TPPA session closed.";
                    disconnectNotified = true;
                }
                else
                {
                    message = $"Stopped by MLAstro plugin ({reason}).";
                }

                Notification.ShowWarning(message);
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstroRPA] Notification failed: {ex.Message}");
            }
        }

        public void Close()
        {
            service.LineReceived -= OnLineReceived;
            service.StopRequested -= OnServiceStopRequested;
            service.StateChanged -= OnServiceStateChanged;
            open = false;
            disconnectNotified = false;
            // Trả quyền điều khiển để MLAstro MỞ KHOÁ UI (giống SharedMlastroSerial khi TPPA ngắt):
            // cả cờ ở tầng WebSocket lẫn cờ của SerialConnectionService (nơi MLAstroController nghe).
            try { service.EndExternalControl(); } catch { }
            try { MLAstro_Robotic_Polar_Alignment.Services.SerialConnectionService.Instance?.EndExternalControl(); } catch { }
            lock (sync)
            {
                incoming.Clear();
            }
        }

        public void DiscardInBuffer()
        {
            lock (sync)
            {
                incoming.Clear();
            }
        }

        public void WriteLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // Handshake của driver: transport đã handshake bằng JSON khi kết nối.
            if (text.Contains("[MLAstroRPA-TC]"))
            {
                lock (sync)
                {
                    incoming.Enqueue("ok,wireless,SN:n/a");
                }
                return;
            }

            // Poll "?" : firmware tự đẩy telemetry định kỳ, không cần gửi gì.
            if (text.Trim() == "?")
            {
                return;
            }

            service.Send(text);
        }

        public string ReadLine()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(50, ReadTimeout));
            while (DateTime.UtcNow < deadline)
            {
                lock (sync)
                {
                    if (incoming.Count > 0)
                    {
                        return incoming.Dequeue();
                    }
                }

                if (!service.IsConnected)
                {
                    throw new TimeoutException("Wireless connection closed.");
                }

                Thread.Sleep(10);
            }

            throw new TimeoutException("No data received over wireless link within read timeout.");
        }

        public string ReadExisting()
        {
            lock (sync)
            {
                if (incoming.Count == 0)
                {
                    return string.Empty;
                }

                var sb = new StringBuilder();
                while (incoming.Count > 0)
                {
                    sb.Append(incoming.Dequeue()).Append('\n');
                }
                return sb.ToString();
            }
        }

        private void OnLineReceived(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (sync)
            {
                incoming.Enqueue(line.TrimEnd('\r', '\n'));
            }
        }

        public void Dispose() => Close();
    }
}
