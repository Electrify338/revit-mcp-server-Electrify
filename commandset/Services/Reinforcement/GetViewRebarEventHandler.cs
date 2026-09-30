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
    /// The rebar visible in a view with everything the detailing tools need: the view's u/v frame,
    /// each set's extents in it, mark, presentation mode and how many bars can be tagged. Read-only.
    /// </summary>
    public class GetViewRebarEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "get view rebar";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var view = RebarHelpers.ResolveView(uiDoc, p["viewId"]?.Value<long?>());
            var frame = new ViewFrame(view);
            var hostFilter = new HashSet<long>(RebarHelpers.Ids(p["hostIds"]));
            var parameterNames = (p["parameterNames"] as JArray)?.Select(t => t.Value<string>()).ToList()
                                 ?? new List<string>();
            int maxElements = p["maxElements"]?.Value<int?>() ?? 300;

            var all = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Rebar)
                .WhereElementIsNotElementType()
                .Where(RebarHelpers.IsReinforcement)
                .Where(e => hostFilter.Count == 0 || hostFilter.Contains(HostId(e).GetValue()))
                .ToList();

            var rebars = all.Take(maxElements).Select(e => Describe(doc, frame, e, parameterNames)).ToList();

            var hosts = all.Select(HostId)
                .Where(id => id != ElementId.InvalidElementId)
                .Distinct()
                .Select(id => doc.GetElement(id))
                .Where(h => h != null)
                .Select(h => new
                {
                    id = h.Id.GetValue(),
                    category = h.Category?.Name,
                    typeName = RebarHelpers.NameOf(doc, h.GetTypeId()) ?? h.Name,
                    boxUv = frame.Box(h)?.ToJson()
                })
                .ToList();

            var response = new Dictionary<string, object>
            {
                ["view"] = frame.Describe(),
                ["rebarCount"] = all.Count,
                ["rebar"] = rebars,
                ["hosts"] = hosts
            };
            if (all.Count > maxElements)
                response["truncated"] = $"Showing {maxElements} of {all.Count}. Pass hostIds or raise maxElements.";

            return Ok($"{all.Count} rebar element(s) visible in view '{view.Name}'", response);
        }

        private static ElementId HostId(Element element)
        {
            if (element is RevitRebar rebar)
                return rebar.GetHostId();
            if (element is RebarInSystem inSystem)
                return inSystem.GetHostId();
            return ElementId.InvalidElementId;
        }

        private static object Describe(Document doc, ViewFrame frame, Element element, List<string> parameterNames)
        {
            var view = frame.View;
            var barType = doc.GetElement(element.GetTypeId()) as RebarBarType;
            var info = new Dictionary<string, object>
            {
                ["id"] = element.Id.GetValue(),
                ["kind"] = element is RebarInSystem ? "RebarInSystem" : "Rebar",
                ["hostId"] = RebarHelpers.IdOrNull(HostId(element)),
                ["barType"] = barType?.Name,
                ["diameterMm"] = barType != null ? RebarHelpers.ToMm(barType.BarNominalDiameter) : (double?)null,
                ["shape"] = element.get_Parameter(BuiltInParameter.REBAR_SHAPE)?.AsValueString(),
                ["rebarNumber"] = element.get_Parameter(BuiltInParameter.REBAR_NUMBER)?.AsString(),
                ["partition"] = element.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.AsString(),
                ["barLengthMm"] = RebarHelpers.LengthParamMm(element, BuiltInParameter.REBAR_ELEM_LENGTH),
                ["taggableBars"] = RebarHelpers.Try(() => element.GetSubelements().Count),
                ["boxUv"] = frame.Box(element)?.ToJson()
            };

            if (element is RevitRebar rebar)
            {
                bool isSet = rebar.LayoutRule != RebarLayoutRule.Single;
                info["style"] = RebarHelpers.Try(() => (doc.GetElement(rebar.GetShapeId()) as RebarShape)?.RebarStyle.ToString());
                info["layoutRule"] = rebar.LayoutRule.ToString();
                info["quantity"] = rebar.Quantity;
                info["barPositions"] = rebar.NumberOfBarPositions;
                if (isSet)
                {
                    info["spacingMm"] = RebarHelpers.Try(() => (double?)RebarHelpers.ToMm(rebar.MaxSpacing));
                    info["presentationMode"] = RebarHelpers.Try(() => rebar.GetPresentationMode(view).ToString());
                    info["hiddenBarIndexes"] = RebarHelpers.Try(() => Enumerable.Range(0, rebar.NumberOfBarPositions)
                        .Where(i => rebar.DoesBarExistAtPosition(i) && rebar.IsBarHidden(view, i)).ToList());
                }
                info["cutByView"] = RebarHelpers.Try(() => (bool?)rebar.IsRebarInSection(view));
                info["unobscured"] = RebarHelpers.Try(() => (bool?)rebar.IsUnobscuredInView(view));
            }
            else if (element is RebarInSystem inSystem)
            {
                info["layoutRule"] = inSystem.LayoutRule.ToString();
                info["quantity"] = inSystem.Quantity;
                info["barPositions"] = inSystem.NumberOfBarPositions;
                info["spacingMm"] = RebarHelpers.Try(() => (double?)RebarHelpers.ToMm(inSystem.MaxSpacing));
                info["presentationMode"] = RebarHelpers.Try(() => inSystem.GetPresentationMode(view).ToString());
                info["systemId"] = RebarHelpers.IdOrNull(inSystem.SystemId);
                info["cutByView"] = RebarHelpers.Try(() => (bool?)inSystem.IsRebarInSection(view));
                info["unobscured"] = RebarHelpers.Try(() => (bool?)inSystem.IsUnobscuredInView(view));
            }

            if (parameterNames.Count > 0)
            {
                var values = new Dictionary<string, string>();
                foreach (var name in parameterNames)
                {
                    var parameter = element.LookupParameter(name);
                    if (parameter != null)
                        values[name] = parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
                }
                info["parameters"] = values;
            }

            return info;
        }
    }
}
