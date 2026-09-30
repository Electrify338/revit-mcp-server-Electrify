using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Rebar numbers (the Rebar Number parameter) are handed out by Revit per partition. This tool
    /// reads the partitions and renumbers them: close gaps, shift the start, change one number,
    /// move / merge / append partitions, or put rebar into a partition.
    /// </summary>
    public class ManageRebarNumberingEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "manage rebar numbering";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var schema = NumberingSchema.GetNumberingSchema(doc, NumberingSchemaTypes.StructuralNumberingSchemas.Rebar)
                         ?? throw new Exception("This model has no rebar numbering schema");

            string action = (p["action"]?.Value<string>() ?? "list").Trim().ToLowerInvariant();
            if (action == "list")
                return Ok("Rebar numbering partitions", Describe(doc, schema));

            var warnings = new List<string>();
            string done = RebarTransaction.Run(doc, "MCP: Rebar Numbering", warnings, () => Apply(uiDoc, schema, action, p));

            var response = Describe(doc, schema);
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;
            return Ok(done, response);
        }

        // MoveSequence, AppendSequence, MergeSequences and AssignElementsToSequence are marked
        // obsolete in Revit 2027 because they only work on reinforcement schemas, which is what
        // this one is; there is no replacement yet.
#pragma warning disable CS0618
        private static string Apply(UIDocument uiDoc, NumberingSchema schema, string action, JObject p)
        {
            var doc = uiDoc.Document;
            switch (action)
            {
                case "remove_gaps":
                {
                    string partition = Partition(p, "partition");
                    schema.RemoveGaps(partition);
                    return $"Removed the gaps in partition '{partition}'";
                }
                case "shift":
                {
                    string partition = Partition(p, "partition");
                    int first = p["firstNumber"]?.Value<int?>() ?? throw new Exception("firstNumber is required");
                    schema.ShiftNumbers(partition, first);
                    return $"Partition '{partition}' now starts at {first}";
                }
                case "change_number":
                {
                    string partition = Partition(p, "partition");
                    int from = p["fromNumber"]?.Value<int?>() ?? throw new Exception("fromNumber is required");
                    int to = p["toNumber"]?.Value<int?>() ?? throw new Exception("toNumber is required");
                    int affected = schema.ChangeNumber(partition, from, to).Count;
                    return $"Changed number {from} to {to} in partition '{partition}' ({affected} element(s))";
                }
                case "move_partition":
                {
                    string from = Partition(p, "fromPartition");
                    string to = Partition(p, "toPartition");
                    schema.MoveSequence(from, to);
                    return $"Renamed partition '{from}' to '{to}' (numbers kept)";
                }
                case "append":
                {
                    string from = Partition(p, "fromPartition");
                    string to = Partition(p, "toPartition");
                    schema.AppendSequence(from, to);
                    return $"Appended partition '{from}' to '{to}' (the appended rebar was renumbered)";
                }
                case "merge":
                {
                    var sources = (p["sourcePartitions"] as JArray)?.Select(t => t.Value<string>() ?? "").ToList();
                    if (sources == null || sources.Count < 2)
                        throw new Exception("sourcePartitions needs at least two partition names");
                    string to = Partition(p, "toPartition");
                    schema.MergeSequences(sources, to);
                    return $"Merged {sources.Count} partitions into the new partition '{to}' (renumbered without gaps)";
                }
                case "assign":
                {
                    string partition = Partition(p, "partition");
                    if (!NumberingSchema.IsValidPartitionName(partition, out string message))
                        throw new Exception($"'{partition}' is not a valid partition name: {message}");
                    var rebars = RebarHelpers.ResolveReinforcement(uiDoc, p, true);
                    schema.AssignElementsToSequence(new HashSet<ElementId>(rebars.Select(r => r.Id)), partition);
                    return $"Put {rebars.Count} rebar element(s) into partition '{partition}'";
                }
                case "set_method":
                    return SetMethod(doc, p["numberingMethod"]?.Value<string>());
                default:
                    throw new Exception($"Invalid action '{action}'. Valid: list, remove_gaps, shift, change_number, move_partition, append, merge, assign, set_method");
            }
        }
#pragma warning restore CS0618

        private static string SetMethod(Document doc, string method)
        {
#if REVIT2026_OR_GREATER
            if (string.IsNullOrWhiteSpace(method))
                throw new Exception("numberingMethod is required: " + string.Join(", ", Enum.GetNames(typeof(ReinforcementNumberingMethod))));
            var value = RebarHelpers.ParseEnum(method, "numberingMethod", ReinforcementNumberingMethod.MatchSetsWithIdenticalBars);
            ReinforcementSettings.GetReinforcementSettings(doc).NumberingMethod = value;
            return $"Rebar numbering method set to {value}";
#else
            throw new Exception("the numbering method setting needs Revit 2026 or later");
#endif
        }

        /// <summary>The default partition has an empty name, so a missing name is not an error.</summary>
        private static string Partition(JObject p, string key)
        {
            return p[key]?.Value<string>() ?? "";
        }

        private static Dictionary<string, object> Describe(Document doc, NumberingSchema schema)
        {
            var counts = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rebar)
                .WhereElementIsNotElementType()
                .Where(RebarHelpers.IsReinforcement)
                .GroupBy(e => e.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.AsString() ?? "")
                .ToDictionary(g => g.Key, g => g.Count());

            var partitions = schema.GetNumberingSequences()
                .OrderBy(name => name)
                .Select(name => new
                {
                    partition = name,
                    isDefault = name.Length == 0,
                    rebarElements = counts.TryGetValue(name, out int count) ? count : 0,
                    numbers = RebarHelpers.Try(() => schema.GetNumbers(name).Select(range => new { from = range.Low, to = range.High }).ToList())
                })
                .ToList();

            var result = new Dictionary<string, object>
            {
                ["partitions"] = partitions,
                ["note"] = "numbers lists the ranges in use: more than one range means the partition has gaps. "
                           + "Revit gives identical bars the same number."
            };
#if REVIT2026_OR_GREATER
            result["numberingMethod"] = RebarHelpers.Try(() => ReinforcementSettings.GetReinforcementSettings(doc).NumberingMethod.ToString());
#endif
            return result;
        }
    }
}
