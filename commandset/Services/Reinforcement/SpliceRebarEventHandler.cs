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
    /// Lap splices (Revit 2025+): cut bars to a maximum stock length by rules, cut at given
    /// points, join two spliced bars back into one, remove a splice, or read a splice chain.
    /// </summary>
    public class SpliceRebarEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "splice rebar";

        protected override AIResult<object> Run(UIApplication app)
        {
#if REVIT2025_OR_GREATER
            var uiDoc = app.ActiveUIDocument;
            var p = Parameters;
            string action = (p["action"]?.Value<string>() ?? "by_rules").Trim().ToLowerInvariant();
            switch (action)
            {
                case "by_rules":
                    return ByRules(uiDoc, p);
                case "at_points":
                    return AtPoints(uiDoc, p);
                case "unify":
                    return Unify(uiDoc.Document, p);
                case "remove":
                    return Remove(uiDoc.Document, p);
                case "get_chain":
                    return GetChain(uiDoc.Document, p);
                default:
                    throw new Exception($"Invalid action '{action}'. Valid: by_rules, at_points, unify, remove, get_chain");
            }
#else
            throw new Exception("rebar splicing needs Revit 2025 or later");
#endif
        }

#if REVIT2025_OR_GREATER
        private AIResult<object> ByRules(UIDocument uiDoc, JObject p)
        {
            var doc = uiDoc.Document;
            var rebars = RebarHelpers.ResolveReinforcement(uiDoc, p, false).Cast<RevitRebar>().ToList();
            double maxLength = RebarHelpers.ToFeet(p["maxBarLengthMm"]?.Value<double?>() ?? 12000);
            double minLength = RebarHelpers.ToFeet(p["minBarLengthMm"]?.Value<double?>() ?? 1000);
            var runOut = RebarHelpers.ParseEnum(p["runOutPosition"]?.Value<string>(), "runOutPosition", RebarSpliceByRulesRunOutPosition.End);

            var results = new List<object>();
            var warnings = new List<string>();
            var notes = new List<string>();
            int spliced = 0, created = 0;
            RebarTransaction.Run(doc, "MCP: Splice Rebar", warnings, () =>
            {
                var spliceTypeId = ResolveSpliceType(doc, p, notes);
                // Splicing by rules only works with the lap centred on the splice point
                using (var options = new RebarSpliceOptions(doc, spliceTypeId, RebarSplicePosition.Middle))
                using (var rules = RebarSpliceRules.Create(doc))
                {
                    rules.SetMaximumAndMinimumBarLength(maxLength, minLength);
                    rules.RunOutPosition = runOut;

                    foreach (var rebar in rebars)
                    {
                        long id = rebar.Id.GetValue();
                        try
                        {
                            var computed = RebarSpliceUtils.GetSpliceGeometries(doc, rebar.Id, options, rules);
                            if (computed.Error == RebarSpliceByRulesError.MaximumLengthBiggerThanBarLength)
                            {
                                results.Add(new { rebarId = id, spliced = false, reason = "no splice needed: the bar is not longer than maxBarLengthMm" });
                                continue;
                            }
                            if (computed.Error != RebarSpliceByRulesError.Success)
                            {
                                results.Add(new { rebarId = id, spliced = false, reason = computed.Error.ToString() });
                                continue;
                            }

                            var geometries = computed.GetSpliceGeometries();
                            if (geometries == null || geometries.Count == 0)
                            {
                                results.Add(new { rebarId = id, spliced = false, reason = "no splice needed: the bar is not longer than maxBarLengthMm" });
                                continue;
                            }

                            var pieces = RebarSpliceUtils.SpliceRebar(doc, rebar.Id, options, geometries);
                            spliced++;
                            created += pieces.Count;
                            results.Add(new { rebarId = id, spliced = true, splices = geometries.Count, resultRebarIds = pieces.Select(x => x.GetValue()).ToList() });
                        }
                        catch (Exception ex)
                        {
                            results.Add(new { rebarId = id, spliced = false, reason = ex.Message });
                        }
                    }
                }
            });

            var response = new Dictionary<string, object>
            {
                ["maxBarLengthMm"] = RebarHelpers.ToMm(maxLength),
                ["minBarLengthMm"] = RebarHelpers.ToMm(minLength),
                ["rebar"] = results
            };
            if (notes.Count > 0)
                response["notes"] = notes;
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return Ok($"Spliced {spliced} of {rebars.Count} rebar set(s) into {created} piece(s) at max {RebarHelpers.ToMm(maxLength)} mm", response);
        }

        private AIResult<object> AtPoints(UIDocument uiDoc, JObject p)
        {
            var doc = uiDoc.Document;
            var rebar = Rebar(doc, p["rebarId"], "rebarId");
            var points = (p["points"] as JArray)?.Select(RebarHelpers.PointFromMm).ToList();
            if (points == null || points.Count == 0)
                throw new Exception("points is required: where along the bar to splice, {x, y, z} in mm");
            var givenNormal = RebarHelpers.VectorFromJson(p["normal"]);
            var position = RebarHelpers.ParseEnum(p["position"]?.Value<string>(), "position", RebarSplicePosition.Middle);

            var warnings = new List<string>();
            var notes = new List<string>();
            var pieces = RebarTransaction.Run(doc, "MCP: Splice Rebar", warnings, () =>
            {
                var spliceTypeId = ResolveSpliceType(doc, p, notes);
                using (var options = new RebarSpliceOptions(doc, spliceTypeId, position))
                {
                    var geometries = new List<RebarSpliceGeometry>();
                    for (int i = 0; i < points.Count; i++)
                    {
                        var normal = givenNormal != null && givenNormal.GetLength() > 1e-9
                            ? givenNormal.Normalize()
                            : BarDirectionAt(rebar, points[i]);
                        var geometry = new RebarSpliceGeometry(points[i], normal);
                        var check = RebarSpliceUtils.CanRebarBeSpliced(rebar, options, geometry);
                        if (check != RebarSpliceError.Success)
                            throw new Exception($"point {i} cannot be used: {check}");
                        geometries.Add(geometry);
                    }
                    return RebarSpliceUtils.SpliceRebar(doc, rebar.Id, options, geometries).Select(x => x.GetValue()).ToList();
                }
            });

            var response = new Dictionary<string, object> { ["resultRebarIds"] = pieces };
            if (notes.Count > 0)
                response["notes"] = notes;
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;
            return Ok($"Spliced rebar at {points.Count} point(s) into {pieces.Count} piece(s)", response);
        }

        private AIResult<object> Unify(Document doc, JObject p)
        {
            var first = Rebar(doc, p["firstRebarId"], "firstRebarId");
            var second = Rebar(doc, p["secondRebarId"], "secondRebarId");
            var warnings = new List<string>();
            var unified = RebarTransaction.Run(doc, "MCP: Unify Rebar", warnings, () =>
            {
                var id = RebarSpliceUtils.UnifyRebarsIntoOne(doc, first.Id, second.Id);
                if (id == null || id == ElementId.InvalidElementId)
                    throw new Exception("Revit could not unify these two bars; they must be joined by a splice");
                return id.GetValue();
            });

            var response = new Dictionary<string, object> { ["rebarId"] = unified };
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;
            return Ok($"Unified the two bars into rebar {unified}", response);
        }

        private AIResult<object> Remove(Document doc, JObject p)
        {
            var rebar = Rebar(doc, p["rebarId"], "rebarId");
            int end = End(p["end"]?.Value<string>());
            var warnings = new List<string>();
            RebarTransaction.Run(doc, "MCP: Remove Rebar Splice", warnings, () => rebar.RemoveSplice(end));

            var response = new Dictionary<string, object> { ["rebarId"] = rebar.Id.GetValue() };
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;
            return Ok($"Removed the splice at the {(end == 0 ? "start" : "end")} of rebar {rebar.Id.GetValue()}; the bars keep their lengths", response);
        }

        private AIResult<object> GetChain(Document doc, JObject p)
        {
            var rebar = Rebar(doc, p["rebarId"], "rebarId");
            var chain = RebarSpliceUtils.GetSpliceChain(rebar)
                .Select(id => doc.GetElement(id) as RevitRebar)
                .Where(r => r != null);
            var items = chain.Select(r => new
            {
                rebarId = r.Id.GetValue(),
                barLengthMm = RebarHelpers.LengthParamMm(r, BuiltInParameter.REBAR_ELEM_LENGTH),
                lapAtStartMm = RebarHelpers.Try(() => (double?)RebarHelpers.ToMm(r.GetLapLength(0))),
                lapAtEndMm = RebarHelpers.Try(() => (double?)RebarHelpers.ToMm(r.GetLapLength(1)))
            }).ToList();
            return Ok($"Splice chain of {items.Count} rebar element(s)", new Dictionary<string, object> { ["chain"] = items });
        }

        private static ElementId ResolveSpliceType(Document doc, JObject p, List<string> notes)
        {
            var all = RebarSpliceTypeUtils.GetAllRebarSpliceTypes(doc).Select(id => doc.GetElement(id)).Where(e => e != null).ToList();

            var typeId = p["spliceTypeId"]?.Value<long?>();
            if (typeId.HasValue)
            {
                var byId = all.FirstOrDefault(t => t.Id.GetValue() == typeId.Value);
                return byId?.Id ?? throw new Exception($"Element {typeId.Value} is not a rebar splice type");
            }

            string name = p["spliceTypeName"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                var byName = all.FirstOrDefault(t => t.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
                return byName?.Id ?? throw new Exception($"Rebar splice type '{name}' not found. Available: "
                                                         + (all.Count > 0 ? string.Join(", ", all.Select(t => t.Name)) : "none"));
            }

            var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.RebarSpliceType);
            if (defaultId != null && defaultId != ElementId.InvalidElementId)
                return defaultId;
            if (all.Count > 0)
                return all[0].Id;
            notes.Add("The model had no rebar splice type, so 'Lap Splice' was created with Revit's default lap settings: "
                      + "check the lap lengths on the bar types (Splice Lengths) before relying on them");
            return RebarSpliceTypeUtils.CreateRebarSpliceType(doc, "Lap Splice").Id;
        }

        /// <summary>Direction of the bar at the point of its centerline nearest to <paramref name="point"/>.</summary>
        private static XYZ BarDirectionAt(RevitRebar rebar, XYZ point)
        {
            Curve nearest = null;
            double best = double.MaxValue;
            double parameter = 0;
            foreach (var curve in rebar.GetCenterlineCurves(false, true, true, MultiplanarOption.IncludeOnlyPlanarCurves, 0))
            {
                var projection = curve.Project(point);
                if (projection != null && projection.Distance < best)
                {
                    best = projection.Distance;
                    nearest = curve;
                    parameter = projection.Parameter;
                }
            }
            if (nearest == null)
                throw new Exception("could not find the bar near this point; pass 'normal' (the bar direction at the splice)");
            return nearest.ComputeDerivatives(parameter, false).BasisX.Normalize();
        }

        private static RevitRebar Rebar(Document doc, JToken token, string what)
        {
            var id = token?.Value<long?>() ?? throw new Exception($"{what} is required");
            return doc.GetElement(Utils.ElementIdExtensions.FromLong(id)) as RevitRebar
                   ?? throw new Exception($"Element {id} is not a Rebar element");
        }

        private static int End(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new Exception("end is required: 'start' or 'end'");
            switch (value.Trim().ToLowerInvariant())
            {
                case "start":
                case "0":
                    return 0;
                case "end":
                case "1":
                    return 1;
                default:
                    throw new Exception($"Invalid end '{value}'. Valid: start, end");
            }
        }
#endif
    }
}
