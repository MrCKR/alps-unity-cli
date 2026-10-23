import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const repoRoot = process.cwd();
const unitypkg = new URL("../unitypkg/", import.meta.url);
const sessionToolsRoot = new URL("Editor/Tools/debug/session/", unitypkg);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const sharedRoot = new URL("Shared/", unitypkg);

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

const TOOLS = ["create", "info", "list", "stop", "remove"];

test("the five debug.session tools exist with the declared descriptor shape", async () => {
  for (const tool of TOOLS) {
    const text = await source(sessionToolsRoot, `../session-${tool}.cs`);
    assert.match(text, /IUnityCliTool<DebugSession\w+Request, DebugSession\w+Result>/, `${tool}.cs must implement the typed Tool interface`);
    assert.match(text, new RegExp(`Name = "debug\\.session-${tool}"`), `${tool}.cs must declare its exact public name`);
    assert.match(text, /Group = "debug"/);
    assert.match(text, /DestructiveHint = false/);
    assert.match(text, /Target = "editor"/);
    assert.match(text, /PlayModeSupport = "supported"/);
    assert.match(text, /DebugLimits\.SessionSummary\(\)/, `${tool}.cs must publish the single limit summary`);
    assert.doesNotMatch(text, /DestructiveHint = true|SupportsDryRun = true/);
    assert.doesNotMatch(text, /EditorApplication\.update \+=/, "the debug session domain must not register its own update callback");
  }
  const info = await source(sessionToolsRoot, "../session-info.cs");
  const list = await source(sessionToolsRoot, "../session-list.cs");
  for (const reader of [info, list]) assert.doesNotMatch(reader, /SideEffects =/);
  for (const writer of ["create", "stop", "remove"]) {
    const text = await source(sessionToolsRoot, `../session-${writer}.cs`);
    assert.match(text, /ctx\.CommitEffect\(\);/, `${writer}.cs must cross the single effect barrier`);
  }
  assert.match(
    await source(sessionToolsRoot, "../session-create.cs"),
    /SideEffects = new\[\] \{ "debug-session", "log-subscription" \}/,
    "debug.session-create must declare the frozen log-subscription side effect",
  );
  for (const writer of ["stop", "remove"]) {
    const text = await source(sessionToolsRoot, `../session-${writer}.cs`);
    assert.match(text, /SideEffects = new\[\] \{ "debug-session" \}/);
  }
  for (const reader of [info, list]) assert.doesNotMatch(reader, /ctx\.CommitEffect\(\)/);
});

test("debug.session-create declares log capture through the single log subscription owner", async () => {
  const create = await source(sessionToolsRoot, "../session-create.cs");
  assert.match(create, /LogCapture = new DebugLogCaptureSpec/, "create must document the log capture input");
  const dtos = await source(sessionToolsRoot, "DebugSessionDtos.cs");
  assert.match(dtos, /public sealed class DebugLogCaptureSpec/);
  assert.match(dtos, /public DebugLogCaptureSpec LogCapture \{ get; set; \}/);
  // 线程化日志订阅只有一个 owner：只有该文件可以订阅 logMessageReceivedThreaded。
  const listener = await source(debugRoot, "DebugListenerHost.cs");
  const subscribers = (listener.match(/Application\.logMessageReceivedThreaded \+=/g) ?? []).length;
  assert.equal(subscribers, 1, "the whole domain must subscribe to the threaded log callback exactly once");
  assert.match(listener, /Application\.logMessageReceivedThreaded -= OnLogMessage/);
  assert.match(listener, /Sessions\.Count > 0 \|\| !subscribed/, "the last released session must unsubscribe");
  assert.doesNotMatch(listener, /EditorApplication\.update \+=/, "the log owner must not register a private update callback");
  assert.doesNotMatch(listener, /Application\.logMessageReceived \+=/, "only the threaded callback is allowed");
});

test("the session domain declares every budget in the single debug limit owner", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  for (const value of [
    "MaxSessions = 16",
    "MaxActiveResourcesPerSession = 4",
    "MaxSessionStateBytes = 32 * 1024",
    "MaxSessionStoreBytes = 8L * 1024 * 1024",
    "SessionListDefaultLimit = 50",
    "SessionListMaxLimit = 200",
  ]) {
    assert.ok(limits.includes(value), `DebugLimits must declare ${value}`);
  }
  assert.match(limits, /internal static string SessionSummary\(\)/);
  for (const tool of TOOLS) {
    const text = await source(sessionToolsRoot, `../session-${tool}.cs`);
    assert.doesNotMatch(text, /MaxSessions = 16|MaxActiveResourcesPerSession = 4|MaxSessionStateBytes =/, `${tool}.cs must not restate a session limit`);
  }
});

test("the debug domain keeps exactly one session store file", async () => {
  const store = await source(debugRoot, "DebugSessionStore.cs");
  assert.match(store, /Path\.Combine\(Root\(\), "session\.json"\)/);
  assert.match(store, /Library", "UnityCli", "debug"/);
  assert.match(store, /UnityCliBridge\.DurableWriteText\(/);
  assert.match(store, /PublicationGate/);
  const host = await source(debugRoot, "DebugSessionHost.cs");
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  assert.doesNotMatch(`${store}${host}${shared}`, /method-hooks|MethodHookStore/, "the session domain must not own or migrate the live hook store");
  for (const tool of TOOLS) {
    const text = await source(sessionToolsRoot, `../session-${tool}.cs`);
    assert.doesNotMatch(text, /File\.|Directory\./, `${tool}.cs must not perform file IO itself`);
  }
});

test("the cleanup pipeline order and the no-owner marker are declared once", async () => {
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  const ordered = shared.match(/public static string\[\] Ordered\(\)\s*\{\s*return new\[\]\s*\{([\s\S]*?)\};/);
  assert.ok(ordered, "the frozen cleanup order must be declared");
  const names = [...ordered[1].matchAll(/\b(StopDispatch|CancelAndResume|ReleaseInput|CloseObservation|DetachHooks|ExplicitCleanup|ReleaseWaits|EmitReport)\b/g)].map((match) => match[1]);
  assert.deepEqual(names, ["StopDispatch", "CancelAndResume", "ReleaseInput", "CloseObservation", "DetachHooks", "ExplicitCleanup", "ReleaseWaits", "EmitReport"]);
  assert.match(shared, /public const string NoOwnerRegistered = "no owner registered"/);
  const owners = shared.match(/public static string\[\] All\(\)\s*\{\s*return new\[\] \{([^}]*)\};/);
  assert.ok(owners);
  for (const owner of ["Hook", "Program", "Input", "Pause", "Run", "Observation"]) {
    assert.ok(owners[1].includes(owner), `the owner set must include ${owner}`);
  }
});

test("the ownership registry exposes reserve, attach, release and cleanup registration", async () => {
  const host = await source(debugRoot, "DebugSessionHost.cs");
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  for (const api of ["ReserveResource", "AttachResource", "ReleaseResource", "RegisterCleanupStep"]) {
    assert.match(host, new RegExp(`internal static [A-Za-z<>?., \\[\\]]+ ${api}\\(`), `the facility entry point ${api} must exist`);
  }
  assert.match(shared, /public UnityCliDebugResourceReservation Reserve\(/);
  assert.match(shared, /public bool Attach\(string resourceId\)/);
  assert.match(shared, /public bool Release\(string resourceId\)/);
  assert.match(shared, /public string RegisterCleanupStep\(/);
  assert.match(shared, /public string CanCreate\(/);
  assert.match(shared, /public string CanRemove\(/);
});

test("the session store is fail closed on every declared unusable condition", async () => {
  const shared = await source(sharedRoot, "UnityCliDebugSessions.cs");
  const store = await source(debugRoot, "DebugSessionStore.cs");
  for (const reason of [
    "format-mismatch",
    "editor-session-mismatch",
    "domain-generation-mismatch",
    "checksum-missing",
    "checksum-mismatch",
    "publication-regressed",
    "publication-invalid",
    "session-index-invalid",
  ]) {
    assert.ok(shared.includes(reason), `TryRestore must report ${reason}`);
  }
  assert.match(shared, /public const string Format = "unitycli\.debug\.session\/1"/);
  assert.match(shared, /TryRestore\(IDictionary<string, object> payload/);
  const host = await source(debugRoot, "DebugSessionHost.cs");
  assert.match(host, /reason = "file-empty"/);
  assert.match(host, /reason = "file-over-budget"/);
  assert.match(host, /reason = "file-unparsable"/);
  assert.match(store, /LogUnusable/);
  assert.match(shared, /RestoredWithoutActivation = !string\.Equals\(generation, domainGeneration/, "restored definitions must not come back activated");
  assert.match(shared, /CleanupPending = false,/, "runtime-only cleanup state must not be restored");
  assert.match(host, /DebugSessionStore\.PublishedGeneration\(\)/, "a Domain Reload transition must be recognised from the recorded previous generation");
});

test("the new public error codes are registered once", async () => {
  const output = await readFile(join(repoRoot, "src", "output.ts"), "utf8");
  const docs = await readFile(join(repoRoot, "docs", "error-codes.md"), "utf8");
  for (const code of [
    "E_DEBUG_LIMIT_EXCEEDED",
    "E_DEBUG_CLEANUP_PENDING",
    "E_DEBUG_SESSION_NOT_FOUND",
    "E_DEBUG_SESSION_LABEL_CONFLICT",
    "E_DEBUG_SESSION_SCOPE_CONFLICT",
    "E_DEBUG_STORE_FAILED",
  ]) {
    assert.match(output, new RegExp(`^  ${code}: "${code}",$`, "m"), `${code} must be registered in ERROR_CODES`);
    assert.ok(docs.includes(`\`${code}\``), `${code} must be documented`);
  }
  const host = await source(debugRoot, "DebugSessionHost.cs");
  const returned = new Set([...host.matchAll(/"(E_[A-Z0-9_]+)"/g)].map((match) => match[1]));
  for (const code of returned) {
    const declared = ["E_DEBUG_LIMIT_EXCEEDED", "E_DEBUG_CLEANUP_PENDING", "E_DEBUG_SESSION_NOT_FOUND", "E_DEBUG_SESSION_LABEL_CONFLICT", "E_DEBUG_SESSION_SCOPE_CONFLICT", "E_DEBUG_STORE_FAILED",
      "E_SCHEMA_INVALID", "E_CONTINUATION_STALE"].includes(code);
    assert.ok(declared, `${code} must be a declared stable code`);
  }
});

test("every session tool is covered by the completion contract", async () => {
  const contract = JSON.parse(await readFile(join(repoRoot, "contracts", "tool-completion-contract.json"), "utf8"));
  for (const tool of TOOLS) {
    const entry = contract.tools.find((item: { toolName: string }) => item.toolName === `debug.session-${tool}`);
    assert.ok(entry, `debug.session-${tool} must appear in the completion contract`);
    assert.equal(entry.source, `unitypkg/Editor/Tools/debug/session-${tool}.cs`);
    assert.equal(entry.descriptorOwner, "built-in");
    assert.equal(entry.deliveryMode, "direct-terminal");
  }
});

test("debug.session-list binds its state filter into the continuation scope and the tools state the slot semantics", async () => {
  const dtos = await source(sessionToolsRoot, "DebugSessionDtos.cs");
  assert.match(
    dtos,
    /\[UnityCliStringEnum\("open", "stopping", "stopped"\)\] public string State \{ get; set; \}/,
    "the list request must declare the closed state filter",
  );
  const host = await source(debugRoot, "DebugSessionHost.cs");
  assert.match(host, /var stateFilter = request == null \? null : request\.State;/, "list must read the state filter before paging");
  assert.match(
    host,
    /editorSessionId \+ "\\0debug\.session-list\\0" \+ \(stateFilter \?\? string\.Empty\)/,
    "the filter must be part of the continuation scope so a cursor cannot be reused across filters",
  );
  assert.match(
    host,
    /state stopped to find definitions/,
    "the session limit rejection must point at the reclaimable filter",
  );
  assert.match(
    await source(sessionToolsRoot, "../session-create.cs"),
    /holds one session slot from this moment until debug\.session-remove deletes it/,
    "create must state that the slot is held until remove",
  );
  assert.match(
    await source(sessionToolsRoot, "../session-list.cs"),
    /state stopped is the filter that finds reclaimable definitions/,
    "list must state which filter finds reclaimable definitions",
  );
  assert.match(
    await source(sessionToolsRoot, "../session-remove.cs"),
    /This is the only action that releases the session definition slot/,
    "remove must state that it is the only action that releases the slot",
  );
});
