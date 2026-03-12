# decompile.ps1
# Decompiles CS2's Game.dll into a versioned folder, diffs patch targets against
# the previous version, and updates ./decompiled/ to the new version.
#
# Usage (from project root in any terminal):
#   pwsh ./decompile.ps1
#   pwsh ./decompile.ps1 -Force        # re-decompile even if version already exists
#   pwsh ./decompile.ps1 -DiffOnly     # skip decompile, just show diff between two latest

param(
    [string]$GamePath    = "D:\Steam\steamapps\common\Cities Skylines II",
    [string]$AppManifest = "D:\Steam\steamapps\appmanifest_949230.acf",
    [switch]$Force,
    [switch]$DiffOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$GameDll     = "$GamePath\Cities2_Data\Managed\Game.dll"
$VersionsDir = "$PSScriptRoot\decompiled-versions"
$CurrentDir  = "$PSScriptRoot\decompiled"

# ---------------------------------------------------------------------------
# The exact files CS2Hooks patches — only these are shown in the diff.
# If a file is missing after a game update that's itself a red flag.
# ---------------------------------------------------------------------------
$PatchTargets = @(
    [PSCustomObject]@{ File = "Game.Tools\NetToolSystem.cs";              Method = "Apply" }
    [PSCustomObject]@{ File = "Game.Tools\BulldozeToolSystem.cs";         Method = "Apply" }
    [PSCustomObject]@{ File = "Game.Tools\ZoneToolSystem.cs";             Method = "Apply" }
    [PSCustomObject]@{ File = "Game.Tools\ObjectToolSystem.cs";           Method = "Apply" }
    [PSCustomObject]@{ File = "Game.Tools\UpgradeToolSystem.cs";          Method = "Apply" }
    [PSCustomObject]@{ File = "Game.Simulation\CityServiceBudgetSystem.cs"; Method = "SetServiceBudget" }
    [PSCustomObject]@{ File = "Game.UI.InGame\PoliciesUISystem.cs";       Method = "SetPolicy" }
    [PSCustomObject]@{ File = "Game.UI.InGame\PoliciesUISystem.cs";       Method = "SetCityPolicy" }
)

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Write-Header($text) {
    Write-Host ""
    Write-Host "━━━ $text ━━━" -ForegroundColor Cyan
}

function Write-Ok($text)   { Write-Host "  ✓ $text" -ForegroundColor Green  }
function Write-Warn($text) { Write-Host "  ⚠ $text" -ForegroundColor Yellow }
function Write-Fail($text) { Write-Host "  ✗ $text" -ForegroundColor Red    }

# ---------------------------------------------------------------------------
# 1. Detect game version via Steam build ID
# ---------------------------------------------------------------------------
Write-Header "Detecting CS2 version"

if (-not (Test-Path $AppManifest)) {
    Write-Fail "Steam app manifest not found: $AppManifest"
    Write-Host "  Edit the -AppManifest param if your Steam library is elsewhere."
    exit 1
}

if (-not (Test-Path $GameDll)) {
    Write-Fail "Game.dll not found: $GameDll"
    Write-Host "  Edit the -GamePath param if CS2 is installed elsewhere."
    exit 1
}

$manifestContent = Get-Content $AppManifest -Raw
if ($manifestContent -match '"buildid"\s+"(\d+)"') {
    $BuildId = $Matches[1]
} else {
    Write-Fail "Could not parse buildid from app manifest."
    exit 1
}

# Also grab the Game.dll hash as a secondary fingerprint
$DllHash = (Get-FileHash $GameDll -Algorithm MD5).Hash.Substring(0, 8)
$Version  = "$BuildId-$DllHash"

Write-Ok "Steam Build ID : $BuildId"
Write-Ok "Game.dll MD5   : $DllHash  (full version key: $Version)"

$OutputDir = "$VersionsDir\$Version"

# ---------------------------------------------------------------------------
# 2. Migrate existing ./decompiled/ if this is the first run
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $VersionsDir | Out-Null

if ((Test-Path $CurrentDir) -and -not (Test-Path "$VersionsDir\*")) {
    Write-Header "First run — archiving existing decompiled/ folder"
    $unknownDir = "$VersionsDir\initial-unknown"
    Write-Host "  Moving ./decompiled/ → $unknownDir"
    Move-Item $CurrentDir $unknownDir
    Write-Warn "Existing decompiled/ saved as 'initial-unknown'. It won't be used for diffs."
}

# ---------------------------------------------------------------------------
# 3. Decompile (skip if already done for this version)
# ---------------------------------------------------------------------------
if ((Test-Path $OutputDir) -and -not $Force -and -not $DiffOnly) {
    Write-Header "Decompilation"
    Write-Ok "Version $Version already decompiled — skipping. (Use -Force to redo)"
} elseif (-not $DiffOnly) {
    Write-Header "Decompiling Game.dll"

    # Ensure ilspycmd is installed
    $ilspyCmd = Get-Command ilspycmd -ErrorAction SilentlyContinue
    if (-not $ilspyCmd) {
        Write-Host "  ilspycmd not found — installing..."
        dotnet tool install ilspycmd -g
        # Add .dotnet/tools to PATH for this session
        $env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
        $ilspyCmd = Get-Command ilspycmd -ErrorAction SilentlyContinue
        if (-not $ilspyCmd) {
            Write-Fail "ilspycmd still not found after install. Open a new terminal and retry."
            exit 1
        }
    }
    Write-Ok "ilspycmd found: $($ilspyCmd.Source)"

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    Write-Host "  Decompiling... (this takes ~30-60 seconds)"
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    ilspycmd $GameDll --project -o $OutputDir 2>&1 | Out-Null
    $timer.Stop()

    if ($LASTEXITCODE -ne 0) {
        Write-Fail "ilspycmd exited with code $LASTEXITCODE"
        exit 1
    }
    Write-Ok "Decompilation done in $([int]$timer.Elapsed.TotalSeconds)s → $OutputDir"
}

# ---------------------------------------------------------------------------
# 4. Diff patch targets vs the previous version
# ---------------------------------------------------------------------------
$allVersions = Get-ChildItem $VersionsDir -Directory | Sort-Object Name
$currentIdx  = [array]::IndexOf($allVersions.Name, $Version)

Write-Header "Diffing patch targets"

if ($currentIdx -le 0) {
    Write-Warn "No previous version to diff against — this is the baseline."
} else {
    $PrevVersion = $allVersions[$currentIdx - 1]
    Write-Host "  Comparing build $($PrevVersion.Name)  →  $Version"

    $anyChange = $false
    $seen      = @{}   # deduplicate files that appear multiple times (PoliciesUISystem)

    foreach ($target in $PatchTargets) {
        $file    = $target.File
        $method  = $target.Method
        $oldFile = "$($PrevVersion.FullName)\$file"
        $newFile = "$OutputDir\$file"
        $key     = $file

        if ($seen[$key]) { continue }
        $seen[$key] = $true

        Write-Host ""
        Write-Host "  $file" -ForegroundColor White

        if (-not (Test-Path $oldFile)) {
            Write-Warn "    File not found in previous version — may have been renamed!"
            continue
        }
        if (-not (Test-Path $newFile)) {
            Write-Fail "    File not found in new version — patch target MISSING!"
            $anyChange = $true
            continue
        }

        # git diff --no-index gives colored output and exits 1 if different
        $diffOutput = git diff --no-index -U3 $oldFile $newFile 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "    No changes"
        } else {
            $anyChange = $true
            Write-Warn "    CHANGED — check if patch still targets the right method"

            # Filter diff to show only context around the patched methods
            $methodsInFile = ($PatchTargets | Where-Object { $_.File -eq $file }).Method -join "|"
            $relevantLines = $diffOutput | Where-Object {
                $_ -match "^(@@|[+-])" -or
                $_ -match "($methodsInFile)" -or
                $_ -match "ApplyMode|applyMode"
            }

            if ($relevantLines) {
                Write-Host "    --- Relevant diff excerpt ---" -ForegroundColor DarkGray
                $relevantLines | ForEach-Object {
                    $color = if ($_ -match "^\+") { "Green" } elseif ($_ -match "^-") { "Red" } else { "DarkGray" }
                    Write-Host "    $_" -ForegroundColor $color
                }
            }

            # Also show full diff in a temp file for deeper review
            $diffFile = "$PSScriptRoot\diff_$($file -replace '\\','_').diff"
            $diffOutput | Set-Content $diffFile
            Write-Host "    Full diff saved: $diffFile" -ForegroundColor DarkGray
        }
    }

    Write-Host ""
    if ($anyChange) {
        Write-Warn "Changes detected — review diffs above and update CS2Hooks patches as needed."
    } else {
        Write-Ok "All patch targets unchanged. No mod updates required."
    }
}

# ---------------------------------------------------------------------------
# 5. Update ./decompiled/ to the new version
# ---------------------------------------------------------------------------
Write-Header "Updating ./decompiled/"
Write-Host "  Syncing from $OutputDir ..."
robocopy $OutputDir $CurrentDir /MIR /NP /NFL /NDL | Out-Null
Write-Ok "./decompiled/ is now at version $Version"

Write-Header "Done"
Write-Host "  Versions stored in: $VersionsDir"
Write-Host "  Active decompile:   $CurrentDir"
Write-Host ""
