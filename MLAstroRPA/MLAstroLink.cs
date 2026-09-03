using MLAstro_Robotic_Polar_Alignment.Services;
using NINA.Core.Utility;
using System;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment {
    /// <summary>
    /// Cầu nối tới SerialConnectionService của MLAstro (nay CÙNG assembly NINA.Plugins.PolarAlignment).
    /// MLAstro/SerialConnectionService là CHỦ cổng COM duy nhất; driver TPPA-MLAstroRPA ưu tiên dùng
    /// chung cổng qua "external control" giữ nguyên kiến trúc cũ — nhưng gọi trực tiếp, không reflection.
    /// Nếu service chưa được khởi tạo (Instance null) thì <see cref="TryCreate"/> trả null và driver
    /// tự mở cổng riêng (fallback quét COM như trước đây).
    /// </summary>
    public sealed class MLAstroLink : IDisposable {
        private readonly SerialConnectionService service;

        private MLAstroLink(SerialConnectionService service) {
            this.service = service;
        }

        /// <summary>Trả link tới SerialConnectionService nếu đã nạp; null nếu chưa (-> tự mở cổng).</summary>
        public static MLAstroLink TryCreate() {
            try {
                var instance = SerialConnectionService.Instance;
                return instance == null ? null : new MLAstroLink(instance);
            } catch {
                return null;
            }
        }

        public bool IsConnected {
            get {
                try { return service != null && service.IsConnected; } catch { return false; }
            }
        }

        public string ConfiguredComPort {
            get {
                try { return service?.ConfiguredComPort; } catch { return null; }
            }
        }

        /// <summary>Mở cổng qua SerialConnectionService (auto-open cho cả plugin nếu chưa mở).</summary>
        public Task<bool> ConnectAsync() {
            if (service == null) return Task.FromResult(false);
            try { return service.EnsureExternalConnectedAsync(); }
            catch (Exception ex) { Logger.Error($"[MLAstroLink] Connect failed: {ex.Message}"); return Task.FromResult(false); }
        }

        public void Disconnect() {
            try { service?.Disconnect(); }
            catch (Exception ex) { Logger.Error($"[MLAstroLink] Disconnect failed: {ex.Message}"); }
        }

        /// <summary>Ghi một lệnh đã có ký tự xuống dòng (vd "...\n").</summary>
        public bool Send(string line) {
            if (service == null) return false;
            try { return service.Send(line); }
            catch (Exception ex) { Logger.Error($"[MLAstroLink] Send failed: {ex.Message}"); return false; }
        }

        /// <summary>Tạm dừng poll "?" của MLAstro khi TPPA đang chủ động điều khiển.</summary>
        public void SetPauseQuery(bool pause) {
            try { service?.SetExternalPauseQuery(pause); }
            catch { }
        }

        public bool ExternalControlActive {
            get {
                try { return service != null && service.IsExternalControlActive; }
                catch { return false; }
            }
        }

        /// <summary>TPPA bắt đầu GIỮ quyền điều khiển (auto-open + khoá UI MLAstro).</summary>
        public Task<bool> BeginExternalControl() {
            if (service == null) return Task.FromResult(false);
            try { return service.BeginExternalControlAsync(); }
            catch (Exception ex) { Logger.Error($"[MLAstroLink] BeginExternalControl failed: {ex.Message}"); return Task.FromResult(false); }
        }

        /// <summary>TPPA THẢ quyền (KHÔNG đóng cổng) - MLAstro mở khoá UI & poll lại.</summary>
        public void EndExternalControl() {
            try { service?.EndExternalControl(); }
            catch (Exception ex) { Logger.Error($"[MLAstroLink] EndExternalControl failed: {ex.Message}"); }
        }

        public void SubscribeStop(Action<string> onStop) {
            try { if (onStop != null && service != null) service.AddExternalStopListener(onStop); } catch { }
        }

        public void UnsubscribeStop(Action<string> onStop) {
            try { if (onStop != null && service != null) service.RemoveExternalStopListener(onStop); } catch { }
        }

        public void Subscribe(Action<string> onLine, Action<bool> onState) {
            try { if (onLine != null && service != null) service.AddExternalLineListener(onLine); } catch { }
            try { if (onState != null && service != null) service.AddExternalStateListener(onState); } catch { }
        }

        public void Unsubscribe(Action<string> onLine, Action<bool> onState) {
            try { if (onLine != null && service != null) service.RemoveExternalLineListener(onLine); } catch { }
            try { if (onState != null && service != null) service.RemoveExternalStateListener(onState); } catch { }
        }

        public void Dispose() => Disconnect();
    }
}
