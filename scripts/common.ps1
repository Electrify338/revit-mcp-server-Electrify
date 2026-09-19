#Requires -Version 5.1
<#
.SYNOPSIS
    Shared constants and helper functions for mcp-servers-for-revit scripts.

.DESCRIPTION
    Dot-source this file from install.ps1, diagnose.ps1, and fix-mcp.ps1.
    Contains common logic for Revit detection, Node.js checks, Claude Desktop
    detection, npm package resolution, and config file management.

    Usage:  . "$PSScriptRoot\common.ps1"
#>

# -- Constants -----------------------------------------------------------------
$script:REPO           = 'Electrify338/revit-mcp-server-Electrify'
$script:PLUGIN_NAME    = 'mcp-servers-for-revit'
$script:PLUGIN_FOLDER  = 'revit_mcp_plugin'
$script:NPM_PACKAGE    = 'mcp-server-for-revit'
$script:ADDIN_FILE     = "$PLUGIN_NAME.addin"
$script:MIN_NODE       = 18
$script:REVIT_YEARS    = 2023..2027
$script:MCP_HOST       = '127.0.0.1'
$script:MCP_PORT       = 8080

# ==============================================================================
# Revit Detection
# ==============================================================================

<#
.SYNOPSIS
    Detect installed Revit versions by checking registry, Addins folder, and exe.

.PARAMETER Limit
    Optional array of year strings to restrict detection (e.g. @('2025')).

.OUTPUTS
    Array of PSCustomObject with Year and AddinsDir properties.
#>
function Get-RevitVersions {
    param([string[]]$Limit = @())
    $found = @()
    foreach ($year in $REVIT_YEARS) {
        if ($Limit.Count -gt 0 -and $year.ToString() -notin $Limit) { continue }
        $addinsDir = "$env:APPDATA\Autodesk\Revit\Addins\$year"
        $regPaths  = @(
            "HKLM:\SOFTWARE\Autodesk\Revit\Autodesk Revit $year",
            "HKLM:\SOFTWARE\WOW6432Node\Autodesk\Revit\Autodesk Revit $year"
        )
        $exePath = "C:\Program Files\Autodesk\Revit $year\Revit.exe"
        $inRegistry = ($regPaths | Where-Object { Test-Path $_ }).Count -gt 0
        $inAddins   = Test-Path $addinsDir
        $inExe      = Test-Path $exePath
        if ($inRegistry -or $inAddins -or $inExe) {
            $found += [PSCustomObject]@{ Year = $year; AddinsDir = $addinsDir }
        }
    }
    return $found
}

<#
.SYNOPSIS
    Test whether a specific Revit year is detected on this machine.

.PARAMETER Year
    Revit year (e.g. 2025).

.OUTPUTS
    Boolean.
#>
function Test-RevitInstalled {
    param([int]$Year)
    $addinsDir = "$env:APPDATA\Autodesk\Revit\Addins\$Year"
    $regPaths  = @(
        "HKLM:\SOFTWARE\Autodesk\Revit\Autodesk Revit $Year",
        "HKLM:\SOFTWARE\WOW6432Node\Autodesk\Revit\Autodesk Revit $Year"
    )
    $exePath   = "C:\Program Files\Autodesk\Revit $Year\Revit.exe"
    $inReg     = ($regPaths | Where-Object { Test-Path $_ }).Count -gt 0
    $inExe     = Test-Path $exePath
    $inAddins  = Test-Path $addinsDir
    return ($inReg -or $inExe -or $inAddins)
}

# ==============================================================================
# Node.js Detection
# ==============================================================================

<#
.SYNOPSIS
    Find the best available Node.js executable.
    Checks the system PATH first, then falls back to the portable node.exe
    bundled alongside the plugin (shipped in server/runtime/node.exe).

.OUTPUTS
    String full path to node.exe, or $null if not found anywhere.
#>
function Get-NodePath {
    # 1. System node (already in PATH — preferred for developers)
    $sysNode = Get-Command node -ErrorAction SilentlyContinue
    if ($sysNode) { return $sysNode.Source }

    # 2. Portable node.exe bundled with the plugin (for end-users without Node.js)
    foreach ($year in ($REVIT_YEARS | Sort-Object -Descending)) {
        $portableNode = "$env:APPDATA\Autodesk\Revit\Addins\$year\$PLUGIN_FOLDER\Commands\RevitMCPCommandSet\server\runtime\node.exe"
        if (Test-Path $portableNode) { return $portableNode }
    }

    return $null
}

<#
.SYNOPSIS
    Check if Node.js is installed and meets the minimum version requirement.
    Checks system PATH first, then the portable runtime bundled with the plugin.

.OUTPUTS
    PSCustomObject with properties: Available (bool), Version (string),
    Major (int), Path (string), MeetsMinimum (bool), IsBundled (bool).
#>
function Get-NodeStatus {
    $result = [PSCustomObject]@{
        Available    = $false
        Version      = $null
        Major        = 0
        Path         = $null
        MeetsMinimum = $false
        IsBundled    = $false
    }
    $nodePath = Get-NodePath
    if ($nodePath) {
        $result.Available = $true
        $result.Path      = $nodePath
        $result.IsBundled = -not [bool](Get-Command node -ErrorAction SilentlyContinue)
        $result.Version   = (& "$nodePath" --version 2>$null).TrimStart('v')
        $result.Major     = [int](($result.Version -split '\.')[0])
        $result.MeetsMinimum = $result.Major -ge $MIN_NODE
    }
    return $result
}

# ==============================================================================
# npm Package Resolution
# ==============================================================================

<#
.SYNOPSIS
    Get the npm global prefix directory.

.OUTPUTS
    String path or $null.
#>
function Get-NpmGlobalPrefix {
    $prefix = (cmd /c "npm prefix -g" 2>$null | Select-Object -First 1)
    if ($prefix) { $prefix = $prefix.Trim() }
    if ([string]::IsNullOrWhiteSpace($prefix)) { return $null }
    return $prefix
}

<#
.SYNOPSIS
    Resolve the .cmd file path for a globally installed npm package.

.PARAMETER PkgName
    The npm package name (e.g. 'mcp-server-for-revit').

.OUTPUTS
    String path to the .cmd file, or $null if not found.
#>
function Get-NpmCmdPath {
    param([string]$PkgName)
    $prefix = Get-NpmGlobalPrefix
    if (-not $prefix) { return $null }
    $p = Join-Path $prefix "$PkgName.cmd"
    if (Test-Path $p) { return $p }
    return $null
}

# ==============================================================================
# Claude Desktop Detection
# ==============================================================================

<#
.SYNOPSIS
    Find the Claude Desktop configuration directory (standard or MSIX install).

.OUTPUTS
    String path to the Claude directory, or $null if not found.
#>
function Get-ClaudeDesktopDir {
    $candidates = @(
        "$env:APPDATA\Claude",
        (Get-ChildItem "$env:LOCALAPPDATA\Packages" -Filter "Claude_*" -ErrorAction SilentlyContinue |
            Select-Object -First 1 |
            ForEach-Object { "$($_.FullName)\LocalCache\Roaming\Claude" })
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

<#
.SYNOPSIS
    Get all Claude Desktop candidate paths (for diagnostic display).

.OUTPUTS
    Array of strings (some may be $null if MSIX is not present).
#>
function Get-ClaudeDesktopCandidates {
    return @(
        "$env:APPDATA\Claude",
        (Get-ChildItem "$env:LOCALAPPDATA\Packages" -Filter "Claude_*" -ErrorAction SilentlyContinue |
            Select-Object -First 1 |
            ForEach-Object { "$($_.FullName)\LocalCache\Roaming\Claude" })
    )
}

# ==============================================================================
# Claude Desktop Config Management
# ==============================================================================

<#
.SYNOPSIS
    Read and parse the claude_desktop_config.json file.

.PARAMETER ClaudeDir
    Path to the Claude Desktop directory.

.OUTPUTS
    PSCustomObject with properties: Exists (bool), Path (string),
    Config (parsed object or $null), HasRevitMcp (bool),
    RevitMcpEntry (object or $null).
#>
# ==============================================================================
# MCP Server (local installation)
# ==============================================================================

<#
.SYNOPSIS
    Find the path to the locally installed MCP server (index.js).
    Searches all Revit versions from newest to oldest and returns the first found.

.OUTPUTS
    String path to index.js, or $null if not installed.
#>
function Get-McpServerPath {
    foreach ($year in ($REVIT_YEARS | Sort-Object -Descending)) {
        $serverJs = "$env:APPDATA\Autodesk\Revit\Addins\$year\$PLUGIN_FOLDER\Commands\RevitMCPCommandSet\server\build\index.js"
        if (Test-Path $serverJs) { return $serverJs }
    }
    return $null
}

<#
.SYNOPSIS
    Build the Claude Desktop mcpServers entry for revit-mcp using the local server.

.PARAMETER ServerPath
    Full path to the server index.js file.

.OUTPUTS
    PSCustomObject suitable for JSON serialisation.
#>
function New-RevitMcpEntry {
    param([string]$ServerPath)
    $nodePath = Get-NodePath
    if ($nodePath) {
        # Use the node executable directly (system or bundled portable)
        return [PSCustomObject]@{ command = $nodePath; args = @($ServerPath) }
    }
    # Last resort: rely on node in PATH via cmd shell
    return [PSCustomObject]@{ command = 'cmd'; args = @('/c', 'node', $ServerPath) }
}

function Get-ClaudeDesktopConfig {
    param([string]$ClaudeDir)
    $configPath = "$ClaudeDir\claude_desktop_config.json"
    $result = [PSCustomObject]@{
        Exists        = $false
        Path          = $configPath
        Config        = $null
        HasRevitMcp   = $false
        RevitMcpEntry = $null
    }
    if (Test-Path $configPath) {
        $result.Exists = $true
        try {
            $result.Config = Get-Content $configPath -Raw | ConvertFrom-Json
            if ($result.Config.mcpServers -and $result.Config.mcpServers.'revit-mcp') {
                $result.HasRevitMcp   = $true
                $result.RevitMcpEntry = $result.Config.mcpServers.'revit-mcp'
            }
        } catch {
            # Config exists but cannot be parsed; caller handles this
        }
    }
    return $result
}

# ==============================================================================
# AI client configuration (Claude Desktop, Codex, Antigravity, Cursor, ...)
# ==============================================================================
# The Revit plugin does the same job itself at every Revit start (see
# plugin/Utils/McpClientConfigurator.cs). These functions let the installer
# and fix-mcp.ps1 do it up front. Claude Code (~/.claude.json) and Gemini CLI
# (~/.gemini/settings.json) are deliberately left to the plugin: those files
# are large and full of timestamps, and Windows PowerShell 5.1's JSON cmdlets
# rewrite ISO dates as "\/Date(...)\/", which would corrupt them.

<#
.SYNOPSIS
    Every AI client this script knows, with its config file and whether it
    looks installed on this machine.
#>
function Get-McpClientTargets {
    $userHome = $env:USERPROFILE
    $claudeDir = Get-ClaudeDesktopDir
    if (-not $claudeDir) { $claudeDir = "$env:APPDATA\Claude" }
    return @(
        [PSCustomObject]@{ Name = 'Claude Desktop';     Layout = 'mcpServers'; ConfigPath = "$claudeDir\claude_desktop_config.json";           Detected = $true }
        [PSCustomObject]@{ Name = 'Codex';              Layout = 'toml';       ConfigPath = "$userHome\.codex\config.toml";                      Detected = (Test-Path "$userHome\.codex") }
        [PSCustomObject]@{ Name = 'Antigravity';        Layout = 'mcpServers'; ConfigPath = "$userHome\.gemini\config\mcp_config.json";          Detected = (Test-Path "$userHome\.gemini\config") }
        [PSCustomObject]@{ Name = 'Antigravity (1.x)';  Layout = 'mcpServers'; ConfigPath = "$userHome\.gemini\antigravity\mcp_config.json";     Detected = (Test-Path "$userHome\.gemini\antigravity") }
        [PSCustomObject]@{ Name = 'Cursor';             Layout = 'mcpServers'; ConfigPath = "$userHome\.cursor\mcp.json";                        Detected = (Test-Path "$userHome\.cursor") }
        [PSCustomObject]@{ Name = 'VS Code';            Layout = 'servers';    ConfigPath = "$env:APPDATA\Code\User\mcp.json";                   Detected = (Test-Path "$env:APPDATA\Code\User") }
        [PSCustomObject]@{ Name = 'Windsurf';           Layout = 'mcpServers'; ConfigPath = "$userHome\.codeium\windsurf\mcp_config.json";       Detected = (Test-Path "$userHome\.codeium\windsurf") }
    )
}

function ConvertTo-TomlString { param([string]$s) return '"' + $s.Replace('\', '\\').Replace('"', '\"') + '"' }
function ConvertFrom-TomlString { param([string]$s) return $s.Replace('\"', '"').Replace('\\', '\') }

function Backup-McpConfigFile {
    param([string]$Path)
    if (Test-Path $Path) {
        $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        Copy-Item $Path "$Path.$stamp.bak" -Force -ErrorAction SilentlyContinue
    }
}

function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $false))
}

<#
.SYNOPSIS
    Add or fix the revit-mcp entry in a JSON client config. Returns $true when
    the file was written, $false when it was already correct.
#>
function Set-McpJsonEntry {
    param([string]$Path, [string]$Key, [string]$NodePath, [string]$ServerPath, [switch]$IncludeType)
    $config = $null
    if (Test-Path $Path) {
        $raw = Get-Content $Path -Raw
        if ($raw -and $raw.Trim()) {
            try { $config = $raw | ConvertFrom-Json }
            catch {
                $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
                Copy-Item $Path "$Path.corrupted.$stamp.bak" -Force
                $config = $null
            }
        }
    }
    if (-not $config) { $config = [PSCustomObject]@{} }

    $servers = $config.$Key
    if (-not $servers) {
        $servers = [PSCustomObject]@{}
        $config | Add-Member -NotePropertyName $Key -NotePropertyValue $servers -Force
    }

    $existing = $servers.'revit-mcp'
    if ($existing -and $existing.command -eq $NodePath -and $existing.args -and @($existing.args)[0] -eq $ServerPath) {
        return $false
    }

    $entry = [ordered]@{ command = $NodePath; args = @($ServerPath) }
    if ($IncludeType) { $entry['type'] = 'stdio' }
    if ($existing) {
        # Keep anything else the user put on the entry (env, disabled, ...).
        foreach ($p in $existing.PSObject.Properties) {
            if (-not $entry.Contains($p.Name)) { $entry[$p.Name] = $p.Value }
        }
    }
    $servers | Add-Member -NotePropertyName 'revit-mcp' -NotePropertyValue ([PSCustomObject]$entry) -Force

    Backup-McpConfigFile $Path
    Write-Utf8NoBom -Path $Path -Text ($config | ConvertTo-Json -Depth 100)
    # Validate what we wrote.
    $null = Get-Content $Path -Raw | ConvertFrom-Json
    return $true
}

<#
.SYNOPSIS
    Add or fix [mcp_servers.revit-mcp] in Codex's config.toml. Returns $true
    when the file was written.
#>
function Set-McpTomlEntry {
    param([string]$Path, [string]$NodePath, [string]$ServerPath)
    $lines = New-Object 'System.Collections.Generic.List[string]'
    if (Test-Path $Path) {
        $text = Get-Content $Path -Raw
        if ($text) { foreach ($l in (($text -replace "`r`n", "`n") -split "`n")) { $lines.Add($l) } }
    }

    $header = '^\s*\[\s*mcp_servers\s*\.\s*(?:"revit-mcp"|revit-mcp)\s*\]\s*(#.*)?$'
    $sub    = '^\s*\[\s*mcp_servers\s*\.\s*(?:"revit-mcp"|revit-mcp)\s*\.'
    $start = -1; $end = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $header) {
            $start = $i; $end = $lines.Count
            for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                if ($lines[$j] -match '^\s*\[' -and $lines[$j] -notmatch $sub) { $end = $j; break }
            }
            break
        }
    }

    $block = [string[]]@(
        '[mcp_servers.revit-mcp]',
        "command = $(ConvertTo-TomlString $NodePath)",
        "args = [$(ConvertTo-TomlString $ServerPath)]"
    )

    if ($start -ge 0) {
        $cmd = $null; $arg = $null
        for ($i = $start + 1; $i -lt $end; $i++) {
            if ($lines[$i] -match '^\s*command\s*=\s*"((?:[^"\\]|\\.)*)"') { $cmd = ConvertFrom-TomlString $Matches[1] }
            if ($lines[$i] -match '^\s*args\s*=\s*\[\s*"((?:[^"\\]|\\.)*)"')  { $arg = ConvertFrom-TomlString $Matches[1] }
        }
        if ($cmd -eq $NodePath -and $arg -eq $ServerPath) { return $false }
        $lines.RemoveRange($start, $end - $start)
        $lines.InsertRange($start, $block)
    } else {
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Trim()) { $lines.Add('') }
        $lines.AddRange($block)
    }

    Backup-McpConfigFile $Path
    Write-Utf8NoBom -Path $Path -Text (($lines -join "`r`n") + "`r`n")
    return $true
}

<#
.SYNOPSIS
    Register the local Revit MCP server with every detected AI client.

.OUTPUTS
    One object per client: Name, ConfigPath, Detected, Changed, Error.
#>
function Set-McpClientConfigs {
    param([string]$NodePath, [string]$ServerPath)
    $results = @()
    foreach ($t in Get-McpClientTargets) {
        $r = [PSCustomObject]@{ Name = $t.Name; ConfigPath = $t.ConfigPath; Detected = $t.Detected; Changed = $false; Error = $null }
        $results += $r
        if (-not $t.Detected) { continue }
        try {
            if ($t.Layout -eq 'toml') {
                $r.Changed = Set-McpTomlEntry -Path $t.ConfigPath -NodePath $NodePath -ServerPath $ServerPath
            } else {
                $r.Changed = Set-McpJsonEntry -Path $t.ConfigPath -Key $t.Layout -NodePath $NodePath `
                                              -ServerPath $ServerPath -IncludeType:($t.Layout -eq 'servers')
            }
        } catch {
            $r.Error = $_.Exception.Message
        }
    }
    return $results
}
