import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, rebarTargets, pointMm, vector } from "../utils/rebarTool.js";

export function registerSpliceRebarTool(server: McpServer) {
  registerRebarTool(
    server,
    "splice_rebar",
    `Lap-splice rebar (Revit 2025+). Actions:
- by_rules (default): cut every given rebar that is longer than maxBarLengthMm into lapped pieces. Bars within the limit are left alone.
- at_points: splice one rebar at the given points along it.
- unify: join two bars connected by a splice back into one.
- remove: drop the splice relation at one end of a bar (the bars keep their lengths).
- get_chain: list the bars spliced together with a bar, with their lap lengths.
Lap length comes from the splice type and the bar type. Free-form, multiplanar, arc-shaped and grouped rebar cannot be spliced.`,
    {
      action: z.enum(["by_rules", "at_points", "unify", "remove", "get_chain"]).optional().describe("Default by_rules."),
      ...rebarTargets,
      rebarId: z.number().optional().describe("The rebar for at_points, remove and get_chain."),
      maxBarLengthMm: z.number().positive().optional().describe("by_rules: longest bar allowed (stock length). Default 12000."),
      minBarLengthMm: z.number().positive().optional().describe("by_rules: shortest piece allowed. Default 1000."),
      runOutPosition: z.enum(["Start", "End"]).optional().describe("by_rules: which end of the bar gets the short remaining piece. Default End."),
      points: z.array(pointMm).optional().describe("at_points: where to splice, on or near the bar, model coordinates, mm."),
      normal: vector.optional().describe("at_points: direction of the bar at the splice. Default: taken from the bar."),
      position: z
        .enum(["End1", "Middle", "End2"])
        .optional()
        .describe("at_points: where the lap sits relative to the point. Middle = half each side (default); End1 = towards the start of the bar; End2 = towards the end."),
      spliceTypeName: z.string().optional().describe("Rebar splice type name (see get_rebar_types, include ['spliceTypes']). Default: the model's default splice type."),
      spliceTypeId: z.number().optional().describe("Rebar splice type element ID."),
      firstRebarId: z.number().optional().describe("unify: the first bar; the result takes its layout."),
      secondRebarId: z.number().optional().describe("unify: the bar spliced to the first one."),
      end: z.enum(["start", "end"]).optional().describe("remove: which end of the bar."),
    },
    { timeoutMs: 180000 }
  );
}
