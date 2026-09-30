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
    /// Bar counts, total lengths and nominal steel weight, grouped by bar type, host, partition,
    /// shape or bar mark. Covers Rebar and the bars of area / path reinforcement. Read-only.
    /// </summary>
    public class GetRebarQuantitiesEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "get rebar quantities";

        private class Row
        {
            public Element Element;
            public string BarType;
            public double DiameterFeet;
            public int Bars;
            public double TotalLengthFeet;
            public double WeightKg;
        }

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            string groupBy = (p["groupBy"]?.Value<string>() ?? "barType").Trim();
            double density = p["densityKgPerM3"]?.Value<double?>() ?? 7850;
            bool includeElements = p["includeElements"]?.Value<bool?>() ?? false;

            var elements = Collect(uiDoc, out string scope);
            var rows = new List<Row>();
            foreach (var element in elements)
            {
                var barType = doc.GetElement(element.GetTypeId()) as RebarBarType;
                double diameter = barType?.BarNominalDiameter ?? 0;
                int bars;
                double totalLength;
                if (element is RevitRebar rebar)
                {
                    bars = rebar.Quantity;
                    totalLength = rebar.TotalLength;
                }
                else if (element is RebarInSystem inSystem)
                {
                    bars = inSystem.Quantity;
                    totalLength = inSystem.TotalLength;
                }
                else
                {
                    continue;
                }

                rows.Add(new Row
                {
                    Element = element,
                    BarType = barType?.Name ?? "(no bar type)",
                    DiameterFeet = diameter,
                    Bars = bars,
                    TotalLengthFeet = totalLength,
                    WeightKg = RebarHelpers.WeightKg(diameter, totalLength, density)
                });
            }

            var groups = rows
                .GroupBy(r => GroupKey(doc, r, groupBy))
                .Select(g => new
                {
                    key = g.Key,
                    diameterMm = g.Select(r => RebarHelpers.ToMm(r.DiameterFeet)).Distinct().Count() == 1
                        ? RebarHelpers.ToMm(g.First().DiameterFeet)
                        : (double?)null,
                    sets = g.Count(),
                    bars = g.Sum(r => r.Bars),
                    totalLengthM = Math.Round(g.Sum(r => r.TotalLengthFeet) * 0.3048, 2),
                    weightKg = Math.Round(g.Sum(r => r.WeightKg), 1)
                })
                .OrderBy(g => g.diameterMm ?? double.MaxValue)
                .ThenBy(g => g.key)
                .ToList();

            var response = new Dictionary<string, object>
            {
                ["scope"] = scope,
                ["groupBy"] = groupBy,
                ["groups"] = groups,
                ["total"] = new
                {
                    sets = rows.Count,
                    bars = rows.Sum(r => r.Bars),
                    totalLengthM = Math.Round(rows.Sum(r => r.TotalLengthFeet) * 0.3048, 2),
                    weightKg = Math.Round(rows.Sum(r => r.WeightKg), 1)
                },
                ["note"] = $"Weight = nominal bar area x total length x {density} kg/m3. Lengths are Revit's Total Bar Length (rounding settings apply)."
            };

            if (includeElements)
            {
                response["elements"] = rows.Select(r => new
                {
                    id = r.Element.Id.GetValue(),
                    hostId = RebarHelpers.IdOrNull(HostId(r.Element)),
                    barType = r.BarType,
                    mark = r.Element.get_Parameter(BuiltInParameter.REBAR_NUMBER)?.AsString(),
                    bars = r.Bars,
                    barLengthMm = RebarHelpers.LengthParamMm(r.Element, BuiltInParameter.REBAR_ELEM_LENGTH),
                    totalLengthM = Math.Round(r.TotalLengthFeet * 0.3048, 2),
                    weightKg = Math.Round(r.WeightKg, 1)
                }).ToList();
            }

            return Ok($"{rows.Count} rebar set(s), {rows.Sum(r => r.Bars)} bar(s), "
                      + $"{Math.Round(rows.Sum(r => r.WeightKg), 1)} kg in {scope}", response);
        }

        private List<Element> Collect(UIDocument uiDoc, out string scope)
        {
            var doc = uiDoc.Document;
            var p = Parameters;

            var hostIds = RebarHelpers.Ids(p["hostIds"]);
            if (hostIds.Count > 0)
            {
                scope = $"{hostIds.Count} host(s)";
                return hostIds
                    .Select(id => doc.GetElement(Utils.ElementIdExtensions.FromLong(id)) ?? throw new Exception($"No element with id {id}"))
                    .SelectMany(h => RebarHelpers.HostedReinforcement(doc, h, true))
                    .ToList();
            }

            var viewId = p["viewId"]?.Value<long?>();
            bool activeViewOnly = p["activeViewOnly"]?.Value<bool?>() ?? false;
            FilteredElementCollector collector;
            if (viewId.HasValue || activeViewOnly)
            {
                var view = RebarHelpers.ResolveView(uiDoc, viewId);
                collector = new FilteredElementCollector(doc, view.Id);
                scope = $"view '{view.Name}'";
            }
            else
            {
                collector = new FilteredElementCollector(doc);
                scope = "the whole model";
            }

            return collector.OfCategory(BuiltInCategory.OST_Rebar)
                .WhereElementIsNotElementType()
                .Where(RebarHelpers.IsReinforcement)
                .ToList();
        }

        private static ElementId HostId(Element element)
        {
            if (element is RevitRebar rebar)
                return rebar.GetHostId();
            if (element is RebarInSystem inSystem)
                return inSystem.GetHostId();
            return ElementId.InvalidElementId;
        }

        private static string GroupKey(Document doc, Row row, string groupBy)
        {
            var e = row.Element;
            switch (groupBy.ToLowerInvariant())
            {
                case "host":
                    var host = doc.GetElement(HostId(e));
                    return host == null ? "(no host)" : $"{host.Category?.Name} {host.Id.GetValue()} {host.Name}".Trim();
                case "partition":
                    return Blank(e.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.AsString(), "(default partition)");
                case "shape":
                    return Blank(e.get_Parameter(BuiltInParameter.REBAR_SHAPE)?.AsValueString(), "(no shape)");
                case "mark":
                    return Blank(e.get_Parameter(BuiltInParameter.REBAR_NUMBER)?.AsString(), "(no rebar number)");
                case "none":
                    return "all";
                case "bartype":
                    return row.BarType;
                default:
                    throw new Exception($"Invalid groupBy '{groupBy}'. Valid: barType, host, partition, shape, mark, none");
            }
        }

        private static string Blank(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
