/**
 * Which AI app is on the other end of this MCP session (Claude Desktop,
 * Codex, Antigravity, ...). The MCP `initialize` handshake carries the
 * client's name; index.ts installs a provider that reads it from the SDK.
 * Every JSON-RPC request forwarded to Revit carries it as `client`, so the
 * plugin's usage counter can say which app made the call.
 */
let provider: () => string | undefined = () => undefined;

export function setClientNameProvider(fn: () => string | undefined): void {
  provider = fn;
}

export function getClientName(): string | undefined {
  try {
    return provider();
  } catch {
    return undefined;
  }
}
