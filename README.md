# MLAstroRPA + TPPA

A single **NINA** plugin that merges:

- **MLAstro Robotic Polar Alignment** — full hardware control for the MLAstro RPA motor
  controller (ESP32) over USB serial:
  - **CONTROL** — manual jog/move, home, position & polar-alignment error readout,
    alarm history, FORCE STOP / RESET ERROR.
  - **CONNECTION** — COM port + baud selection, connect, ESP32 reset, live serial terminal
    (Hex checkbox before Send, handshake `[MLAstroRPA-TC]` → `Handshake: OK!` / `NO ANSWER`).
  - **CONFIGURATION** — soft limits, TMC2209 motor drivers (AZ/ALT), backlash & P.A overshoot,
    WiFi (AP + Station), save-all & reboot.
- **Three Point Polar Alignment (TPPA)** — the polar alignment wizard/assistant available as a
  tool pane inside the imaging tab plus an advanced sequencer instruction, able to drive the
  mount manually or fully automated.

The plugin options page is a single page with top-level tabs:
`TPPA OPTION` | `CONTROL` | `CONNECTION` | `CONFIGURATION`.

## Architecture notes

- Single assembly `NINA.Plugins.MLAstroRPA_TPPA`, plugin display name **MLAstroRPA+TPPA**, with a
  UNIQUE PluginId (GUID) `1352D162-2E66-4F80-A05B-854F021DB913` — distinct from the standalone TPPA
  plugin (`1de8d7d3-f11e-494c-a371-95cb48dffa18`) so NINA treats them as two separate plugins and
  both can be installed side by side.
- MLAstro-origin code keeps the `MLAstro_Robotic_Polar_Alignment.*` namespaces under `MLAstroRPA-navigation\`.
- There is exactly ONE `IPluginManifest` (`PolarAlignmentPlugin`). The MLAstro options/state
  controller (`MLAstroRPA-navigation\Plugin\MLAstroController.cs`) is owned by it (`PolarAlignmentPlugin.MLAstro`).
- The serial COM port is owned by `SerialConnectionService` (MLAstro). The TPPA `MLAstroRPA`
  driver borrows it through the external-control API (`MLAstroRPA\MLAstroLink.cs` → direct calls,
  no reflection) with a direct COM-scan fallback.

## Requirements

- NINA 3.1.2.9001 or later (.NET 8 / Windows)
- Camera + plate solving (for TPPA wizard)
- MLAstro RPA controller over USB serial (for CONTROL / CONNECTION / CONFIGURATION)
- Latitude & longitude configured in NINA options (TPPA)

## Install (development)

Build the plugin (Debug/Release) — the post-build step copies the DLL to
`%LOCALAPPDATA%\NINA\Plugins\3.0.0\MLAstroRPA-TPPA\`.

```powershell
dotnet build MLAstroRPA_TPPA.csproj -c Release -tl:off
# or via the solution
dotnet build MLAstroRPA_TPPA.slnx -c Release -tl:off
```

> Uninstall any previous separate installations of the old `MLAstro Robotic Polar Alignment`
> and `Three Point Polar Alignment` plugins before using this merged plugin.

## License

MPL-2.0
