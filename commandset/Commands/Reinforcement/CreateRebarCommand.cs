using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Reinforcement;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Reinforcement
{
    public class CreateRebarCommand : ExternalEventCommandBase
    {
        private CreateRebarEventHandler _handler => (CreateRebarEventHandler)Handler;

        public override string CommandName => "create_rebar";

        public CreateRebarCommand(UIApplication uiApp)
            : base(new CreateRebarEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                if (parameters == null)
                    throw new ArgumentException("Parameters are required");

                var points = parameters["points"] as JArray;
                if (points == null || points.Count < 2)
                    throw new ArgumentException("points needs at least 2 points {x, y, z} in mm");

                var layout = parameters["layout"] as JObject;
                var request = new CreateRebarRequest
                {
                    HostId = parameters["hostId"]?.Value<long?>(),
                    BarTypeId = parameters["barTypeId"]?.Value<long?>(),
                    BarTypeName = parameters["barTypeName"]?.Value<string>(),
                    Style = parameters["style"]?.Value<string>() ?? "Standard",
                    Points = points.Select(RebarHelpers.PointFromMm).ToList(),
                    Normal = RebarHelpers.VectorFromJson(parameters["normal"]),
                    ShapeId = parameters["shapeId"]?.Value<long?>(),
                    ShapeName = parameters["shapeName"]?.Value<string>(),
                    StartHookId = parameters["startHookId"]?.Value<long?>(),
                    StartHookName = parameters["startHookName"]?.Value<string>(),
                    EndHookId = parameters["endHookId"]?.Value<long?>(),
                    EndHookName = parameters["endHookName"]?.Value<string>(),
                    StartHookOrientation = parameters["startHookOrientation"]?.Value<string>() ?? "Left",
                    EndHookOrientation = parameters["endHookOrientation"]?.Value<string>() ?? "Left",
                    LayoutRule = layout?["rule"]?.Value<string>() ?? "Single",
                    Number = layout?["number"]?.Value<int?>(),
                    Spacing = ToFeet(layout?["spacingMm"]),
                    ArrayLength = ToFeet(layout?["arrayLengthMm"]),
                    BarsOnNormalSide = layout?["barsOnNormalSide"]?.Value<bool?>() ?? true,
                    IncludeFirstBar = layout?["includeFirstBar"]?.Value<bool?>() ?? true,
                    IncludeLastBar = layout?["includeLastBar"]?.Value<bool?>() ?? true,
                    UseExistingShapeIfPossible = parameters["useExistingShapeIfPossible"]?.Value<bool?>() ?? true,
                    CreateNewShape = parameters["createNewShape"]?.Value<bool?>() ?? true,
                    ShowUnobscuredInActiveView = parameters["showUnobscuredInActiveView"]?.Value<bool?>() ?? false
                };

                if (request.BarTypeId == null && string.IsNullOrWhiteSpace(request.BarTypeName))
                    throw new ArgumentException("barTypeName or barTypeId is required (see get_rebar_types)");

                _handler.SetParameters(request);

                if (RaiseAndWaitForCompletion(60000))
                    return _handler.Result;
                throw new TimeoutException("Create rebar timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Create rebar failed: {ex.Message}");
            }
        }

        private static double? ToFeet(JToken mm)
        {
            var value = mm?.Value<double?>();
            return value.HasValue ? RebarHelpers.ToFeet(value.Value) : (double?)null;
        }
    }
}
