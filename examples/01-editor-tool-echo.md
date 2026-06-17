# Example 01: Editor Tool — Echo

This example demonstrates running a built-in **editor-domain** tool via `tools run`.

## Context

- **Target**: editor (default)
- **Tool**: `editor.console` — reads recent Unity Editor console log entries
- **Availability scope**: editorSession (always available when Bridge is connected)

## Step 1: Discover the tool

```powershell
unity_cli tools search console
```

Expected output includes:

```json
{
  "ok": true,
  "command": "tools.search",
  "keyword": "console",
  "count": 1,
  "tools": [
    {
      "name": "editor.console",
      "title": "Editor Console Logs",
      "group": "editor",
      "readOnlyHint": true,
      "destructiveHint": false,
      "target": "editor",
      "availabilityScope": "editorSession",
      "cliCommand": "editor console"
    }
  ]
}
```

## Step 2: Describe the tool

Get full metadata including input schema and examples:

```powershell
unity_cli tools describe editor.console
```

Expected output includes `inputSchema`, `readOnlyHint`, `target: "editor"`, and `availabilityScope: "editorSession"`.

## Step 3: Run the tool

```powershell
unity_cli tools run editor.console
```

The tool runs through the Bridge in editor mode. The response envelope contains:

```json
{
  "ok": true,
  "command": "tools.run",
  "requestId": "...",
  "data": {
    "entries": [
      { "message": "...", "level": "Log", "count": 1 }
    ]
  }
}
```

## Step 4: Verify

Check that the response envelope has `ok: true` and contains a non-empty `entries` array.

## Notes

- Editor-domain tools (target=editor) are available whenever the Bridge is connected, regardless of Play Mode state.
- No `--runtime` flag needed — the default target is `editor`.
- The tool name `editor.console` is the canonical name, not a CLI command like `editor console`.
