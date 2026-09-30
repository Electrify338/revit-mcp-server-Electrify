import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, rebarTargets } from "../utils/rebarTool.js";

export function registerSetRebarPresentationTool(server: McpServer) {
  registerRebarTool(
    server,
    "set_rebar_presentation",
    `Set how rebar sets are drawn in one view: which bars of each set show, and whether rebar shows through the concrete.
- mode: All = every bar, FirstLast = the two end bars, Middle = one bar in the middle, Select = only the bars left unhidden, Default = back to the project's setting.
- hideBarIndexes / showBarIndexes hide or show single bars (0-based) and switch the set to Select.
- unobscured: true shows rebar in front of the host's concrete (Revit's "View Unobscured").
- Without rebarIds, hostIds or a selection, all rebar visible in the view is changed. Single bars and sets seen end-on are skipped for the mode, with the reason.
Only bars that are drawn can be tagged, so set this before tag_rebar.`,
    {
      viewId: z.number().optional().describe("View to change. Default: the active view."),
      ...rebarTargets,
      mode: z.enum(["All", "FirstLast", "Middle", "Select", "Default"]).optional().describe("Presentation mode for the sets."),
      hideBarIndexes: z.array(z.number().int().min(0)).optional().describe("Bars to hide in this view."),
      showBarIndexes: z.array(z.number().int().min(0)).optional().describe("Bars to show again in this view."),
      unobscured: z.boolean().optional().describe("Show the rebar through the host's concrete in this view."),
    }
  );
}
