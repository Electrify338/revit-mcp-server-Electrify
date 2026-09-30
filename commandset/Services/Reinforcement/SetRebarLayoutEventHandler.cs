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
    /// Changes existing rebar sets: layout rule, number, spacing, which bars are included,
    /// individual bar moves and flipping.
    /// </summary>
    public class SetRebarLayoutEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "set rebar layout";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var rebars = RebarHelpers.ResolveReinforcement(uiDoc, p, false).Cast<RevitRebar>().ToList();
            var layout = RebarLayout.FromJson(p["layout"] as JObject);
            var exclude = RebarHelpers.Ints(p["excludeBarIndexes"]);
            var include = RebarHelpers.Ints(p["includeBarIndexes"]);
            var resetMoves = RebarHelpers.Ints(p["resetMovedBarIndexes"]);
            var moves = (p["moveBars"] as JArray ?? new JArray())
                .Select(m => new
                {
                    Index = m["index"]?.Value<int?>() ?? throw new Exception("moveBars items need 'index'"),
                    Offset = RebarHelpers.PointFromMm(m["offsetMm"] ?? throw new Exception("moveBars items need 'offsetMm' {x, y, z}"))
                })
                .ToList();
            bool flipSet = p["flipSet"]?.Value<bool?>() ?? false;
            bool flipBar = p["flipBar"]?.Value<bool?>() ?? false;

            if (layout == null && exclude.Count == 0 && include.Count == 0 && resetMoves.Count == 0 && moves.Count == 0 && !flipSet && !flipBar)
                throw new Exception("Nothing to change: pass layout, excludeBarIndexes, includeBarIndexes, moveBars, resetMovedBarIndexes, flipSet or flipBar");

            var warnings = new List<string>();
            RebarTransaction.Run(doc, "MCP: Set Rebar Layout", warnings, () =>
            {
                foreach (var rebar in rebars)
                {
                    try
                    {
                        layout?.Apply(rebar);
                        // The bar positions the index operations check against follow the new layout
                        if (layout != null && (include.Count > 0 || exclude.Count > 0 || resetMoves.Count > 0 || moves.Count > 0))
                            doc.Regenerate();
                        foreach (int i in include)
                            rebar.SetBarIncluded(true, i);
                        foreach (int i in exclude)
                            rebar.SetBarIncluded(false, i);
                        foreach (int i in resetMoves)
                            rebar.ResetMovedBarTransform(i);
                        foreach (var move in moves)
                            rebar.MoveBarInSet(move.Index, Transform.CreateTranslation(move.Offset));
                        if (flipSet)
                            Flip(rebar, false);
                        if (flipBar)
                            Flip(rebar, true);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"rebar {rebar.Id.GetValue()}: {ex.Message}");
                    }
                }
            });

            var response = new Dictionary<string, object>
            {
                ["rebar"] = rebars.Select(RebarLayout.Describe).ToList()
            };
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return Ok($"Updated {rebars.Count} rebar set(s)"
                      + (warnings.Count > 0 ? $" with {warnings.Count} Revit warning(s)" : ""), response);
        }

        /// <summary>
        /// FlipRebarSet arrived in Revit 2023.1 and FlipRebar in 2026.1: on an un-updated Revit the
        /// call is missing at run time, which surfaces when the method that contains it is compiled.
        /// </summary>
        private static void Flip(RevitRebar rebar, bool bar)
        {
            if (!rebar.IsRebarShapeDriven())
                throw new Exception("free-form rebar cannot be flipped by this tool");
            try
            {
                if (bar)
                    FlipBarCore(rebar);
                else
                    FlipSetCore(rebar);
            }
            catch (MissingMethodException)
            {
                throw new Exception(bar
                    ? "flipBar needs Revit 2026.1 or later; install the latest Revit update"
                    : "flipSet needs Revit 2023.1 or later; install the latest Revit update");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FlipSetCore(RevitRebar rebar)
        {
            rebar.GetShapeDrivenAccessor().FlipRebarSet();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FlipBarCore(RevitRebar rebar)
        {
#if REVIT2026_OR_GREATER
            rebar.GetShapeDrivenAccessor().FlipRebar();
#else
            throw new Exception("flipBar needs Revit 2026.1 or later");
#endif
        }
    }
}
