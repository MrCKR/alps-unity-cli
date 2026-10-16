import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import test from "node:test";

const unitypkg = new URL("../unitypkg/", import.meta.url);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const hooksRoot = new URL("Editor/Infrastructure/MethodHooks/", unitypkg);
const sessionToolsRoot = new URL("Editor/Tools/debug/session/", unitypkg);
const sharedRoot = new URL("Shared/", unitypkg);
const testsRoot = new URL("Tests/Editor/", unitypkg);

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

/** 提取一个 DTO 类的花括号主体，用于只检查该类自己的字段。 */
function classBody(text: string, name: string): string {
  const start = text.indexOf(`class ${name}`);
  if (start < 0) throw new Error(`${name} is missing`);
  const open = text.indexOf("{", start);
  const close = text.indexOf("\n    }", open);
  return text.slice(open, close);
}

test("the frozen stop order is the single ordered pipeline and every stage exists by name", async () => {
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  assert.match(
    shared,
    /return new\[\]\s*\{\s*StopDispatch, CancelAndResume, ReleaseInput, CloseObservation, DetachHooks, ExplicitCleanup, ReleaseWaits, EmitReport\s*\};/,
    "the frozen stage order must be declared exactly once",
  );
  for (const stage of [
    "StopDispatch = \"stop-dispatch\"",
    "CancelAndResume = \"cancel-and-resume\"",
    "ReleaseInput = \"release-input\"",
    "CloseObservation = \"close-observation\"",
    "DetachHooks = \"detach-hooks\"",
    "ExplicitCleanup = \"explicit-cleanup\"",
    "ReleaseWaits = \"release-waits\"",
    "EmitReport = \"emit-report\"",
  ]) {
    assert.ok(shared.includes(stage), `the frozen stage ${stage} must keep its stable name`);
  }
  // stages 1、2、3、4 都有真实 owner：关闭派发、解除暂停、释放输入、关闭观察。
  const hookStopDispatch = await source(hooksRoot, "MethodHookHost.StopDispatch.cs");
  assert.match(hookStopDispatch, /UnityCliDebugCleanupSteps\.StopDispatch/, "the hook facility owns the first stage");
  const programStopDispatch = await source(debugRoot, "DebugProgramHost.StopDispatch.cs");
  assert.match(programStopDispatch, /UnityCliDebugCleanupSteps\.CancelAndResume/, "the program facility owns the cooperative cancellation stage");
  assert.match(programStopDispatch, /DebugScheduler\.DropPendingForProgram/, "stage one cancels not-yet-started program deliveries");
  assert.match(hookStopDispatch, /DebugScheduler\.DropPendingForHook/, "stage one cancels not-yet-started hook deliveries");
  assert.match(hookStopDispatch, /string\.Equals\(SessionIdOf\(definition\), sessionId, StringComparison\.Ordinal\)/, "the hook stop entry only closes the definitions of the session being stopped");
  const pause = await source(debugRoot, "DebugPauseHost.cs");
  assert.match(pause, /UnityCliDebugCleanupSteps\.CancelAndResume/, "the pause facility resumes held sites in stage two");
  const input = await source(debugRoot, "DebugInputHost.cs");
  assert.match(input, /UnityCliDebugCleanupSteps\.ReleaseInput/, "the input facility releases virtual input in stage three");
});

test("a stopped session stops dispatching on every thread without touching other sessions", async () => {
  const host = await source(debugRoot, "DebugSessionHost.cs");
  assert.match(host, /internal static bool IsDispatchOpen\(string sessionId\)/, "the session owner exposes the dispatch gate");
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  assert.match(shared, /definition\.DispatchClosed = true;[\s\S]{0,200}definition\.State = UnityCliDebugSessionStates\.Stopping;/, "dispatch closes before the cleanup pipeline runs");
  assert.match(shared, /public bool IsDispatchOpen\(string sessionId\)[\s\S]{0,400}definition\.DispatchClosed[\s\S]{0,200}UnityCliDebugSessionStates\.Open/, "the gate is a definition-level fact, not a per-call heuristic");
  const dispatch = await source(hooksRoot, "MethodHookHost.Dispatch.cs");
  assert.match(dispatch, /if \(!DispatchAllowed\(definition\)\) continue;/, "the method-hit path consults the gate before entering user code");
  const phase = await source(hooksRoot, "MethodHookHost.PhaseListener.cs");
  assert.match(phase, /if \(!DispatchAllowed\(definition\)\) return false;/, "the phase listener path consults the gate");
  const programRuntime = await source(debugRoot, "DebugProgramHost.Runtime.cs");
  assert.match(programRuntime, /DebugSessionHost\.IsDispatchOpen\(definition\.SessionId\)/, "the step driver refuses to advance a closed session");
});

test("the cleanup report carries every required field category with stable names", async () => {
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  const report = classBody(shared, "UnityCliDebugCleanupReport");
  for (const field of [
    "public string StopEntry",
    "public string Status",
    "public bool CleanupPending",
    "public long StoppedFutureEntries",
    "public long CancelledPendingOperations",
    "public long ReleasedPauses",
    "public long ReleasedInput",
    "public long ClosedObservations",
    "public long RemovedDetours",
    "public long PendingDetours",
    "public long ExplicitCleanupEntries",
    "public long ReleasedWaits",
    "public long ReleasedHandles",
    "public long ActiveInvocationCount",
    "public long ActiveStepCount",
    "public long ActiveEvaluateCount",
    "public int ActiveThreadId",
    "public long ActiveRevision",
    "public bool UntrackedEffectsPossible",
    "public bool UnmanagedUnknown",
    "public int LoadedNonUnloadableAssemblyCount",
    "public long LoadedNonUnloadableAssemblyBytes",
    "public readonly List<UnityCliDebugTargetOwners> SharedTargets",
    "public readonly List<string> RestoredFields",
    "public readonly List<string> ConflictingFields",
    "public readonly List<string> StaleTargets",
    "public readonly List<string> UnrecoverableEffects",
    "public readonly List<string> DeclaredEffects",
    "public readonly List<string> UnmanagedFacts",
  ]) {
    assert.ok(report.includes(field), `the report must declare ${field}`);
  }
  assert.match(shared, /public static class UnityCliDebugStopEntries[\s\S]{0,400}MainThread = "main-thread"[\s\S]{0,200}PauseControlChannel = "pause-control-channel"/, "the two stop entries are published as stable values");
  const dto = await source(sessionToolsRoot, "DebugSessionDtos.cs");
  const view = classBody(dto, "DebugSessionCleanupView");
  for (const field of ["StopEntry", "UnmanagedUnknown", "StaleTargets", "UnrecoverableEffects", "DeclaredEffects"]) {
    assert.ok(view.includes(`${field} { get; set; }`), `the public cleanup projection must expose ${field}`);
  }
  assert.match(view, /StaleTargets = new List<string>\(report\.StaleTargets\)/, "the projection copies the whole field category instead of dropping it");
});

test("a repeated stop returns the same terminal facts and never invents a revision", async () => {
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  assert.match(shared, /internal UnityCliDebugCleanupReport LastReport;/, "the definition keeps its last cleanup report as the current fact");
  assert.match(shared, /if \(definition\.LastReport != null\)[\s\S]{0,2000}report\.RestoredFields\.AddRange\(previous\.RestoredFields\);/, "a repeated stop carries the restored fields forward");
  assert.match(shared, /report\.ConflictingFields\.AddRange\(previous\.ConflictingFields\);/, "a repeated stop carries the conflicting fields forward");
  assert.match(shared, /report\.StaleTargets\.AddRange\(previous\.StaleTargets\);/, "a repeated stop carries the stale targets forward");
  assert.match(shared, /report\.UnrecoverableEffects\.AddRange\(previous\.UnrecoverableEffects\);/, "a repeated stop carries the unrecoverable effects forward");
  const stopTool = await source(sessionToolsRoot, "stop.cs");
  assert.match(stopTool, /The definition and its evidence stay loaded; only debug\.session\.remove deletes it\./, "the descriptor states what stop does and does not do");
});

test("conditional restore separates restored, conflicting and stale targets at a trusted boundary", async () => {
  const restore = await source(debugRoot, "DebugValueRestore.cs");
  assert.match(restore, /report\.StaleTargets\.Add\([\s\S]{0,200}report\.UnrecoverableEffects\.Add\(/, "an invalid target is reported as both stale and unrecoverable");
  assert.match(restore, /report\.ConflictingFields\.Add\([\s\S]{0,300}the business value is kept/, "a conflict keeps the business value and says so");
  assert.match(restore, /DebugValueSupport\.Canonical\(current\) != DebugValueSupport\.Canonical\(record\.WrittenValue\)/, "the comparison is against the value this session wrote");
  assert.match(restore, /var restoreOrder = UnityCliDebugCleanupSteps\.OrderOf\(UnityCliDebugCleanupSteps\.ExplicitCleanup\);[\s\S]{0,400}true\);/, "the restore entry declares that it needs the main thread, so no off-main stop can claim it");
  assert.doesNotMatch(restore, /Task\.Run|ThreadPool/, "the restore never fabricates a second synchronous boundary");
});

test("the stop order covers the Domain Reload, Play exit and Editor restart boundaries", async () => {
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  assert.match(shared, /RestoredWithoutActivation = !string\.Equals\(generation, domainGeneration, StringComparison\.Ordinal\)/, "a reload marks restored definitions as inactive");
  assert.match(shared, /editor-session-mismatch/, "another Editor session is rejected instead of reused");
  assert.match(shared, /public int InvalidatePlayScopedResources\(string playSegmentId\)/, "Play exit invalidates exactly the ended segment's resources");
  const host = await source(debugRoot, "DebugSessionHost.cs");
  assert.match(host, /DebugInputHost\.OnPlayExit\(ended\);/, "Play exit releases the Play-owned virtual input");
  const tests = await source(testsRoot, "DebugStopCleanupTests.cs");
  for (const name of [
    "DomainReloadRestoresDefinitionsWithNoActiveWorkAndNoCleanupPending",
    "EditorRestartRejectsTheStoredDefinitionsInsteadOfReusingThemAStaleEditorSession",
    "PlayExitInvalidatesPlayScopedResourcesAndTheStopReportStaysPendingUntilTheyAreReleased",
  ]) {
    assert.ok(tests.includes(`public void ${name}`), `the boundary ${name} must stay covered`);
  }
});

test("every session fixture in the test tree declares a scope the current editor mode can activate", async () => {
  const fixturesRoot = new URL("../unitypkg/Tests/", import.meta.url);
  const files: string[] = [];
  for (const directory of ["Editor", "Runtime"]) {
    for (const entry of await readdir(new URL(`${directory}/`, fixturesRoot), { withFileTypes: true })) {
      if (entry.isFile() && entry.name.endsWith(".cs")) files.push(`${directory}/${entry.name}`);
    }
  }
  assert.ok(files.length > 0, "the fixture sources must stay discoverable for the activation audit");

  let hostedCreates = 0;
  const problems: string[] = [];
  for (const file of files) {
    const text = await source(fixturesRoot, file);
    // DebugSessionHost.create 的定义必须以“当前 EditorApplication.isPlaying”判定激活范围：
    // 一个 play-only 或未声明的范围会在 EditMode 运行里被拒绝，使测试到不了它真正要断言的语义。
    for (let at = text.indexOf("new DebugSessionCreateRequest"); at >= 0; at = text.indexOf("new DebugSessionCreateRequest", at + 1)) {
      hostedCreates += 1;
      const close = text.indexOf("})", at);
      const literal = text.slice(at, close < 0 ? at + 800 : close + 2);
      if (!/ActivationScope\s*=\s*UnityCliDebugActivationScopes\.EditorAndPlay/.test(literal)) {
        problems.push(`${file}: a hosted session definition must declare editor-and-play`);
      }
    }
    // 直接驱动 registry 的测试必须写死它假设的编辑器模式，不能把运行时 Play 状态带进离线断言。
    for (let at = text.indexOf("registry.Create("); at >= 0; at = text.indexOf("registry.Create(", at + 1)) {
      if (text.slice(at, at + 400).includes("EditorApplication.isPlaying")) {
        problems.push(`${file}: a direct registry.Create must pass a literal editor mode`);
      }
    }
  }
  assert.deepEqual(problems, [], problems.join("; "));
  assert.ok(hostedCreates >= 9, `every hosted session fixture must stay audited, saw ${hostedCreates}`);
});

test("the shared value-session fixture asserts its activation precondition and keeps the three restore outcomes", async () => {
  const tests = await source(testsRoot, "DebugStopCleanupTests.cs");
  assert.match(
    tests,
    /internal ValueSessionScope\(string label\)[\s\S]{0,700}ApplyCreate\(plan\)[\s\S]{0,300}Assert\.That\(/,
    "the fixture must assert that its own create succeeded instead of letting a rejected create surface inside an unrelated assertion",
  );
  for (const name of [
    "AConditionalRestoreWritesBackOnlyWhileTheTargetStillHoldsTheWrittenValue",
    "ABusinessWriteAfterOursIsReportedAsAConflictAndNeverOverwritten",
    "AnInvalidatedRestoreTargetIsReportedAsStaleInsteadOfAsAConflict",
  ]) {
    assert.ok(tests.includes(`public void ${name}`), `the conditional-restore outcome ${name} must stay covered`);
  }
});

test("the stop machinery adds no new queue, no update callback and no second limit source", async () => {
  for (const [root, path] of [
    [debugRoot, "DebugSessionHost.cs"],
    [debugRoot, "DebugProgramHost.StopDispatch.cs"],
    [debugRoot, "DebugScheduler.cs"],
    [debugRoot, "DebugPauseHost.cs"],
    [debugRoot, "DebugValueRestore.cs"],
  ] as [URL, string][]) {
    const text = await source(root, path);
    assert.doesNotMatch(text, /EditorApplication\.update \+=/, `${path} must not register its own update callback`);
    assert.doesNotMatch(text, /new (Concurrent)?Queue<[^>]*[Dd]eliver/, `${path} must not add a second delivery queue`);
  }
  const limits = await source(debugRoot, "DebugLimits.cs");
  assert.match(limits, /internal static class DebugLimits/, "DebugLimits stays the single debug-domain limit declaration point");
  const programStopDispatch = await source(debugRoot, "DebugProgramHost.StopDispatch.cs");
  assert.doesNotMatch(programStopDispatch, /const int Max\w+ = \d/, "stop behaviour must not restate a limit");
  const hookStopDispatch = await source(hooksRoot, "MethodHookHost.StopDispatch.cs");
  assert.doesNotMatch(hookStopDispatch, /const int Max\w+ = \d/, "the hook stop entry must not restate a limit");
});
