# MLAstroRPA+TPPA - MSI Build Script
# Creates the MSI installer (single merged plugin) using WiX Toolset v6

param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [switch]$CreateRelease,
    [switch]$ReleaseOnly,
    [string]$Repo = ""
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$MSIProjectDir = $ScriptDir
$OutputDir = Join-Path $MSIProjectDir "Output"
$PackageWxs = Join-Path $MSIProjectDir "Package.wxs"
$PluginCsproj = Join-Path $ProjectRoot "MLAstroRPA_TPPA.csproj"

# MSI output filename convention, e.g. MLAstroRPA_TPPA_Plugin_2.3.0.1.msi
$msiPrefix = "MLAstroRPA_TPPA_Plugin"
$msiNamePattern = 'MLAstroRPA_TPPA_Plugin_(\d+\.\d+\.\d+(\.\d+)?)\.msi'

# Returns the MSI in $OutputDir with the HIGHEST NUMERIC version. Do NOT sort by Name:
# a string sort mis-ranks e.g. 2.0.0.10 BELOW 2.0.0.9 ('1' < '9' at the 7th char),
# which would make an already-built .10 never be detected as the newest.
# Files that don't match the naming convention sort as 0.0.0.0 so they never win.
function Get-NewestMsi {
    Get-ChildItem -Path $OutputDir -Filter "$msiPrefix*.msi" -ErrorAction SilentlyContinue |
        Sort-Object -Property @{ Expression = {
            if ($_.Name -match $msiNamePattern) { [version]$matches[1] } else { [version]"0.0.0.0" }
        } } -Descending |
        Select-Object -First 1
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "MLAstroRPA+TPPA Plugin - MSI Builder" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# ========== RELEASE-ONLY MODE (no MSI build) ==========
# The "GIT: Release Repo" task runs with -CreateRelease -ReleaseOnly. It does NOT build the MSI:
# it asks for confirmation that the new-version MSI was already built (e.g. via ".NET Build MSI")
# and then creates the GitHub release from the NEWEST MSI currently present in Output.
if ($ReleaseOnly) {
    if (-not $CreateRelease) {
        Write-Host "ERROR: -ReleaseOnly can only be used together with -CreateRelease." -ForegroundColor Red
        exit 1
    }

    $existingMsi = Get-NewestMsi
    if (-not $existingMsi -or $existingMsi.Name -notmatch $msiNamePattern) {
        Write-Host "ERROR: No MSI found in Output ($OutputDir). Build the new version first with '.NET Build MSI'." -ForegroundColor Red
        exit 1
    }

    $Version = $matches[1]
    $msiDest = $existingMsi.FullName
    Write-Host "Release-only mode - MSI build is SKIPPED." -ForegroundColor Yellow
    Write-Host "Newest MSI in Output: v$Version ($(Split-Path $msiDest -Leaf))" -ForegroundColor Green
    Write-Host ""
}

# ========== VERSION RESOLUTION ==========
# Version is decided BEFORE the build — by the `version-pump` skill (or manually),
# which stamps it into the plugin csproj (<Version>/<AssemblyVersion>/...) and adds the
# matching Changelog.md entry. This script NO LONGER bumps/chooses a version while
# building the MSI; it only reads the version already set in the csproj.
# (-ReleaseOnly above already resolved $Version from the newest MSI in Output, so this
# block is skipped in that mode.)
if ([string]::IsNullOrWhiteSpace($Version)) {
    if (Test-Path $PluginCsproj) {
        $csprojContent = [System.IO.File]::ReadAllText($PluginCsproj)
        $verMatch = [regex]::Match($csprojContent, '<Version>([^<]+)</Version>')
        if ($verMatch.Success -and $verMatch.Groups[1].Value -match '^\d+\.\d+\.\d+(\.\d+)?$') {
            $Version = $verMatch.Groups[1].Value
            Write-Host "Version resolved from plugin csproj: $Version" -ForegroundColor Green
        }
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        Write-Host "ERROR: No -Version given and no valid <Version> found in the plugin csproj ($PluginCsproj)." -ForegroundColor Red
        Write-Host "Set the version first (e.g. via the version-pump skill) or pass -Version." -ForegroundColor Yellow
        exit 1
    }
}

# ========== BUILD / SYNC PHASES (SKIPPED when -ReleaseOnly) ==========
if (-not $ReleaseOnly) {

    # ========== VERSION MISMATCH CHECK ==========
    # Compare the version about to be built with the top Changelog.md entry. If they differ,
    # the version was probably not pumped (version-pump skill) - pause and ask to continue/abort.
    $changelogPath = Join-Path $ProjectRoot "Changelog.md"
    $clVer = $null
    if (Test-Path $changelogPath) {
        foreach ($clLine in Get-Content $changelogPath) {
            if ($clLine -match '^##\s*\[?(\d+\.\d+\.\d+(\.\d+)?)') {
                $clVer = $matches[1]
                break
            }
        }
    }
    if (-not $clVer) {
        Write-Host "WARNING: Could not parse the top Changelog.md version - cannot verify the pump." -ForegroundColor Yellow
    } elseif ($clVer -ne $Version) {
        Write-Host "WARNING: csproj version $Version does NOT match the top Changelog.md entry $clVer." -ForegroundColor Yellow
        Write-Host "You may have forgotten to bump the version (version-pump skill)." -ForegroundColor Yellow
        $confirm = Read-Host "Continue building the MSI at $Version anyway? (y/N)"
        if ($confirm -notmatch '^[yY]$') {
            Write-Host "Aborted - run the version-pump skill first, then build again." -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "Version check OK - csproj ($Version) matches the top Changelog entry." -ForegroundColor Green
    }

    # Sync the ProductVersion define in Package.wxs with the chosen version
    $wxsContent = [System.IO.File]::ReadAllText($PackageWxs)
    if ($wxsContent -match '<\?define ProductVersion = "[^"]*" \?>') {
        $wxsContent = $wxsContent -replace '<\?define ProductVersion = "[^"]*" \?>', "<?define ProductVersion = `"$Version`" ?>"
        [System.IO.File]::WriteAllText($PackageWxs, $wxsContent, (New-Object System.Text.UTF8Encoding $false))
        Write-Host "Package.wxs ProductVersion updated to $Version" -ForegroundColor Green
    } else {
        Write-Host "WARNING: Could not find ProductVersion define in Package.wxs" -ForegroundColor Yellow
    }

    # ========== PLUGIN PROJECT VERSION SYNC ==========
    # Stamp the new version into the merged plugin csproj so the built DLL matches the MSI.
    if (Test-Path $PluginCsproj) {
        # AssemblyVersion/FileVersion need 4 parts; pad a 3-part version (e.g. 2.3.1) with ".0"
        $fourPart = if ($Version -match '^\d+\.\d+\.\d+\.\d+$') { $Version } else { "$Version.0" }

        $csprojContent = [System.IO.File]::ReadAllText($PluginCsproj)
        $versionTagPattern = '<(?<tag>Version|AssemblyVersion|FileVersion|InformationalVersion)>[^<]*</\k<tag>>'
        $matchCount = [regex]::Matches($csprojContent, $versionTagPattern).Count
        if ($matchCount -gt 0) {
            $updatedContent = [regex]::Replace(
                $csprojContent,
                $versionTagPattern,
                { param($m) "<$($m.Groups['tag'].Value)>$fourPart</$($m.Groups['tag'].Value)>" })
            if ($updatedContent -ne $csprojContent) {
                [System.IO.File]::WriteAllText($PluginCsproj, $updatedContent, (New-Object System.Text.UTF8Encoding $false))
                Write-Host "Plugin csproj version updated to ${fourPart}: $PluginCsproj" -ForegroundColor Green
            } else {
                Write-Host "Plugin csproj already at version ${fourPart} - nothing to change." -ForegroundColor Green
            }
        } else {
            Write-Host "WARNING: Could not find version tags in plugin csproj" -ForegroundColor Yellow
        }
    } else {
        Write-Host "WARNING: Plugin project not found at $PluginCsproj - skipping plugin version sync." -ForegroundColor Yellow
    }

    Write-Host ""

    # Check for WiX Toolset
    Write-Host "Checking for WiX Toolset..." -ForegroundColor Yellow
    $wixInstalled = $false
    try {
        $wixCheck = dotnet tool list -g | Select-String "wix"
        if ($wixCheck) {
            $wixInstalled = $true
            Write-Host "WiX Toolset found (global tool)" -ForegroundColor Green
        }
    } catch {}

    if (-not $wixInstalled) {
        Write-Host "WiX Toolset not found. Installing..." -ForegroundColor Yellow
        dotnet tool install --global wix
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Failed to install WiX Toolset" -ForegroundColor Red
            exit 1
        }
        Write-Host "WiX Toolset installed!" -ForegroundColor Green
    }

    # ========== BUILD MSI ==========
    # Building the wixproj builds the referenced plugin (Release) which stages its DLL into
    # Installer\MSI\Plugin\MLAstroRPA_TPPA (RefreshInstallerPluginDll target), then compiles
    # Package.wxs which harvests that staged DLL.
    Write-Host ""
    Write-Host "Building MSI package..." -ForegroundColor Yellow

    Push-Location $MSIProjectDir
    try {
        dotnet build -c $Configuration -p:Version=$Version --verbosity minimal -tl:off
        if ($LASTEXITCODE -ne 0) {
            Write-Host "MSI build failed!" -ForegroundColor Red
            exit 1
        }
        Write-Host "MSI build successful!" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }

    # ========== COPY MSI TO OUTPUT ==========
    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }

    $msiSource = Get-ChildItem -Path "$MSIProjectDir\bin\$Configuration" -Filter "*.msi" -Recurse | Select-Object -First 1
    if ($msiSource) {
        $msiDest = Join-Path $OutputDir "$msiPrefix`_$Version.msi"
        Copy-Item -Path $msiSource.FullName -Destination $msiDest -Force

        Write-Host ""
        Write-Host "============================================" -ForegroundColor Cyan
        Write-Host "MSI BUILD COMPLETE!" -ForegroundColor Green
        Write-Host "============================================" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "MSI location:" -ForegroundColor White
        Write-Host "  $msiDest" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Size: $([math]::Round((Get-Item $msiDest).Length / 1KB, 2)) KB" -ForegroundColor Gray
    } else {
        Write-Host "ERROR: MSI file not found in build output!" -ForegroundColor Red
        Write-Host "Check: $MSIProjectDir\bin\$Configuration" -ForegroundColor Yellow
        exit 1
    }
}  # ========== end of BUILD / SYNC PHASES (skipped when -ReleaseOnly) ==========

# ========== GITHUB RELEASE (optional, enabled with -CreateRelease) ==========
# Creates a new GitHub release v<version> on the repo and uploads:
#   - the MSI
#   - the staged plugin DLL (Installer\MSI\Plugin\MLAstroRPA_TPPA)
if ($CreateRelease) {
    Write-Host ""
    Write-Host "============================================" -ForegroundColor Magenta
    Write-Host "GITHUB RELEASE" -ForegroundColor Magenta
    Write-Host "============================================" -ForegroundColor Magenta

    $tag = "v$Version"

    # Locate the GitHub CLI (gh). It may not be on PATH when VS Code was started before the
    # install, so also probe the standard install locations and use the full path.
    $ghExe = "gh"
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) {
        $ghPath = @(
            "C:\Program Files\GitHub CLI\gh.exe",
            "C:\Program Files (x86)\GitHub CLI\gh.exe",
            "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe",
            "$env:USERPROFILE\scoop\shims\gh.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($ghPath) {
            $ghExe = $ghPath
        } else {
            Write-Host "ERROR: GitHub CLI (gh) not found. Install from https://cli.github.com/ and run 'gh auth login'." -ForegroundColor Red
            exit 1
        }
    }

    & $ghExe auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Not authenticated with GitHub. Run 'gh auth login' first." -ForegroundColor Red
        exit 1
    }

    # Auto-detect the target repo from the git remote
    $detectedRepo = ""
    $remoteUrl = git remote get-url origin 2>&1 | Select-Object -First 1
    if ($remoteUrl -match 'github\.com[/:]([^/]+/[^/]+?)(\.git)?$') {
        $detectedRepo = $matches[1]
    }

    if ([string]::IsNullOrWhiteSpace($Repo)) {
        $Repo = $detectedRepo
    }

    if ([string]::IsNullOrWhiteSpace($Repo)) {
        Write-Host "ERROR: Cannot determine the GitHub repo. Provide -Repo or make sure the git remote 'origin' is set." -ForegroundColor Red
        exit 1
    }

    if (-not [string]::IsNullOrWhiteSpace($detectedRepo) -and $detectedRepo -ne $Repo) {
        Write-Host "WARNING: -Repo ($Repo) differs from the git remote ($detectedRepo)." -ForegroundColor Yellow
    }

    # Always confirm before publishing (guards against releasing to the wrong repo/version).
    # In release-only mode the confirm states that the shown version is the newest in Output.
    Write-Host ""
    Write-Host "Target GitHub repo: $Repo" -ForegroundColor Magenta
    if ($ReleaseOnly) {
        Write-Host "Version $Version is the newest MSI. Do you want to release it to GitHub ('$Repo')? (y/N)" -ForegroundColor Yellow -NoNewline
        $confirm = Read-Host
    } else {
        Write-Host "Create release v$Version on '$Repo'? (y/N)" -ForegroundColor Yellow -NoNewline
        $confirm = Read-Host
    }
    if ($confirm -notmatch '^[yY]$') {
        Write-Host "Aborted by user." -ForegroundColor Yellow
        exit 0
    }

    # Warn if there are uncommitted changes (release points to the latest commit)
    $dirty = git status --porcelain 2>&1
    if ($dirty) {
        Write-Host "WARNING: There are uncommitted changes - the release will point to the latest commit." -ForegroundColor Yellow
    }

    # Assemble assets: the MSI + the staged plugin DLL
    $pluginDir = Join-Path $MSIProjectDir "Plugin"
    $assets = New-Object System.Collections.Generic.List[string]
    $assets.Add($msiDest)
    $candidate = Join-Path $pluginDir "MLAstroRPA_TPPA\NINA.Plugins.MLAstroRPA_TPPA.dll"
    if (Test-Path $candidate) {
        $assets.Add($candidate)
    } else {
        Write-Host "WARNING: Asset not found, skipping: $candidate" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "Creating GitHub release: $tag  (repo: $Repo)" -ForegroundColor Yellow
    $notes = "Release v$Version`n`nView README.md to know how to install.`n`n`"NINA.Plugins.MLAstroRPA_TPPA.dll`" is the merged MLAstroRPA+TPPA plugin (MLAstro hardware control + Three Point Polar Alignment)."

    $createOut = & $ghExe release create $tag --repo $Repo --title $tag --notes $notes 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: gh release create failed:" -ForegroundColor Red
        $createOut | ForEach-Object { Write-Host "  $_" }
        Write-Host "Tip: if the tag already exists, use a new version or delete the old release/tag first." -ForegroundColor Yellow
        exit 1
    }
    $createOut | ForEach-Object { Write-Host "  $_" }

    Write-Host ""
    Write-Host "Uploading $($assets.Count) asset(s)..." -ForegroundColor Yellow
    foreach ($asset in $assets) {
        Write-Host "  Uploading: $(Split-Path $asset -Leaf)" -ForegroundColor Gray
        $uploadOut = & $ghExe release upload $tag $asset --repo $Repo --clobber 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ERROR: upload failed for $asset" -ForegroundColor Red
            $uploadOut | ForEach-Object { Write-Host "    $_" }
        }
    }

    Write-Host ""
    Write-Host "============================================" -ForegroundColor Green
    Write-Host "GITHUB RELEASE COMPLETE: $tag" -ForegroundColor Green
    Write-Host "============================================" -ForegroundColor Green
    Write-Host "Release: https://github.com/$Repo/releases/tag/$tag"
    Write-Host ""
    Write-Host "SHA256:"
    foreach ($asset in $assets) {
        $hash = (Get-FileHash -Algorithm SHA256 -Path $asset).Hash.ToLowerInvariant()
        Write-Host "  $(Split-Path $asset -Leaf): $hash"
    }
}
