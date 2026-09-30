using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Splits one rebar set into several sets at the given bar indexes (Revit 2026.3+), e.g. to
    /// give the stirrups near the supports a different spacing from those at midspan.
    /// </summary>
    public class SplitRebarSetEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "split rebar set";

        protected override AIResult<object> Run(UIApplication app)
        {
#if REVIT2026_OR_GREATER
            var doc = app.ActiveUIDocument.Document;
            var p = Parameters;

            var id = p["rebarId"]?.Value<long?>() ?? throw new Exception("rebarId is required");
            var rebar = doc.GetElement(Utils.ElementIdExtensions.FromLong(id)) as RevitRebar
                        ?? throw new Exception($"Element {id} is not a Rebar element");
            var indexes = RebarHelpers.Ints(p["barIndexes"]);
            if (indexes.Count == 0)
                throw new Exception("barIndexes is required: each index is the last bar of a new set (0-based)");
            int positions = rebar.NumberOfBarPositions;
            var outOfRange = indexes.Where(i => i < 0 || i > positions - 1).ToList();
            if (outOfRange.Count > 0)
                throw new Exception($"barIndexes {string.Join(", ", outOfRange)} are outside 0..{positions - 1}");

            bool constrain = p["constrainSplitSets"]?.Value<bool?>() ?? true;
            bool wholeChain = p["splitAllSetsInSpliceChain"]?.Value<bool?>() ?? false;

            var warnings = new List<string>();
            List<long> resultIds;
            try
            {
                resultIds = RebarTransaction.Run(doc, "MCP: Split Rebar Set", warnings,
                    () => Split(doc, rebar.Id, indexes, constrain, wholeChain));
            }
            catch (MissingMethodException)
            {
                throw new Exception("splitting a rebar set needs Revit 2026.3 or later; install the latest Revit 2026 update");
            }

            var sets = resultIds
                .Select(x => doc.GetElement(Utils.ElementIdExtensions.FromLong(x)) as RevitRebar)
                .Where(r => r != null)
                .Select(RebarLayout.Describe)
                .ToList();
            var response = new Dictionary<string, object>
            {
                ["rebarIds"] = resultIds,
                ["sets"] = sets
            };
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return Ok($"Split rebar {id} ({positions} bar positions) into {resultIds.Count} set(s)", response);
#else
            throw new Exception("splitting a rebar set needs Revit 2026.3 or later");
#endif
        }

#if REVIT2026_OR_GREATER
        // Kept out of line: Rebar.SplitRebar does not exist before Revit 2026.3, and the missing
        // method only surfaces when the method that calls it is compiled.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<long> Split(Document doc, ElementId rebarId, List<int> indexes, bool constrain, bool wholeChain)
        {
            return RevitRebar.SplitRebar(doc, rebarId, new HashSet<int>(indexes), constrain, wholeChain)
                .Select(x => x.GetValue())
                .ToList();
        }
#endif
    }
}
