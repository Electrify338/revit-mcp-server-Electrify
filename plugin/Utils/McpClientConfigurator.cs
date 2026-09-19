using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Helpers;

namespace revit_mcp_plugin.Utils
{
    /// <summary>
    /// Registers the Revit MCP server with every AI client found on this
    /// machine, so the user never runs a script or edits a config file:
    /// Claude Desktop, Claude Code, OpenAI Codex, Google Antigravity, Gemini
    /// CLI, Cursor, VS Code and Windsurf. Runs at every Revit start; only
    /// writes when an entry is missing or points at the wrong files, backs up
    /// before writing, validates after, and never throws.
    /// </summary>
    public static class McpClientConfigurator
    {
        private const string Tag = "McpClientConfigurator";

        /// <summary>Name of the server entry written into every client.</summary>
        public const string ServerName = "revit-mcp";

        internal enum Format { JsonMcpServers, JsonServers, Toml }

        /// <summary>One AI client that can talk to the MCP server.</summary>
        public sealed class Client
        {
            public string Name;
            public string ConfigPath;
            /// <summary>
            /// True when the client is (or was) installed on this machine.
            /// Clients that are not installed are left alone so that no
            /// stray config files appear for programs the user never had.
            /// </summary>
            public bool Detected;
            internal Format Layout;
        }

        /// <summary>Outcome for one client, for logs and the status panel.</summary>
        public sealed class Result
        {
            public string Name;
            public string ConfigPath;
            public bool Detected;
            public bool Configured;   // entry present and correct after this run
            public bool Changed;      // this run wrote the file
            public string Error;
        }

        // -----------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------

        /// <summary>
        /// Ensures every detected client has a correct revit-mcp entry.
        /// Safe to call at every startup. Shows one dialog, only when a file
        /// was actually changed, telling the user which apps to restart.
        /// </summary>
        public static List<Result> EnsureConfigured()
        {
            var results = new List<Result>();
            try
            {
                McpLogger.Info(Tag, "Checking AI client configurations");

                string serverPath = GetServerPath();
                string nodePath = GetNodePath();
                if (serverPath == null || nodePath == null)
                {
                    McpLogger.Warn(Tag,
                        $"Auto-config skipped: server={serverPath ?? "null"}, node={nodePath ?? "null"}");
                    return results;
                }

                foreach (Client client in KnownClients())
                {
                    var r = new Result
                    {
                        Name = client.Name,
                        ConfigPath = client.ConfigPath,
                        Detected = client.Detected
                    };
                    results.Add(r);
                    if (!client.Detected)
                    {
                        McpLogger.Info(Tag, $"{client.Name}: not installed, skipped");
                        continue;
                    }
                    try
                    {
                        r.Changed = Configure(client, nodePath, serverPath);
                        r.Configured = true;
                        McpLogger.Info(Tag, r.Changed
                            ? $"{client.Name}: configured ({client.ConfigPath})"
                            : $"{client.Name}: already correct");
                    }
                    catch (Exception ex)
                    {
                        r.Error = ex.Message;
                        McpLogger.Error(Tag, $"{client.Name}: could not configure {client.ConfigPath}", ex);
                    }
                }

                Notify(results);
            }
            catch (Exception ex)
            {
                // Never crash Revit because of auto-config
                McpLogger.Error(Tag, "AI client auto-config failed (non-fatal)", ex);
            }
            return results;
        }

        /// <summary>
        /// Reports, without writing anything, which clients are installed and
        /// whether each one currently has a revit-mcp entry.
        /// </summary>
        public static List<Result> Inspect()
        {
            var results = new List<Result>();
            foreach (Client client in KnownClients())
            {
                var r = new Result
                {
                    Name = client.Name,
                    ConfigPath = client.ConfigPath,
                    Detected = client.Detected
                };
                try { r.Configured = HasEntry(client); }
                catch (Exception ex) { r.Error = ex.Message; }
                results.Add(r);
            }
            return results;
        }

        /// <summary>
        /// Every client this plugin knows how to configure, with its config
        /// file location on this machine and whether it looks installed.
        /// </summary>
        public static List<Client> KnownClients()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var list = new List<Client>();

            // Claude Desktop: written even when not yet installed (the folder
            // is created) so the tools are there the day it is installed.
            string claudeDir = GetClaudeDesktopDir();
            list.Add(new Client
            {
                Name = "Claude Desktop",
                ConfigPath = Path.Combine(claudeDir, "claude_desktop_config.json"),
                Detected = true,
                Layout = Format.JsonMcpServers
            });

            // Claude Code: user-scope servers live at the top level of ~/.claude.json.
            string claudeCodeFile = Path.Combine(home, ".claude.json");
            list.Add(new Client
            {
                Name = "Claude Code",
                ConfigPath = claudeCodeFile,
                Detected = File.Exists(claudeCodeFile) || Directory.Exists(Path.Combine(home, ".claude")),
                Layout = Format.JsonMcpServers
            });

            // OpenAI Codex CLI / IDE extension: ~/.codex/config.toml, [mcp_servers.<name>].
            string codexDir = Path.Combine(home, ".codex");
            list.Add(new Client
            {
                Name = "Codex",
                ConfigPath = Path.Combine(codexDir, "config.toml"),
                Detected = Directory.Exists(codexDir),
                Layout = Format.Toml
            });

            // Google Antigravity 2.0 (IDE + CLI) shares ~/.gemini/config/mcp_config.json;
            // the first Antigravity release used ~/.gemini/antigravity/mcp_config.json.
            string geminiDir = Path.Combine(home, ".gemini");
            string antigravityConfigDir = Path.Combine(geminiDir, "config");
            list.Add(new Client
            {
                Name = "Antigravity",
                ConfigPath = Path.Combine(antigravityConfigDir, "mcp_config.json"),
                Detected = Directory.Exists(antigravityConfigDir),
                Layout = Format.JsonMcpServers
            });
            string antigravityLegacyDir = Path.Combine(geminiDir, "antigravity");
            list.Add(new Client
            {
                Name = "Antigravity (1.x)",
                ConfigPath = Path.Combine(antigravityLegacyDir, "mcp_config.json"),
                Detected = Directory.Exists(antigravityLegacyDir),
                Layout = Format.JsonMcpServers
            });

            // Gemini CLI: ~/.gemini/settings.json, "mcpServers".
            string geminiSettings = Path.Combine(geminiDir, "settings.json");
            list.Add(new Client
            {
                Name = "Gemini CLI",
                ConfigPath = geminiSettings,
                Detected = File.Exists(geminiSettings),
                Layout = Format.JsonMcpServers
            });

            // Cursor: ~/.cursor/mcp.json, "mcpServers".
            string cursorDir = Path.Combine(home, ".cursor");
            list.Add(new Client
            {
                Name = "Cursor",
                ConfigPath = Path.Combine(cursorDir, "mcp.json"),
                Detected = Directory.Exists(cursorDir),
                Layout = Format.JsonMcpServers
            });

            // VS Code (Copilot agent mode): %APPDATA%\Code\User\mcp.json, "servers".
            string vsCodeUser = Path.Combine(appData, "Code", "User");
            list.Add(new Client
            {
                Name = "VS Code",
                ConfigPath = Path.Combine(vsCodeUser, "mcp.json"),
                Detected = Directory.Exists(vsCodeUser),
                Layout = Format.JsonServers
            });

            // Windsurf: ~/.codeium/windsurf/mcp_config.json, "mcpServers".
            string windsurfDir = Path.Combine(home, ".codeium", "windsurf");
            list.Add(new Client
            {
                Name = "Windsurf",
                ConfigPath = Path.Combine(windsurfDir, "mcp_config.json"),
                Detected = Directory.Exists(windsurfDir),
                Layout = Format.JsonMcpServers
            });

            return list;
        }

        // -----------------------------------------------------------------
        // Per-client work
        // -----------------------------------------------------------------

        /// <summary>Returns true when the config file was written.</summary>
        private static bool Configure(Client client, string nodePath, string serverPath)
        {
            switch (client.Layout)
            {
                case Format.Toml:
                    return ConfigureToml(client, nodePath, serverPath);
                default:
                    return ConfigureJson(client, nodePath, serverPath);
            }
        }

        private static bool HasEntry(Client client)
        {
            if (!File.Exists(client.ConfigPath)) return false;
            string text = File.ReadAllText(client.ConfigPath);
            if (client.Layout == Format.Toml)
                return FindTomlSection(SplitLines(text), out int _, out int _);

            JObject config = ParseJson(text);
            var servers = config[ServersKey(client.Layout)] as JObject;
            return servers != null && servers[ServerName] != null;
        }

        private static string ServersKey(Format layout) =>
            layout == Format.JsonServers ? "servers" : "mcpServers";

        // ---- JSON clients ------------------------------------------------

        private static bool ConfigureJson(Client client, string nodePath, string serverPath)
        {
            string configPath = client.ConfigPath;
            string key = ServersKey(client.Layout);

            JObject config = ReadJsonOrBackupCorrupt(configPath);

            var servers = config[key] as JObject;
            if (servers == null)
            {
                servers = new JObject();
                config[key] = servers;
            }

            var entry = new JObject
            {
                ["command"] = nodePath,
                ["args"] = new JArray(serverPath)
            };
            if (client.Layout == Format.JsonServers)
                entry["type"] = "stdio";

            var existing = servers[ServerName] as JObject;
            if (existing != null &&
                existing["command"]?.ToString() == nodePath &&
                FirstArg(existing) == serverPath)
            {
                return false;   // already correct, touch nothing
            }

            // Keep anything else the user put on the entry (env, disabled...).
            if (existing != null)
            {
                foreach (JProperty p in existing.Properties())
                    if (entry[p.Name] == null) entry[p.Name] = p.Value;
            }
            servers[ServerName] = entry;

            BackupIfExists(configPath);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            WriteUtf8NoBom(configPath, config.ToString(Formatting.Indented));

            // Validate by re-reading and parsing
            ParseJson(File.ReadAllText(configPath));
            return true;
        }

        /// <summary>
        /// Parses without turning ISO date strings into DateTime values, so a
        /// file like ~/.claude.json (full of timestamps) is written back
        /// exactly as it was read, apart from our own entry.
        /// </summary>
        private static JObject ParseJson(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Decimal;
                return JObject.Load(reader);
            }
        }

        private static string FirstArg(JObject entry)
        {
            var args = entry["args"] as JArray;
            return args != null && args.Count > 0 ? args[0].ToString() : null;
        }

        private static JObject ReadJsonOrBackupCorrupt(string configPath)
        {
            if (!File.Exists(configPath)) return new JObject();

            string existingJson = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(existingJson)) return new JObject();
            try
            {
                return ParseJson(existingJson);
            }
            catch (Exception parseEx)
            {
                string corruptedPath = configPath + ".corrupted." + Stamp() + ".bak";
                File.Copy(configPath, corruptedPath, true);
                McpLogger.Warn(Tag,
                    $"{configPath} was not valid JSON, backed up to {corruptedPath} ({parseEx.Message})");
                return new JObject();
            }
        }

        // ---- TOML (Codex) ------------------------------------------------

        private static bool ConfigureToml(Client client, string nodePath, string serverPath)
        {
            string configPath = client.ConfigPath;
            var lines = File.Exists(configPath)
                ? SplitLines(File.ReadAllText(configPath))
                : new List<string>();

            var block = new List<string>
            {
                "[mcp_servers." + ServerName + "]",
                "command = " + TomlString(nodePath),
                "args = [" + TomlString(serverPath) + "]"
            };

            if (FindTomlSection(lines, out int start, out int end))
            {
                // Already correct? Compare command and first arg inside the section.
                string command = null, arg = null;
                for (int i = start + 1; i < end; i++)
                {
                    Match m = Regex.Match(lines[i], @"^\s*command\s*=\s*""((?:[^""\\]|\\.)*)""");
                    if (m.Success) command = TomlUnescape(m.Groups[1].Value);
                    m = Regex.Match(lines[i], @"^\s*args\s*=\s*\[\s*""((?:[^""\\]|\\.)*)""");
                    if (m.Success) arg = TomlUnescape(m.Groups[1].Value);
                }
                if (command == nodePath && arg == serverPath) return false;

                // Replace the whole section (including any [mcp_servers.revit-mcp.env]).
                lines.RemoveRange(start, end - start);
                lines.InsertRange(start, block);
            }
            else
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.AddRange(block);
            }

            BackupIfExists(configPath);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            WriteUtf8NoBom(configPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            return true;
        }

        /// <summary>
        /// Finds our [mcp_servers.revit-mcp] table. start = header line;
        /// end = first line after the table and its sub-tables (exclusive).
        /// </summary>
        private static bool FindTomlSection(List<string> lines, out int start, out int end)
        {
            start = -1; end = -1;
            var header = new Regex(@"^\s*\[\s*mcp_servers\s*\.\s*(?:""" + ServerName + @"""|" + ServerName + @")\s*\]\s*(#.*)?$");
            var subHeader = new Regex(@"^\s*\[\s*mcp_servers\s*\.\s*(?:""" + ServerName + @"""|" + ServerName + @")\s*\.");
            var anyHeader = new Regex(@"^\s*\[");
            for (int i = 0; i < lines.Count; i++)
            {
                if (!header.IsMatch(lines[i])) continue;
                start = i;
                end = lines.Count;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    if (anyHeader.IsMatch(lines[j]) && !subHeader.IsMatch(lines[j])) { end = j; break; }
                }
                return true;
            }
            return false;
        }

        private static string TomlString(string s) =>
            "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string TomlUnescape(string s) =>
            s.Replace("\\\"", "\"").Replace("\\\\", "\\");

        private static List<string> SplitLines(string text) =>
            text.Replace("\r\n", "\n").Split('\n').ToList();

        // ---- Shared helpers ------------------------------------------------

        private static void BackupIfExists(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                string backupPath = path + "." + Stamp() + ".bak";
                File.Copy(path, backupPath, true);
                McpLogger.Info(Tag, $"Backed up {path} to {backupPath}");
            }
            catch (Exception ex)
            {
                // The update matters more than the backup
                McpLogger.Error(Tag, "Failed to back up " + path, ex);
            }
        }

        private static void WriteUtf8NoBom(string path, string text) =>
            File.WriteAllText(path, text, new UTF8Encoding(false));

        private static string Stamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss");

        private static void Notify(List<Result> results)
        {
            var changed = results.Where(r => r.Changed).Select(r => r.Name).ToList();
            if (changed.Count == 0) return;
            try
            {
                var td = new TaskDialog("Revit MCP Plugin")
                {
                    MainInstruction = "AI apps connected to Revit",
                    MainContent =
                        "The Revit MCP server was registered in: " + string.Join(", ", changed) + ".\n\n" +
                        "Restart those apps to see the Revit tools, then click " +
                        "\"Revit MCP Switch\" on the Add-Ins tab when you want them to reach this model.\n\n" +
                        "(This message only appears when a configuration file was updated.)",
                    CommonButtons = TaskDialogCommonButtons.Ok
                };
                td.Show();
            }
            catch
            {
                // If TaskDialog fails (e.g. during silent startup), ignore
            }
        }

        // -----------------------------------------------------------------
        // Path resolution
        // -----------------------------------------------------------------

        private static string GetServerPath()
        {
            string pluginDir = PathManager.GetAppDataDirectoryPath();
            string serverJs = Path.Combine(
                pluginDir, "Commands", "RevitMCPCommandSet", "server", "build", "index.js");
            return File.Exists(serverJs) ? serverJs : null;
        }

        private static string GetNodePath()
        {
            // 1. Bundled portable node.exe (ships with the Release ZIP)
            string pluginDir = PathManager.GetAppDataDirectoryPath();
            string bundledNode = Path.Combine(
                pluginDir, "Commands", "RevitMCPCommandSet", "server", "runtime", "node.exe");
            if (File.Exists(bundledNode))
                return bundledNode;

            // 2. System node in PATH
            return FindInPath("node.exe");
        }

        private static string FindInPath(string executable)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv)) return null;

            foreach (string dir in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string fullPath = Path.Combine(dir.Trim(), executable);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
                catch
                {
                    // Invalid path entry — skip
                }
            }
            return null;
        }

        private static string GetClaudeDesktopDir()
        {
            // Standard install
            string standard = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
            if (Directory.Exists(standard))
                return standard;

            // MSIX (Microsoft Store) install
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string packagesDir = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packagesDir))
            {
                try
                {
                    string claudePkg = Directory.GetDirectories(packagesDir, "Claude_*").FirstOrDefault();
                    if (claudePkg != null)
                    {
                        string msixDir = Path.Combine(claudePkg, "LocalCache", "Roaming", "Claude");
                        if (Directory.Exists(msixDir))
                            return msixDir;
                    }
                }
                catch
                {
                    // Permission issue reading Packages — skip MSIX check
                }
            }

            // Claude Desktop not installed — return standard path anyway so the
            // config is ready when the user eventually installs it.
            return standard;
        }
    }
}
