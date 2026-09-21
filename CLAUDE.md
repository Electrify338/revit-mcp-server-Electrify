# mcp-servers-for-revit (Kemet fork) — working notes

Fork of LuDattilo/revit-mcp-server. A Revit add-in (`plugin/`, C#) that
runs a TCP server inside Revit, a command set (`commandset/`, C#) that does
the actual model work, and an MCP server (`server/`, TypeScript, stdio) that
AI apps launch and that forwards each tool call to the plugin as JSON-RPC.
Read `README.md` for tools and setup, `INSTALLATION.md` for manual installs.

## Why this fork exists, and for how long

Kemet uses it so Claude Desktop, Claude Code, Codex, Antigravity and friends
can **write** to the open model. Autodesk's official Revit 2027 MCP server
(tech preview, April 2026) is read-only. When it supports writes for
external clients this fork is retired; keep changes small and reversible.

## Who owns what (Kemet Addons vs this plugin)

Inside the Kemet office this plugin is **installed, updated and fronted by
Kemet Addons** (`Electrify338/Kemet-Revit-Addons`, private). Division of
labour, which must stay true:

| Job | Owner |
|---|---|
| Getting the zip onto machines, updating it | Kemet (`Update\McpInstaller.cs`) — from a share, never from GitHub directly |
| Asking the user which AI apps to connect, writing their configs | Kemet (`Tools\RevitMcp\`) — **this plugin skips `McpClientConfigurator` when `KemetAddons.addin` is in the same Addins folder** (`Application.cs`) |
| Running the TCP server, executing commands | this plugin |
| Counting tool calls | this plugin (`Helpers\UsageTracker.cs`); Kemet only supplies `usage.json` |
| Ribbon buttons users are told about | Kemet's "Revit MCP" button; our three buttons stay on the Add-Ins tab |

Standalone (no Kemet): `install.ps1` + `McpClientConfigurator` still do the
full job, so the fork remains usable on its own.

## Contract Kemet relies on — change both repos together

- Release workflow (`.github/workflows/release.yml`, on `v*` tags) produces
  `mcp-servers-for-revit-v<ver>-Revit<year>.zip` with
  `mcp-servers-for-revit.addin` and `revit_mcp_plugin\` at the zip root.
  Kemet copies exactly those two; anything else in the zip is ignored.
- `RevitMCPPlugin.dll` `AssemblyVersion` == tag (`scripts/release.ps1` does this).
- Paths inside the plugin folder: `Commands\RevitMCPCommandSet\server\runtime\node.exe`
  and `Commands\RevitMCPCommandSet\server\build\index.js`.
- Entry name in AI apps: `revit-mcp` (`McpClientConfigurator.ServerName`).
- Files Kemet's installer preserves across updates: `logs\`, `mcp-port.txt`,
  `usage.json`. Put new per-machine state under `logs\` or add it to
  Kemet's `McpInstaller.IsPreserved`.
- `usage.json` (written by Kemet, optional):
  `{ "url": "...", "token": "...", "department": "..." }`.
- Usage record (one JSON line per tool call, `logs\usage-yyyy-MM.jsonl`;
  batches POSTed to `url` as a JSON array with `Authorization: Bearer`):
  `{ ts, user, machine, department, revit, client, tool, ok, ms, project }`.
  Never prompts, never model output, never element data.
- The MCP server adds `client` (the AI app's name from the MCP `initialize`
  handshake, `server/src/utils/ClientInfo.ts`) as an extra top-level field
  on every JSON-RPC request; the plugin reads it in
  `SocketService.ReadClientName`. The JSON-RPC parser ignores it.

## Where things are

- `plugin/Core/SocketService.cs` — TCP loop; `ProcessJsonRPCRequest` is the
  one place every tool call passes through (usage hook lives here).
- `plugin/Helpers/UsageTracker.cs` — local jsonl + pending queue + uploader.
- `plugin/Utils/McpClientConfigurator.cs` — standalone-mode AI app config.
- `server/src/utils/ConnectionManager.ts` / `SocketClient.ts` — connection,
  retries, port discovery (`mcp-port.txt`), request framing.
- `scripts/install.ps1`, `common.ps1` — manual/one-liner install, also
  `-NonInteractive` for unattended runs; `$REPO` points at this fork.

## Building

- Server: `cd server && npm ci && npm run build:check` (typecheck) /
  `npm run build`. `server/build/` is git-ignored; the release workflow
  builds it.
  **`npm run build` ends with `deploy-addins.mjs`, which overwrites the
  server files of the plugin INSTALLED in `%AppData%\...\Addins\<year>\`**
  (`index.js`, `sql-wasm.wasm`, `tool_schemas.json`), no backup. On a machine
  whose installed plugin is a different version that leaves a mixed install.
  Verified 2026-09-21. To build without touching the install:
  `node esbuild.config.mjs && node generate-tool-schemas.mjs`.
- Verified 2026-09-21 on Windows: `dotnet build mcp-servers-for-revit.sln -c "Release R26"`
  builds clean; Release configs do not copy into the Addins folder (Debug ones do).
  The zip layout it produces installs and updates correctly through Kemet's
  `McpInstaller`, and Kemet's server switch drives `SocketService` by
  reflection: `Instance`, `IsRunning`, `Port`, `Initialize(UIApplication)`,
  `Start()`, `Stop()` are now part of the contract above - do not rename them.
- Plugin/commandset: Windows only (Revit API NuGet packages + WPF), see
  README "Build from source". A session without Windows/.NET must say it
  could not compile rather than claim a build passed.
- Release: `scripts/release.ps1 -Version X.Y.Z` then
  `git push origin main --tags`. **No release exists on this fork yet.**
