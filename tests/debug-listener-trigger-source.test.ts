import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const repoRoot = process.cwd();
const unitypkg = new URL("../unitypkg/", import.meta.url);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const hooksRoot = new URL("Editor/Infrastructure/MethodHooks/", unitypkg);
const hookToolsRoot = new URL("Editor/Tools/debug/hook/", unitypkg);

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

test("the trigger and delivery budgets are declared only in DebugLimits", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  for (const value of [
    "MaxPlannedInjectionsPerSession = 1024",
    "MaxListenerConditions = 8",
    "MaxListenerHits = 1000000",
    "MaxSignalRecursionDepth = 16",
    "MaxDeliveriesPerTick = 8",
    "SchedulerTickSamples = 32",
    "SchedulerTickOverrunMs = 4",
  ]) {
    assert.ok(limits.includes(value), `DebugLimits must declare ${value}`);
  }
  const summary = memberBody(limits, "internal static string TriggerSummary()");
  for (const name of ["MaxListenerConditions", "MaxListenerHits", "MaxSignalRecursionDepth", "MaxPlannedInjectionsPerSession", "MaxDeliveriesPerTick", "SchedulerTickSamples", "SchedulerTickOverrunMs"]) {
    assert.ok(summary.includes(name), `TriggerSummary() must publish ${name}`);
  }
  const LIMIT_DECLARATION = /(?:const|static readonly)\s+int\s+(?:MaxSessions|MaxDefinitions|MaxPlanned|MaxListener|MaxDeliveries|MaxSignal|SchedulerTick|MaxEvents|MaxThread|MaxFrames|MaxProgram|MaxHandles|MaxNamed)\w* = \d+/;
  for (const file of ["DebugTriggers.cs", "DebugListenerHost.cs", "DebugScheduler.cs"]) {
    const text = await source(debugRoot, file);
    assert.doesNotMatch(text, LIMIT_DECLARATION, `${file} must not restate a debug limit`);
  }
  for (const file of ["MethodHookHost.Listener.cs", "MethodHookHost.Dispatch.cs", "MethodHookHost.PhaseListener.cs"]) {
    const text = await source(hooksRoot, file);
    assert.doesNotMatch(text, LIMIT_DECLARATION, `${file} must not restate a debug limit`);
  }
});

test("every trigger source is wired to one owner and the Player phase is refused in Edit mode", async () => {
  const triggers = await source(debugRoot, "DebugTriggers.cs");
  for (const name of ["EditorTick", "PlayerLoopPhase", "MethodHit", "Log", "HostSignal"]) {
    assert.match(triggers, new RegExp(`internal const string ${name} = "`), `the trigger vocabulary must declare ${name}`);
  }
  assert.match(triggers, /internal const string PlayerLoopPhaseUnavailableCode = "E_DEBUG_PROGRAM_SITE_UNAVAILABLE"/, "the refusal code has one owner");
  assert.match(triggers, /internal static BridgeCommandException PlayerLoopPhaseUnavailable\(/, "the refusal has one implementation");

  // (a) Editor tick 与 (c) exact Hook 执行点、(e) host signal 都来自已有基础设施。
  const scheduler = await source(debugRoot, "DebugScheduler.cs");
  assert.match(scheduler, /internal static void Tick\(\)/);
  assert.doesNotMatch(scheduler, /EditorApplication\.update \+=|EditorApplication\.update -=/, "the scheduler must never register a private update callback");
  const dispatch = await source(hooksRoot, "MethodHookHost.Dispatch.cs");
  assert.match(dispatch, /internal static bool DispatchPrefixCore\(/);
  // (b) PlayerLoop phase 由中央框架的唯一 owner 安装，并在 Edit 模式用同一错误码拒绝。
  const phaseOwner = await source(debugRoot, "DebugPlayerLoopHost.cs");
  assert.match(phaseOwner, /PlayerLoop\.GetCurrentPlayerLoop\(\)/);
  assert.match(phaseOwner, /PlayerLoop\.SetPlayerLoop\(loop\)/);
  assert.match(phaseOwner, /throw DebugTriggerSources\.PlayerLoopPhaseUnavailable\(/, "the Edit-mode refusal is implemented once for both callers");
  const programHost = await source(debugRoot, "DebugProgramHost.cs");
  assert.match(programHost, /DebugPlayerLoopHost\.RequirePlayDomain\(/, "program execution sites use the shared phase gate");
  const listener = await source(hooksRoot, "MethodHookHost.Listener.cs");
  assert.match(listener, /DebugPlayerLoopHost\.RequirePlayDomain\(/, "listener triggers use the same shared phase gate");

  // (d) 线程化日志 callback 是唯一订阅，并且记录真实 callback 线程。
  const logOwner = await source(debugRoot, "DebugListenerHost.cs");
  assert.equal((logOwner.match(/Application\.logMessageReceivedThreaded \+=/g) ?? []).length, 1, "exactly one log subscription owner");
  assert.match(logOwner, /var threadId = Thread\.CurrentThread\.ManagedThreadId;/);
  assert.match(logOwner, /MainThread = false/);
  assert.match(logOwner, /DebugScheduler\.ResumeWaits\(settings\.Signal\)/, "a matched log condition publishes a host signal");
  const editorRoot = new URL("Editor/", unitypkg);
  for (const file of await files(editorRoot, ".cs")) {
    if (file.endsWith("Infrastructure/Debug/DebugListenerHost.cs")) continue;
    const text = await source(editorRoot, file);
    assert.ok(!text.includes("logMessageReceivedThreaded"), `Editor/${file} must not register a second log subscription`);
  }
});

test("the PlayerLoop phase source is owned, closed and only installed by the framework", async () => {
  const phaseOwner = await source(debugRoot, "DebugPlayerLoopHost.cs");
  // phase 集合封闭：声明只能来自唯一 owner，且 install 时校验。
  assert.match(phaseOwner, /internal static class DebugPlayerLoopPhases[\s\S]*?internal static bool IsDefined\(string value\)/);
  for (const name of ["before-update", "after-update", "fixed-update", "late-update"]) {
    assert.ok(phaseOwner.includes(`"${name}"`), `the closed phase set must declare ${name}`);
  }
  assert.match(phaseOwner, /internal static string\[\] All\(\)/, "the phase owner publishes its closed set");
  assert.match(phaseOwner, /internal static string Install\(string phase, string ownerId\)/);
  assert.match(phaseOwner, /internal static bool Release\(string ownerId\)/);
  assert.match(phaseOwner, /internal static bool ReleaseAll\(string reason\)/);
  // 插入/移除只匹配本框架的标记类型，并且只推入同一中央 scheduler。
  assert.match(phaseOwner, /private static Type MarkerOf\(string phase\)/);
  assert.match(phaseOwner, /DebugScheduler\.AdvancePhase\(phase\)/);
  assert.match(phaseOwner, /if \(subsystem\.type == marker\) continue;/);
  assert.doesNotMatch(phaseOwner, /EditorApplication\.update \+=|EditorApplication\.update -=/, "the phase owner must never register an update callback");
  assert.doesNotMatch(phaseOwner, /EditorApplication\.(Exit|Quit)|Application\.Quit/, "the phase owner must never ask the Editor to quit");

  // 没有第二个 owner：整个 Editor 侧只允许这一处插入/移除 PlayerLoop 子系统。
  const editorRoot = new URL("Editor/", unitypkg);
  const insertions: string[] = [];
  for (const file of await files(editorRoot, ".cs")) {
    const text = await source(editorRoot, file);
    if (text.includes("PlayerLoop.SetPlayerLoop") || text.includes("PlayerLoopSystem")) insertions.push(file);
  }
  assert.deepEqual(insertions, ["Infrastructure/Debug/DebugPlayerLoopHost.cs"], "the central framework is the only PlayerLoop injection owner");
  for (const root of ["Tools/"]) {
    for (const file of await files(editorRoot, ".cs")) {
      if (!file.startsWith(root)) continue;
      const text = await source(editorRoot, file);
      assert.ok(!text.includes("EditorApplication.update"), `Editor/${file} must not register its own update callback`);
      assert.ok(!text.includes("PlayerLoop.SetPlayerLoop"), `Editor/${file} must not inject a PlayerLoop system`);
    }
  }
});

test("in-place and cross-thread delivery stay separate paths", async () => {
  const dispatch = await source(hooksRoot, "MethodHookHost.Dispatch.cs");
  const prefix = memberBody(dispatch, "internal static bool DispatchPrefixCore(");
  const postfix = memberBody(dispatch, "internal static void DispatchPostfixCore(");
  const finalizer = memberBody(dispatch, "internal static void DispatchFinalizerCore(");
  // 只有当场路径可以改参数与返回值：暂存在 prefix 提交，postfix 只串联结果。
  assert.match(prefix, /writeResult = true;/);
  assert.match(postfix, /resultOut = accumulatedResult;/);
  assert.match(finalizer, /AppendOriginalException\(/, "the finalizer records the original exception instead of replacing it");
  assert.doesNotMatch(finalizer, /writeResult|resultOut/, "the finalizer must not intervene on arguments or the result");
  // 跨线程操作在已绑定执行点上原子取得执行权。
  assert.match(prefix, /if \(DebugScheduler\.PendingDeliveryCount > 0\) DispatchTargetDeliveries\(runtime\.TargetKey\);/);
  const listener = await source(hooksRoot, "MethodHookHost.Listener.cs");
  assert.match(listener, /DebugProgramHost\.DispatchDeliveriesForTarget\(targetKey, Thread\.CurrentThread\.ManagedThreadId\)/);

  const scheduler = await source(debugRoot, "DebugScheduler.cs");
  const claim = memberBody(scheduler, "private static Delivery ClaimLocked(");
  assert.match(claim, /if \(!string\.Equals\(delivery\.State, "pending", StringComparison\.Ordinal\)\) return null;/);
  assert.match(claim, /delivery\.State = "running";/);
  assert.match(claim, /delivery\.ExecutionThreadId = threadId;/);
  // 只有一条投递队列：不存在第二条队列或第二个到期扫描。
  assert.equal((scheduler.match(/readonly List<Delivery> Deliveries = new List<Delivery>\(\);/g) ?? []).length, 1);
  assert.match(scheduler, /internal static List<Delivery> TryClaimTargetDeliveries\(/);
  assert.match(scheduler, /internal static List<Delivery> ClaimFrameworkPhaseDeliveries\(/);
  assert.match(scheduler, /internal static Delivery TryClaimDelivery\(/);
  // 过期是终态：只关闭未来条目，绝不补执行。
  const expire = memberBody(scheduler, "private static void ExpireLocked(");
  assert.match(expire, /delivery\.State = "expired";/);
  assert.match(expire, /expiredDeliveries\+\+;/);
  assert.match(expire, /AppendTriggerEvent\(/);
});

test("listener conditions cover hit count, thread, instance and value comparison", async () => {
  const dtos = await source(hookToolsRoot, "MethodHookDtos.cs");
  for (const field of ["public string SessionId { get; set; }", "public string Trigger { get; set; }", "public long? MaxHits { get; set; }", "public int? ThreadId { get; set; }", "public string InstanceIdentity { get; set; }", "public List<MethodHookConditionSpec> Conditions { get; set; }"]) {
    assert.ok(dtos.includes(field), `the listener contract must declare ${field}`);
  }
  assert.match(dtos, /public sealed class MethodHookConditionSpec/);
  const listener = await source(hooksRoot, "MethodHookHost.Listener.cs");
  assert.match(listener, /if \(listener\.TryCountHit\(\)\) return true;/, "finite triggering consumes exactly one hit per accepted call");
  assert.match(listener, /interlocked|Interlocked\.Increment\(ref listenerExhaustedHits\)/i);
  assert.match(listener, /Name = "listener-exhausted"/, "an exhausted one-shot is recorded instead of executing the callback");
  const state = await source(hooksRoot, "MethodHookDispatch.cs");
  assert.match(state, /internal bool Matches\(int hitThreadId, string instanceIdentity, object\[\] arguments, object instance, bool allowUnityIdentity\)/);
  assert.match(state, /internal bool TryCountHit\(\)/);
  assert.match(state, /internal sealed class MethodHookCondition/);
  assert.match(state, /internal static class MethodHookConditionOperators/);
  assert.match(state, /internal static class MethodHookConditionKinds/);
  const evaluator = await source(hooksRoot, "MethodHookHost.Listener.cs");
  assert.match(evaluator, /UnityCliSerializer\.Canonicalize\(projected\)/, "comparison uses the same projection boundary as captured values");
  // 复杂条件用显式调试代码，不是 DSL：条件种类与算子都是闭集。
  assert.match(state, /internal static string\[\] All\(\) \{ return new\[\] \{ Argument, Field \}; \}/);
  assert.match(state, /internal static string\[\] All\(\) \{ return new\[\] \{ Equals, NotEquals \}; \}/);
});

test("one injection budget owner covers listeners and inputs per session", async () => {
  const triggers = await source(debugRoot, "DebugTriggers.cs");
  assert.match(triggers, /internal static class DebugTriggerBudget/);
  for (const api of ["TryPlan", "Release", "PlannedFor", "CountOfKind", "Describe", "Reset"]) {
    assert.match(triggers, new RegExp(`internal static [A-Za-z<>?., \\[\\]]+ ${api}\\(`), `the budget owner must expose ${api}`);
  }
  assert.match(triggers, /used >= DebugLimits\.MaxPlannedInjectionsPerSession/, "the shared cap comes from the single limits owner");
  assert.match(triggers, /internal const string KindListener = "listener";/);
  assert.match(triggers, /internal const string KindInput = "input";/);
  assert.match(triggers, /pendingDeliveries", \(long\)DebugScheduler\.PendingDeliveryCount/, "pending deliveries are read from the single queue owner");
  const operations = await source(hooksRoot, "MethodHookHost.Operations.cs");
  assert.match(operations, /DebugTriggerBudget\.TryPlan\(listener\.SessionId, DebugTriggerBudget\.KindListener, triggerPlanId, out used\)/);
  assert.match(operations, /private static void ReleaseTriggerPlan\(/);
  assert.match(operations, /E_DEBUG_TRIGGER_BUDGET_EXHAUSTED/);
  const sessionHost = await source(debugRoot, "DebugSessionHost.cs");
  assert.match(sessionHost, /DebugTriggerBudget\.Describe\(definition\.SessionId\)/, "session views must report the same ledger");
});

test("per-tick observability reports cost, backlog and overrun", async () => {
  const scheduler = await source(debugRoot, "DebugScheduler.cs");
  const describe = memberBody(scheduler, "internal static Dictionary<string, object> Describe()");
  for (const key of ["backlog", "queueHighWaterMark", "maxDeliveriesPerTick", "expiredLastTick", "targetDeliveriesClaimed", "frameworkDeliveriesClaimed"]) {
    assert.ok(describe.includes(`{ "${key}"`), `Describe() must expose ${key}`);
  }
  assert.match(scheduler, /foreach \(var pair in DebugSchedulerTicks\.Observe\(tickCount, lastTickMilliseconds, maxTickMilliseconds, overrunTicks, TickCosts, signalRecursionRefusals\)\)/);
  assert.match(scheduler, /foreach \(var pair in MethodHookHost\.DispatchObservability\(\)\)/, "hit, sample and drop counts come from the hook dispatch owner");
  const observe = await source(debugRoot, "DebugScheduler.cs");
  for (const key of ["tickCount", "lastTickMilliseconds", "maxTickMilliseconds", "overrunTicks", "tickSamples", "maxTickSamples"]) {
    assert.ok(observe.includes(`{ "${key}"`), `the tick projection must expose ${key}`);
  }
  assert.match(observe, /report\["meanTickMilliseconds"\] = /, "the tick projection must expose meanTickMilliseconds");
  const record = memberBody(scheduler, "private static void RecordTickCost(");
  assert.match(record, /if \(milliseconds > DebugLimits\.SchedulerTickOverrunMs\) overrunTicks\+\+;/);
  assert.match(record, /while \(TickCosts\.Count > DebugLimits\.SchedulerTickSamples\) TickCosts\.Dequeue\(\);/, "tick cost samples stay bounded");
  const tick = memberBody(scheduler, "private static void Advance(string phase)");
  assert.match(tick, /DebugProgramHost\.DispatchFrameworkDeliveries\(phase, Thread\.CurrentThread\.ManagedThreadId, DebugLimits\.MaxDeliveriesPerTick\)/);
  assert.match(tick, /if \(!string\.Equals\(driver\.Phase, phase, StringComparison\.Ordinal\)\) continue;/, "a driver is advanced only on its own declared execution point");
  assert.match(tick, /RecordTickCost\(/, "the tick cost is measured after the bounded work");
  assert.match(scheduler, /internal static bool TryEnterSignalScope\(\)/, "host signal recursion is bounded");
  assert.match(scheduler, /if \(signalDepth >= DebugLimits\.MaxSignalRecursionDepth\)/);
});

test("a program-owned binding refuses every direct hook mutation", async () => {
  const operations = await source(hooksRoot, "MethodHookHost.Operations.cs");
  assert.match(operations, /private static void RequireHookOwner\(string action, MethodHookDefinition definition, string callerOwner\)/);
  assert.match(operations, /throw new BridgeCommandException\(\s*"E_HOOK_OWNED_BY_PROGRAM"/);
  for (const [action, call] of [
    ["replace", "internal static MethodHookMutationPlan PrepareReplace("],
    ["enable", "internal static MethodHookMutationPlan PrepareEnable("],
    ["disable", "internal static MethodHookMutationPlan PrepareDisable("],
    ["remove", "internal static MethodHookMutationPlan PrepareRemove("],
  ] as const) {
    const body = memberBody(operations, call);
    assert.match(body, new RegExp(`RequireHookOwner\\("${action}"`), `${action} must run the ownership guard before any effect`);
  }
  // 只有 program 自己能解除自己的 binding。
  assert.match(memberBody(operations, "internal static MethodHookMutationPlan PrepareRemove("), /RequireHookOwner\("remove", definition, callerOwner\);/);
  const support = await source(debugRoot, "DebugProgramHost.Support.cs");
  assert.match(support, /MethodHookHost\.PrepareInstall\(hook, ProgramOwnerOf\(plan\.ProgramId\)\)/);
  assert.match(support, /\}, ProgramOwnerOf\(programId\)\);/, "program detach passes its own owner identity");
  assert.match(support, /internal static string ProgramOwnerOf\(string programId\)/);
  const definition = await source(hooksRoot, "MethodHookDefinition.cs");
  assert.match(definition, /internal string Owner = string\.Empty;/);
  assert.match(definition, /internal string TriggerOwnerId = string\.Empty;/);
  assert.doesNotMatch(memberBody(definition, "internal Dictionary<string, object> ToData()"), /Owner/, "program ownership is never persisted across a reload");
  const host = await source(hooksRoot, "MethodHookHost.cs");
  assert.match(host, /definition\.Owner = string\.Empty;/, "a restored definition must not keep the previous startup's owner");
  assert.match(host, /private static void RestoreListener\(MethodHookDefinition definition\)/);
  assert.match(host, /E_HOOK_LISTENER_NOT_REARMED/, "a bounded listener is not rearmed after a domain reload");
  assert.match(host, /DebugTriggerBudget\.Reset\(\);/, "trigger state belongs to one Editor startup");
  assert.match(host, /DebugListenerHost\.Reset\(\);/);
});

test("the cross-thread signal target must be bound before it can execute", async () => {
  const support = await source(debugRoot, "DebugProgramHost.Support.cs");
  assert.match(support, /private static void RequireSignalExecutionPoint\(/);
  assert.match(support, /E_DEBUG_PROGRAM_SIGNAL_TARGET_UNBOUND/);
  assert.match(support, /MethodHookHost\.IsTargetBound\(binding\.TargetKey\)/);
  assert.match(support, /TargetKey = string\.Equals\(spec\.Phase, UnityCliDebugProgramBindingPhases\.Signal, StringComparison\.Ordinal\) && spec\.Target != null/, "a signal binding resolves its target key without installing a detour");
  const runtime = await source(debugRoot, "DebugProgramHost.Runtime.cs");
  assert.match(runtime, /internal static void DispatchDeliveriesForTarget\(string targetKey, int threadId\)/);
  assert.match(runtime, /internal static void DispatchFrameworkDeliveries\(string phase, int threadId, int max\)/, "a framework delivery is dispatched by the execution point that declares it");
  assert.match(runtime, /playerLoopPhase \? "trigger:" \+ DebugTriggerSources\.PlayerLoopPhase \+ ":" \+ frameworkPhase : null/, "a phase delivery records the real phase in executionPoint");
  assert.match(runtime, /private static bool TryEnterDeliveryScope\(\)/);
  assert.match(runtime, /E_DEBUG_SIGNAL_RECURSION/);
  assert.match(runtime, /SignalTargetKey\(definition, Entry\)/, "a scheduled signal carries its declared delivery address");
  assert.match(runtime, /DebugScheduler\.TryEnterSignalScope\(\)/);
});

test("a phase arrival evaluates its listeners and delivers through the single queue owner", async () => {
  const scheduler = await source(debugRoot, "DebugScheduler.cs");
  const advance = memberBody(scheduler, "private static void Advance(string phase)");
  assert.match(advance, /MethodHookHost\.TriggerPhaseListeners\(phase\);/, "the phase arrival path belongs to the hook listener owner");
  assert.match(advance, /DebugProgramHost\.DispatchFrameworkDeliveries\(phase, Thread\.CurrentThread\.ManagedThreadId, DebugLimits\.MaxDeliveriesPerTick\)/, "the same segment claims and executes the queued delivery under the one per-segment budget");
  assert.ok(advance.indexOf("TriggerPhaseListeners") < advance.indexOf("DispatchFrameworkDeliveries"), "a satisfied listener is queued before the segment executes its deliveries");
  // hook listener 与 program delivery 共用同一条队列、同一个取得路径与同一份预算。
  assert.match(scheduler, /internal string HookId = string\.Empty;/, "the single queue carries which owner a framework delivery belongs to");
  assert.equal((scheduler.match(/readonly List<Delivery> Deliveries = new List<Delivery>\(\);/g) ?? []).length, 1, "a hook listener must not add a second queue");
  const runtime = await source(debugRoot, "DebugProgramHost.Runtime.cs");
  const dispatch = memberBody(runtime, "internal static void DispatchFrameworkDeliveries(string phase, int threadId, int max)");
  assert.match(dispatch, /if \(delivery\.HookId\.Length != 0\) MethodHookHost\.ExecutePhaseListenerTrigger\(delivery\);/, "the claimed delivery is executed by its declared owner");
  assert.match(dispatch, /else ExecuteDelivery\(delivery, null\);/, "a program delivery keeps its existing executor");

  const phaseListener = await source(hooksRoot, "MethodHookHost.PhaseListener.cs");
  assert.match(phaseListener, /internal static void TriggerPhaseListeners\(string phase\)/);
  assert.match(phaseListener, /\| !DebugPlayerLoopPhases\.IsDefined\(phase\)\) return;/, "only a real PlayerLoop phase drives this path");
  // 条件、线程与有限命中只有一个实现：phase 路径复用同一 listener 状态对象。
  assert.match(phaseListener, /listener\.Matches\(threadId, identity, null, null, mainThread\)/);
  assert.match(phaseListener, /if \(!listener\.TryCountHit\(\)\)/);
  assert.match(phaseListener, /MethodHookInstanceIdentity\.Of\(null, mainThread\)/, "a phase arrival compares against the same identity implementation as a hit");
  assert.match(phaseListener, /internal static void ExecutePhaseListenerTrigger\(DebugScheduler\.Delivery delivery\)/);
  assert.match(phaseListener, /"trigger:" \+ DebugTriggerSources\.PlayerLoopPhase \+ ":" \+ phase/, "the trigger event names the real phase in its executionPoint");
  assert.match(phaseListener, /ReleaseTriggerPlan\(ownerId\)/, "an exhausted listener releases its site through the existing release path");
  assert.match(phaseListener, /if \(listener\.Exhausted\) ReleaseExhaustedPhaseSite\(binding\);/, "a finite budget releases the phase site when it is exhausted");
  assert.match(phaseListener, /PhaseActionCoverageName = "phase-action-requires-invocation"/, "declared actions that need a method invocation are reported under one stable name");
  assert.match(phaseListener, /DebugScheduler\.EnqueueDelivery\(/, "the delivery goes through the single queue owner");
  assert.doesNotMatch(phaseListener, /EditorApplication\.update \+=|EditorApplication\.update -=|PlayerLoop\.SetPlayerLoop|PlayerLoop\.GetCurrentPlayerLoop|PlayerLoopSystem/, "a hook listener owns no PlayerLoop subsystem and no private update callback");
  assert.doesNotMatch(phaseListener, /new List<Delivery>|readonly Queue</, "a hook listener owns no second queue");
  assert.doesNotMatch(phaseListener, /EditorApplication\.(Exit|Quit)|Application\.Quit/, "no path may ask the Editor to quit");
});

test("hook.list reports what hook.remove requires", async () => {
  const operations = await source(hooksRoot, "MethodHookHost.Operations.cs");
  const list = memberBody(operations, "internal static MethodHookListResult List(");
  assert.match(list, /Items = page\.Select\(definition => PublicItem\(definition, session\)\)\.ToList\(\),/, "every listed item goes through the one self-sufficient projection");
  // 列表自足性由两个读取入口共用的同一投影保证，而不是散在 List 自己的 lambda 里。
  const publicItem = memberBody(operations, "private static MethodHookListItem PublicItem(");
  assert.match(publicItem, /item\.EditorSessionId = session;/, "the list must report the session every mutation call requires");
  assert.match(publicItem, /item\.Listener = SummarizeListener\(definition\.Listener\);/, "the list must project the declared listener instead of leaving trigger and phase empty");
  assert.match(publicItem, /item\.Breakpoint = SummarizeBreakpoint\(definition\);/, "the list must project the declared breakpoint");
  // 只读精查入口必须复用同一份投影：缺一个入口就会让声明过的 listener 在该入口凭空消失（导入后精查看到 null）。
  const info = memberBody(operations, "internal static MethodHookInfoResult Info(");
  assert.match(info, /Listener = SummarizeListener\(definition\.Listener\),/, "debug.hook.info must project the declared listener instead of returning null");
  assert.match(info, /Breakpoint = SummarizeBreakpoint\(definition\),/, "debug.hook.info must project the declared breakpoint");
  const dtos = await source(hookToolsRoot, "MethodHookDtos.cs");
  const item = memberBody(dtos, "public sealed class MethodHookListItem");
  for (const field of ["public string HookId { get; set; }", "public long Revision { get; set; }", "public string EditorSessionId { get; set; }", "public MethodHookListenerSummary Listener { get; set; }"]) {
    assert.ok(item.includes(field), `a listed item must report ${field}`);
  }
  // 列表报告的 revision 与 editorSessionId 正是 remove/replace/enable/disable 要求的输入，不新增别名字段。
  assert.ok(!item.includes("ExpectedRevision"), "the list must not add an alias revision field");
  assert.match(item, /这正是 debug\.hook\.remove\/replace\/enable\/disable 要求的 expectedRevision/);
  const removeTool = await source(hookToolsRoot, "remove.cs");
  assert.match(removeTool, /pass the item's editorSessionId as-is and its revision as expectedRevision/);
  const limits = await source(debugRoot, "DebugLimits.cs");
  assert.match(limits, /internal const int ListItemMembers = 12;/, "the list read budget must track the added DTO member");
});

test("the new trigger error codes are registered once and documented", async () => {
  const output = await readFile(join(repoRoot, "src", "output.ts"), "utf8");
  const docs = await readFile(join(repoRoot, "docs", "error-codes.md"), "utf8");
  for (const code of ["E_DEBUG_SESSION_NOT_OPEN", "E_DEBUG_TRIGGER_BUDGET_EXHAUSTED", "E_DEBUG_SIGNAL_RECURSION", "E_HOOK_OWNED_BY_PROGRAM", "E_HOOK_LISTENER_NOT_REARMED", "E_DEBUG_PLAYER_LOOP_PHASE_UNKNOWN", "E_DEBUG_PLAYER_LOOP_UNAVAILABLE", "E_DEBUG_PHASE_TRIGGER_REQUIRES_INVOCATION"]) {
    assert.match(output, new RegExp(`^  ${code}: "${code}",$`, "m"), `${code} must be registered in ERROR_CODES`);
    assert.ok(docs.includes(`\`${code}\``), `${code} must be documented`);
  }
  const declared = new Set(["E_DEBUG_SESSION_NOT_OPEN", "E_DEBUG_TRIGGER_BUDGET_EXHAUSTED", "E_DEBUG_SIGNAL_RECURSION", "E_HOOK_OWNED_BY_PROGRAM", "E_HOOK_LISTENER_NOT_REARMED", "E_DEBUG_PROGRAM_SITE_UNAVAILABLE", "E_DEBUG_PROGRAM_SIGNAL_TARGET_UNBOUND", "E_DEBUG_PLAYER_LOOP_PHASE_UNKNOWN", "E_DEBUG_PLAYER_LOOP_UNAVAILABLE", "E_DEBUG_PHASE_TRIGGER_REQUIRES_INVOCATION", "E_SCHEMA_INVALID", "E_DEBUG_SESSION_NOT_FOUND"]);
  for (const file of ["DebugTriggers.cs", "DebugListenerHost.cs", "DebugScheduler.cs", "DebugPlayerLoopHost.cs"]) {
    const text = await source(debugRoot, file);
    for (const match of text.matchAll(/"(E_[A-Z0-9_]+)"/g)) {
      assert.ok(declared.has(match[1]), `${file} must only use declared stable codes, found ${match[1]}`);
    }
  }
  const hooks = await source(hooksRoot, "MethodHookHost.Listener.cs");
  for (const match of hooks.matchAll(/"(E_[A-Z0-9_]+)"/g)) {
    assert.ok(declared.has(match[1]), `MethodHookHost.Listener.cs must only use declared stable codes, found ${match[1]}`);
  }
});
