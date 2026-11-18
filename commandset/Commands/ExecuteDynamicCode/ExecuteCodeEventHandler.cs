using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// External event handler for code execution
    /// </summary>
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public const string TransactionModeAuto = "auto";
        public const string TransactionModeNone = "none";

        // Code execution parameters
        private string _generatedCode;
        private object[] _executionParameters;
        private string _transactionMode = TransactionModeAuto;

        // Execution result info
        public ExecutionResultInfo ResultInfo { get; private set; }

        // State synchronization
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        // Set code and parameters for execution
        public void SetExecutionParameters(string code, object[] parameters = null, string transactionMode = TransactionModeAuto)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            _transactionMode = transactionMode ?? TransactionModeAuto;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        // Wait for completion - IWaitableExternalEventHandler implementation
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                ResultInfo = new ExecutionResultInfo();

                if (_transactionMode == TransactionModeNone)
                {
                    // Let user code manage its own transactions
                    var result = CompileAndExecuteCode(
                        code: _generatedCode,
                        doc: doc,
                        parameters: _executionParameters
                    );

                    ResultInfo.Success = true;
                    ResultInfo.Result = JsonConvert.SerializeObject(result);
                }
                else
                {
                    // Default: wrap in a transaction
                    using (var transaction = new Transaction(doc, "Execute AI Code"))
                    {
                        transaction.Start();

                        var result = CompileAndExecuteCode(
                            code: _generatedCode,
                            doc: doc,
                            parameters: _executionParameters
                        );

                        transaction.Commit();

                        ResultInfo.Success = true;
                        ResultInfo.Result = JsonConvert.SerializeObject(result);
                    }
                }
            }
            catch (Exception ex)
            {
                ResultInfo.Success = false;
                ResultInfo.ErrorMessage = $"Execution failed: {ex.Message}";
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private object CompileAndExecuteCode(string code, Document doc, object[] parameters)
        {
            // Wrap code with a standardized entry point
            var wrappedCode = $@"
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;

namespace AIGeneratedCode
{{
    public static class CodeExecutor
    {{
        public static object Execute(Document document, object[] parameters)
        {{
            // User code entry point
            {code}
        }}
    }}
}}";

            var syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);

            // Reference every assembly Revit has loaded, except ones the compiler cannot read.
            // Third-party add-ins (e.g. Naviate's CheckMulti.dll / license.dll) can ship
            // protected or obfuscated DLLs; one unreadable file used to break every script.
            var references = BuildReferences();

            // Compile code
            var compilation = CSharpCompilation.Create(
                "AIGeneratedCode",
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);

                // Safety net: if the compiler still says some reference file cannot be
                // opened (CS0009), drop those files and compile once more.
                if (!result.Success)
                {
                    var unreadable = result.Diagnostics
                        .Where(d => d.Id == "CS0009" && d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.GetMessage())
                        .ToList();
                    if (unreadable.Count > 0)
                    {
                        var kept = references
                            .Where(r => !(r is PortableExecutableReference pe) ||
                                        string.IsNullOrEmpty(pe.FilePath) ||
                                        !unreadable.Any(m => m.IndexOf(pe.FilePath, StringComparison.OrdinalIgnoreCase) >= 0))
                            .ToList();
                        if (kept.Count < references.Count)
                        {
                            compilation = compilation.WithReferences(kept);
                            ms.SetLength(0);
                            result = compilation.Emit(ms);
                        }
                    }
                }

                // Handle compilation result
                if (!result.Success)
                {
                    var errors = string.Join("\n", result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => $"Line {d.Location.GetLineSpan().StartLinePosition.Line}: {d.GetMessage()}"));
                    throw new Exception($"Code compilation error:\n{errors}");
                }

                // Invoke execution method via reflection
                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());
                var executorType = assembly.GetType("AIGeneratedCode.CodeExecutor");
                var executeMethod = executorType.GetMethod("Execute");

                return executeMethod.Invoke(null, new object[] { doc, parameters });
            }
        }

        /// <summary>
        /// One reference per loaded assembly (first one wins per simple name, because add-ins
        /// like BIM360 load duplicates). Skips dynamic/in-memory assemblies, files that no
        /// longer exist, and files that are not readable .NET assemblies.
        /// </summary>
        private static List<MetadataReference> BuildReferences()
        {
            var references = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                string location;
                try
                {
                    if (assembly.IsDynamic) continue;
                    location = assembly.Location;
                    name = assembly.GetName().Name;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(name)) continue;
                if (seen.Contains(name)) continue;
                if (!IsReadableAssembly(location)) continue;

                try
                {
                    references.Add(MetadataReference.CreateFromFile(location));
                    seen.Add(name);
                }
                catch
                {
                    // Locked, deleted or unreadable: leave it out rather than fail the script.
                }
            }

            return references;
        }

        private static bool IsReadableAssembly(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var peReader = new System.Reflection.PortableExecutable.PEReader(stream))
                {
                    if (!peReader.HasMetadata) return false;
                    var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(peReader);
                    if (!reader.IsAssembly) return false;
                    reader.GetAssemblyDefinition();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public string GetName()
        {
            return "Execute AI Code";
        }
    }

    // Execution result data structure
    public class ExecutionResultInfo
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("result")]
        public string Result { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
