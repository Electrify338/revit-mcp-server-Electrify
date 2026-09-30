using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Shared helpers for the rebar tools: units, lookups by id or name, host resolution.
    /// All lengths crossing the MCP boundary are millimetres; Revit works in feet.
    /// </summary>
    internal static class RebarHelpers
    {
        public const double MmPerFoot = 304.8;

        public static double ToMm(double feet) => Math.Round(feet * MmPerFoot, 1);

        public static double ToFeet(double mm) => mm / MmPerFoot;

        public static XYZ PointFromMm(JToken p)
        {
            if (p == null)
                throw new ArgumentException("Point is missing");
            return new XYZ(
                ToFeet(p["x"]?.Value<double>() ?? 0),
                ToFeet(p["y"]?.Value<double>() ?? 0),
                ToFeet(p["z"]?.Value<double>() ?? 0));
        }

        public static XYZ VectorFromJson(JToken v)
        {
            if (v == null)
                return null;
            return new XYZ(
                v["x"]?.Value<double>() ?? 0,
                v["y"]?.Value<double>() ?? 0,
                v["z"]?.Value<double>() ?? 0);
        }

        public static object PointToMm(XYZ p) => new
        {
            x = ToMm(p.X),
            y = ToMm(p.Y),
            z = ToMm(p.Z)
        };

        public static object Vector(XYZ v) => new
        {
            x = Math.Round(v.X, 6),
            y = Math.Round(v.Y, 6),
            z = Math.Round(v.Z, 6)
        };

        /// <summary>
        /// Host from an explicit id, or the single selected element when no id is given.
        /// </summary>
        public static Element ResolveHost(UIDocument uiDoc, long? hostId)
        {
            var doc = uiDoc.Document;
            if (hostId.HasValue)
            {
                var element = doc.GetElement(Utils.ElementIdExtensions.FromLong(hostId.Value));
                if (element == null)
                    throw new Exception($"No element with id {hostId.Value}");
                return element;
            }

            var selected = uiDoc.Selection.GetElementIds();
            if (selected.Count == 1)
                return doc.GetElement(selected.First());
            if (selected.Count == 0)
                throw new Exception("No hostId given and nothing is selected. Pass hostId or select one host element.");
            throw new Exception($"No hostId given and {selected.Count} elements are selected. Pass hostId or select exactly one host element.");
        }

        /// <summary>
        /// Element of type T by id (if given) or by name (case-insensitive). Null when neither is given.
        /// </summary>
        public static T FindByIdOrName<T>(Document doc, long? id, string name, string what) where T : Element
        {
            if (id.HasValue)
            {
                var byId = doc.GetElement(Utils.ElementIdExtensions.FromLong(id.Value)) as T;
                if (byId == null)
                    throw new Exception($"Element {id.Value} is not a {what}");
                return byId;
            }

            if (string.IsNullOrWhiteSpace(name))
                return null;

            var all = AllOfClass<T>(doc).ToList();
            var match = all.FirstOrDefault(e => e.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;

            var names = string.Join(", ", all.Select(e => e.Name).OrderBy(n => n).Take(30));
            throw new Exception($"{what} '{name}' not found. Available: {names}{(all.Count > 30 ? ", ..." : "")}. Use get_rebar_types to list them.");
        }

        /// <summary>
        /// All elements of a class. Area and path reinforcement types are not in Revit's native
        /// object model, so the class filter refuses them and they are picked out of the element types.
        /// </summary>
        public static IEnumerable<T> AllOfClass<T>(Document doc) where T : Element
        {
            if (typeof(T) == typeof(AreaReinforcementType) || typeof(T) == typeof(PathReinforcementType))
                return new FilteredElementCollector(doc).WhereElementIsElementType().OfType<T>();
            return new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>();
        }

        public static string NameOf(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId)
                return null;
            return doc.GetElement(id)?.Name;
        }

        public static long? IdOrNull(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId)
                return null;
            return id.GetValue();
        }

        public static T ParseEnum<T>(string value, string what, T fallback) where T : struct
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            if (Enum.TryParse(value.Trim(), true, out T parsed) && Enum.IsDefined(typeof(T), parsed))
                return parsed;
            throw new Exception($"Invalid {what} '{value}'. Valid: {string.Join(", ", Enum.GetNames(typeof(T)))}");
        }

        /// <summary>Element ids from a JSON array of numbers (or a single number). Empty when missing.</summary>
        public static List<long> Ids(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return new List<long>();
            if (token is JArray array)
                return array.Select(t => t.Value<long>()).ToList();
            return new List<long> { token.Value<long>() };
        }

        public static List<int> Ints(JToken token)
        {
            return token is JArray array ? array.Select(t => t.Value<int>()).ToList() : new List<int>();
        }

        public static double? FeetOrNull(JToken mm)
        {
            var value = mm?.Value<double?>();
            return value.HasValue ? ToFeet(value.Value) : (double?)null;
        }

        /// <summary>The view with this id, or the active view. View templates are refused.</summary>
        public static View ResolveView(UIDocument uiDoc, long? viewId)
        {
            View view;
            if (viewId.HasValue)
            {
                view = uiDoc.Document.GetElement(Utils.ElementIdExtensions.FromLong(viewId.Value)) as View;
                if (view == null)
                    throw new Exception($"Element {viewId.Value} is not a view");
            }
            else
            {
                view = uiDoc.ActiveView ?? throw new Exception("No active view. Pass viewId.");
            }

            if (view.IsTemplate)
                throw new Exception($"View {view.Id.GetValue()} is a view template");
            return view;
        }

        public static bool IsReinforcement(Element element)
        {
            return element is Autodesk.Revit.DB.Structure.Rebar || element is RebarInSystem;
        }

        /// <summary>Rebar in a host; with <paramref name="includeInSystem"/> also the bars of its area and path reinforcement.</summary>
        public static List<Element> HostedReinforcement(Document doc, Element host, bool includeInSystem)
        {
            var result = new List<Element>();
            if (!RebarHostData.IsValidHost(host))
                return result;

            var hostData = RebarHostData.GetRebarHostData(host);
            result.AddRange(hostData.GetRebarsInHost());
            if (includeInSystem)
            {
                var systemIds = hostData.GetAreaReinforcementsInHost().SelectMany(a => a.GetRebarInSystemIds())
                    .Concat(hostData.GetPathReinforcementsInHost().SelectMany(p => p.GetRebarInSystemIds()));
                foreach (var id in systemIds)
                {
                    var element = doc.GetElement(id);
                    if (element != null)
                        result.Add(element);
                }
            }
            return result;
        }

        /// <summary>
        /// The reinforcement a tool should act on: 'rebarIds' if given, else everything hosted by
        /// 'hostIds' / 'hostId', else the selection (selected rebar, or the rebar of selected hosts).
        /// </summary>
        public static List<Element> ResolveReinforcement(UIDocument uiDoc, JObject p, bool includeInSystem)
        {
            var doc = uiDoc.Document;
            var rebarIds = Ids(p["rebarIds"]);
            if (rebarIds.Count == 0 && p["rebarId"] != null)
                rebarIds = Ids(p["rebarId"]);

            if (rebarIds.Count > 0)
            {
                var elements = new List<Element>();
                foreach (var id in rebarIds)
                {
                    var element = doc.GetElement(Utils.ElementIdExtensions.FromLong(id));
                    if (element == null)
                        throw new Exception($"No element with id {id}");
                    if (!IsReinforcement(element) || (!includeInSystem && element is RebarInSystem))
                        throw new Exception($"Element {id} ({element.Category?.Name}) is not "
                                            + (includeInSystem ? "rebar" : "a Rebar element (bars owned by area/path reinforcement are not supported here)"));
                    elements.Add(element);
                }
                return elements;
            }

            var hostIds = Ids(p["hostIds"]);
            if (hostIds.Count == 0 && p["hostId"] != null)
                hostIds = Ids(p["hostId"]);

            List<Element> hosts;
            if (hostIds.Count > 0)
            {
                hosts = hostIds.Select(id => doc.GetElement(Utils.ElementIdExtensions.FromLong(id))
                                             ?? throw new Exception($"No element with id {id}")).ToList();
            }
            else
            {
                var selected = uiDoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
                var selectedRebar = selected.Where(e => IsReinforcement(e) && (includeInSystem || !(e is RebarInSystem))).ToList();
                if (selectedRebar.Count > 0)
                    return selectedRebar;
                hosts = selected;
                if (hosts.Count == 0)
                    throw new Exception("Pass rebarIds or hostIds, or select rebar or a host element in Revit");
            }

            var hosted = hosts.SelectMany(h => HostedReinforcement(doc, h, includeInSystem)).ToList();
            if (hosted.Count == 0)
                throw new Exception("No rebar found in the given host element(s)");
            return hosted;
        }

        /// <summary>Nominal steel weight: bar area x length x density.</summary>
        public static double WeightKg(double diameterFeet, double lengthFeet, double densityKgPerM3)
        {
            double d = diameterFeet * 0.3048;
            double length = lengthFeet * 0.3048;
            return Math.PI * d * d / 4.0 * length * densityKgPerM3;
        }

        public static double? LengthParamMm(Element element, BuiltInParameter parameter)
        {
            var p = element.get_Parameter(parameter);
            return p != null && p.StorageType == StorageType.Double ? ToMm(p.AsDouble()) : (double?)null;
        }

        public static T Try<T>(Func<T> read, T fallback = default)
        {
            try { return read(); }
            catch { return fallback; }
        }
    }

    /// <summary>How many bars a shape-driven set has and how they are spaced. Lengths in feet.</summary>
    internal class RebarLayout
    {
        public string Rule { get; set; }
        public int? Number { get; set; }
        public double? Spacing { get; set; }
        public double? ArrayLength { get; set; }
        public bool? BarsOnNormalSide { get; set; }
        public bool? IncludeFirstBar { get; set; }
        public bool? IncludeLastBar { get; set; }

        public static RebarLayout FromJson(JObject layout)
        {
            if (layout == null)
                return null;
            return new RebarLayout
            {
                Rule = layout["rule"]?.Value<string>(),
                Number = layout["number"]?.Value<int?>(),
                Spacing = RebarHelpers.FeetOrNull(layout["spacingMm"]),
                ArrayLength = RebarHelpers.FeetOrNull(layout["arrayLengthMm"]),
                BarsOnNormalSide = layout["barsOnNormalSide"]?.Value<bool?>(),
                IncludeFirstBar = layout["includeFirstBar"]?.Value<bool?>(),
                IncludeLastBar = layout["includeLastBar"]?.Value<bool?>()
            };
        }

        /// <summary>
        /// Applies the layout. Anything left out keeps the set's current value, so a call can change
        /// just the spacing or just the number of bars.
        /// </summary>
        public void Apply(Autodesk.Revit.DB.Structure.Rebar rebar)
        {
            if (!rebar.IsRebarShapeDriven())
                throw new Exception($"Rebar {rebar.Id.GetValue()} is free-form; its layout cannot be set by this tool");

            var accessor = rebar.GetShapeDrivenAccessor();
            var current = rebar.LayoutRule;
            var rule = RebarHelpers.ParseEnum(Rule, "layout.rule", current);
            if (rule == RebarLayoutRule.Single)
            {
                if (string.IsNullOrWhiteSpace(Rule) && (Number.HasValue || Spacing.HasValue || ArrayLength.HasValue))
                    throw new Exception("this is a single bar: pass layout.rule (FixedNumber, MaximumSpacing, NumberWithSpacing "
                                        + "or MinimumClearSpacing) to turn it into a set");
                if (current != RebarLayoutRule.Single)
                    accessor.SetLayoutAsSingle();
                return;
            }

            // A value the rule does not use would be dropped without a word
            if (rule == RebarLayoutRule.FixedNumber && Spacing.HasValue)
                throw new Exception("layout.spacingMm does not apply to the FixedNumber rule (it spreads 'number' bars over arrayLengthMm); "
                                    + "use rule MaximumSpacing or NumberWithSpacing to set a spacing");
            if ((rule == RebarLayoutRule.MaximumSpacing || rule == RebarLayoutRule.MinimumClearSpacing) && Number.HasValue)
                throw new Exception($"layout.number does not apply to the {rule} rule (the number follows from spacingMm and arrayLengthMm); "
                                    + "use rule FixedNumber or NumberWithSpacing to set a number");
            if (rule == RebarLayoutRule.NumberWithSpacing && ArrayLength.HasValue)
                throw new Exception("layout.arrayLengthMm does not apply to the NumberWithSpacing rule (the length follows from number and spacingMm)");

            bool isSet = current != RebarLayoutRule.Single;
            int? number = Number ?? (isSet ? rebar.NumberOfBarPositions : (int?)null);
            double? spacing = Spacing ?? (isSet ? RebarHelpers.Try(() => (double?)rebar.MaxSpacing) : null);
            double? arrayLength = ArrayLength ?? (isSet ? RebarHelpers.Try(() => (double?)accessor.ArrayLength) : null);
            bool side = BarsOnNormalSide ?? (isSet ? accessor.BarsOnNormalSide : true);
            bool first = IncludeFirstBar ?? (isSet ? rebar.IncludeFirstBar : true);
            bool last = IncludeLastBar ?? (isSet ? rebar.IncludeLastBar : true);

            switch (rule)
            {
                case RebarLayoutRule.FixedNumber:
                    accessor.SetLayoutAsFixedNumber(Require(number, "layout.number"), Require(arrayLength, "layout.arrayLengthMm"), side, first, last);
                    break;
                case RebarLayoutRule.MaximumSpacing:
                    accessor.SetLayoutAsMaximumSpacing(Require(spacing, "layout.spacingMm"), Require(arrayLength, "layout.arrayLengthMm"), side, first, last);
                    break;
                case RebarLayoutRule.NumberWithSpacing:
                    accessor.SetLayoutAsNumberWithSpacing(Require(number, "layout.number"), Require(spacing, "layout.spacingMm"), side, first, last);
                    break;
                case RebarLayoutRule.MinimumClearSpacing:
                    accessor.SetLayoutAsMinimumClearSpacing(Require(spacing, "layout.spacingMm"), Require(arrayLength, "layout.arrayLengthMm"), side, first, last);
                    break;
            }
        }

        private static T Require<T>(T? value, string what) where T : struct
        {
            if (!value.HasValue)
                throw new Exception($"{what} is required for this layout rule");
            return value.Value;
        }

        public static object Describe(Autodesk.Revit.DB.Structure.Rebar rebar)
        {
            bool isSet = rebar.LayoutRule != RebarLayoutRule.Single;
            return new
            {
                rebarId = rebar.Id.GetValue(),
                layoutRule = rebar.LayoutRule.ToString(),
                quantity = rebar.Quantity,
                barPositions = rebar.NumberOfBarPositions,
                spacingMm = isSet ? RebarHelpers.Try(() => (double?)RebarHelpers.ToMm(rebar.MaxSpacing)) : null,
                arrayLengthMm = isSet && rebar.IsRebarShapeDriven()
                    ? RebarHelpers.Try(() => (double?)RebarHelpers.ToMm(rebar.GetShapeDrivenAccessor().ArrayLength))
                    : null,
                includeFirstBar = rebar.IncludeFirstBar,
                includeLastBar = rebar.IncludeLastBar
            };
        }
    }
}
