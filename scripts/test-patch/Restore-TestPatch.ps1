<#
    Puts back the files Apply-TestPatch.ps1 replaced. Close Revit first.
#>
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$year = (Get-Content (Join-Path $here "year.txt") -TotalCount 1).Trim()
$root = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\revit_mcp_plugin"
$backup = Join-Path $root "_backup-before-test"

if (-not (Test-Path $backup)) {
    throw "No backup found at ${backup}. Nothing to restore: reinstall the plugin from Kemet Addons > Revit MCP instead."
}
if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    throw "Revit is running. Close Revit, then run this again."
}

# The same places Apply-TestPatch.ps1 writes to
$files = @(
    "Commands\RevitMCPCommandSet\$year\RevitMCPCommandSet.dll",
    "Commands\RevitMCPCommandSet\command.json",
    "Commands\commandRegistry.json",
    "Commands\RevitMCPCommandSet\server\build\index.js",
    "tool_schemas.json"
)

foreach ($f in $files) {
    $kept = Join-Path $backup $f
    if (Test-Path $kept) { Copy-Item $kept (Join-Path $root $f) -Force }
}
Remove-Item $backup -Recurse -Force

Write-Host "Released version restored for Revit $year. Restart your AI app."
