import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { registerRebarTool, rebarTargets } from "../utils/rebarTool.js";

export function registerManageRebarNumberingTool(server: McpServer) {
  registerRebarTool(
    server,
    "manage_rebar_numbering",
    `Read and renumber rebar numbers (the Rebar Number used as bar mark). Revit numbers rebar per partition and gives identical bars the same number. Actions:
- list (default): partitions, the number ranges in use (more than one range = gaps) and how many rebar elements each holds.
- remove_gaps: close the gaps in a partition.
- shift: make a partition start at firstNumber.
- change_number: change one number to another that is not used yet.
- move_partition: rename a partition, numbers kept.
- append: add one partition's rebar to the end of another (the appended rebar is renumbered).
- merge: merge two or more partitions into a new one, renumbered without gaps.
- assign: put rebar into a partition.
- set_method: the project's numbering method (Revit 2026+).
The default partition has an empty name: pass "" for it. Returns the partitions after the change.`,
    {
      action: z
        .enum(["list", "remove_gaps", "shift", "change_number", "move_partition", "append", "merge", "assign", "set_method"])
        .optional()
        .describe("Default list."),
      partition: z.string().optional().describe("Partition name for remove_gaps, shift, change_number and assign."),
      firstNumber: z.number().int().min(1).optional().describe("shift: the new lowest number."),
      fromNumber: z.number().int().min(1).optional().describe("change_number: the number to change."),
      toNumber: z.number().int().min(1).optional().describe("change_number: the new number (must be unused)."),
      fromPartition: z.string().optional().describe("move_partition / append: the source partition."),
      toPartition: z.string().optional().describe("move_partition / merge: the new partition name. append: the existing target partition."),
      sourcePartitions: z.array(z.string()).optional().describe("merge: the partitions to merge (two or more)."),
      ...rebarTargets,
      numberingMethod: z
        .enum(["AssignUniqueNumberPerSet", "MatchSetsWithIdenticalBars", "NumberBarsIndividually"])
        .optional()
        .describe("set_method: how Revit numbers rebar in this project."),
    }
  );
}
