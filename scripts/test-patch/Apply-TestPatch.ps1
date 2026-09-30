<#
    Puts the test build over the installed Revit MCP plugin (per user, no admin).
    Close Revit first. The files it replaces are kept in _backup-before-test so
    Restore-TestPatch.ps1 can put them back.
#>
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$year = (Get-Content (Join-Path $here "year.txt") -TotalCount 1).Trim()
$root = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\revit_mcp_plugin"

if (-not (Test-Path (Join-Path $root "RevitMCPPlugin.dll"))) {
    throw "The Revit MCP plugin is not installed for Revit $year ($root). Install it first (Kemet Addons > Revit MCP)."
}
if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    throw "Revit is running. Close Revit, then run this again."
}

# source file in this folder -> place inside the plugin folder
$files = @(
    @{ Src = "RevitMCPCommandSet.dll"; Dst = "Commands\RevitMCPCommandSet\$year\RevitMCPCommandSet.dll" },
    @{ Src = "command.json";           Dst = "Commands\RevitMCPCommandSet\command.json" },
    @{ Src = "command.json";           Dst = "Commands\commandRegistry.json" },
    @{ Src = "index.js";               Dst = "Commands\RevitMCPCommandSet\server\build\index.js" },
    @{ Src = "tool_schemas.json";      Dst = "tool_schemas.json" }
)

# Keep the first backup: applying a newer test build must not overwrite the released files
$backup = Join-Path $root "_backup-before-test"
$firstTime = -not (Test-Path $backup)
foreach ($f in $files) {
    $target = Join-Path $root $f.Dst
    if ($firstTime -and (Test-Path $target)) {
        $kept = Join-Path $backup $f.Dst
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $kept) | Out-Null
        Copy-Item $target $kept -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item (Join-Path $here $f.Src) $target -Force
}

Write-Host ""
Write-Host "Test build installed for Revit $year."
Write-Host "  1. Close your AI app (Claude, Codex, ...) completely and open it again."
Write-Host "  2. Start Revit $year and switch MCP On."
Write-Host "To go back to the released version: run Restore-TestPatch.ps1"
