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
    /// Copies the rebar of one host into other hosts of the same category and adapts it to each
    /// (Revit's "Propagate Rebar": RebarPropagation.AlignByHost).
    /// </summary>
    public class PropagateRebarEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "propagate rebar";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var destinationIds = RebarHelpers.Ids(p["destinationHostIds"]);
            if (destinationIds.Count == 0)
                throw new Exception("destinationHostIds is required: the hosts to copy the rebar into");

            var notes = new List<string>();
            var sources = SourceRebars(uiDoc, p, notes);
            var sourceHostId = sources[0].GetHostId();
            var sourceHost = doc.GetElement(sourceHostId);

            var results = new List<object>();
            int createdTotal = 0, hostsDone = 0;

            // RebarPropagation opens its own transaction, so ours cannot wrap it: a transaction
            // group makes the whole call one undo step and the event scope handles the failures.
            using (var failures = new FailureEventScope(app.Application))
            using (var group = new TransactionGroup(doc, "MCP: Propagate Rebar"))
            {
                group.Start();
                try
                {
                    foreach (var id in destinationIds)
                    {
                        failures.Warnings.Clear();
                        failures.Errors.Clear();
                        var created = PropagateTo(doc, sources, sourceHostId, id, failures, out string error);
                        if (error != null)
                        {
                            results.Add(new { hostId = id, success = false, error });
                            continue;
                        }

                        hostsDone++;
                        createdTotal += created.Count;
                        results.Add(new
                        {
                            hostId = id,
                            success = true,
                            rebarIds = created,
                            revitWarnings = failures.Warnings.Count > 0 ? failures.Warnings.Distinct().ToList() : null
                        });
                    }

                    if (createdTotal > 0)
                        group.Assimilate();
                    else
                        group.RollBack();
                }
                catch
                {
                    if (group.GetStatus() == TransactionStatus.Started)
                        group.RollBack();
                    throw;
                }
            }

            var response = new Dictionary<string, object>
            {
                ["sourceHostId"] = sourceHostId.GetValue(),
                ["sourceRebarCount"] = sources.Count,
                ["createdRebarCount"] = createdTotal,
                ["destinations"] = results
            };
            if (notes.Count > 0)
                response["notes"] = notes;

            return new AIResult<object>
            {
                Success = hostsDone > 0,
                Message = $"Propagated {sources.Count} rebar set(s) from {sourceHost?.Category?.Name} {sourceHostId.GetValue()} "
                          + $"into {hostsDone} of {destinationIds.Count} host(s): {createdTotal} rebar element(s) created",
                Response = response
            };
        }

        private static List<long> PropagateTo(Document doc, List<RevitRebar> sources, ElementId sourceHostId, long destinationId,
            FailureEventScope failures, out string error)
        {
            error = null;
            try
            {
                var destination = doc.GetElement(Utils.ElementIdExtensions.FromLong(destinationId));
                if (destination == null)
                    throw new Exception("no such element");
                if (destination.Id == sourceHostId)
                    throw new Exception("this is the source host");
                if (!RebarHostData.IsValidHost(destination))
                    throw new Exception($"{destination.Category?.Name} cannot host rebar");

                var created = RebarPropagation.AlignByHost(doc, sources, destination);
                if (failures.Errors.Count > 0)
                    throw new Exception("Revit rolled it back: " + string.Join("; ", failures.Errors.Distinct()));
                if (created == null || created.Count == 0)
                    throw new Exception("Revit created no rebar in this host");
                return created.Select(id => id.GetValue()).ToList();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static List<RevitRebar> SourceRebars(UIDocument uiDoc, JObject p, List<string> notes)
        {
            // The source host is named 'sourceHostId' here so it cannot be confused with the destinations
            var lookup = new JObject { ["rebarIds"] = p["rebarIds"], ["hostId"] = p["sourceHostId"] };
            var rebars = RebarHelpers.ResolveReinforcement(uiDoc, lookup, false).Cast<RevitRebar>().ToList();

            int grouped = rebars.RemoveAll(r => r.GroupId != ElementId.InvalidElementId);
            if (grouped > 0)
                notes.Add($"{grouped} rebar set(s) in a group were skipped: Revit cannot propagate group members");
            if (rebars.Count == 0)
                throw new Exception("No rebar left to propagate");

            var hosts = rebars.Select(r => r.GetHostId()).Distinct().ToList();
            if (hosts.Count > 1)
                throw new Exception($"The source rebar sits in {hosts.Count} hosts; all of it must come from one host");
            return rebars;
        }
    }
}
