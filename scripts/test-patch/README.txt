Revit MCP - test build
======================

This is a TEST build of the Revit MCP tools, not a release. It goes over the
Revit MCP plugin already installed on this PC and can be removed again.

Install
  1. Close Revit.
  2. Right-click Apply-TestPatch.ps1 > Run with PowerShell.
     (If Windows blocks it: open PowerShell in this folder and run
      powershell -ExecutionPolicy Bypass -File .\Apply-TestPatch.ps1 )
  3. Close your AI app (Claude, Codex, ...) completely and open it again.
  4. Start Revit and switch MCP On.

Remove
  Close Revit, run Restore-TestPatch.ps1 the same way, restart the AI app.

Testing
  Work in a COPY of a project, never in a live model.
  The test plan is in REBAR-test-plan.md in this folder (section "Test plan").
  For every failure note: the tool, what you asked, the message that came
  back, and whether Revit showed a dialog.
