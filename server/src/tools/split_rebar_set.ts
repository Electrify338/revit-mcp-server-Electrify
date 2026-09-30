import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool } from "../utils/rebarTool.js";

export function registerSplitRebarSetTool(server: McpServer) {
  registerRebarTool(
    server,
    "split_rebar_set",
    `Split one rebar set into several sets at given bar positions (Revit 2026.3+), e.g. to give the stirrups near the supports a closer spacing than those at midspan: split, then set_rebar_layout on each part.
- barIndexes are 0-based; each index is the last bar of a new set. [4, 14] on a 20-bar set gives bars 0-4, 5-14 and 15-19.
- Free-form rebar, sets with one bar and rebar in a group cannot be split.`,
    {
      rebarId: z.number().describe("The rebar set to split."),
      barIndexes: z.array(z.number().int().min(0)).min(1).describe("Bar positions where the set is cut (each is the last bar of a part)."),
      constrainSplitSets: z
        .boolean()
        .optional()
        .describe("Keep each part constrained to the one before it, so they move together. Default true."),
      splitAllSetsInSpliceChain: z
        .boolean()
        .optional()
        .describe("Also split the sets lap-spliced to this one and keep the splices. Default false."),
    }
  );
}
