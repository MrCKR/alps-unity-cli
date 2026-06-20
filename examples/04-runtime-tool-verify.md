# Example 04: Runtime Tool — Execute and Verify Runtime Code

This example demonstrates using `tools run` with a runtime tool that executes code within a Play Mode session and verifying the result.

## Context

- **Target**: runtime
- **Tool**: `RuntimeWaitTool` (a sample project tool that waits for a duration and returns results)
- **Availability scope**: playModeSession

## Step 1: Enter Play Mode

```powershell
unity_cli tools run editor.play
```

## Step 2: Discover runtime tools

```powershell
unity_cli tools group list custom --include-custom
```

Filter for tools with `target: "runtime"`.

## Step 3: Run the runtime wait tool

```powershell
unity_cli tools run sample.wait --input "{\"durationMs\":500,\"message\":\"test complete\"}" --timeout-ms 5000
```

The `--timeout-ms` flag ensures the CLI waits long enough for the runtime tool to complete within the Play Mode session.

## Step 4: Verify the result

Check the response envelope:

```json
{
  "ok": true,
  "data": {
    "completed": true,
    "message": "test complete",
    "elapsedMs": 502,
    "playModeSessionId": "..."
  }
}
```

Verification steps:
1. `ok` is `true`
2. `completed` is `true`
3. `message` matches the input
4. `elapsedMs` is approximately the requested `durationMs`

## Step 5: Exit Play Mode

```powershell
unity_cli tools run editor.stop
```

## Error Handling

If you attempt to run a runtime tool when Unity is not in Play Mode:

```json
{
  "ok": false,
  "error": {
    "code": "E_TOOL_NOT_FOUND_FOR_TARGET",
    "message": "Tool not found: sample.wait.",
    "next_actions": [
      "Run unity_cli tools group list custom --include-custom to see available tools.",
      "Run unity_cli tools search <keyword> to find related tools."
    ]
  }
}
```

This indicates the tool exists in metadata but is not currently available because the runtime session is not active.

## Notes

- Runtime tools may have a `playModeSessionId` in their response — this identifies the Play Mode session in which the tool executed.
- Use `--timeout-ms` for runtime tools that may take longer than the default timeout.
- The `fingerprint-mode` flag (`warn` or `block`) can guard against tool schema changes between sessions.
