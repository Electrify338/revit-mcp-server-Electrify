import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool } from "../utils/rebarTool.js";

export function registerGetRebarQuantitiesTool(server: McpServer) {
  registerRebarTool(
    server,
    "get_rebar_quantities",
    "Rebar take-off: number of sets and bars, total length (m) and nominal steel weight (kg), grouped by bar type, host, partition, shape or rebar number. Covers rebar sets and the bars of area / path reinforcement. Scope: the given hosts, one view, or the whole model. Read-only.",
    {
      hostIds: z.array(z.number()).optional().describe("Only rebar in these host elements."),
      viewId: z.number().optional().describe("Only rebar visible in this view."),
      activeViewOnly: z.boolean().optional().describe("Only rebar visible in the active view. Default false (whole model)."),
      groupBy: z
        .enum(["barType", "host", "partition", "shape", "mark", "none"])
        .optional()
        .describe("How to group the totals. 'mark' groups by Rebar Number. Default barType."),
      includeElements: z.boolean().optional().describe("Also list every rebar element with its own length and weight. Default false."),
      densityKgPerM3: z.number().positive().optional().describe("Steel density for the weight. Default 7850."),
    },
    { raw: true }
  );
}
