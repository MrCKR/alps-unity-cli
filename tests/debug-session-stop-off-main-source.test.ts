import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const unitypkg = new URL("../unitypkg/", import.meta.url);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const hookRoot = new URL("Editor/Infrastructure/MethodHooks/", unitypkg);
const sessionToolsRoot = new URL("Editor/Tools/debug/session/", unitypkg);
const sharedRoot = new URL("Shared/", unitypkg);

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

/** 提取一个方法/属性的花括号体，用于"只允许在某方法里做某事"的断言。 */
function body(text: string, signature: string): string {
  const start = text.indexOf(signature);
  assert.ok(start > 0, `${signature} must exist`);
  const open = text.indexOf("{", start);
  let depth = 0;
  for (let index = open; index < text.length; index += 1) {
    if (text[index] === "{") depth += 1;
    else if (text[index] === "}") {
      depth -= 1;
      if (depth === 0) return text.slice(open, index + 1);
    }
  }
  throw new Error(`unbalanced body for ${signature}`);
}

/** 某个文件里"声明本次清理需要主线程"的注册点数量：主线程暂停期间必须被推迟的条目才允许这样声明。 */
function mainThreadDeclarations(text: string): number {
  return (text.match(/\n\s*true\);/g) ?? []).length;
}

test("the paused-session stop widening is precise: only the owning session is served off-main", async () => {
  const control = await source(debugRoot, "DebugPauseControl.cs");

  // 放宽的对象只有一个常量化的 Tool 名，且它不在冻结读名单里。
  assert.match(control, /internal const string StopTool = "debug\.session-stop";/, "the widened Tool must have exactly one declared name");
  const allowlist = control.match(/private static readonly HashSet<string> OffMainTools = new HashSet<string>\(StringComparer\.Ordinal\)\s*\{([\s\S]*?)\};/);
  assert.ok(allowlist, "the frozen off-main Tool allowlist must exist");
  assert.ok(!allowlist[1].includes("debug.session-stop"), "stop must not become an unconditional member of the frozen allowlist");
  assert.match(
    body(control, "internal static bool IsReachable(string command, string nestedTool, string nestedInputJson)"),
    /if \(string\.Equals\(nestedTool, StopTool, StringComparison\.Ordinal\)\) return OwnsHeldMainThreadPause\(nestedInputJson\);/,
    "stop must be reachable only through the owning-session predicate",
  );

  // 判定必须是"命名了 sessionId 且与当前暂停现场的 owner 相同"，并且只看已发布的内存事实。
  const owns = body(control, "private static bool OwnsHeldMainThreadPause(");
  assert.match(owns, /TryGetMainThreadPause\(out held\)/, "the predicate must read the live pause publication");
  assert.match(owns, /!string\.IsNullOrEmpty\(sessionId\)/, "a stop without a named owner must stay refused");
  assert.match(owns, /string\.Equals\(sessionId, held\.SessionId, StringComparison\.Ordinal\)/, "the named session must be the site's own owner");
  const sessionIdOf = body(control, "private static string SessionIdOf(");
  assert.match(sessionIdOf, /TryGetValue\("sessionId", out sessionId\)/, "the owner may only come from the explicit sessionId field");
  // 控制通道不得为了这个放宽而读取任何主线程专属 Unity 状态。
  assert.doesNotMatch(control, /EditorApplication\.|PlayerLoop\.|SessionState\./, "the pause control channel must not read main-thread-only Unity state");

  // 服务端与控制通道判定同源：两者脱节时按统一拒绝答复，而不是静默停止别的 session。
  const serve = body(control, "private static object ServeStop(");
  assert.match(serve, /throw MainThreadPaused\("catalog\.editor\.run", StopTool, held\)/, "a diverging ownership decision must use the single rejection path");
  assert.match(serve, /DebugSessionHost\.StopFromPauseControl\(/, "the served stop must route to the session owner");
  assert.match(body(control, "private static object ServeToolResult("), /case "debug\.session-stop":\s*\n\s*return ServeStop\(inputJson\);/, "stop must be served from the same single dispatch switch as the allowlisted reads");
});

test("the off-main stop keeps exactly one stop owner, one cleanup pipeline and one effect barrier", async () => {
  const host = await source(debugRoot, "DebugSessionHost.cs");
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  const tool = await source(sessionToolsRoot, "../session-stop.cs");

  // 控制通道没有第二套 stop：它复用同一 PrepareStop/ApplyStop 与同一报告，只标记"本次服务不在主线程上"。
  const offMain = body(host, "internal static DebugSessionStopResult StopFromPauseControl(");
  assert.match(offMain, /var plan = PrepareStop\(request\);/);
  assert.match(offMain, /return ApplyStop\(plan, true\);/);
  assert.equal((host.match(/session\.Stop\(/g) ?? []).length, 1, "the registry stop may have exactly one caller in the session owner");
  assert.match(body(host, "internal static DebugSessionStopResult ApplyStop("), /session\.Stop\(plan\.SessionId,[\s\S]*?offMainStop\);/, "the off-main flag must reach the single registry stop");

  // 公开 stop Tool 与暂停通道入口都落在同一个 owner 上，并且只跨一次 effect barrier。
  const execute = body(tool, "public ValueTask<DebugSessionStopResult> ExecuteAsync(");
  assert.match(execute, /DebugSessionHost\.PrepareStop\(request\);/);
  assert.match(execute, /ctx\.CommitEffect\(\);/);
  assert.match(execute, /DebugSessionHost\.ApplyStop\(plan\)/);

  // 清理管线只有一个实现与一个调用点；暂停通道不得自行复制清理顺序。
  assert.equal((shared.match(/private UnityCliDebugCleanupReport RunCleanupPipeline\(/g) ?? []).length, 1, "the cleanup pipeline must have exactly one implementation");
  assert.equal((shared.match(/var report = RunCleanupPipeline\(/g) ?? []).length, 1, "the cleanup pipeline must run from exactly one place");
  assert.equal((shared.match(/public UnityCliDebugCleanupReport RunCleanupPipeline\(/g) ?? []).length, 1, "the read-only pipeline projection must stay single");
  const control = await source(debugRoot, "DebugPauseControl.cs");
  assert.doesNotMatch(control, /RunCleanupPipeline|DebugSessionCleanupReport/, "the control channel must not own or re-implement the cleanup pipeline");
});

test("cleanup that needs the main thread is deferred with an exact reason instead of being executed off-main", async () => {
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  const pipeline = body(shared, "private UnityCliDebugCleanupReport RunCleanupPipeline(");
  const stepGuard = pipeline.indexOf("offMainStop && registration.RequiresMainThread");
  const stepRun = pipeline.indexOf("registration.Apply(report)");
  assert.ok(stepGuard > 0 && stepRun > stepGuard, "a main-thread-required cleanup entry must be decided before it can run");
  const resourceGuard = pipeline.indexOf("offMainStop && resource.RequiresMainThread");
  const resourceRun = pipeline.indexOf("resource.Cleanup(report)");
  assert.ok(resourceGuard > 0 && resourceRun > resourceGuard, "a main-thread-required resource cleanup must be decided before it can run");

  // 唯一的推迟点：报告 pending 与精确原因，绝不执行条目。
  const defer = body(shared, "private static void DeferMainThreadEntry(");
  assert.doesNotMatch(defer, /registration\.Apply/, "the deferral point must never run the entry it defers");
  assert.match(defer, /status\.Note = MainThreadDeferredReason;/);
  assert.match(defer, /report\.CleanupPending = true;/);
  assert.match(defer, /status\.Succeeded = false;/);
  assert.match(defer, /report\.PendingDetours\+\+;/, "a deferred detour must be counted as a pending detour, not as removed");
  assert.match(defer, /registration\.Name/, "the report must name the deferred entry");
  assert.match(shared, /public const string MainThreadDeferredReason = "deferred:[^"]*main thread[^"]*";/, "the deferral reason must be a single declared fact");
  assert.match(shared, /report\.UnmanagedFacts\.Add\("resource " \+ resource\.ResourceId[\s\S]{0,120}MainThreadDeferredReason\);/, "a deferred resource cleanup must keep its slot and say why");

  // 每个需要主线程的 owner 必须自己声明；主线程专属 Unity API 是分类的事实依据。
  const playerLoop = await source(debugRoot, "DebugPlayerLoopHost.cs");
  assert.match(playerLoop, /PlayerLoop\.SetPlayerLoop\(loop\);/, "removing the framework PlayerLoop subsystem is main-thread-only");
  const operations = await source(hookRoot, "MethodHookHost.Operations.cs");
  assert.match(operations, /player-loop-phase:" \+ definition\.HookId[\s\S]{0,900}DebugPlayerLoopHost\.Release/, "the PlayerLoop phase entry must release through the main-thread-only owner");
  const programSupport = await source(debugRoot, "DebugProgramHost.Support.cs");
  assert.match(programSupport, /program-detach:" \+ definition\.ProgramId[\s\S]{0,400}DetachBindings\(current\)/, "the program detach entry must be the detour owner");
  const programRuntime = await source(debugRoot, "DebugProgramHost.Runtime.cs");
  assert.match(programRuntime, /program-entry-cleanup:[\s\S]{0,400}cleanup\(\);/, "the explicit cleanup entry must run user cleanup code");
  const programHost = await source(debugRoot, "DebugProgramHost.cs");
  assert.match(programHost, /UnityCliDebugResourceKinds\.Run, plan\.Target, report => CloseProgramOnCleanup\(definition\.ProgramId, report\),\s*\n\s*true\);/, "the run resource closes runs and detaches detours, so its cleanup needs the main thread");
  const valueRestore = await source(debugRoot, "DebugValueRestore.cs");
  assert.match(valueRestore, /restoreOrder, RestoreStepName[\s\S]{0,260}true\);/, "conditional field restore writes business fields, so it needs the main thread");

  for (const [label, text, expected] of [
    ["player-loop phase", operations, 1],
    ["program detach", programSupport, 1],
    ["program explicit cleanup", programRuntime, 1],
    ["run resource", programHost, 1],
    ["value restore", valueRestore, 1],
  ] as const) {
    assert.equal(mainThreadDeclarations(text), expected, `${label} must declare exactly ${expected} main-thread-required cleanup registration(s)`);
  }
  // off-main 安全的清理保持不声明：日志退订与句柄释放可以（也必须）在服务线程上完成。
  assert.equal(mainThreadDeclarations(await source(debugRoot, "DebugSessionHost.cs")), 0, "the log capture release must stay off-main safe");
  assert.equal(mainThreadDeclarations(await source(debugRoot, "DebugPauseHost.cs")), 0, "releasing a pause site is a signal, so its cleanup must stay off-main safe");
});

test("the off-main stop leaves the main-thread-only store publication to a later main-thread write", async () => {
  // 主线程专属的 store 恢复事实（SessionState 两键）曾经也写在 off-main stop 路径上：发布已经落盘，
  // 却在 SetString 上抛 UnityException，调用方拿到 E_BRIDGE_INTERNAL_ERROR，现场却已被改变。
  const store = await source(debugRoot, "DebugSessionStore.cs");

  // 写入只有一个主人，而且它没有被 off-main 路径直接调用。
  const writer = body(store, "private static void WriteRecoveryFacts(");
  assert.match(writer, /SessionState\.SetString\(GenerationSessionKey/, "the generation key must be written by the single writer");
  assert.match(writer, /SessionState\.SetString\(PublicationSessionKey/, "the publication key must be written by the single writer");
  assert.equal((store.match(/SessionState\.SetString/g) ?? []).length, 2, "every SessionState write must sit in the single main-thread writer");
  const publish = body(store, "internal static long Publish(");
  const offMainGuard = publish.indexOf("UnityCliBridge.CatalogOffMainRead");
  const deferral = publish.indexOf("recoveryFactsPending = true;");
  const mainThreadWrite = publish.indexOf("WriteRecoveryFacts(");
  assert.ok(offMainGuard > 0, "the store publication must branch on the off-main service scope");
  assert.ok(deferral > offMainGuard, "the off-main branch must record the deferred facts instead of writing them");
  assert.ok(mainThreadWrite > deferral, "the SessionState writer must be reachable only through the main-thread branch");
  // 推迟是命名事实，不是静默丢弃：报告必须给出条目名与精确原因。
  assert.match(store, /internal static bool RecoveryFactsPending/, "the deferral must be observable to the stop report");
  assert.match(store, /return "pending entry " \+ RecoveryFactsEntryName \+ "[^"]*" \+ RecoveryFactsDeferredReason;/, "the fact text must name the entry and carry the exact reason");
  assert.match(store, /internal const string RecoveryFactsEntryName = "store-recovery-facts";/, "the deferred entry must have exactly one declared name");
  assert.match(store, /internal const string RecoveryFactsDeferredReason = "deferred:[^"]*main-thread-only[^"]*";/, "the deferral reason must be a single declared fact");

  // 唯一 stop 入口在发布之后如实报告未完成的写入；第三方（控制通道）不拥有第二套发布。
  const host = await source(debugRoot, "DebugSessionHost.cs");
  const stop = body(host, "internal static DebugSessionStopResult ApplyStop(");
  assert.match(stop, /Publish\("stop"\)/, "stop must publish through the single store path");
  assert.match(stop, /if \(DebugSessionStore\.RecoveryFactsPending\) outcome\.Report\.UnmanagedFacts\.Add\(DebugSessionStore\.RecoveryFactsDeferredFact\(\)\);/, "a deferred store write must be reported as an unfinished named item");
  assert.equal((host.match(/DebugSessionStore\.Publish\(/g) ?? []).length, 1, "the session owner must keep exactly one publication call site");

  // 控制通道服务 stop 的作用域就是 store 读取的同一个 off-main 判定依据。
  const control = await source(debugRoot, "DebugPauseControl.cs");
  assert.match(body(control, "private static object ServeTool("), /return UnityCliBridge\.ServeOffMainTool\(/, "the off-main stop must run inside the off-main service scope the store reads");
});
