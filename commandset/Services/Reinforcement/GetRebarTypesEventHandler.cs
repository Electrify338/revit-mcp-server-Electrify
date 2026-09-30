using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Lists what rebar and its annotation are made from: bar, hook, shape and cover types by
    /// default, and on request end treatments, cranks, splice types, tag types, bending detail
    /// types, area / path reinforcement types and multi-rebar annotation types (read-only).
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

                if (Include.Contains("endTreatmentTypes"))
                {
                    result["endTreatmentTypes"] = Collect<EndTreatmentType>(doc)
                        .OrderBy(t => t.Name)
                        .Select(t => new { id = t.Id.GetValue(), name = t.Name, endTreatment = t.EndTreatment })
                        .ToList();
                }

                if (Include.Contains("rebarTagTypes"))
                {
                    result["rebarTagTypes"] = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_RebarTags)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .Where(t => Matches($"{t.FamilyName} : {t.Name}"))
                        .OrderBy(t => t.FamilyName).ThenBy(t => t.Name)
                        .Select(t => new { id = t.Id.GetValue(), family = t.FamilyName, name = t.Name })
                        .ToList();
                }

                if (Include.Contains("areaReinforcementTypes"))
                {
                    result["areaReinforcementTypes"] = Collect<AreaReinforcementType>(doc)
                        .OrderBy(t => t.Name)
                        .Select(t => new { id = t.Id.GetValue(), name = t.Name })
                        .ToList();
                }

                if (Include.Contains("pathReinforcementTypes"))
                {
                    result["pathReinforcementTypes"] = Collect<PathReinforcementType>(doc)
                        .OrderBy(t => t.Name)
                        .Select(t => new { id = t.Id.GetValue(), name = t.Name })
                        .ToList();
                }

                if (Include.Contains("multiRebarAnnotationTypes"))
                {
                    result["multiRebarAnnotationTypes"] = Collect<MultiReferenceAnnotationType>(doc)
                        .OrderBy(t => t.Name)
                        .Select(t => new
                        {
                            id = t.Id.GetValue(),
                            name = t.Name,
                            tagType = RebarHelpers.NameOf(doc, t.TagTypeId),
                            dimensionStyle = RebarHelpers.NameOf(doc, t.DimensionStyleId)
                        })
                        .ToList();
                }

                var unavailable = new List<string>();
                if (Include.Contains("bendingDetailTypes"))
                {
#if REVIT2024_OR_GREATER
                    result["bendingDetailTypes"] = Collect<RebarBendingDetailType>(doc)
                        .OrderBy(t => t.Name)
                        .Select(t => new { id = t.Id.GetValue(), name = t.Name })
                        .ToList();
#else
                    unavailable.Add("bendingDetailTypes need Revit 2024 or later");
#endif
                }

                if (Include.Contains("spliceTypes"))
                {
#if REVIT2025_OR_GREATER
                    result["spliceTypes"] = RebarSpliceTypeUtils.GetAllRebarSpliceTypes(doc)
                        .Select(id => doc.GetElement(id))
                        .Where(t => t != null && Matches(t.Name))
                        .OrderBy(t => t.Name)
                        .Select(t => new
                        {
                            id = t.Id.GetValue(),
                            name = t.Name,
                            lapLengthMultiplier = RebarHelpers.Try(() => (double?)RebarSpliceTypeUtils.GetLapLengthMultiplier(doc, t.Id))
                        })
                        .ToList();
#else
                    unavailable.Add("spliceTypes need Revit 2025 or later");
#endif
                }

                if (Include.Contains("crankTypes"))
                {
#if REVIT2026_OR_GREATER
                    result["crankTypes"] = RebarCrankTypeUtils.GetAllRebarCrankTypes(doc)
                        .Select(id => doc.GetElement(id))
                        .Where(t => t != null && Matches(t.Name))
                        .OrderBy(t => t.Name)
                        .Select(t => new { id = t.Id.GetValue(), name = t.Name })
                        .ToList();
#else
                    unavailable.Add("crankTypes need Revit 2026 or later");
#endif
                }

                var counts = string.Join(", ", result.Select(kv => $"{((System.Collections.ICollection)kv.Value).Count} {kv.Key}"));
                if (unavailable.Count > 0)
                    result["unavailable"] = unavailable;
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
            return RebarHelpers.AllOfClass<T>(doc).Where(e => Matches(e.Name));
        }

        private bool Matches(string name)
        {
            return string.IsNullOrWhiteSpace(NameFilter)
                   || (name ?? "").IndexOf(NameFilter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public string GetName() => "Get Rebar Types";
    }
}
