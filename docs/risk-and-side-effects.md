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
| `tools run debug.code.execute` | Execute Live Code | ✅ |
| `tools run scriptable-object.create` | Create ScriptableObject Asset | ✅ |
| `tools run scriptable-object.modify` | Modify ScriptableObject Asset | ✅ |
| `tools run texture.modify` | Modify Texture Asset | ✅ |
| `tools run texture.delete` | Delete Texture Asset | ✅ |
| `tools run shader.delete` | Delete Shader Asset | ✅ |
| `tools run serialized-object.patch` | Modify Object (deep patch) | ✅ |
| `tools run debug.method.invoke` | Call Method via Reflection | ✅ |
| `tools run lighting.bake.clear` | Clear Baked Lighting | ❌ |
| `tools run lighting.bake.start` | Start Light Bake | ❌ |
| `tools run lighting.bake.reflection-probe` | Bake Reflection Probe | ❌ |
| `tools run animator-controller.remove-layer` | Remove Animator Controller Layer | ❌ |
| `tools run editor.menu` | Execute Editor Menu | ❌ |
| `tools run package.embed` | Embed Package | ✅ |
| `tools run package.install` | Package Install | ❌ |
| `tools run package.remove` | Package Remove | ❌ |
| `tools run package.deploy` | Package Deploy | ✅ |
| `tools run package.restore` | Package Restore | ✅ |
| `tools run package.bridge-remove` | Package Bridge Remove | ✅ |
| `tools run debug.hook.install` | Install Live Method Hook | ✅ |
| `tools run debug.hook.replace` | Replace Hook Configuration | ✅ |
| `tools run debug.hook.enable` | Enable Hook Definition | ✅ |

### Live method hooks

`debug.hook.install`, `debug.hook.replace` and `debug.hook.enable` may activate arbitrary C# callbacks inside the current Editor domain, so they always declare `destructiveHint: true` and the internal `execute-user-code` permission. `debug.hook.disable` and `debug.hook.remove` do not run user callbacks and declare `method-patch` plus `session-definition` side effects instead.

What the hook group does not do: it never edits game `.cs` files, never rewrites the on-disk assembly, never triggers a project compile, and never changes Enter Play Mode settings. Definitions belong to one Editor session and are discarded on Editor restart, while their patches only exist in the current code domain.

Two boundaries matter for correct expectations:

- A managed call site that was compiled before the patch keeps the original behaviour, so an installed definition can legitimately produce no hits. Patch installation is not proof that the method was called or that a callback ran.
- Callback source is compiled on demand with the Unity-bundled Mono/csc subprocess and loaded into the current domain. That costs a compiler subprocess and bounded temporary files, and loaded assemblies cannot be unloaded individually, so removing a hook does not reclaim them.

A callback that returns normally after exceeding the overrun threshold is recorded and faults that definition, but that check is post-hoc: an infinite loop, a long sleep or a deadlock in a callback can still keep the Editor unresponsive, and `disable`/`remove` cannot terminate a callback that is already running. Callbacks run on the intercepted thread; hits observed on worker threads are handled on that same thread rather than being deferred to the main thread. Field, scene and file writes performed by callbacks are real side effects and are not rolled back.

### Breakpoints and pauses

A hook definition may declare a `breakpoint`, and a breakpoint is a hit behaviour of that same hook: same exact target, same fixed revision, same unified event truth. A `snapshot` breakpoint captures the configured values and an optional bounded stack and then continues; it never waits for a client. A `pause` breakpoint publishes a `pauseId` and stops the hit thread at the current call boundary, which is why `debug.hook.install` and `debug.hook.replace` may declare `debug-pause` alongside `method-patch`.

`debug.pause.evaluate` declares `destructiveHint: true` because it runs caller-supplied C# inside the paused business call and can change the paused invocation's arguments, result and reachable object state; the response lists the interventions that actually happened instead of inferring them from the fact that the call returned. `debug.pause.resume` only declares `debug-pause`, and `debug.pause.list`, `debug.pause.wait` and `debug.pause.inspect` are pure reads.

Honest boundaries of a pause:

- Only the hit thread is guaranteed stopped. Other managed threads, servers, external clocks and object mutation continue, and a thread that holds a business lock can block other threads while it is paused.
- Pausing the main thread stops Editor UI, rendering and the normal tick; Windows may show the Editor as not responding. A pause is still executed without any extra confirmation step, because the call itself is the authorisation.
- A bounded pause uses monotonic wall time, so a CLI request timeout, Unity pause and `timeScale` never extend it; on expiry the pause auto-releases, the confirmed in-flight invocation continues and a `pause-timeout` fact is recorded. An unbounded pause is not released by a CLI exit or by a wait window ending; it is released by an explicit resume, by `session.stop` when the breakpoint declared that session, or by the reachable Play exit, Domain Reload and Editor shutdown cleanup.
- `debug.pause.inspect` reads the paused thread's real stack, but locals of other frames are not readable from a stack trace and are reported as unavailable instead of guessed.
- `debug.pause.resume` releases a wait; it is not preemption. Synchronous code that a pause site is executing still runs to completion, and if it never returns the caller is told the execution state is unknown or pending rather than that the thread was forcibly resumed.
- A snapshot or pause capture never touches other frames' locals, and a skipped hit (simultaneous-site limit) or a suppressed nested hit keeps the original method path and is reported as a coverage gap.

`debug.code.execute` keeps unrestricted synchronous C# execution. It does not reject infinite loops, long sleeps, blocking waits, lock contention, synchronous I/O, or long-running Unity API calls. Before submitting such code, the caller must decide whether it is necessary for the requested result and consider a bounded implementation or an existing Tool; necessary code may still be executed unchanged. After `Run` starts on the Unity Editor main thread, `--timeout-ms` can stop the CLI from waiting but cannot stop the C# code. The Editor can remain unresponsive, the mutation owner can remain active, Unity state can be partial or unknown, and recovery can require terminating the Editor process. A timeout is not proof that the code stopped, so inspect business state before retrying. Dry-run only previews the source hash and planned effect; it does not compile, execute, or prove termination.

The `debug.method.invoke` tool performs no scope restriction on the invoked method (public/private/static/instance, any namespace/assembly, Editor/Runtime). Its risk metadata describes that effect; invoking the public Tool authorizes execution. When a method name has overloads, pass the selected signature's `parameterType` values as `parameterTypes`.

### Debug sessions

`debug.session.create` declares the `debug-session` side effect and, when `logCapture` is enabled, the `log-subscription` side effect: it publishes this Editor session's session definition file at `Library/UnityCli/debug/session.json`, which is one fixed file for the whole debug domain, and it registers at most one threaded log subscription for the whole domain. `debug.session.stop` and `debug.session.remove` declare the `debug-session` side effect: they publish the same fixed file and stop or release the session's log subscription. None of them are destructive: they change debug bookkeeping only, never project assets, scenes, packages or scripts. `debug.session.info` and `debug.session.list` are pure reads that do not write that file and do not run cleanup.

What the session group does not do: it never edits game `.cs` files, never triggers a compile or a domain reload, and never enters or leaves Play Mode. It captures logs only when `logCapture` is enabled, and that capture records the real callback thread instead of assuming the callback is the business call boundary. Definitions belong to one Editor session. They survive a Domain Reload inside the same Editor session, but a restored definition comes back without any running resource, and an Editor restart invalidates every definition. A stopped definition keeps its evidence until `debug.session.remove` deletes it, and removal only succeeds after the cleanup pipeline reports no pending work.

A session is the resource owner for debug facilities: a facility must reserve an active resource slot before its effect, and cleanup only ever touches the resources of its own session, so stopping one session cannot tear down another session's resources. Limits, the resource budget and the ordered cleanup steps are reported by `tools describe debug.session.create` and returned in the cleanup report of `debug.session.stop`.

### Debug evidence runs

`debug.run.begin` declares an evidence window instead of executing anything. It publishes one window definition owned by an open session and records the caller's claims: the required checks with their source, capture position and quantifier, the source coverage requirements, the end conditions, the declared preparation and inputs, and the declared seed control. It never prepares the scene, never injects inputs, never compiles and never schedules. The only side effect it declares is `debug-session`, because the window is debug bookkeeping; `debug.run.end`, `debug.run.wait`, `debug.run.info`, `debug.run.list` and `debug.run.compare` declare no side effect at all, so none of them writes a file, stops a program or changes Unity state.

What a verdict means, and what it does not: a check can only pass when the events the unified event store really retained prove it, so a coverage gap, a sampled stream, truncated evidence, an interrupted window, a related configuration change, an undrained capture boundary or an evicted detail all become `indeterminate` with a machine-readable reason instead of a silent pass. A negative or whole-window claim additionally requires a caller-declared coverage requirement; without that declaration the check never passes. A complete counterexample is reported as `failed` and is never downgraded by a later unrelated gap. Assertion failures are business results, not errors: `debug.run.end`, `debug.run.wait` and `debug.run.info` answer `ok: true` with a `verdict` field, and no assertion-failed error code exists. Only an unknown `runId` (including one from a previous domain generation) or a session that is not open fails the call.

Honest boundaries: `debug.run.end` seals evidence and never stops a running program, so sealing is not cleanup and `debug.program.stop` is still required when the program must end. Sealing takes one atomic capture of the boundary sequence and the scheduler queue watermark, which is how the report states whether events produced before the boundary had already entered the store. Windows belong to the current domain generation: a Domain Reload or an Editor restart removes them and an old `runId` is answered with `E_DEBUG_RUN_NOT_FOUND` rather than reattached to a different window. A terminal window keeps the report it closed with, so its verdict and bounded evidence payload stay readable by `runId` while the declared run budget retains them; `debug.run.list` keeps the newest summaries and only the newest windows keep their detailed payload, and a released payload is reported as `detailAvailable: false` with the `detail-evicted` reason instead of silently replacing the frozen verdict.

`debug.run.compare` compares two retained windows on dimensions the caller must declare: the event source, the capture position, the alignment key, the compared value path and the applicable clock. It never defaults to global sequence numbers, object addresses or wall time, and it reads live evidence only, so a pure data comparison of external files belongs to the AI itself. The answer is `equal`, `diverged` or `indeterminate`: a difference is only proven when both sides retained a real event at the same aligned position (or, for a business-key alignment, under the same key), while an event that exists on one side only, a value on a truncated projection, a sampled stream, a coverage gap, an evicted event range, a partial clock match, a missing or repeated key, or a window that is still open all degrade the answer to `indeterminate` with a machine-readable reason and an `unprovenDifference` entry. The result also reports a bounded context per side, the differences between the two runs' declared definition, configuration, environment, clock coverage, coverage facts and window state, and each side's real coverage range. It does not stop, seal or re-execute anything.

### Debug input

`debug.input.info` is a pure read: it reports the optional device backend status, the general input paths, the managed wrapper verification for one candidate input, and the devices and input records this debug owner actually holds. It never creates a device, never submits an event, never installs a package and never writes the session store. A backend status of `unavailable` is the answer, not an error: when the optional Input System API is not loaded the reason is `backend-not-loaded`, and when the API exists but its shape does not match what this owner needs the reason is `api-member-missing` with the bounded `missingApiMembers` list. Nothing is installed in either case.

`debug.input.key` and `debug.input.pointer` declare the `virtual-input-device` side effect: they create at most one key device and one pointer device per session and submit state to them. `debug.input.release` declares the same side effect because it clears that state and ends the device lifetime for scope `all`. None of them is destructive, none touches project assets, scenes, packages or scripts, and all of them fail with `E_DEBUG_BINDING_UNSUPPORTED` and `status: unavailable` before any effect when no backend is available.

Honest boundaries of the device backend:

- It is an optional backend only. General input is broader than a keyboard: an input can be a project method call, a message enqueue, protocol data, parameter replacement, result replacement or a device event, so the general paths are `debug.method.invoke` (business entry), `debug.hook.install` (parameter, result or skip intervention on an interceptable managed wrapper), `debug.code.execute` (explicit code) and `debug.program.run` (bounded program entry or cross-thread delivery to a thread-safe point). None of them is tied to a single logical frame, and the device backend does not replace them.
- Every dispatch carries its own declared end boundary and there is no default: `hold-ms` needs an explicit `holdMs`, and `until-release` holds until an explicit release, a pause, a target invalidation, `session.stop` or Play exit. A dispatch that omits its boundary is refused before the effect barrier.
- Release is never assumed. `released` is reported only when the device readback proves the pressed state is gone, or when the device is provably absent from the backend's device list; everything else is `pending` with the exact reason (`release-not-applied`, `backend-unavailable`, `device-list-unreadable`, `requires-main-thread`), and the remaining confirmation is completed by the central scheduler tick on the Unity main thread. `debug.input.release` on a stopped session answers from the current records instead of failing, because release is what a stop already asked for.
- Ownership is exclusive and conflicts are refused before activation: a device whose name is already used by another owner is never taken over, a control that is already held under a different end boundary is never overwritten, and an unreadable device list is refused rather than assumed empty. This owner never resets, rewrites or removes a real device or another owner's device.
- A managed wrapper is reported supported only after on-site verification: a real detour armed on that exact target and at least one observed invocation. Without a candidate wrapper, without a live detour, or without an observed invocation the answer is `supported: false` with the reason (`no-wrapper-declared`, `wrapper-unresolved`, `no-live-detour`, `no-observed-invocation`) and an explicit statement that this input cannot be driven; no project-specific shortcut is used to claim support.
- A submitted device event proves only that this owner submitted it. It never proves that the business read the input, so acceptance must be proven with a hook capture, a program check, a value read or a sealed `debug.run` window. Each dispatch and release also appends one `trigger`-source event (`kind: input-dispatch`, execution point `input-dispatch`), so `debug.events.read` can read the same fact and `debug.run.begin` can require it with `source: trigger` and `kind: input-dispatch`.

### Debug threads and values

`debug.thread.list` is a pure read: it reports only the threads this debug facility actually observed, meaning threads that produced a retained debug event or currently hold an in-flight hook hit frame. It is not an enumeration of every managed thread in the Editor process, a thread that produced nothing is absent rather than reported as idle, and counts describe the retained window honestly. Nothing is persisted, so a Domain Reload or an Editor restart simply removes the observed records.

`debug.value.read` and `debug.value.write` address one exact root plus a bounded field path. The root is a static field, an exact Unity selector (the same resolution as the other object tools, so an ambiguous or missing selector fails), or a current managed object handle.

The read path is deliberately narrow. A pure read never executes a property getter, never calls an arbitrary `ToString`, never runs a user serializer or enumerator, and never triggers type initialisation. Requests that cannot be proven safe are refused with `E_DEBUG_SCOPE_UNSUPPORTED` and a rule naming the exact boundary, and the route for those effects is `debug.code.execute`. A static field is therefore only readable on a type that declares no static constructor, because there is no supported way to prove the initialiser already ran. Values that are reference types outside the safe set are reported by type only, so a read never serialises an arbitrary object graph. Handles are weak by default and bound to the runtime and domain generation that observed them: after collection, Unity destruction, a Play runtime change or a Domain Reload they report `E_DEBUG_TARGET_STALE` with their actual state, and a stale handle is never presented as a null field value.

`debug.value.write` is a structured intervention, not a property setter and not a method invoker: the value is converted to the declared field type and must match it, an intermediate value-type member is written back through an explicit boxed copy chain, and the result reports that as `structWriteBack`. Every pre-visible cause — the target field is not writable (`E_DEBUG_FIELD_NOT_WRITABLE` with rule `field-is-literal` or `field-is-init-only`), a path member is null or not a field, the value does not match the declared type, the root is not addressable, the session is not open or the guard already mismatches — is decided before the single effect barrier, so a refused write never suggests that state may have changed. Only a failure that is genuinely discovered after the barrier is reported as `E_EXECUTION_TIMEOUT` with `effectMayBePartial: true`, and then the field must be re-read. The tool declares the `runtime-object-modify` side effect because it changes managed field state on a live object. With `registerRestore` (the default) it also registers a conditional restore record owned by the named session: `debug.session.stop` restores the field only while the target is still valid and the current value still equals the value this session wrote, and reports any other write as a conflict instead of overwriting it. The record keeps the resolved target identity for the lifetime of the session, so it is itself a recorded intervention. A `strong` handle keeps the target alive while it exists and is recorded as an intervention that changes object lifetime; handles and restore records are bounded per session and per process, are released by `debug.session.stop`, and are refused once the session is not open. Field writes are not rolled back automatically, and explicit code execution performed by the caller is never rolled back.

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
| Catalog publication isolates an ownership, duplicate-name or descriptor error instead of failing the whole catalog | Medium | Use `doctor` for transport and catalog-health results, fix the reported definition, then reload Unity; `tools describe` and `tools run` return the frozen isolation result for the affected name |

## Cross-Reference

- Runtime and execution errors: [error-codes](./error-codes.md)
- Tool descriptor field coverage: [tool-discovery](./tool-discovery.md) §tools describe
- Tool metadata schema: [../schemas/tool-metadata.schema.json](../schemas/tool-metadata.schema.json)
- Destructive metadata source: TS tool metadata and bridge specs (`src/tools/**`, `src/bridge/domains/*.ts`) plus C# `ToolDescriptor.DestructiveHint`, cross-checked by `src/contracts/command-contract.ts`
- AI skill entry: [skills/unitycli/SKILL.md](../skills/unitycli/SKILL.md)
