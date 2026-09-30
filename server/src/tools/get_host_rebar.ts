import { errorMessage } from "../utils/errorUtils.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { toolResponse, toolError } from "../utils/compactTool.js";

export function registerGetHostRebarTool(server: McpServer) {
  server.tool(
    "get_host_rebar",
    "Describe a rebar host (beam, column, wall, floor, foundation) and the rebar already in it. Returns whether it can host rebar, its rebar covers, a local frame (origin + x/y/z axes, mm) with the host's extents in that frame so bar points can be computed for create_rebar, and every rebar in it (bar type, shape, layout, quantity, spacing, lengths, hooks). Read-only.",
    {
      hostId: z
        .number()
        .optional()
        .describe("Element ID of the host. If omitted, the single selected element is used."),
      includeGeometry: z
        .boolean()
        .optional()
        .describe("Also return the centerline of the first bar of each rebar set (mm). Default false."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_host_rebar", {
            hostId: args.hostId,
            includeGeometry: args.includeGeometry,
          });
        }, 90000);

        return toolResponse("get_host_rebar", response);
      } catch (error) {
        return toolError("get_host_rebar", `Get host rebar failed: ${errorMessage(error)}`);
      }
    }
  );
}
