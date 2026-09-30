using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Reinforcement;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Reinforcement
{
    public class GetHostRebarCommand : ExternalEventCommandBase
    {
        private GetHostRebarEventHandler _handler => (GetHostRebarEventHandler)Handler;

        public override string CommandName => "get_host_rebar";

        public GetHostRebarCommand(UIApplication uiApp)
            : base(new GetHostRebarEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var hostId = parameters?["hostId"]?.Value<long?>();
                var includeGeometry = parameters?["includeGeometry"]?.Value<bool?>() ?? false;

                _handler.SetParameters(hostId, includeGeometry);

                if (RaiseAndWaitForCompletion(60000))
                    return _handler.Result;
                throw new TimeoutException("Get host rebar timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Get host rebar failed: {ex.Message}");
            }
        }
    }
}
