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

            var all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
            var match = all.FirstOrDefault(e => e.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;

            var names = string.Join(", ", all.Select(e => e.Name).OrderBy(n => n).Take(30));
            throw new Exception($"{what} '{name}' not found. Available: {names}{(all.Count > 30 ? ", ..." : "")}. Use get_rebar_types to list them.");
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
    }
}
