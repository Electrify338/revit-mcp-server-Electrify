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
    /// How rebar sets are drawn in one view: presentation mode (all bars, first and last, middle,
    /// selected bars), individual bars hidden or shown, and "view unobscured".
    /// </summary>
    public class SetRebarPresentationEventHandler : ReinforcementEventHandlerBase
    {
        /// <summary>Rebar and RebarInSystem have the same per-view calls but no common interface.</summary>
        private class Presentable
        {
            public long Id;
            public bool IsSet;
            public int Positions;
            public Func<View, bool> CanApply;
            public Action<View, RebarPresentationMode> SetMode;
            public Action<View> ClearMode;
            public Func<View, RebarPresentationMode> GetMode;
            public Action<View, int, bool> SetBarHidden;
            public Action<View, bool> SetUnobscured;

            public static Presentable From(Element element)
            {
                if (element is RevitRebar rebar)
                {
                    return new Presentable
                    {
                        Id = rebar.Id.GetValue(),
                        IsSet = rebar.LayoutRule != RebarLayoutRule.Single,
                        Positions = rebar.NumberOfBarPositions,
                        CanApply = rebar.CanApplyPresentationMode,
                        SetMode = rebar.SetPresentationMode,
                        ClearMode = rebar.ClearPresentationMode,
                        GetMode = rebar.GetPresentationMode,
                        SetBarHidden = rebar.SetBarHiddenStatus,
                        SetUnobscured = rebar.SetUnobscuredInView
                    };
                }

                var inSystem = (RebarInSystem)element;
                return new Presentable
                {
                    Id = inSystem.Id.GetValue(),
                    IsSet = inSystem.LayoutRule != RebarLayoutRule.Single,
                    Positions = inSystem.NumberOfBarPositions,
                    CanApply = inSystem.CanApplyPresentationMode,
                    SetMode = inSystem.SetPresentationMode,
                    ClearMode = inSystem.ClearPresentationMode,
                    GetMode = inSystem.GetPresentationMode,
                    SetBarHidden = inSystem.SetBarHiddenStatus,
                    SetUnobscured = inSystem.SetUnobscuredInView
                };
            }
        }

        protected override string Title => "set rebar presentation";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var view = RebarHelpers.ResolveView(uiDoc, p["viewId"]?.Value<long?>());
            string modeText = p["mode"]?.Value<string>();
            bool clearMode = modeText != null && modeText.Trim().Equals("Default", StringComparison.OrdinalIgnoreCase);
            var mode = clearMode || string.IsNullOrWhiteSpace(modeText)
                ? (RebarPresentationMode?)null
                : RebarHelpers.ParseEnum(modeText, "mode", RebarPresentationMode.All);
            var hide = RebarHelpers.Ints(p["hideBarIndexes"]);
            var show = RebarHelpers.Ints(p["showBarIndexes"]);
            var unobscured = p["unobscured"]?.Value<bool?>();

            if (mode == null && !clearMode && hide.Count == 0 && show.Count == 0 && unobscured == null)
                throw new Exception("Nothing to change: pass mode, hideBarIndexes, showBarIndexes or unobscured");

            var items = Targets(uiDoc, p, view).Select(Presentable.From).ToList();
            if (items.Count == 0)
                throw new Exception($"No rebar found in view '{view.Name}'");

            var skipped = new List<object>();
            var warnings = new List<string>();
            int changed = 0;
            RebarTransaction.Run(doc, "MCP: Set Rebar Presentation", warnings, () =>
            {
                foreach (var item in items)
                {
                    try
                    {
                        bool touched = false;
                        if (unobscured.HasValue)
                        {
                            item.SetUnobscured(view, unobscured.Value);
                            touched = true;
                        }

                        bool wantsMode = mode.HasValue || clearMode || hide.Count > 0 || show.Count > 0;
                        if (wantsMode)
                        {
                            if (!item.IsSet)
                            {
                                if (!touched)
                                    skipped.Add(new { rebarId = item.Id, reason = "single bar: presentation modes apply to sets only" });
                            }
                            else if (!item.CanApply(view))
                            {
                                skipped.Add(new { rebarId = item.Id, reason = "this view cannot show a presentation for this set (the set is seen end-on, or the view type does not support it)" });
                            }
                            else if (hide.Concat(show).Any(i => i < 0 || i >= item.Positions))
                            {
                                skipped.Add(new { rebarId = item.Id, reason = $"a bar index is outside 0..{item.Positions - 1}; nothing was changed for this set" });
                            }
                            else
                            {
                                if (clearMode)
                                    item.ClearMode(view);
                                else if (mode.HasValue)
                                    item.SetMode(view, mode.Value);
                                // Hiding single bars switches the set to the Select mode
                                foreach (int i in hide)
                                    item.SetBarHidden(view, i, true);
                                foreach (int i in show)
                                    item.SetBarHidden(view, i, false);
                                touched = true;
                            }
                        }

                        if (touched)
                            changed++;
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(new { rebarId = item.Id, reason = ex.Message });
                    }
                }
            });

            var response = new Dictionary<string, object>
            {
                ["viewId"] = view.Id.GetValue(),
                ["changed"] = changed,
                ["rebar"] = items.Where(i => i.IsSet).Select(i => new
                {
                    rebarId = i.Id,
                    presentationMode = RebarHelpers.Try(() => i.GetMode(view).ToString())
                }).ToList()
            };
            if (skipped.Count > 0)
                response["skipped"] = skipped;
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return new AIResult<object>
            {
                Success = changed > 0,
                Message = $"Changed the presentation of {changed} of {items.Count} rebar element(s) in view '{view.Name}'"
                          + (skipped.Count > 0 ? $", {skipped.Count} skipped" : ""),
                Response = response
            };
        }

        /// <summary>rebarIds / hostIds / selection as usual; with none of them, all rebar visible in the view.</summary>
        private static List<Element> Targets(UIDocument uiDoc, JObject p, View view)
        {
            bool explicitTargets = RebarHelpers.Ids(p["rebarIds"]).Count > 0 || RebarHelpers.Ids(p["hostIds"]).Count > 0
                                   || uiDoc.Selection.GetElementIds().Count > 0;
            if (explicitTargets)
                return RebarHelpers.ResolveReinforcement(uiDoc, p, true);

            return new FilteredElementCollector(uiDoc.Document, view.Id)
                .OfCategory(BuiltInCategory.OST_Rebar)
                .WhereElementIsNotElementType()
                .Where(RebarHelpers.IsReinforcement)
                .ToList();
        }
    }
}
