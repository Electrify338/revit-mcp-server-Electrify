import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, rebarTargets, layoutSchema, pointMm } from "../utils/rebarTool.js";

export function registerSetRebarLayoutTool(server: McpServer) {
  registerRebarTool(
    server,
    "set_rebar_layout",
    `Change existing shape-driven rebar sets: layout rule, number of bars, spacing, distribution length, which bars are included, single bars moved, or the set flipped.
- layout: pass only what changes; the rest keeps its current value (e.g. {spacingMm: 150} on a MaximumSpacing set, {number: 5} on a FixedNumber set). A value the set's rule does not use is refused: pass the rule too to switch it. Turning a single bar into a set needs the rule and its values.
- Bar indexes are 0-based positions in the set (see barPositions in get_host_rebar).
- Returns each set's layout after the change. Revit errors roll everything back.`,
    {
      ...rebarTargets,
      layout: layoutSchema.optional().describe("New layout values."),
      excludeBarIndexes: z.array(z.number().int().min(0)).optional().describe("Bars to remove from the set (kept as empty positions)."),
      includeBarIndexes: z.array(z.number().int().min(0)).optional().describe("Bars to put back into the set."),
      moveBars: z
        .array(
          z.object({
            index: z.number().int().min(0).describe("Bar position in the set."),
            offsetMm: pointMm.describe("Move vector in model coordinates, mm. Added to any earlier move."),
          })
        )
        .optional()
        .describe("Move individual bars of the set."),
      resetMovedBarIndexes: z.array(z.number().int().min(0)).optional().describe("Bars to put back to their default position."),
      flipSet: z.boolean().optional().describe("Flip the set to the other side of its first bar (Revit 2023.1+)."),
      flipBar: z.boolean().optional().describe("Flip the bar end for end, swapping start and end (Revit 2026.1+)."),
    }
  );
}
