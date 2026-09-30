import { errorMessage } from "../utils/errorUtils.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { toolResponse, toolError } from "../utils/compactTool.js";

export function registerGetRebarTypesTool(server: McpServer) {
  server.tool(
    "get_rebar_types",
    "List what rebar and its annotation are made from in the open model. By default: bar types (diameters, bend diameters), hook types (angle, style), rebar shapes (name, style) and cover types (distance). On request: end treatments, crank types (2026+), splice types (2025+), rebar tag types, bending detail types (2024+), multi-rebar annotation types, area and path reinforcement types. Read-only. Call this before the other rebar tools to pick valid names.",
    {
      include: z
        .array(
          z.enum([
            "barTypes",
            "hookTypes",
            "shapes",
            "coverTypes",
            "endTreatmentTypes",
            "crankTypes",
            "spliceTypes",
            "rebarTagTypes",
            "bendingDetailTypes",
            "multiRebarAnnotationTypes",
            "areaReinforcementTypes",
            "pathReinforcementTypes",
          ])
        )
        .optional()
        .describe("Which lists to return. Default: barTypes, hookTypes, shapes, coverTypes. The others are returned only when asked for."),
      nameFilter: z
        .string()
        .optional()
        .describe("Only items whose name contains this text (case-insensitive), e.g. '16' or 'Stirrup'."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_rebar_types", {
            include: args.include,
            nameFilter: args.nameFilter,
          });
        }, 60000);

        return toolResponse("get_rebar_types", response);
      } catch (error) {
        return toolError("get_rebar_types", `Get rebar types failed: ${errorMessage(error)}`);
      }
    }
  );
}
