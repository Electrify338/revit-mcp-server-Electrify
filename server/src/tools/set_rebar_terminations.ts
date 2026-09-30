import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, rebarTargets } from "../utils/rebarTool.js";

const endSchema = z.object({
  hook: z.string().optional().describe("Hook type name (see get_rebar_types), or 'none' to remove the hook."),
  hookId: z.number().optional().describe("Hook type element ID (instead of hook)."),
  orientation: z
    .enum(["Left", "Right"])
    .optional()
    .describe("Side the hook or crank turns to: stand at this end, the bar behind you, the bar's normal pointing up."),
  rotationDeg: z.number().optional().describe("Out-of-plane rotation of the hook or crank, degrees."),
  endTreatment: z.string().optional().describe("End treatment type name (e.g. a threaded end), or 'none'."),
  crank: z.string().optional().describe("Crank type name, or 'none'. Revit 2026+."),
});

export function registerSetRebarTerminationsTool(server: McpServer) {
  registerRebarTool(
    server,
    "set_rebar_terminations",
    `Change what is at the ends of existing rebar: hook, end treatment or crank, the side it turns to and its rotation. 'start' and 'end' are the bar's own ends (first and last point of its centerline).
- A hook must suit the bar: Standard hooks on standard bars, Stirrup/Tie hooks on stirrups, and allowed by the bar type.
- An end holds one of hook, crank or end treatment: setting one clears the others at that end.
- Returns the terminations after the change. Revit errors roll everything back.`,
    {
      ...rebarTargets,
      start: endSchema.optional().describe("Changes at the start of the bar."),
      end: endSchema.optional().describe("Changes at the end of the bar."),
    }
  );
}
