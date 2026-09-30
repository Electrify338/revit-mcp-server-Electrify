# Rebar tools: reference, test plan and API notes

19 MCP tools let an AI app read, place, edit and detail reinforcement in the
open model. Target is **Revit 2026**; the code builds for 2023-2027 and each
tool says so when the running Revit is too old for it.

## Status (2026-09-30)

- **Released in v2.3.0.** The command set builds for R23, R24, R25, R26 and
  R27 with 0 errors and no new warnings; the server typechecks and
  `tool-schemas.txt` lists 157 tools.
- **Run live in Revit 2026 before the release:** the read tools were checked
  against a production model (`get_rebar_types` with all twelve lists,
  `get_view_rebar`, `get_rebar_quantities`, `manage_rebar_numbering` `list`),
  and the tools were then tried hands-on on the dev machine with no failure
  reported.
- **Not done:** the test plan below has not been worked through item by item,
  and nothing was run on Revit 2023-2025 or 2027. The "Verify live" column
  lists the points still worth a deliberate check.
- No Kemet change needed: the tools live inside the plugin; zip layout, the
  `revit-mcp` entry, `SocketService` names and preserved files are untouched.

## The tools

| Tool | Writes? | What it does | Min. Revit |
|---|---|---|---|
| **Read** | | | |
| `get_rebar_types` | no | Bar, hook, shape and cover types; on request end treatments, cranks, splice types, rebar tag types, bending detail types, multi-rebar annotation types, area / path reinforcement types. | 2023 |
| `get_host_rebar` | no | One host: can it host rebar, covers, a local frame + extents to compute bar points from, and every rebar in it. | 2023 |
| `get_view_rebar` | no | Rebar visible in a view: the view's u/v frame, each set's extents in it, rebar number, presentation mode, taggable bars. The starting point for the detailing tools. | 2023 |
| `get_rebar_quantities` | no | Sets, bars, total length and nominal weight, grouped by bar type / host / partition / shape / rebar number. | 2023 |
| **Model** | | | |
| `create_rebar` | yes | Shape-driven bar or set from centerline points (mm), hooks, layout. | 2023 |
| `create_rebar_from_shape` | yes | A named RebarShape fitted to a rectangle (origin + two edges). | 2023 |
| `propagate_rebar` | yes | Copy one host's rebar into other hosts of the same category, adapted to each. | 2023 |
| `set_rebar_layout` | yes | Rule, number, spacing, array length; include / exclude / move single bars; flip. | 2023 (flipSet 2023.1, flipBar 2026.1) |
| `set_rebar_terminations` | yes | Hook, end treatment, crank, orientation, rotation at each end. | 2023 (cranks 2026) |
| `set_rebar_cover` | yes | Cover type on all faces or top / bottom / other / exterior / interior. | 2023 |
| `splice_rebar` | yes | Lap splices by maximum bar length or at points; unify; remove; read a chain. | 2025 |
| `split_rebar_set` | yes | Split a set into several at bar positions. | 2026.3 |
| `create_area_reinforcement` | yes | Four-layer mesh in a floor, foundation slab or wall; optional convert to rebar. | 2023 |
| `create_path_reinforcement` | yes | Bars square to a path; optional convert to rebar. | 2023 |
| **Detailing** | | | |
| `set_rebar_presentation` | yes | Presentation mode, hidden bars, view unobscured, per view. | 2023 |
| `tag_rebar` | yes | Rebar tags on a visible bar of each set, with head / leader placement. | 2023 |
| `create_bending_detail` | yes | Create (given points or stacked rows), move, list bending details. | 2024 |
| `create_multi_rebar_annotation` | yes | One dimension + tag across the bars of a set. | 2023 |
| `manage_rebar_numbering` | yes | Partitions and numbers: list, remove gaps, shift, change, move, append, merge, assign; numbering method. | 2023 (method 2026) |

Every writing tool is one undo step named "MCP: ...". Revit **warnings** are
dismissed and returned in `revitWarnings`; a Revit **error** rolls the call
back and its text is returned, so no dialog blocks the connector.

### Conventions

- Lengths in **mm**, angles in **degrees**, model coordinates unless stated.
- "Which rebar": `rebarIds`, else all rebar in `hostIds`, else the selection in
  Revit (selected rebar, or the rebar of selected hosts).
- Names (bar types, hooks, shapes, tags) differ per template: look them up
  with `get_rebar_types`; a wrong name returns the available ones.
- Annotation tools take points as view coordinates `{u, v}` (mm along the
  view's right / up direction from `view.Origin`) or model `{x, y, z}`.
  `get_view_rebar` returns the frame and every rebar's `boxUv`.
- Bar indexes are 0-based positions in a set.

## Files

| File | Job |
|---|---|
| `commandset/Services/Reinforcement/RebarToolBase.cs` | Handler base class, the failures preprocessor, `RebarTransaction.Run`, `FailureEventScope`, `ViewFrame`. |
| `commandset/Services/Reinforcement/RebarHelpers.cs` | mm/ft, lookups by id or name, "which rebar" resolution, `RebarLayout`. |
| `commandset/Services/Reinforcement/<Tool>EventHandler.cs` | One per tool: the Revit work. |
| `commandset/Commands/Reinforcement/ReinforcementCommands.cs` | The command classes of the 16 newer tools (they only pass JSON through). The first three tools have their own `*Command.cs`. |
| `server/src/tools/<tool>.ts` | Tool schema the AI sees. |
| `server/src/utils/rebarTool.ts` | Shared zod pieces (`pointMm`, `viewPoint`, `layoutSchema`, `rebarTargets`) and `registerRebarTool`. |
| `command.json` | One entry per command so the plugin loads it. |

The folders are called `Reinforcement`, not `Rebar`: a namespace named
`...Rebar` would hide Revit's `Rebar` class inside it.

### Adding another rebar tool

1. `Services/Reinforcement/<Name>EventHandler.cs` deriving from
   `ReinforcementEventHandlerBase`: read `Parameters` (a `JObject`), do the
   work inside `RebarTransaction.Run`, return `Ok(message, response)`.
2. A five-line class in `ReinforcementCommands.cs`.
3. An entry in `command.json`.
4. `server/src/tools/<name>.ts` calling `registerRebarTool`, plus an import
   and a list entry in `register.ts`.
5. `cd server && node esbuild.config.mjs && node generate-tool-schemas.mjs`
   (not `npm run build`, which overwrites the installed plugin, see CLAUDE.md).

## Build and install for testing (Windows)

Close Revit and any AI app that has the MCP server open (they hold `node.exe`).

```powershell
git fetch origin
git checkout feat/rebar-tools

cd server
npm ci
node esbuild.config.mjs          # builds server\build\index.js, does NOT touch the install
cd ..

# Debug builds copy into %AppData%\Autodesk\Revit\Addins\2026\revit_mcp_plugin\Commands\:
#   RevitMCPCommandSet\2026\RevitMCPCommandSet.dll, command.json, commandRegistry.json,
#   and server\build\ (the new index.js)
dotnet build commandset\RevitMCPCommandSet.csproj -c "Debug R26"
```

Building only the command set leaves the installed `RevitMCPPlugin.dll`
(v2.2.2) alone, so Kemet's `McpInstaller` sees the same version and does not
reinstall over the test build. `commandRegistry.json` is overwritten, which
re-enables every command (any switched off in the plugin's settings page are
switched back on).

**On a machine without the repo** (an engineer's PC with the released
plugin installed): build a test patch on the dev machine and hand over the zip.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make-test-patch.ps1 -Year 2026
# -> dist-test\revit-mcp-test-<branch>-R26-<date>.zip
```

The zip holds the five files that differ from a released install
(`RevitMCPCommandSet.dll`, `command.json`, which is also copied as
`commandRegistry.json`, `server\build\index.js`, `tool_schemas.json`), this
file as the test plan, and `Apply-TestPatch.ps1` / `Restore-TestPatch.ps1`.
Apply backs up what it replaces into `revit_mcp_plugin\_backup-before-test`;
Restore puts it back. The build uses the Release configuration, so making the
patch does not touch the plugin installed on the dev machine.

**Undo the test install:** `Restore-TestPatch.ps1`, or reinstall the released
zip (`INSTALLA.bat`, or `scripts\install.ps1`), or delete the plugin folder
and let Kemet install it again from the share.

Then: start Revit 2026, click **MCP On**, restart the AI app (Claude Code:
new session) so it sees the new tools.

## Test plan

Work in a **copy** of a project, or a new project from the Structural
template. Needed: a concrete beam (e.g. `Concrete-Rectangular Beam` 300x600,
6 m), a second identical beam, a structural floor and a structural wall, a
3D view, and a section along the first beam. Ask the AI in plain words; it
picks the tool. After each step look at the model, and press Ctrl+Z once to
check the step undoes in one go (then redo).

**Note for every failure:** the tool, what was asked, the message that came
back, and whether Revit showed a dialog (it never should).

### A. Read

| # | Ask | Expect |
|---|---|---|
| A1 | "List the rebar types" | Non-empty bar, hook, shape and cover lists; diameters match the names. |
| A2 | "Also list rebar tag types, bending detail types, splice types and area reinforcement types" | Each list present (may be empty in a bare template), no error. |
| A3 | Select the beam: "What can you tell me about this host's rebar?" | Valid host, covers in mm, frame along the beam, extents about 0..6000 / -150..150 / -600..0, no rebar yet. |
| A4 | Same on a door or a steel beam | "cannot host rebar", no exception. |

### B. Create and edit

| # | Ask | Expect | Verify live |
|---|---|---|---|
| B1 | "Put 4 bottom bars of 16 mm in this beam" (`create_rebar`) | 4 straight bars inside the cover. | |
| B2 | "Add 10 mm closed stirrups at 150 mm with 135 degree hooks" (`create_rebar`) | Stirrups along the beam. | Which hook orientation (Left / Right) is correct. |
| B3 | Delete the stirrups; "add stirrups using shape `<a stirrup shape>` fitted inside the cover" (`create_rebar_from_shape`) | Same result from a shape + rectangle. | Whether the rectangle is the bar's outer edge or its centerline (off by half a bar), and that the set runs along the beam. |
| B4 | "Change the stirrup spacing to 200 mm" (`set_rebar_layout`) | Spacing changes, array length kept. | |
| B5 | "Make the bottom bars 5 instead of 4" | 5 bars over the same width. | |
| B6 | "Remove the third stirrup", then "put it back" | Bar disappears / returns. | First / last bar may need `includeFirstBar` instead. |
| B7 | "Put 90 degree hooks on both ends of the bottom bars, turned up" (`set_rebar_terminations`) | Hooks appear and point into the beam. | Left / Right. |
| B8 | "Remove the hook at the start" | Hook gone at one end only. | |
| B9 | "Set the cover of this beam to 50 mm" (`set_rebar_cover`) | Cover changes; bars constrained to the cover move. | A cover type "Rebar Cover 50 mm" is created if none has 50 mm. |
| B10 | "Set only the bottom cover to 40 mm" | Only the bottom cover changes. | |
| B11 | "Copy this beam's rebar to the other beam" (`propagate_rebar`) | The second beam gets the same rebar, adapted. | One undo step for the whole call; no dialog. **This API opens its own transaction: the most likely tool to need a fix.** |
| B12 | Make a beam longer than 12 m with bottom bars: "splice bars longer than 12 m" (`splice_rebar`) | Bars cut into lapped pieces. | Lap length plausible; bars under 12 m untouched. |
| B13 | "Unify those two spliced bars again" | One bar. | |
| B14 | "Split the stirrups into three sets: the first 5, the last 5 and the middle" (`split_rebar_set`) | Three sets. | Needs Revit 2026.3; on an older 2026 the message must say so. |
| B15 | On the floor: "add area reinforcement, 12 mm at 200 both ways, top and bottom" (`create_area_reinforcement`) | Mesh in four layers. | Whether spacing and bar type were applied per layer (`notes` says if not). |
| B16 | "Convert that area reinforcement to rebar" (re-create with `convertToRebar`) | Ordinary rebar sets, no area system. | |
| B17 | On the floor: "add path reinforcement, 12 mm at 150, 1500 long, along this line" (`create_path_reinforcement`) | Bars square to the path. | Which side they go (flip). |
| B18 | Same two on the wall | Works with exterior / interior layers. | |

### C. Detailing (in the section along the beam)

| # | Ask | Expect | Verify live |
|---|---|---|---|
| C1 | "What rebar is in this view?" (`get_view_rebar`) | Each set with `boxUv`, rebar number, presentation mode; the view frame. | u runs right, v up; boxes match what is on screen. |
| C2 | "Show only the first and last stirrup" / "show all" / "show the middle one" (`set_rebar_presentation`) | Presentation changes; bottom bars (seen side-on as one line) are skipped with a reason. | |
| C3 | "Show the rebar unobscured in this view" | Bars show through the concrete. | |
| C4 | "Tag the bottom bars 300 mm below them" (`tag_rebar`) | Visible tag, text filled in. | Tag lands at the offset. With presentation Middle, the tag must still be visible (the tool tries bars until one is). |
| C5 | "Tag the stirrups with a leader, the arrow on the third stirrup" | Leader tag with a free end. | Leader end and elbow land where asked. |
| C6 | "Add a multi-rebar annotation for the stirrups" (`create_multi_rebar_annotation`) | Dimension across the stirrups with one tag. | If it says the direction is wrong, try vertical and note which was right. |
| C7 | "Add bending details for all the rebar in this beam below the elevation" (`create_bending_detail`) | One per set, stacked in rows, each under its rebar. | Row spacing sensible; nothing copied in the model (rebar count unchanged). |
| C8 | "Move the second bending detail 500 mm right" / "list the bending details" | Moves; list shows ids, rebar and positions. | |
| C9 | "List the rebar numbering" (`manage_rebar_numbering`) | Partitions with number ranges. | |
| C10 | "Put the stirrups in a partition called ST", then "make partition ST start at 101", then "remove gaps" | Rebar Number values change accordingly. | |
| C11 | "How much steel is in this model, by diameter?" (`get_rebar_quantities`) | Totals per bar type; weight = 0.00617 x d^2 kg/m x length (16 mm: 1.58 kg/m). | Compare with a Revit rebar schedule. |

### D. Robustness

| # | Test | Expect |
|---|---|---|
| D1 | A wrong bar type, hook, shape or tag name | Error listing the available names; nothing created. |
| D2 | Points outside the host | Error, or success with `revitWarnings`; never a blocking dialog. |
| D3 | A tool that needs a newer Revit (e.g. `split_rebar_set` on 2026.0, `splice_rebar` on 2024) | A plain "needs Revit ..." message. |
| D4 | `logs\usage-yyyy-MM.jsonl` in the plugin folder | One line per rebar tool call with `ok` true / false. |
| D5 | Revit 2025 or 2024 if at hand: B1, B2, B7 | The old hook API path works. |

### Worked numbers for B1-B3

Suppose `get_host_rebar` returned `originMm {x:0,y:0,z:3000}`, `xAxis (1,0,0)`,
`yAxis (0,1,0)`, `zAxis (0,0,1)`, extents x 0..6000, y -150..150, z -600..0,
covers 40 mm, with 10 mm stirrups and 16 mm bars.

Bottom bars, centerline 40 + 10 + 8 = 58 mm inside the concrete:

```json
{
  "hostId": 123456, "barTypeName": "16 mm",
  "points": [ { "x": 40, "y": -92, "z": 2458 }, { "x": 5960, "y": -92, "z": 2458 } ],
  "normal": { "x": 0, "y": 1, "z": 0 },
  "layout": { "rule": "FixedNumber", "number": 4, "arrayLengthMm": 184 }
}
```

Stirrups by points, centerline 45 mm in from each face, closed by repeating
the first point:

```json
{
  "hostId": 123456, "barTypeName": "10 mm", "style": "StirrupTie",
  "points": [
    { "x": 50, "y": -105, "z": 2955 }, { "x": 50, "y": 105, "z": 2955 },
    { "x": 50, "y": 105, "z": 2445 }, { "x": 50, "y": -105, "z": 2445 },
    { "x": 50, "y": -105, "z": 2955 }
  ],
  "normal": { "x": 1, "y": 0, "z": 0 },
  "startHookName": "Stirrup/Tie Seismic - 135 deg.", "endHookName": "Stirrup/Tie Seismic - 135 deg.",
  "layout": { "rule": "MaximumSpacing", "spacingMm": 150, "arrayLengthMm": 5900 }
}
```

The same stirrups from a shape (`create_rebar_from_shape`), rectangle 40 mm
inside each face:

```json
{
  "hostId": 123456, "shapeName": "M_T1", "barTypeName": "10 mm",
  "origin": { "x": 50, "y": -110, "z": 2440 },
  "xDirection": { "x": 0, "y": 1, "z": 0 }, "yDirection": { "x": 0, "y": 0, "z": 1 },
  "widthMm": 220, "heightMm": 520,
  "layout": { "rule": "MaximumSpacing", "spacingMm": 150, "arrayLengthMm": 5900 }
}
```

(xDirection x yDirection = (1,0,0), so the set runs along the beam.)

## Revit API notes

Source: `RevitAPI.xml` in `Nice3point.Revit.Api.RevitAPI` 2026.4.10
(Autodesk's own reference: summaries, remarks, documented exceptions and
`<since>` versions). All classes are in `Autodesk.Revit.DB.Structure` unless
noted.

### Hooks moved into `BarTerminationsData` in 2026

One object holds hook, crank and end-treatment type at each end, plus
orientation and rotation. Setting one kind clears the others at that end.
Deprecated in 2026 and **removed in 2027**, hence the
`#if REVIT2026_OR_GREATER` in `create_rebar` and `set_rebar_terminations`:

| 2023-2025 | 2026+ |
|---|---|
| `Rebar.CreateFromCurves(doc, style, barType, startHook, endHook, host, norm, curves, startOrient, endOrient, useExisting, createNew)` | `Rebar.CreateFromCurves(doc, style, barType, host, norm, curves, BarTerminationsData, useExisting, createNew)` |
| `Rebar.CreateFromCurvesAndShape(... startHook, endHook ..., startOrient, endOrient)` | `Rebar.CreateFromCurvesAndShape(doc, shape, barType, host, norm, curves, BarTerminationsData)` |
| `Rebar.Get/SetHookOrientation`, `Get/SetHookRotationAngle(angle, end)` | `Rebar.Get/SetTerminationOrientation`, `Get/SetTerminationRotationAngle(end, angle)` |
| `RebarHookOrientation` | `RebarTerminationOrientation` (Left / Right) |

Orientation, per the reference: *Left / Right = the termination is on your
left / right as you stand at the end of the bar, with the bar behind you,
taking the bar's normal as "up".*

### Things that are easy to get wrong

- **Version gates.** `RebarBendingDetail` 2024; `RebarSplice*` 2025;
  `BarTerminationsData`, cranks, `ReinforcementSettings.NumberingMethod` 2026.
  Point releases: `FlipRebarSet` 2023.1, `FlipRebar` 2026.1,
  `Rebar.SplitRebar` 2026.3. A point-release call compiles against the
  latest package but is missing on an un-updated Revit; those calls sit in
  their own `NoInlining` method and `MissingMethodException` becomes a
  "needs Revit x.y" message.
- **`RebarPropagation.AlignByHost` opens its own transaction** and may not be
  called inside one. `propagate_rebar` wraps it in a `TransactionGroup` (one
  undo step) and handles failures through the application's
  `FailuresProcessing` event (`FailureEventScope`) instead of a preprocessor.
- **`FilteredElementCollector.OfClass` refuses `AreaReinforcementType` and
  `PathReinforcementType`** (not in Revit's native object model). Use
  `RebarHelpers.AllOfClass<T>`.
- **Tags attach to a bar, not the set**: `element.GetSubelements()[i]
  .GetReference()`. A tag on a bar the view does not draw (presentation mode
  Middle, FirstLast...) exists but has no bounding box and draws nothing, so
  `tag_rebar` creates, regenerates, checks `get_BoundingBox(view)` and tries
  the next bar.
- **Never copy a bending detail** (`ElementTransformUtils.CopyElements`): it
  copies the host rebar too. `create_bending_detail` always uses
  `RebarBendingDetail.Create`, trying bar 0, 1, 2 since Revit sometimes
  refuses bar 0.
- `Rebar.GetShapeId()` throws for free-form rebar matched to several shapes;
  `MaxSpacing` throws on a Single layout; `SetPresentationMode` throws for a
  single bar and for a set seen end-on (`CanApplyPresentationMode`).
- `GetSpliceGeometries` (splice by rules) only accepts
  `RebarSplicePosition.Middle`.
- `NumberingSchema.MoveSequence / AppendSequence / MergeSequences /
  AssignElementsToSequence` are marked obsolete in 2027 because they only work
  on reinforcement schemas; still the way to do it for rebar.
- `View.Origin` of a plan view is "not meaningful" per the reference. It is
  still a fixed point, so u/v stay consistent within one view; do not compare
  u/v between views.
- `ElementIdExtensions` is ambiguous between `RevitMCPCommandSet.Utils` and
  `Nice3point.Revit.Extensions`: write `Utils.ElementIdExtensions.FromLong(...)`.

## Not covered yet

| Candidate | API |
|---|---|
| Rebar dimensions (setting-out strings to faces and bar ends) | `doc.Create.NewDimension` with rebar subelement / face references; stable-reference handling is delicate |
| Couplers | `RebarCoupler.Create(doc, typeId, ReinforcementData, ReinforcementData, out error)` |
| Fabric (mesh sheets) | `FabricArea.Create`, `FabricSheet.Create` |
| Free-form rebar, arcs in `create_rebar` | `Rebar.CreateFreeForm`, `Arc.Create` segments |
| Rebar constraints (snap a bar to a face / cover / another bar) | `RebarConstraintsManager` |
| Propagate to a face of another category | `RebarPropagation.AlignByFace` (needs face references) |

## Known limits

- `create_rebar`: straight segments only, shape-driven rebar only;
  `createNewShape` defaults to true, so an odd polyline can add a new
  RebarShape to the model.
- `set_rebar_layout`, `set_rebar_terminations`, `propagate_rebar`,
  `splice_rebar`, `split_rebar_set` act on `Rebar` elements, not on the bars
  owned by an area or path reinforcement (convert those first).
- `get_rebar_quantities` weight is nominal (bar area x length x density); it
  ignores the lap and rounding rules a fabricator may apply.
- Hosts must be valid rebar hosts (structural concrete); steel and
  non-structural elements are refused with a message.
