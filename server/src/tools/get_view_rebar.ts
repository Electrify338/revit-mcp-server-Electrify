import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool } from "../utils/rebarTool.js";

export function registerGetViewRebarTool(server: McpServer) {
  registerRebarTool(
    server,
    "get_view_rebar",
    `List the rebar visible in a view, with what the detailing tools need. Read-only. Call it before tag_rebar, create_bending_detail, create_multi_rebar_annotation or set_rebar_presentation.
Returns the view frame (origin, right, up: a view point {u, v} in mm = origin + u*right + v*up) and per rebar: host, bar type, shape, rebar number, quantity, spacing, presentation mode, hidden bars, how many bars can be tagged, and its extents boxUv in the view. Also the extents of each host.`,
    {
      viewId: z.number().optional().describe("View to read. Default: the active view."),
      hostIds: z.array(z.number()).optional().describe("Only rebar hosted by these elements."),
      parameterNames: z
        .array(z.string())
        .optional()
        .describe("Extra instance parameters to read from each rebar by name, e.g. a project's bar-position parameter."),
      maxElements: z.number().int().positive().optional().describe("Maximum rebar elements to describe. Default 300."),
    },
    { raw: true }
  );
}
