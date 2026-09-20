import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerTools } from "./tools/register.js";
import { getDatabase } from "./database/db.js";
import { createRequire } from "module";
import { setClientNameProvider } from "./utils/ClientInfo.js";

const require = createRequire(import.meta.url);
const { version } = require("../package.json");

// Create server instance
const server = new McpServer({
  name: "mcp-server-for-revit",
  version,
});

// Tag forwarded commands with the connected AI app's name (known after the
// MCP initialize handshake, hence read lazily on every call).
setClientNameProvider(() => server.server.getClientVersion()?.name);

// Start server
async function main() {
  // Initialize database (sql.js is async)
  await getDatabase();

  // Register tools
  await registerTools(server);

  // Connect to transport layer
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("Revit MCP Server started successfully");
}

main().catch((error) => {
  console.error("Error starting Revit MCP Server:", error);
  process.exit(1);
});
