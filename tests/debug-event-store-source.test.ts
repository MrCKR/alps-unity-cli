import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import { readdir } from "node:fs/promises";
import test from "node:test";

const unitypkg = new URL("../unitypkg/", import.meta.url);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const hooksRoot = new URL("Editor/Infrastructure/MethodHooks/", unitypkg);
const sharedRoot = new URL("Shared/", unitypkg);

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

/** 递归列出目录下的 .cs 文件。 */
async function csharpFiles(root: URL, prefix = ""): Promise<string[]> {
  const entries = await readdir(new URL(prefix, root), { withFileTypes: true });
  const files: string[] = [];
  for (const entry of entries) {
    const relative = `${prefix}${entry.name}`;
    if (entry.isDirectory()) files.push(...(await csharpFiles(root, `${relative}/`)));
    else if (entry.name.endsWith(".cs")) files.push(relative);
  }
  return files;
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

test("the debug domain declares every budget in exactly one place", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  await assert.rejects(access(new URL("Editor/Infrastructure/MethodHooks/MethodHookLimits.cs", unitypkg)));

  for (const value of [
    "MaxEventsPerSession = 4096",
    "MaxEventBytesPerSession = 8L * 1024 * 1024",
    "MaxEventBytesGlobal = 32L * 1024 * 1024",
    "MaxStoredEventBytes = 16 * 1024",
    "MaxStoredProjectionStringBytes = 2 * 1024",
    "MaxStoredProjectionDepth = 4",
    "MaxStoredProjectionItemsPerLevel = 64",
  ]) {
    assert.ok(limits.includes(value), `DebugLimits must declare ${value}`);
  }
  assert.match(limits, /Summary\(\)/);
  assert.ok(
    limits.indexOf("class DebugLimits") < limits.indexOf("MaxDefinitionsPerSession"),
    "every hook and store constant must live inside the single budget owner",
  );

  for (const file of await csharpFiles(unitypkg)) {
    const text = file === "Editor/Infrastructure/Debug/DebugLimits.cs" ? "" : await source(unitypkg, file);
    assert.doesNotMatch(text, /class MethodHookLimits/, `${file} must not declare a second budget class`);
    assert.doesNotMatch(text, /MethodHookLimits\./, `${file} must read the unified debug budget`);
  }
});

test("the hook path appends into the unified store and keeps no second event ring", async () => {
  const store = await source(debugRoot, "DebugEventStore.cs");
  const shared = await source(sharedRoot, "UnityCliDebugEvents.cs");
  const dispatch = await source(hooksRoot, "MethodHookHost.Dispatch.cs");
  const host = await source(hooksRoot, "MethodHookHost.cs");

  assert.match(store, /UnityCliDebugEventBuffer/);
  assert.match(store, /internal static long Append\(UnityCliDebugEvent entry\)/);
  assert.match(host + dispatch, /DebugEventStore\.Append\(/);
  assert.match(host, /DebugEventStore\.Restore\(/);
  assert.match(host, /DebugEventStore\.Snapshot\(/);
  assert.doesNotMatch(host + dispatch, /MethodHookEventBuffer|new Queue<MethodHookEventRecord>/);

  // 全仓只有统一存储分配事件 sequence。
  let authorities = 0;
  for (const file of await csharpFiles(unitypkg)) {
    const text = await source(unitypkg, file);
    authorities += (text.match(/entry\.Sequence = \+\+sequence;/g) ?? []).length;
  }
  assert.equal(authorities, 1, "the event sequence must have exactly one authority");
  assert.match(shared, /entry\.Sequence = \+\+sequence;/);
  assert.doesNotMatch(host, /lastSequence/);
});

test("no frame API is reachable from the off-main append path", async () => {
  const store = await source(debugRoot, "DebugEventStore.cs");
  const clocks = await source(debugRoot, "DebugEventClocks.cs");
  const shared = await source(sharedRoot, "UnityCliDebugEvents.cs");

  const frameReaders: string[] = [];
  for (const file of await csharpFiles(debugRoot)) {
    const text = await source(debugRoot, file);
    if (/Time\.frameCount/.test(text)) frameReaders.push(file);
  }
  assert.deepEqual(frameReaders, ["DebugEventClocks.cs"], "the Unity frame may only be read by the debug clock owner");

  const offMain = memberBody(store, "private static void CaptureOffMainThread(UnityCliDebugEvent entry)");
  assert.doesNotMatch(offMain, /Time\.frameCount|ReadUnityFrame|ObserveMainThread/);
  assert.match(offMain, /DebugEventClocks\.LastObservedMainFrame/);
  assert.match(offMain, /entry\.Frame = 0L;/);
  assert.match(offMain, /DebugEventClocks\.MonotonicMilliseconds/);

  const mainCapture = memberBody(store, "private static void CaptureMainThread(UnityCliDebugEvent entry)");
  assert.match(mainCapture, /DebugEventClocks\.IsCurrentThreadMainThread|ReadUnityFrame/);
  assert.match(clocks, /internal static bool IsCurrentThreadMainThread\(\)/);
  assert.match(clocks, /internal static long LastObservedMainFrame/);
  assert.match(clocks, /bound != 0 && bound == Thread\.CurrentThread\.ManagedThreadId/);
  assert.match(store, /if \(DebugEventClocks\.IsCurrentThreadMainThread\(\)\) CaptureMainThread\(entry\);/);

  assert.doesNotMatch(shared, /UnityEngine|Time\.frameCount|EditorApplication/);
});

test("the program append path reads the Unity frame only behind a main-thread check", async () => {
  const support = await source(debugRoot, "DebugProgramHost.Support.cs");
  const store = await source(debugRoot, "DebugEventStore.cs");
  const dtos = await source(debugRoot, "DebugEventDtos.cs");

  // 目标方法 entry 在命中线程当场执行；该线程可能是 worker。
  // 无条件调用主线程专属帧读取会抛异常并静默丢掉整条事件，因此只允许条件读取。
  const entry = memberBody(support, "private static UnityCliDebugEvent ProgramEvent(");
  assert.equal((entry.match(/DebugEventClocks\.ReadUnityFrame\(\)/g) ?? []).length, 1, "the program append path must read the real frame exactly once");
  assert.match(entry, /Frame = mainThread \? DebugEventClocks\.ReadUnityFrame\(\) : DebugEventClocks\.LastObservedMainFrame,/, "the real frame may only be read on a proven main thread");
  assert.doesNotMatch(entry, /Frame = DebugEventClocks\.ReadUnityFrame\(\)/);
  assert.match(entry, /MainThread = mainThread,/);

  const append = memberBody(support, "private static void AppendProgramEvent(");
  assert.match(append, /var mainThread = DebugEventClocks\.IsCurrentThreadMainThread\(\);/);
  assert.match(append, /DebugEventStore\.ReportAppendFailure\(/, "a failed append must be reported, not swallowed");
  assert.match(append, /catch \(Exception exception\)/);

  // 丢事件必须可见：计数与归因由存储 owner 一次完成，因此不存在“只计数不归因”的失败。
  const report = memberBody(store, "internal static void ReportAppendFailure(UnityCliDebugEvent lost, string reason)");
  assert.doesNotMatch(report, /ReadUnityFrame\(\)/, "the compensation fact must not touch a main-thread-only reader");
  assert.match(report, /Interlocked\.Increment\(ref appendFailures\)/, "the count and its attribution must happen in this one owner");
  assert.match(report, /UnityCliBridge\.WriteLog\("debug_event_append_failed /);
  assert.match(report, /Kind = CoverageKind,/);
  assert.match(report, /Name = CoverageEventName,/);
  assert.match(report, /\{ "lostKind", kind \}/);
  assert.match(report, /\{ "lostName", name \}/);
  assert.match(report, /Append\(new UnityCliDebugEvent/);
  assert.match(store, /private const string CoverageKind = "coverage";/);

  // 归因事实与计数同属一个保留窗口：窗口被丢弃时计数一起归零，读到的失败必然可归因。
  assert.doesNotMatch(store, /internal static void RecordAppendFailure\(\)/, "the store must not expose a count-only failure API");
  assert.match(store, /internal static long AppendFailures \{ get \{ return Interlocked\.Read\(ref appendFailures\); \} \}/);
  assert.match(store, /Interlocked\.Exchange\(ref appendFailures, 0L\)/);
  assert.match(store, /AppendFailures = failedAppends,/);
  assert.match(dtos, /public long AppendFailures \{ get; set; \}/);
  for (const file of await csharpFiles(unitypkg)) {
    const text = await source(unitypkg, file);
    assert.doesNotMatch(text, /RecordAppendFailure\(/, `${file} must count an append failure only through the attributing owner`);
  }
});

test("the off-main dispatch and entry path never reads main-thread-only Unity state", async () => {
  const runtime = await source(debugRoot, "DebugProgramHost.Runtime.cs");
  const support = await source(debugRoot, "DebugProgramHost.Support.cs");
  const session = await source(debugRoot, "DebugSessionHost.cs");

  // 这些成员会在命中线程（可能是 worker）上同步执行：它们不得直接读取任何主线程专属 Unity 状态，
  // 只能使用主线程已发布的发布值或条件读取。
  const offMainMembers: Array<[string, string]> = [
    [runtime, "internal static bool DispatchMethod("],
    [runtime, "internal static void DispatchSignal("],
    [runtime, "internal static void DispatchDeliveriesForTarget("],
    [runtime, "internal static void DispatchFrameworkDeliveries("],
    [runtime, "private static void ExecuteDelivery("],
    [runtime, "private static void CompleteDispatchRun("],
    [runtime, "private static UnityCliDebugProgramRun ClaimDispatchRun("],
    [runtime, "private static void ClaimPointRun("],
    [runtime, "private static bool MatchesSite("],
    [support, "private static void ReleaseRunResource("],
    [support, "private static void AppendProgramEvent("],
    [support, "private static void AppendLifecycle("],
    [support, "private static void RecordFault("],
    [session, "internal static bool ReleaseResource("],
    [session, "internal static string RegisterCleanupStep("],
    [session, "internal static string RuntimeId()"],
  ];
  for (const [text, name] of offMainMembers) {
    const body = memberBody(text, name);
    assert.doesNotMatch(body, /EditorApplication\.|Time\.frameCount|UnityEngine\.Time/, `${name} runs on the hitting thread and must not read main-thread-only Unity state`);
  }

  // 离主线程路径需要的 Play 状态由主线程发布；唯一读取点必须由主线程证明守卫。
  const sync = memberBody(session, "private static void SyncObservedState()");
  assert.equal((sync.match(/EditorApplication\./g) ?? []).length, 1, "the Play state may be read exactly once, behind the main-thread proof");
  assert.match(sync, /if \(DebugEventClocks\.IsCurrentThreadMainThread\(\)\) observedIsPlaying = EditorApplication\.isPlaying;/);
  assert.match(sync, /var playing = observedIsPlaying;/, "off-main callers must use the published value instead of reading Unity state");
  assert.match(session, /private static volatile bool observedIsPlaying;/);
  assert.equal((session.match(/observedIsPlaying = /g) ?? []).length, 1, "the Play state may only be published on a proven main thread");
  assert.match(memberBody(session, "internal static string RuntimeId()"), /return observedRuntimeSegment;/);
});

test("the append critical section performs only bookkeeping", async () => {
  const shared = await source(sharedRoot, "UnityCliDebugEvents.cs");
  const append = memberBody(shared, "public long Append(UnityCliDebugEvent entry)");
  const lockStart = append.lastIndexOf("lock (gate)");
  assert.ok(lockStart > 0, "the append must keep a short critical section");
  const section = append.slice(lockStart);

  for (const forbidden of [/File\./, /Directory\./, /UnityCliSerializer/, /UnityEngine/, /DebugEventClocks\./]) {
    assert.doesNotMatch(section, forbidden, `the append critical section must not do ${forbidden}`);
  }
  assert.match(append.slice(0, lockStart), /Materialize\(entry\)/, "projection materialisation must stay outside the critical section");
  assert.match(shared, /private void Materialize\(UnityCliDebugEvent entry\)/);
  assert.match(section, /Trim\(\)/);
  assert.doesNotMatch(shared, /Thread\.Sleep/);
});

test("debug.events-read is the only managed debug event reader", async () => {
  const reader = await source(new URL("Editor/Tools/debug/", unitypkg), "events-read.cs");
  const consoleRead = await source(new URL("Editor/Tools/debug/", unitypkg), "console-read.cs");

  assert.match(reader, /IUnityCliTool<DebugEventsRequest, DebugEventsResult>/);
  assert.match(reader, /Name = "debug\.events-read"/);
  assert.match(reader, /DestructiveHint = false/);
  assert.match(reader, /PlayModeSupport = "supported"/);
  assert.doesNotMatch(reader, /DestructiveHint = true|SupportsDryRun = true|SideEffects =/);
  assert.match(reader, /DebugEventStore\.ReadEvents\(/);
  assert.match(reader, /ctx\.Wait\.Until/);
  assert.match(reader, /ReadBudget\.ResumeAfterWait\(\)/);

  const readers: string[] = [];
  for (const file of await csharpFiles(new URL("Editor/", unitypkg))) {
    const text = await source(new URL("Editor/", unitypkg), file);
    if (/DebugEventStore\.ReadEvents\(/.test(text)) readers.push(file);
  }
  // T-14：主线程暂停时 debug.events-read 也必须可达，因此除了 read Tool 之外，只有暂停控制通道可以调用同一读取入口。
  assert.deepEqual(readers, ["Infrastructure/Debug/DebugPauseControl.cs", "Tools/debug/events-read.cs"], "only the read Tool and the pause control channel may call the single store read path");
  const store = await source(debugRoot, "DebugEventStore.cs");
  assert.equal((store.match(/internal static DebugEventsResult ReadEvents\(/g) ?? []).length, 1, "the store must own exactly one read entry point");

  const stores: string[] = [];
  for (const file of await csharpFiles(new URL("Editor/", unitypkg))) {
    const text = await source(new URL("Editor/", unitypkg), file);
    if (/new UnityCliDebugEventBuffer\(/.test(text)) stores.push(file);
  }
  assert.deepEqual(stores, ["Infrastructure/Debug/DebugEventStore.cs"], "the unified store must have a single owner");
  assert.doesNotMatch(consoleRead, /DebugEventStore|DebugEventsRequest/);
});

test("cursor status is reduced to the frozen stable error codes", async () => {
  const store = await source(debugRoot, "DebugEventStore.cs");
  const shared = await source(sharedRoot, "UnityCliDebugEvents.cs");

  assert.match(store, /"E_CONTINUATION_STALE"/);
  assert.match(store, /"E_HOOK_EVENT_CURSOR_EXPIRED"/);
  assert.match(store, /oldestAvailableCursor/);
  assert.doesNotMatch(store, /E_HOOK_EVENT_CURSOR_ERROR|E_DEBUG_EVENT_/);
  assert.match(shared, /page\.CursorStatus = "stale"/);
  assert.match(shared, /page\.CursorStatus = "expired"/);
  assert.match(shared, /UnityCliContinuationToken\.TryParse/);
});

test("the unified read DTO exposes the required unified fields and keeps hook field names", async () => {
  const dtos = await source(debugRoot, "DebugEventDtos.cs");
  for (const member of [
    "Sequence", "SourceId", "Kind", "HookId", "Revision", "InvocationId", "ThreadId", "MainThread",
    "ExecutionPoint", "ClockKind", "ClockValue", "Frame", "LastObservedMainFrame", "PlaySessionId",
    "ClockSegment", "ElapsedMilliseconds", "Sampled", "SampleEvery", "CallbackPending", "Instance",
    "MethodIdentity", "Arguments", "Result", "Fields", "Name", "Value", "FaultCode", "FaultMessage",
    "Truncated", "ProjectionTruncated",
  ]) {
    assert.match(dtos, new RegExp(`public [A-Za-z<>?., ]+ ${member} \\{ get; set; \\}`), `DebugEventItem must expose ${member}`);
  }
  const itemMembers = (dtos.slice(dtos.indexOf("class DebugEventItem"), dtos.indexOf("class DebugEventsResult")).match(/\{ get; set; \}/g) ?? []).length;
  const limits = await source(debugRoot, "DebugLimits.cs");
  assert.match(limits, new RegExp(`EventItemMembers = ${itemMembers}`), "the derived read budget must track the published event item");

  for (const field of ["SourceId", "Kind", "HookId", "PlaySessionId", "ThreadId", "FromClockValue", "ToClockValue", "Limit", "Cursor", "WaitMs"]) {
    assert.match(dtos, new RegExp(`public [A-Za-z<>?., ]+ ${field} \\{ get; set; \\}`), `DebugEventsRequest must expose ${field}`);
  }
  for (const field of ["Events", "NextCursor", "HasMore", "OldestAvailableCursor", "OldestAvailableSequence", "LatestSequence", "Dropped", "AppendFailures", "Sampled", "SampleEvery", "Truncated", "Coverage", "Gaps", "EditorSessionId", "WaitedMilliseconds"]) {
    assert.match(dtos, new RegExp(`public [A-Za-z<>?., ]+ ${field} \\{ get; set; \\}`), `DebugEventsResult must expose ${field}`);
  }
  assert.match(dtos, /UnityCliDebugGap/);
});

test("the read Tool self-describes its scope and its difference from the console reader", async () => {
  const reader = await source(new URL("Editor/Tools/debug/", unitypkg), "events-read.cs");
  for (const phrase of ["hook", "lifecycle", "program", "log", "check", "pause", "input", "debug.console-read", "not for console text", "E_HOOK_EVENT_CURSOR_EXPIRED", "E_CONTINUATION_STALE", "waitMs"]) {
    assert.ok(reader.includes(phrase), `describe must state ${phrase}`);
  }
  assert.match(reader, /DebugLimits\.EventsDefaultLimit/);
  assert.match(reader, /DebugLimits\.EventsMaxLimit/);
  assert.match(reader, /DebugLimits\.EventsMaxWaitMs/);
});
