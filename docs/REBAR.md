# Adding rebar tools: Revit 2026 API guide

A build guide for adding reinforcement tools to this plugin. Nothing here is
implemented yet: this file is the plan, the API facts and the gotchas.

Sources: Autodesk's API reference shipped in the NuGet packages this repo
already builds against (`Nice3point.Revit.Api.RevitAPI` 2023, 2025, 2026.4.10,
2027.3.0: `RevitAPI.xml` and the `[Obsolete]` messages in `RevitAPI.dll`),
plus [API Changes 2026](https://www.revitapidocs.com/2026/news) and the
[Developer's Guide, Rebar](https://help.autodesk.com/view/RVT/2025/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Discipline_Specific_Functionality_Structural_Engineering_Structural_Model_Elements_Reinforcement_Rebar_html).
All classes below are in `Autodesk.Revit.DB.Structure`.

## 1. What the API offers

| Area | Main calls | Priority |
|---|---|---|
| Bars / sets in a host (beam, column, wall, floor, foundation) | `Rebar.CreateFromCurves`, `Rebar.CreateFromCurvesAndShape`, `Rebar.CreateFromRebarShape` | first |
| Set layout | `rebar.GetShapeDrivenAccessor().SetLayoutAs{Single, FixedNumber, MaximumSpacing, NumberWithSpacing, MinimumClearSpacing}` | first |
| Hooks, cranks, end treatments | `BarTerminationsData` (new in 2026) | first |
| Read host / existing bars | `RebarHostData.IsValidHost`, `GetRebarHostData(host).GetRebarsInHost()`, `GetAreaReinforcementsInHost`, `GetPathReinforcementsInHost` | first |
| Types | `RebarBarType`, `RebarHookType`, `RebarShape`, `RebarCoverType` (FilteredElementCollector `OfClass`) | first |
| Area reinforcement | `AreaReinforcement.Create(doc, host, majorDirection, areaTypeId, barTypeId, hookTypeId)` | next |
| Path reinforcement | `PathReinforcement.Create(doc, host, curves, flip, pathTypeId, barTypeId, startHookId, endHookId[, shapeId])` | next |
| Bending details | `RebarBendingDetail.Create(doc, viewId, rebarId, subelementKey, type, position, rotation)` | next (beam-detailing skills) |
| One tag across a set | `MultiReferenceAnnotation.Create(doc, viewId, options)` | next |
| Split a set (2026+) | `Rebar.SplitRebar(doc, id, ISet<int> barIndexes, bool, bool)` | later |
| Free form | `Rebar.CreateFreeForm(doc, barType, host, curves, RebarStyle)` | later |
| Couplers, fabric | `RebarCoupler.Create`, `FabricArea.Create`, `FabricSheet.Create` | later |

## 2. What changed in 2026 (and was removed in 2027)

Hooks, cranks and end treatments now live in one object, `BarTerminationsData`
(`HookTypeIdAtStart/End`, `CrankTypeIdAtStart/End`, `EndTreatmentTypeIdAtStart/End`,
`TerminationOrientationAtStart/End`, `TerminationRotationAngleAtStart/End`).
Setting a hook clears the crank/end treatment at that end, and vice versa.

| 2023-2025 (deprecated in 2026, **gone in 2027**) | 2026+ |
|---|---|
| `Rebar.CreateFromCurves(doc, style, barType, startHook, endHook, host, norm, curves, startOrient, endOrient, useExisting, createNew)` | `Rebar.CreateFromCurves(doc, style, barType, host, norm, curves, BarTerminationsData, useExisting, createNew)` |
| `Rebar.CreateFromCurvesAndShape(doc, shape, barType, startHook, endHook, host, norm, curves, startOrient, endOrient)` | `Rebar.CreateFromCurvesAndShape(doc, shape, barType, host, norm, curves, BarTerminationsData)` |
| `RebarShape.Create(... RebarHookOrientation ...)` | `RebarShape.Create(..., RebarShapeTerminationsData)` |
| `Rebar.Get/SetHookOrientation`, `Get/SetHookRotationAngle` | `Rebar.Get/SetTerminationOrientation`, `Get/SetTerminationRotationAngle` |
| enum `RebarHookOrientation` | enum `RebarTerminationOrientation` (Left, Right) |
| `new RebarBendData(... hooks ...)`, `HookOrient0/1` | `new RebarBendData(barType, style, BarTerminationsData)`, `TerminationOrientation0/1` |
| `Rebar.CreateFreeForm(..., out RebarFreeFormValidationResult)` | `Rebar.CreateFreeForm(doc, barType, host, curves, RebarStyle)` |

Orientation, from the reference: *Left/Right = the termination is on your
left/right as you stand at the end of the bar, with the bar behind you, taking
the bar's normal as "up". Default Left.*

This repo builds R23-R27 from one source, so creation code needs
`#if REVIT2026_OR_GREATER` (the symbol is already defined in
`commandset/RevitMCPCommandSet.csproj`). The old overloads do not exist in
2027, so they can't just be left in place with a warning.

Unchanged 2023-2027 (checked in all four XMLs): the `SetLayoutAs*` methods,
`RebarHostData.*`, `Rebar.GetHookTypeId`, `GetShapeId`, `Quantity`,
`NumberOfBarPositions`, `TotalLength`, `LayoutRule`, `MaxSpacing`,
`SetUnobscuredInView`, `GetCenterlineCurves`, `RebarBarType.BarNominalDiameter`
/ `BarModelDiameter` / `StandardBendDiameter` / `StirrupTieBendDiameter`,
`RebarHookType.HookAngle` / `Style`, `RebarShape.RebarStyle`,
`RebarCoverType.CoverDistance`, and the parameters `CLEAR_COVER_TOP/BOTTOM/
OTHER/INTERIOR/EXTERIOR`, `REBAR_ELEM_LENGTH`, `REBAR_NUMBER`,
`NUMBER_PARTITION_PARAM`.

## 3. How a tool is wired in this repo

Five touch points per tool (copy `get_compound_structure` /
`create_floor` as models):

1. `commandset/Commands/<Folder>/<Name>Command.cs`: `ExternalEventCommandBase`,
   `CommandName => "snake_name"`, parse the `JObject`, `RaiseAndWaitForCompletion`.
2. `commandset/Services/<Folder>/<Name>EventHandler.cs`: `IExternalEventHandler,
   IWaitableExternalEventHandler`; does the Revit work, sets `AIResult<object>`,
   `_resetEvent.Set()` in `finally`. Reset the event in `SetParameters`
   (as `CreateFloorEventHandler` does).
3. `command.json`: one entry with the same `commandName` (the plugin only loads
   listed commands; `Enabled` defaults to true).
4. `server/src/tools/<snake_name>.ts` with zod schema, plus two lines in
   `server/src/tools/register.ts`.
5. Regenerate `tool-schemas.txt` and `plugin/tool_schemas.json`:
   `cd server && node esbuild.config.mjs && node generate-tool-schemas.mjs`
   (not `npm run build`, which also overwrites the installed plugin, see
   CLAUDE.md).

Then README / COMMANDS.md / USER_GUIDE.md tables.

## 4. Proposed first three tools

**`get_rebar_types`** (read-only). Params: `include` (barTypes, hookTypes,
shapes, coverTypes), `nameFilter`. Returns id, name and mm values for each.

**`get_host_rebar`** (read-only). Params: `hostId` (else the selected element),
`includeGeometry`. Returns `isValidRebarHost`, covers (from the `CLEAR_COVER_*`
parameters, which hold a `RebarCoverType` id), a local frame and every rebar in
the host (bar type, shape, style, layout rule, quantity, spacing, array length,
bar/total length, hooks, mark, partition). The frame is what makes placement
possible for an AI:
- beam / wall with a straight location line: x = line direction, y =
  `BasisZ.CrossProduct(x)`, z = `x.CrossProduct(y)`, origin = line start;
- other family instances (columns): `GetTransform()` basis;
- then project the host's solid edge points onto the axes to get min/max
  extents in mm (fallback: bounding box corners).

**`create_rebar`**. Params: `hostId`, `barTypeName|barTypeId`, `style`
(Standard | StirrupTie), `points` (polyline, mm, ≥2; closed stirrup repeats the
first point), `normal` (required for straight bars, derivable from 3
non-collinear points otherwise), `shapeName|shapeId` (optional, forces
`CreateFromCurvesAndShape`), `start/endHookName|Id`, `start/endHookOrientation`,
`layout {rule, number, spacingMm, arrayLengthMm, barsOnNormalSide,
includeFirstBar, includeLastBar}`, `useExistingShapeIfPossible`,
`createNewShape`, `showUnobscuredInActiveView`.
Validate before calling Revit: every segment square to the normal, all points
in one plane, consecutive points ≥ 1 mm apart.

## 5. Code that matters

The snippets below were compile-checked against the R23-R27 reference
assemblies. They have not been run in Revit.

Creation, both API generations:

```csharp
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;

#if REVIT2026_OR_GREATER
using (var terms = new BarTerminationsData(doc))
{
    if (startHook != null) terms.HookTypeIdAtStart = startHook.Id;
    if (endHook != null)   terms.HookTypeIdAtEnd = endHook.Id;
    terms.TerminationOrientationAtStart = RebarTerminationOrientation.Left;
    terms.TerminationOrientationAtEnd   = RebarTerminationOrientation.Left;

    rebar = shape != null
        ? RevitRebar.CreateFromCurvesAndShape(doc, shape, barType, host, normal, curves, terms)
        : RevitRebar.CreateFromCurves(doc, style, barType, host, normal, curves, terms,
                                      useExistingShape, createNewShape);
}
#else
rebar = shape != null
    ? RevitRebar.CreateFromCurvesAndShape(doc, shape, barType, startHook, endHook, host, normal, curves,
                                          RebarHookOrientation.Left, RebarHookOrientation.Left)
    : RevitRebar.CreateFromCurves(doc, style, barType, startHook, endHook, host, normal, curves,
                                  RebarHookOrientation.Left, RebarHookOrientation.Left,
                                  useExistingShape, createNewShape);
#endif
// null => no matching shape (createNewShape false, or curves/hooks don't fit shapeName)
```

Layout (same in all versions; lengths in feet):

```csharp
var a = rebar.GetShapeDrivenAccessor();
a.SetLayoutAsFixedNumber(number, arrayLength, barsOnNormalSide, includeFirst, includeLast);
a.SetLayoutAsMaximumSpacing(spacing, arrayLength, barsOnNormalSide, includeFirst, includeLast);
a.SetLayoutAsNumberWithSpacing(number, spacing, barsOnNormalSide, includeFirst, includeLast);
a.SetLayoutAsMinimumClearSpacing(spacing, arrayLength, barsOnNormalSide, includeFirst, includeLast);
```

Keep Revit dialogs out of an unattended call: give the transaction an
`IFailuresPreprocessor` that records and `DeleteWarning`s warnings and returns
`ProceedWithRollBack` on errors; then `tx.Commit() != Committed` means Revit
refused, and the recorded error text is the message for the AI.

## 6. Gotchas found while checking

- **Don't name the namespace `...Rebar`.** Inside
  `RevitMCPCommandSet.Services.Rebar`, `Rebar` resolves to the namespace, not
  Revit's class. Use `Reinforcement` for the folders, and/or
  `using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;`.
- **`ElementIdExtensions` is ambiguous** between `RevitMCPCommandSet.Utils`
  and `Nice3point.Revit.Extensions`: write `Utils.ElementIdExtensions.FromLong(...)`.
- `Rebar.GetShapeId()` throws for free-form rebar matched to several shapes;
  `MaxSpacing` throws on a Single layout; `GetCenterlineCurves(..., 0)` throws if
  the first bar is excluded. Guard these when listing a host's bars.
- `RebarHostData.IsValidHost` is false for steel and non-structural
  elements: check it first and return a plain message.
- Units: tools speak mm, Revit feet (`/ 304.8`). Hook angles are radians.
- Bar type and hook names differ per template (`10M`, `16 mm`, `Standard -
  90 deg.`, `Stirrup/Tie Seismic - 135 deg.`...): always look them up.

## 7. Building and testing at the office

Build (Windows, Revit closed, AI apps closed since they hold `node.exe`):

```powershell
cd server; npm ci; node esbuild.config.mjs; node generate-tool-schemas.mjs; cd ..
dotnet build commandset\RevitMCPCommandSet.csproj -c "Debug R26"
```

The Debug build copies the command set DLL, `command.json`,
`commandRegistry.json` and `server\build\` into
`%AppData%\Autodesk\Revit\Addins\2026\revit_mcp_plugin\Commands\`. It leaves
`RevitMCPPlugin.dll` (2.2.2) alone, so Kemet does not reinstall over it.
`commandRegistry.json` is replaced, which re-enables any switched-off
commands. To undo, reinstall the release zip (`INSTALLA.bat`).

Start Revit, click **MCP On**, restart the AI app.

Test model: Structural template, `Concrete-Rectangular Beam` 300x600, a 6 m
beam, a 3D view active.

| # | Test | Expect |
|---|---|---|
| 1 | `get_rebar_types` | Non-empty lists, diameters match names. |
| 2 | `get_host_rebar` on the selected beam | Valid host, covers in mm, frame along the beam, extents ~0..6000 / -150..150 / -600..0. |
| 3 | `get_host_rebar` on a door or steel beam | Not a valid host, plain message. |
| 4 | One straight bottom bar | Bar inside the concrete, straight shape, quantity 1. |
| 5 | Same, FixedNumber 4 across the width | 4 bars within cover. |
| 6 | Closed stirrups, 135° hooks, 150 mm max spacing | Stirrups along the beam. **Note whether Left or Right orientation is correct.** |
| 7 | Bar with 90° hooks both ends | Hooks turn into the beam; note Left/Right. |
| 8 | Straight bar with no `normal`; bad bar type name | Clear errors, nothing created. |
| 9 | Points outside the host | Error or `revitWarnings`, never a blocking dialog. |
| 10 | Ctrl+Z | Each create undoes in one step. |
| 11 | Same in Revit 2025/2024 if available | Old API path works. |

Worked numbers for 4-6 (beam top at z = 3000, 40 mm cover, 10 mm stirrups,
16 mm bars): bottom bar centerline z = 3000 - 600 + 40 + 10 + 8 = 2458,
y = ±92, x = 40..5960, `normal` (0,1,0), FixedNumber 4 over 184 mm.
Stirrup centerline 45 mm in from each face: points at x = 50,
(y, z) = (-105, 2955) → (105, 2955) → (105, 2445) → (-105, 2445) → back to
start, `normal` (1,0,0), MaximumSpacing 150 over 5900.

## 8. Contract check

New tools live entirely inside the plugin: zip layout, the `revit-mcp` entry,
`SocketService` names and preserved files are untouched, so Kemet Addons needs
no change.
