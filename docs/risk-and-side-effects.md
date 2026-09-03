# Risk and Side Effects

> **How the unified tool descriptor expresses risk, effects, and optional preview**

## The Risk Model

Every tool carries risk hints in its descriptor. Public Tool and CLI control-plane invocation is authorization: there is no separate risk gate, confirmation prompt, predefined button list, or intent review.

| Descriptor Field | Meaning | How AI Should Handle |
|-----------------|---------|---------------------|
| `destructiveHint: true` | Tool may irreversibly modify or delete data | Assess the effect; the hint does not block execution |
| `supportsDryRun: true` | Tool accepts `--dry-run` for a safe preview | Use preview when useful; it is optional |
| `permissions` | Permission tokens required (e.g. `["executes_code"]`) | Ensure the context allows the required permissions |
| `target` / `availabilityScope` | Editor target and availability boundary | Execute against the selected open Unity Editor |

## Destructive Commands

`destructiveHint: true` documents a potentially irreversible effect. It does not add an authorization requirement or alter normal execution. Key destructive commands include:

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
| `tools run reflection.method.call` | Call Method via Reflection | ✅ |
| `tools run graphics.bake-clear` | Clear Baked Lighting | ❌ |
| `tools run graphics.bake-start` | Start Light Bake | ❌ |
| `tools run graphics.bake-reflection-probe` | Bake Reflection Probe | ❌ |
| `tools run controller.remove-layer` | Remove Animator Controller Layer | ❌ |
| `tools run editor.menu` | Execute Editor Menu | ❌ |
| `tools run package.embed` | Embed Package | ✅ |
| `tools run package.install` | Package Install | ❌ |
| `tools run package.remove` | Package Remove | ❌ |
| `tools run package.deploy` | Package Deploy | ✅ |
| `tools run package.restore` | Package Restore | ✅ |
| `tools run package.bridge-remove` | Package Bridge Remove | ✅ |

`code.exec` keeps unrestricted synchronous C# execution. It does not reject infinite loops, long sleeps, blocking waits, lock contention, synchronous I/O, or long-running Unity API calls. Before submitting such code, the caller must decide whether it is necessary for the requested result and consider a bounded implementation or an existing Tool; necessary code may still be executed unchanged. After `Run` starts on the Unity Editor main thread, `--timeout-ms` can stop the CLI from waiting but cannot stop the C# code. The Editor can remain unresponsive, the mutation owner can remain active, Unity state can be partial or unknown, and recovery can require terminating the Editor process. A timeout is not proof that the code stopped, so inspect business state before retrying. Dry-run only previews the source hash and planned effect; it does not compile, execute, or prove termination.

The `reflection.method.call` tool performs no scope restriction on the invoked method (public/private/static/instance, any namespace/assembly, Editor/Runtime). Its risk metadata describes that effect; invoking the public Tool authorizes execution. When a method name has overloads, pass the selected signature's `parameterType` values as `parameterTypes`.

### Optional Preview

When `supportsDryRun: true`, pass `--dry-run` to obtain planned effects without executing the mutation. The caller may invoke the Tool directly instead; neither dry-run nor a separate confirmation is required.

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

## Project Tool Integrity

Each Unity domain validates the complete Tool table before publishing its single executable slot. List and describe read the same publication's descriptor-only projection, while execution reads the executable entry from that slot; the reader cannot obtain invokers, CLR types, or writable metadata. A failed candidate never replaces the prior slot. Source changes become visible only after Unity compiles and replaces the domain and listener generation. `doctor` reads a separate bounded catalog-health snapshot so transport reachability is not confused with registry health.

## How to Assess Risk as an AI Agent

1. **Always `tools describe` first.** Read `destructiveHint`, `supportsDryRun`, `target`, `availabilityScope`, permissions, and schemas.
2. **For destructive tools**: Assess the declared effects and invoke directly when authorized by the public call; use `--dry-run` only when a preview is useful.
3. **For runtime tools**: Verify Play Mode state before calling `target: "runtime"` tools.
4. **For direct-final tools**: Set an appropriate `--timeout-ms` when the operation may take time, then use the returned business data as the terminal result.
5. **For project tools**: Use `doctor` to inspect current-domain catalog health; fix invalid definitions and let Unity compile before rediscovery.

## Known Limitations

| Limitation | Risk Level | Mitigation |
|-----------|-----------|------------|
| Dry-run is not available for every destructive tool | Medium | Invoke directly with declared effects; use preview only where supported |
| Project tool registration changes only when Unity replaces the domain | Low | Re-discover after compilation/reload; bind exact session/listener provenance and let the first business POST reject stale generation |
| Catalog publication can fail closed on an ownership or descriptor error | Medium | Use `doctor` for transport and catalog-health results, fix the reported definition, then reload Unity |

## Cross-Reference

- Runtime and execution errors: [error-codes](./error-codes.md)
- Tool descriptor field coverage: [tool-discovery](./tool-discovery.md) §Descriptor field coverage
- Tool metadata schema: [../schemas/tool-metadata.schema.json](../schemas/tool-metadata.schema.json)
- Security policy source: `src/security-policy.ts` and `src/security.ts`
- AI skill (compact version): [/skills/unitycli/SKILL.md](../skills/unitycli/SKILL.md)
