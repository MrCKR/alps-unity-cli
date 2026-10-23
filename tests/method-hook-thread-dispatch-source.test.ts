import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const repoRoot = process.cwd();
const unitypkg = new URL("../unitypkg/", import.meta.url);
const hooksRoot = new URL("Editor/Infrastructure/MethodHooks/", unitypkg);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const hookToolsRoot = new URL("Editor/Tools/debug/hook/", unitypkg);

const HOOK_TOOLS = ["install", "replace", "enable", "disable", "remove", "list", "info"];

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

/** 递归列出目录下指定扩展名的文件。 */
async function files(root: URL, extension: string, prefix = ""): Promise<string[]> {
  const entries = await readdir(new URL(prefix, root), { withFileTypes: true });
  const result: string[] = [];
  for (const entry of entries) {
    const relative = `${prefix}${entry.name}`;
    if (entry.isDirectory()) result.push(...(await files(root, extension, `${relative}/`)));
    else if (entry.name.endsWith(extension)) result.push(relative);
  }
  return result;
}

/** 按花括号配对切出一个成员体，避免跨到相邻成员。 */
function memberBody(text: string, name: string): string {
  const index = text.indexOf(name);
  assert.ok(index >= 0, `missing member: ${name}`);
  const open = text.indexOf("{", index);
  assert.ok(open > index, `missing body for member: ${name}`);
  let depth = 0;
  for (let cursor = open; cursor < text.length; cursor += 1) {
    if (text[cursor] === "{") depth += 1;
    else if (text[cursor] === "}") {
      depth -= 1;
      if (depth === 0) return text.slice(open + 1, cursor);
    }
  }
  throw new Error(`unbalanced body for member: ${name}`);
}

test("the dispatch thread and frame limits are declared in the single debug budget owner", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  for (const value of ["MaxThreadRecords = 256", "MaxFramesPerThread = 64", "MaxPendingSupplements = 64"]) {
    assert.ok(limits.includes(value), `DebugLimits must declare ${value}`);
  }
  const summary = memberBody(limits, "internal static string Summary()");
  for (const name of ["MaxThreadRecords", "MaxFramesPerThread"]) {
    assert.ok(summary.includes(name), `Summary() must publish ${name}`);
  }
});

test("the hook dispatch is driven by an immutable published snapshot", async () => {
  const dispatch = await source(hooksRoot, "MethodHookHost.Dispatch.cs");
  const snapshot = await source(hooksRoot, "MethodHookDispatch.cs");
  const definition = await source(hooksRoot, "MethodHookDefinition.cs");

  assert.match(snapshot, /internal sealed class MethodHookDispatch/);
  assert.match(snapshot, /internal readonly MethodHookBinding\[\] Ordered;/);
  assert.match(snapshot, /internal static readonly MethodHookDispatch Empty/);
  assert.match(definition, /internal MethodHookDispatch Dispatch = MethodHookDispatch\.Empty;/);

  // 命中路径只按引用读取已发布的快照，发布整体替换它。
  assert.match(dispatch, /var dispatch = Volatile\.Read\(ref runtime\.Dispatch\);/);
  assert.match(dispatch, /Volatile\.Write\(ref runtime\.Dispatch/);
  const prefix = memberBody(dispatch, "internal static bool DispatchPrefixCore(");
  const postfix = memberBody(dispatch, "internal static void DispatchPostfixCore(");
  for (const body of [prefix, postfix]) {
    assert.doesNotMatch(body, /runtime\.Definitions/, "the hit path must never iterate the mutable definition list");
    assert.doesNotMatch(body, /\.Ordered\(\)/, "the hit path must not re-order definitions at hit time");
    assert.doesNotMatch(body, /Definitions\.Values/, "the hit path must not enumerate the mutable definition map");
  }
  assert.match(prefix, /ordered\.Length/);

  // 一个 exact target 只有一套 Adapter，命中目标表是并发字典。
  assert.match(dispatch, /ConcurrentDictionary<MethodBase, MethodHookTargetRuntime> DispatchTargets/);
  assert.match(dispatch, /if \(!DispatchTargets\.TryGetValue\(original, out runtime\)\) return true;/);
  assert.equal((dispatch.match(/private static void PublishDispatchSnapshot\(/g) ?? []).length, 1, "a single publish owner");
  assert.match(memberBody(dispatch, "private static int CompareDefinitions("), /right\.Priority\.CompareTo\(left\.Priority\)/);
});

test("one invocation fixes its configuration at prefix and never requeries it", async () => {
  const dispatch = await source(hooksRoot, "MethodHookHost.Dispatch.cs");
  const snapshot = await source(hooksRoot, "MethodHookDispatch.cs");

  assert.match(snapshot, /internal sealed class MethodHookFrame/);
  for (const member of ["InvocationIds", "Bindings", "Record", "Stamp"]) {
    assert.match(snapshot, new RegExp(`internal [A-Za-z<>\\[\\],. ]+ ${member};`), `the fixed frame must carry ${member}`);
  }
  assert.match(snapshot, /internal MethodHookFrame Pop\(MethodBase original\)/);
  assert.match(snapshot, /if \(!ReferenceEquals\(frames\[index\]\.Original, original\)\) continue;/);

  const prefix = memberBody(dispatch, "internal static bool DispatchPrefixCore(");
  const postfix = memberBody(dispatch, "internal static void DispatchPostfixCore(");
  assert.match(prefix, /frames\.Push\(new MethodHookFrame/);
  assert.match(postfix, /var frame = frames\.Pop\(original\);/);
  assert.match(postfix, /if \(frame == null\) return;/, "a postfix without its own frame must be a no-op instead of guessing");
  assert.match(postfix, /if \(frame\.Record\[index\]\) pending\.Add\(BuildInvocationEvent/);
  const finalizer = memberBody(dispatch, "internal static void DispatchFinalizerCore(");
  assert.match(finalizer, /frames\.Pop\(original\)/);

  // 采样决定与调用 id 在 prefix 固定，计数与 id 分配使用线程安全操作。
  assert.match(prefix, /var record = binding\.EmitHitEvents && SampleHit\(binding, definition\);/);
  assert.match(dispatch, /Interlocked\.Increment\(ref invocationSequence\)/);
  assert.match(dispatch, /internal long NextSampleIndex\(int step\)|definition\.NextSampleIndex\(every\)/);
  assert.match(await source(hooksRoot, "MethodHookDefinition.cs"), /Interlocked\.Increment\(ref HitCount\)/);
  assert.match(await source(hooksRoot, "MethodHookDefinition.cs"), /Interlocked\.Increment\(ref BypassCount\)/);
  assert.match(await source(hooksRoot, "MethodHookDefinition.cs"), /Interlocked\.Increment\(ref SampleIndex\)/);

  // 活动帧有界：达到上限时保持原方法语义并记录覆盖缺口。
  assert.match(dispatch, /if \(frames\.Count >= DebugLimits\.MaxFramesPerThread\)/);
  assert.match(dispatch, /RecordCoverageSkip\(original, "frame-limit"\)/);
  assert.match(dispatch, /if \(frames == null\)[\s\S]{0,200}RecordCoverageSkip\(original, "thread-record-limit"\)/);
  assert.match(dispatch, /ReleaseThreadFramesIfEmpty\(frames\)/);
  assert.match(dispatch, /Interlocked\.Increment\(ref threadRecordCount\) > DebugLimits\.MaxThreadRecords/);
  assert.doesNotMatch(dispatch, /ThreadFrames\.Clear\(\) \/\/|eviction|lru/i, "active frames must never be evicted by policy");
});

test("no Unity main-thread API is reachable from the hit path", async () => {
  const dispatch = await source(hooksRoot, "MethodHookHost.Dispatch.cs");
  const projection = await source(hooksRoot, "MethodHookProjection.cs");
  const definition = await source(hooksRoot, "MethodHookDefinition.cs");

  assert.doesNotMatch(dispatch, /Time\.frameCount/, "the dispatch must not read the Unity frame directly");
  assert.match(dispatch, /DebugEventClocks\.ReadUnityFrame\(\)/);
  assert.match(dispatch, /DebugEventClocks\.LastObservedMainFrame/);
  assert.match(dispatch, /if \(!playScope\) frame = Volatile\.Read\(ref tickCounter\);/, "an off-main hit must not assume a frame");
  assert.match(dispatch, /private static bool IsMainThread\(\)/);
  assert.match(dispatch, /frame = mainThread \? DebugEventClocks\.ReadUnityFrame\(\) : DebugEventClocks\.LastObservedMainFrame;/);

  // Unity 显示身份只有一个读取点，且只在允许主线程身份的路径上被使用。
  assert.equal((projection.match(/internal static object ProjectUnityIdentity\(/g) ?? []).length, 1);
  assert.match(projection, /allowUnityIdentity \? ProjectUnityIdentity\(unityObject, truncation\) : "unity-object:"/);
  assert.match(projection, /internal static object Project\(object value, Truncation truncation, bool allowUnityIdentity\)/);
  assert.match(projection, /return Project\(value, truncation, true\);/);
  assert.match(dispatch, /ProjectUnityIdentity\(alive, truncation\)/);
  assert.match(dispatch, /if \(record && !mainThread\) EnqueueInstanceSupplement\(binding, invocationId, instance, stamp\);/);
  assert.match(definition, /if \(!allowUnityIdentity\)[\s\S]{0,200}unity-object-pending:/, "an off-main hit must mark the identity as pending instead of faking it");
  assert.match(definition, /var projected = MethodHookProjection\.Project\(instance, truncation, true\);/);

  // 主线程补充是独立、带时间戳的事件，不伪装成命中时刻的值。
  const supplements = memberBody(dispatch, "private static void PublishInstanceSupplements()");
  assert.match(supplements, /Kind = "supplement"/);
  assert.match(supplements, /Name = "instance-identity"/);
  assert.match(supplements, /"identityCapturedAt", "main-thread-supplement"/);
  assert.match(supplements, /DebugEventClocks\.MonotonicMilliseconds - entry\.HitMilliseconds/);
  assert.match(dispatch, /PendingInstanceSupplements\b|PendingSupplements\.TryDequeue/);
});

test("worker faults close one revision only and are committed on the main thread tick", async () => {
  const dispatch = await source(hooksRoot, "MethodHookHost.Dispatch.cs");
  const host = await source(hooksRoot, "MethodHookHost.cs");
  const definition = await source(hooksRoot, "MethodHookDefinition.cs");

  assert.match(definition, /internal int DispatchClosed;/);
  assert.match(dispatch, /Interlocked\.CompareExchange\(ref definition\.DispatchClosed, \(int\)revision, 0\)/);
  assert.match(dispatch, /if \(revision != definition\.Revision\)/);
  assert.match(dispatch, /Interlocked\.Increment\(ref definition\.StaleFaults\)/);
  assert.match(dispatch, /if \(IsMainThread\(\)\) FaultDefinitionOnMainThread/);
  assert.match(dispatch, /PendingThreadFaults\[definition\.HookId\] = new MethodHookThreadFault/);
  assert.match(dispatch, /if \(definition\.Revision != fault\.Revision\)/);

  const tick = memberBody(host, "internal static void Tick()");
  assert.match(tick, /ApplyPendingThreadFaults\(\);/);
  assert.match(tick, /PublishInstanceSupplements\(\);/);
  assert.match(tick, /ReleaseIdleThreadFrames\(\);/);
  assert.doesNotMatch(host, /DeferredDetach|TryDeferredDetach/, "the main-thread-only deferred detach path is obsolete");
  assert.match(host, /private static volatile bool initialized;/);
  assert.doesNotMatch(dispatch, /ManagedThreadId != mainThreadId/);

  // 线程侧故障只在本 target 已无活动定义时撤销 detour。
  const release = memberBody(dispatch, "private static void ReleaseAdapterWhenInactive(");
  assert.match(release, /if \(state == MethodHookState\.Armed \|\| state == MethodHookState\.Dormant \|\| state == MethodHookState\.Restoring\) return;/);
});

test("callback self-recursion is suppressed separately from real business recursion", async () => {
  const dispatch = await source(hooksRoot, "MethodHookHost.Dispatch.cs");
  const prefix = memberBody(dispatch, "internal static bool DispatchPrefixCore(");
  assert.match(prefix, /if \(IsReentrant\(binding\.HookId\)\)[\s\S]{0,220}definition\.CountBypass\(\);[\s\S]{0,80}continue;/);
  assert.match(prefix, /var invocationId = Interlocked\.Increment\(ref invocationSequence\);/, "invocation ids are allocated per real hit");

  const dtos = await source(hookToolsRoot, "MethodHookDtos.cs");
  assert.match(dtos, /public long BypassCount \{ get; set; \}/);
  const info = await source(hookToolsRoot, "../hook-info.cs");
  assert.match(info, /bypassCount counts callbacks that re-entered their own hooked target and were suppressed/);
});

test("one real invocation is counted as exactly one hit", async () => {
  // 回归护栏：prefix 与 postfix 都计数会把每次调用算成两次（异常结束没有 postfix，又不得漏计）。
  // 命中计数只允许在 prefix 按一次真实调用发生。
  const dispatch = await source(hooksRoot, "MethodHookHost.Dispatch.cs");
  const hits = dispatch.match(/definition\.CountHit\(\);/g) ?? [];
  assert.equal(hits.length, 1, "hit counting must appear exactly once in dispatch");
  const prefix = memberBody(dispatch, "internal static bool DispatchPrefixCore(");
  const postfix = memberBody(dispatch, "internal static void DispatchPostfixCore(");
  const finalizer = memberBody(dispatch, "internal static void DispatchFinalizerCore(");
  assert.match(prefix, /definition\.CountHit\(\);/, "the prefix path counts the invocation");
  for (const [name, body] of [["postfix", postfix], ["finalizer", finalizer]] as const) {
    assert.doesNotMatch(body, /CountHit\(\)|CountBypass\(\)/, `${name} must not count the same invocation again`);
  }
});

test("the intervention conflict rule is enforced before every activation path", async () => {
  const operations = await source(hooksRoot, "MethodHookHost.Operations.cs");
  assert.match(operations, /private static void EnsureNoConflictingIntervention\(string targetKey, MethodHookDefinition self, bool intervention\)/);
  assert.match(operations, /throw new BridgeCommandException\(\s*"E_DEBUG_RESOURCE_CONFLICT"/);
  const install = memberBody(operations, "internal static MethodHookInstallPlan PrepareInstall(");
  const replace = memberBody(operations, "internal static MethodHookMutationPlan PrepareReplace(");
  const enable = memberBody(operations, "internal static MethodHookMutationPlan PrepareEnable(");
  for (const body of [install, replace, enable]) {
    assert.match(body, /EnsureNoConflictingIntervention\(/, "every activation path must reject a second intervention before any effect");
  }
  assert.match(operations, /EnsureNoConflictingIntervention\(/, "the conflict rule must have one owner");
  assert.match(await source(hooksRoot, "MethodHookDefinition.cs"), /internal bool IsIntervention \{ get \{ return HasCallbacks; \} \}/);

  const output = await readFile(join(repoRoot, "src", "output.ts"), "utf8");
  const docs = await readFile(join(repoRoot, "docs", "error-codes.md"), "utf8");
  assert.match(output, /^  E_DEBUG_RESOURCE_CONFLICT: "E_DEBUG_RESOURCE_CONFLICT",$/m);
  assert.ok(docs.includes("`E_DEBUG_RESOURCE_CONFLICT`"), "the conflict code must be documented");
});

test("stop, disable and remove keep other owners and in-flight calls intact", async () => {
  const host = await source(hooksRoot, "MethodHookHost.cs");
  const release = memberBody(host, "private static bool ReleaseAdapterIfUnused(");
  assert.match(release, /runtime\.Definitions\.Remove\(definition\);/);
  assert.match(release, /if \(runtime\.Definitions\.Count > 0\)/);
  assert.match(release, /Volatile\.Write\(ref runtime\.Dispatch, MethodHookDispatch\.Empty\);/);
  assert.doesNotMatch(release, /DispatchTargets\.Remove\(/, "the concurrent target map may only be updated through TryRemove");
  // 层出中的调用继续用自己固定的帧完成，disable/remove 从不清理在途帧。
  assert.doesNotMatch(await source(hooksRoot, "MethodHookHost.Operations.cs"), /ThreadFrames|frames\.Pop|MethodHookFrame/);
});

test("obsolete thread rejection markers are gone from product, docs and tests", async () => {
  const forbidden = "E_HOOK_" + "THREAD_UNSUPPORTED";
  for (const file of await files(unitypkg, ".cs")) {
    const text = await source(unitypkg, file);
    assert.ok(!text.includes(forbidden), `${file} must not reference ${forbidden}`);
  }
  for (const file of await files(new URL("../docs/", import.meta.url), ".md")) {
    const text = await readFile(join(repoRoot, "docs", file), "utf8");
    assert.ok(!text.includes(forbidden), `docs/${file} must not document ${forbidden}`);
  }
  for (const file of await files(new URL("../tests/", import.meta.url), ".ts")) {
    const text = await readFile(join(repoRoot, "tests", file), "utf8");
    assert.ok(!text.includes(forbidden), `tests/${file} must not assert ${forbidden}`);
  }
});

test("every hook descriptor states the real thread scope", async () => {
  for (const tool of HOOK_TOOLS) {
    const text = await source(hookToolsRoot, `../hook-${tool}.cs`);
    assert.match(text, /supported managed thread/, `${tool}.cs must state the supported thread scope`);
  }
  const install = await source(hookToolsRoot, "../hook-install.cs");
  assert.match(install, /callbacks run inline on that same thread/);
  assert.match(install, /cannot be proven statically/);
  assert.match(install, /DebugLimits\.Summary\(\)/);
  const info = await source(hookToolsRoot, "../hook-info.cs");
  assert.match(info, /staleFaults/);
  assert.match(info, /droppedSupplements/);
  const dtos = await source(hookToolsRoot, "MethodHookDtos.cs");
  assert.match(dtos, /public long StaleFaults \{ get; set; \}/);
  assert.match(dtos, /public long DroppedSupplements \{ get; set; \}/);
  const events = await source(debugRoot, "DebugEventDtos.cs");
  assert.match(events, /hook 来源当前取值 prefix、postfix、custom、fault、lifecycle、supplement、coverage；pause 来源当前取值 snapshot、pause、custom、pause-skipped、pause-suppressed、pause-resume-requested。/);
});
