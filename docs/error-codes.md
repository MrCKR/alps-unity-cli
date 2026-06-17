# Error Codes and nextActions

> **Structured error taxonomy for recovery and retry**

## The Error Envelope

Every CLI command that fails returns a standardized error envelope (see [../schemas/error-code.schema.json](../schemas/error-code.schema.json) and [../schemas/response-envelope.schema.json](../schemas/response-envelope.schema.json)):

```json
{
  "ok": false,
  "requestId": "req_01234567-89ab-cdef",
  "error": {
    "code": "E_UNITY_BUSY",
    "message": "Unity Editor is busy (compiling or executing a long operation).",
    "details": { "timeoutMs": 60000, "bridgeEndpoint": "http://127.0.0.1:47392" },
    "next_actions": [
      "Retry the same command after Unity finishes the current operation.",
      "Check editor state with unity_cli editor state.",
      "Increase timeout with --timeout-ms."
    ],
    "transient": true,
    "retry_after_ms": 5000
  }
}
```

## Error Code Fields

| Field | Type | Meaning |
|-------|------|---------|
| `code` | string | Machine-readable error code from the taxonomy (e.g. `E_UNITY_BUSY`, `E_TARGET_AMBIGUOUS`) |
| `message` | string | Human-readable error description |
| `details` | object | Additional structured context (e.g. `jobId`, `timeoutMs`, `targets`) |
| `next_actions` | string[] | Suggested recovery actions the caller can take |
| `transient` | boolean | `true` if the same request may succeed after retry |
| `retry_after_ms` | integer | Suggested minimum retry delay for transient errors; `0` = no specific recommendation |
| `requestId` | string | Echoed from the original request for audit correlation |

## Error Code Taxonomy

### Target and Instance Errors

| Code | Meaning | Transient? | nextActions |
|------|---------|------------|-------------|
| `E_TARGET_NOT_FOUND` | No Unity target matches the selector | No | Run `instances list`; use `target set` |
| `E_TARGET_AMBIGUOUS` | Multiple ready targets match | No | Pass `--target <id|alias>`; run `target set` |
| `E_TARGET_STALE` | Target heartbeat expired | Yes | Reopen Unity Editor; run `instances prune` |
| `E_TARGET_UNREACHABLE` | Target `/health` not responding | Yes | Check Editor process; run `instances prune` |
| `E_TARGET_ALIAS_CONFLICT` | Alias already points to another instance | No | Run `target alias unset <alias>` first, or use `--force` |
| `BRIDGE_CONNECTION_FAILED` | Cannot connect to Bridge endpoint | Yes | Check `--bridge-endpoint`; ensure Editor is running |
| `E_UNITY_BUSY` | Unity is compiling or executing | Yes | Retry after delay; check `editor state` |
| `E_UNITY_COMPILING` | Domain reload in progress | Yes | Wait for compilation to finish |

### Scene and Object Errors

| Code | Meaning | Transient? | nextActions |
|------|---------|------------|-------------|
| `E_SCENE_VIEW_NOT_AVAILABLE` | Scene view not available for screenshot | No | Use `camera screenshot` instead |
| `E_TARGET_AMBIGUOUS` (domain) | Multiple objects match the selector | No | Use hierarchy path or instance ID |
| `E_TARGET_NOT_FOUND` (domain) | Specified object not found | No | Use `go find` or `scene hierarchy` |
| `E_ANIMATOR_NOT_FOUND` | No Animator on target object | No | Check `go inspect` for component list |

### Asset Errors

| Code | Meaning | Transient? | nextActions |
|------|---------|------------|-------------|
| `E_ASSET_PATH_REQUIRED` | Asset path argument missing | No | Provide `Assets/...` path |
| `E_ASSET_NOT_FOUND` | Asset does not exist at path | No | Use `asset search` to find correct path |
| `E_ASSET_OUTPUT_EXISTS` | Output path already exists | No | Add `--overwrite` or choose different path |
| `E_ASSET_DELETE_FAILED` | AssetDatabase delete failed | No | Check asset is not locked; use `asset info` |

### Package Errors

| Code | Meaning | Transient? | nextActions |
|------|---------|------------|-------------|
| `E_SHADER_NOT_FOUND` | Shader asset not found | No | Use `asset search --type Shader` |
| `E_VFX_PACKAGE_MISSING` | Visual Effect Graph not installed | No | Install `com.unity.visualeffectgraph` |
| `E_PROBUILDER_PACKAGE_NOT_FOUND` | ProBuilder not installed | No | Install `com.unity.probuilder` |

### Execution Errors

| Code | Meaning | Transient? | nextActions |
|------|---------|------------|-------------|
| `E_CODE_SOURCE_INVALID` | Invalid code source specification | No | Use `--file`, `--stdin`, or `--code` (exactly one) |
| `E_CODE_COMPILE_FAILED` | C# compilation error in runtime code | No | Check `compile.diagnostics` in response |
| `E_CODE_RUNTIME_EXCEPTION` | Runtime exception during code execution | No | Review `runtime.stderr` in response |
| `E_EXECUTION_TIMEOUT` | Execution exceeded timeout | No | Increase `--timeout-ms` or simplify the code |
| `E_REFERENCE_RESOLUTION_FAILED` | Assembly reference not found | No | Check `--refs`; ensure referenced assemblies are loaded |

### Security and Confirmation Errors

| Code | Meaning | Transient? | nextActions |
|------|---------|------------|-------------|
| `E_DESTRUCTIVE_CONFIRMATION_REQUIRED` | Destructive command needs `--yes` | No | Add `--yes` to confirm; use `--dry-run` for preview if supported |
| `E_DRY_RUN_UNSUPPORTED` | `--dry-run` not supported for this command | No | Remove `--dry-run`; use `tools describe` to check `supportsDryRun` |
| `E_TOOL_FINGERPRINT_MISMATCH` | Project tool descriptor/handler fingerprint changed | No | Review code changes; use `--fingerprint-mode warn` for intentional changes |
| `BATCH_PARALLEL_CONFLICT` | Batch step declares unsafe parallelism | No | Reduce `--parallel` or mark steps as `parallel-safe` |

### IO and Path Errors

| Code | Meaning | Transient? | nextActions |
|------|---------|------------|-------------|
| `E_OUTPUT_EXISTS` | Output file already exists | No | Add `--overwrite` |
| `E_OUTPUT_PATH_REQUIRED` | Output path argument missing | No | Provide `--output <path>` |
| `E_IO_ERROR` | File system IO error | No | Check path validity and permissions |
| `E_CAMERA_NOT_FOUND` | Camera target not found | No | Use `camera list` to find camera names |

## How to Use nextActions as an AI Agent

1. **Read the `code` first.** It tells you the category of the problem.
2. **Check `transient`.** If `true`, retry after `retry_after_ms` (or a reasonable delay if `0`). If `false`, the error requires a different action.
3. **Follow `next_actions`.** Each suggestion is a concrete CLI command or action you can take.
4. **Inspect `details`.** Structured context like `targets`, `timeoutMs`, `diagnostics` provides additional information for decision-making.
5. **Never guess.** If `next_actions` suggests running `instances list`, run `instances list` — don't assume you know which instances are available.

## Cross-Reference

- Target errors: [target-and-instance](./target-and-instance.md)
- Availability differences: [availability-differences](./availability-differences.md)
- Risk and side effects: [risk-and-side-effects](./risk-and-side-effects.md)
- Error schema: [../schemas/error-code.schema.json](../schemas/error-code.schema.json)
- Response envelope schema: [../schemas/response-envelope.schema.json](../schemas/response-envelope.schema.json)
