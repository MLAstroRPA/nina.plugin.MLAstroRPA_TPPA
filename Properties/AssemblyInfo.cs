using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is the PluginId (Identifier) of the merged MLAstroRPA+TPPA plugin.
// NOTE: this is a UNIQUE GUID distinct from the standalone TPPA plugin (1de8d7d3-f11e-494c-a371-95cb48dffa18)
// so NINA treats MLAstroRPA+TPPA and Three Point Polar Alignment as two separate plugins.
[assembly: Guid("1352D162-2E66-4F80-A05B-854F021DB913")]

//The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.1.2.9001")]

//Your plugin homepage - omit if not applicable
[assembly: AssemblyMetadata("Homepage", "https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA_TPPA")]
//The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
//The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
//The repository where your plugin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA_TPPA")]

[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA_TPPA/blob/main/Changelog.md")]

//Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Polar alignment,Motor Control,Hardware,Sequencer,MLAstroRPA,TPPA")]

[assembly: AssemblyMetadata("FeaturedImageURL", "https://raw.githubusercontent.com/MLAstroRPA/nina.plugin.MLAstroRPA_TPPA/main/MLAstroRPA-navigation/Resources/MLAstro_logo.png")]

//An example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "https://raw.githubusercontent.com/isbeorn/nina.plugin.polaralignment/master/PolarAlignment/Starlock2.png")]

[assembly: AssemblyMetadata("LongDescription", @"MLAstroRPA + TPPA - a single NINA plugin.

This plugin merges the complete MLAstro Robotic Polar Alignment plugin (hardware CONTROL,
CONNECTION and CONFIGURATION for the MLAstro RPA motor controller over serial) with the
Three Point Polar Alignment (TPPA) wizard, polar alignment assistant and sequencer
instruction into ONE plugin.

* CONTROL / CONNECTION / CONFIGURATION tabs: connect to the MLAstro RPA controller (ESP32) via
  serial, run the robotic polar alignment routine, configure motor drivers, soft limits,
  backlash & P.A overshoot and WiFi.
* TPPA: three point polar alignment anywhere in the sky, as a tool pane inside the imaging tab
  and as an advanced sequencer instruction.

Prerequisites
* Latitude and Longitude have to be set in NINA options.
* Camera has to be connected and ready.
* A goto mount that can move along the right ascension axis using one of three methods:
  + Fully automated - requires the mount connected via its ASCOM driver
  + Manual mode with mount connected via its ASCOM driver
  + Manual mode without the mount connected
* Plate solving must be set up.

For the MLAstroRPA hardware features a supported MLAstro RPA controller must be connected
via USB serial.")]
