import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool } from "../utils/rebarTool.js";

export function registerSetRebarCoverTool(server: McpServer) {
  registerRebarTool(
    server,
    "set_rebar_cover",
    `Set the rebar cover of host elements (beams, columns, walls, floors, foundations). Pick a cover type by name or id, or give distanceMm: a cover type with that distance is reused, or created if the model has none.
- faces: 'all' (default) or any of top, bottom, other, exterior, interior. Beams and floors have top / bottom / other; walls have exterior / interior / other.
- Rebar constrained to the cover moves with it. Returns each host's covers after the change.`,
    {
      hostIds: z.array(z.number()).optional().describe("Host element IDs. If omitted, the single selected element is used."),
      coverTypeName: z.string().optional().describe("Rebar cover type name (see get_rebar_types)."),
      coverTypeId: z.number().optional().describe("Rebar cover type element ID."),
      distanceMm: z.number().min(0).optional().describe("Cover distance in mm, used when no cover type is named."),
      faces: z
        .array(z.enum(["all", "top", "bottom", "other", "exterior", "interior"]))
        .optional()
        .describe("Which faces get the cover. Default ['all']."),
    }
  );
}
