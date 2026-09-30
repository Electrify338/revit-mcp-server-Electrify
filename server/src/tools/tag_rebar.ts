import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, rebarTargets, viewPoint } from "../utils/rebarTool.js";

const offsetSchema = z.object({
  u: z.number().optional().describe("Along the view's right direction, mm."),
  v: z.number().optional().describe("Along the view's up direction, mm."),
});

export function registerTagRebarTool(server: McpServer) {
  registerRebarTool(
    server,
    "tag_rebar",
    `Place structural rebar tags in a view. Each tag is attached to one bar of the set; a bar the view does not draw gives an invisible tag, so bars are tried (first, middle, last, then in order) until the tag is visible.
- Simple: rebarIds (or hostIds / the selection) with offsetMm: each tag head goes at the centre of its rebar in the view plus the offset.
- Exact: 'tags', one entry per tag, with head (and leaderEnd / leaderElbow when leader is true) as view points {u, v} or model points {x, y, z}.
- Points and the rebar extents come from get_view_rebar. Tag types from get_rebar_types with include ['rebarTagTypes'].
Returns each tag's id, the bar it took and its text; failures are listed per rebar.`,
    {
      viewId: z.number().optional().describe("View to tag in. Default: the active view."),
      ...rebarTargets,
      tagTypeName: z
        .string()
        .optional()
        .describe("Tag type as 'Family : Type', or just the type or family name. Default: the model's default rebar tag."),
      tagTypeId: z.number().optional().describe("Tag type element ID."),
      leader: z.boolean().optional().describe("Tags have a leader. Default false."),
      orientation: z.enum(["Horizontal", "Vertical"]).optional().describe("Tag orientation. Default Horizontal."),
      offsetMm: offsetSchema.optional().describe("Tag head offset from the centre of each rebar in the view, mm. Default none."),
      tags: z
        .array(
          z.object({
            rebarId: z.number().describe("Rebar element to tag."),
            barIndex: z.number().int().min(0).optional().describe("Which bar of the set to attach to. Default: the first one that gives a visible tag."),
            head: viewPoint.optional().describe("Tag head position. Default: the rebar's centre plus offsetMm."),
            offsetMm: offsetSchema.optional().describe("Offset for this tag instead of the common one."),
            leaderEnd: viewPoint.optional().describe("Free end of the leader (arrow tip). Omit to keep the leader attached to the bar."),
            leaderElbow: viewPoint.optional().describe("Elbow point of the leader."),
          })
        )
        .optional()
        .describe("One entry per tag, for exact placement. Used instead of rebarIds / hostIds."),
    },
    { timeoutMs: 180000 }
  );
}
