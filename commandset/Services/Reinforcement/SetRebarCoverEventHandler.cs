using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Sets the rebar cover of host elements, for all faces or for the top / bottom / other /
    /// exterior / interior faces. A cover type is picked by name, or by distance (created if missing).
    /// </summary>
    public class SetRebarCoverEventHandler : ReinforcementEventHandlerBase
    {
        private static readonly Dictionary<string, BuiltInParameter> FaceParameters =
            new Dictionary<string, BuiltInParameter>(StringComparer.OrdinalIgnoreCase)
            {
                ["top"] = BuiltInParameter.CLEAR_COVER_TOP,
                ["bottom"] = BuiltInParameter.CLEAR_COVER_BOTTOM,
                ["other"] = BuiltInParameter.CLEAR_COVER_OTHER,
                ["exterior"] = BuiltInParameter.CLEAR_COVER_EXTERIOR,
                ["interior"] = BuiltInParameter.CLEAR_COVER_INTERIOR
            };

        protected override string Title => "set rebar cover";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var hostIds = RebarHelpers.Ids(p["hostIds"]);
            var hosts = hostIds.Count > 0
                ? hostIds.Select(id => doc.GetElement(Utils.ElementIdExtensions.FromLong(id)) ?? throw new Exception($"No element with id {id}")).ToList()
                : new List<Element> { RebarHelpers.ResolveHost(uiDoc, null) };

            var faces = (p["faces"] as JArray)?.Select(t => t.Value<string>().Trim()).ToList() ?? new List<string> { "all" };
            bool allFaces = faces.Any(f => f.Equals("all", StringComparison.OrdinalIgnoreCase));
            foreach (var face in faces.Where(f => !f.Equals("all", StringComparison.OrdinalIgnoreCase)))
            {
                if (!FaceParameters.ContainsKey(face))
                    throw new Exception($"Invalid face '{face}'. Valid: all, {string.Join(", ", FaceParameters.Keys)}");
            }

            var cover = RebarHelpers.FindByIdOrName<RebarCoverType>(doc, p["coverTypeId"]?.Value<long?>(), p["coverTypeName"]?.Value<string>(), "Rebar cover type");
            double? distance = RebarHelpers.FeetOrNull(p["distanceMm"]);
            if (cover == null && !distance.HasValue)
                throw new Exception("Pass coverTypeName, coverTypeId or distanceMm");
            if (distance.HasValue && distance.Value < 0)
                throw new Exception("distanceMm cannot be negative");

            var notes = new List<string>();
            var warnings = new List<string>();
            int changed = 0;
            RebarTransaction.Run(doc, "MCP: Set Rebar Cover", warnings, () =>
            {
                if (cover == null)
                {
                    cover = new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType)).Cast<RebarCoverType>()
                        .FirstOrDefault(c => Math.Abs(c.CoverDistance - distance.Value) < 1e-6);
                    if (cover == null)
                    {
                        string name = $"Rebar Cover {RebarHelpers.ToMm(distance.Value):0.#} mm";
                        cover = RebarCoverType.Create(doc, name, distance.Value);
                        notes.Add($"Created cover type '{name}'");
                    }
                }

                foreach (var host in hosts)
                {
                    if (!RebarHostData.IsValidHost(host))
                    {
                        notes.Add($"{host.Id.GetValue()} ({host.Category?.Name}) cannot host rebar: skipped");
                        continue;
                    }

                    if (allFaces)
                    {
                        RebarHostData.GetRebarHostData(host).SetCommonCoverType(cover);
                        changed++;
                        continue;
                    }

                    bool any = false;
                    foreach (var face in faces)
                    {
                        var parameter = host.get_Parameter(FaceParameters[face]);
                        if (parameter == null || parameter.IsReadOnly)
                        {
                            notes.Add($"{host.Id.GetValue()} ({host.Category?.Name}) has no settable '{face}' cover");
                            continue;
                        }
                        parameter.Set(cover.Id);
                        any = true;
                    }
                    if (any)
                        changed++;
                }
            });

            var response = new Dictionary<string, object>
            {
                ["coverType"] = new { id = cover.Id.GetValue(), name = cover.Name, distanceMm = RebarHelpers.ToMm(cover.CoverDistance) },
                ["hosts"] = hosts.Where(RebarHostData.IsValidHost).Select(h => new
                {
                    hostId = h.Id.GetValue(),
                    category = h.Category?.Name,
                    covers = GetHostRebarEventHandler.ReadCovers(doc, h)
                }).ToList()
            };
            if (notes.Count > 0)
                response["notes"] = notes;
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return new AIResult<object>
            {
                Success = changed > 0,
                Message = $"Set cover '{cover.Name}' ({RebarHelpers.ToMm(cover.CoverDistance)} mm) on {changed} of {hosts.Count} host(s). "
                          + "Rebar constrained to the cover moves with it.",
                Response = response
            };
        }
    }
}
