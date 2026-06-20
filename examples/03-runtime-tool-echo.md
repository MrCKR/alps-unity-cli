# Example 03: Runtime Tool — Echo in Play Mode

This example demonstrates running a **runtime-domain** tool via `tools run`. Runtime tools only execute when Unity is in Play Mode.

## Context

- **Target**: runtime
- **Tool**: `SampleRuntimeEchoTool` (a sample project tool with `target=runtime`)
- **Availability scope**: playModeSession (only available during Play Mode)

## Prerequisites

Before running runtime tools, Unity must be in Play Mode:

```powershell
unity_cli tools run editor.play
```

Or enter Play Mode manually in the Unity Editor.

## Step 1: Verify the runtime tool is available

Runtime tools are only listed when Unity is in Play Mode:

```powershell
unity_cli tools group list custom --include-custom
```

Look for tools with `target: "runtime"` and `availabilityScope: "playModeSession"`. If Unity is not in Play Mode, `available` will be `false`.

## Step 2: Describe the runtime tool

```powershell
unity_cli tools describe sample.echo --include-custom
```

The response includes:

```json
{
  "name": "sample.echo",
  "target": "runtime",
  "availabilityScope": "playModeSession",
  "available": true,
  "handler": "bridge-runtime",
  "supportStatus": "live-bridge-runtime"
}
```

Key fields:
- `target: "runtime"` — this tool routes through the RuntimeExecutor
- `availabilityScope: "playModeSession"` — only available during Play Mode
- `available: true` — the tool is currently reachable

## Step 3: Run the runtime tool

```powershell
unity_cli tools run sample.echo --input "{\"message\":\"hello from runtime\"}"
```

The CLI detects `target=runtime` and routes through `runtime-tool.run` instead of `project-tool.run`. The RuntimeExecutor executes the tool within the Play Mode session.

Response:

```json
{
  "ok": true,
  "data": {
    "echo": "hello from runtime",
    "playModeSessionId": "..."
  }
}
```

## Step 4: Verify

Confirm the response has `ok: true` and contains the echoed message matching the input.

## Step 5: Exit Play Mode

```powershell
unity_cli tools run editor.stop
```

After exiting Play Mode, runtime tools become unavailable:

```powershell
unity_cli tools describe sample.echo --include-custom
```

Now shows `available: false` (or the tool may not appear at all).

## Notes

- Runtime tools use `handler: "bridge-runtime"` and `supportStatus: "live-bridge-runtime"`.
- The `--runtime` flag is only needed when a tool has `target: "both"` and you want to force runtime execution.
- If you try to run a runtime tool outside Play Mode, you get `E_TOOL_NOT_FOUND_FOR_TARGET` or a Play Mode guard error.
- Never use a standalone `runtime` command group — runtime capabilities are always accessed through `tools run`.
