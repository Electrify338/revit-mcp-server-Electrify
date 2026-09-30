using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Reinforcement
{
    /// <summary>
    /// Base for the rebar tools that take their parameters as raw JSON: handles the wait handle,
    /// turns an exception into a failed AIResult and leaves the Revit work to <see cref="Run"/>.
    /// </summary>
    public abstract class ReinforcementEventHandlerBase : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        protected JObject Parameters { get; private set; } = new JObject();
        public AIResult<object> Result { get; private set; }

        /// <summary>Lower-case verb phrase used in messages, e.g. "propagate rebar".</summary>
        protected abstract string Title { get; }

        public void SetParameters(JObject parameters)
        {
            Parameters = parameters ?? new JObject();
            Result = null;
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
                if (app.ActiveUIDocument == null)
                    throw new Exception("No document is open in Revit");
                Result = Run(app);
            }
            catch (Exception ex)
            {
                Result = new AIResult<object>
                {
                    Success = false,
                    Message = $"Failed to {Title}: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        protected abstract AIResult<object> Run(UIApplication app);

        protected static AIResult<object> Ok(string message, object response)
        {
            return new AIResult<object> { Success = true, Message = message, Response = response };
        }

        public string GetName() => Title;
    }

    /// <summary>
    /// Keeps Revit dialogs away from an unattended AI call: warnings are recorded and dismissed,
    /// errors are recorded and the transaction is rolled back.
    /// </summary>
    internal class CollectingFailuresPreprocessor : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            return Collect(accessor, Warnings, Errors)
                ? FailureProcessingResult.ProceedWithRollBack
                : FailureProcessingResult.Continue;
        }

        /// <summary>Records the pending failures and dismisses the warnings. True when there is an error.</summary>
        public static bool Collect(FailuresAccessor accessor, List<string> warnings, List<string> errors)
        {
            bool hasError = false;
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    warnings.Add(failure.GetDescriptionText());
                    accessor.DeleteWarning(failure);
                }
                else
                {
                    errors.Add(failure.GetDescriptionText());
                    hasError = true;
                }
            }
            return hasError;
        }
    }

    /// <summary>
    /// The same failure handling for API calls that open their own transaction
    /// (RebarPropagation), where no preprocessor can be attached.
    /// </summary>
    internal sealed class FailureEventScope : IDisposable
    {
        private readonly Autodesk.Revit.ApplicationServices.Application _application;

        public List<string> Warnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();

        public FailureEventScope(Autodesk.Revit.ApplicationServices.Application application)
        {
            _application = application;
            _application.FailuresProcessing += OnFailuresProcessing;
        }

        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            var accessor = e.GetFailuresAccessor();
            if (!CollectingFailuresPreprocessor.Collect(accessor, Warnings, Errors))
                return;

            // Without this the errors are still shown to the user after the rollback
            var options = accessor.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            accessor.SetFailureHandlingOptions(options);
            e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
        }

        public void Dispose()
        {
            _application.FailuresProcessing -= OnFailuresProcessing;
        }
    }

    internal static class RebarTransaction
    {
        /// <summary>
        /// Runs <paramref name="work"/> in one transaction. Revit warnings are added to
        /// <paramref name="warnings"/>; a Revit error rolls everything back and throws its text.
        /// </summary>
        public static T Run<T>(Document doc, string name, List<string> warnings, Func<T> work)
        {
            var failures = new CollectingFailuresPreprocessor();
            using (var tx = new Transaction(doc, name))
            {
                var options = tx.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(failures);
                options.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(options);
                tx.Start();
                try
                {
                    var result = work();
                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new Exception("Revit rolled the change back: "
                                            + (failures.Errors.Count > 0 ? string.Join("; ", failures.Errors.Distinct()) : status.ToString()));
                    warnings.AddRange(failures.Warnings.Distinct());
                    return result;
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                        tx.RollBack();
                    throw;
                }
            }
        }

        public static void Run(Document doc, string name, List<string> warnings, Action work)
        {
            Run<object>(doc, name, warnings, () =>
            {
                work();
                return null;
            });
        }
    }

    /// <summary>
    /// A view's own 2D coordinates: u along RightDirection, v along UpDirection, both in mm from
    /// view.Origin. Annotation tools accept points either as model {x, y, z} or as view {u, v}.
    /// </summary>
    internal class ViewFrame
    {
        public View View { get; }
        public XYZ Origin { get; }
        public XYZ Right { get; }
        public XYZ Up { get; }
        public XYZ Direction { get; }

        public ViewFrame(View view)
        {
            View = view;
            Origin = view.Origin ?? XYZ.Zero;
            Right = view.RightDirection;
            Up = view.UpDirection;
            Direction = view.ViewDirection;
        }

        public double U(XYZ p) => (p - Origin).DotProduct(Right) * RebarHelpers.MmPerFoot;

        public double V(XYZ p) => (p - Origin).DotProduct(Up) * RebarHelpers.MmPerFoot;

        public XYZ FromUv(double uMm, double vMm)
        {
            return Origin + Right * RebarHelpers.ToFeet(uMm) + Up * RebarHelpers.ToFeet(vMm);
        }

        /// <summary>{u, v} in view mm or {x, y, z} in model mm. Null when the token is missing.</summary>
        public XYZ Point(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token["u"] != null || token["v"] != null)
                return FromUv(token["u"]?.Value<double>() ?? 0, token["v"]?.Value<double>() ?? 0);
            return RebarHelpers.PointFromMm(token);
        }

        public object Uv(XYZ p) => new { u = Math.Round(U(p), 1), v = Math.Round(V(p), 1) };

        /// <summary>Extents of a model-aligned bounding box in this view's u/v. Null when there is no box.</summary>
        public UvBox Box(BoundingBoxXYZ box)
        {
            if (box == null)
                return null;
            var transform = box.Transform ?? Transform.Identity;
            var us = new List<double>();
            var vs = new List<double>();
            foreach (var x in new[] { box.Min.X, box.Max.X })
            foreach (var y in new[] { box.Min.Y, box.Max.Y })
            foreach (var z in new[] { box.Min.Z, box.Max.Z })
            {
                var p = transform.OfPoint(new XYZ(x, y, z));
                us.Add(U(p));
                vs.Add(V(p));
            }
            return new UvBox { UMin = us.Min(), UMax = us.Max(), VMin = vs.Min(), VMax = vs.Max() };
        }

        public UvBox Box(Element element) => Box(element.get_BoundingBox(View));

        public object Describe()
        {
            return new
            {
                viewId = View.Id.GetValue(),
                name = View.Name,
                viewType = View.ViewType.ToString(),
                scale = View.Scale,
                originMm = RebarHelpers.PointToMm(Origin),
                right = RebarHelpers.Vector(Right),
                up = RebarHelpers.Vector(Up),
                viewDirection = RebarHelpers.Vector(Direction),
                note = "View point {u, v} in mm = origin + u*right + v*up. Annotation tools accept {u, v} or model {x, y, z}."
            };
        }
    }

    internal class UvBox
    {
        public double UMin { get; set; }
        public double UMax { get; set; }
        public double VMin { get; set; }
        public double VMax { get; set; }

        public double UCenter => (UMin + UMax) / 2;
        public double VCenter => (VMin + VMax) / 2;

        public object ToJson() => new
        {
            uMin = Math.Round(UMin, 1),
            uMax = Math.Round(UMax, 1),
            vMin = Math.Round(VMin, 1),
            vMax = Math.Round(VMax, 1)
        };
    }
}
