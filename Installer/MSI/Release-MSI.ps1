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

    $existingMsi = Get-ChildItem -Path $OutputDir -Filter "$msiPrefix*.msi" -ErrorAction SilentlyContinue |
                   Sort-Object Name -Descending | Select-Object -First 1
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

# ========== VERSION PUMP ==========
if ([string]::IsNullOrWhiteSpace($Version)) {
    $existingMsi = Get-ChildItem -Path $OutputDir -Filter "$msiPrefix*.msi" -ErrorAction SilentlyContinue |
                   Sort-Object Name -Descending | Select-Object -First 1
    $currentVersion = $null

    if ($existingMsi) {
        if ($existingMsi.Name -match $msiNamePattern) {
            $currentVersion = $matches[1]
            Write-Host "Current version in Output: $currentVersion  ($($existingMsi.Name))" -ForegroundColor Green
        } else {
            Write-Host "Found in Output: $($existingMsi.Name) (cannot parse version)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "Output: no MSI yet." -ForegroundColor Yellow
    }

    # Suggest the next patch version (e.g. 2.3.0.0 -> 2.3.0.1)
    $suggested = "2.3.0.0"
    if ($currentVersion -match '^(\d+)\.(\d+)\.(\d+)$') {
        $suggested = "{0}.{1}.{2}" -f $matches[1], $matches[2], ([int]$matches[3] + 1)
    } elseif ($currentVersion -match '^(\d+)\.(\d+)\.(\d+)\.(\d+)$') {
        $suggested = "{0}.{1}.{2}.{3}" -f $matches[1], $matches[2], $matches[3], ([int]$matches[4] + 1)
    }

    do {
        $Version = Read-Host "Enter new version [default: $suggested]"
        if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $suggested }
        if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
            Write-Host "Invalid version format - use e.g. 2.3.0.1" -ForegroundColor Red
            $Version = ""
        }
    } while ([string]::IsNullOrWhiteSpace($Version))

    Write-Host "Version set to: $Version" -ForegroundColor Green
}

# ========== BUILD / SYNC PHASES (SKIPPED when -ReleaseOnly) ==========
if (-not $ReleaseOnly) {

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
