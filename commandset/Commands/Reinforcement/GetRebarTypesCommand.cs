using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Reinforcement;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Reinforcement
{
    public class GetRebarTypesCommand : ExternalEventCommandBase
    {
        private GetRebarTypesEventHandler _handler => (GetRebarTypesEventHandler)Handler;

        public override string CommandName => "get_rebar_types";

        public GetRebarTypesCommand(UIApplication uiApp)
            : base(new GetRebarTypesEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var include = parameters?["include"]?.ToObject<List<string>>();
                var nameFilter = parameters?["nameFilter"]?.Value<string>();

                _handler.SetParameters(include, nameFilter);

                if (RaiseAndWaitForCompletion(30000))
                    return _handler.Result;
                throw new TimeoutException("Get rebar types timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Get rebar types failed: {ex.Message}");
            }
        }
    }
}
