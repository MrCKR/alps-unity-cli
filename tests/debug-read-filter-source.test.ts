import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const unitypkg = new URL("../unitypkg/", import.meta.url);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const sharedRoot = new URL("Shared/", unitypkg);
const toolsRoot = new URL("Editor/Tools/debug/", unitypkg);

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

test("debug.events.read exposes the identity, sequence and thread filters it maps into the store filter", async () => {
  const dtos = await source(debugRoot, "DebugEventDtos.cs");
  for (const field of ["FromSequence", "ToSequence", "InvocationId", "ExecutionPoint", "MethodIdentityPrefix", "MainThread"]) {
    assert.match(
      dtos,
      new RegExp(`public [A-Za-z<>?., ]+ ${field} \\{ get; set; \\}`),
      `DebugEventsRequest must expose ${field}`,
    );
  }

  const store = await source(debugRoot, "DebugEventStore.cs");
  for (const mapping of [
    "FromSequence = request.FromSequence",
    "ToSequence = request.ToSequence",
    "InvocationId = request.InvocationId",
    "ExecutionPoint = request.ExecutionPoint",
    "MethodIdentityPrefix = request.MethodIdentityPrefix",
    "MainThread = request.MainThread",
  ]) {
    assert.ok(store.includes(mapping), `the public filter must map into the store filter: ${mapping}`);
  }
  assert.match(store, /fromSequence must not be greater than toSequence/, "an inverted sequence range must be rejected before scanning");
});

test("every new event filter participates in both matching and the continuation scope", async () => {
  const shared = await source(sharedRoot, "UnityCliDebugEvents.cs");
  const filter = shared.slice(shared.indexOf("class UnityCliDebugEventFilter"), shared.indexOf("class UnityCliDebugEventCapacity"));
  for (const field of ["FromSequence", "ToSequence", "InvocationId", "ExecutionPoint", "MethodIdentityPrefix", "MainThread"]) {
    assert.match(filter, new RegExp(`public [A-Za-z<>?., ]+ ${field};`), `the store filter must declare ${field}`);
  }
  for (const scopeKey of ["seqFrom=", "seqTo=", "invocation=", "point=", "identityPrefix=", "mainThread="]) {
    assert.ok(filter.includes(scopeKey), `the canonical scope must bind ${scopeKey}`);
  }
  assert.match(filter, /entry\.Sequence < FromSequence\.Value/);
  assert.match(filter, /entry\.Sequence > ToSequence\.Value/);
  assert.match(filter, /entry\.InvocationId != InvocationId\.Value/);
  assert.match(filter, /entry\.MethodIdentity\.StartsWith\(MethodIdentityPrefix/);
  assert.match(filter, /entry\.MainThread != MainThread\.Value/);
});

test("debug.run.list filters by verdict and detail availability and binds them into its cursor scope", async () => {
  const dtos = await source(new URL("run/", toolsRoot), "DebugRunDtos.cs");
  assert.match(dtos, /\[UnityCliStringEnum\(UnityCliDebugRunVerdicts\.Passed, UnityCliDebugRunVerdicts\.Failed, UnityCliDebugRunVerdicts\.Indeterminate\)\]/);
  assert.match(dtos, /public string Verdict \{ get; set; \}/);
  assert.match(dtos, /public bool\? DetailAvailable \{ get; set; \}/);

  const host = await source(debugRoot, "DebugRunHost.cs");
  const list = host.slice(host.indexOf("internal static DebugRunListResult List"), host.indexOf("// compare"));
  assert.ok(list.includes('(verdict ?? string.Empty)'), "the cursor scope must include the verdict filter");
  assert.ok(list.includes("detailAvailable.HasValue ? (detailAvailable.Value"), "the cursor scope must include the detail availability filter");
  assert.match(list, /summary\.Verdict, verdict/);
  assert.match(list, /summary\.DetailAvailable != detailAvailable\.Value/);
  assert.match(list, /nextOffset = index \+ 1;/, "the cursor must advance over examined windows rather than over returned items");
});

test("debug.pause.list filters by state, invocation and thread ownership and binds them into its cursor scope", async () => {
  const dtos = await source(new URL("pause/", toolsRoot), "DebugPauseDtos.cs");
  assert.match(dtos, /\[UnityCliStringEnum\("paused", "resuming"\)\] public string State \{ get; set; \}/);
  assert.match(dtos, /public long\? InvocationId \{ get; set; \}/);
  assert.match(dtos, /public bool\? MainThread \{ get; set; \}/);

  const host = await source(debugRoot, "DebugPauseHost.cs");
  const list = host.slice(host.indexOf("internal static DebugPauseListResult List"), host.indexOf("internal static DebugPauseSiteView View"));
  assert.match(list, /string\.Equals\(site\.State, state/);
  assert.match(list, /site\.InvocationId == invocationId\.Value/);
  assert.match(list, /site\.MainThread == mainThread\.Value/);
  assert.ok(list.includes("UnityCliContinuationToken.Fingerprint("), "the pause list cursor must be bound to its filters");
  assert.ok(list.includes("UnityCliContinuationToken.Create(scope"), "the pause list cursor must be created from that scope");
});

test("debug.console.read reports a budget-stopped scan instead of failing with no result", async () => {
  const support = await source(new URL("Editor/Tools/editor/", unitypkg), "EditorToolSupport.cs");
  const page = support.slice(support.indexOf("internal static ConsoleReadPage ReadConsolePage(\n            string typeFilter"), support.indexOf("internal interface IConsoleSnapshotAdapter"));
  assert.ok(!page.includes("throw new UnityCliRequestBudgetExceededException"), "an exhausted empty scan must not throw");
  assert.match(page, /ScanExhausted = true/);
  assert.match(page, /ScannedEntries = examined/);
  assert.match(support, /public bool ScanExhausted \{ get; set; \}/);
  assert.match(support, /public long ScannedEntries \{ get; set; \}/);

  const tool = await source(new URL("console/", toolsRoot), "read.cs");
  assert.match(tool, /public bool ScanExhausted \{ get; set; \}/);
  assert.match(tool, /public long ScannedEntries \{ get; set; \}/);
  assert.match(tool, /ScanExhausted = page\.ScanExhausted/);
  assert.ok(tool.includes("cannot publish") || tool.includes("fails with E_REQUEST_BUDGET_LIMIT_EXCEEDED"), "describe must state that a fully consumed budget cannot publish a result");
  assert.ok(tool.includes("can only be lowered"), "describe must state that the budget can only be lowered");
  assert.ok(!tool.includes("larger count or budget"), "describe must not promise a larger budget");
});

test("a budget rejection names a resume path that the tool actually has", async () => {
  const registry = await source(new URL("Editor/ProjectTools/", unitypkg), "ProjectToolRegistry.cs");
  assert.ok(!registry.includes("continue from the returned continuation when available"), "the rejection must not promise a continuation unconditionally");
  assert.ok(registry.includes("If this tool's described result exposes nextCursor"), "the rejection must make the continuation conditional");
  assert.ok(registry.includes("non-resumable"), "the rejection must name the non-resumable case");
});

test("the debug group and its tools use one term for evidence windows", async () => {
  const summaries = await readFile(new URL("../src/tools/tools/group-summaries.js", import.meta.url), "utf8");
  const debugLine = summaries.split("\n").find((line) => line.trimStart().startsWith("debug:"));
  assert.ok(debugLine, "the group summary map must declare the debug group");
  assert.ok(debugLine!.includes("evidence windows"), "the debug group summary must use the same term as debug.run.*");
  assert.ok(!debugLine!.includes("evidence runs"), "the debug group summary must not use a second term for the same object");
});

test("run list budget counters describe the scope they were read in", async () => {
  const host = await source(debugRoot, "DebugRunHost.cs");
  const budget = host.slice(host.indexOf("private static DebugRunBudgetView BudgetView"), host.indexOf("private static DebugRunBudgetView BudgetView") + 1200);
  assert.match(budget, /foreach \(var pair in RunsBySession\)/, "budget counters must aggregate the reported scope instead of staying zero without a session filter");
  assert.match(budget, /used \+= pair\.Value\.Count/, "budget counters must count windows in every reported session");

  const tool = await source(new URL("run/", toolsRoot), "list.cs");
  assert.ok(tool.includes("describe the requested session, or every session when sessionId is omitted"), "describe must state the counter scope");
});

test("run list states which field decides a failed window", async () => {
  const tool = await source(new URL("run/", toolsRoot), "list.cs");
  assert.ok(tool.includes("A window counts as failed only when its verdict is failed"), "describe must name the failed criterion");
  assert.ok(tool.includes("not the window verdict"), "describe must separate failedCheckCount from the verdict");
});
