# Example 05: Full Discovery → Describe → Run → Verify Workflow

This example demonstrates the complete AI usage workflow:
`instances/discovery → select target → tools search → tools describe → tools run → verify`

## Step 1: Discover Unity instances

```powershell
unity_cli instances list
```

Response:

```json
{
  "ok": true,
  "data": {
    "instances": [
      {
        "id": "a1b2c3d4",
        "alias": "main-editor",
        "bridgeEndpoint": "http://127.0.0.1:18080",
        "status": "connected"
      }
    ]
  }
}
```

## Step 2: Select a target

```powershell
unity_cli target set main-editor
```

Or set by ID:

```powershell
unity_cli target set a1b2c3d4
```

Verify:

```powershell
unity_cli target get
```

## Step 3: Search for a tool

```powershell
unity_cli tools search screenshot
```

Response:

```json
{
  "ok": true,
  "command": "tools.search",
  "keyword": "screenshot",
  "count": 2,
  "tools": [
    {
      "name": "camera.screenshot",
      "title": "Camera Screenshot",
      "group": "camera",
      "readOnlyHint": false,
      "destructiveHint": false,
      "target": "editor",
      "availabilityScope": "editorSession"
    },
    {
      "name": "scene.screenshot",
      "title": "Scene Screenshot",
      "group": "scene",
      "readOnlyHint": false,
      "destructiveHint": false,
      "target": "editor",
      "availabilityScope": "editorSession"
    }
  ]
}
```

## Step 4: Describe the tool

```powershell
unity_cli tools describe camera.screenshot
```

Review the full metadata including `inputSchema`, `outputSchema`, `examples`, `nextActions`, `cancellationSemantics`, and `supportStatus`.

## Step 5: Run the tool

```powershell
unity_cli tools run camera.screenshot --input "{\"target\":\"Main Camera\",\"output\":\"Artifacts/verify-screenshot.png\",\"overwrite\":true}"
```

## Step 6: Verify

Check the response envelope for `ok: true`:

```powershell
# Verify the file was written
dir Artifacts\verify-screenshot.png
```

For a runtime tool, the verification also checks `playModeSessionId` and `available: true` in the describe step.

## Complete One-Liner Summary

```
instances list → target set <alias> → tools search <keyword> → tools describe <name> → tools run <name> [--input <json>] → verify ok
```

## Notes

- Always discover and describe before running — this confirms the tool exists and is available for the current target.
- The `target` field in tool metadata (editor/runtime/both) determines the execution path automatically.
- Verification differs by tool type: editor tools verify output artifacts, runtime tools verify session-specific response fields.
