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
    /// Places an instance of a named RebarShape and, when a box is given, stretches it to fit a
    /// rectangle (origin + two edges). The caller needs no bar-by-bar geometry: a stirrup is
    /// "shape M_T1 in this 220 x 520 rectangle".
    /// </summary>
    public class CreateRebarFromShapeEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "create rebar from shape";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var host = RebarHelpers.ResolveHost(uiDoc, p["hostId"]?.Value<long?>());
            if (!RebarHostData.IsValidHost(host))
                throw new Exception($"Element {host.Id.GetValue()} ({host.Category?.Name}) cannot host rebar. "
                                    + "Use a structural concrete beam, column, wall, floor or foundation.");

            var shape = RebarHelpers.FindByIdOrName<RebarShape>(doc, p["shapeId"]?.Value<long?>(), p["shapeName"]?.Value<string>(), "Rebar shape")
                        ?? throw new Exception("shapeName or shapeId is required (see get_rebar_types)");
            var barType = RebarHelpers.FindByIdOrName<RebarBarType>(doc, p["barTypeId"]?.Value<long?>(), p["barTypeName"]?.Value<string>(), "Rebar bar type")
                          ?? throw new Exception("barTypeName or barTypeId is required (see get_rebar_types)");

            var origin = RebarHelpers.PointFromMm(p["origin"] ?? throw new Exception("origin {x, y, z} in mm is required"));
            var xDir = Direction(p["xDirection"], "xDirection");
            var yDir = Direction(p["yDirection"], "yDirection");
            if (Math.Abs(xDir.DotProduct(yDir)) > 1e-3)
                throw new Exception("xDirection and yDirection must be square to each other");

            double? width = RebarHelpers.FeetOrNull(p["widthMm"]);
            double? height = RebarHelpers.FeetOrNull(p["heightMm"]);
            if (width.HasValue != height.HasValue)
                throw new Exception("Pass both widthMm and heightMm to fit the shape to a box, or neither to keep the shape's default size");

            var layout = RebarLayout.FromJson(p["layout"] as JObject);
            bool unobscured = p["showUnobscuredInActiveView"]?.Value<bool?>() ?? false;

            var warnings = new List<string>();
            var rebar = RebarTransaction.Run(doc, "MCP: Create Rebar From Shape", warnings, () =>
            {
                var created = RevitRebar.CreateFromRebarShape(doc, shape, barType, host, origin, xDir, yDir);
                if (created == null)
                    throw new Exception($"Revit could not place shape '{shape.Name}' with bar type '{barType.Name}' here");

                if (width.HasValue)
                    created.GetShapeDrivenAccessor().ScaleToBox(origin, xDir * width.Value, yDir * height.Value);

                layout?.Apply(created);

                if (unobscured && uiDoc.ActiveView != null && !uiDoc.ActiveView.IsTemplate)
                    created.SetUnobscuredInView(uiDoc.ActiveView, true);
                return created;
            });

            var response = new Dictionary<string, object>
            {
                ["rebarId"] = rebar.Id.GetValue(),
                ["hostId"] = host.Id.GetValue(),
                ["barType"] = barType.Name,
                ["shape"] = shape.Name,
                ["style"] = shape.RebarStyle.ToString(),
                ["layoutRule"] = rebar.LayoutRule.ToString(),
                ["quantity"] = rebar.Quantity,
                ["barLengthMm"] = RebarHelpers.LengthParamMm(rebar, BuiltInParameter.REBAR_ELEM_LENGTH),
                ["totalLengthMm"] = RebarHelpers.ToMm(rebar.TotalLength),
                ["hookStart"] = RebarHelpers.NameOf(doc, rebar.GetHookTypeId(0)),
                ["hookEnd"] = RebarHelpers.NameOf(doc, rebar.GetHookTypeId(1)),
                ["setDirection"] = RebarHelpers.Vector(xDir.CrossProduct(yDir).Normalize())
            };
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return Ok($"Created rebar {rebar.Id.GetValue()} ({barType.Name}, shape {shape.Name}, {rebar.Quantity} bar(s)) "
                      + $"in {host.Category?.Name} {host.Id.GetValue()}"
                      + (warnings.Count > 0 ? $" with {warnings.Count} Revit warning(s)" : ""), response);
        }

        private static XYZ Direction(JToken token, string what)
        {
            var vector = RebarHelpers.VectorFromJson(token);
            if (vector == null || vector.GetLength() < 1e-9)
                throw new Exception($"{what} {{x, y, z}} is required and cannot be zero");
            return vector.Normalize();
        }
    }
}
