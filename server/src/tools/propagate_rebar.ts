import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool } from "../utils/rebarTool.js";

export function registerPropagateRebarTool(server: McpServer) {
  registerRebarTool(
    server,
    "propagate_rebar",
    `Copy the rebar of one host into other hosts and adapt it to each (Revit's Propagate Rebar). Use it to reinforce one beam / column / footing, then repeat it on the similar ones.
- Source: rebarIds, or all rebar in sourceHostId, or the selection. All source rebar must sit in one host.
- Destinations must be the same category as the source host and able to host rebar. Bars follow the destination's size, so hosts of a different length or section get adapted bars.
- Rebar inside a group is skipped. Each destination reports its new rebar ids or why it failed; the whole call is one undo step.`,
    {
      destinationHostIds: z.array(z.number()).min(1).describe("Host element IDs to copy the rebar into."),
      sourceHostId: z.number().optional().describe("Host whose rebar is copied (all of it)."),
      rebarIds: z.array(z.number()).optional().describe("Copy only these rebar elements (all from one host)."),
    },
    { timeoutMs: 180000 }
  );
}
