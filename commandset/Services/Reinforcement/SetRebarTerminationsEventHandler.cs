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
    /// Changes what is at the ends of existing rebar: hook, end treatment or crank (2026+), the
    /// side it turns to and its out-of-plane rotation.
    /// </summary>
    public class SetRebarTerminationsEventHandler : ReinforcementEventHandlerBase
    {
        private const string None = "none";

        protected override string Title => "set rebar terminations";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var rebars = RebarHelpers.ResolveReinforcement(uiDoc, p, false).Cast<RevitRebar>().ToList();
            var ends = new[] { p["start"] as JObject, p["end"] as JObject };
            if (ends[0] == null && ends[1] == null)
                throw new Exception("Pass 'start' and/or 'end' with what to change at that end of the bar");

            var warnings = new List<string>();
            RebarTransaction.Run(doc, "MCP: Set Rebar Terminations", warnings, () =>
            {
                foreach (var rebar in rebars)
                {
                    for (int end = 0; end < 2; end++)
                    {
                        if (ends[end] == null)
                            continue;
                        try
                        {
                            Apply(doc, rebar, end, ends[end]);
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"rebar {rebar.Id.GetValue()}, {(end == 0 ? "start" : "end")}: {ex.Message}");
                        }
                    }
                }
            });

            var response = new Dictionary<string, object>
            {
                ["rebar"] = rebars.Select(r => Describe(doc, r)).ToList()
            };
            bool orientationAsked = ends.Any(e => !string.IsNullOrWhiteSpace(e?["orientation"]?.Value<string>()));
            if (orientationAsked && RebarHelpers.Try(() => ReinforcementSettings.GetReinforcementSettings(doc).RebarShapeDefinesHooks))
                response["notes"] = new List<string>
                {
                    "This project has 'Include hooks in Rebar Shape definition' on, where the rebar shape decides the hook side: "
                    + "Revit may have ignored 'orientation'. Compare it with the values returned here."
                };
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return Ok($"Updated the terminations of {rebars.Count} rebar element(s)"
                      + (warnings.Count > 0 ? $" with {warnings.Count} Revit warning(s)" : ""), response);
        }

        private static void Apply(Document doc, RevitRebar rebar, int end, JObject spec)
        {
            string hookName = spec["hook"]?.Value<string>();
            long? hookId = spec["hookId"]?.Value<long?>();
            if (hookId.HasValue || !string.IsNullOrWhiteSpace(hookName))
            {
                if (IsNone(hookName))
                {
                    rebar.SetHookTypeId(end, ElementId.InvalidElementId);
                }
                else
                {
                    var hook = RebarHelpers.FindByIdOrName<RebarHookType>(doc, hookId, hookName, "Rebar hook type");
                    if (!rebar.CanUseHookType(hook.Id))
                        throw new Exception($"hook '{hook.Name}' ({hook.Style}) cannot be used on this rebar: "
                                            + "the bar type does not allow it or its style does not match the rebar shape (Standard vs Stirrup/Tie)");
                    rebar.SetHookTypeId(end, hook.Id);
                }
            }

            string treatmentName = spec["endTreatment"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(treatmentName))
            {
                var treatmentId = IsNone(treatmentName)
                    ? ElementId.InvalidElementId
                    : RebarHelpers.FindByIdOrName<EndTreatmentType>(doc, null, treatmentName, "End treatment type").Id;
                rebar.SetEndTreatmentTypeId(end, treatmentId);
            }

            string crankName = spec["crank"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(crankName))
                SetCrank(doc, rebar, end, crankName);

            string orientation = spec["orientation"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(orientation))
            {
#if REVIT2026_OR_GREATER
                rebar.SetTerminationOrientation(end, RebarHelpers.ParseEnum(orientation, "orientation", RebarTerminationOrientation.Left));
#else
                rebar.SetHookOrientation(end, RebarHelpers.ParseEnum(orientation, "orientation", RebarHookOrientation.Left));
#endif
            }

            double? rotationDeg = spec["rotationDeg"]?.Value<double?>();
            if (rotationDeg.HasValue)
            {
                double radians = rotationDeg.Value * Math.PI / 180.0;
#if REVIT2026_OR_GREATER
                rebar.SetTerminationRotationAngle(end, radians);
#else
                rebar.SetHookRotationAngle(radians, end);
#endif
            }
        }

        private static void SetCrank(Document doc, RevitRebar rebar, int end, string crankName)
        {
#if REVIT2026_OR_GREATER
            if (IsNone(crankName))
            {
                rebar.SetCrankTypeId(end, ElementId.InvalidElementId);
                return;
            }

            var cranks = RebarCrankTypeUtils.GetAllRebarCrankTypes(doc).Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
            var crank = cranks.FirstOrDefault(c => c.Name.Equals(crankName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (crank == null)
                throw new Exception($"crank type '{crankName}' not found. Available: "
                                    + (cranks.Count > 0 ? string.Join(", ", cranks.Select(c => c.Name)) : "none in this model"));
            rebar.SetCrankTypeId(end, crank.Id);
#else
            throw new Exception("cranks need Revit 2026 or later");
#endif
        }

        private static bool IsNone(string name)
        {
            return name != null && name.Trim().Equals(None, StringComparison.OrdinalIgnoreCase);
        }

        private static object Describe(Document doc, RevitRebar rebar)
        {
            return new
            {
                rebarId = rebar.Id.GetValue(),
                start = DescribeEnd(doc, rebar, 0),
                end = DescribeEnd(doc, rebar, 1)
            };
        }

        private static object DescribeEnd(Document doc, RevitRebar rebar, int end)
        {
            return new
            {
                hook = RebarHelpers.NameOf(doc, rebar.GetHookTypeId(end)),
                endTreatment = RebarHelpers.Try(() => RebarHelpers.NameOf(doc, rebar.GetEndTreatmentTypeId(end))),
#if REVIT2026_OR_GREATER
                crank = RebarHelpers.Try(() => RebarHelpers.NameOf(doc, rebar.GetCrankTypeId(end))),
                orientation = RebarHelpers.Try(() => rebar.GetTerminationOrientation(end).ToString()),
                rotationDeg = RebarHelpers.Try(() => (double?)Math.Round(rebar.GetTerminationRotationAngle(end) * 180.0 / Math.PI, 1))
#else
                orientation = RebarHelpers.Try(() => rebar.GetHookOrientation(end).ToString()),
                rotationDeg = RebarHelpers.Try(() => (double?)Math.Round(rebar.GetHookRotationAngle(end) * 180.0 / Math.PI, 1))
#endif
            };
        }
    }
}
