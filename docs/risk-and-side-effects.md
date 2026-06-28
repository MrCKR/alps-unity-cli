# Risk and Side Effects

> **How the unified tool descriptor expresses risk, confirmation, and preview**

## The Risk Model

Every tool carries risk hints in its descriptor. These are the only way to assess risk — there is no separate risk registry, no independent safety gate, and no external confirmation prompt outside the unified `tools describe` metadata.

| Descriptor Field | Meaning | How AI Should Handle |
|-----------------|---------|---------------------|
| `destructiveHint: true` | Tool may irreversibly modify or delete data | Must confirm with `--yes`; consider `--dry-run` first |
| `supportsDryRun: true` | Tool accepts `--dry-run` for safe preview | Always preview with `--dry-run` before `--yes` |
| `longRunning: true` | Tool may take seconds or minutes | Check `cancellationSemantics`; consider `--timeout-ms` |
| `permissions` | Permission tokens required (e.g. `["executes_code"]`) | Ensure the context allows the required permissions |
| `cancellationSemantics` | How cancellation works for this tool | Use `job cancel` or cooperative cancel as described |

## Destructive Commands

The CLI enforces a confirmation gate for all tools with `destructiveHint: true`. Current built-in metadata declares destructive commands across all domains. Key destructive commands include:

| Command | Title | Supports `--dry-run`? |
|---------|-------|----------------------|
| `tools run asset.delete` | Delete Asset | ✅ |
| `tools run scene.save` | Save Scene | ✅ |
| `tools run code.exec` | Execute Live Code | ✅ |
| `tools run scriptable-object.create` | Create ScriptableObject Asset | ✅ |
| `tools run scriptable-object.modify` | Modify ScriptableObject Asset | ✅ |
| `tools run texture.modify` | Modify Texture Asset | ✅ |
| `tools run texture.delete` | Delete Texture Asset | ✅ |
| `tools run shader.delete` | Delete Shader Asset | ✅ |
| `tools run object.modify` | Modify Object (deep patch) | ✅ |
| `tools run reflection.method.call` | Call Method via Reflection | security fingerprint |
| `tools run graphics.bake-clear` | Clear Baked Lighting | ❌ |
| `tools run graphics.bake-start` | Start Light Bake | ❌ |
| `tools run graphics.bake-reflection-probe` | Bake Reflection Probe | ❌ |
| `tools run controller.remove-layer` | Remove Animator Controller Layer | ❌ |
| `tools run editor.menu` | Execute Editor Menu | ❌ |
| `tools run package.embed` | Embed Package | ✅ |
| `tools run package.install` | Package Install | ❌ |
| `tools run package.remove` | Package Remove | ❌ |
| `batch run` | Batch Run | ✅ (preflight) |
| `jobs cancel` | Cancel Job | ❌ |

The `reflection.method.call` tool is classified `riskLevel: critical`. It performs no scope restriction on the invoked method (public/private/static/instance, any namespace/assembly, Editor/Runtime). It requires a `security` confirmation object with `confirmed: true` and a `destructiveFingerprint`, and returns a structured audit including target method preview, parameters, result serialization, and any exception.

### Confirmation Flow

1. **Call with `--dry-run`** (if `supportsDryRun: true`) — preview effects without execution.
2. **Call with `--yes`** — execute the command with explicit confirmation.
3. **Without `--yes` or `--dry-run`** — the CLI returns `E_DESTRUCTIVE_CONFIRMATION_REQUIRED` with `next_actions` suggesting how to proceed.

In a TTY environment, the CLI also offers an interactive confirmation prompt. In non-TTY or CI environments, only `--yes` or `--dry-run` bypass the gate.

### Destructive Security Proof

When a destructive command is confirmed, the response carries a `destructiveSecurityProof`:

```json
{
  "destructive": true,
  "confirmed": true,
  "confirmedBy": "--yes",
  "destructiveFingerprint": "destructive-safety-v1"
}
```

This proof is propagated through the request chain for audit.

## Planned Effects Preview

Mutating tools (both destructive and non-destructive) often return a `plannedEffects` summary in their response. This tells you what the tool intends to change before or after execution:

```json
{
  "plannedEffects": {
    "assetCreated": ["Assets/Materials/New.mat"],
    "assetModified": ["Assets/Scenes/Main.unity"],
    "requiresReimport": true
  }
}
```

For tools with `supportsDryRun`, `--dry-run` returns the same `plannedEffects` without executing the actual mutation.

## Project Tool Fingerprint

Project tools registered by `[UnityCliTool]` C# attributes carry a `fingerprint` field (SHA-256 of handler code + descriptor). This fingerprint is checked at execution time:

| `--fingerprint-mode` | Behavior on mismatch |
|---------------------|---------------------|
| `block` (default) | Returns `E_TOOL_FINGERPRINT_MISMATCH`; execution blocked |
| `warn` | Logs warning; execution proceeds |
| (environment `UNITY_CLI_PROJECT_TOOL_FINGERPRINT_MODE=warn`) | Same as `--fingerprint-mode warn` |

**Use `warn` only for intentional, audited code changes.** The default `block` mode protects against tampering or accidental descriptor drift.

The `doctor` command reports fingerprint status for all project tools. Use it to verify fingerprint consistency before and after code changes.

## How to Assess Risk as an AI Agent

1. **Always `tools describe` first.** Read `destructiveHint`, `supportsDryRun`, `target`, `availabilityScope`, and `available`.
2. **For destructive tools**: Use `--dry-run` if supported; otherwise, explain the planned effects to the user before calling with `--yes`.
3. **For runtime tools**: Verify `available: true` and Play Mode state before calling `target: "runtime"` tools.
4. **For long-running tools**: Set appropriate `--timeout-ms`; understand `cancellationSemantics` before starting.
5. **For project tools**: Check fingerprint status with `doctor`; use `--fingerprint-mode block` (default) to ensure integrity.

## Known Limitations

| Limitation | Risk Level | Mitigation |
|-----------|-----------|------------|
| Dry-run is not available for every destructive tool | Medium | All destructive commands require `--yes`; dry-run is supplemental |
| Built-in tools do not use project-tool fingerprints | Low | Built-in commands are protected by contract validation and security policy checks |

## Cross-Reference

- Error codes including `E_DESTRUCTIVE_CONFIRMATION_REQUIRED`, `E_RUNTIME_NOT_READY`, and `E_TOOL_FINGERPRINT_MISMATCH`: [error-codes](./error-codes.md)
- Tool descriptor field coverage: [tool-discovery](./tool-discovery.md) §Descriptor field coverage
- Tool metadata schema: [../schemas/tool-metadata.schema.json](../schemas/tool-metadata.schema.json)
- Security policy source: `src/security-policy.ts` and `src/security.ts`
- AI skill (compact version): [/skills/unity_cli/SKILL.md](../skills/unity_cli/SKILL.md)
