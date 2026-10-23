import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const repoRoot = process.cwd();
const unitypkg = new URL("../unitypkg/", import.meta.url);
const pauseToolsRoot = new URL("Editor/Tools/debug/pause/", unitypkg);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const hookRoot = new URL("Editor/Infrastructure/MethodHooks/", unitypkg);

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

/** 递归读取 unitypkg 下所有 C# 源码，用于"只有唯一 owner"类断言。 */
async function allCSharp(): Promise<Map<string, string>> {
  const { readdir } = await import("node:fs/promises");
  const result = new Map<string, string>();
  const visit = async (directory: URL) => {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
      if (entry.isDirectory()) await visit(child);
      else if (entry.name.endsWith(".cs")) result.set(child.pathname, await readFile(child, "utf8"));
    }
  };
  await visit(unitypkg);
  return result;
}

/** 提取一个方法/属性的花括号体，用于"只在某方法里允许/禁止某调用"的断言。 */
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

const TOOLS = ["list", "wait", "inspect", "evaluate", "resume"];

test("the five debug.pause tools exist with the declared descriptor shape", async () => {
  for (const tool of TOOLS) {
    const text = await source(pauseToolsRoot, `../pause-${tool}.cs`);
    assert.match(text, /IUnityCliTool<DebugPause\w+Request, DebugPause\w+Result>/, `${tool}.cs must implement the typed Tool interface`);
    assert.match(text, new RegExp(`Name = "debug\\.pause-${tool}"`), `${tool}.cs must declare its exact public name`);
    assert.match(text, /Group = "debug"/);
    assert.match(text, /Target = "editor"/);
    assert.match(text, /PlayModeSupport = "supported"/);
    assert.match(text, /DebugLimits\.PauseSummary\(\)/, `${tool}.cs must publish the single limit summary`);
    assert.doesNotMatch(text, /EditorApplication\.update \+=|EditorApplication\.delayCall|new Timer\(/, `${tool}.cs must not register its own update, timer or delay callback`);
  }
  // 证据链与冻结的破坏性语义：evaluate 执行用户代码，resume 只解除线程暂停。
  const evaluate = await source(pauseToolsRoot, "../pause-evaluate.cs");
  assert.match(evaluate, /DestructiveHint = true/);
  assert.match(evaluate, /SideEffects = new\[\] \{ "debug-pause", "user-code-execution" \}/);
  assert.match(evaluate, /ctx\.CommitEffect\(\);/, "debug.pause-evaluate must cross the single effect barrier");
  const resume = await source(pauseToolsRoot, "../pause-resume.cs");
  assert.match(resume, /DestructiveHint = false/);
  assert.match(resume, /SideEffects = new\[\] \{ "debug-pause" \}/);
  assert.match(resume, /ctx\.CommitEffect\(\);/, "debug.pause-resume must cross the single effect barrier");
  for (const reader of ["list", "wait", "inspect"]) {
    const text = await source(pauseToolsRoot, `../pause-${reader}.cs`);
    assert.doesNotMatch(text, /ctx\.CommitEffect\(\)|SideEffects =|DestructiveHint = true/, `${reader}.cs must stay a pure read`);
  }
  // 等待与真实栈读取必须由框架 wait 驱动，而不是私有 scheduler。
  assert.match(await source(pauseToolsRoot, "../pause-wait.cs"), /ctx\.Wait\.Until\(/, "debug.pause-wait must use the framework wait");
  assert.match(await source(pauseToolsRoot, "../pause-inspect.cs"), /ctx\.Wait\.Until\(/);
  assert.match(evaluate, /ctx\.Wait\.Until\(/);
  assert.match(resume, /ctx\.Wait\.Until\(/);
});

test("pause limits are declared once and only in DebugLimits", async () => {
  const names = [
    "MaxPauseSites",
    "MaxPauseStackFrames",
    "MaxPauseMailboxWaiting",
    "MaxPauseMailboxExecuting",
    "PauseServicePollMs",
    "MaxRetainedPauseSites",
    "MaxPauseDurationMs",
    "PauseListDefaultLimit",
    "PauseListMaxLimit",
    "PauseWaitMaxMs",
    "PauseOperationTimeoutMs",
    "MaxPauseInspectFields",
    "PauseCleanupWaitMs",
    "PauseCompilerAcquireMs",
    "PauseMaxWaitForReleaseMs",
  ];
  const files = await allCSharp();
  for (const name of names) {
    const declarers = [...files.entries()]
      .filter(([, text]) => new RegExp(`\\bconst\\s+\\w+\\s+${name}\\b`).test(text))
      .map(([path]) => path);
    assert.deepEqual(
      declarers.map((path) => path.split("/").pop()),
      ["DebugLimits.cs"],
      `${name} must be declared only in DebugLimits.cs`,
    );
  }
  const limits = await source(debugRoot, "DebugLimits.cs");
  assert.match(limits, /internal static string PauseSummary\(\)/, "the pause limit summary must exist");
  // 暂停核心不得自行写数量常量：上限一律从 DebugLimits 读取。
  for (const path of ["DebugPause.cs", "DebugPauseHost.cs", "DebugPauseEvaluation.cs"]) {
    const text = await source(debugRoot, path);
    assert.doesNotMatch(text, /\bconst\s+int\s+Max\w*(Pause|Mailbox|Stack)\w*\s*=/, `${path} must not redeclare a pause limit`);
  }
});

test("pause is a hit behaviour of the hook and reuses one dispatch, store and compiler owner", async () => {
  const dispatch = await source(hookRoot, "MethodHookHost.Dispatch.cs");
  const hits = (dispatch.match(/DebugPauseHost\.Hit\(binding, invocation, arguments/g) ?? []).length;
  assert.equal(hits, 2, "both the prefix and the postfix boundary must route the breakpoint once");
  assert.match(dispatch, /breakpoint = definition\.Breakpoint == null \? null : definition\.Breakpoint\.Clone\(\);/, "the published binding must freeze the breakpoint configuration");
  const operations = await source(hookRoot, "MethodHookHost.Operations.cs");
  assert.match(operations, /DebugPauseHost\.Prepare\(request\.Breakpoint, "breakpoint"\)/, "install and replace must validate the breakpoint before any effect");
  assert.match(operations, /DebugPauseHost\.ReleaseOwnership\(definition, DebugPauseReleaseReasons\.HookDisabled\)/);
  assert.match(operations, /DebugPauseHost\.ReleaseOwnership\(definition, DebugPauseReleaseReasons\.HookRemoved\)/);
  const files = await allCSharp();
  const stores = [...files.entries()].filter(([, text]) => /DebugEventStore\.Append\(/.test(text)).map(([path]) => path.split("/").pop());
  assert.ok(stores.includes("DebugPauseHost.cs"), "pause facts must enter the single unified event store");
  // 没有第二套编译所有权：pause evaluate 只能经 DebugCompilerOwner 的 PauseService lane。
  const evaluation = await source(debugRoot, "DebugPauseEvaluation.cs");
  assert.match(evaluation, /DebugCompilerOwner\.TryBeginOwnershipWithin\(DebugCompilerLane\.PauseService/);
  assert.match(evaluation, /DebugCompilerOwner\.AdvanceOwnedCompilation\(run, DebugCompilerLane\.PauseService\)/);
  assert.match(evaluation, /UnityCliBridge\.WrapCodeSource\(source\)/, "pause evaluate must reuse the shared code wrapper shape");
  assert.doesNotMatch(evaluation, /new EditorApplication/, "the pause service must not create a second scheduler");
});

test("the pause core keeps resume out of the mailbox queue and never defaults the timeout", async () => {
  const host = await source(debugRoot, "DebugPauseHost.cs");
  const loopStart = host.indexOf("private static void ServiceSite(");
  const loopEnd = host.indexOf("private static string TerminalReasonLocked(", loopStart);
  assert.ok(loopStart > 0 && loopEnd > loopStart, "the service loop must exist");
  const loop = host.slice(loopStart, loopEnd);
  const terminalIndex = loop.indexOf("TerminalReasonLocked(site)");
  const dequeueIndex = loop.indexOf("site.Waiting.Dequeue()");
  assert.ok(terminalIndex > 0 && dequeueIndex > terminalIndex, "the resume signal and the bounded deadline must be checked before any waiting request is dequeued");
  assert.match(loop, /site\.Signal\.Wait\(DebugLimits\.PauseServicePollMs\);/, "the paused thread must wait on its own signal with a bounded poll, not on a private update registration");
  const limits = await source(debugRoot, "DebugLimits.cs");
  assert.match(limits, /internal const int MaxPauseSites = 16;/);
  assert.match(limits, /internal const int MaxPauseStackFrames = 64;/);
  assert.match(limits, /internal const int MaxPauseMailboxWaiting = 8;/);
  assert.match(limits, /internal const int MaxPauseMailboxExecuting = 1;/);
  // pauseTimeout 没有默认值：DTO 不为 timeouts 预置值，拒绝逻辑在 host。
  const dtos = await source(pauseToolsRoot, "DebugPauseDtos.cs");
  assert.doesNotMatch(dtos, /PauseTimeout\s*=\s*new/, "the DTO must not carry a default pauseTimeout");
  assert.match(dtos, /public MethodHookPauseTimeoutSpec PauseTimeout \{ get; set; \}/);
  assert.match(host, /a positive integer number of milliseconds/, "the bounded duration must be judged explicitly, so 0 or null is never read as infinite");
  assert.match(host, /omitted when kind is unbounded/, "unbounded must forbid durationMs");
  // 无限暂停的合法解除路径必须可达。
  const pause = await source(debugRoot, "DebugPause.cs");
  for (const reason of ["SessionStop", "HookDisabled", "HookRemoved", "PlayExit", "DomainReload", "EditorShutdown"]) {
    assert.match(pause, new RegExp(`internal const string ${reason} =`), `${reason} must be a declared release reason`);
  }
  const hookHost = await source(hookRoot, "MethodHookHost.cs");
  assert.match(hookHost, /DebugPauseHost\.ReleaseAll\(DebugPauseReleaseReasons\.EditorShutdown\)/);
  assert.match(hookHost, /DebugPauseHost\.ReleaseAll\(DebugPauseReleaseReasons\.PlayExit\)/);
  assert.match(hookHost, /DebugPauseHost\.ReleaseAll\(DebugPauseReleaseReasons\.DomainReload\)/, "a domain reload must release every pause site");
  assert.ok(
    hookHost.indexOf("DebugPauseHost.Reset();") > hookHost.indexOf("DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.DomainReload)"),
    "the reload release must precede the registry reset, otherwise the site is unreachable and its hit thread is abandoned",
  );
  // 生命周期解除只置信号；有界释放确认只允许出现在显式变更路径上。
  assert.doesNotMatch(body(host, "internal static void ReleaseAll(string reason)"), /WaitForRelease\(/, "a lifecycle release must never block the caller on pause release");
  assert.match(body(host, "internal static bool ReleaseSitesForHook("), /WaitForRelease\(/, "the explicit hook mutation path must confirm release with a bounded wait");
  assert.match(hookHost, /DebugPauseHost\.Pump\(\)/, "the main-thread pulse must publish runtime identity and release pending session slots");
});

test("honesty rules are frozen in source: pending is explicit and the pause core owns no control plane", async () => {
  const host = await source(debugRoot, "DebugPauseHost.cs");
  const pendingStart = host.indexOf("internal static DebugPauseEvaluateResult PendingEvaluateResult(");
  assert.ok(pendingStart > 0, "the pending conclusion must have exactly one construction point");
  const pending = host.slice(pendingStart, host.indexOf("internal static DebugPauseOperation EnqueueInspect("));
  assert.match(pending, /ExecutionState = DebugPauseExecutionStates\.Pending/);
  assert.match(pending, /Completed = false/);
  assert.match(pending, /was not resumed/);
  assert.match(pending, /unknown/);
  assert.match(pending, /does not preempt running code/, "the pending report must explicitly deny preemption");
  assert.doesNotMatch(pending, /preemption = true|Preempted|forcibly resumed/i, "the pending report must not claim that the thread was preempted or forcibly resumed");
  assert.match(host, /Locals of a frame other than the paused invocation are not readable from a stack trace/, "other-frame locals must be listed as unavailable");
  assert.match(host, /Other threads, servers, external clocks and object mutation continue/, "the pause must state that only the hit thread is stopped");
  const evaluation = await source(debugRoot, "DebugPauseEvaluation.cs");
  assert.match(evaluation, /Process stdout is not captured or redirected/, "stdout capture must not be claimed");
  // 控制通道由 T-14 独占：pause 核心自身不拥有健康 blocker、也不拥有主线程拒绝。
  for (const path of ["DebugPause.cs", "DebugPauseHost.cs", "DebugPauseEvaluation.cs"]) {
    const text = await source(debugRoot, path);
    assert.doesNotMatch(text, /E_DEBUG_MAIN_THREAD_PAUSED/, `${path} must not own the main-thread rejection`);
    assert.doesNotMatch(text, /debug-pause"|blocker/, `${path} must not publish the health blocker`);
  }
  const files = await allCSharp();
  const mainThreadRejections = [...files.entries()]
    .filter(([path]) => !path.includes("/Tests/"))
    .filter(([, text]) => /E_DEBUG_MAIN_THREAD_PAUSED/.test(text))
    .map(([path]) => path.split("/").pop());
  assert.deepEqual(mainThreadRejections, ["DebugPauseControl.cs"], "the main-thread rejection must have exactly one production owner");
});

test("a compile that did not finish inside the on-site budget keeps its own stable attribution", async () => {
  const output = await readFile(join(repoRoot, "src", "output.ts"), "utf8");
  const docs = await readFile(join(repoRoot, "docs", "error-codes.md"), "utf8");
  const code = "E_COMPILER_BUDGET_EXHAUSTED";
  assert.match(output, new RegExp(`^  ${code}: "${code}",$`, "m"), `${code} must be registered in ERROR_CODES`);
  assert.ok(docs.includes(`\`${code}\``), `${code} must be documented`);
  const limits = await source(debugRoot, "DebugLimits.cs");
  assert.match(limits, /internal const int PauseOperationTimeoutMs = 10000;/, "the on-site operation bound must stay the shared stricter limit");
  const evaluation = await source(debugRoot, "DebugPauseEvaluation.cs");
  // 预算到期只能归因为共享编译没在 on-site 预算内产出结果，不得冒充“用户代码未返回”。
  const runCode = body(evaluation, "private static object RunCode(");
  assert.doesNotMatch(runCode, /"E_EXECUTION_TIMEOUT"/, "a compile that did not finish must never reuse the user-code timeout code");
  assert.match(runCode, /CompileBudgetExceeded\("compile"\)/, "a compile that hit the budget must route to the single compile-budget attribution point");
  assert.match(runCode, /CompileBudgetExceeded\("compile-output"\)/, "a compile without a usable artifact must route to the same attribution point");
  assert.match(runCode, /DebugLimits\.PauseOperationTimeoutMs/, "the on-site bound must stay the shared stricter limit");
  // 引用解析与编译共用同一预算：解析阶段到期也走同一稳定码。
  assert.match(evaluation, /private static List<string> ResolveReferencesWithinBudget\(Func<bool> deadlineOk\)/);
  assert.match(evaluation, /DebugCompilerOwner\.References\(null, null, deadlineOk\)/);
  // 归因点只有一个，且必须给出可执行的恢复路径：先确认现场仍可达，再用非暂停路径预热共享编译。
  const attribution = body(evaluation, "private static BridgeCommandException CompileBudgetExceeded(");
  assert.match(attribution, new RegExp(`"${code}"`), `${code} must be the code produced at the single attribution point`);
  assert.match(attribution, /DebugLimits\.PauseOperationTimeoutMs/, "the reported budget must come from the single limits owner");
  assert.match(attribution, /debug\.pause-list/);
  assert.match(attribution, /debug\.code-execute/);
  assert.match(attribution, /debug\.pause-resume/);
  assert.match(attribution, /not a compile failure and not user code that failed to return/);
});

test("the main-thread pause control channel is frozen, published before any pause resource, and keeps the queue out of the path", async () => {
  const control = await source(debugRoot, "DebugPauseControl.cs");
  // 可达集合必须是冻结名单：逐条列出，不靠前缀或语义推断。
  for (const tool of ["debug.pause-list", "debug.pause-wait", "debug.pause-inspect", "debug.pause-evaluate", "debug.pause-resume", "debug.events-read", "debug.thread-list", "debug.session-info", "debug.session-list", "debug.program-info", "debug.program-list", "debug.hook-info", "debug.hook-list"]) {
    assert.match(control, new RegExp(`"${tool.replace(/\./g, "\\.")}"`), `${tool} must be listed in the frozen off-main allowlist`);
  }
  assert.match(control, /internal static void Activate\(\)/, "the channel must have one publication point");
  assert.match(control, /OffMainEditorStateSnapshot/, "editor.state must be served from the pause publication while the main thread is held");
  assert.match(control, /foreach \(var name in new\[\] \{ "compile", "import", "build", "playMode", "prefabStage", "activeScene", "runtime" \}\)\n\s+fields\[name\] = new Dictionary<string, object> \{ \{ "known", false \} \};/, "a paused snapshot must report every Unity field as unknown");
  assert.match(control, /pause never writes the pulse|DebugMainThreadPauseView/, "the pause publication must be separate evidence");
  // 解除路径必须先于任何 pause 资源安装存在。
  const host = await source(debugRoot, "DebugPauseHost.cs");
  assert.match(host, /DebugPauseControl\.Available/, "a pause breakpoint must be refused while the control channel is unpublished");
  assert.match(host, /E_DEBUG_CONTROL_PLANE_UNAVAILABLE/);
  // 闸门必须先于编译/导入/PlayMode 缓存闸门，并且必须在入队之前。
  const http = await source(new URL("Editor/Bridge/", unitypkg), "BridgeHttpServer.cs");
  const pauseGate = http.indexOf("DebugPauseControl.TryGetMainThreadPause(out mainThreadPause)");
  const compileGate = http.indexOf("if (cachedIsCompiling && !allowedDuringCompileOrImport)");
  const enqueue = http.indexOf("EnqueueOrAttach(context.Request, request, rawBody");
  assert.ok(pauseGate > 0 && compileGate > pauseGate, "the pause gate must precede the compile/import gates");
  assert.ok(enqueue > pauseGate, "the pause gate must decide before the request can be queued behind the paused main thread");
  // health 将暂停作为独立证据发布，不改写 pulse。
  assert.match(http, /\{ "debugPause", pause == null \? null : pause\.ToData\(\) \}/, "health must publish the live pause publication");
  assert.match(http, /DebugPauseControl\.TryGetMainThreadPause\(out mainThreadPause\)/, "the HTTP path must own the single pause gate");
  // 闸门只在请求进入的那一刻判定；暂停发布之前就已入队的请求必须在等待期间用同一名单重新判定一次，
  // 否则它只能等现场释放后才被执行——有限暂停此时已经自动解除，调用方看不到任何暂停事实。
  assert.match(http, /internal static bool WaitForQueuedStart\(BridgeWorkItem item, out int queueSignal, out string pauseServed\)/, "the queued wait must re-evaluate the pause gate");
  assert.match(http, /WaitForQueuedStart\(item, out queueSignal, out pauseServed\)\)\n\s+\{/, "the request path must divert an already-queued reachable request");
  assert.match(
    body(control, "internal static bool TryServeQueuedRequest("),
    /if \(!IsReachable\(request, out nestedTool, out nestedInput\)\) return false;/,
    "a queued request may only be taken over through the same frozen allowlist",
  );
  assert.match(
    body(control, "internal static bool TryServeQueuedRequest("),
    /if \(!item\.CancelBeforeStart\(\)\) return false;/,
    "a request that already started must never be answered by the control channel",
  );
  assert.match(
    body(control, "internal static bool TryServeQueuedRequest("),
    /item\.Response = envelope;/,
    "the queue item must keep the control-channel answer so it can never execute a second time",
  );
  const bridge = await source(unitypkg, "Editor/UnityCliBridge.cs");
  assert.match(bridge, /DebugPauseControl\.Activate\(\)/, "startup must publish the channel before the listener accepts requests");
  // 主线程暂停期间的目录可用性不得从后台线程调用 Unity API。
  const registry = await source(new URL("Editor/ProjectTools/", unitypkg), "ProjectToolRegistry.cs");
  assert.match(registry, /IsPlayingNow\(\)/);
  assert.match(registry, /catalogOffMainRead \? cachedIsPlaying : EditorApplication\.isPlaying/);
});

test("F-16 makes debug.hook-list self-sufficient and F-17 treats a pause breakpoint as an intervention owner", async () => {
  const dtos = await source(new URL("Editor/Tools/debug/hook/", unitypkg), "MethodHookDtos.cs");
  const itemStart = dtos.indexOf("public sealed class MethodHookListItem");
  const item = dtos.slice(itemStart, dtos.indexOf("public sealed class MethodHookListResult", itemStart));
  assert.match(item, /public string EditorSessionId \{ get; set; \}/, "F-16: the list must carry the editorSessionId that remove/replace require");
  assert.match(item, /public MethodHookBreakpointSummary Breakpoint \{ get; set; \}/, "F-16: the list must carry the declared breakpoint summary");
  const operations = await source(hookRoot, "MethodHookHost.Operations.cs");
  assert.match(operations, /item\.EditorSessionId = session;/);
  assert.match(operations, /item\.Breakpoint = SummarizeBreakpoint\(definition\);/);
  // F-17：pause 断点与回调体共用同一任意干预权，同一 exact target 上不能并存两个 owner。
  assert.match(operations, /private static bool IsPauseBreakpoint\(MethodHookBreakpoint breakpoint\)/);
  assert.match(operations, /return hasCallbacks \|\| IsPauseBreakpoint\(breakpoint\);/);
  assert.match(operations, /OwnsArbitraryIntervention\(callbacks != null[\s\S]{0,160}request\.Breakpoint\)/, "install must treat a pause breakpoint as an intervention owner");
  assert.match(operations, /\{ "ownerKind", kind \}/, "the conflict must name whether the existing owner is a callback or a pause breakpoint");
  const limits = await source(debugRoot, "DebugLimits.cs");
  assert.match(limits, /internal const int ListItemMembers = 12;/, "the list read budget must track every DTO member");
});

test("the off-main Tool success terminal reuses the normal catalog.editor.run terminal builder", async () => {
  // 公开 CLI 只把携带 result 包装的 catalog.editor.run 成功响应当成业务 DTO。暂停通道的 Tool 路径
  // 曾经把业务 DTO 本身当作终态，于是已经生效的 resume 被判成 malformed terminal；终态必须只有一个构造点。
  const control = await source(debugRoot, "DebugPauseControl.cs");
  assert.match(
    body(control, "private static object ServeTool("),
    /UnityCliBridge\.OffMainToolTerminalData\(request, definition, ServeToolResult\(/,
    "the off-main Tool path must wrap its result with the shared terminal builder inside the frozen mode scope",
  );
  assert.doesNotMatch(
    body(control, "private static object ServeToolResult("),
    /SerializeOffMainSuccess|EnvelopeFactory/,
    "the pause channel must not serialize an envelope of its own",
  );
  const bridge = await source(new URL("Editor/Bridge/", unitypkg), "UnityCliBridge.PauseControl.cs");
  assert.match(
    body(bridge, "internal static object OffMainToolTerminalData("),
    /return CompleteProjectToolSuccess\(request, definition, result\);/,
    "the off-main terminal must come from the same single construction point as the normal run path",
  );
  const registry = await source(new URL("Editor/ProjectTools/", unitypkg), "ProjectToolRegistry.cs");
  assert.match(registry, /internal static object CompleteProjectToolSuccess\(/);
  assert.equal(
    (registry.match(/CompleteProjectToolSuccess/g) ?? []).length,
    2,
    "the normal run terminal must keep exactly one construction point and its single ordinary call site",
  );
});

test("no production source can reach a Unity quit, close or exit API from any pause or shutdown path", async () => {
  // F-19：主线程暂停期注到有序 Application.Shutdown，必须先冻结"产品永远不是退出请求的发出者"。
  // `EditorApplication.quitting` 只是退出通知，产品只能清理；任何退出/关闭/强制结束调用都不得出现在生产源码。
  const quitApis = /EditorApplication\.(Exit|Quit|Close|ExitPlaymode)\s*\(|Application\.Quit\s*\(|Environment\.Exit\s*\(|CloseMainWindow\s*\(|Process\.GetCurrentProcess\(\)\.Kill\s*\(/;
  const files = await allCSharp();
  const production = [...files.entries()].filter(([path]) => !path.includes("/Tests/"));
  const offenders = production.filter(([, text]) => quitApis.test(text)).map(([path]) => path.split("/").pop());
  assert.deepEqual(offenders, [], "the product must never ask Unity Editor to quit, close, exit or force-terminate a process");
  // Play 模式退出是已冻结的公开能力，只允许它有这个赋值；暂停/清理路径一个也不得触碰。
  const playModeOwners = production.filter(([, text]) => /EditorApplication\.isPlaying\s*=\s*false/.test(text)).map(([path]) => path.split("/").pop());
  assert.deepEqual(playModeOwners, ["play-mode-stop.cs"], "only editor.play-mode-stop may toggle EditorApplication.isPlaying");
  // 退出通知里的清理顺序：先解除暂停现场，再清定义与状态，不得留下已释放但未终结的现场。
  const bridge = await source(unitypkg, "Editor/UnityCliBridge.cs");
  const quitHandler = body(bridge, "private static void UnregisterAndStop()");
  assert.ok(quitHandler.indexOf("MethodHookHost.Shutdown()") > 0, "the quit notification must route to the hook host shutdown");
  assert.doesNotMatch(quitHandler, quitApis, "the quit notification must only clean up, never request another quit");
});

test("the off-main control channel never reads main-thread-only Editor state and stays traceable when it fails", async () => {
  // debug.session.* 是白名单里唯一会越过 session store 的读取；那里曾经仍有主线程专属 SessionState 读取，
  // 使 off-main 服务在真正服务任何事实之前就抛 UnityException。SessionState 只允许在主线程读，并把结果发布给其它线程。
  const store = await source(debugRoot, "DebugSessionStore.cs");
  assert.match(store, /internal static void PublishRecoveryFacts\(\)/, "the store must have one main-thread publication point for its SessionState facts");
  assert.match(body(store, "private static long ReadFloor()"), /SessionState\.GetString/, "the floor must be read through its single main-thread reader");
  assert.match(body(store, "private static string ReadGeneration()"), /SessionState\.GetString/, "the generation must be read through its single main-thread reader");
  assert.equal((store.match(/SessionState\./g) ?? []).length, 4, "every SessionState access must sit in the readers, the publication point or the writer");
  for (const reader of ["internal static long PublicationFloor()", "internal static string PublishedGeneration()"]) {
    const text = body(store, reader);
    assert.match(text, /UnityCliBridge\.CatalogOffMainRead/, `${reader} must keep off-main callers on the published value`);
    assert.doesNotMatch(text, /SessionState\./, `${reader} must never call the main-thread-only SessionState API itself`);
  }
  const bridge = await source(unitypkg, "Editor/UnityCliBridge.cs");
  const startup = body(bridge, "private static void StartListener()");
  const activateAt = startup.indexOf("DebugPauseControl.Activate()");
  const factsAt = startup.indexOf("DebugSessionStore.PublishRecoveryFacts()");
  assert.ok(activateAt > 0, "startup must publish the control channel");
  assert.ok(factsAt > activateAt, "startup must publish the store recovery facts on the main thread right after the channel is published, before the listener serves anything");
  // 归因必须自足：catch-all 只报类型名时无法从测试输出或 CLI 回复定位现场。
  const control = await source(debugRoot, "DebugPauseControl.cs");
  const serve = body(control, "internal static bool TryServe(");
  assert.match(serve, /BoundedText\(exception\.Message \?\? string\.Empty, DebugLimits\.MaxDiagnosticTextBytes\)/, "the catch-all must keep the bounded exception message, not only its type name");
  assert.match(serve, /\{ "failure", failure \}/, "the catch-all must publish that diagnostic in its details");
  const limits = await source(debugRoot, "DebugLimits.cs");
  assert.match(limits, /internal const int MaxDiagnosticTextBytes = 512;/, "the diagnostic bound must live in the single debug-domain limits owner");
});

