#Requires -Version 5.1
<#
.SYNOPSIS
Install the NinjaExportOHLC AddOn into NinjaTrader 8.

.DESCRIPTION
End-to-end installer:
  1. Verifies NinjaTrader 8 is installed and not running.
  2. Locates required DLLs (DuckDB.NET.Data, DuckDB.NET.Bindings,
     duckdb native, System.Memory) in common NT directories. Reports
     missing ones with download links.
  3. Adds System.Numerics.dll and System.Memory.dll references to
     Documents\NinjaTrader 8\Config.xml so the DuckDB Appender API
     compiles. Backs up Config.xml first. Idempotent.
  4. Copies ExportOHLC.cs (and optionally ExportOHLC.Plexus.cs) into
     Documents\NinjaTrader 8\bin\Custom\AddOns\.
  5. Tells you to start NT and F5.

.PARAMETER SourceDir
Directory containing ExportOHLC.cs and (optionally) ExportOHLC.Plexus.cs.
Defaults to the script's own directory.

.PARAMETER NTPath
Path to the NinjaTrader 8 install.
Default: %ProgramFiles%\NinjaTrader 8

.PARAMETER NTUserPath
Path to NT's per-user data directory.
Default: %USERPROFILE%\Documents\NinjaTrader 8

.PARAMETER IncludePlexus
Also install ExportOHLC.Plexus.cs (the optional Plexus-bus integration).

.PARAMETER Force
Skip "NT is running" check and overwrite without prompting.

.EXAMPLE
PS> .\Install-NinjaExportOHLC.ps1

.EXAMPLE
PS> .\Install-NinjaExportOHLC.ps1 -IncludePlexus

.EXAMPLE
PS> .\Install-NinjaExportOHLC.ps1 -NTPath 'D:\NinjaTrader 8' -SourceDir 'C:\src\NinjaExportOHLC'
#>
[CmdletBinding()]
param(
    [string]$SourceDir   = $PSScriptRoot,
    [string]$NTPath      = "${env:ProgramFiles}\NinjaTrader 8",
    [string]$NTUserPath  = "${env:USERPROFILE}\Documents\NinjaTrader 8",
    [switch]$IncludePlexus,
    [switch]$Force
)

# ---- output helpers ---------------------------------------------------------
function Write-Step([string]$msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-OK  ([string]$msg) { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Write-Err ([string]$msg) { Write-Host "  [ERR]  $msg" -ForegroundColor Red }

$ErrorActionPreference = 'Stop'
$installFailed = $false

# ---- 1. NT installation -----------------------------------------------------
Write-Step "Verifying NinjaTrader 8 installation"

if (-not (Test-Path $NTPath)) {
    Write-Err "NT install not found at: $NTPath"
    Write-Host "       Pass -NTPath '<your install path>' if NT is elsewhere." -ForegroundColor DarkGray
    exit 1
}
Write-OK "NT install: $NTPath"

if (-not (Test-Path $NTUserPath)) {
    Write-Err "NT user data not found at: $NTUserPath"
    Write-Host "       Pass -NTUserPath '<your user data path>' if elsewhere." -ForegroundColor DarkGray
    exit 1
}
Write-OK "NT user data: $NTUserPath"

$ntProc = Get-Process NinjaTrader -ErrorAction SilentlyContinue
if ($ntProc) {
    if ($Force) {
        Write-Warn "NinjaTrader is running. Continuing because -Force was specified."
    } else {
        Write-Err "NinjaTrader is currently running."
        Write-Host "       Stop NT before installing, or pass -Force to override (not recommended)." -ForegroundColor DarkGray
        exit 1
    }
}

# ---- 2. Source files --------------------------------------------------------
Write-Step "Locating source files"

$baseFile   = Join-Path $SourceDir 'ExportOHLC.cs'
$plexusFile = Join-Path $SourceDir 'ExportOHLC.Plexus.cs'

if (-not (Test-Path $baseFile)) {
    Write-Err "ExportOHLC.cs not found in: $SourceDir"
    Write-Host "       Pass -SourceDir to specify the location." -ForegroundColor DarkGray
    exit 1
}
Write-OK "Base file: $baseFile"

if ($IncludePlexus) {
    if (-not (Test-Path $plexusFile)) {
        Write-Err "ExportOHLC.Plexus.cs not found (required by -IncludePlexus): $plexusFile"
        exit 1
    }
    Write-OK "Plexus partial: $plexusFile"
}

# ---- 3. Verify required DLLs ------------------------------------------------
Write-Step "Verifying required DLLs"

$customDir     = Join-Path $NTPath     'bin\Custom'
$ntBinDir      = Join-Path $NTPath     'bin'
$userCustomDir = Join-Path $NTUserPath 'bin\Custom'

# Locate a DLL in any of NT's typical DLL-search paths.
function Find-Dll {
    param([string]$name)
    foreach ($p in @($customDir, $ntBinDir, $userCustomDir)) {
        $candidate = Join-Path $p $name
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

# DuckDB.NET managed DLLs --- both are part of the same NuGet/release package.
$duckdbData = Find-Dll 'DuckDB.NET.Data.dll'
if ($duckdbData) {
    Write-OK "DuckDB.NET.Data.dll: $duckdbData"
} else {
    Write-Err "DuckDB.NET.Data.dll NOT FOUND"
    Write-Host "       Download:  https://github.com/Giorgi/DuckDB.NET/releases" -ForegroundColor Yellow
    Write-Host "       Place in:  $customDir" -ForegroundColor DarkGray
    $installFailed = $true
}

$duckdbBindings = Find-Dll 'DuckDB.NET.Bindings.dll'
if ($duckdbBindings) {
    Write-OK "DuckDB.NET.Bindings.dll: $duckdbBindings"
} else {
    Write-Err "DuckDB.NET.Bindings.dll NOT FOUND"
    Write-Host "       Bundled with the same release as DuckDB.NET.Data.dll above." -ForegroundColor Yellow
    Write-Host "       Place in:  $customDir" -ForegroundColor DarkGray
    $installFailed = $true
}

# DuckDB native --- the actual C engine.
$duckdbNative = Find-Dll 'duckdb.dll'
if ($duckdbNative) {
    Write-OK "duckdb.dll (native): $duckdbNative"
} else {
    Write-Err "duckdb.dll (native x64) NOT FOUND"
    Write-Host "       Download:  https://duckdb.org/docs/installation/" -ForegroundColor Yellow
    Write-Host "                  Choose: Windows / C/C++ API / x64. Inside the .zip is duckdb.dll." -ForegroundColor DarkGray
    Write-Host "       Place in:  $customDir" -ForegroundColor DarkGray
    $installFailed = $true
}

# System.Memory.dll --- needed at compile time once Config.xml references it,
# AND at runtime for any code path that touches Span<T>. Usually ships with
# the DuckDB.NET package.
$systemMemory = Find-Dll 'System.Memory.dll'
if ($systemMemory) {
    Write-OK "System.Memory.dll: $systemMemory"
} else {
    Write-Err "System.Memory.dll NOT FOUND"
    Write-Host "       Usually shipped alongside DuckDB.NET.Data.dll in the same release." -ForegroundColor Yellow
    Write-Host "       Or installable via the .NET Framework reference assemblies pack." -ForegroundColor DarkGray
    Write-Host "       Place in:  $customDir" -ForegroundColor DarkGray
    $installFailed = $true
}

# System.Numerics.dll --- always in the .NET Framework 4.x GAC; we just sanity check.
$gacNumerics = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Numerics.dll'
if (Test-Path $gacNumerics) {
    Write-OK "System.Numerics.dll (GAC): $gacNumerics"
} else {
    Write-Warn "System.Numerics.dll not at expected GAC path: $gacNumerics"
    Write-Host "       Unusual for a .NET Framework 4.x install. Verify your Windows install." -ForegroundColor DarkGray
}

if ($installFailed) {
    Write-Step "Cannot proceed: one or more required DLLs are missing"
    Write-Host "  Place the missing DLLs at the paths shown above, then re-run this script." -ForegroundColor Yellow
    exit 1
}

# ---- 4. Edit Config.xml -----------------------------------------------------
Write-Step "Updating Config.xml references"

$configPath = Join-Path $NTUserPath 'Config.xml'
if (-not (Test-Path $configPath)) {
    Write-Err "Config.xml not found at: $configPath"
    Write-Host "       Has NT been launched at least once? It creates Config.xml on first run." -ForegroundColor DarkGray
    exit 1
}

# Backup the original (only once — never clobber an existing backup).
$backupPath = "$configPath.pre-ninjaexportohlc-bak"
if (-not (Test-Path $backupPath)) {
    Copy-Item $configPath $backupPath
    Write-OK "Backed up Config.xml -> $(Split-Path $backupPath -Leaf)"
} else {
    Write-OK "Backup already exists: $(Split-Path $backupPath -Leaf)"
}

$cfgRaw  = Get-Content $configPath -Raw
$cfgOrig = $cfgRaw
$cfg     = $cfgRaw

# Add System.Numerics.dll if not present.
if ($cfg -notmatch '<string>System\.Numerics\.dll</string>') {
    $cfg = $cfg -replace '(<string>System\.Xml\.Linq\.dll</string>)', "`$1`r`n      <string>System.Numerics.dll</string>"
    if ($cfg -ne $cfgOrig) { Write-OK "Added <string>System.Numerics.dll</string>" }
} else {
    Write-OK "System.Numerics.dll already referenced"
}

# Add System.Memory.dll if not present.
if ($cfg -notmatch 'System\.Memory\.dll') {
    $cfg = $cfg -replace '(<string>\*ProgramFiles\*\\NinjaTrader 8\\bin\\Custom\\DuckDB\.NET\.Data\.dll</string>)', "`$1`r`n      <string>*ProgramFiles*\NinjaTrader 8\bin\Custom\System.Memory.dll</string>"
    Write-OK "Added <string>...\System.Memory.dll</string>"
} else {
    Write-OK "System.Memory.dll already referenced"
}

if ($cfg -ne $cfgOrig) {
    # Preserve original encoding (UTF-8 no BOM) — NT writes Config.xml that way.
    [System.IO.File]::WriteAllText($configPath, $cfg, (New-Object System.Text.UTF8Encoding $false))
    Write-OK "Config.xml updated"
} else {
    Write-OK "Config.xml unchanged (already configured)"
}

# ---- 5. Copy AddOn files ----------------------------------------------------
Write-Step "Copying AddOn files"

$addOnsDir = Join-Path $NTUserPath 'bin\Custom\AddOns'
if (-not (Test-Path $addOnsDir)) {
    Write-Err "AddOns directory not found: $addOnsDir"
    Write-Host "       NT install seems incomplete. Run NT once to initialize, then re-run this script." -ForegroundColor DarkGray
    exit 1
}

# Remove obsolete ExportOHLCTransfer.cs (pre-1.9.0 layout). Its content is
# now in ExportOHLC.Plexus.cs.
$obsoleteTransfer = Join-Path $addOnsDir 'ExportOHLCTransfer.cs'
if (Test-Path $obsoleteTransfer) {
    Remove-Item $obsoleteTransfer -Force
    Write-OK "Removed obsolete ExportOHLCTransfer.cs"
}

Copy-Item $baseFile (Join-Path $addOnsDir 'ExportOHLC.cs') -Force
Write-OK "Installed: ExportOHLC.cs"

if ($IncludePlexus) {
    Copy-Item $plexusFile (Join-Path $addOnsDir 'ExportOHLC.Plexus.cs') -Force
    Write-OK "Installed: ExportOHLC.Plexus.cs"
} else {
    $stalePlexus = Join-Path $addOnsDir 'ExportOHLC.Plexus.cs'
    if (Test-Path $stalePlexus) {
        Write-Warn "ExportOHLC.Plexus.cs already at destination. Pass -IncludePlexus to refresh it,"
        Write-Warn "  or delete it manually if you no longer want Plexus integration."
    }
}

# ---- Done -------------------------------------------------------------------
Write-Step "Installation complete"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Start NinjaTrader 8"
Write-Host "  2. Tools -> NinjaScript Editor -> F5 (Compile)"
Write-Host "  3. Control Center -> New -> 'Export OHLC History (All Contracts)'"
Write-Host ""
Write-Host "If F5 fails with CS0246 (DuckDBConnection) or CS0012 (Span/BigInteger):" -ForegroundColor DarkGray
Write-Host "  Close NT entirely, then re-run this script to re-apply Config.xml changes." -ForegroundColor DarkGray
Write-Host ""
