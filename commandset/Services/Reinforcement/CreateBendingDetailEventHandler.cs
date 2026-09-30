using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Rebar bending details in a view (Revit 2024+): create them for rebar sets (at given points,
    /// or stacked in rows below the rebar), move or rotate existing ones, or list those in a view.
    /// Always created natively: copying a bending detail also copies its host rebar.
    /// </summary>
    public class CreateBendingDetailEventHandler : ReinforcementEventHandlerBase
    {
        protected override string Title => "create bending detail";

        protected override AIResult<object> Run(UIApplication app)
        {
#if REVIT2024_OR_GREATER
            var uiDoc = app.ActiveUIDocument;
            var p = Parameters;
            var view = RebarHelpers.ResolveView(uiDoc, p["viewId"]?.Value<long?>());
            var frame = new ViewFrame(view);

            string action = (p["action"]?.Value<string>() ?? "create").Trim().ToLowerInvariant();
            switch (action)
            {
                case "create":
                    return Create(uiDoc, frame, p);
                case "move":
                    return Move(uiDoc.Document, frame, p);
                case "list":
                    return List(uiDoc.Document, frame);
                default:
                    throw new Exception($"Invalid action '{action}'. Valid: create, move, list");
            }
#else
            throw new Exception("rebar bending details need Revit 2024 or later");
#endif
        }

#if REVIT2024_OR_GREATER
        private class Item
        {
            public Element Rebar;
            public int? BarIndex;
            public XYZ Position;
            public double RotationRadians;
            public UvBox RebarBox;
        }

        private AIResult<object> Create(UIDocument uiDoc, ViewFrame frame, JObject p)
        {
            var doc = uiDoc.Document;
            var view = frame.View;
            var type = ResolveType(doc, p);
            var items = Items(uiDoc, frame, p);

            bool centerOnRebar = p["centerOnRebar"]?.Value<bool?>() ?? true;
            double gapBelow = p["gapBelowMm"]?.Value<double?>() ?? 600;
            double rowSpacing = p["rowSpacingMm"]?.Value<double?>() ?? 1150;

            // Rows for the items without a position: stacked downwards from below the lowest rebar,
            // each one under the rebar it shows
            var auto = items.Where(i => i.Position == null).ToList();
            if (auto.Count > 0)
            {
                var missing = auto.Where(i => i.RebarBox == null).Select(i => i.Rebar.Id.GetValue()).ToList();
                if (missing.Count > 0)
                    throw new Exception($"rebar {string.Join(", ", missing)} is not visible in view '{view.Name}': pass 'position' for it or fix the view");

                double top = p["startV"]?.Value<double?>() ?? items.Where(i => i.RebarBox != null).Min(i => i.RebarBox.VMin) - gapBelow;
                for (int row = 0; row < auto.Count; row++)
                    auto[row].Position = frame.FromUv(auto[row].RebarBox.UCenter, top - row * rowSpacing);
            }

            var created = new List<object>();
            var failed = new List<object>();
            var warnings = new List<string>();
            RebarTransaction.Run(doc, "MCP: Create Bending Details", warnings, () =>
            {
                foreach (var item in items)
                {
                    long rebarId = item.Rebar.Id.GetValue();
                    Element detail = null;
                    try
                    {
                        detail = CreateOne(doc, view, item, type, out int barIndex);
                        doc.Regenerate();

                        var box = frame.Box(detail);
                        if (box == null)
                            throw new Exception("the bending detail has no graphics in this view");

                        if (centerOnRebar && item.RebarBox != null)
                        {
                            double shift = item.RebarBox.UCenter - box.UCenter;
                            if (Math.Abs(shift) > 1)
                            {
                                RebarBendingDetail.SetPosition(detail, RebarBendingDetail.GetPosition(detail) + frame.Right * RebarHelpers.ToFeet(shift));
                                doc.Regenerate();
                                box = frame.Box(detail) ?? box;
                            }
                        }

                        created.Add(new
                        {
                            bendingDetailId = detail.Id.GetValue(),
                            rebarId,
                            barIndex,
                            positionUv = frame.Uv(RebarBendingDetail.GetPosition(detail)),
                            boxUv = box.ToJson()
                        });
                    }
                    catch (Exception ex)
                    {
                        if (detail != null && detail.IsValidObject)
                            doc.Delete(detail.Id);
                        failed.Add(new { rebarId, error = ex.Message });
                    }
                }
            });

            var response = new Dictionary<string, object>
            {
                ["viewId"] = view.Id.GetValue(),
                ["type"] = type.Name,
                ["bendingDetails"] = created
            };
            if (failed.Count > 0)
                response["failed"] = failed;
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return new AIResult<object>
            {
                Success = created.Count > 0,
                Message = $"Created {created.Count} bending detail(s) of type '{type.Name}' in view '{view.Name}'"
                          + (failed.Count > 0 ? $", {failed.Count} failed" : ""),
                Response = response
            };
        }

        /// <summary>Revit sometimes refuses bar 0 of a set, so the next bars are tried before giving up.</summary>
        private static Element CreateOne(Document doc, View view, Item item, RebarBendingDetailType type, out int barIndex)
        {
            int bars = Math.Max(1, RebarHelpers.Try(() => item.Rebar.GetSubelements().Count));
            var candidates = item.BarIndex.HasValue
                ? new[] { item.BarIndex.Value }
                : Enumerable.Range(0, Math.Min(3, bars)).ToArray();
            Exception first = null;
            foreach (int index in candidates)
            {
                try
                {
                    var detail = RebarBendingDetail.Create(doc, view.Id, item.Rebar.Id, index, type, item.Position, item.RotationRadians);
                    if (detail != null)
                    {
                        barIndex = index;
                        return detail;
                    }
                }
                catch (Exception ex)
                {
                    first = first ?? ex;
                }
            }
            throw new Exception(first?.Message ?? "Revit created no bending detail");
        }

        private AIResult<object> Move(Document doc, ViewFrame frame, JObject p)
        {
            var id = p["bendingDetailId"]?.Value<long?>() ?? throw new Exception("bendingDetailId is required");
            var detail = doc.GetElement(Utils.ElementIdExtensions.FromLong(id));
            if (detail == null || !IsBendingDetail(detail))
                throw new Exception($"Element {id} is not a rebar bending detail");

            // {u, v} is measured in the view the detail lives in, which need not be the active one
            if (doc.GetElement(detail.OwnerViewId) is View ownerView)
                frame = new ViewFrame(ownerView);

            var position = frame.Point(p["position"]);
            var rotationDeg = p["rotationDeg"]?.Value<double?>();
            if (position == null && !rotationDeg.HasValue)
                throw new Exception("Pass position and/or rotationDeg");

            var warnings = new List<string>();
            RebarTransaction.Run(doc, "MCP: Move Bending Detail", warnings, () =>
            {
                if (position != null)
                    RebarBendingDetail.SetPosition(detail, position);
                if (rotationDeg.HasValue)
                    RebarBendingDetail.SetRotation(detail, rotationDeg.Value * Math.PI / 180.0);
                doc.Regenerate();
            });

            var response = new Dictionary<string, object> { ["bendingDetail"] = Describe(doc, frame, detail) };
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;
            return Ok($"Moved bending detail {id}", response);
        }

        private AIResult<object> List(Document doc, ViewFrame frame)
        {
            var details = new FilteredElementCollector(doc, frame.View.Id)
                .OfCategory(BuiltInCategory.OST_RebarBendingDetails)
                .WhereElementIsNotElementType()
                .Where(IsBendingDetail)
                .Select(d => Describe(doc, frame, d))
                .ToList();

            return Ok($"{details.Count} bending detail(s) in view '{frame.View.Name}'", new Dictionary<string, object>
            {
                ["view"] = frame.Describe(),
                ["bendingDetails"] = details
            });
        }

        private static bool IsBendingDetail(Element element)
        {
            return RebarHelpers.Try(() => RebarBendingDetail.IsBendingDetail(element));
        }

        private static object Describe(Document doc, ViewFrame frame, Element detail)
        {
            return new
            {
                bendingDetailId = detail.Id.GetValue(),
                type = RebarHelpers.NameOf(doc, detail.GetTypeId()),
                rebarId = RebarHelpers.Try(() => RebarHelpers.IdOrNull(RebarBendingDetail.GetHost(detail)?.ElementId)),
                positionUv = RebarHelpers.Try(() => frame.Uv(RebarBendingDetail.GetPosition(detail))),
                rotationDeg = RebarHelpers.Try(() => (double?)Math.Round(RebarBendingDetail.GetRotation(detail) * 180.0 / Math.PI, 1)),
                boxUv = frame.Box(detail)?.ToJson()
            };
        }

        private static List<Item> Items(UIDocument uiDoc, ViewFrame frame, JObject p)
        {
            var doc = uiDoc.Document;
            if (p["items"] is JArray array && array.Count > 0)
            {
                return array.Select(t =>
                {
                    var id = t["rebarId"]?.Value<long?>() ?? throw new Exception("each item in 'items' needs rebarId");
                    var rebar = doc.GetElement(Utils.ElementIdExtensions.FromLong(id));
                    if (rebar == null || !RebarHelpers.IsReinforcement(rebar))
                        throw new Exception($"Element {id} is not a rebar element");
                    return new Item
                    {
                        Rebar = rebar,
                        BarIndex = t["barIndex"]?.Value<int?>(),
                        Position = frame.Point(t["position"]),
                        RotationRadians = (t["rotationDeg"]?.Value<double?>() ?? 0) * Math.PI / 180.0,
                        RebarBox = frame.Box(rebar)
                    };
                }).ToList();
            }

            return RebarHelpers.ResolveReinforcement(uiDoc, p, true)
                .Select(rebar => new Item { Rebar = rebar, RebarBox = frame.Box(rebar) })
                .ToList();
        }

        private static RebarBendingDetailType ResolveType(Document doc, JObject p)
        {
            var type = RebarHelpers.FindByIdOrName<RebarBendingDetailType>(doc, p["typeId"]?.Value<long?>(), p["typeName"]?.Value<string>(), "Bending detail type");
            if (type != null)
                return type;

            return new FilteredElementCollector(doc).OfClass(typeof(RebarBendingDetailType)).Cast<RebarBendingDetailType>()
                       .OrderBy(t => t.Name).FirstOrDefault()
                   ?? throw new Exception("This model has no rebar bending detail type. Create one in Revit (Structure > Reinforcement > Bending Detail) first.");
        }
#endif
    }
}
