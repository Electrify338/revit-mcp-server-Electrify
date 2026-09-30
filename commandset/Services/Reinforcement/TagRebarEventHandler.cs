using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Places structural rebar tags in a view. Rebar is tagged through one bar of the set (a
    /// subelement reference); a tag on a bar the view does not draw comes out invisible, so bars
    /// are tried until one gives a visible tag.
    /// </summary>
    public class TagRebarEventHandler : ReinforcementEventHandlerBase
    {
        private const int MaxBarsToTry = 12;

        private class TagRequest
        {
            public long RebarId;
            public int? BarIndex;
            public JToken Head;
            public JToken LeaderEnd;
            public JToken LeaderElbow;
            public double? OffsetU;
            public double? OffsetV;
        }

        protected override string Title => "tag rebar";

        protected override AIResult<object> Run(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc.Document;
            var p = Parameters;

            var view = RebarHelpers.ResolveView(uiDoc, p["viewId"]?.Value<long?>());
            var frame = new ViewFrame(view);
            var tagType = ResolveTagType(doc, p);
            bool leader = p["leader"]?.Value<bool?>() ?? false;
            var orientation = RebarHelpers.ParseEnum(p["orientation"]?.Value<string>(), "orientation", TagOrientation.Horizontal);
            double defaultOffsetU = p["offsetMm"]?["u"]?.Value<double?>() ?? 0;
            double defaultOffsetV = p["offsetMm"]?["v"]?.Value<double?>() ?? 0;

            var requests = Requests(uiDoc, p);
            var placed = new List<object>();
            var failed = new List<object>();
            var warnings = new List<string>();

            RebarTransaction.Run(doc, "MCP: Tag Rebar", warnings, () =>
            {
                if (!tagType.IsActive)
                {
                    tagType.Activate();
                    doc.Regenerate();
                }

                foreach (var request in requests)
                {
                    IndependentTag tag = null;
                    try
                    {
                        var element = doc.GetElement(Utils.ElementIdExtensions.FromLong(request.RebarId));
                        if (element == null || !RebarHelpers.IsReinforcement(element))
                            throw new Exception("not a rebar element");

                        var head = frame.Point(request.Head);
                        if (head == null)
                        {
                            var box = frame.Box(element)
                                      ?? throw new Exception($"not visible in view '{view.Name}'");
                            head = frame.FromUv(box.UCenter + (request.OffsetU ?? defaultOffsetU), box.VCenter + (request.OffsetV ?? defaultOffsetV));
                        }

                        tag = PlaceVisibleTag(doc, view, element, tagType.Id, request.BarIndex, orientation, head,
                            out int barIndex, out Reference reference, out string createError);
                        if (tag == null)
                            throw new Exception(createError != null
                                ? $"Revit refused the tag: {createError}"
                                : "no bar of this set gives a visible tag in this view: check the set's presentation mode and the view's crop / view range");

                        if (leader)
                        {
                            tag.HasLeader = true;
                            var leaderEnd = frame.Point(request.LeaderEnd);
                            var leaderElbow = frame.Point(request.LeaderElbow);
                            if (leaderEnd != null)
                            {
                                tag.LeaderEndCondition = LeaderEndCondition.Free;
                                tag.SetLeaderEnd(reference, leaderEnd);
                            }
                            if (leaderElbow != null)
                                tag.SetLeaderElbow(reference, leaderElbow);
                        }
                        tag.TagHeadPosition = head;

                        placed.Add(new
                        {
                            tagId = tag.Id.GetValue(),
                            rebarId = request.RebarId,
                            barIndex,
                            text = RebarHelpers.Try(() => tag.TagText),
                            headUv = frame.Uv(head)
                        });
                    }
                    catch (Exception ex)
                    {
                        // A tag that was placed but could not be finished is not left behind
                        if (tag != null && tag.IsValidObject)
                            doc.Delete(tag.Id);
                        failed.Add(new { rebarId = request.RebarId, error = ex.Message });
                    }
                }
            });

            var response = new Dictionary<string, object>
            {
                ["viewId"] = view.Id.GetValue(),
                ["tagType"] = $"{tagType.FamilyName} : {tagType.Name}",
                ["tags"] = placed
            };
            if (failed.Count > 0)
                response["failed"] = failed;
            if (warnings.Count > 0)
                response["revitWarnings"] = warnings;

            return new AIResult<object>
            {
                Success = placed.Count > 0,
                Message = $"Placed {placed.Count} rebar tag(s) in view '{view.Name}'"
                          + (failed.Count > 0 ? $", {failed.Count} failed" : ""),
                Response = response
            };
        }

        /// <summary>
        /// Null when no candidate gave a visible tag. <paramref name="createError"/> is Revit's own
        /// message when it refused every candidate (3D view not locked, reference not taggable...).
        /// </summary>
        private static IndependentTag PlaceVisibleTag(Document doc, View view, Element element, ElementId tagTypeId, int? wantedBar,
            TagOrientation orientation, XYZ head, out int barIndex, out Reference reference, out string createError)
        {
            var subelements = RebarHelpers.Try(() => element.GetSubelements()) ?? new List<Subelement>();
            barIndex = 0;
            reference = null;
            createError = null;

            var candidates = new List<KeyValuePair<int, Reference>>();
            if (subelements.Count == 0)
                candidates.Add(new KeyValuePair<int, Reference>(0, new Reference(element)));
            else
                candidates.AddRange(BarsToTry(subelements.Count, wantedBar)
                    .Select(i => new KeyValuePair<int, Reference>(i, subelements[i].GetReference())));

            string firstError = null;
            bool anyCreated = false;
            foreach (var candidate in candidates)
            {
                IndependentTag tag;
                try
                {
                    // Placed without a leader (added afterwards): a leader line alone could give the
                    // tag a bounding box and hide the fact that the tag itself is not drawn
                    tag = IndependentTag.Create(doc, tagTypeId, view.Id, candidate.Value, false, orientation, head);
                }
                catch (Exception ex)
                {
                    firstError = firstError ?? ex.Message;
                    continue;
                }

                anyCreated = true;
                doc.Regenerate();
                if (tag.get_BoundingBox(view) != null)
                {
                    barIndex = candidate.Key;
                    reference = candidate.Value;
                    return tag;
                }
                doc.Delete(tag.Id);
            }

            if (!anyCreated)
                createError = firstError;
            return null;
        }

        /// <summary>The asked-for bar only, or: first, middle, last, then the rest in order.</summary>
        private static IEnumerable<int> BarsToTry(int count, int? wanted)
        {
            if (wanted.HasValue)
            {
                if (wanted.Value < 0 || wanted.Value >= count)
                    throw new Exception($"barIndex {wanted.Value} is outside 0..{count - 1}");
                return new[] { wanted.Value };
            }

            var order = new List<int> { 0, count / 2, count - 1 };
            order.AddRange(Enumerable.Range(0, count));
            return order.Distinct().Take(MaxBarsToTry);
        }

        private static List<TagRequest> Requests(UIDocument uiDoc, JObject p)
        {
            if (p["tags"] is JArray tags && tags.Count > 0)
            {
                return tags.Select(t => new TagRequest
                {
                    RebarId = t["rebarId"]?.Value<long?>() ?? throw new Exception("each item in 'tags' needs rebarId"),
                    BarIndex = t["barIndex"]?.Value<int?>(),
                    Head = t["head"],
                    LeaderEnd = t["leaderEnd"],
                    LeaderElbow = t["leaderElbow"],
                    OffsetU = t["offsetMm"]?["u"]?.Value<double?>(),
                    OffsetV = t["offsetMm"]?["v"]?.Value<double?>()
                }).ToList();
            }

            return RebarHelpers.ResolveReinforcement(uiDoc, p, true)
                .Select(e => new TagRequest { RebarId = e.Id.GetValue() })
                .ToList();
        }

        private static FamilySymbol ResolveTagType(Document doc, JObject p)
        {
            var all = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RebarTags)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();
            if (all.Count == 0)
                throw new Exception("No structural rebar tag family is loaded in this model");

            var id = p["tagTypeId"]?.Value<long?>();
            if (id.HasValue)
                return all.FirstOrDefault(t => t.Id.GetValue() == id.Value)
                       ?? throw new Exception($"Element {id.Value} is not a structural rebar tag type");

            string name = p["tagTypeName"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                string wanted = Normalize(name);
                var match = all.FirstOrDefault(t => Normalize($"{t.FamilyName}:{t.Name}") == wanted)
                            ?? all.FirstOrDefault(t => Normalize(t.Name) == wanted)
                            ?? all.FirstOrDefault(t => Normalize(t.FamilyName) == wanted);
                if (match != null)
                    return match;
                throw new Exception($"Rebar tag type '{name}' not found. Available: "
                                    + string.Join("; ", all.Select(t => $"{t.FamilyName} : {t.Name}").OrderBy(n => n).Take(40))
                                    + ". Use get_rebar_types with include ['rebarTagTypes'].");
            }

            var defaultId = doc.GetDefaultFamilyTypeId(new ElementId(BuiltInCategory.OST_RebarTags));
            return all.FirstOrDefault(t => t.Id == defaultId) ?? all[0];
        }

        private static string Normalize(string name)
        {
            return name.Replace(" ", "").ToLowerInvariant();
        }
    }
}
