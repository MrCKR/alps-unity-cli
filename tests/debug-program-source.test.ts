import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const repoRoot = process.cwd();
const unitypkg = new URL("../unitypkg/", import.meta.url);
const programToolsRoot = new URL("Editor/Tools/debug/program/", unitypkg);
const runToolsRoot = new URL("Editor/Tools/debug/run/", unitypkg);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const sharedRoot = new URL("Shared/", unitypkg);

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

const TOOLS = ["install", "replace", "run", "wait", "stop", "info", "list", "remove"];
const READERS = ["wait", "info", "list"];
const WRITERS = ["install", "replace", "run", "stop", "remove"];
const DESTRUCTIVE = ["install", "replace", "run"];

test("the eight debug.program tools exist with the declared descriptor shape", async () => {
  for (const tool of TOOLS) {
    const text = await source(programToolsRoot, `../program-${tool}.cs`);
    assert.match(text, /IUnityCliTool<DebugProgram\w+Request, DebugProgram\w+Result>/, `${tool}.cs must implement the typed Tool interface`);
    assert.match(text, new RegExp(`Name = "debug\\.program-${tool}"`), `${tool}.cs must declare its exact public name`);
    assert.match(text, /Group = "debug"/);
    assert.match(text, /Target = "editor"/);
    assert.match(text, /PlayModeSupport = "supported"/);
    assert.match(text, /DebugLimits\.ProgramSummary\(\)/, `${tool}.cs must publish the single program limit summary`);
    assert.doesNotMatch(text, /EditorApplication\.update \+=/, "the program domain must not register its own update callback");
    assert.doesNotMatch(text, /MaxProgramsPerSession = |MaxProgramsGlobal = |MaxProgramSourceBytes =/, `${tool}.cs must not restate a program limit`);
  }
  for (const writer of WRITERS) {
    const text = await source(programToolsRoot, `../program-${writer}.cs`);
    assert.match(text, /ctx\.CommitEffect\(\);/, `${writer}.cs must cross the single effect barrier`);
    assert.match(text, /SideEffects = new\[\] \{/, `${writer}.cs must declare its side effects`);
  }
  for (const reader of READERS) {
    const text = await source(programToolsRoot, `../program-${reader}.cs`);
    assert.doesNotMatch(text, /SideEffects =/, `${reader}.cs is a pure read and must declare no side effect`);
    assert.doesNotMatch(text, /ctx\.CommitEffect\(\)/, `${reader}.cs must not cross an effect barrier`);
  }
  for (const tool of DESTRUCTIVE) {
    const text = await source(programToolsRoot, `../program-${tool}.cs`);
    assert.match(text, /DestructiveHint = true/, `${tool}.cs is declared destructive`);
  }
  assert.match(await source(programToolsRoot, "../program-install.cs"), /SideEffects = new\[\] \{ "debug-session", "artifact-write" \}/);
  assert.match(await source(programToolsRoot, "../program-replace.cs"), /SideEffects = new\[\] \{ "debug-session", "artifact-write" \}/);
  assert.match(await source(programToolsRoot, "../program-run.cs"), /SideEffects = new\[\] \{ "debug-activation", "user-code-execution" \}/);
  assert.match(await source(programToolsRoot, "../program-stop.cs"), /SideEffects = new\[\] \{ "debug-activation" \}/);
  assert.match(await source(programToolsRoot, "../program-remove.cs"), /SideEffects = new\[\] \{ "debug-session" \}/);
  assert.match(await source(programToolsRoot, "../program-stop.cs"), /DestructiveHint = false/);
  assert.match(await source(programToolsRoot, "../program-remove.cs"), /DestructiveHint = false/);
});

test("the program domain declares every budget in the single debug limit owner", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  for (const value of [
    "MaxProgramsPerSession = 32",
    "MaxProgramsGlobal = 64",
    "MaxActiveProgramRunsGlobal = 16",
    "MaxProgramSourceBytes = 256 * 1024",
    "MaxProgramEntries = 16",
    "MaxProgramBindings = 16",
    "MaxProgramStateBytes = 32 * 1024",
    "MaxProgramArgsBytes = 16 * 1024",
    "MaxProgramRunMaxDurationMs = 3600000L",
    "MaxProgramDeliveries = 64",
    "ProgramWaitMaxMs = 30000",
    "ProgramListDefaultLimit = 32",
    "ProgramListMaxLimit = 64",
  ]) {
    assert.ok(limits.includes(value), `DebugLimits must declare ${value}`);
  }
  assert.match(limits, /internal static string ProgramSummary\(\)/);
});

test("program source and bindings use the single compiler owner and the single hook host", async () => {
  const host = `${await source(debugRoot, "DebugProgramHost.cs")}${await source(debugRoot, "DebugProgramHost.Runtime.cs")}${await source(debugRoot, "DebugProgramHost.Support.cs")}`;
  for (const reuse of [
    "DebugCompilerOwner.TryBeginOwnership",
    "DebugCompilerOwner.References(",
    "DebugCompilerOwner.TryAdmitCandidate",
    "DebugCompilerOwner.CommitLoadedArtifact",
    "DebugCompilerOwner.EndOwnership",
    "DynamicCodeCompiler.CompileAsync",
    "MethodHookHost.PrepareInstall",
    "MethodHookHost.ApplyInstall",
    "MethodHookHost.CompileCallbacksAsync",
    "MethodHookHost.PrepareRemove",
    "MethodHookHost.ApplyRemove",
    "MethodHookHost.Info",
    "DebugSessionHost.ReserveResource",
    "DebugSessionHost.AttachResource",
    "DebugSessionHost.ReleaseResource",
    "DebugSessionHost.RegisterCleanupStep",
    "MethodHookTargetKey.From",
  ]) {
    assert.ok(host.includes(reuse), `the program owner must reuse ${reuse}`);
  }
  for (const forbidden of [
    "Application.logMessageReceived",
    "IUnityCliMethodHookBackend",
    "Harmony",
    "new Thread(",
    "new Task(",
    "EditorApplication.update +=",
    "DebugSessionStore",
    "MethodHookStore",
    "MaxCompiledArtifacts = ",
  ]) {
    assert.ok(!host.includes(forbidden), `the program owner must not use ${forbidden}`);
  }
});

test("the program scheduler is advanced by the single central loop only", async () => {
  const scheduler = await source(debugRoot, "DebugScheduler.cs");
  assert.match(scheduler, /internal static void Tick\(\)/, "the scheduler exposes one bounded tick");
  assert.doesNotMatch(scheduler, /EditorApplication\.update \+=/);
  assert.match(scheduler, /driver-already-planted/, "at most one continuous driver per program is enforced");
  assert.match(scheduler, /expiredDeliveries/, "expired deliveries are recorded instead of executed late");
  const waiter = await source(new URL("Editor/Infrastructure/", unitypkg), "UnityCliWaiter.cs");
  const registrations = waiter.match(/TickCoordinator\("debug\.program", DebugScheduler\.Tick\);/g) ?? [];
  assert.equal(registrations.length, 1, "the program scheduler must be registered exactly once in the central loop");
});

test("the step driver claims its own steps through the registered central tick", async () => {
  const runtime = await source(debugRoot, "DebugProgramHost.Runtime.cs");
  const advance = runtime.slice(runtime.indexOf("internal static void AdvanceDriver"));
  assert.ok(advance.length > 0, "the program host must own the driver advance");
  const bounded = advance.split("internal static void StopDriverByDeadline")[0];
  assert.match(bounded, /session\.TryBeginStep\(/, "the driver claims each step through the registry step claim");
  assert.doesNotMatch(bounded, /session\.TryBeginRun\(/, "a driver cannot depend on the scheduled-to-running transition it already consumed");
  assert.match(bounded, /run\.IsTerminal[\s\S]*run\.CancellationRequested[\s\S]*UtcMilliseconds\(\) > run\.DeadlineUtcMs/, "terminal, cancellation and expiry are decided before claiming a step");
  const stepClaim = bounded.indexOf("session.TryBeginStep(");
  const stepInvocation = bounded.indexOf("InvokeStep(definition, run, entry, driver.StepIndex, driver.Phase)");
  assert.ok(stepInvocation > stepClaim && stepClaim > 0, "the step claim must precede the entry invocation");
  const completedBranch = bounded.indexOf("if (result.Completed)");
  const applyWait = bounded.indexOf("DebugScheduler.ApplyWait(", completedBranch);
  const markedReturned = bounded.lastIndexOf("session.MarkCodeReturned(run.ProgramRunId);", applyWait);
  assert.ok(completedBranch > 0 && markedReturned > completedBranch && applyWait > markedReturned, "no synchronous entry code may stay in flight between two steps");
  // 只有一个 closer：已发布终态的运行仍必须释放 driver，不能在已终态时留下无推进者的运行。
  assert.match(runtime, /private static void FinishDriver\([\s\S]*if \(!published && !run\.IsTerminal\)/, "an already published terminal must still release its driver");
  assert.match(runtime, /private static void CloseExpiredRun\(UnityCliDebugProgramRegistry session, UnityCliDebugProgramRun run, double nowUtc\)/, "an expired run is closed exactly once with its lifecycle evidence");
  const shared = await source(sharedRoot, "UnityCliDebugPrograms.cs");
  assert.match(shared, /public bool TryBeginStep\(string programRunId, double nowUtcMs, int threadId\)/);
  assert.match(shared, /if \(run == null \|\| run\.IsTerminal\) return false;[\s\S]*if \(run\.CancellationRequested \|\| nowUtcMs > run\.DeadlineUtcMs\) return false;/, "a cancelled expired or terminal run never gets a step");
  assert.match(shared, /if \(run\.InFlight\) return false;/, "a step never reenters code that is still in flight");
});

test("the run observation proof accepts exactly the declared non-terminal window", async () => {
  const coordinator = await source(unitypkg, "Editor/Infrastructure/UnityCliOperationCoordinator.cs");
  assert.match(coordinator, /Completion result contains an intermediate accepted\/requested\/queued\/started\/triggered\/running status\./, "the strict proof rejection must stay");
  assert.match(coordinator, /private static bool DeclaresBoundedNonTerminalObservation\(UnityCliToolCompletionContract contract, object normalized\)/);
  assert.match(coordinator, /contract\.CompletionPredicate, "owned-run-terminal-readback", StringComparison\.Ordinal/, "the exemption is keyed on the declared predicate, not on a tool name");
  assert.match(coordinator, /ContainsIntermediateStatus\(normalized, DeclaresBoundedNonTerminalObservation\(contract, normalized\)\)/, "the readback check must consult the declared observation");
  assert.match(coordinator, /entry\.Value is bool && !\(bool\)entry\.Value/, "only an explicit terminal=false is accepted");
  assert.match(coordinator, /if \(IsIntermediateField\(key\) && IntermediateFieldIsSet\(entry\.Value\)\) return true;/, "intermediate marker fields stay rejected");
  assert.match(coordinator, /namedRootState && IsIntermediateStatus\(text\) && !rootIsDeclaredBoundedObservation/, "only the root run state of a declared observation is exempt");
  const contract = JSON.parse(await readFile(join(repoRoot, "contracts", "tool-completion-contract.json"), "utf8"));
  for (const toolName of ["debug.program-run", "debug.program-wait", "debug.program-stop"]) {
    const entry = contract.tools.find((candidate: { toolName: string }) => candidate.toolName === toolName);
    assert.equal(entry.completionPredicate.kind, "owned-run-terminal-readback", `${toolName} declares the run observation predicate`);
    assert.match(entry.completionPredicate.description, /terminal[:=]false/, `${toolName} must declare the bounded non-terminal window`);
  }
  // 创建信封必须携带诚实非终态事实：合法的 dispatch 运行在返回时仍在等待自己的执行点。
  const host = await source(debugRoot, "DebugProgramHost.cs");
  assert.match(host, /Terminal = run\.IsTerminal/, "debug.program-run must return the run's own terminal fact");
  const dtos = await source(programToolsRoot, "DebugProgramDtos.cs");
  assert.match(dtos, /public bool Terminal \{ get; set; \}/, "DebugProgramRunResult must carry the terminal observation flag");
});

test("the program owner never carries closures threads or tasks across a revision", async () => {
  const abi = await source(debugRoot, "DebugProgramAbi.cs");
  const scheduler = await source(debugRoot, "DebugScheduler.cs");
  for (const text of [abi, scheduler]) {
    assert.doesNotMatch(text, /EditorApplication\.update \+=/, "no second update registration");
    assert.doesNotMatch(text, /Application\.logMessageReceived/, "log capture belongs to the session log owner");
  }
  const events = await source(sharedRoot, "UnityCliDebugPrograms.cs");
  for (const state of ["installed", "active", "stopped"]) assert.ok(events.includes(`"${state}"`), `program state ${state} must exist`);
  for (const state of ["scheduled", "running", "completed", "failed", "cancelled", "interrupted"]) {
    assert.ok(`${events}${abi}`.includes(`"${state}"`), `run state ${state} must exist`);
  }
});

test("the frozen ABI is self-describing and the descriptor carries a legal example", async () => {
  const abi = await source(debugRoot, "DebugProgramAbi.cs");
  for (const signature of [
    "UnityCliDebugStepResult ",
    "void Entry(UnityCliDebugInvocation invocation)",
    "void Entry(UnityCliDebugEventContext context)",
    "object Entry(UnityCliDebugOneShotContext context)",
    "void Entry(UnityCliDebugCleanupContext context)",
  ]) {
    assert.ok(abi.includes(signature), `the ABI must declare ${signature}`);
  }
  assert.match(abi, /UnityCliDebugProgramWrapperAttribute/, "an instance entry requires an explicit wrapper marker");
  assert.match(abi, /AsyncStateMachineAttribute/, "an async state machine must be rejected");
  assert.match(abi, /internal static string ExampleSource\(\)/);
  assert.match(abi, /internal static string StepContextAbi\(\)/);
  assert.match(abi, /void Check\(string name, object expected, object actual, bool passed, string coverage = null, params string\[\] evidence\)/);
  assert.match(abi, /void Emit\(string name, object value\)/);
  assert.match(abi, /long StepIndex \{ get; \}/);
  for (const tool of ["install", "replace", "run"]) {
    const text = await source(programToolsRoot, `../program-${tool}.cs`);
    assert.match(text, /UnityCliDebugProgramAbi\.StepContextAbi\(\)/, `${tool}.cs must publish the step member ABI`);
  }
  assert.match(await source(runToolsRoot, "../run-begin.cs"), /UnityCliDebugProgramAbi\.StepContextAbi\(\)/, "begin.cs names context.Check and must publish the same step member ABI");
  const install = await source(programToolsRoot, "../program-install.cs");
  assert.match(install, /UnityCliDebugProgramAbi\.ExampleSource\(\)/, "install must ship a minimal legal program");
  assert.match(install, /UnityCliDebugProgramAbi\.SignatureTable\(\)/, "install must ship the whole signature table");
  const replace = await source(programToolsRoot, "../program-replace.cs");
  assert.match(replace, /UnityCliDebugProgramAbi\.SignatureTable\(\)/);
});

test("the program domain declares its stable error codes in the public documentation", async () => {
  const docs = await readFile(join(repoRoot, "docs", "error-codes.md"), "utf8");
  for (const code of [
    "E_DEBUG_PROGRAM_NOT_FOUND",
    "E_DEBUG_PROGRAM_RUN_NOT_FOUND",
    "E_DEBUG_PROGRAM_LABEL_CONFLICT",
    "E_DEBUG_PROGRAM_REVISION_CONFLICT",
    "E_DEBUG_PROGRAM_STATE_CONFLICT",
    "E_DEBUG_PROGRAM_WRONG_STATE",
    "E_DEBUG_PROGRAM_RUN_ACTIVE",
    "E_DEBUG_PROGRAM_DRIVER_CONFLICT",
    "E_DEBUG_PROGRAM_ENTRY_MISSING",
    "E_DEBUG_PROGRAM_ENTRY_INVALID",
    "E_DEBUG_PROGRAM_ENTRY_SITE_MISMATCH",
    "E_DEBUG_PROGRAM_ENTRY_FAILED",
    "E_DEBUG_PROGRAM_CLEANUP_FAILED",
    "E_DEBUG_PROGRAM_ABI_INVALID",
    "E_DEBUG_PROGRAM_BINDING_INVALID",
    "E_DEBUG_PROGRAM_BINDING_NOT_PREPARED",
    "E_DEBUG_PROGRAM_SESSION_REQUIRED",
    "E_DEBUG_PROGRAM_SESSION_STOPPED",
    "E_DEBUG_PROGRAM_SESSION_UNAVAILABLE",
    "E_DEBUG_PROGRAM_SITE_INVALID",
    "E_DEBUG_PROGRAM_SITE_UNAVAILABLE",
    "E_DEBUG_PROGRAM_SOURCE_UNREADABLE",
    "E_DEBUG_PROGRAM_DOMAIN_CHANGED",
    "E_DEBUG_PROGRAM_RUN_EXPIRED",
    "E_DEBUG_PROGRAM_RUN_CANCELLED",
    "E_DEBUG_PROGRAM_RUN_INTERRUPTED",
    "E_DEBUG_PROGRAM_WAIT_TIMEOUT",
    "E_DEBUG_PROGRAM_TERMINAL_PENDING",
  ]) {
    assert.ok(docs.includes(code), `docs/error-codes.md must declare ${code}`);
  }
});
