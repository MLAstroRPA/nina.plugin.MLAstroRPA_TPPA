# Changelog

> **Origin & license (TPPA).** The polar-alignment portion of this plugin is a fork of the
> original open-source **Three Point Polar Alignment (TPPA)** plugin for NINA by
> [Isbeorn](https://github.com/isbeorn/nina.plugin.polaralignment), which is licensed under the
> **Mozilla Public License 2.0 (MPL-2.0)**. The TPPA-derived code in this project therefore stays
> under MPL-2.0, keeping the original license/copyright notices. **MLAstroRPA+TPPA is a separate,
> unofficial build — it is NOT the original/official TPPA plugin.** When redistributing, comply
> with MPL-2.0: retain the license and notices, credit the original author, and make the source
> (including your modifications) available.

## 2.1.0.0

### MLAstroRPA — Wireless connection (WebSocket over mDNS)
- **New connection type selector** at the top of the CONNECTION tab: `Serial connection`
  (unchanged) or **`Wireless connection`**. Choosing Wireless hides every serial-only setting
  (COM port, data bits/parity/stop bits, refresh ports, pause-polling, handshake timeout, polling
  period, Hex/Send row) and shows the wireless panel instead.
- **Wireless panel** contains exactly: **Address** (`MLAstroRPA.local` by default, or the device IP
  as a fallback when mDNS does not resolve), **Connection** (Connect/Disconnect + status), and
  **System log** (same RichTextBox, now fed by the WebSocket traffic).
- **WebSocket transport** (`MlastroWebSocketService`) connects to `ws://<address>:80/ws`, performs the
  PC handshake `{"cmd":"handshake","data":{"key":"MLAstroRPA-TC"}}` and then behaves like the COM
  port: the serial text protocol is translated to WebSocket JSON (`AAll`/`AzAN`/`AlAN` → `align`,
  `STOP`/`ESTOP` → `stop`/`forceStop`, `ReER` → `resetError`, home commands, `SLvl` → `speedLevel`,
  configuration chain → `applyConfig`/`saveConfig` + `reboot`, `Disconnect` → `releaseControl`).
- Incoming JSON telemetry is converted back into the firmware's serial telemetry text and injected
  into the existing pipeline, so the CONTROL/CONFIGURATION tabs, the MLAstro dock and
  `TelemetryParser` keep working unchanged (no duplicated parsing logic).
- **TPPA automated adjustment works over Wireless**: `MlastroWirelessSerial` implements
  `ISerialLink`, so the polar-alignment driver (`UniversalPolarAlignmentMLAstroRPA`) uses the
  WebSocket session when `PluginSettings.TransportMode == Wireless` (sharing the single PC session
  with the plugin UI — the firmware only allows one PC client).
- Safety: switching connection type disconnects the current transport first (a single transport may
  hold control at a time), and losing the wireless link raises the same external-stop path so a
  running polar-alignment routine is aborted.
- Known limitation: AP SSID/IP/subnet and Station SSID/password are **not** transmitted over
  WebSocket (firmware has no such command) — change them while connected via Serial.

### MLAstroRPA — Wireless: System log & fake alarms fixed

- **Fake "Alarm History" warnings on connect (WSta / Home / AzRM / AlRM / Back):** the synthesized
  telemetry line fed into the shared pipeline was also parsed by the error-telemetry parser, which
  accepted *any* `Key:value` list — so telemetry DATA_SETTING keys whose value is `1`
  (`WSta:1` WiFi status, `Home:1` homed, `AzRM:1`/`AlRM:1` run mode, `Back:1` backlash) became
  ACTIVE WARNING rows. `ProcessErrorTelemetry` now only accepts lines starting with `ERROR:`
  (firmware code list: Sys/AzNC/AlNC/AzOT/AlOT/AzPW/AlPW/AzSA/AzSB/AlSA/AlSB/AzOL/AlOL/AzHL/AlHL/AzSL/AlSL/Esc),
  and the wireless session starts from a clean error state (`ResetErrorStateForNewSession`).
- **System log cleaned up:** TX/RX prefixes are no longer duplicated (`RX: RX: …` → `RX: …`), the
  250 ms telemetry stream is no longer written to the log (it still feeds the UI/driver), and
  connection lifecycle messages are shown as neutral status entries instead of fake RX lines.
- **CONTROL / CONFIGURATION tabs and the dock now report "connected" in Wireless mode**:
  `SerialConnectionService` acts as a facade — `IsConnected`, `ConnectionStatus`, `HandshakeStatus`,
  `FirmwareVersion`, `Send`, `SendCommandAndAwaitOkAsync`, `ResetEsp32`, `QueryTelemetry` and the
  external-control API all route to the WebSocket session while the wireless transport is active
  (the COM port stays closed). The firmware version is now taken from the WebSocket handshake /
  init snapshot, and AP/Station passwords are synced from that snapshot.
- Connect hints now say *(Serial or Wireless)*, and the dock hint points at the **CONNECTION** tab.

### MLAstroRPA — Wireless: jog / relative / password commands

- **Jog buttons now work over Wireless.** The dock emits serial-style commands (`MAzL:1`, `MAzR:1`,
  `MAlU:1`, `MAlD:1`, released with `:0`, resent every 250 ms by the jog watchdog) and the WebSocket
  bridge had no rule for them, so every press logged *"command not supported over Wireless"* and no
  motion happened. They are now translated to `move` (`axis`/`direction`/`speed`) and `stop`.
- Duplicate jog sends are **collapsed**: the firmware rejects any motion command while a motion is
  running (`rejectMotionStartIfBusy`), so the 250 ms watchdog repeats would only produce
  *"Motion is active…"* alerts. A repeated press of the same direction is now a no-op until the
  direction changes or the button is released.
- **Relative mode over Wireless**: `JoRe` / `ReDe` / `ReAM` / `ReAS` have no WebSocket equivalent, so
  they are remembered locally and an arrow press becomes a single `moveRelative` (axis + direction +
  angle + speed). Releasing the button sends nothing, matching the serial firmware behaviour.
- `APpa:?` / `STAp:?` (password queries) are answered from the WebSocket init snapshot instead of
  being logged as unsupported.
- `STOP:0` / `ESTOP:0` (button-release events) are ignored, as in the serial protocol.
- Telemetry frames that only carry `sys_status` (e.g. `STOPPED`, `REBOOTING`) are no longer treated
  as position telemetry, so they cannot reset the displayed Az/Alt position.
- Still Serial-only: **setting** `APpa` / `STAp` / `APss` / `APip` / `STAs` (the firmware WebSocket API
  has no command for them) — the log states this explicitly.

### MLAstroRPA — Wireless: System log panel now matches the Web UI

- The Wireless **System log** is no longer the raw serial terminal. It is now a web-style table:
  `[h:mm:ss tt] message`, colour-coded with the same keyword rules as the Web UI
  (`CRITICAL/ERROR/failed` → red bold, `WARNING/limit` → dark orange, `COMPLETED/saved` → green,
  `[Apply]`/`[SAVE&REBOOT required]`/`Reset by User` → orange/blue, default black), newest entry on
  top, capped at **50 lines**, and the Web UI's noisy *"Manual stop sequence completed. Hardlimit
  re-enabled."* line is filtered out.
- **No more TX/RX frame logging and no plugin chatter**: the panel now contains *only* what the
  device sends — the firmware `log` messages plus the `reason` of `controlTakenBySerial` /
  `controlReleased` — reproduced verbatim (no `NOTE:`/`ERROR:` prefixes, lowercase `am/pm` time),
  exactly like the Web UI's System Log. Plugin diagnostics (*Resolving…*, *Connected…*,
  *Handshake: OK!…*) go to the NINA log file instead, and firmware `alert` messages are surfaced as
  NINA toast notifications (the Web UI shows them as a modal, not in the log).
- The serial terminal (RichTextBox + Hex/Send) is unchanged and is now shown **only** for
  `Serial connection`; Wireless shows this web-style log instead.
- The log is rendered in a **RichTextBox**, so the text can be **selected and copied** (context menu
  → Copy), its **background follows the NINA theme** (no forced white box; plain info lines inherit
  the theme foreground instead of hard-coded black, which was unreadable on dark themes), and a drag
  **Thumb** below the box resizes its height exactly like the serial terminal.
- Timestamp format matches the Web UI (`[h:mm:ss tt]`, lowercase `am/pm`).
- Text colours are now **contrast-aware**: the palette is derived from the panel's effective
  background (theme-driven, resolved through the visual tree) — light body text plus bright
  red/amber/green/blue accents on dark themes, the Web UI's original red/darkorange/green/orange plus
  black body text on light themes. Plain lines used to inherit a dark foreground that was unreadable
  on dark themes.
- **Inline highlight**: `(Backlash applied)` is drawn **orange bold**, matching the Web UI's
  `span.log-backlash` (including the Web UI's behaviour of wrapping a bare `Backlash applied` in
  parentheses).
- Added the Web UI's three buttons above the log: **⚠ RESET ERROR** (sends `ReER:1` through the
  active transport), **Export CSV** (Save-file dialog, `Time,Level,Message`) and **Clear**.

**Files:** `MLAstroRPA-navigation/Dockables/SystemLogEntry.cs` (new),
`MLAstroRPA-navigation/Services/MlastroWebSocketService.cs`,
`MLAstroRPA-navigation/Plugin/MLAstroController.cs`,
`MLAstroRPA-navigation/Plugin/MLAstroOptions.xaml`,
`MLAstroRPA-navigation/Services/SerialConnectionService.cs`

### MLAstroRPA — Wireless: jog deceleration, Relative mode, alarms & error logs

- **Jog release now decelerates** instead of stopping dead. The WebSocket `stop` command calls
  `stopAllMotion()`, which cancels the far target with `setCurrentPosition()` (a deliberate hard stop
  for the STOP/E-STOP buttons) — so releasing a jog braked instantly. Added a dedicated
  `{"cmd":"stopMove","data":{"axis":"az|alt"}}` command mirroring the Serial `MAzL:0` / `MAlU:0`
  release (`setAcceleration(decel)` + `stop()`), used by the plugin **and** by the Web UI jog release
  (mouse-up / mouse-leave / touch-end / arrow key-up). STOP and E-STOP keep the old hard-stop path.
- **Relative mode stays on**: `TelemetryData.IsRelativeMode` is parsed from the telemetry `JoRe` key,
  which the WebSocket telemetry does not carry — so the dock reset the toggle to OFF on every packet.
  The synthesized telemetry line now includes `JoRe`/`ReDe`/`ReAM`/`ReAS` from the tracked state.
- **Alarm History now works over Wireless**: the firmware broadcasts its error telemetry as
  `{"error":"ERROR:Code:value,..."}` (edge-triggered, same string as the Serial line); the plugin
  feeds it into the shared pipeline, so the dock's Alarm panel and `HasActiveErrors` behave exactly as
  with the COM port.
- **Error/warning logs now appear in the System log**: each `alert` from the firmware is added to the
  log (colour-coded) in addition to the NINA toast, and every error-state change writes a readable
  summary line — e.g. `DRIVER ERROR: AZ open load (AzOL), ALT hard limit (AlHL)` / `All clear`.

**Files:** `MLAstroRPA-navigation/Services/MlastroWebSocketService.cs`,
`src/Web/WebControl.cpp`, `src/Serial/SerialControl.cpp`, `data/script.js` (firmware repo),
`src/Websocket-protocol.md`

### MLAstroRPA — Jog: nút mũi tên tự khoá khi bị từ chối vì soft-limit

- Khi firmware từ chối lệnh jog vì trục đã ở/qua soft-limit (`CmdRf` bit JOG_AZ/JOG_ALT → mã
  `RfJogAz`/`RfJogAl`), plugin **khoá đúng nút hướng vừa bấm** và **nhả jog ngay một lần** (gửi
  `MAzL:0` / `MAlU:0` qua `StopJogMovement()` — hàm này cũng dừng watchdog 250 ms và xoá lệnh đang
  chạy) rồi **không gửi lệnh nào nữa** → hết cảnh spam lệnh bị từ chối mỗi 250 ms.
- Ghi chú kỹ thuật: **chỉ `IsEnabled = false` là KHÔNG đủ** — WPF không phát `MouseUp`/`MouseLeave`
  cho button vừa bị disable nên handler nhả nút không chạy; vì vậy phải gọi `StopJogMovement()`
  tường minh ngay khi nhận cảnh báo từ chối.
- Nút được mở khoá lại khi người dùng bấm **hướng ngược lại** (`UnblockJogAxis`) — lúc đó trục đi ra
  khỏi giới hạn nên hướng cũ dùng lại được.
- **Tự mở khoá sau 2 giây** (`JOG_UNBLOCK_DELAY_MS`, `DispatcherTimer`): mỗi lần bị từ chối lại dời hẹn,
  nên trong lúc giữ/nhấn liên tục nút vẫn khoá, nhưng chỉ 2 s sau lần từ chối cuối là nút sáng lại.
  Cảnh báo "trục đang ở biên" là tạm thời — trục có thể đã được đưa ra khỏi giới hạn bằng nguồn khác
  (relative / auto / Web UI) nên không giữ nút khoá vĩnh viễn. Timer được dọn trong `Dispose()`.
- Chống trùng cảnh báo: nút mũi tên khoá theo **cả hai** nguồn — `AzSL`/`AlSL` (guard vừa hãm dừng trục
  tại biên) hoặc `RfJogAz`/`RfJogAl` (trục đứng sẵn tại biên mà vẫn nhấn jog). Firmware bảo đảm hai
  nguồn **loại trừ nhau** nên bảng Alarm và toast chỉ có **1 dòng** cho mỗi sự việc (trước đó jog vào
  giới hạn làm hiện 2 warning cùng lúc: `AZ soft limit stop` + `AZ jog refused`).
- Bốn nút mũi tên giờ bind `IsEnabled` vào `CanJogAltUp` / `CanJogAltDown` / `CanJogAzLeft` /
  `CanJogAzRight` (= `CanManualControl` && chưa bị khoá) thay cho `CanManualControl` trực tiếp.

**Files:** `MLAstroRPA-navigation/Dockables/PolarAlignmentDockVM.cs`,
`MLAstroRPA-navigation/Dockables/PolarAlignmentDockable.xaml`

### MLAstroRPA — Alarm: nút CLEAR + tên cảnh báo soft-limit

- Bảng **Alarm History** có thêm nút **🗑 CLEAR** (`ClearAlarmHistoryCommand`) để xoá lịch sử cảnh báo.
  Nút chỉ xoá phần hiển thị — **không** đụng tới trạng thái lỗi/cảnh báo đang active của thiết bị
  (khác với `ClearAlarmHistory()` nội bộ dùng khi ngắt kết nối, hàm này vẫn reset cả trạng thái).
- Đổi tên hiển thị cho khớp log firmware: `AzSL`/`AlSL` = **"AZ/ALT soft limit reached"** (trước là
  "AZ soft limit stop") và `RfJogAz`/`RfJogAl` = **"AZ/ALT jog refused (already at soft limit)"** —
  nhờ vậy dòng trên bảng Alarm đọc ra giống hệt dòng log trong System Log, dễ đối chiếu.

**Files:** `MLAstroRPA-navigation/Services/SerialConnectionService.cs`,
`MLAstroRPA-navigation/Dockables/PolarAlignmentDockVM.cs`,
`MLAstroRPA-navigation/Dockables/PolarAlignmentDockable.xaml`

### MLAstroRPA — Alarm: hiển thị các lệnh bị TỪ CHỐI vì soft-limit (`CmdRf`)

- Dòng ERROR telemetry có token mới `CmdRf:<bitfield>`: mỗi bit là một loại lệnh bị từ chối vì
  soft-limit, bật ngay lúc bị từ chối và firmware tự tắt sau ~1.5 s nếu loại lệnh đó không còn bị
  từ chối nữa.
- Plugin **giải mã bitfield** thành từng mã riêng, mức **WARNING** (giá trị 1), với tên rõ ràng:
  `RfRelAz` / `RfRelAl` (relative move AZ/ALT), `RfAlnAz` / `RfAlnAl` / `RfAlnOv` (align: target AZ,
  target ALT, nhánh overshoot ALT), `RfJogAz` / `RfJogAl` (jog tại giới hạn) → mỗi mã một dòng trong
  **Alarm History** kèm thời điểm bắt đầu và kết thúc.
- Vì là WARNING nên không khóa hệ thống và không bắn toast (giống các cảnh báo soft-limit khác).

**Files:** `MLAstroRPA-navigation/Services/SerialConnectionService.cs`

### MLAstroRPA — Wireless: System log now identical to the Web UI (removed 2 extra sources)

- The plugin's System log used to show two kinds of lines the Web UI never has:
  (1) the device `alert` (e.g. `⚠️ Soft Limit Reached! AZ axis stopped at configured limit.`) and
  (2) plugin-generated driver summaries (`DRIVER WARNING: AZ soft limit stop (AzSL)`,
  `DRIVER ERROR: …`, `All clear - no active driver errors`).
- The Web UI shows `alert` in a **modal** (`showModal('System Message', data.alert)`) and does **not**
  log it; it has no driver-summary lines at all. The plugin now matches exactly: `alert` → NINA toast
  (equivalent of the modal) + NINA log file only, `error` telemetry → Alarm History panel + NINA log
  file only.
- Removed the now-dead `AddErrorSummaryToSystemLog()` helper and its `_lastErrorSummary` field.
- The System log therefore has exactly the two device-origin sources the Web UI has: the `log`
  messages and the `reason` of `controlTakenBySerial` / `controlReleased`.
- Driver alarms remain fully visible in the **Alarm History** panel and as NINA notifications.
- The log timestamp now uses the locale's **short time** pattern (`ToString("t")`) — the same source the
  Web UI uses (`toLocaleTimeString()`), so a vi-VN machine shows `[17:07:51]` on both sides instead of
  the plugin showing `[5:07:51 pm]`.

**Files:** `MLAstroRPA-navigation/Services/MlastroWebSocketService.cs`,
`MLAstroRPA-navigation/Dockables/SystemLogEntry.cs`

### MLAstroRPA — Wireless: Relative mode now pushes to the device (Web/PC stay in sync)

- Toggling **Jog ↔ Relative** (or editing the relative degrees/minutes/seconds) in the dock now also
  pushes the setting **down to the firmware** via `saveConfig` with `{"relative":{mode,d,m,s}}` —
  exactly what the Web UI does (`saveRelativeSettings()`). Previously `JoRe`/`ReDe`/`ReAM`/`ReAS` were
  only remembered inside the plugin, so the device (and the Web UI monitoring from a browser) stayed in
  *Jog* while the PC was already moving relatively.
- The plugin also reads the state **back** from the device: the new firmware broadcast
  `{"relative":{...}}` and the connect snapshot both update the tracked state, so the dock, the
  synthesized telemetry (`JoRe`/`ReDe`/`ReAM`/`ReAS`) and the Web UI can never disagree.
- Sends are tagged `origin:"pcPlugin"` so the firmware's `configSaved` ack is not mistaken by the Web
  frontend for the ack of its own *SAVE ALL & REBOOT* flow.

**Files:** `MLAstroRPA-navigation/Services/MlastroWebSocketService.cs`

**Files:** `MLAstroRPA-navigation/Settings/PluginSettings.cs`,
`MLAstroRPA-navigation/Services/MlastroWebSocketService.cs`,
`MLAstroRPA-implement/MlastroWirelessSerial.cs`,
`MLAstroRPA-implement/UniversalPolarAlignmentMLAstroRPA.cs`, `MLAstroRPA-navigation/Plugin/MLAstroController.cs`,
`MLAstroRPA-navigation/Plugin/MLAstroOptions.xaml`, `MLAstroRPA-navigation/Services/SerialConnectionService.cs`

## 2.0.2.0

### MLAstroRPA — dockable layout cleanup

- **Removed the `🏠 SET HOME HERE` and `⚠️ RESET HOME` buttons** from the *Position* section of the
  MLAstroRPA dockable. The home reference is no longer set/cleared from the docking window (it stays
  available from the CONFIGURATION tab / WebUI); the `↻ RETURN TO HOME` button remains.
- **`↻ RETURN TO HOME` restyled** with the accent blue used by the Align direction toggle ON state
  (`#3498DB`, white text, rounded corners), auto-sized to its label, `40 px` high (same height as
  `✓ ALIGN ALL`) and with dedicated hover / pressed / disabled states.
- **`✓ ALIGN ALL` width now matches** the `↻ RETURN TO HOME` button so both sit in an aligned column.

### Installer / build tooling

- The MSI build (`Release-MSI.ps1`) now compares the version about to be packaged with the top
  `Changelog.md` entry and **pauses for confirmation when they differ**, so a forgotten version pump
  can no longer be packaged silently; `Package.wxs` `ProductVersion` is synced from the resolved
  version as before.

## 2.0.0.10 

### TPPA / automated correction

- **Press Start to begin the automated adjustment.** After the three measurement points and the
  error calculation, when automated adjustments are enabled the routine now connects to the
  selected polar alignment system and then **pauses** — nothing is nudged until the user presses
  Start/Resume (the dock Play button). A dedicated *"Press Start to begin the automated
  adjustment."* notification is shown right after the *"Successfully connected"* toast. The
  routine no longer starts correcting immediately after the first error solve.
- **No `STOP:1` when pausing right after connect.** The initial pause that waits for Start no
  longer sends the motor-stop command (nothing is moving yet); pausing mid-correction still stops
  a running move as before.
- **Fix false "Unable to connect" error.** The connect-success toast and its toast cleanup are now
  raised on the UI thread — previously `CloseAll` could throw from a background thread and show a
  misleading *"Unable to connect to MLAstroRPA"* error even though the hardware had connected.
- **Correction axis mode** (MLAstroRPA option): choose between **Auto** — only the axis with the
  larger error is corrected per pass (previous behaviour) — and **Both axes together** (default):
  Azimuth + Altitude are corrected in a **single ALIGN command** that runs both axes at the same
  time on the device (`ALIGN_COMPLETED` awaited once), which is faster than two separate nudges.
- **Auto-reverse hidden and off by default.** The *"Enable auto-reverse"* / *"Detecting
  direction"* controls are hidden on the options page and the feature is disabled by default;
  axis direction is set with the Reverse Azimuth / Reverse Altitude toggles.
- **Automated adjustment timeout** (new setting, minutes): the correction phase now **stops the
  polar alignment automatically** when it exceeds the configured time (default 10 min, `0`
  disables it) without reaching the alignment tolerance — replacing the old fixed 5-minute
  "still running, consider restarting" reminder that never stopped. Time spent waiting for
  Start / auto-pause is not counted towards the timeout.

## 2.0.0.9 — Merged MLAstroRPA into TPPA (nina.plugin.MLAstroRPA_TPPA)

Single options page with top-level tabs — **TPPA OPTION**, **CONTROL**, **CONNECTION**,
**CONFIGURATION** — and a single `IPluginManifest` (`PolarAlignmentPlugin`); the MLAstro
options/state controller (`MLAstroRPA-navigation\Plugin\MLAstroController.cs`) is owned by the
manifest.

### MLAstroRPA — main features (CONTROL / CONNECTION / CONFIGURATION)
- **CONTROL** — manual jog/move and home, live position & polar-alignment error readout, alarm
  history, FORCE STOP / RESET ERROR.
- **CONNECTION** — COM port + baud selection, connect/disconnect, ESP32 reset, and a live serial
  terminal (Hex checkbox before Send sends up to 16 hex characters as raw bytes; HandShake sends
  `[MLAstroRPA-TC]` and shows `Handshake: OK!` on `OK!` / `Handshake: NO ANSWER` otherwise).
- **CONFIGURATION** — soft limits, TMC2209 motor drivers (AZ/ALT), backlash & P.A. overshoot,
  WiFi (AP + Station), save-all & reboot.


### Added to TPPA
- New **MLAstroRPA** polar alignment system alongside None / UPAS / OAPA. When selected, the TPPA
  routine drives the MLAstro RPA hardware automatically over serial (USB/WiFi bridge): handshake
  on connect, corrections translated to the device's DMS format, structured align command + `ok`
  acknowledgement, and a polling loop that completes when the device reports `READY` /
  `ALIGN_COMPLETED`. `Abort` sends `STOP:1` and cancels the in-progress move.
- **Alt-axis overshoot** for automated corrections — master **"Enable overshoot"** with
  per-direction (**"Run overshoot for moving Up"** / **"Run overshoot for moving Down"**) toggles
  and a 0–240 arcminute overshoot amount. With overshoot, the Alt axis corrects 100% of the error
  then travels the overshoot past the target; without overshoot it corrects only the configured
  safety factor (same as Azimuth).
- Configurable **"Correction Safety Factor"** (default 75%, range 1–100%) applied to Azimuth on
  every automated correction and to Altitude when overshoot is not active for the current
  direction.
- **Axis reversal** for the MLAstroRPA system — **"Reverse Azimuth/Altitude Axis?"** toggles
  (default ON, persisted per axis) plus **"Enable auto-reverse"** (default OFF) which probes the
  correct direction with a small first nudge (**"Detecting direction"**, default 50%) before
  committing the full move. The direction follows the solved on-screen error, not the motor
  command.
- Removed the **"Log polling data"** option (serial TX/RX logging); only connection lifecycle is
  logged.
- Under the hood, the shared polar-alignment driver/VM base gained virtual hooks (movement,
  status polling, port handshake, abort) so the MLAstroRPA driver reuses the standard connect /
  status flow without code duplication.

---

## Version 2.2.5.0
- Replaced AAPA/Avalon checkboxes with a single ComboBox selector (None / UPAS / AAPA) per code review feedback
- Common settings (reverse axes, backlash, automated adjustments) now displayed based on the selected system
- Eliminated code duplication by extracting shared base classes and interfaces for polar alignment systems
- Removed redundant UsePolarAlignmentSystem boolean in favor of enum-based selection

## Version 2.2.4.3
- Polar alignment tab in imaging now correctly pulls the binning settings from the plate solve settings on startup

## Version 2.2.4.2
- When polar alignment is started, guiding will be stopped automatically

## Version 2.2.4.1
- Polar alignment progress is now sent via message broker using message topic `PolarAlignmentPlugin_PolarAlignment_Progress` for other plugins to consume.

## Version 2.2.4.0
- Removed the position angle spread warning as it was not giving any useful information
- Instead the declination spread that the driver is reporting is now measured and a warning is shown if it exceeds 2 arcseconds. The declination axis should not move at all during measurements.

## Version 2.2.3.8
- Log mount position when connected on each measurement point

## Version 2.2.3.7
- Fix messagebroker message parsing for filter name

## Version 2.2.3.5
- Fixed the window popout not closing automatically after the polar alignment was within the set tolerance

## Version 2.2.3.4
- Fixed manual mode to work again without a mount being connected

## Version 2.2.3.2
- `PolarAlignmentPlugin_DockablePolarAlignmentVM_StartAlignment` will now process the message content to be able to adjust parameters as needed

## Version 2.2.3.1
- Added message broker subscription to message topic `PolarAlignmentPlugin_PolarAlignment_ResumeAlignment` to resume the procedure
- Added message broker subscription to message topic `PolarAlignmentPlugin_PolarAlignment_PauseAlignment` to pause the procedure

## Version 2.2.3.0
- Added an option to auto pause between continuous exposures

## Version 2.2.2.2
- Fixed an issue when multiple polar alignment instructions were placed in the sequence with custom binning

## Version 2.2.2.1
- Fixed an issue when the UPA Gear Ratio is changed that it will not be initialized with the changed ratio in the next session

## Version 2.2.2.0
- Fixed an issue when a weather device is connected but reporting 0 hPa pressure

## Version 2.2.1.0
- Added message broker broadcast for alignment error using message topic `PolarAlignmentPlugin_PolarAlignment_AlignmentError`
- Added message broker subscription to message topic `PolarAlignmentPlugin_DockablePolarAlignmentVM_StartAlignment` to start the procedure
- Added message broker subscription to message topic `PolarAlignmentPlugin_DockablePolarAlignmentVM_StopAlignment` to stop the procedure

## Version 2.2.0.1
- After slewing to the first point, added an explicit wait for the dome synchronization if a dome is connected

## Version 2.2.0.0
- Refraction correction will now be properly applied and the option `Adjust for refraction` should now correctly align to the true pole
- Observer elevation is now considered for all transformations

## Version 2.1.0.2
- Fixed an issue when using the UPA that the direction would constantly be reversed on each adjustment.
- When using the UPA it will no longer move a last time without re-evaluation when the alignment threshold has already been reached.
- Added options for UPA to reverse azimuth and altitude axes

## Version 2.1.0.1
- Polar Alignment Tolerance can now be set on instruction level. For example when you are running an automated polar alignment run and want to dial in the polar alignment in multiple phases and getting more precise in each step.
- Now showing UPA positions in automatic mode in addition to the already existing nudge direction

## Version 2.1.0.0
- The position angle spread between the three measurements is now measured. If it is too large, a warning will be shown.

### Integration for the [Avalon Universal Polar Alignment System](https://www.avalon-instruments.com/products-menu/accessories/universal-polar-alignment-system-detail)

#### New Setting: `Use Avalon Polar Alignment System?`
- When activated, the polar alignment routine will connect to the unit automatically after the third step, allowing you to remotely adjust the altitude and azimuth of your system.

#### New Setting: `Do automated adjustments?`
- When activated, this will connect to the UPA and slowly nudge the UPA to the target position automatically after the error has been determined. The control panel will not be shown as movements are done automatically.
- Ensure your gear ratio settings are roughly matched so that one step in the UPA results in an arcminute of movement. The default settings should work fine for the standard version of the UPA.
- Make sure your mount is roughly leveled.
- *Note: For this setting to work, you also need to set the `Polar Alignment Tolerance` to a non-zero value.*


## Version 2.0.2.0
- Automatically increase search radius on plate solve by 5 during solving of the first three points each time it fails

## Version 2.0.1.0
- After automated move to next point, wait for the telescope to indicate it is no longer slewing
- Use Snapshot mode for taking images during polar alignment

## Version 2.0.0.3
- Fixed issue where the TPPA instruction with a filter set would override the autofocus exposure time

## Version 2.0.0.1
- Fixed issue with serilog when PA error logging was enabled

## Version 2.0
- Updated plugin to work with latest major N.I.N.A. version

## Version 1.7.2.0
- It is now possible to pause in between the steps and continue after making the adjustments. Useful in case your image downloads and solves take a while.

## Version 1.7.1.0
- Add an option to continue tracking when TPPA is done. Use with caution to not run into pier collisions!
- Prepopulate the filter with the platesolving filter for defaults
- When refraction correction is enabled, the pole will now also be corrected for it to determine the initial error

## Version 1.7.0.0
- Show a loading spinner while a new image is waiting for a solve to update the error details. The spinner is shown in the total error details. 
- Changed the error circle indicator to draw based on the image scale at 30 arcseconds, 1 arcminute and 5 arcminutes
- When latitude and longitude is set to 0 it was most likely never set (as these coordinates are inside the Atlantic ocean). A validation will now check for this and notify to set these values.
- Add a warning when initial error exceeds 2 degrees, that the adjustment phase will be error prone and that it is advised to run it again once the error was reduced
- A further warning when the error exceeds 10 degrees is shown, that the mount is too far off, the location is incorrect or that the RA axis was not moved exclusively

## Version 1.6.3.0
- Added a reset to defaults button
- Added an alignment tolerance to automatically finish polar alignment when below the given threshold

## Version 1.6.2.0

- Fixed an issue where the polar alignment would fail when output logging was enabled

## Version 1.6.0.0

- Enhanced the scaling of the error text for smaller resolutions
- Added an option to account for refraction (which needs further testing in live conditions)

## Version 1.5.3.0

- Gain should now be prepopulated by plate solve gain setting

## Version 1.5.1.0

- Added dome support by waiting for the dome to sync after moving the axis for both automated mode as well as manual mode when both the mount and dome is connected
- Improved manual mode when mount is connected to only get a plate solved image after movement is complete
- Adjusted status report slightly

## Version 1.5.0.0

- When moving near the pole in automated mode and having multiple degrees of PA error, the warning that the mount did not move far enough was shown, even when the mount did indeed travel far enough
	- This was caused by comparing the actual solved image RA with the starting RA, but now it will compare the drivers reported RA where the mount thinks it is
	- Comparing the actual solved RA does lead to this error, as the axis of the mount is shifted and the circle is not perfectly aligned with the pole
- Fixed an issue when solving succeeded, but star detection did not detect any stars, that the algorithm should no longer fail but use the center of the image instead

## Version 1.4.1.0

- With nightly 1.11 #165 the star detector became incompatible. This version will make it compatible again.

## Version 1.4.0.0

- The plugin now logs the amount of error into `User Documents >> N.I.N.A >> PolarAlignment` when activated in the options
- Added validation when telescope is connected but at park
- Fixed that filter is not saved when saving the instruction as part of an advanced sequence

## Version 1.3.7.0

- In addition to left/right the error display will also include east/west
- Fixed that the altitude error for southern hemisphere was flipped
- Added a toggle to be able to start from the current mount position instead of slewing to a specific alt/az
- Added an expander to the imaging tab tool panel to collapse the options

## Version 1.3.6.0

- Added the individual steps as progress and mark them visually as completed to give the user a better indication of the completion of individual steps
- Added a new color option for the completed steps color

## Version 1.3.5.0

- The manual mode now also works in full blind mode without any telescope connection. A blind solver needs to be setup - but it must not be astrometry.net due to being too slow.
- Added the validation messages to imaging dock to see why the routine cannot be started

## Version 1.3.4.0

- Adjusted plugin description with new markdown syntax

## Version 1.3.3.0

- Fix DefaultAzimuthOffset to be correctly applied in the southern hemisphere as azimuth 180° + offset (instead of 0° + offset)

## Version 1.3.2.0

- Remove the compensation when the automated slew did not reach the expected distance. The various mount drivers differ too much to determine a clever compensation model
- Instead the slew timeout factor can be adjusted. See the [FAQ for details](https://bitbucket.org/Isbeorn/nina.plugins/src/master/NINA.Plugin.Notification/NINA.Plugins.PolarAlignment/FAQ.md)
- In manual mode, wait for the telescope to not report *slewing* before trying to solve

## Version 1.3.1.0

- Improved the target distance check for more tolerance and better compensation

## Version 1.3.0.0

- Added a new "Manual Mode", for mounts that are either no goto mounts or do not implement the necessary interfaces for automated point retrieval
- Further refactoring to reduce code duplication

## Version 1.2.2.0

- Added a check, when the target distance was not reached within one degree to reslew again until the target distance is reached. This can happen when the move rate is less than advertised inside the mount driver.
- Fix an issue when running Three Point Polar Alignment on the imaging tab that it won't be started again after the first iteration.

## Version 1.2.1.0

- Reveal "Default Altitude Offset" and "Default Azimuth Offset" to alter the initial coordinates that are getting preset
- Optimize some of the default settings
- Internal refactorings to reduce code duplications as well as layout improvements
- Check if the camera is free to use when starting the routine out of the imaging tab. If the camera is in use, the play button will be disabled.
- When starting the polar alignment out of framing the camera will be blocked during the routine, to not allow other areas to take control of the camera.

## Version 1.2.0.1

- Fixed an issue when moving the axis would traverse over 24h right ascension - leading to an incorrect distance moved

## Version 1.2.0.0

- The plugin is now also available in the imaging tab to be started directly there instead of inside the sequence.
- A new button inside the tools pane in the imaging tab on the top right is available to open the polar alignment tool

## Version 1.1.0.0

- Complete rewrite of the error determination and correction logic to allow for locations further off from celestial pole and meridian
- Show the initial error amount in smaller numbers below the adjusted error
- Display a shadow rectangle showing the original error for reference behind the adjustet error rectangle

## Version 1.0.0.8

- Added a dedicated changelog file to the repository
- Fix: When using debayered images the plugin would close on the final step with an error

## Version 1.0.0.7

- Fix: Azimuth error could sometimes exceed 180° instead of showing a negative error instead

## Version 1.0.0.6

- Fix: Azimuth error for southern hemisphere was calculated incorrectly

## Version 1.0.0.5

- Initial release using the new plugin manager approach, making the plugin available for download inside N.I.N.A.