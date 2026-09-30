import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z, ZodRawShape } from "zod";
import { errorMessage } from "./errorUtils.js";
import { withRevitConnection } from "./ConnectionManager.js";
import { toolResponse, toolError, rawToolResponse } from "./compactTool.js";

/** Model point, mm. */
export const pointMm = z.object({
  x: z.number().describe("X in mm"),
  y: z.number().describe("Y in mm"),
  z: z.number().describe("Z in mm"),
});

export const vector = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number(),
});

/**
 * A point for annotation tools: either view coordinates {u, v} (mm along the view's right and up
 * directions from the view origin, see get_view_rebar) or model coordinates {x, y, z} (mm).
 * One object with optional fields rather than a union, which some MCP clients reject.
 */
export const viewPoint = z.object({
  u: z.number().optional().describe("View coordinate along the view's right direction, mm"),
  v: z.number().optional().describe("View coordinate along the view's up direction, mm"),
  x: z.number().optional().describe("Model X, mm (use x/y/z instead of u/v)"),
  y: z.number().optional().describe("Model Y, mm"),
  z: z.number().optional().describe("Model Z, mm"),
});

export const layoutSchema = z.object({
  rule: z
    .enum(["Single", "FixedNumber", "MaximumSpacing", "NumberWithSpacing", "MinimumClearSpacing"])
    .optional()
    .describe(
      "Single = one bar. FixedNumber needs number + arrayLengthMm. MaximumSpacing and MinimumClearSpacing need spacingMm + arrayLengthMm. NumberWithSpacing needs number + spacingMm."
    ),
  number: z.number().int().min(1).optional().describe("Number of bars in the set."),
  spacingMm: z.number().positive().optional().describe("Spacing between bars, mm."),
  arrayLengthMm: z.number().positive().optional().describe("Length the set is spread over, mm."),
  barsOnNormalSide: z.boolean().optional().describe("Lay the set out on the normal's side of the first bar."),
  includeFirstBar: z.boolean().optional(),
  includeLastBar: z.boolean().optional(),
});

/** Which rebar a tool acts on. With neither given, the selection in Revit is used. */
export const rebarTargets = {
  rebarIds: z.array(z.number()).optional().describe("Rebar element IDs."),
  hostIds: z
    .array(z.number())
    .optional()
    .describe("Host element IDs: acts on all rebar in these hosts. If rebarIds and hostIds are both omitted, the selection in Revit is used."),
};

/**
 * Registers a rebar tool whose arguments are passed to the Revit command of the same name
 * unchanged. `raw` skips response compaction (which cuts arrays at 100 items).
 */
export function registerRebarTool(
  server: McpServer,
  name: string,
  description: string,
  shape: ZodRawShape,
  options: { timeoutMs?: number; raw?: boolean } = {}
) {
  const label = name.replace(/_/g, " ");
  server.tool(name, description, shape, async (args) => {
    try {
      const response = await withRevitConnection(async (revitClient) => {
        return await revitClient.sendCommand(name, args);
      }, options.timeoutMs ?? 90000);

      return options.raw ? rawToolResponse(name, response) : toolResponse(name, response);
    } catch (error) {
      return toolError(name, `${label} failed: ${errorMessage(error)}`);
    }
  });
}
