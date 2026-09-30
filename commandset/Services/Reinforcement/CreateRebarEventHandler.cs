using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>Input of create_rebar, lengths already converted to feet.</summary>
    public class CreateRebarRequest
    {
        public long? HostId { get; set; }
        public long? BarTypeId { get; set; }
        public string BarTypeName { get; set; }
        public string Style { get; set; } = "Standard";
        public List<XYZ> Points { get; set; } = new List<XYZ>();
        public XYZ Normal { get; set; }
        public long? ShapeId { get; set; }
        public string ShapeName { get; set; }
        public long? StartHookId { get; set; }
        public string StartHookName { get; set; }
        public long? EndHookId { get; set; }
        public string EndHookName { get; set; }
        public string StartHookOrientation { get; set; } = "Left";
        public string EndHookOrientation { get; set; } = "Left";
        public string LayoutRule { get; set; } = "Single";
        public int? Number { get; set; }
        public double? Spacing { get; set; }
        public double? ArrayLength { get; set; }
        public bool BarsOnNormalSide { get; set; } = true;
        public bool IncludeFirstBar { get; set; } = true;
        public bool IncludeLastBar { get; set; } = true;
        public bool UseExistingShapeIfPossible { get; set; } = true;
        public bool CreateNewShape { get; set; } = true;
        public bool ShowUnobscuredInActiveView { get; set; }
    }

    /// <summary>
    /// Creates one shape-driven rebar (a single bar or a set) in a host from a polyline.
    /// Revit 2026+ uses BarTerminationsData; earlier versions the hook-type overloads
    /// (deprecated in 2026, removed in 2027).
    /// </summary>
    public class CreateRebarEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public CreateRebarRequest Request { get; private set; }
        public AIResult<object> Result { get; private set; }

        public void SetParameters(CreateRebarRequest request)
        {
            Request = request;
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
                var uiDoc = app.ActiveUIDocument;
                var doc = uiDoc.Document;
                var r = Request;

                var host = RebarHelpers.ResolveHost(uiDoc, r.HostId);
                if (!RebarHostData.IsValidHost(host))
                    throw new Exception($"Element {host.Id.GetValue()} ({host.Category?.Name}) cannot host rebar. "
                                        + "Use a structural concrete beam, column, wall, floor or foundation.");

                var barType = RebarHelpers.FindByIdOrName<RebarBarType>(doc, r.BarTypeId, r.BarTypeName, "Rebar bar type")
                              ?? throw new Exception("barTypeName or barTypeId is required");
                var shape = RebarHelpers.FindByIdOrName<RebarShape>(doc, r.ShapeId, r.ShapeName, "Rebar shape");
                var startHook = RebarHelpers.FindByIdOrName<RebarHookType>(doc, r.StartHookId, r.StartHookName, "Rebar hook type");
                var endHook = RebarHelpers.FindByIdOrName<RebarHookType>(doc, r.EndHookId, r.EndHookName, "Rebar hook type");
                var style = ParseStyle(r.Style);

                var notes = new List<string>();
                var curves = BuildCurves(r.Points);
                var normal = ResolveNormal(r.Points, r.Normal, notes);

                var failures = new CollectingFailuresPreprocessor();
                RevitRebar rebar;
                using (var tx = new Transaction(doc, "MCP: Create Rebar"))
                {
                    var options = tx.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(failures);
                    options.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(options);
                    tx.Start();
                    try
                    {
                        rebar = Create(doc, r, style, barType, shape, startHook, endHook, host, normal, curves);
                        if (rebar == null)
                            throw new Exception("Revit found no rebar shape matching these curves and hooks. "
                                                + "Set createNewShape=true, pass a shapeName that fits, or check the points.");

                        ApplyLayout(rebar, r);

                        if (r.ShowUnobscuredInActiveView && uiDoc.ActiveView != null && !uiDoc.ActiveView.IsTemplate)
                            rebar.SetUnobscuredInView(uiDoc.ActiveView, true);

                        var status = tx.Commit();
                        if (status != TransactionStatus.Committed)
                            throw new Exception("Revit rolled the rebar back: "
                                                + (failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : status.ToString()));
                    }
                    catch
                    {
                        if (tx.GetStatus() == TransactionStatus.Started)
                            tx.RollBack();
                        throw;
                    }
                }

                var usedShape = doc.GetElement(rebar.GetShapeId()) as RebarShape;
                var response = new Dictionary<string, object>
                {
                    ["rebarId"] = rebar.Id.GetValue(),
                    ["hostId"] = host.Id.GetValue(),
                    ["barType"] = barType.Name,
                    ["shape"] = usedShape?.Name,
                    ["style"] = style.ToString(),
                    ["layoutRule"] = rebar.LayoutRule.ToString(),
                    ["quantity"] = rebar.Quantity,
                    ["barLengthMm"] = LengthMm(rebar, BuiltInParameter.REBAR_ELEM_LENGTH),
                    ["totalLengthMm"] = RebarHelpers.ToMm(rebar.TotalLength),
                    ["hookStart"] = startHook?.Name,
                    ["hookEnd"] = endHook?.Name,
                    ["normalUsed"] = RebarHelpers.Vector(normal)
                };
                if (notes.Count > 0)
                    response["notes"] = notes;
                if (failures.Warnings.Count > 0)
                    response["revitWarnings"] = failures.Warnings;

                Result = new AIResult<object>
                {
                    Success = true,
                    Message = $"Created rebar {rebar.Id.GetValue()} ({barType.Name}, {usedShape?.Name}, {rebar.Quantity} bar(s)) in {host.Category?.Name} {host.Id.GetValue()}"
                              + (failures.Warnings.Count > 0 ? $" with {failures.Warnings.Count} Revit warning(s)" : ""),
                    Response = response
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object>
                {
                    Success = false,
                    Message = $"Failed to create rebar: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private static RevitRebar Create(Document doc, CreateRebarRequest r, RebarStyle style, RebarBarType barType,
            RebarShape shape, RebarHookType startHook, RebarHookType endHook, Element host, XYZ normal, IList<Curve> curves)
        {
#if REVIT2026_OR_GREATER
            using (var terminations = new BarTerminationsData(doc))
            {
                if (startHook != null)
                    terminations.HookTypeIdAtStart = startHook.Id;
                if (endHook != null)
                    terminations.HookTypeIdAtEnd = endHook.Id;
                terminations.TerminationOrientationAtStart = ParseEnum(r.StartHookOrientation, "startHookOrientation", RebarTerminationOrientation.Left);
                terminations.TerminationOrientationAtEnd = ParseEnum(r.EndHookOrientation, "endHookOrientation", RebarTerminationOrientation.Left);

                return shape != null
                    ? RevitRebar.CreateFromCurvesAndShape(doc, shape, barType, host, normal, curves, terminations)
                    : RevitRebar.CreateFromCurves(doc, style, barType, host, normal, curves, terminations,
                        r.UseExistingShapeIfPossible, r.CreateNewShape);
            }
#else
            var startOrientation = ParseEnum(r.StartHookOrientation, "startHookOrientation", RebarHookOrientation.Left);
            var endOrientation = ParseEnum(r.EndHookOrientation, "endHookOrientation", RebarHookOrientation.Left);
            return shape != null
                ? RevitRebar.CreateFromCurvesAndShape(doc, shape, barType, startHook, endHook, host, normal, curves,
                    startOrientation, endOrientation)
                : RevitRebar.CreateFromCurves(doc, style, barType, startHook, endHook, host, normal, curves,
                    startOrientation, endOrientation, r.UseExistingShapeIfPossible, r.CreateNewShape);
#endif
        }

        private static void ApplyLayout(RevitRebar rebar, CreateRebarRequest r)
        {
            var rule = ParseEnum(r.LayoutRule, "layout.rule", RebarLayoutRule.Single);
            if (rule == RebarLayoutRule.Single)
                return;

            var accessor = rebar.GetShapeDrivenAccessor();
            switch (rule)
            {
                case RebarLayoutRule.FixedNumber:
                    accessor.SetLayoutAsFixedNumber(Require(r.Number, "layout.number"), Require(r.ArrayLength, "layout.arrayLengthMm"),
                        r.BarsOnNormalSide, r.IncludeFirstBar, r.IncludeLastBar);
                    break;
                case RebarLayoutRule.MaximumSpacing:
                    accessor.SetLayoutAsMaximumSpacing(Require(r.Spacing, "layout.spacingMm"), Require(r.ArrayLength, "layout.arrayLengthMm"),
                        r.BarsOnNormalSide, r.IncludeFirstBar, r.IncludeLastBar);
                    break;
                case RebarLayoutRule.NumberWithSpacing:
                    accessor.SetLayoutAsNumberWithSpacing(Require(r.Number, "layout.number"), Require(r.Spacing, "layout.spacingMm"),
                        r.BarsOnNormalSide, r.IncludeFirstBar, r.IncludeLastBar);
                    break;
                case RebarLayoutRule.MinimumClearSpacing:
                    accessor.SetLayoutAsMinimumClearSpacing(Require(r.Spacing, "layout.spacingMm"), Require(r.ArrayLength, "layout.arrayLengthMm"),
                        r.BarsOnNormalSide, r.IncludeFirstBar, r.IncludeLastBar);
                    break;
            }
        }

        private static IList<Curve> BuildCurves(List<XYZ> points)
        {
            if (points == null || points.Count < 2)
                throw new Exception("points needs at least 2 points (a straight bar); more points make a bent bar");

            var curves = new List<Curve>();
            for (int i = 0; i < points.Count - 1; i++)
            {
                if (points[i].DistanceTo(points[i + 1]) < RebarHelpers.ToFeet(1))
                    throw new Exception($"points {i} and {i + 1} are closer than 1 mm");
                curves.Add(Line.CreateBound(points[i], points[i + 1]));
            }
            return curves;
        }

        /// <summary>
        /// The normal is square to the plane of the bar and is the direction a set is laid out in.
        /// Bent bars: derived from the points when not given. Straight bars: must be given.
        /// </summary>
        private static XYZ ResolveNormal(List<XYZ> points, XYZ given, List<string> notes)
        {
            XYZ planeNormal = null;
            var first = points[1] - points[0];
            for (int i = 2; i < points.Count && planeNormal == null; i++)
            {
                var cross = first.CrossProduct(points[i] - points[0]);
                if (cross.GetLength() > 1e-6)
                    planeNormal = cross.Normalize();
            }

            XYZ normal;
            if (given != null && given.GetLength() > 1e-9)
            {
                normal = given.Normalize();
            }
            else if (planeNormal != null)
            {
                normal = planeNormal;
                notes.Add("normal was derived from the points; pass it explicitly to choose which side a set grows to");
            }
            else
            {
                throw new Exception("A straight bar needs 'normal': the direction square to the bar that a set is laid out along "
                                    + "(e.g. along a beam for stirrups, across it for longitudinal bars)");
            }

            for (int i = 0; i < points.Count - 1; i++)
            {
                var dir = (points[i + 1] - points[i]).Normalize();
                if (Math.Abs(dir.DotProduct(normal)) > 1e-3)
                    throw new Exception($"Segment {i} is not square to the normal. All points must lie in one plane and the normal must be square to it.");
            }
            for (int i = 1; i < points.Count; i++)
            {
                if (Math.Abs((points[i] - points[0]).DotProduct(normal)) > RebarHelpers.ToFeet(0.5))
                    throw new Exception($"Point {i} is not in the plane of point 0 (square to the normal)");
            }
            return normal;
        }

        private static RebarStyle ParseStyle(string style)
        {
            if (string.IsNullOrWhiteSpace(style))
                return RebarStyle.Standard;
            var s = style.Replace("/", "").Replace(" ", "");
            if (s.Equals("Stirrup", StringComparison.OrdinalIgnoreCase) || s.Equals("Tie", StringComparison.OrdinalIgnoreCase))
                return RebarStyle.StirrupTie;
            return ParseEnum(s, "style", RebarStyle.Standard);
        }

        private static T ParseEnum<T>(string value, string what, T fallback) where T : struct
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            if (Enum.TryParse(value.Trim(), true, out T parsed) && Enum.IsDefined(typeof(T), parsed))
                return parsed;
            throw new Exception($"Invalid {what} '{value}'. Valid: {string.Join(", ", Enum.GetNames(typeof(T)))}");
        }

        private static T Require<T>(T? value, string what) where T : struct
        {
            if (!value.HasValue)
                throw new Exception($"{what} is required for this layout rule");
            return value.Value;
        }

        private static double? LengthMm(Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            return p != null && p.StorageType == StorageType.Double ? RebarHelpers.ToMm(p.AsDouble()) : (double?)null;
        }

        public string GetName() => "Create Rebar";
    }
}
