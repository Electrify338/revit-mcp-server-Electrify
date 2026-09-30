import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, rebarTargets, viewPoint } from "../utils/rebarTool.js";

export function registerCreateMultiRebarAnnotationTool(server: McpServer) {
  registerRebarTool(
    server,
    "create_multi_rebar_annotation",
    `Create a multi-rebar annotation in a view: one dimension line across the bars of a set (or several sets) with a single rebar tag. The usual call-out for stirrups along a beam or bars in a slab.
- The dimension line must run across the bars: for stirrups in a beam elevation that is 'horizontal'.
- Without positions, the dimension line goes 300 mm below the rebar and the tag 600 mm below.
- The annotation type sets the tag and dimension style: get_rebar_types with include ['multiRebarAnnotationTypes'].
Returns the annotation, dimension and tag ids.`,
    {
      viewId: z.number().optional().describe("View to annotate in. Default: the active view."),
      ...rebarTargets,
      dimensionDirection: z
        .enum(["horizontal", "vertical"])
        .optional()
        .describe("Direction of the dimension line in the view. Default horizontal."),
      dimensionLineOrigin: viewPoint.optional().describe("A point the dimension line passes through."),
      tagHead: viewPoint.optional().describe("Tag head position."),
      tagHasLeader: z.boolean().optional().describe("Give the tag a leader. Default false."),
      dimensionStyle: z
        .enum(["Linear", "LinearFixed"])
        .optional()
        .describe("Linear = aligned to the bars (default). LinearFixed = horizontal or vertical in the view."),
      typeName: z.string().optional().describe("Multi-rebar annotation type name. Default: the model's default."),
      typeId: z.number().optional().describe("Multi-rebar annotation type element ID."),
    }
  );
}
