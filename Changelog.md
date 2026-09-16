# Changelog

> **Origin & license (TPPA).** The polar-alignment portion of this plugin is a fork of the
> original open-source **Three Point Polar Alignment (TPPA)** plugin for NINA by
> [Isbeorn](https://github.com/isbeorn/nina.plugin.polaralignment), which is licensed under the
> **Mozilla Public License 2.0 (MPL-2.0)**. The TPPA-derived code in this project therefore stays
> under MPL-2.0, keeping the original license/copyright notices. **MLAstroRPA+TPPA is a separate,
> unofficial build — it is NOT the original/official TPPA plugin.** When redistributing, comply
> with MPL-2.0: retain the license and notices, credit the original author, and make the source
> (including your modifications) available.

## 2.1.1.0

### MLAstroRPA — Wireless connect: clear failure + automatic Serial fallback
- A failed wireless connect (mDNS/DNS not resolving, wrong device address, refused handshake) is now reported instead of being hidden by a silent Serial fallback: the toast/status says *"Wireless connection failed (…). Trying to scan the serial (COM) connection…"*
- When the Serial fallback does find the device, the plugin **switches the Connection type back to Serial** on its own (CONNECTION tab selector follows immediately) and MLAstro stays the COM owner, so the UI/telemetry match the transport actually in use
- Wireless connect now retries only a few times inside a 5 s window (per-attempt resolve/connect timeouts) and then fails with the reason, instead of retrying indefinitely on a bad address
- Auto-reconnect over Wireless is bounded too (30 s so a device reboot can finish) and the error dialog shows the reason; the CONNECTION tab *Connect* button shows a warning toast when the wireless connect fails
- TPPA **Test Connect** now tests the wireless transport first when Wireless is selected, before falling back to the COM scan

### MLAstroRPA — CONNECTION tab / connect feedback
- The connect toast and status now name the transport actually in use: *Successfully connected to MLAstroRPA over Serial (COM5)* / *… over Wireless (WebSocket @ MLastroRPA.local)*, so a Serial connection is never mistaken for a wireless one
- Warning line next to the *Connection type* selector (orange ⚠️): switching Serial ↔ Wireless disconnects the current session (and stops an alignment in progress)

### MLAstroRPA — Wireless: parity with Serial for the TPPA session
- TPPA connecting over **Wireless** now takes the external-control lock, so the MLAstro plugin locks CONTROL / CONFIGURATION while TPPA holds the session (before: only the Serial session locked)
- **STOP / FORCE STOP** pressed on MLAstro while TPPA runs over Wireless now reaches TPPA — the WebSocket notify path returned early when no external-stop listener was registered; TPPA now shows the toast and stops the whole PA routine (not just the current move)
- MLAstro disconnecting the wireless session now shows *"Disconnected by MLAstro plugin - TPPA session closed."* (before: fully silent — only the Serial session notified)
- Toast wording: *"FORCE-STOP pressed on MLAstro plugin - TPPA PA stopped."* (was `E-STOP`); the serial `ESTOP:1` command and the WS `forceStop` JSON are unchanged

## 2.1.0.0

### MLAstroRPA — Wireless connection (WebSocket over mDNS)
- New connection type selector in the CONNECTION tab: Serial or **Wireless** (address `MLAstroRPA.local`, or the device IP as fallback)
- The WebSocket transport performs the PC handshake and then behaves like the COM port (same UI, dock and telemetry pipeline)
- TPPA automated adjustment works over Wireless; switching transport disconnects the other one first

### MLAstroRPA — Complete text <-> JSON translator
- Commands: Serial text -> WebSocket JSON (jog/stopMove, relative, align, home, speed, settings, reboot, `saveConfig`/`applyConfig`)
- Telemetry: JSON -> the firmware's serial telemetry text, with every token (limits, motor, backlash, WiFi, align errors, `Mpos` header)
- Two-way setting sync via `saveConfig` + `config_pushed`, so web UI, dock and device never disagree
- WiFi/AP settings and passwords can be saved over Wireless

### MLAstroRPA — System log & dock parity with the Web UI
- Web-style log table (keyword colours, newest on top, 50 lines) instead of the raw serial terminal
- Same buttons as the web: RESET ERROR, Export CSV, Clear; `alert` messages shown as NINA toasts
- Telemetry keys are no longer parsed as alarms (only `ERROR:` lines are)

### MLAstroRPA — Jog, Relative and soft-limit safety
- Jog release decelerates via `stopMove`; STOP / E-STOP stay hard stops
- Jog buttons lock when the soft limit refuses, and re-enable after 2 s or when the axis moves away
- Relative mode stays on (synthesized telemetry carries `JoRe/ReDe/ReAM/ReAS`)

### MLAstroRPA — Alarm panel
- `CmdRf` decoded into readable warnings (`RfRelAz/Al`, `RfAlnAz/Al/Ov`, `RfJogAz/Al`); newest alarm on top
- CLEAR only clears the displayed history; one notification per event

### MLAstroRPA — CONNECTION tab cleanup
- Serial panel: removed Data Bits / Parity / Stop Bits and Refresh Ports (always 115200 8N1)
- **Reset ESP32** (renamed from *Reboot device*) moved next to **Connect**

### MLAstroRPA — Reboot / Reset ESP32 over Wireless
- Fixed: the reset button did nothing over Wireless (missing `reboot` mapping + `releaseControl` sent too early)
- Now sends `{"cmd":"reboot"}` like the Web UI button and reconnects automatically

### MLAstroRPA — WiFi passwords + settings
- Passwords are no longer broadcast: the 👁 button fetches them on demand from the device
- Empty credential fields are never sent — a blank box means "keep the current value"
- Telemetry-mirrored settings no longer keep stale values or fabricated defaults

### MLAstroRPA — Relative distance by keyboard
- Type D/M/S directly in the dock and web UI: degrees 0–5, minutes/seconds 0–59 (clamped, Enter sends)

### MLAstroRPA — Dock / options UI restyle
- Movement pad: the four direction buttons are rounded triangles with the Alt/Az label centred on the triangle's centroid; STOP is a centred square button
- Speed Level buttons are circles (40×40); the header FORCE STOP is a red circle inside a square frame (same look as the web UI)
- Relative steppers: removed the surrounding frame and the Degrees/Minutes/Seconds labels (values are still typed, clamped and sent on Enter)

### MLAstroRPA — Align error values shared instantly (dock <-> Web UI)
- Pressing **Enter** in an Az/Alt error field sends that axis' D/M/S + direction to the device right away (no need to press an Align button first)
- The device now broadcasts the align D/M/S + direction to every WebSocket client, so the Web UI mirrors the values changed from the dock/Serial without a page refresh

## 2.0.2.0

### MLAstroRPA — dockable layout cleanup
- Removed SET HOME HERE / RESET HOME from the dock (still available in CONFIGURATION / WebUI); RETURN TO HOME and ALIGN ALL restyled

### Installer / build tooling
- The MSI build compares its version with the top changelog entry and asks for confirmation when they differ

## 2.0.0.10 

### TPPA / automated correction

- Correction now waits for **Start** (connects, then pauses) instead of nudging right after the first error solve
- New **Automated adjustment timeout** (default 10 min, `0` disables): stops the routine if the tolerance is not reached
- **Correction axis mode**: `Auto` (larger error only) or **Both axes together** (default, single ALIGN command on the device)
- **Auto-reverse** hidden and off by default; axis direction via the Reverse Azimuth / Reverse Altitude toggles
- Fixed a false *"Unable to connect"* toast (raised from a background thread)

## 2.0.0.9 — Merged MLAstroRPA into TPPA (nina.plugin.MLAstroRPA_TPPA)

Single options page with tabs **TPPA OPTION / CONTROL / CONNECTION / CONFIGURATION** and a single
`IPluginManifest` (`PolarAlignmentPlugin`).

### MLAstroRPA — main features
- **CONTROL** — manual jog/move and home, live position & polar-alignment error, alarm history, FORCE STOP / RESET ERROR
- **CONNECTION** — COM port, connect/disconnect, ESP32 reset, live serial terminal (Hex send + HandShake)
- **CONFIGURATION** — soft limits, TMC2209 drivers (AZ/ALT), backlash & P.A. overshoot, WiFi (AP + Station), save-all & reboot

### Added to TPPA
- New **MLAstroRPA** polar-alignment system (beside None / UPAS / OAPA): handshake on connect, DMS corrections, structured align command + `ok`, completes on `READY` / `ALIGN_COMPLETED`
- **Alt-axis overshoot**: master enable + per-direction toggles + 0–240 arcminute amount
- **Correction Safety Factor** (default 75%, range 1–100%) for Azimuth and for Altitude when overshoot is inactive
- **Axis reversal** toggles (default ON, persisted) and **auto-reverse** (default OFF)
- Removed the **"Log polling data"** option; shared base classes/hooks removed code duplication between alignment systems

---

## Older versions (TPPA upstream)

All versions **2.2.5.0 and older** are the upstream **Three Point Polar Alignment (TPPA)** plugin by
[Isbeorn](https://github.com/isbeorn/nina.plugin.polaralignment) — they are NOT maintained in this
repository and are listed here only as the reference for the behaviour this plugin inherits.

- **Forked from:** upstream branch `master`, commit
  [`16b785ee2a3a36d2bf509968db3c307312e82017`](https://github.com/isbeorn/nina.plugin.polaralignment/commit/16b785ee2a3a36d2bf509968db3c307312e82017)
  (*"Merge pull request #10 from michelebergo/rename-aapa-to-oapa"*, 2026-04-11) — upstream plugin
  version **2.2.5.0**. The matching upstream tag `2.2.5.0` points at
  `ff981e8e14fbceefff7dcb0bf0861deff49a2a92` (2026-03-21).
- **Imported here by:** commit `859f659` — *"Create project: Copy TPPA in to this project make base
  code. Implement MLAstroRPA plugin into base code"* (2026-09-03).
- Upstream work released after that fork point (upstream `2.2.6.0` and later) is **not** included in
  this plugin; check the upstream repository for that history.