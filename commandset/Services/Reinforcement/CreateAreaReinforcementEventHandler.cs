using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Area reinforcement (a mesh of bars in up to four layers) in a structural floor, foundation
    /// slab or wall, over the whole host or inside a boundary. Can be converted to plain rebar sets.
    /// </summary>
    public class CreateAreaReinforcementEventHandler : ReinforcementEventHandlerBase
    {
        private class LayerSpec
        {
            public AreaReinforcementLayerType Type;
            public string Key;
            public BuiltInParameter[] Spacing;
            public BuiltInParameter[] BarType;
        }

        // Floors use the TOP/BOTTOM parameters, walls FRONT (exterior) / BACK (interior);
        // DIR_1 is the major direction, DIR_2 the minor.
        private static readonly LayerSpec[] Layers =
        {
            new LayerSpec
            {
                Type = AreaReinforcementLayerType.TopOrFrontMajor, Key = "topMajor",
                Spacing = new[] { BuiltInParameter.REBAR_SYSTEM_SPACING_TOP_DIR_1, BuiltInParameter.REBAR_SYSTEM_SPACING_FRONT_DIR_1, BuiltInParameter.REBAR_SYSTEM_SPACING_TOP_DIR_1_GENERIC },
                BarType = new[] { BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_TOP_DIR_1, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_FRONT_DIR_1, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_TOP_DIR_1_GENERIC }
            },
            new LayerSpec
            {
                Type = AreaReinforcementLayerType.TopOrFrontMinor, Key = "topMinor",
                Spacing = new[] { BuiltInParameter.REBAR_SYSTEM_SPACING_TOP_DIR_2, BuiltInParameter.REBAR_SYSTEM_SPACING_FRONT_DIR_2, BuiltInParameter.REBAR_SYSTEM_SPACING_TOP_DIR_2_GENERIC },
                BarType = new[] { BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_TOP_DIR_2, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_FRONT_DIR_2, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_TOP_DIR_2_GENERIC }
            },
            new LayerSpec
            {
                Type = AreaReinforcementLayerType.BottomOrBackMajor, Key = "bottomMajor",
                Spacing = new[] { BuiltInParameter.REBAR_SYSTEM_SPACING_BOTTOM_DIR_1, BuiltInParameter.REBAR_SYSTEM_SPACING_BACK_DIR_1, BuiltInParameter.REBAR_SYSTEM_SPACING_BOTTOM_DIR_1_GENERIC },
                BarType = new[] { BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_BOTTOM_DIR_1, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_BACK_DIR_1, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_BOTTOM_DIR_1_GENERIC }
            },
            new LayerSpec
            {
                Type = AreaReinforcementLayerType.BottomOrBackMinor, Key = "bottomMinor",
                Spacing = new[] { BuiltInParameter.REBAR_SYSTEM_SPACING_BOTTOM_DIR_2, BuiltInParameter.REBAR_SYSTEM_SPACING_BACK_DIR_2, BuiltInParameter.REBAR_SYSTEM_SPACING_BOTTOM_DIR_2_GENERIC },
                BarType = new[] { BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_BOTTOM_DIR_2, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_BACK_DIR_2, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_BOTTOM_DIR_2_GENERIC }
            }
        };

        protected override string Title => "create area reinforcement";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var host = RebarHelpers.ResolveHost(uiDoc, p["hostId"]?.Value<long?>());
            var barType = RebarHelpers.FindByIdOrName<RebarBarType>(doc, p["barTypeId"]?.Value<long?>(), p["barTypeName"]?.Value<string>(), "Rebar bar type")
                          ?? throw new Exception("barTypeName or barTypeId is required (see get_rebar_types)");
            var hook = RebarHelpers.FindByIdOrName<RebarHookType>(doc, p["hookTypeId"]?.Value<long?>(), p["hookTypeName"]?.Value<string>(), "Rebar hook type");
            var areaType = RebarHelpers.FindByIdOrName<AreaReinforcementType>(doc, p["areaReinforcementTypeId"]?.Value<long?>(),
                p["areaReinforcementTypeName"]?.Value<string>(), "Area reinforcement type");

            var majorDirection = RebarHelpers.VectorFromJson(p["majorDirection"]);
            if (majorDirection == null || majorDirection.GetLength() < 1e-9)
                majorDirection = DefaultMajorDirection(host);

            var boundary = Boundary(p["boundary"] as JArray);
            var layerSettings = p["layers"] as JObject;
            bool convertToRebar = p["convertToRebar"]?.Value<bool?>() ?? false;
            bool unobscured = p["showUnobscuredInActiveView"]?.Value<bool?>() ?? false;

            var notes = new List<string>();
            var warnings = new List<string>();
            var response = RebarTransaction.Run(doc, "MCP: Create Area Reinforcement", warnings, () =>
            {
                var typeId = areaType?.Id ?? DefaultTypeId(doc);
                var hookId = hook?.Id ?? ElementId.InvalidElementId;
                var area = boundary != null
                    ? AreaReinforcement.Create(doc, host, boundary, majorDirection.Normalize(), typeId, barType.Id, hookId)
                    : AreaReinforcement.Create(doc, host, majorDirection.Normalize(), typeId, barType.Id, hookId);

                if (layerSettings != null)
                    ApplyLayers(doc, area, layerSettings, notes);

                doc.Regenerate();

                var result = new Dictionary<string, object>
                {
                    ["hostId"] = host.Id.GetValue(),
                    ["barType"] = barType.Name,
                    ["majorDirection"] = RebarHelpers.Vector(majorDirection.Normalize()),
                    ["layers"] = Layers.ToDictionary(l => l.Key, l => (object)new
                    {
                        active = RebarHelpers.Try(() => (bool?)area.IsLayerActive(l.Type)),
                        lines = RebarHelpers.Try(() => (int?)area.GetNumberOfLines(l.Type))
                    })
                };

                if (convertToRebar)
                {
                    result["rebarIds"] = AreaReinforcement.RemoveAreaReinforcementSystem(doc, area).Select(id => id.GetValue()).ToList();
                    result["converted"] = true;
                }
                else
                {
                    if (unobscured && uiDoc.ActiveView != null && !uiDoc.ActiveView.IsTemplate)
                        area.SetUnobscuredInView(uiDoc.ActiveView, true);
                    result["areaReinforcementId"] = area.Id.GetValue();
                    result["rebarInSystemIds"] = area.GetRebarInSystemIds().Select(id => id.GetValue()).ToList();
                }
                return result;
            });

            if (notes.Count > 0)
                response["notes"] = notes;
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return Ok(convertToRebar
                ? $"Created area reinforcement in {host.Category?.Name} {host.Id.GetValue()} and converted it to {((List<long>)response["rebarIds"]).Count} rebar set(s)"
                : $"Created area reinforcement {response["areaReinforcementId"]} in {host.Category?.Name} {host.Id.GetValue()}", response);
        }

        private static void ApplyLayers(Document doc, AreaReinforcement area, JObject settings, List<string> notes)
        {
            // A layer's spacing and bar type can only be set once the layer is on
            bool switched = false;
            foreach (var layer in Layers)
            {
                var active = (settings[layer.Key] as JObject)?["active"]?.Value<bool?>();
                if (active.HasValue)
                {
                    area.SetLayerActive(active.Value, layer.Type);
                    switched = true;
                }
            }
            if (switched)
                doc.Regenerate();

            foreach (var layer in Layers)
            {
                if (!(settings[layer.Key] is JObject spec))
                    continue;

                var spacing = RebarHelpers.FeetOrNull(spec["spacingMm"]);
                if (spacing.HasValue && !SetFirst(area, layer.Spacing, x => x.Set(spacing.Value)))
                    notes.Add($"{layer.Key}: spacing could not be set on this area reinforcement");

                string barTypeName = spec["barTypeName"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(barTypeName))
                {
                    var barType = RebarHelpers.FindByIdOrName<RebarBarType>(doc, null, barTypeName, "Rebar bar type");
                    if (!SetFirst(area, layer.BarType, x => x.Set(barType.Id)))
                        notes.Add($"{layer.Key}: bar type could not be set on this area reinforcement");
                }
            }
        }

        private static bool SetFirst(Element element, BuiltInParameter[] candidates, Func<Parameter, bool> set)
        {
            foreach (var candidate in candidates)
            {
                var parameter = element.get_Parameter(candidate);
                if (parameter != null && !parameter.IsReadOnly && set(parameter))
                    return true;
            }
            return false;
        }

        private static ElementId DefaultTypeId(Document doc)
        {
            var id = doc.GetDefaultElementTypeId(ElementTypeGroup.AreaReinforcementType);
            if (id != null && id != ElementId.InvalidElementId)
                return id;
            var first = RebarHelpers.AllOfClass<AreaReinforcementType>(doc).FirstOrDefault();
            return first?.Id ?? AreaReinforcementType.CreateDefaultAreaReinforcementType(doc);
        }

        /// <summary>Walls: along the wall. Floors and foundations: world X.</summary>
        private static XYZ DefaultMajorDirection(Element host)
        {
            if (host is Wall && host.Location is LocationCurve location && location.Curve is Line line)
                return line.Direction;
            return XYZ.BasisX;
        }

        private static IList<Curve> Boundary(JArray points)
        {
            if (points == null)
                return null;
            var loop = points.Select(RebarHelpers.PointFromMm).ToList();
            if (loop.Count > 1 && loop[0].DistanceTo(loop[loop.Count - 1]) < RebarHelpers.ToFeet(1))
                loop.RemoveAt(loop.Count - 1);
            if (loop.Count < 3)
                throw new Exception("boundary needs at least 3 points {x, y, z} in mm (a closed loop in the plane of the host)");

            var curves = new List<Curve>();
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                if (a.DistanceTo(b) < RebarHelpers.ToFeet(1))
                    throw new Exception($"boundary points {i} and {(i + 1) % loop.Count} are closer than 1 mm");
                curves.Add(Line.CreateBound(a, b));
            }
            return curves;
        }
    }
}
