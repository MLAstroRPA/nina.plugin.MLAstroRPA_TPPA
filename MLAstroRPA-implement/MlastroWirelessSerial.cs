using MLAstro_Robotic_Polar_Alignment.Services;
using NINA.Core.Utility;
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
            service.LineReceived += OnLineReceived;
            service.StopRequested += OnServiceStopRequested;
            if (!service.EnsureExternalConnectedAsync().GetAwaiter().GetResult())
            {
                service.LineReceived -= OnLineReceived;
                service.StopRequested -= OnServiceStopRequested;
                throw new Exception($"Unable to connect to MLAstroRPA over wireless ({service.ConnectionStatus}).");
            }

            open = true;
            Logger.Info("[MLAstroRPA] Wireless (WebSocket) transport opened for TPPA.");
        }

        private void OnServiceStopRequested(string reason)
        {
            try { StopRequested?.Invoke(reason); } catch { }
        }

        public void Close()
        {
            service.LineReceived -= OnLineReceived;
            service.StopRequested -= OnServiceStopRequested;
            open = false;
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
