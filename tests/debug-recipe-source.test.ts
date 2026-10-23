import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const repoRoot = process.cwd();
const unitypkg = new URL("../unitypkg/", import.meta.url);
const sessionToolsRoot = new URL("Editor/Tools/debug/session/", unitypkg);
const hooksRoot = new URL("Editor/Infrastructure/MethodHooks/", unitypkg);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);

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
      if (depth === 0) return text.slice(open, cursor + 1);
    }
  }
  assert.fail(`unbalanced body for member: ${name}`);
}

test("the recipe tools exist with the frozen catalog shape", async () => {
  const exportText = await source(sessionToolsRoot, "../session-export.cs");
  const importText = await source(sessionToolsRoot, "../session-import.cs");
  assert.match(exportText, /IUnityCliTool<DebugSessionExportRequest, DebugSessionExportResult>/);
  assert.match(importText, /IUnityCliTool<DebugSessionImportRequest, DebugSessionImportResult>/);
  assert.match(exportText, /Name = "debug\.session-export"/);
  assert.match(exportText, /Summary = "Read the current immutable recipe snapshot/);
  assert.match(importText, /Name = "debug\.session-import"/);
  assert.match(importText, /Summary = "Validate a stored recipe and atomically establish a new inactive debug session/);
  for (const text of [exportText, importText]) {
    assert.match(text, /Group = "debug"/);
    assert.match(text, /Target = "editor"/);
    assert.match(text, /PlayModeSupport = "supported"/);
    assert.match(text, /DestructiveHint = false/);
    assert.match(text, /SupportsDryRun = false/, "dry-run must not be claimed for compile-loading import or a read-only snapshot");
    assert.match(text, /DebugLimits\.RecipeSummary\(\)/, "descriptor limits must come from the single debug limit owner");
    assert.doesNotMatch(text, /EditorApplication\.update \+=/);
  }
  assert.match(exportText, /SideEffects = new string\[0\]/, "export writes no artifact and no file");
  assert.match(importText, /SideEffects = new\[\] \{ "debug-session", "artifact-write" \}/);
});

test("export declares read-only snapshot semantics, exclusions, cursor expiry and the 8 MiB bound", async () => {
  const exportText = await source(sessionToolsRoot, "../session-export.cs");
  for (const required of [
    /Read the current immutable recipe snapshot of one debug session as bounded pages, without writing any artifact or file/,
    /writes no file and creates no artifact/,
    /freezes the session's current publication/,
    /SAME snapshot/,
    /live pause sites, object handles, Thread\/Task\/delegate objects, invocation records and unfinished commands/,
    /E_DEBUG_EXPORT_CURSOR_EXPIRED/,
    /E_DEBUG_LIMIT_EXCEEDED/,
    /the actual source text is always included/,
  ]) {
    assert.match(exportText, required);
  }
  const store = await source(debugRoot, "DebugRecipeStore.cs");
  for (const required of [
    /internal const string Format = "unitycli\.debug\.recipe\/1"/,
    /internal const string CursorExpired = "cursor-expired"/,
    /internal const string SnapshotMissing = "snapshot-missing"/,
    /Snapshots\[sessionId\] = snapshot;/,
  ]) {
    assert.match(store, required, "the recipe snapshot owner must declare " + required);
  }
  // 每 session 只有一个快照槽位：替换即失效旧 cursor。
  assert.equal((store.match(/Snapshots\[sessionId\] = snapshot;/g) ?? []).length, 1, "one fixed snapshot slot per session");
  assert.doesNotMatch(store, /File\.Write|DurableWriteText|Directory\.CreateDirectory/, "export must never touch the file system");
});

test("import declares the lifecycle table and reports loaded candidate assemblies", async () => {
  const importText = await source(sessionToolsRoot, "../session-import.cs");
  for (const required of [
    /Domain Reload keeps definitions, source, selected JSON, in-budget evidence and terminal summaries/,
    /Import itself is the only path that establishes a session after an Editor restart/,
    /Exiting Play invalidates the Play run, Play handles, input and pause sites/,
    /no startup or cleanup entry of any program is called/,
    /ReplayedOperations is always empty/,
    /E_DEBUG_RECIPE_INVALID with reason content-invalid, format-unsupported, checksum-missing, checksum-mismatch, path-mismatch, over-budget, excluded-content, file-unreadable or binding-content-invalid/,
    /already loaded candidate assembly count and bytes/,
    /never leaves a half-activatable session/,
  ]) {
    assert.match(importText, required);
  }
  const host = await source(debugRoot, "DebugRecipeHost.cs");
  assert.match(host, /DebugSessionHost\.ApplyCreate\(createPlan, true\)/, "import must establish the identity through the single session host");
  assert.match(host, /DebugProgramHost\.PrepareInstallAsync/, "import must reuse the single program install path");
  assert.match(host, /MethodHookHost\.ImportDormant/, "import must register hooks as dormant definitions");
  assert.match(host, /RollBackImport\(/, "a failed import must roll its own definitions back");
  assert.doesNotMatch(host, /EditorApplication\.update \+=/, "the recipe host must not register its own update callback");
});

test("the old Hook store and its restore path are gone in one pass", async () => {
  const files = await readdir(hooksRoot);
  assert.equal(files.includes("MethodHookStore.cs"), false, "MethodHookStore must be deleted, not kept beside the unified store");
  const entries = await Promise.all(
    files.filter((name) => name.endsWith(".cs")).map(async (name) => [name, await source(hooksRoot, name)] as const),
  );
  for (const [name, text] of entries) {
    assert.doesNotMatch(text, /MethodHookStore/, `${name} must not reference the removed Hook store`);
  }
  const host = await source(hooksRoot, "MethodHookHost.cs");
  assert.match(host, /DebugStore\.ObserveDefinitions\(\)/, "reload must read definitions from the unified debug store");
  assert.match(host, /DebugStore\.Publish\(DebugStore\.DefinitionsPath\(\)/, "definitions must be published through the unified debug store");
  assert.match(host, /definitions-file-unusable:/, "an unusable definitions file must record a gap");
  assert.match(host, /DebugStore\.DeleteOwnedFiles\(\)/, "the unified store owns the shutdown cleanup of its fixed files");

  const store = await source(debugRoot, "DebugStore.cs");
  assert.match(store, /Library", "UnityCli", "debug"/);
  assert.match(store, /internal const string DefinitionsFormat = "unitycli\.debug\.definitions\/1"/);
  assert.match(store, /internal const string EventsFormat = "unitycli\.debug\.events\/1"/);
  assert.match(store, /Remove/);
  assert.match(store, /Directory\.Delete\(root\)/, "the legacy Hook store directory is removed once when the unified store first publishes");
  assert.match(store, /unitycli\.debug\.mechanism\./, "mechanism state must live under debug-domain SessionState keys");
  assert.doesNotMatch(store, /unitycli\.method-hook/, "no legacy mechanism key may survive");
});

test("every new limit is declared in the single debug limit owner", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  for (const value of ["RecipeSnapshotsPerSession = 1", "MaxRecipeBytes = 8 * 1024 * 1024", "RecipePageDefaultBytes = 512 * 1024", "RecipePageMaxBytes = 2 * 1024 * 1024"]) {
    assert.ok(limits.includes(value), `DebugLimits must declare ${value}`);
  }
  const recipeStore = await source(debugRoot, "DebugRecipeStore.cs");
  for (const text of [recipeStore, await source(debugRoot, "DebugRecipeHost.cs")]) {
    assert.doesNotMatch(text, /\bconst (int|long) Max\w*(Recipe|Page)\w* = \d/, "recipe budgets must not be redeclared outside DebugLimits");
  }
});

test("no compatibility path, alias or duplicated truth was added", async () => {
  const host = await source(debugRoot, "DebugRecipeHost.cs");
  const store = await source(debugRoot, "DebugStore.cs");
  for (const text of [host, store]) {
    assert.doesNotMatch(text, /\bTODO\b|\bFIXME\b/, "no placeholder may ship");
    // 兼容层禁词由片段拼接，避免本测试自己成为残留扫描命中。
    assert.doesNotMatch(text, new RegExp("leg" + "acy\\s+(?:fallback|adapter)|" + "兼容" + "分支", "i"), "no compatibility branch may ship");
  }
  const contracts = JSON.parse(await readFile(join(repoRoot, "contracts", "tool-completion-contract.json"), "utf8"));
  const names = contracts.tools.map((entry: { toolName: string }) => entry.toolName);
  assert.ok(names.includes("debug.session-export"), "the completion contract must cover debug.session-export");
  assert.ok(names.includes("debug.session-import"), "the completion contract must cover debug.session-import");
  assert.equal(names.includes("method-hook" + ".events"), false, "the old event route must stay unreachable");
});

test("the recipe hook projection omits live listener identity while the same-session snapshot keeps it", async () => {
  const definition = await source(hooksRoot, "MethodHookDefinition.cs");
  // recipe 与任何外部读取端读的是声明投影：现场线程身份在构造时就不存在，不靠后置过滤。
  // `{ "hookId", HookId }` 只出现在 Hook Definition 自己的 ToData 里，因此它到 stateValues 之间就是该投影。
  const start = definition.indexOf('{ "hookId", HookId }');
  assert.ok(start >= 0, "the Hook Definition declaration projection must still write hookId");
  const declaration = definition.slice(start, definition.indexOf('{ "stateValues"'));
  assert.ok(declaration.includes('{ "listener", Listener == null'), "the declaration projection must carry the listener section");
  assert.doesNotMatch(declaration, /"threadId"/, "a reusable recipe projection must never emit one Editor startup's thread identity");
  assert.match(declaration, /\{ "instanceIdentity"/, "the declared instance identity string stays a declaration, not a managed object identity");
  assert.match(declaration, /\{ "sessionId"/, "the declaration projection keeps the session reference that import re-establishes");
  const snapshot = memberBody(definition, "internal Dictionary<string, object> ToSnapshotData()");
  assert.match(snapshot, /var data = ToData\(\);/, "the same-Editor-session snapshot reuses the one declaration projection");
  assert.match(snapshot, /listener\["threadId"\] = \(long\)Listener\.ThreadId;/, "only the same-session snapshot writes the live thread identity");
  const host = await source(hooksRoot, "MethodHookHost.cs");
  assert.match(host, /result\.Add\(definition\.ToData\(\)\);/, "recipe export must write the declaration projection");
  assert.match(host, /Select\(definition => \(object\)definition\.ToSnapshotData\(\)\)/, "the debug store is the only reader of the live identity");

  // 校验端保持 fail closed：排除键集合一字未改，手写它们仍然被拒绝。
  const store = await source(debugRoot, "DebugRecipeStore.cs");
  const excluded = memberBody(store, "private static readonly HashSet<string> ExcludedKeys");
  for (const frozen of [
    '"pause", "pauseId", "pauseSites", "pauses", "breakpointPauses",',
    '"handles", "handleId", "handleIds", "handleTable",',
    '"threads", "thread", "threadId", "threadIds", "managedTask", "task", "tasks", "taskId",',
    '"delegate", "delegates", "callback", "callbackInstance",',
    '"programRuns", "programRunId", "invocations", "invocationId", "invocation",',
    '"unfinishedCommands", "pendingCommands", "pendingInjections", "requestId", "continuationToken",',
    '"weakReferences", "unityObject", "objectId", "instanceId"',
  ]) {
    assert.ok(excluded.includes(frozen), `the frozen exclusion list must still contain ${frozen}`);
  }
});
