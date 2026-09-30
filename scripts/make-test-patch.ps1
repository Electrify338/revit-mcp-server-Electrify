<#
.SYNOPSIS
    Builds a small test patch that drops this checkout's command set and MCP server
    over an installed plugin, so new tools can be tried without cutting a release.

.DESCRIPTION
    Builds the server bundle and the command set (Release, which does not touch the
    plugin installed on this machine), then zips the five files that differ from a
    released install together with Apply-TestPatch.ps1 / Restore-TestPatch.ps1.

    RevitMCPPlugin.dll is not part of the patch, so its version stays that of the
    installed release and Kemet Addons does not reinstall over the test build.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\make-test-patch.ps1 -Year 2026
#>
param(
    [ValidateSet("2023", "2024", "2025", "2026", "2027")]
    [string]$Year = "2026",
    [string]$OutDir
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repo "dist-test" }
$config = "Release R$($Year.Substring(2))"

Push-Location (Join-Path $repo "server")
try {
    if (-not (Test-Path "node_modules")) { npm ci; if ($LASTEXITCODE) { throw "npm ci failed" } }
    # Not 'npm run build': that also overwrites the plugin installed on this machine
    node esbuild.config.mjs; if ($LASTEXITCODE) { throw "server build failed" }
    node generate-tool-schemas.mjs; if ($LASTEXITCODE) { throw "schema generation failed" }
}
finally { Pop-Location }

dotnet build (Join-Path $repo "commandset\RevitMCPCommandSet.csproj") -c $config -nologo -v q
if ($LASTEXITCODE) { throw "command set build failed" }

$dll = Get-ChildItem (Join-Path $repo "commandset\bin\$config") -Recurse -Filter "RevitMCPCommandSet.dll" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { throw "RevitMCPCommandSet.dll not found under commandset\bin\$config" }

$branch = (git -C $repo rev-parse --abbrev-ref HEAD).Trim() -replace '[^\w.-]', '-'
$stamp = Get-Date -Format "yyyyMMdd-HHmm"
$name = "revit-mcp-test-$branch-R$($Year.Substring(2))-$stamp"
$stage = Join-Path $OutDir $name
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item $dll.FullName $stage
Copy-Item (Join-Path $repo "command.json") $stage
Copy-Item (Join-Path $repo "server\build\index.js") $stage
Copy-Item (Join-Path $repo "plugin\tool_schemas.json") $stage
Copy-Item (Join-Path $PSScriptRoot "test-patch\Apply-TestPatch.ps1") $stage
Copy-Item (Join-Path $PSScriptRoot "test-patch\Restore-TestPatch.ps1") $stage
Copy-Item (Join-Path $PSScriptRoot "test-patch\README.txt") $stage
Copy-Item (Join-Path $repo "docs\REBAR.md") (Join-Path $stage "REBAR-test-plan.md")
Set-Content -Path (Join-Path $stage "year.txt") -Value $Year -Encoding ASCII

$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Write-Host "Test patch: $zip"
