# MLAstroRPA+TPPA - Installer

This folder contains scripts and tooling to create the MSI installer for the merged
**MLAstroRPA+TPPA** N.I.N.A. plugin (`NINA.Plugins.MLAstroRPA_TPPA.dll`).

The plugin is a single assembly combining the MLAstro Robotic Polar Alignment hardware control
with the Three Point Polar Alignment wizard, and is installed to
`%LocalAppData%\NINA\Plugins\3.0.0\MLAstroRPA-TPPA`.

## Tasks (VS Code)

From the command palette (Ctrl+Shift+P > "Tasks: Run Task"):

- **dotnet: build** - build `MLAstroRPA_TPPA.csproj` (Release).
- **.NET Build MSI** - run `MSI\Release-MSI.ps1`: bumps the version (Package.wxs + csproj),
  builds the plugin + WiX installer, and copies the MSI to `Output\MLAstroRPA_TPPA_Plugin_<v>.msi`.
- **GIT: Release Repo** - no MSI build. Confirms the new MSI was already built, then creates a
  GitHub release from the newest MSI in `Output` and uploads the MSI + staged plugin DLL.

## Manual MSI build

```powershell
cd MSI
.\Release-MSI.ps1
```

Requirements: WiX Toolset (auto-installed by the script as a global dotnet tool) and the
`WixToolset.UI.wixext` / `WixToolset.Util.wixext` packages (restored via NuGet by the wixproj).

The MSI will be created in `Output\MLAstroRPA_TPPA_Plugin_<version>.msi`.

## Layout

```
Installer/
  README.md
  MSI/
    MLAstroRPA_TPPA.Installer.wixproj   # WiX project (references the plugin csproj)
    Package.wxs                          # WiX source (installs the single merged DLL)
    License.rtf                          # MPL-2.0 license shown by the installer UI
    Release-MSI.ps1                      # version bump + MSI build + optional GitHub release
    Plugin\MLAstroRPA_TPPA\              # staged plugin DLL harvested by Package.wxs
  Output\                                # produced MSI packages
```
