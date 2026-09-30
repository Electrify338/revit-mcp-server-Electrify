# Rebar tools: notes and office test plan

Three new MCP tools let an AI app read and place reinforcement in the open
model. Target is **Revit 2026**; the code also builds for 2023-2027.

| Tool | Writes? | What it does |
|---|---|---|
| `get_rebar_types` | no | Bar types (nominal/model/bend diameters), hook types (angle, style), rebar shapes (name, style), cover types (distance). |
| `get_host_rebar` | no | One host (id or current selection): can it host rebar, its covers, a local frame + extents to compute bar points from, and every rebar in it (bar type, shape, layout, quantity, spacing, lengths, hooks, mark, partition). |
| `create_rebar` | yes | One shape-driven rebar, single bar or set, from a polyline of centerline points (mm), optional hooks and layout rule. One undo step: "MCP: Create Rebar". |

## Status (2026-09-30)

- **Compiled, not run.** The command set was compiled on Linux against the
  Revit API reference assemblies (NuGet `Nice3point.Revit.Api.RevitAPI`)
  for R23, R24, R25, R26 and R27: 0 errors, no warnings from the new files.
  The server typechecks (`npm run build:check`) and the three tools appear in
  `tool-schemas.txt`. **Nothing has been exercised inside Revit yet** - that
  is what the test plan below is for.
- No Kemet change needed: new tools live inside the plugin; zip layout, the
  `revit-mcp` entry, `SocketService` names and preserved files are untouched.

## Files

| File | Job |
|---|---|
| `commandset/Commands/Reinforcement/*Command.cs` | Parse JSON, raise the external event. |
| `commandset/Services/Reinforcement/GetRebarTypesEventHandler.cs` | `get_rebar_types`. |
| `commandset/Services/Reinforcement/GetHostRebarEventHandler.cs` | `get_host_rebar`, incl. the local frame. |
| `commandset/Services/Reinforcement/CreateRebarEventHandler.cs` | `create_rebar`. `#if REVIT2026_OR_GREATER` picks the API (see below). |
| `commandset/Services/Reinforcement/RebarHelpers.cs` | mm/ft, lookups by id or name, host from selection. |
| `server/src/tools/{get_rebar_types,get_host_rebar,create_rebar}.ts` | Tool schemas the AI sees. |
| `command.json` | Three entries so the plugin loads the commands. |

The folders are called `Reinforcement`, not `Rebar`: a namespace named
`...Rebar` would hide Revit's `Rebar` class inside it.

## Build and install for testing (office PC, Windows)

Close Revit and any AI app that has the MCP server open (they hold `node.exe`).

```powershell
git fetch origin
git checkout ccr-dba00ec1-htwee1
git pull

cd server
npm ci
node esbuild.config.mjs          # builds server\build\index.js, does NOT touch the install
cd ..

# Debug builds copy into %AppData%\Autodesk\Revit\Addins\2026\revit_mcp_plugin\Commands\:
#   RevitMCPCommandSet\2026\RevitMCPCommandSet.dll, command.json, commandRegistry.json,
#   and server\build\ (the new index.js with the three tools)
dotnet build commandset\RevitMCPCommandSet.csproj -c "Debug R26"
```

Building only the command set leaves the installed `RevitMCPPlugin.dll`
(v2.2.2) alone, so Kemet's `McpInstaller` sees the same version and does not
reinstall over the test build. `commandRegistry.json` is overwritten, which
re-enables every command (any commands switched off in the plugin's settings
page are switched back on).

**Undo the test install:** reinstall the released zip (`INSTALLA.bat`, or
`scripts\install.ps1`), or delete the plugin folder and let Kemet install it
again from the share.

Then: start Revit 2026, click **MCP On**, restart the AI app (Claude Code:
new session) so it sees the new tools.

## Test plan

Use a model with a **concrete** beam: e.g. new project from the Structural
template, load `Concrete-Rectangular Beam` (300x600), draw a 6 m beam on
Level 2. Keep a 3D view active.

| # | Ask the AI / call | Expect |
|---|---|---|
| 1 | `get_rebar_types` | Non-empty `barTypes`, `hookTypes`, `shapes`, `coverTypes`. Diameters in mm match the type names. |
| 2 | Select the beam, `get_host_rebar` (no id) | `isValidRebarHost: true`, `covers.top/bottom/other` with mm, `frame.kind: "locationLine"`, `xAxis` along the beam, extents x ~ 0..6000, y ~ -150..150, z ~ -600..0 (location line is usually the top). `rebarCount: 0`. |
| 3 | `get_host_rebar` on a door or a steel beam | `isValidRebarHost: false` and a plain message, no exception. |
| 4 | One straight bottom bar (example A) | Bar appears, inside the concrete, `shape` is a straight shape (e.g. `00`/`M_00`), `quantity: 1`. |
| 5 | Same bar with `layout: {rule: "FixedNumber", number: 4, arrayLengthMm: <b - 2*cover - 2*stirrup - bar>}` and `normal` = frame `yAxis` | 4 bars across the width, within the cover. |
| 6 | Stirrups (example B) | Closed stirrups with 135 degree hooks, spaced 150 mm along the beam. If the hooks point outwards or the shape looks wrong, retry with `startHookOrientation`/`endHookOrientation: "Right"` and **note which one was right** (goes into the tool description). |
| 7 | Bar with 90 degree hooks both ends | Hooks turn up into the beam. Again note Left/Right. |
| 8 | Straight bar **without** `normal` | Error "A straight bar needs 'normal' ...". Nothing created. |
| 9 | Bad `barTypeName` | Error that lists available bar types. |
| 10 | Points outside the host | Either an error, or success with `revitWarnings` (e.g. rebar outside host). No Revit dialog should block. |
| 11 | `get_host_rebar` with `includeGeometry: true` | Lists everything created, with spacing, lengths, hooks and first-bar centerline. |
| 12 | Ctrl+Z in Revit | Each `create_rebar` undoes in one step. |
| 13 | Revit 2025 or 2024 if at hand | Same as 4-7 (old hook API path). |
| 14 | `logs\usage-yyyy-MM.jsonl` in the plugin folder (this month) | Lines for the rebar tools with `ok` true/false. |

### Example A - straight bottom bar

Suppose `get_host_rebar` returned `originMm {x:0,y:0,z:3000}`, `xAxis (1,0,0)`,
`yAxis (0,1,0)`, `zAxis (0,0,1)`, extents x 0..6000, y -150..150, z -600..0,
covers 40 mm, and 10 mm stirrups are planned. For a 16 mm bar the centerline
sits 40 + 10 + 8 = 58 mm inside the concrete:

```json
{
  "hostId": 123456,
  "barTypeName": "16 mm",
  "style": "Standard",
  "points": [ { "x": 40, "y": -92, "z": 2458 }, { "x": 5960, "y": -92, "z": 2458 } ],
  "normal": { "x": 0, "y": 1, "z": 0 },
  "layout": { "rule": "FixedNumber", "number": 4, "arrayLengthMm": 184 },
  "showUnobscuredInActiveView": true
}
```

(z = 3000 - 600 + 58; y from -150 + 58 to +150 - 58, so the set spans 184 mm.)

### Example B - stirrups

Rectangle in the plane square to the beam at x = 50, 10 mm bar, centerline
45 mm in from each face (40 cover + 5), closed by repeating the first point:

```json
{
  "hostId": 123456,
  "barTypeName": "10 mm",
  "style": "StirrupTie",
  "points": [
    { "x": 50, "y": -105, "z": 2955 }, { "x": 50, "y": 105, "z": 2955 },
    { "x": 50, "y": 105, "z": 2445 }, { "x": 50, "y": -105, "z": 2445 },
    { "x": 50, "y": -105, "z": 2955 }
  ],
  "normal": { "x": 1, "y": 0, "z": 0 },
  "startHookName": "Stirrup/Tie Seismic - 135 deg.",
  "endHookName": "Stirrup/Tie Seismic - 135 deg.",
  "layout": { "rule": "MaximumSpacing", "spacingMm": 150, "arrayLengthMm": 5900 },
  "showUnobscuredInActiveView": true
}
```

Bar type and hook names differ per template; take them from `get_rebar_types`.

## Revit API notes (from the 2026 API reference)

Source: `RevitAPI.xml` / `RevitAPI.dll` in `Nice3point.Revit.Api.RevitAPI`
2026.4.10 and 2027.3.0 (Autodesk's own reference docs and `[Obsolete]`
messages), plus revitapidocs.com "API Changes 2026".

**Hooks moved into `BarTerminationsData` in 2026.** One object holds hook,
crank and end-treatment type at each end, plus orientation and rotation.
Deprecated in 2026 and **removed in 2027**:

| Old (2023-2025) | 2026+ |
|---|---|
| `Rebar.CreateFromCurves(doc, style, barType, startHook, endHook, host, norm, curves, startOrient, endOrient, useExisting, createNew)` | `Rebar.CreateFromCurves(doc, style, barType, host, norm, curves, BarTerminationsData, useExisting, createNew)` |
| `Rebar.CreateFromCurvesAndShape(... startHook, endHook ..., startOrient, endOrient)` | `Rebar.CreateFromCurvesAndShape(doc, shape, barType, host, norm, curves, BarTerminationsData)` |
| `RebarShape.Create(... RebarHookOrientation ...)` | `RebarShape.Create(..., RebarShapeTerminationsData)` |
| `Rebar.Get/SetHookOrientation`, `Get/SetHookRotationAngle` | `Rebar.Get/SetTerminationOrientation`, `Get/SetTerminationRotationAngle` |
| `RebarHookOrientation` | `RebarTerminationOrientation` (Left/Right) |
| `RebarBendData` hook constructor/properties | `RebarBendData(barType, style, BarTerminationsData)`, `TerminationOrientation0/1` |
| `Rebar.CreateFreeForm(..., out RebarFreeFormValidationResult)` | `Rebar.CreateFreeForm(doc, barType, host, curves, RebarStyle)` |

Orientation, per the reference: *Left/Right = the termination is on your
left/right as you stand at the end of the bar, with the bar behind you,
taking the bar's normal as "up". Default Left.*

Also new in 2026: `Rebar.SplitRebar(doc, id, ISet<int> barIndexes, bool, bool)`
(split a set), `RebarCrankTypeUtils`, `RebarCrankOverridableParameters`,
`RebarEndType`.

**Unchanged and used here:** `RebarShapeDrivenAccessor.SetLayoutAs{Single,
FixedNumber, MaximumSpacing, NumberWithSpacing, MinimumClearSpacing}`,
`RebarHostData.IsValidHost / GetRebarHostData / GetRebarsInHost`,
`Rebar.GetHookTypeId`, `SetUnobscuredInView`, `GetCenterlineCurves`,
`RebarBarType.BarNominalDiameter / BarModelDiameter`, `CLEAR_COVER_*`
parameters.

## Candidates for the next round

| Tool | API |
|---|---|
| `create_area_reinforcement` (slabs, walls) | `AreaReinforcement.Create(doc, host, majorDirection, areaTypeId, barTypeId, hookTypeId)` |
| `create_path_reinforcement` | `PathReinforcement.Create(...)` |
| `create_bending_detail` (for the beam-detailing skills) | `RebarBendingDetail.Create(doc, viewId, rebarId, subelementKey, type, position, rotation)` |
| `tag_rebar` (one tag across a set) | `MultiReferenceAnnotation.Create(doc, viewId, options)` |
| `split_rebar_set` (2026+) | `Rebar.SplitRebar(...)` |
| Arcs in `create_rebar` | `Arc.Create` segments; same `CreateFromCurves` |
| Couplers, fabric, free form | `RebarCoupler.Create`, `FabricArea/FabricSheet.Create`, `Rebar.CreateFreeForm` |

## Known limits

- Straight segments only (no arcs) and shape-driven rebar only.
- Points are model (internal) coordinates in mm, same as every other tool
  here; `get_host_rebar` returns its frame in the same coordinates.
- `createNewShape` defaults to true, so an odd polyline can add a new
  RebarShape to the model. Set it false to only reuse existing shapes.
- The host must be a valid rebar host (structural concrete). Steel or
  non-structural elements are refused with a message.
