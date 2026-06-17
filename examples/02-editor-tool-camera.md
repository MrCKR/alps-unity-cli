# Example 02: Editor Tool — Camera Screenshot

This example demonstrates using `tools run` to list cameras and capture a screenshot from a specific camera.

## Context

- **Target**: editor (default)
- **Tools**: `camera.list`, `camera.info`, `camera.screenshot`
- **Availability scope**: editorSession

## Step 1: Discover camera tools

```powershell
unity_cli tools search camera
```

## Step 2: Describe the screenshot tool

```powershell
unity_cli tools describe camera.screenshot
```

Review the `inputSchema` — it requires a `target` (camera name) and supports `--output`, `--width`, `--height`, `--format`, `--overwrite` flags.

## Step 3: List cameras

```powershell
unity_cli tools run camera.list
```

Response contains available cameras:

```json
{
  "ok": true,
  "data": {
    "cameras": [
      { "name": "Main Camera", "enabled": true }
    ]
  }
}
```

## Step 4: Capture a screenshot

```powershell
unity_cli tools run camera.screenshot --input "{\"target\":\"Main Camera\",\"output\":\"Artifacts/screenshot.png\",\"overwrite\":true}"
```

Or use an input file:

```powershell
unity_cli tools run camera.screenshot --input-file screenshot-input.json
```

Where `screenshot-input.json` contains:

```json
{
  "target": "Main Camera",
  "output": "Artifacts/screenshot.png",
  "overwrite": true
}
```

## Step 5: Verify

Confirm the output file exists and the response envelope has `ok: true`:

```powershell
dir Artifacts\screenshot.png
```

## Notes

- `camera.screenshot` is a read-write tool (`destructiveHint: false` but produces artifacts).
- Use `--overwrite` to replace existing output files without error.
- The `target` parameter refers to the camera name, not the execution target (which is always `editor` for this tool).
