using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using RevitRebar = Autodesk.Revit.DB.Structure.Rebar;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Describes a rebar host (can it host rebar, covers, a local frame with its extents so the
    /// caller can place bars) and the rebar already in it. Read-only.
    /// </summary>
    public class GetHostRebarEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private static readonly BuiltInParameter[] CoverParameters =
        {
            BuiltInParameter.CLEAR_COVER_TOP,
            BuiltInParameter.CLEAR_COVER_BOTTOM,
            BuiltInParameter.CLEAR_COVER_OTHER,
            BuiltInParameter.CLEAR_COVER_EXTERIOR,
            BuiltInParameter.CLEAR_COVER_INTERIOR
        };

        public long? HostId { get; private set; }
        public bool IncludeGeometry { get; private set; }
        public AIResult<object> Result { get; private set; }

        public void SetParameters(long? hostId, bool includeGeometry)
        {
            HostId = hostId;
            IncludeGeometry = includeGeometry;
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
                var host = RebarHelpers.ResolveHost(uiDoc, HostId);

                bool isValidHost = RebarHostData.IsValidHost(host);
                var response = new Dictionary<string, object>
                {
                    ["hostId"] = host.Id.GetValue(),
                    ["category"] = host.Category?.Name ?? "",
                    ["typeName"] = RebarHelpers.NameOf(doc, host.GetTypeId()) ?? host.Name,
                    ["isValidRebarHost"] = isValidHost,
                    ["covers"] = ReadCovers(doc, host),
                    ["frame"] = DescribeFrame(host)
                };

                if (!isValidHost)
                {
                    Result = new AIResult<object>
                    {
                        Success = true,
                        Message = $"Element {host.Id.GetValue()} ({host.Category?.Name}) cannot host rebar. "
                                  + "Rebar hosts are structural concrete elements: beams, columns, walls, floors, foundations.",
                        Response = response
                    };
                    return;
                }

                var hostData = RebarHostData.GetRebarHostData(host);
                var rebars = hostData.GetRebarsInHost();
                response["rebarCount"] = rebars.Count;
                response["rebar"] = rebars.Select(r => Try(() => DescribeRebar(doc, r)) ?? new { id = r.Id.GetValue(), error = "could not be read" }).ToList();
                response["areaReinforcementIds"] = hostData.GetAreaReinforcementsInHost().Select(a => a.Id.GetValue()).ToList();
                response["pathReinforcementIds"] = hostData.GetPathReinforcementsInHost().Select(p => p.Id.GetValue()).ToList();

                Result = new AIResult<object>
                {
                    Success = true,
                    Message = $"{host.Category?.Name} {host.Id.GetValue()} holds {rebars.Count} rebar element(s)",
                    Response = response
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object>
                {
                    Success = false,
                    Message = $"Failed to read host rebar: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private static Dictionary<string, object> ReadCovers(Document doc, Element host)
        {
            var covers = new Dictionary<string, object>();
            foreach (var bip in CoverParameters)
            {
                var p = host.get_Parameter(bip);
                if (p == null || p.StorageType != StorageType.ElementId)
                    continue;
                var cover = doc.GetElement(p.AsElementId()) as RebarCoverType;
                if (cover == null)
                    continue;
                string key = bip.ToString().Replace("CLEAR_COVER_", "").ToLowerInvariant();
                covers[key] = new { name = cover.Name, distanceMm = RebarHelpers.ToMm(cover.CoverDistance) };
            }
            return covers;
        }

        /// <summary>
        /// A local frame for the host and the extents of its solid geometry in that frame.
        /// Beams/walls: x along the location line, y horizontal across it (to the left looking
        /// along x), z up and square to x.
        /// Other family instances: the instance transform. Anything else: world axes.
        /// </summary>
        private static object DescribeFrame(Element host)
        {
            XYZ origin = XYZ.Zero, xAxis = XYZ.BasisX, yAxis = XYZ.BasisY, zAxis = XYZ.BasisZ;
            string kind = "world";

            if (host.Location is LocationCurve lc && lc.Curve is Line line
                && Math.Abs(line.Direction.Z) < 0.999)
            {
                origin = line.GetEndPoint(0);
                xAxis = line.Direction.Normalize();
                yAxis = XYZ.BasisZ.CrossProduct(xAxis).Normalize();
                zAxis = xAxis.CrossProduct(yAxis).Normalize();
                kind = "locationLine";
            }
            else if (host is FamilyInstance fi)
            {
                var t = fi.GetTransform();
                origin = t.Origin;
                xAxis = t.BasisX;
                yAxis = t.BasisY;
                zAxis = t.BasisZ;
                kind = "instanceTransform";
            }
            else if (host.Location is LocationCurve lc2)
            {
                origin = lc2.Curve.GetEndPoint(0);
                kind = "worldAtLocationStart";
            }

            var points = CollectGeometryPoints(host);
            if (points.Count == 0)
            {
                var bb = host.get_BoundingBox(null);
                if (bb != null)
                {
                    foreach (var x in new[] { bb.Min.X, bb.Max.X })
                    foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                        points.Add(new XYZ(x, y, z));
                }
            }

            object extents = null;
            if (points.Count > 0)
            {
                var xs = points.Select(p => (p - origin).DotProduct(xAxis)).ToList();
                var ys = points.Select(p => (p - origin).DotProduct(yAxis)).ToList();
                var zs = points.Select(p => (p - origin).DotProduct(zAxis)).ToList();
                extents = new
                {
                    xMinMm = RebarHelpers.ToMm(xs.Min()), xMaxMm = RebarHelpers.ToMm(xs.Max()),
                    yMinMm = RebarHelpers.ToMm(ys.Min()), yMaxMm = RebarHelpers.ToMm(ys.Max()),
                    zMinMm = RebarHelpers.ToMm(zs.Min()), zMaxMm = RebarHelpers.ToMm(zs.Max())
                };
            }

            return new
            {
                kind,
                originMm = RebarHelpers.PointToMm(origin),
                xAxis = RebarHelpers.Vector(xAxis),
                yAxis = RebarHelpers.Vector(yAxis),
                zAxis = RebarHelpers.Vector(zAxis),
                extents,
                note = "World point = origin + x*xAxis + y*yAxis + z*zAxis (mm). Extents are the host's solid geometry in this frame."
            };
        }

        private static List<XYZ> CollectGeometryPoints(Element host)
        {
            var points = new List<XYZ>();
            var geom = host.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (geom == null)
                return points;

            void Walk(GeometryElement ge)
            {
                foreach (var obj in ge)
                {
                    if (obj is Solid solid && solid.Volume > 1e-9)
                    {
                        foreach (Edge edge in solid.Edges)
                            points.AddRange(edge.Tessellate());
                    }
                    else if (obj is GeometryInstance gi)
                    {
                        Walk(gi.GetInstanceGeometry());
                    }
                }
            }

            Walk(geom);
            return points;
        }

        private object DescribeRebar(Document doc, RevitRebar rebar)
        {
            // GetShapeId throws for free-form rebar matched to several shapes
            var shape = Try(() => doc.GetElement(rebar.GetShapeId())) as RebarShape;
            var barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
            bool shapeDriven = rebar.IsRebarShapeDriven();

            var info = new Dictionary<string, object>
            {
                ["id"] = rebar.Id.GetValue(),
                ["barType"] = barType?.Name,
                ["diameterMm"] = barType != null ? RebarHelpers.ToMm(barType.BarNominalDiameter) : (double?)null,
                ["shape"] = shape?.Name,
                ["style"] = shape?.RebarStyle.ToString(),
                ["shapeDriven"] = shapeDriven,
                ["layoutRule"] = rebar.LayoutRule.ToString(),
                ["quantity"] = rebar.Quantity,
                ["barPositions"] = rebar.NumberOfBarPositions,
                ["barLengthMm"] = ReadLengthMm(rebar, BuiltInParameter.REBAR_ELEM_LENGTH),
                ["totalLengthMm"] = RebarHelpers.ToMm(rebar.TotalLength),
                ["hookStart"] = RebarHelpers.NameOf(doc, rebar.GetHookTypeId(0)),
                ["hookEnd"] = RebarHelpers.NameOf(doc, rebar.GetHookTypeId(1)),
#if REVIT2026_OR_GREATER
                ["hookOrientationStart"] = rebar.GetTerminationOrientation(0).ToString(),
                ["hookOrientationEnd"] = rebar.GetTerminationOrientation(1).ToString(),
#else
                ["hookOrientationStart"] = rebar.GetHookOrientation(0).ToString(),
                ["hookOrientationEnd"] = rebar.GetHookOrientation(1).ToString(),
#endif
                ["mark"] = rebar.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                ["rebarNumber"] = rebar.get_Parameter(BuiltInParameter.REBAR_NUMBER)?.AsString(),
                ["partition"] = rebar.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.AsString()
            };

            if (rebar.LayoutRule != RebarLayoutRule.Single)
                info["spacingMm"] = Try(() => RebarHelpers.ToMm(rebar.MaxSpacing));

            if (shapeDriven)
            {
                var accessor = rebar.GetShapeDrivenAccessor();
                info["arrayLengthMm"] = Try(() => RebarHelpers.ToMm(accessor.ArrayLength));
                info["normal"] = Try(() => RebarHelpers.Vector(accessor.Normal));
            }

            if (IncludeGeometry)
            {
                info["firstBarCenterline"] = Try(() => rebar
                    .GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0)
                    .Select(c => new
                    {
                        type = c is Arc ? "arc" : c is Line ? "line" : c.GetType().Name,
                        startMm = RebarHelpers.PointToMm(c.GetEndPoint(0)),
                        endMm = RebarHelpers.PointToMm(c.GetEndPoint(1)),
                        lengthMm = RebarHelpers.ToMm(c.Length)
                    })
                    .ToList());
            }

            return info;
        }

        private static double? ReadLengthMm(Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            if (p == null || p.StorageType != StorageType.Double)
                return null;
            return RebarHelpers.ToMm(p.AsDouble());
        }

        private static object Try(Func<object> read)
        {
            try { return read(); }
            catch { return null; }
        }

        public string GetName() => "Get Host Rebar";
    }
}
