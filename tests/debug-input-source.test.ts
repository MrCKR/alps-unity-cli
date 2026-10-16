import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const repoRoot = process.cwd();
const unitypkg = new URL("../unitypkg/", import.meta.url);
const inputToolsRoot = new URL("Editor/Tools/debug/input/", unitypkg);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const sharedRoot = new URL("Shared/", unitypkg);

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

/** 提取一个 DTO 类的花括号主体，用于只检查该类自己的字段。 */
function requestBody(text: string, name: string): string {
  const start = text.indexOf(`class ${name}`);
  if (start < 0) throw new Error(`${name} is missing from the input DTO file`);
  const open = text.indexOf("{", start);
  const close = text.indexOf("\n    }", open);
  return text.slice(open, close);
}

const TOOLS = ["info", "key", "pointer", "release"];
const WRITERS = ["key", "pointer", "release"];

test("the four debug.input tools exist with the declared descriptor shape", async () => {
  for (const tool of TOOLS) {
    const text = await source(inputToolsRoot, `${tool}.cs`);
    assert.match(text, /IUnityCliTool<DebugInput\w+Request, DebugInput\w+Result>/, `${tool}.cs must implement the typed Tool interface`);
    assert.match(text, new RegExp(`Name = "debug\\.input\\.${tool}"`), `${tool}.cs must declare its exact public name`);
    assert.match(text, /Group = "debug"/);
    assert.match(text, /Target = "editor"/);
    assert.match(text, /PlayModeSupport = "supported"/);
    assert.match(text, /DestructiveHint = false/, `${tool}.cs declares no destructive hint`);
    assert.match(text, /DebugLimits\.InputSummary\(\)/, `${tool}.cs must publish the single input limit summary`);
    assert.doesNotMatch(text, /EditorApplication\.update \+=/, "the input domain must not register its own update callback");
    assert.doesNotMatch(text, /MaxInput\w+ = \d/, `${tool}.cs must not restate an input limit`);
  }
  // info 是纯读取；其余三个跨效果屏障并声明副作用。
  const info = await source(inputToolsRoot, "info.cs");
  assert.doesNotMatch(info, /SideEffects =/, "debug.input.info is a pure read and must declare no side effect");
  assert.doesNotMatch(info, /ctx\.CommitEffect\(\)/, "debug.input.info must not cross an effect barrier");
  assert.match(info, /ctx\.UseReadBudget\(/, "debug.input.info budgets its own read");
  // 每个 Tool 一个职责：提交请求里不得出现 action 路由器字段。
  const dtos = await source(inputToolsRoot, "DebugInputDtos.cs");
  assert.doesNotMatch(requestBody(dtos, "DebugInputKeyRequest"), /Action/, "the key request must not route on an action field");
  assert.doesNotMatch(requestBody(dtos, "DebugInputPointerRequest"), /Action/, "the pointer request must not route on an action field");
  assert.match(requestBody(dtos, "DebugInputReleaseRequest"), /Scope \{ get; set; \}/, "release declares which owner input it releases");
  assert.match(dtos, /\[UnityCliRequired, UnityCliStringEnum\("screen-pixels", "game-view-normalized"\)\] public string TargetSpace \{ get; set; \}/, "a pointer submission always declares its coordinate space");
  for (const writer of WRITERS) {
    const text = await source(inputToolsRoot, `${writer}.cs`);
    assert.match(text, /SideEffects = new\[\] \{ "virtual-input-device" \}/, `${writer}.cs must declare the virtual device side effect`);
    assert.match(text, /ctx\.CommitEffect\(\);/, `${writer}.cs must cross the effect barrier after prevalidation`);
    assert.match(text, /Prepare(Key|Pointer|Release)\(request\)/, `${writer}.cs must prevalidate before the effect`);
  }
});

test("the input domain declares every bound in the single debug limit owner", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  for (const value of [
    "MaxInputDevicesPerSession = 2",
    "MaxInputKeysPerDispatch = 8",
    "MaxInputPointerButtons = 3",
    "MaxInputHoldsPerSession = 16",
    "MaxInputHoldMs = 60000L",
    "MaxInputPendingReleases = MaxSessions",
    "MaxInputNameBytes = 128",
    "MaxInputMissingApiMembers = 8",
  ]) {
    assert.ok(limits.includes(value), `DebugLimits must declare ${value}`);
  }
  assert.match(limits, /internal static string InputSummary\(\)/);
  const host = `${await source(debugRoot, "DebugInputHost.cs")}${await source(debugRoot, "DebugInputHost.Report.cs")}`;
  assert.doesNotMatch(host, /MaxInput\w+ = \d/, "input bounds must only be declared in DebugLimits");
});

test("the input owner reuses the one device backend probe, event store, scheduler and session owner", async () => {
  const host = `${await source(debugRoot, "DebugInputHost.cs")}${await source(debugRoot, "DebugInputHost.Report.cs")}`;
  for (const reuse of [
    "DebugInputDeviceBackends.Resolve()",
    "DebugEventStore.Append(new UnityCliDebugEvent",
    "DebugTriggerBudget.TryPlan(",
    "DebugTriggerBudget.Release(",
    "DebugSessionHost.ReserveResource(",
    "DebugSessionHost.RegisterCleanupStep(",
    "DebugSessionHost.ReleaseResource(",
    "DebugSessionHost.RuntimeId()",
    "UnityCliDebugCleanupSteps.ReleaseInput",
    "UnityCliDebugResourceKinds.Input",
    "UnityCliDebugFacilityOwners.Input",
    "MethodHookHost.ListByTargetKey(",
    "MethodHookTargetResolver.Resolve(",
  ]) {
    assert.ok(host.includes(reuse), `the input owner must reuse ${reuse}`);
  }
  for (const forbidden of [
    "EditorApplication.update +=",
    "new Thread(",
    "new Task(",
    "Application.logMessageReceived",
    "class UnityCliDebugEventBuffer",
    "EnqueueDelivery",
    "TryClaimDelivery",
    "PlantDriver",
    "MethodHookStore",
    "DebugSessionStore",
    "InputSystem.AddDevice",
    "Client.Add",
  ]) {
    assert.ok(!host.includes(forbidden), `the input owner must not use ${forbidden}`);
  }
  assert.doesNotMatch(host, /AddDevice\(/, "the backend API is only reached through the probed device contract");
  // 通用输入路径必须指名真实 Tool，不建立第二套输入模型。
  const routes = host.slice(host.indexOf("private static List<DebugInputRouteView> GenericRoutes()"));
  assert.match(routes, /"debug\.method\.invoke"/, "the business entry path names its real Tool");
  assert.match(routes, /"debug\.hook\.install"/, "the managed wrapper path names its real Tool");
  assert.match(routes, /"debug\.code\.execute"/, "the arbitrary code path names its real Tool");
  assert.match(routes, /"debug\.program\.run"/, "the program and delivery paths name their real Tool");
  assert.match(routes, /no logical frame is required/, "the general paths must state that they are not tied to one logical frame");
});

test("the central scheduler and lifecycle owners drive input release", async () => {
  const scheduler = await source(debugRoot, "DebugScheduler.cs");
  assert.match(scheduler, /if \(string\.Equals\(phase, UnityCliDebugProgramExecutionSites\.EditorTick, StringComparison\.Ordinal\)\) DebugInputHost\.Advance\(\);/,
    "input boundaries and deferred releases advance on the central editor tick, not on a new update callback");
  const pause = await source(debugRoot, "DebugPauseHost.cs");
  assert.match(pause, /DebugInputHost\.OnPauseHit\(mainThread\);[\s\S]{0,200}ServiceSite\(site\);/, "a pause releases reachable input before the hit thread stops");
  const session = await source(debugRoot, "DebugSessionHost.cs");
  assert.match(session, /DebugInputHost\.OnPlayExit\(ended\);/, "a Play exit requests input release for that segment");
});

test("a press carries its own end boundary and there is no default", async () => {
  const host = await source(debugRoot, "DebugInputHost.cs");
  const report = await source(debugRoot, "DebugInputHost.Report.cs");
  assert.ok(report.includes("private static void ValidateBoundary(bool press, string endBoundary, long? holdMs, out string normalized, out long normalizedMs)"));
  assert.ok(report.includes('"hold-ms or until-release, with no default"'), "a press without a declared boundary is refused");
  assert.ok(report.includes("no end boundary when no control is pressed, because nothing is held"), "a move declares no boundary of its own");
  assert.ok(host.includes("ExpiresAtMonotonicMs = string.Equals(spec.EndBoundary, UnityCliDebugInputEndBoundaries.HoldMs, StringComparison.Ordinal) ? now + spec.HoldMs : 0d,"),
    "each record keeps its own expiry");
  assert.ok(host.includes("&& now >= record.ExpiresAtMonotonicMs).ToArray();"), "an elapsed boundary is closed by the central tick");
});

test("released is reported only when the device readback confirms it", async () => {
  const host = await source(debugRoot, "DebugInputHost.cs");
  assert.match(host, /private static void ConfirmReleaseLocked\(InputRecord record, string reason, double now\)/, "one confirmation path decides released");
  assert.match(host, /record\.ReleaseConfirmed = true;/, "confirmation is recorded as a fact, not inferred from the submission");
  assert.match(host, /record\.ReleaseConfirmed = false;[\s\S]{0,200}RegisterPendingReleaseLocked\(record\.SessionId, ScopeOf\(record\.DeviceKind\)\);/, "an unconfirmed release is registered for the central tick");
  assert.ok(host.includes("UnityCliDebugInputReasons.ReleaseNotApplied"), "a submitted but unapplied clear is reported as such");
  assert.ok(host.includes("internal static bool HasUnreleasedInput(string sessionId)"), "the owner reports its own unreleased state");
  assert.match(host, /if \(device == null\)[\s\S]{0,400}ConfirmReleaseLocked\(record, UnityCliDebugInputReasons\.DeviceAbsent, now\);/, "an absent device proves no hold survives");
  const report = await source(debugRoot, "DebugInputHost.Report.cs");
  assert.match(report, /ReleaseState = released \? UnityCliDebugInputHoldStates\.Released : UnityCliDebugInputHoldStates\.Pending/, "the release answer is one of the two honest states");
  assert.match(report, /At least one targeted record is not confirmed released/, "the pending answer states the reason instead of claiming release");
});

test("the owner never resets a real device or another owner's device", async () => {
  const host = await source(debugRoot, "DebugInputHost.cs");
  assert.match(host, /private static void RequireNoForeignDevice\(string deviceName, IUnityCliDebugInputDeviceBackend backend\)/, "a foreign device name is detected before activation");
  assert.match(host, /"E_DEBUG_RESOURCE_CONFLICT"[\s\S]{0,600}DeviceNameOwnedElsewhere/, "a foreign device name is refused with the stable reason");
  assert.match(host, /Do not reset or remove that device/, "the refusal tells the caller not to touch that device");
  assert.ok(host.includes("private static void RequireNoHoldConflictLocked(string sessionId, string kind, List<string> controls)"), "a competing hold is refused before activation");
  assert.match(host, /if \(removed\)[\s\S]{0,200}DropDeviceLocked\(device\);/, "a rollback only ever drops a device created by that very call");
  assert.match(host, /WasCreatedByThisCall\(device, now\)/, "device removal after a failed dispatch requires that this call created it");
  // 后端契约只暴露它自己建立的设备句柄，没有任何“列出真实设备并改写”的入口。
  const contract = await source(debugRoot, "DebugInputDeviceBackends.cs");
  assert.match(contract, /bool TryRemoveDevice\(object device, out string reason\);/);
  assert.doesNotMatch(contract, /ResetDevice|TryReset|SetDeviceState\(string|UseDevice\(/, "the contract has no way to reset or adopt a real device");
});

test("an unavailable backend is reported without installing anything", async () => {
  const host = await source(debugRoot, "DebugInputHost.Report.cs");
  assert.match(host, /private static IUnityCliDebugInputDeviceBackend RequireBackend\(string deviceKind\)/, "a write path refuses before the effect when no backend is available");
  assert.match(host, /"E_DEBUG_BINDING_UNSUPPORTED"/, "the refusal uses the frozen binding error code");
  assert.match(host, /\{ "status", UnityCliDebugInputBackendStates\.Unavailable \}/, "the refusal reports the unavailable status as data");
  assert.match(host, /\{ "installedByThisOwner", false \}/, "the refusal states that nothing was installed");
  const backend = await source(debugRoot, "DebugInputBackendInputSystem.cs");
  assert.match(backend, /UnityCliDebugInputReasons\.BackendNotLoaded/, "a missing package is reported as backend-not-loaded");
  assert.match(backend, /UnityCliDebugInputReasons\.ApiMemberMissing/, "an unusable API shape is reported as api-member-missing");
  assert.match(backend, /missingMembers\.RemoveRange\(DebugLimits\.MaxInputMissingApiMembers/, "the missing member list is bounded");
  const files = `${host}${backend}${await source(debugRoot, "DebugInputDeviceBackends.cs")}`;
  assert.doesNotMatch(files, /Client\.Add|Packages\/manifest|PackageManager/, "no package is ever installed or changed");
});

test("a managed wrapper is supported only after an armed detour and a real invocation", async () => {
  const report = await source(debugRoot, "DebugInputHost.Report.cs");
  assert.match(report, /var armed = string\.Equals\(best\.State, MethodHookStateText\.Of\(MethodHookState\.Armed\), StringComparison\.Ordinal\);/, "support requires an armed detour");
  assert.match(report, /var invoked = best\.HitCount > 0L;/, "support requires an observed invocation");
  assert.match(report, /if \(!armed\)[\s\S]{0,400}NoLiveDetour/, "an unarmed definition is not support");
  assert.match(report, /if \(!invoked\)[\s\S]{0,500}NoObservedInvocation/, "a never-called wrapper is not support");
  assert.match(report, /cannot drive that input, and it does not use project-specific shortcuts to pretend otherwise/, "the honest statement says the input cannot be driven");
  assert.match(report, /This owner only drives virtual devices it created/, "the ownership statement is reported with the input picture");
  const sharedText = await source(sharedRoot, "UnityCliDebugInputs.cs");
  for (const token of [
    'public const string InputSystem = "input-system"',
    'public const string None = "none"',
    'public const string HoldMs = "hold-ms"',
    'public const string UntilRelease = "until-release"',
    'public const string ScreenPixels = "screen-pixels"',
    'public const string GameViewNormalized = "game-view-normalized"',
    'public const string Held = "held"',
    'public const string Pending = "pending"',
    'public const string Released = "released"',
    'public const string BackendNotLoaded = "backend-not-loaded"',
    'public const string ApiMemberMissing = "api-member-missing"',
    'public const string ReleaseNotApplied = "release-not-applied"',
    'public const string DeviceAbsent = "device-absent"',
    'public const string PauseMainThread = "pause-main-thread"',
    'public const string PauseWorkerDeferred = "pause-worker-deferred"',
    'public const string SessionStop = "session-stop"',
    'public const string PlayExit = "play-exit"',
    'public const string BusinessEntry = "business-entry"',
    'public const string ManagedWrapperInjection = "managed-wrapper-injection"',
    'public const string ThreadSafePointDelivery = "thread-safe-point-delivery"',
  ]) {
    assert.ok(sharedText.includes(token), `UnityCliDebugInputs must declare ${token}`);
  }
});

test("a backend reason is classified before it becomes a public reason", async () => {
  const host = await source(debugRoot, "DebugInputHost.cs");
  const report = await source(debugRoot, "DebugInputHost.Report.cs");
  assert.match(report, /private static string StableReason\(string backendReason, string fallback\)/,
    "one convergence point classifies a backend reason before it is published");
  assert.match(report, /var classification = StableReason\(reported, UnityCliDebugInputReasons\.BackendUnsupported\);/,
    "the refusal reason is the classified value, never the backend text");
  assert.match(report, /\{ "reason", classification \}/, "the error envelope publishes the classified reason");
  assert.match(report, /details\["backendReason"\] = UnityCliBridge\.BoundedText\(diagnostics/,
    "the raw backend text is reported as a bounded diagnostic field");
  // 原始 out reason 只能作为诊断或经分类后使用；直接放进公开 reason 的写法不得存在。
  for (const [name, text] of [["DebugInputHost.cs", host], ["DebugInputHost.Report.cs", report]] as const) {
    for (const raw of ["readReason", "queueFailure", "removeReason", "confirmReason"]) {
      assert.doesNotMatch(text, new RegExp(`\\{ "reason", ${raw} \\}`), `${name} must never publish the raw ${raw} as the public reason`);
    }
  }
  for (const classified of [
    "StableReason(readReason, UnityCliDebugInputReasons.DeviceListUnreadable)",
    "StableReason(removeReason, UnityCliDebugInputReasons.DeviceRemovalRefused)",
    "StableReason(queueFailure, UnityCliDebugInputReasons.DispatchRefused)",
    "StableReason(readReason, UnityCliDebugInputReasons.HoldReadRefused)",
    "StableReason(confirmReason, UnityCliDebugInputReasons.HoldReadRefused)",
    "UnityCliDebugInputReasons.DeviceCreationRefused",
  ]) {
    assert.ok(host.includes(classified) || report.includes(classified), `the owner must classify with ${classified}`);
  }
  const sharedText = await source(sharedRoot, "UnityCliDebugInputs.cs");
  for (const token of [
    "public static bool IsDefined(string value)",
    'public const string DeviceListUnreadable = "device-list-unreadable"',
    'public const string DeviceRemovalRefused = "device-removal-refused"',
    'public const string DeviceCreationRefused = "device-creation-refused"',
    'public const string DispatchRefused = "dispatch-refused"',
    'public const string HoldReadRefused = "hold-read-refused"',
  ]) {
    assert.ok(sharedText.includes(token), `UnityCliDebugInputs must declare ${token}`);
  }
});

test("the input tools are registered with the generated contracts and the error code", async () => {
  const contract = JSON.parse(await readFile(join(repoRoot, "contracts", "command-contract.json"), "utf8")) as { commands: Array<{ bridgeCommand: string; group: string }> };
  const names = new Set(contract.commands.map((entry) => entry.bridgeCommand));
  for (const tool of TOOLS) {
    assert.ok(names.has(`debug.input.${tool}`), `debug.input.${tool} must be in the generated command contract`);
  }
  // 通用输入路径只能指名真实存在的 Tool；这条断言把路由表与 live catalog 绑在一起。
  const host = await source(debugRoot, "DebugInputHost.Report.cs");
  const referenced = [...host.matchAll(/Tool = "([a-z0-9][a-z0-9.-]*)"/g)].map((match) => match[1]);
  assert.ok(referenced.length >= 4, "the route table must name its Tools explicitly");
  for (const name of referenced) {
    assert.ok(names.has(name), `the route table references the real Tool ${name}`);
  }
  const completion = JSON.parse(await readFile(join(repoRoot, "contracts", "tool-completion-contract.json"), "utf8")) as { tools: Array<{ toolName: string; executionClass: string }> };
  for (const tool of TOOLS) {
    const entry = completion.tools.find((candidate) => candidate.toolName === `debug.input.${tool}`);
    assert.ok(entry, `debug.input.${tool} needs one completion contract`);
    assert.equal(entry?.executionClass, "observed-state", "the input family reports observed device state");
  }
  const output = await readFile(join(repoRoot, "src/output.ts"), "utf8");
  assert.match(output, /E_DEBUG_BINDING_UNSUPPORTED: "E_DEBUG_BINDING_UNSUPPORTED"/);
  const codes = await readFile(join(repoRoot, "docs/error-codes.md"), "utf8");
  assert.match(codes, /`E_DEBUG_BINDING_UNSUPPORTED`/);
  const risks = await readFile(join(repoRoot, "docs/risk-and-side-effects.md"), "utf8");
  assert.match(risks, /### Debug input/);
  assert.match(risks, /virtual-input-device/);
});
