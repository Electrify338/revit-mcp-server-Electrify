using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Path reinforcement: bars of one length laid square to a path, e.g. top bars over a support
    /// or edge bars along a slab edge. Can be converted to plain rebar sets.
    /// </summary>
    public class CreatePathReinforcementEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "create path reinforcement";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var host = RebarHelpers.ResolveHost(uiDoc, p["hostId"]?.Value<long?>());
            var barType = RebarHelpers.FindByIdOrName<RebarBarType>(doc, p["barTypeId"]?.Value<long?>(), p["barTypeName"]?.Value<string>(), "Rebar bar type")
                          ?? throw new Exception("barTypeName or barTypeId is required (see get_rebar_types)");
            var startHook = RebarHelpers.FindByIdOrName<RebarHookType>(doc, p["startHookId"]?.Value<long?>(), p["startHookName"]?.Value<string>(), "Rebar hook type");
            var endHook = RebarHelpers.FindByIdOrName<RebarHookType>(doc, p["endHookId"]?.Value<long?>(), p["endHookName"]?.Value<string>(), "Rebar hook type");
            var shape = RebarHelpers.FindByIdOrName<RebarShape>(doc, p["shapeId"]?.Value<long?>(), p["shapeName"]?.Value<string>(), "Rebar shape");
            var pathType = RebarHelpers.FindByIdOrName<PathReinforcementType>(doc, p["pathReinforcementTypeId"]?.Value<long?>(),
                p["pathReinforcementTypeName"]?.Value<string>(), "Path reinforcement type");

            if (shape != null && !PathReinforcement.IsValidRebarShapeId(doc, shape.Id))
                throw new Exception($"Rebar shape '{shape.Name}' cannot be used for path reinforcement; leave shapeName out to use the default");

            var curves = Path(p["points"] as JArray);
            bool flip = p["flip"]?.Value<bool?>() ?? false;
            double? spacing = RebarHelpers.FeetOrNull(p["spacingMm"]);
            double? barLength = RebarHelpers.FeetOrNull(p["barLengthMm"]);
            bool convertToRebar = p["convertToRebar"]?.Value<bool?>() ?? false;
            bool unobscured = p["showUnobscuredInActiveView"]?.Value<bool?>() ?? false;

            var notes = new List<string>();
            var warnings = new List<string>();
            var response = RebarTransaction.Run(doc, "MCP: Create Path Reinforcement", warnings, () =>
            {
                var typeId = pathType?.Id ?? DefaultTypeId(doc);
                var startHookId = startHook?.Id ?? ElementId.InvalidElementId;
                var endHookId = endHook?.Id ?? ElementId.InvalidElementId;
                var path = shape != null
                    ? PathReinforcement.Create(doc, host, curves, flip, typeId, barType.Id, startHookId, endHookId, shape.Id)
                    : PathReinforcement.Create(doc, host, curves, flip, typeId, barType.Id, startHookId, endHookId);

                if (spacing.HasValue && !Set(path, BuiltInParameter.PATH_REIN_SPACING, spacing.Value))
                    notes.Add("spacing could not be set on this path reinforcement");
                if (barLength.HasValue && !Set(path, BuiltInParameter.PATH_REIN_LENGTH_1, barLength.Value))
                    notes.Add("bar length could not be set on this path reinforcement");

                doc.Regenerate();

                var result = new Dictionary<string, object>
                {
                    ["hostId"] = host.Id.GetValue(),
                    ["barType"] = barType.Name,
                    ["spacingMm"] = RebarHelpers.LengthParamMm(path, BuiltInParameter.PATH_REIN_SPACING),
                    ["barLengthMm"] = RebarHelpers.LengthParamMm(path, BuiltInParameter.PATH_REIN_LENGTH_1),
                    ["numberOfBars"] = path.get_Parameter(BuiltInParameter.PATH_REIN_NUMBER_OF_BARS)?.AsInteger()
                };

                if (convertToRebar)
                {
                    result["rebarIds"] = PathReinforcement.RemovePathReinforcementSystem(doc, path).Select(id => id.GetValue()).ToList();
                    result["converted"] = true;
                }
                else
                {
                    if (unobscured && uiDoc.ActiveView != null && !uiDoc.ActiveView.IsTemplate)
                        path.SetUnobscuredInView(uiDoc.ActiveView, true);
                    result["pathReinforcementId"] = path.Id.GetValue();
                    result["rebarInSystemIds"] = path.GetRebarInSystemIds().Select(id => id.GetValue()).ToList();
                }
                return result;
            });

            if (notes.Count > 0)
                response["notes"] = notes;
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return Ok(convertToRebar
                ? $"Created path reinforcement in {host.Category?.Name} {host.Id.GetValue()} and converted it to {((List<long>)response["rebarIds"]).Count} rebar set(s)"
                : $"Created path reinforcement {response["pathReinforcementId"]} in {host.Category?.Name} {host.Id.GetValue()}", response);
        }

        private static bool Set(Element element, BuiltInParameter id, double value)
        {
            var parameter = element.get_Parameter(id);
            return parameter != null && !parameter.IsReadOnly && parameter.Set(value);
        }

        private static ElementId DefaultTypeId(Document doc)
        {
            var id = doc.GetDefaultElementTypeId(ElementTypeGroup.PathReinforcementType);
            if (id != null && id != ElementId.InvalidElementId)
                return id;
            var first = RebarHelpers.AllOfClass<PathReinforcementType>(doc).FirstOrDefault();
            return first?.Id ?? PathReinforcementType.CreateDefaultPathReinforcementType(doc);
        }

        private static IList<Curve> Path(JArray points)
        {
            var path = points?.Select(RebarHelpers.PointFromMm).ToList();
            if (path == null || path.Count < 2)
                throw new Exception("points needs at least 2 points {x, y, z} in mm: the path the bars are laid along");

            var curves = new List<Curve>();
            for (int i = 0; i < path.Count - 1; i++)
            {
                if (path[i].DistanceTo(path[i + 1]) < RebarHelpers.ToFeet(1))
                    throw new Exception($"points {i} and {i + 1} are closer than 1 mm");
                curves.Add(Line.CreateBound(path[i], path[i + 1]));
            }
            return curves;
        }
    }
}
