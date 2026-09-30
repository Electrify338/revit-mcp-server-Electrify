using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Lists what a rebar needs: bar types, hook types, shapes and cover types (read-only).
    /// </summary>
    public class GetRebarTypesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public HashSet<string> Include { get; private set; }
        public string NameFilter { get; private set; }
        public AIResult<object> Result { get; private set; }

        public void SetParameters(IEnumerable<string> include, string nameFilter)
        {
            var list = include?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            Include = list != null && list.Count > 0
                ? new HashSet<string>(list, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(new[] { "barTypes", "hookTypes", "shapes", "coverTypes" }, StringComparer.OrdinalIgnoreCase);
            NameFilter = nameFilter;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                var result = new Dictionary<string, object>();

                if (Include.Contains("barTypes"))
                {
                    result["barTypes"] = Collect<RebarBarType>(doc)
                        .OrderBy(t => t.BarNominalDiameter)
                        .Select(t => new
                        {
                            id = t.Id.GetValue(),
                            name = t.Name,
                            nominalDiameterMm = RebarHelpers.ToMm(t.BarNominalDiameter),
                            modelDiameterMm = RebarHelpers.ToMm(t.BarModelDiameter),
                            standardBendDiameterMm = RebarHelpers.ToMm(t.StandardBendDiameter),
                            stirrupTieBendDiameterMm = RebarHelpers.ToMm(t.StirrupTieBendDiameter),
                            deformation = t.DeformationType.ToString()
                        })
                        .ToList();
                }

                if (Include.Contains("hookTypes"))
                {
                    result["hookTypes"] = Collect<RebarHookType>(doc)
                        .OrderBy(t => t.Name)
                        .Select(t => new
                        {
                            id = t.Id.GetValue(),
                            name = t.Name,
                            angleDeg = Math.Round(t.HookAngle * 180.0 / Math.PI, 1),
                            style = t.Style.ToString()
                        })
                        .ToList();
                }

                if (Include.Contains("shapes"))
                {
                    result["shapes"] = Collect<RebarShape>(doc)
                        .OrderBy(s => s.Name)
                        .Select(s => new
                        {
                            id = s.Id.GetValue(),
                            name = s.Name,
                            style = s.RebarStyle.ToString(),
                            simpleLine = s.SimpleLine
                        })
                        .ToList();
                }

                if (Include.Contains("coverTypes"))
                {
                    result["coverTypes"] = Collect<RebarCoverType>(doc)
                        .OrderBy(c => c.CoverDistance)
                        .Select(c => new
                        {
                            id = c.Id.GetValue(),
                            name = c.Name,
                            distanceMm = RebarHelpers.ToMm(c.CoverDistance)
                        })
                        .ToList();
                }

                var counts = string.Join(", ", result.Select(kv => $"{((System.Collections.ICollection)kv.Value).Count} {kv.Key}"));
                Result = new AIResult<object>
                {
                    Success = true,
                    Message = counts.Length > 0 ? $"Found {counts}" : "Nothing requested",
                    Response = result
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object>
                {
                    Success = false,
                    Message = $"Failed to get rebar types: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private IEnumerable<T> Collect<T>(Document doc) where T : Element
        {
            var items = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>();
            if (!string.IsNullOrWhiteSpace(NameFilter))
                items = items.Where(e => e.Name.IndexOf(NameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            return items;
        }

        public string GetName() => "Get Rebar Types";
    }
}
