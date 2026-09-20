using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Helpers
{
    /// <summary>
    /// Counts every tool call that reaches Revit, whichever AI app made it.
    /// One JSON line per call goes to logs\usage-yyyy-MM.jsonl next to the
    /// plugin (the source of truth), and, when usage.json names an endpoint,
    /// the same lines are POSTed there in batches from a background thread.
    /// Nothing here ever blocks a command or throws into Revit. Calls made
    /// while the endpoint is unreachable wait in logs\usage-pending.jsonl and
    /// go out later, so a laptop off the network loses nothing.
    ///
    /// usage.json (written by Kemet Addons from its update.json; optional):
    ///   { "url": "https://usage.kemet.local/mcp", "token": "...", "department": "Structure" }
    ///
    /// A record: { ts, user, machine, department, revit, client, tool, ok, ms, project }
    /// Never the prompt, never the model's text, never element data.
    /// </summary>
    public static class UsageTracker
    {
        private const string Tag = "UsageTracker";
        private static readonly object _lock = new object();
        private static string _pluginDir;
        private static string _revitVersion;
        private static Timer _flushTimer;
        private static int _flushing;

        private class Endpoint
        {
            public string Url;
            public string Token;
            public string Department;
        }

        public static void Initialize(string pluginDir, string revitVersion)
        {
            try
            {
                _pluginDir = pluginDir;
                _revitVersion = revitVersion;
                if (_flushTimer == null)
                    _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
            }
            catch (Exception ex)
            {
                McpLogger.Error(Tag, "Initialize failed", ex);
            }
        }

        /// <summary>Path of this month's usage file, or null before Initialize.</summary>
        public static string CurrentFile
        {
            get
            {
                if (string.IsNullOrEmpty(_pluginDir)) return null;
                string month = DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                return Path.Combine(_pluginDir, "logs", "usage-" + month + ".jsonl");
            }
        }

        private static string PendingFile =>
            string.IsNullOrEmpty(_pluginDir) ? null : Path.Combine(_pluginDir, "logs", "usage-pending.jsonl");

        public static void Record(string tool, string client, bool ok, long elapsedMs, string project)
        {
            try
            {
                if (string.IsNullOrEmpty(_pluginDir)) return;
                Endpoint ep = ReadEndpoint();

                var record = new JObject
                {
                    ["ts"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["user"] = Environment.UserName,
                    ["machine"] = Environment.MachineName,
                    ["department"] = ep?.Department ?? "",
                    ["revit"] = _revitVersion ?? "",
                    ["client"] = string.IsNullOrEmpty(client) ? "unknown" : client,
                    ["tool"] = tool ?? "",
                    ["ok"] = ok,
                    ["ms"] = elapsedMs,
                    ["project"] = project ?? ""
                };
                string line = record.ToString(Formatting.None) + Environment.NewLine;

                lock (_lock)
                {
                    Directory.CreateDirectory(Path.Combine(_pluginDir, "logs"));
                    File.AppendAllText(CurrentFile, line, Encoding.UTF8);
                    if (ep != null && !string.IsNullOrWhiteSpace(ep.Url))
                        File.AppendAllText(PendingFile, line, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                McpLogger.Error(Tag, "Record failed", ex);
            }
        }

        /// <summary>Number of calls recorded this month on this machine.</summary>
        public static int CountThisMonth()
        {
            try
            {
                string file = CurrentFile;
                if (file == null || !File.Exists(file)) return 0;
                int n = 0;
                lock (_lock)
                {
                    using (var reader = new StreamReader(file))
                        while (reader.ReadLine() != null) n++;
                }
                return n;
            }
            catch
            {
                return 0;
            }
        }

        // -----------------------------------------------------------------
        // Endpoint upload
        // -----------------------------------------------------------------

        private static void Flush()
        {
            if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
            try
            {
                Endpoint ep = ReadEndpoint();
                string pending = PendingFile;
                if (ep == null || string.IsNullOrWhiteSpace(ep.Url) || pending == null || !File.Exists(pending))
                    return;

                string[] lines;
                lock (_lock)
                {
                    lines = File.ReadAllLines(pending);
                }
                var batch = new List<string>();
                foreach (string l in lines)
                    if (!string.IsNullOrWhiteSpace(l)) batch.Add(l);
                if (batch.Count == 0) return;

                // Send at most 500 per round; the rest goes next minute.
                int take = Math.Min(batch.Count, 500);
                string body = "[" + string.Join(",", batch.GetRange(0, take)) + "]";
                if (!Post(ep, body)) return;

                lock (_lock)
                {
                    // Re-read: calls recorded during the upload must survive.
                    var remaining = new List<string>();
                    string[] now = File.Exists(pending) ? File.ReadAllLines(pending) : new string[0];
                    int skipped = 0;
                    foreach (string l in now)
                    {
                        if (string.IsNullOrWhiteSpace(l)) continue;
                        if (skipped < take) { skipped++; continue; }
                        remaining.Add(l);
                    }
                    File.WriteAllText(pending,
                        remaining.Count == 0 ? "" : string.Join(Environment.NewLine, remaining) + Environment.NewLine,
                        Encoding.UTF8);
                }
                McpLogger.Info(Tag, $"Uploaded {take} usage record(s) to {ep.Url}");
            }
            catch (Exception ex)
            {
                McpLogger.Warn(Tag, "Flush failed (will retry): " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _flushing, 0);
            }
        }

        private static bool Post(Endpoint ep, string body)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(ep.Url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
                request.UserAgent = "revit-mcp-usage";
                if (!string.IsNullOrEmpty(ep.Token))
                    request.Headers["Authorization"] = "Bearer " + ep.Token;

                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using (Stream s = request.GetRequestStream())
                    s.Write(bytes, 0, bytes.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                    return (int)response.StatusCode >= 200 && (int)response.StatusCode < 300;
            }
            catch (Exception ex)
            {
                McpLogger.Warn(Tag, "Upload failed (will retry): " + ex.Message);
                return false;
            }
        }

        private static Endpoint ReadEndpoint()
        {
            try
            {
                string path = Path.Combine(_pluginDir, "usage.json");
                if (!File.Exists(path)) return null;
                JObject o = JObject.Parse(File.ReadAllText(path));
                return new Endpoint
                {
                    Url = o["url"]?.ToString(),
                    Token = o["token"]?.ToString(),
                    Department = o["department"]?.ToString()
                };
            }
            catch (Exception ex)
            {
                McpLogger.Warn(Tag, "usage.json could not be read: " + ex.Message);
                return null;
            }
        }
    }
}
