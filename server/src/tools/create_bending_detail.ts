import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, rebarTargets, viewPoint } from "../utils/rebarTool.js";

export function registerCreateBendingDetailTool(server: McpServer) {
  registerRebarTool(
    server,
    "create_bending_detail",
    `Rebar bending details in a view (Revit 2024+): the drawn bar shape with its segment dimensions. Actions:
- create (default): one bending detail per rebar. Give 'items' with a position each, or just rebarIds / hostIds / the selection and they are stacked in rows below the rebar: the first row gapBelowMm under the lowest rebar, the next ones rowSpacingMm further down, each centred under the rebar it shows.
- move: reposition or rotate an existing bending detail.
- list: the bending details in the view with their rebar, position and extents.
Positions are view points {u, v} or model points {x, y, z} (see get_view_rebar). Bending details are annotations, so they may sit outside the crop region. Bending detail types: get_rebar_types with include ['bendingDetailTypes'].`,
    {
      action: z.enum(["create", "move", "list"]).optional().describe("Default create."),
      viewId: z.number().optional().describe("View to work in. Default: the active view."),
      ...rebarTargets,
      items: z
        .array(
          z.object({
            rebarId: z.number().describe("Rebar the bending detail shows."),
            position: viewPoint.optional().describe("Where to place it. Omit to let it be stacked in the rows."),
            rotationDeg: z.number().optional().describe("Rotation in the view, degrees. Default 0."),
            barIndex: z.number().int().min(0).optional().describe("Which bar of the set it represents. Default: the first one Revit accepts."),
          })
        )
        .optional()
        .describe("create: one entry per bending detail, in row order. Used instead of rebarIds / hostIds."),
      typeName: z.string().optional().describe("create: bending detail type name. Default: the first type in the model."),
      typeId: z.number().optional().describe("create: bending detail type element ID."),
      gapBelowMm: z.number().optional().describe("create: clear distance from the lowest rebar down to the first row, mm. Default 600."),
      rowSpacingMm: z.number().positive().optional().describe("create: distance between rows, mm. Default 1150."),
      startV: z.number().optional().describe("create: view v coordinate of the first row, mm, instead of gapBelowMm."),
      centerOnRebar: z
        .boolean()
        .optional()
        .describe("create: after placing, shift each detail sideways so its centre lines up with its rebar. Default true."),
      bendingDetailId: z.number().optional().describe("move: the bending detail to move."),
      position: viewPoint.optional().describe("move: new position."),
      rotationDeg: z.number().optional().describe("move: new rotation, degrees."),
    },
    { timeoutMs: 180000 }
  );
}
