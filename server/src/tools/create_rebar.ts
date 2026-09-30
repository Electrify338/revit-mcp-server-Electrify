import { errorMessage } from "../utils/errorUtils.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

const pointSchema = z.object({
  x: z.number().describe("X in mm"),
  y: z.number().describe("Y in mm"),
  z: z.number().describe("Z in mm"),
});

const vectorSchema = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number(),
});

export function registerCreateRebarTool(server: McpServer) {
  server.tool(
    "create_rebar",
    `Create shape-driven rebar (one bar or a set) in a concrete host from a polyline of bar centerline points in model coordinates (mm).
Workflow: get_rebar_types for valid names, get_host_rebar for the host's frame, extents and covers, then compute points inside the cover.
- Straight bar: 2 points plus 'normal'. Bent bar (L, U): 3+ points in one plane. Closed stirrup: repeat the first point at the end, style 'StirrupTie'.
- 'normal' is square to the bar's plane and is the direction a set is laid out along (stirrups: along the beam; bottom bars: across it).
- Revit picks or creates the matching RebarShape unless shapeName is given.
- Warnings Revit raises are returned in revitWarnings; errors roll the change back.`,
    {
      hostId: z.number().optional().describe("Host element ID. If omitted, the single selected element is used."),
      barTypeName: z.string().optional().describe("Rebar bar type name, e.g. '16M' or '16 mm'. Provide this or barTypeId."),
      barTypeId: z.number().optional().describe("Rebar bar type element ID."),
      style: z
        .enum(["Standard", "StirrupTie"])
        .optional()
        .describe("Standard for main bars, StirrupTie for stirrups/ties. Default Standard."),
      points: z
        .array(pointSchema)
        .min(2)
        .describe("Bar centerline as a polyline, mm, model coordinates. At least 2 points."),
      normal: vectorSchema
        .optional()
        .describe("Unit vector square to the bar plane; the set direction. Required for straight bars, derived from the points otherwise."),
      shapeName: z.string().optional().describe("Force this RebarShape (must fit the points and hooks)."),
      shapeId: z.number().optional().describe("Force this RebarShape by element ID."),
      startHookName: z.string().optional().describe("Hook type at the first point, e.g. 'Standard - 90 deg.'. Omit for no hook."),
      startHookId: z.number().optional(),
      endHookName: z.string().optional().describe("Hook type at the last point. Omit for no hook."),
      endHookId: z.number().optional(),
      startHookOrientation: z
        .enum(["Left", "Right"])
        .optional()
        .describe("Side the start hook turns to: stand at that end of the bar, bar behind you, normal pointing up; Left or Right. Default Left."),
      endHookOrientation: z.enum(["Left", "Right"]).optional().describe("Same for the end hook. Default Left."),
      layout: z
        .object({
          rule: z
            .enum(["Single", "FixedNumber", "MaximumSpacing", "NumberWithSpacing", "MinimumClearSpacing"])
            .describe("Single = one bar. FixedNumber needs number + arrayLengthMm. MaximumSpacing and MinimumClearSpacing need spacingMm + arrayLengthMm. NumberWithSpacing needs number + spacingMm."),
          number: z.number().int().min(1).optional().describe("Number of bars in the set."),
          spacingMm: z.number().positive().optional().describe("Spacing between bars, mm."),
          arrayLengthMm: z.number().positive().optional().describe("Length the set is spread over along the normal, mm."),
          barsOnNormalSide: z.boolean().optional().describe("Lay the set out on the normal's side of the first bar. Default true."),
          includeFirstBar: z.boolean().optional().describe("Default true."),
          includeLastBar: z.boolean().optional().describe("Default true."),
        })
        .optional()
        .describe("How many bars and how they are spaced. Default Single."),
      useExistingShapeIfPossible: z.boolean().optional().describe("Reuse a matching RebarShape. Default true."),
      createNewShape: z.boolean().optional().describe("Create a RebarShape when none matches. Default true."),
      showUnobscuredInActiveView: z
        .boolean()
        .optional()
        .describe("Show the rebar unobscured (through concrete) in the active view. Default false."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_rebar", args);
        }, 90000);

        return rawToolResponse("create_rebar", response);
      } catch (error) {
        return rawToolError("create_rebar", `Create rebar failed: ${errorMessage(error)}`);
      }
    }
  );
}
