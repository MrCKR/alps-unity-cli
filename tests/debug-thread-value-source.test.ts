import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const repoRoot = process.cwd();
const unitypkg = new URL("../unitypkg/", import.meta.url);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const debugTools = new URL("Editor/Tools/debug/", unitypkg);
const hooksRoot = new URL("Editor/Infrastructure/MethodHooks/", unitypkg);

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
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

test("the three M2 value and thread tools declare their exact public route and risk", async () => {
  const expectations = [
    { file: "thread/list.cs", name: "debug.thread.list", sideEffects: null },
    { file: "value/read.cs", name: "debug.value.read", sideEffects: null },
    { file: "value/write.cs", name: "debug.value.write", sideEffects: 'SideEffects = new[] { "runtime-object-modify" }' },
  ] as const;
  for (const expectation of expectations) {
    const text = await source(debugTools, expectation.file);
    assert.match(text, /IUnityCliTool<Debug\w+Request, Debug\w+Result>/, `${expectation.file} must implement the typed Tool interface`);
    assert.match(text, new RegExp(`Name = "${expectation.name.replaceAll(".", "\\.")}"`));
    assert.match(text, /Group = "debug"/);
    assert.match(text, /DestructiveHint = false/);
    assert.match(text, /Target = "editor"/);
    assert.match(text, /PlayModeSupport = "supported"/);
    assert.doesNotMatch(text, /DestructiveHint = true|SupportsDryRun = true/);
    assert.doesNotMatch(text, /EditorApplication\.update \+=/, "the debug value domain must not register its own update callback");
    if (expectation.sideEffects === null) {
      assert.doesNotMatch(text, /SideEffects =/, `${expectation.name} must declare no side effect`);
    } else {
      assert.ok(text.includes(expectation.sideEffects), `${expectation.name} must declare exactly ${expectation.sideEffects}`);
    }
  }

  const reader = await source(debugTools, "value/read.cs");
  const writer = await source(debugTools, "value/write.cs");
  const threadList = await source(debugTools, "thread/list.cs");
  assert.doesNotMatch(reader, /ctx\.CommitEffect\(\)/, "debug.value.read is a pure read and must not cross the effect barrier");
  assert.doesNotMatch(threadList, /ctx\.CommitEffect\(\)/, "debug.thread.list is a pure read and must not cross the effect barrier");
  assert.match(writer, /ctx\.CommitEffect\(\);/, "debug.value.write must cross the single effect barrier");
  assert.match(writer, /DebugValueSupport\.PrepareWrite\(request/, "all write validation must run before the barrier");
  assert.match(writer, /DebugValueSupport\.ApplyWrite\(plan, request\)/, "the write effect must run after the barrier");
  for (const text of [reader, writer, threadList]) {
    assert.doesNotMatch(text, /File\.|Directory\./, "value and thread tools must not perform file IO");
    assert.match(text, /DebugLimits\.(ValueSummary|ThreadSummary)\(\)/, "each descriptor must publish the single limit summary owner");
  }
});

test("the thread observation never claims to enumerate managed threads", async () => {
  const threads = await source(debugRoot, "DebugThreads.cs");
  assert.match(threads, /EnumeratesAllManagedThreads = false/);
  assert.match(threads, /not an enumeration of every managed thread/);
  assert.match(threads, /Process\.GetCurrentProcess|process thread list is never used as a managed thread set/);
  assert.doesNotMatch(threads, /Process\.GetCurrentProcess\(\)/, "the process thread list must never be used as the managed thread set");
  assert.match(threads, /DebugEventStore\.Snapshot\(\)/, "event records come from the single event store owner");
  assert.match(threads, /MethodHookHost\.ObservedThreadFrames\(\)/, "frame records come from the hook dispatch owner");
  assert.match(threads, /DebugLimits\.ThreadListDefaultLimit/);
  assert.match(threads, /DebugLimits\.ThreadListMaxLimit/);

  const observation = await source(hooksRoot, "MethodHookHost.ThreadObservation.cs");
  assert.match(observation, /internal static partial class MethodHookHost/);
  assert.match(observation, /ThreadFrames/);
  assert.doesNotMatch(observation, /TryRemove|TryAdd|Push\(|Pop\(/, "the observation face must not mutate dispatch state");
  assert.doesNotMatch(observation, /Time\.frameCount/);
});

test("the clock owner is the only place that reads the Unity frame and keeps the off-main rule", async () => {
  const clocks = await source(debugRoot, "DebugClockProvenance.cs");
  const eventClocks = await source(debugRoot, "DebugEventClocks.cs");
  assert.match(clocks, /DebugEventClocks\.ReadUnityFrame\(\)/);
  assert.doesNotMatch(clocks, /Time\.frameCount/, "only the debug clock owner may touch Time.frameCount");
  assert.match(clocks, /DebugEventClocks\.LastObservedMainFrame/);
  assert.match(clocks, /IsCurrentThreadMainThread\(\)/);
  assert.match(clocks, /UnityCliDebugClocks\.Monotonic|UnityCliDebugClocks\.EditorTick|UnityCliDebugClocks\.UnityFrame|UnityCliDebugClocks\.ProjectLogical/);
  assert.match(clocks, /Editor tick counter, not a Unity frame/);
  assert.match(clocks, /Off-main code must never read the Unity frame count/);
  assert.match(eventClocks, /off-main 代码绝不读取 Unity 帧号/);
  assert.match(eventClocks, /必须改用 LastObservedMainFrame 与自己的单调采集时间/);

  const names = [...clocks.matchAll(/UnityCliDebugClocks\.(Monotonic|EditorTick|UnityFrame|ProjectLogical)\b/g)].map((match) => match[1]);
  for (const kind of ["Monotonic", "EditorTick", "UnityFrame", "ProjectLogical"]) {
    assert.ok(names.includes(kind), `the clock owner must formalise ${kind}`);
  }
});

test("value reads refuse side-effectful reads and route the caller to explicit code execution", async () => {
  const support = await source(debugRoot, "DebugValueSupport.cs");
  for (const rule of [
    "property-getter-not-executed",
    "member-not-a-field",
    "array-index-required",
    "static-type-not-initialized",
    "handle-requires-reference-root",
    "root-object-write-not-supported",
    "logical-clock-value-not-numeric",
    "guard-requires-comparable-member",
    "path-member-null",
  ]) {
    assert.ok(support.includes(`"${rule}"`), `the refusal rule ${rule} must be declared`);
  }
  assert.match(support, /"E_DEBUG_SCOPE_UNSUPPORTED"/);
  assert.match(support, /a structured read never executes property getters, arbitrary ToString, user serializers or enumerators, and never triggers type initialisation/);
  assert.match(support, /TypeInitializer == null/, "the static field rule must be based on the declared static constructor");
  assert.match(support, /internal static FieldInfo FindField\(/);
  assert.match(support, /TryReserve|GetProperty/, "a property must be detected so it can be refused");
  assert.match(support, /No field named/);
  assert.match(support, /E_DEBUG_TARGET_STALE/);
  assert.match(support, /stale handle is never reported as a null field value|its state is reported instead of returning a null field value/);
  assert.match(support, /"intermediate-null"/);
});

test("the value projection never runs user code and reuses the declared projection bounds", async () => {
  const projection = await source(debugRoot, "DebugValueProjection.cs");
  assert.match(projection, /IsFrameworkContainer\(/);
  assert.match(projection, /DebugLimits\.MaxProjectionDepth/);
  assert.match(projection, /DebugLimits\.MaxProjectionItems/);
  assert.match(projection, /DebugLimits\.MaxProjectionStringBytes/);
  assert.match(projection, /no-user-code-projection/);
  assert.doesNotMatch(projection, /value\.ToString\(\)|Convert\.ToString\(value/, "the projection must not call arbitrary ToString");
  assert.doesNotMatch(projection, /UnityCliSerializer/, "the projection must not run a user serializer");
});

test("handles are weak by default, bound to runtime and generation, and released with the session", async () => {
  const handles = await source(debugRoot, "DebugHandleRegistry.cs");
  assert.match(handles, /Weak = strong \? null : new WeakReference\(target\)/);
  assert.match(handles, /StrongTarget = strong \? target : null/);
  assert.match(handles, /"runtime-changed"/);
  assert.match(handles, /"domain-generation-changed"/);
  assert.match(handles, /"target-collected"/);
  assert.match(handles, /"unity-object-destroyed"/);
  assert.match(handles, /DebugLimits\.MaxHandlesPerSession/);
  assert.match(handles, /DebugLimits\.MaxHandlesGlobal/);
  assert.match(handles, /PruneCollectedLocked\(\)/);
  assert.match(handles, /Strong handle: it keeps the target alive/);
  assert.doesNotMatch(handles, /EditorApplication\.update \+=/);
});

test("conditional restore is registered into the single session cleanup pipeline", async () => {
  const restore = await source(debugRoot, "DebugValueRestore.cs");
  assert.match(restore, /DebugSessionHost\.RegisterCleanupStep\(/);
  assert.match(restore, /UnityCliDebugCleanupSteps\.ReleaseWaits|UnityCliDebugCleanupSteps\.ExplicitCleanup/);
  assert.match(restore, /report\.RestoredFields\.Add/);
  assert.match(restore, /report\.ConflictingFields\.Add/);
  assert.match(restore, /report\.ReleasedHandles \+=/);
  assert.match(restore, /the current value does not equal the value this session wrote/);
  assert.doesNotMatch(restore, /MethodHookStore/, "restore must not create a second definition truth");
});

test("the debug domain still declares every limit in exactly one place", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  for (const value of [
    "MaxValuePathLength = 16",
    "MaxHandlesPerSession = 256",
    "MaxHandlesGlobal = 1024",
    "ThreadListDefaultLimit = 50",
    "ThreadListMaxLimit = 200",
  ]) {
    assert.ok(limits.includes(value), `DebugLimits must declare ${value}`);
  }
  assert.match(limits, /internal static string ThreadSummary\(\)/);
  assert.match(limits, /internal static string ValueSummary\(\)/);
  for (const file of ["thread/list.cs", "value/read.cs", "value/write.cs"]) {
    const text = await source(debugTools, file);
    assert.doesNotMatch(text, /MaxHandlesPerSession = 256|MaxHandlesGlobal = 1024|MaxValuePathLength = 16/, `${file} must not restate a limit`);
  }
});

test("the value read declares a derived request budget and charges one getter per reflected member", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  assert.match(limits, /internal const int ValueReadSerializations = 3;/, "the framework serializes the same result three times per request");
  assert.match(limits, /internal static long ValueReadGetters\(\)/, "DebugLimits must expose the derived value read budget");
  assert.match(
    limits,
    /internal const int MaxNamedLogicalClocks = \(int\)\(\(ValueReadBudgetShareGetters\s*\n?\s*- ValueReadBudgetReserveGetters/,
    "the named logical clock registry must be derived from the value read budget share instead of the projection item bound",
  );
  assert.match(
    limits,
    /ValueReadBudgetShareGetters = UnityCliRequestBudget\.DefaultMaxGetters \* 3L \/ 4L/,
    "the value read share must be derived from the default request budget",
  );
  assert.match(limits, /ValueReadClocksWithoutPublish \* ValueReadClockMembers\s*\n?\s*\+ ValueReadHandleBudgetMembers/, "the fixed per-pass member count must be derived from the DTO member counts");
  assert.match(limits, /return ValueReadSerializations \* membersPerPass \+ ValueReadBudgetReserveGetters;/, "the derived budget must be passes times members plus a fixed reserve");

  const reader = await source(debugTools, "value/read.cs");
  assert.match(reader, /ctx\.UseReadBudget\(new UnityCliReadBudgetInput \{ MaxGetters = DebugLimits\.ValueReadGetters\(\) \}\)/, "debug.value.read must declare its derived read budget");

  const support = await source(debugRoot, "DebugValueSupport.cs");
  const projection = await source(debugRoot, "DebugValueProjection.cs");
  for (const text of [support, projection]) {
    assert.doesNotMatch(text, /TryConsumeGetter/, "the pure read path must not charge getters itself or scan a whole type");
  }

  const serializer = await source(new URL("Editor/Infrastructure/", unitypkg), "UnityCliSerializer.cs");
  const writeBody = memberBody(serializer, "private static void WriteBudgetedValue(");
  assert.equal((writeBody.match(/TryConsumeGetter\(\)/g) ?? []).length, 1, "exactly one getter is charged per reflected member actually read");
  assert.ok(
    writeBody.indexOf("foreach (var member in BudgetedSerializableMembers") < writeBody.indexOf("TryConsumeGetter()"),
    "the getter must be charged inside the member loop instead of per projection pass",
  );
  const memberScan = memberBody(serializer, "private static IReadOnlyList<MemberInfo> BudgetedSerializableMembers(");
  assert.doesNotMatch(memberScan, /TryConsumeGetter/, "scanning a whole type's members must not charge getters");
});

test("debug.method.find reports the declaring assembly so hook installation needs no guess", async () => {
  const reflection = await source(new URL("Editor/Infrastructure/", unitypkg), "UnityCliReflection.cs");
  assert.match(reflection, /public string AssemblyName \{ get; set; \}/);
  assert.match(reflection, /AssemblyName = assemblyName/);
  assert.match(reflection, /method\.DeclaringType\.Assembly\.GetName\(\)\.Name/);
  assert.match(reflection, /TryReserveSerializedUtf8\(assemblyName\)/, "the new member must consume the shared reflection budget");
  const find = await source(debugTools, "method/find.cs");
  assert.match(find, /assemblyName of the declaring type|assemblyName/);
  assert.match(find, /debug\.hook\.install/);
});

test("the write path decides writability and the guard before the single effect barrier", async () => {
  const support = await source(debugRoot, "DebugValueSupport.cs");
  const writer = await source(debugTools, "value/write.cs");
  const limits = await source(debugRoot, "DebugLimits.cs");

  const prepareIndex = writer.indexOf("DebugValueSupport.PrepareWrite(request, ctx.UseReadBudget())");
  const barrierIndex = writer.indexOf("ctx.CommitEffect();");
  const applyIndex = writer.indexOf("DebugValueSupport.ApplyWrite(plan, request)");
  assert.ok(prepareIndex >= 0, "every write validation must run before the barrier");
  assert.ok(prepareIndex < barrierIndex, "the pre-effect phase must return before the effect barrier");
  assert.ok(barrierIndex < applyIndex, "the write effect must run after the barrier");

  const validate = memberBody(support, "internal static DebugValueWritePlan ValidateWrite(");
  assert.match(validate, /RequireWritableTargets\(plan\);/, "const and readonly fields must be decided during pre-effect validation");
  assert.match(validate, /PrepareHandle\(request, plan, write\);/, "the handle target and its budget must be decided before the barrier");
  assert.match(validate, /TryCompareGuard\(/, "the expectedValue guard must be evaluated before the barrier");
  assert.match(validate, /write\.GuardSatisfied = equal;/, "the guard verdict must be part of the pre-effect plan");
  assert.ok(
    validate.indexOf("write.GuardSatisfied = equal;") < validate.indexOf("return write;"),
    "the guard verdict must be complete before the pre-effect phase returns",
  );
  assert.match(validate, /"path-member-null"/, "a null path member is a pre-effect refusal for a write");
  assert.match(validate, /TryReadPlanValue\(plan, out current, out readFailure, out nullAtPath\)/, "the effect-time-before value is read in the pre-effect phase");

  const apply = memberBody(support, "internal static DebugValueWriteResult ApplyWrite(");
  assert.doesNotMatch(apply, /Refuse\(/, "the effect phase must not contain pre-effect refusals");
  assert.doesNotMatch(apply, /TryReadPlanValue/, "the effect phase must not re-read or re-resolve the target");
  assert.doesNotMatch(
    apply,
    /handle-requires-reference-root|root-object-write-not-supported|guard-requires-comparable-member|field-not-writable/,
    "every pre-visible cause belongs to the pre-effect phase",
  );
  assert.match(apply, /throw PostEffectWriteFailure\(plan, writeFailure\);/, "a genuine mid-write failure is reported as post-effect uncertainty");

  assert.match(support, /"E_DEBUG_FIELD_NOT_WRITABLE"/);
  assert.match(support, /field\.IsLiteral/);
  assert.match(support, /field\.IsInitOnly/);
  assert.match(support, /"field-is-literal"/);
  assert.match(support, /"field-is-init-only"/);
  assert.match(support, /effectMayBePartial/, "a mid-write failure must mark the possible partial effect");

  assert.match(limits, /internal static long ValueWriteGetters\(\)/);
  assert.match(limits, /return ValueWriteSerializations \* membersPerPass \+ ValueReadBudgetReserveGetters;/, "the write bound must be derived from the result shape plus the shared reserve");
  assert.match(limits, /internal const int ValueWriteSerializations = ValueReadSerializations;/, "write and read share the framework serialization passes");
  assert.match(limits, /internal const int ValueWriteClocks = ValueReadClocksWithoutPublish;/, "a write result publishes no named logical clock");
  assert.doesNotMatch(writer, /MaxGetters = DebugLimits\.ValueWriteGetters\(\)/, "the write path must not tighten its own ledger below the shared default");
  assert.match(writer, /DebugLimits\.ValueWriteGetters\(\)/, "the descriptor-less bound must still be declared by the single limits owner");

  const budget = await source(new URL("Shared/", unitypkg), "UnityCliRequestBudget.cs");
  assert.match(budget, /DefaultMaxGetters = 2048L/, "the shared default request budget must not be raised");
});

test("the new stable error codes are registered once", async () => {
  const output = await readFile(join(repoRoot, "src", "output.ts"), "utf8");
  const docs = await readFile(join(repoRoot, "docs", "error-codes.md"), "utf8");
  for (const code of ["E_DEBUG_TARGET_STALE", "E_DEBUG_SCOPE_UNSUPPORTED", "E_DEBUG_FIELD_NOT_WRITABLE"]) {
    assert.match(output, new RegExp(`^  ${code}: "${code}",$`, "m"), `${code} must be registered in ERROR_CODES`);
    assert.ok(docs.includes(`\`${code}\``), `${code} must be documented`);
  }
  const risk = await readFile(join(repoRoot, "docs", "risk-and-side-effects.md"), "utf8");
  assert.ok(risk.includes("runtime-object-modify"), "the write side effect must be documented");
  assert.ok(risk.includes("conditional restore"), "the conditional restore behaviour must be documented");
});

test("every new tool is covered by the completion contract with its exact source", async () => {
  const contract = JSON.parse(await readFile(join(repoRoot, "contracts", "tool-completion-contract.json"), "utf8"));
  const expected: Record<string, string> = {
    "debug.thread.list": "unitypkg/Editor/Tools/debug/thread/list.cs",
    "debug.value.read": "unitypkg/Editor/Tools/debug/value/read.cs",
    "debug.value.write": "unitypkg/Editor/Tools/debug/value/write.cs",
  };
  for (const [toolName, sourcePath] of Object.entries(expected)) {
    const entry = contract.tools.find((item: { toolName: string }) => item.toolName === toolName);
    assert.ok(entry, `${toolName} must appear in the completion contract`);
    assert.equal(entry.source, sourcePath);
    assert.equal(entry.descriptorOwner, "built-in");
    assert.equal(entry.deliveryMode, "direct-terminal");
    assert.ok(entry.completionPredicate?.kind && entry.proofSource?.kind);
  }
});

test("the value write path keeps the struct write-back and guard semantics in the source", async () => {
  const support = await source(debugRoot, "DebugValueSupport.cs");
  const write = await source(debugTools, "value/write.cs");
  assert.match(support, /TryWritePlanValue\(/);
  assert.match(support, /array\.SetValue\(child, segment\.ArrayIndex\)/);
  assert.match(support, /plan\.RootStaticField\.SetValue\(null, child\)/);
  assert.match(support, /"guard-mismatch"|Outcome = applied \? "applied" : "guard-mismatch"/);
  assert.match(support, /UnityCliValueConverter\.ConvertValue\(request\.Value, plan\.LeafType\)/);
  assert.match(write, /structWriteBack/);
  assert.match(write, /guard-mismatch/);
  assert.match(write, /not a property setter and not a method invoker/);
});

test("a published named clock can be released and the tests restore the shared clock state", async () => {
  // 回归护栏：命名逻辑时钟表是同一 domain 内共享的。如果发布者停止后无法释放，或测试不在
  // 每个用例后清空，测试数据会继续出现在真实的 debug.value.read 结果里并占用有限名额。
  const clocks = await source(debugRoot, "DebugClockProvenance.cs");
  assert.match(clocks, /internal static bool Release\(string name\)/);
  assert.match(clocks, /internal static void Clear\(\)/);
  const tests = await source(new URL("Tests/Editor/", unitypkg), "DebugThreadValueTests.cs");
  assert.match(tests, /\[TearDown\][\s\S]{0,200}DebugLogicalClocks\.Clear\(\);/);
});
