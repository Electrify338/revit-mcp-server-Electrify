import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, pointMm } from "../utils/rebarTool.js";

export function registerCreatePathReinforcementTool(server: McpServer) {
  registerRebarTool(
    server,
    "create_path_reinforcement",
    `Create path reinforcement in a structural floor, foundation slab or wall: equal bars laid square to a path and spaced along it, e.g. top bars over a support line or bars along a slab edge.
- points is the path (2 or more points, model mm, on the host's face). The bars extend to one side of it; flip puts them on the other side.
- convertToRebar: true removes the path system and leaves ordinary rebar sets.`,
    {
      hostId: z.number().optional().describe("Structural floor, foundation slab or wall. If omitted, the single selected element is used."),
      points: z.array(pointMm).min(2).describe("The path the bars are laid along, model coordinates, mm."),
      barTypeName: z.string().optional().describe("Rebar bar type name. Provide this or barTypeId."),
      barTypeId: z.number().optional().describe("Rebar bar type element ID."),
      barLengthMm: z.number().positive().optional().describe("Length of each bar, square to the path, mm. Default: the type's default."),
      spacingMm: z.number().positive().optional().describe("Bar spacing along the path, mm. Default: the type's default."),
      flip: z.boolean().optional().describe("Put the bars on the other side of the path. Default false."),
      startHookName: z.string().optional().describe("Hook type at the start of each bar. Omit for no hook."),
      startHookId: z.number().optional(),
      endHookName: z.string().optional().describe("Hook type at the end of each bar. Omit for no hook."),
      endHookId: z.number().optional(),
      shapeName: z.string().optional().describe("RebarShape for the bars. Default: a straight shape fitting the hooks."),
      shapeId: z.number().optional(),
      pathReinforcementTypeName: z.string().optional().describe("Path reinforcement type name. Default: the model's default."),
      pathReinforcementTypeId: z.number().optional(),
      convertToRebar: z.boolean().optional().describe("Replace the path system with ordinary rebar sets. Default false."),
      showUnobscuredInActiveView: z.boolean().optional().describe("Show the bars through the concrete in the active view. Default false."),
    }
  );
}
