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

- **New connection type selector** at the top of the CONNECTION tab: `Serial connection` (unchanged) or
  **`Wireless connection`**. Choosing Wireless hides the serial-only settings (COM port, data
  bits/parity/stop bits, refresh ports, pause-polling, handshake timeout, polling period, Hex/Send row)
  and shows the wireless panel instead: **Address** (`MLAstroRPA.local` by default, or the device IP as
  a fallback when mDNS does not resolve), **Connection** (Connect/Disconnect + status) and **System log**
  (same RichTextBox, fed by the WebSocket traffic).
- **WebSocket transport** (`MlastroWebSocketService`) connects to `ws://<address>:80/ws`, performs the
  PC handshake `{"cmd":"handshake","data":{"key":"MLAstroRPA-TC"}}` and then behaves like the COM
  port: the serial text protocol is translated to WebSocket JSON and incoming JSON telemetry is
  converted back into the firmware's serial telemetry text and injected into the existing pipeline, so
  the CONTROL/CONFIGURATION tabs, the MLAstro dock and `TelemetryParser` keep working unchanged (no
  duplicated parsing logic).
- **TPPA automated adjustment works over Wireless**: `MlastroWirelessSerial` implements `ISerialLink`,
  so the polar-alignment driver (`UniversalPolarAlignmentMLAstroRPA`) uses the WebSocket session when
  `PluginSettings.TransportMode == Wireless` (sharing the single PC session with the plugin UI — the
  firmware only allows one PC client).
- `SerialConnectionService` acts as a facade while Wireless is active: `IsConnected`, `ConnectionStatus`,
  `HandshakeStatus`, `FirmwareVersion`, `Send`, `SendCommandAndAwaitOkAsync`, `ResetEsp32`,
  `QueryTelemetry` and the external-control API all route to the WebSocket session (the COM port stays
  closed), so the CONTROL/CONFIGURATION tabs and the dock report "connected" as usual.
- Safety: switching connection type disconnects the current transport first (a single transport may hold
  control at a time), and losing the wireless link raises the same external-stop path so a running
  polar-alignment routine is aborted.

**Files:** `MLAstroRPA-navigation/Settings/PluginSettings.cs`,
`MLAstroRPA-implement/Services/MlastroWebSocketService.cs`,
`MLAstroRPA-implement/Services/SerialConnectionService.cs`,
`MLAstroRPA-implement/MlastroWirelessSerial.cs`,
`MLAstroRPA-implement/UniversalPolarAlignmentMLAstroRPA.cs`,
`MLAstroRPA-navigation/Plugin/MLAstroController.cs`, `MLAstroRPA-navigation/Plugin/MLAstroOptions.xaml`

### MLAstroRPA — Complete text<->JSON translator on the WebSocket boundary

- The WebSocket wire protocol stays **pure JSON** (the web UI's own command set) while the plugin and the
  TPPA driver keep the **serial text protocol**; everything is translated at the
  `MlastroWebSocketService` boundary, so both halves work over Wireless exactly as over the COM port.
- **Commands** — jog (`MAzL/MAzR/MAlU/MAlD:1` → `move`, release → `stopMove`), Relative mode
  (`JoRe/ReDe/ReAM/ReAS` → `saveConfig{relative}`, arrow press → `moveRelative`), `STOP`/`ESTOP`,
  `ReER`, the home commands, `SLvl`, align (`AAll`/`AzAN`/`AlAN` → `align`; `AzED…/AzDi` without a
  trigger → `saveConfig{align}`, i.e. the values are stored **without** moving the axis), `ApplyConf` →
  `applyConfig`, the whole configuration chain → `saveConfig`/`applyConfig`, `Disconnect` →
  `releaseControl`, and `APss/APpa/APip/APsu`, `STAs/STAp` → `saveConfig{wifi_ap}` / `{wifi}` (with
  `no_reboot` plus an explicit `reboot` after the ack) — so **WiFi/AP settings and passwords can now be
  saved over Wireless**. Read-only / telemetry-only keys are ignored instead of being reported as
  unsupported, and repeated jog sends are collapsed (the firmware rejects motion commands while moving).
- **Telemetry** — the JSON frame is synthesized back into the firmware's serial telemetry text with
  **every** token the serial line carries: `Scal`, `WSta`, `Home`, `SLvl`, `AzPH`/`AlPH`,
  `JoRe/ReDe/ReAM/ReAS`, limits, motor, backlash, `APss/APma/APip/APsu`, `STAs/STAm/STAi`, the align
  errors `AzED/AzEM/AzES/AzDi` (+ Alt) and the `<STATUS|Mpos:…|>` header, using the same number formats
  as the firmware's `snprintf`. Frames that only carry `sys_status` (`STOPPED`, `REBOOTING`) are not
  treated as position telemetry.
- **Two-way setting sync** — the plugin pushes Relative mode/values and every configuration change down
  with `saveConfig` (tagged `origin:"pcPlugin"` so the Web frontend does not mistake the ack for its own
  *SAVE ALL & REBOOT*) and reads the state back from the device broadcast and the config snapshot, so
  the Web UI, the dock and the device can never disagree.
- **Settings are always read from the backend** — the firmware re-sends the whole configuration
  (`config_pushed`) whenever it changes (from the web, from the plugin or from Serial) and the plugin
  re-caches `limits`/`motor`/`backlash`/`serial`/`wifi_ap`/`align`/`align_mode` plus the top-level STA
  fields, instead of keeping the copy taken at connect time.

**Files:** `MLAstroRPA-implement/Services/MlastroWebSocketService.cs`,
`MLAstroRPA-implement/Services/SerialConnectionService.cs`,
`MLAstroRPA-navigation/Plugin/MLAstroController.cs`,
`MLAstroRPA-implement/MlastroWirelessSerial.cs`

### MLAstroRPA — System log & dock parity with the Web UI

- The Wireless **System log** is a web-style table instead of the raw serial terminal: the same keyword
  colouring as the Web UI (contrast-aware palette derived from the panel background), newest entry on
  top, capped at 50 lines, timestamps in the locale short format (`[h:mm:ss tt]`), rendered in a
  RichTextBox (selectable/copyable, context menu → Copy) with a drag Thumb to resize its height. The
  Web UI's noisy *"Manual stop sequence completed. Hardlimit re-enabled."* line is filtered out.
- **The panel contains exactly what the Web UI shows**: the device `log` messages plus the `reason` of
  `controlTakenBySerial` / `controlReleased`, reproduced verbatim (no `NOTE:`/`ERROR:` prefixes, no
  TX/RX frame dumps, no plugin chatter). Plugin diagnostics (*Resolving…*, *Connected…*,
  *Handshake: OK!…*) go to the NINA log file instead, and firmware `alert` messages are surfaced as
  NINA toast notifications (the Web UI shows them as a modal, not in the log). `(Backlash applied)` is
  highlighted orange bold, matching the Web UI's `span.log-backlash`.
- The Web UI's three buttons are available above the log: **⚠ RESET ERROR** (sends `ReER:1` through the
  active transport), **Export CSV** (`Time,Level,Message`) and **Clear**.
- Telemetry data keys are never parsed as alarms: `ProcessErrorTelemetry` only accepts lines starting
  with `ERROR:` (the firmware's code list), so `WSta:1`/`Home:1`/`AzRM:1`/`Back:1` can no longer raise
  fake WARNING rows, and every new session starts from a clean error state.
- Connect hints now say *(Serial or Wireless)* and the dock hint points at the **CONNECTION** tab.

**Files:** `MLAstroRPA-navigation/Dockables/SystemLogEntry.cs`,
`MLAstroRPA-implement/Services/MlastroWebSocketService.cs`,
`MLAstroRPA-implement/Services/SerialConnectionService.cs`,
`MLAstroRPA-navigation/Plugin/MLAstroController.cs`,
`MLAstroRPA-navigation/Plugin/MLAstroOptions.xaml`

### MLAstroRPA — Jog, Relative and soft-limit safety

- **Jog release decelerates** instead of stopping dead: the plugin uses
  `{"cmd":"stopMove","data":{"axis":"az|alt"}}` (mirroring the Serial `MAzL:0` / `MAlU:0` release) for
  mouse-up, mouse-leave, touch-end and arrow key-up; STOP and E-STOP keep the deliberate hard stop.
- **Jog buttons lock when the soft limit refuses the command**: the pressed direction is disabled, the
  jog is released once (stop the 250 ms watchdog and send `:0`) and nothing further is sent — no more
  250 ms spam of refused commands. WPF does not raise `MouseUp`/`MouseLeave` for a button that is
  disabled mid-press, so the release has to be issued explicitly. The button is re-enabled when the
  opposite direction is pressed (the axis then moves away from the limit) or automatically **2 s**
  after the last refusal, because an "at the limit" state is temporary — the axis may have been moved
  back by another source (relative move, auto routine, Web UI). The four arrow buttons now bind
  `IsEnabled` to `CanJogAltUp`/`CanJogAltDown`/`CanJogAzLeft`/`CanJogAzRight` instead of `CanManualControl`.
- **Relative mode stays on**: `TelemetryData.IsRelativeMode` is parsed from the `JoRe` token, which the
  WebSocket telemetry does not carry, so the dock used to reset the toggle on every packet; the
  synthesized telemetry line now includes `JoRe/ReDe/ReAM/ReAS` from the state tracked together with the
  device, and releasing an arrow button in Relative mode sends nothing (as in the serial protocol).

**Files:** `MLAstroRPA-navigation/Dockables/PolarAlignmentDockVM.cs`,
`MLAstroRPA-navigation/Dockables/PolarAlignmentDockable.xaml`,
`MLAstroRPA-implement/Services/MlastroWebSocketService.cs`

### MLAstroRPA — Alarm panel (CLEAR, decoded refusals, newest on top)

- **Alarm History works over Wireless**: the firmware's `{"error":"ERROR:Code:value,…"}` frames are fed
  into the shared pipeline, so the panel and `HasActiveErrors` behave exactly as with the COM port.
- The `CmdRf` bitfield is decoded into individual **WARNING** codes with self-explaining names —
  `RfRelAz`/`RfRelAl` (relative move refused), `RfAlnAz`/`RfAlnAl`/`RfAlnOv` (align target / overshoot
  leg out of range) and `RfJogAz`/`RfJogAl` (jog refused at the limit) — each with its activation and
  end time on the same row. Because they are warnings they never lock the system.
- Alarm rows read exactly like the matching System log line: `AzSL`/`AlSL` are shown as
  *AZ/ALT soft limit reached* and `RfJogAz`/`RfJogAl` as *AZ/ALT jog refused (already at soft limit)*.
- A **🗑 CLEAR** button clears the displayed history only — the device's active error state, the alarm
  toasts and the System log are untouched (unlike the internal reset performed on disconnect).
- **Newest alarm on top**, older ones below — the same order as the System log and the Web UI log.
- **One notification per event**: the alarm code is the single notification channel (it exists on both
  transports), so a limit alert no longer produces a second NINA dialog next to the alarm warning; the
  more detailed alert text is still written to the NINA log file.

**Files:** `MLAstroRPA-implement/Services/SerialConnectionService.cs`,
`MLAstroRPA-navigation/Dockables/PolarAlignmentDockVM.cs`,
`MLAstroRPA-navigation/Dockables/PolarAlignmentDockable.xaml`

### MLAstroRPA — CONNECTION tab cleanup + fixed "Reset ESP32" reconnect over Wireless

- **Serial panel simplified:** the read-only *Data Bits* / *Parity* / *Stop Bits* boxes and the
  **Refresh Ports** button are gone — the COM port is always `115200 8N1` and the port list already
  refreshes when the dropdown is opened, so the button was redundant.
- **Wireless panel:** the `Connection:` label was removed and **Reset ESP32** (renamed from
  *Reboot device*) now sits in the same row as **Connect**.
- **Fixed — Reset ESP32 over Wireless never reconnected.** The button only sent `reboot`: the plugin
  stayed "connected" from its own point of view (`ConnectAsync()` returns immediately while
  `IsConnected == true`, so the automatic retry did nothing) and the device kept the previous PC
  session, so a fresh handshake could be refused (*"Another PC session is already in control"*) until
  the dead socket timed out (~15 s keepalive). The button now (1) sends `releaseControl` so the device
  frees the PC slot before restarting, (2) aborts the socket, and (3) reconnects — with the wireless
  retry window raised to 30 attempts so a full reboot + WiFi join fits.

**Files:** `MLAstroRPA-navigation/Plugin/MLAstroOptions.xaml`,
`MLAstroRPA-navigation/Plugin/MLAstroController.cs`,
`MLAstroRPA-implement/Services/MlastroWebSocketService.cs`

### MLAstroRPA — WiFi passwords fetched on demand, settings never keep stale values

- **Password is no longer part of the snapshot sync.** The firmware stopped broadcasting `pass` in the
  connect snapshot / config push (those frames reach every client), so the plugin no longer reads it
  from there and no longer shows a cached copy.
- **Eye icon = real device read.** Over Wireless, `STAp:?` / `APpa:?` now send the new `getConfig`
  command and feed the `configRead` answer back through the firmware text stream, so the CONFIGURATION
  tab displays the value the **device** actually holds (same behaviour as the Serial cable path).
  A serial `APpa:` / `STAp:` answer is accepted even when it is **empty** — an empty password is real
  information, and hiding it is what made the earlier "STA fails with reason 15" diagnosis point at
  the router instead of at the lost credential.
- **Saving can no longer wipe a stored password:** the plugin never sends `STAp:` / `APpa:` when the
  field is empty (WPF's `UpdateSourceTrigger=PropertyChanged` pushes intermediate `""` values while
  retyping). Leaving the box empty means "keep the device value".
- **No fabricated defaults for telemetry-mirrored settings:** *Current STA Mode IP* starts empty
  instead of the placeholder text `Waiting for connection...`.

**Files:** `MLAstroRPA-implement/Services/MlastroWebSocketService.cs`,
`MLAstroRPA-implement/Services/SerialConnectionService.cs`,
`MLAstroRPA-navigation/Plugin/MLAstroController.cs`, `MLAstroRPA-navigation/Plugin/MLAstroOptions.xaml`,
`MLAstroRPA-navigation/Settings/PluginSettings.cs`

### MLAstroRPA — Reboot / Reset ESP32 over Wireless now behaves like the Web UI button

- **Fixed: the reset button did nothing over Wireless (PC client).** Two causes: the text token
  `reboot` had no translator mapping (it was swallowed and never reached the device), and the service
  sent `releaseControl` *before* the reboot — once control is released the client is no longer the PC
  controller, so the firmware answers `{"status":"locked"}` and refuses to restart.
- The button now sends `{"cmd":"reboot"}` directly, exactly like the Web UI REBOOT button: the device
  logs `System Rebooting command received...`, broadcasts `sys_status: REBOOTING`, restarts after
  500 ms, and the plugin reconnects on its own. The Serial path still uses the DTR/RTS pulse without
  closing the COM port.
- The translator also maps a bare `reboot` token to `{"cmd":"reboot"}`, so any text-protocol caller
  gets the same behaviour as `Save&Reboot`.

**Files:** `MLAstroRPA-implement/Services/MlastroWebSocketService.cs`

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