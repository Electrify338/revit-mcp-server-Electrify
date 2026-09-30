using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// A multi-rebar annotation: one dimension line across the bars of a set (or several sets)
    /// with a single rebar tag, the usual way to call out stirrups or slab bars.
    /// </summary>
    public class CreateMultiRebarAnnotationEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "create multi-rebar annotation";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var view = RebarHelpers.ResolveView(uiDoc, p["viewId"]?.Value<long?>());
            var frame = new ViewFrame(view);
            var rebars = RebarHelpers.ResolveReinforcement(uiDoc, p, true);
            var rebarIds = rebars.Select(r => r.Id).ToList();

            // Default placement: the dimension line just below the bars, the tag below that
            var boxes = rebars.Select(frame.Box).Where(b => b != null).ToList();
            if (boxes.Count == 0)
                throw new Exception($"The rebar is not visible in view '{view.Name}'");
            double uMin = boxes.Min(b => b.UMin);
            double vMin = boxes.Min(b => b.VMin);
            double uCenter = (uMin + boxes.Max(b => b.UMax)) / 2;
            double vCenter = (vMin + boxes.Max(b => b.VMax)) / 2;

            // A horizontal line goes below the bars, a vertical one to their left
            var direction = Direction(frame, p["dimensionDirection"]);
            bool vertical = Math.Abs(direction.DotProduct(frame.Up)) > Math.Abs(direction.DotProduct(frame.Right));
            var origin = frame.Point(p["dimensionLineOrigin"])
                         ?? (vertical ? frame.FromUv(uMin - 300, vCenter) : frame.FromUv(uCenter, vMin - 300));
            var tagHead = frame.Point(p["tagHead"])
                          ?? (vertical ? frame.FromUv(uMin - 600, vCenter) : frame.FromUv(uCenter, vMin - 600));
            bool tagHasLeader = p["tagHasLeader"]?.Value<bool?>() ?? false;
            var styleType = RebarHelpers.ParseEnum(p["dimensionStyle"]?.Value<string>(), "dimensionStyle", DimensionStyleType.Linear);
            if (styleType != DimensionStyleType.Linear && styleType != DimensionStyleType.LinearFixed)
                throw new Exception("dimensionStyle must be Linear or LinearFixed");

            var notes = new List<string>();
            var warnings = new List<string>();
            var annotation = RebarTransaction.Run(doc, "MCP: Create Multi-Rebar Annotation", warnings, () =>
            {
                var type = ResolveType(doc, p, notes);
                using (var options = new MultiReferenceAnnotationOptions(type))
                {
                    options.DimensionStyleType = styleType;
                    options.DimensionLineDirection = direction;
                    options.DimensionLineOrigin = origin;
                    options.DimensionPlaneNormal = frame.Direction;
                    options.TagHeadPosition = tagHead;
                    options.TagHasLeader = tagHasLeader;

                    if (!options.ElementsMatchReferenceCategory(rebarIds))
                        throw new Exception($"annotation type '{type.Name}' cannot annotate these elements (its reference category must be Structural Rebar)");
                    options.SetElementsToDimension(rebarIds);

                    bool valid = styleType == DimensionStyleType.Linear
                        ? MultiReferenceAnnotation.AreReferencesValidForLinearDimension(doc, view.Id, options)
                        : MultiReferenceAnnotation.AreReferencesValidForLinearFixedDimension(doc, view.Id, options);
                    if (!valid)
                        throw new Exception("these bars cannot be dimensioned along this direction in this view: "
                                            + "the dimension line must run across the bars of the set (try the other dimensionDirection)");

                    return MultiReferenceAnnotation.Create(doc, view.Id, options);
                }
            });

            var response = new Dictionary<string, object>
            {
                ["annotationId"] = annotation.Id.GetValue(),
                ["dimensionId"] = RebarHelpers.IdOrNull(annotation.DimensionId),
                ["tagId"] = RebarHelpers.IdOrNull(annotation.TagId),
                ["viewId"] = view.Id.GetValue(),
                ["rebarIds"] = rebarIds.Select(id => id.GetValue()).ToList(),
                ["dimensionLineOriginUv"] = frame.Uv(origin),
                ["tagHeadUv"] = frame.Uv(tagHead)
            };
            if (notes.Count > 0)
                response["notes"] = notes;
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return Ok($"Created multi-rebar annotation {annotation.Id.GetValue()} for {rebarIds.Count} rebar element(s) in view '{view.Name}'", response);
        }

        /// <summary>"horizontal" / "vertical" in the view, or a model vector. Default horizontal.</summary>
        private static XYZ Direction(ViewFrame frame, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return frame.Right;
            if (token.Type == JTokenType.String)
            {
                switch (token.Value<string>().Trim().ToLowerInvariant())
                {
                    case "horizontal":
                        return frame.Right;
                    case "vertical":
                        return frame.Up;
                    default:
                        throw new Exception("dimensionDirection must be 'horizontal', 'vertical' or a vector {x, y, z}");
                }
            }

            var vector = RebarHelpers.VectorFromJson(token);
            if (vector == null || vector.GetLength() < 1e-9)
                throw new Exception("dimensionDirection vector cannot be zero");
            return vector.Normalize();
        }

        private static MultiReferenceAnnotationType ResolveType(Document doc, JObject p, List<string> notes)
        {
            var type = RebarHelpers.FindByIdOrName<MultiReferenceAnnotationType>(doc, p["typeId"]?.Value<long?>(), p["typeName"]?.Value<string>(), "Multi-rebar annotation type");
            if (type != null)
                return type;

            var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.MultiReferenceAnnotationType);
            type = doc.GetElement(defaultId) as MultiReferenceAnnotationType
                   ?? new FilteredElementCollector(doc).OfClass(typeof(MultiReferenceAnnotationType)).Cast<MultiReferenceAnnotationType>().FirstOrDefault();
            if (type != null)
                return type;

            notes.Add("The model had no multi-rebar annotation type; a default one was created");
            return MultiReferenceAnnotationType.CreateDefault(doc);
        }
    }
}
