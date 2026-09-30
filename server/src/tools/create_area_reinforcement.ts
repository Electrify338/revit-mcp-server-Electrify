import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, pointMm, vector } from "../utils/rebarTool.js";

const layerSchema = z.object({
  active: z.boolean().optional().describe("Whether this layer has bars."),
  spacingMm: z.number().positive().optional().describe("Bar spacing in this layer, mm."),
  barTypeName: z.string().optional().describe("Bar type of this layer."),
});

export function registerCreateAreaReinforcementTool(server: McpServer) {
  registerRebarTool(
    server,
    "create_area_reinforcement",
    `Create area reinforcement (a mesh of bars in up to four layers) in a structural floor, foundation slab or wall: over the whole host, or inside a boundary.
- Layers: topMajor, topMinor, bottomMajor, bottomMinor. In a wall, 'top' is the exterior face and 'bottom' the interior. Major bars run along majorDirection, minor bars square to it.
- Without 'layers' all four layers use barTypeName at the type's default spacing.
- convertToRebar: true removes the area system and leaves ordinary rebar sets that can be edited, tagged and scheduled individually.`,
    {
      hostId: z.number().optional().describe("Structural floor, foundation slab or wall. If omitted, the single selected element is used."),
      barTypeName: z.string().optional().describe("Rebar bar type name. Provide this or barTypeId."),
      barTypeId: z.number().optional().describe("Rebar bar type element ID."),
      hookTypeName: z.string().optional().describe("Hook type for the bar ends. Omit for no hooks."),
      hookTypeId: z.number().optional(),
      majorDirection: vector
        .optional()
        .describe("Direction of the major bars, in the plane of the host. Default: world X for slabs, along the wall for walls."),
      boundary: z
        .array(pointMm)
        .min(3)
        .optional()
        .describe("Closed outline to reinforce, model coordinates in mm, on the host's face. Omit to reinforce the whole host."),
      layers: z
        .object({
          topMajor: layerSchema.optional(),
          topMinor: layerSchema.optional(),
          bottomMajor: layerSchema.optional(),
          bottomMinor: layerSchema.optional(),
        })
        .optional()
        .describe("Per-layer settings."),
      areaReinforcementTypeName: z.string().optional().describe("Area reinforcement type name. Default: the model's default."),
      areaReinforcementTypeId: z.number().optional(),
      convertToRebar: z.boolean().optional().describe("Replace the area system with ordinary rebar sets. Default false."),
      showUnobscuredInActiveView: z.boolean().optional().describe("Show the bars through the concrete in the active view. Default false."),
    }
  );
}
