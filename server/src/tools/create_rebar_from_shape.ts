import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, pointMm, vector, layoutSchema } from "../utils/rebarTool.js";

export function registerCreateRebarFromShapeTool(server: McpServer) {
  registerRebarTool(
    server,
    "create_rebar_from_shape",
    `Create rebar as an instance of a named RebarShape and fit it to a rectangle. Easier than create_rebar for stirrups and standard shapes: no bar-by-bar points.
- origin is one corner of the rectangle; xDirection and yDirection are its two edges (square to each other); widthMm and heightMm their lengths. The shape's own x/y axes map to these.
- The rectangle is the bar's outline, so place it inside the cover (see get_host_rebar for covers and extents).
- A set is laid out along xDirection x yDirection (the rectangle's normal): for a beam stirrup, that is along the beam.
- Hooks come from the shape; change them with set_rebar_terminations.
- Revit warnings are returned in revitWarnings; errors roll the change back.`,
    {
      hostId: z.number().optional().describe("Host element ID. If omitted, the single selected element is used."),
      shapeName: z.string().optional().describe("RebarShape name (see get_rebar_types). Provide this or shapeId."),
      shapeId: z.number().optional().describe("RebarShape element ID."),
      barTypeName: z.string().optional().describe("Rebar bar type name. Provide this or barTypeId."),
      barTypeId: z.number().optional().describe("Rebar bar type element ID."),
      origin: pointMm.describe("Lower-left corner of the rectangle, model coordinates, mm."),
      xDirection: vector.describe("Direction of the rectangle's first edge (the shape's x axis)."),
      yDirection: vector.describe("Direction of the second edge (the shape's y axis), square to xDirection."),
      widthMm: z.number().positive().optional().describe("Rectangle size along xDirection, mm. With heightMm, the shape is stretched to fit; omit both to keep the shape's default size."),
      heightMm: z.number().positive().optional().describe("Rectangle size along yDirection, mm."),
      layout: layoutSchema.optional().describe("How many bars and how they are spaced. Default Single."),
      showUnobscuredInActiveView: z.boolean().optional().describe("Show the rebar through the concrete in the active view. Default false."),
    }
  );
}
