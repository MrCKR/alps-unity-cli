import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { handleCommand } from "../src/cli-commands/command.js";
import { SessionSupervisor, SessionSupervisorError } from "../src/bridge/session-supervisor.js";
import { EditorBlockerObserver } from "../src/editor-blocker-observer.js";
import { failure } from "../src/output.js";
import "../src/tools/.generated-registry.js";
import "../src/tools/.generated-schemas.js";
import { resolveCatalogIndependentInvocationTool, resolveCatalogIndependentOperabilityTool } from "../src/tools/catalog.js";
import { dispatchTool, enrichTargetFailureWithDialogBlocker } from "../src/tools/dispatcher.js";
import { findToolByName } from "../src/tools/metadata/index.js";

const OPERABILITY_TOOLS = ["editor.state", "editor.dialogs", "editor.dialog.click"] as const;

test("only exact Editor operability tools own catalog-independent descriptors", () => {
  for (const name of OPERABILITY_TOOLS) {
    const resolved = resolveCatalogIndependentOperabilityTool(name);
    assert.deepEqual(resolved, findToolByName(name));
    assert.equal(resolved?.handler, "offline");
  }
  for (const name of ["dialogs", "editor.compile", "docs.get", "package.list"])
    assert.equal(resolveCatalogIndependentOperabilityTool(name), undefined, name);
});

test("tools run validates Editor operability execution without a live catalog", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-dialog-recovery-routing-"));
  try {
    for (const name of OPERABILITY_TOOLS) {
      const result = await dispatchTool(name, undefined, { input: "{}", "dry-run": true }, { dataDir }, `req_${name.replaceAll(".", "_")}`);
      assert.equal(result.ok, false, name);
      if (!result.ok) {
        assert.equal(result.error.code, "E_DRY_RUN_UNSUPPORTED", name);
        assert.deepEqual(result.error.details, { toolName: name });
      }
    }
  } finally {
    await rm(dataDir, { recursive: true, force: true });
  }
});

test("exact Editor operability definitions bypass business admission while another offline Tool does not", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-observation-lane-"));
  let acquires = 0;
  let revokes = 0;
  const supervisor = {
    acquireRootAdmission: async () => {
      acquires += 1;
      throw new SessionSupervisorError("E_UNITY_BUSY", "admission spy", [], { businessRequestSent: false }, true, 100);
    },
    completeRootAdmission: async () => { revokes += 1; },
  } as unknown as SessionSupervisor;
  const runtime = { dataDir, projectTools: [], createSessionSupervisor: () => supervisor };
  try {
    const cases = [
      ["editor.state", {}],
      ["editor.dialogs", {}],
      ["editor.dialog.click", { dialogId: "dialog_test", button: "Close" }],
    ] as const;
    for (const [name, input] of cases) {
      const result = await dispatchTool(name, undefined, { input: JSON.stringify(input), instance: "missing" }, runtime, `req_lane_${name.replaceAll(".", "_")}`);
      assert.equal(result.ok, false, name);
      if (!result.ok) assert.notEqual(result.error.code, "E_UNITY_BUSY", name);
    }
    assert.equal(acquires, 0);
    assert.equal(revokes, 0);

    const ordinary = await dispatchTool("docs.get", undefined, { input: JSON.stringify({ api: "UnityEngine.Camera", offlineOnly: true }) }, runtime, "req_lane_ordinary");
    assert.equal(ordinary.ok, false);
    if (!ordinary.ok) assert.equal(ordinary.error.code, "E_UNITY_BUSY");
    assert.equal(acquires, 1);
    assert.equal(revokes, 0);
  } finally {
    await rm(dataDir, { recursive: true, force: true });
  }
});

test("tools describe learns exact Editor operability contracts without a live catalog", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-dialog-recovery-describe-"));
  try {
    for (const name of OPERABILITY_TOOLS) {
      assert.deepEqual(resolveCatalogIndependentInvocationTool("tools.describe", name), findToolByName("tools.describe"));
      const result = await dispatchTool("tools.describe", undefined, { input: "{}" }, { dataDir, projectTools: [] }, `req_describe_${name.replaceAll(".", "_")}`, { name });
      assert.equal(result.ok, true, JSON.stringify(result));
      if (result.ok) {
        const data = result.data as { name: string; inputSchema?: unknown };
        assert.equal(data.name, name);
        assert.ok(data.inputSchema);
      }
    }
    assert.equal(resolveCatalogIndependentInvocationTool("tools.describe", "scene.hierarchy"), undefined);
  } finally {
    await rm(dataDir, { recursive: true, force: true });
  }
});

test("catalog failure leaves exact operability execution and describe routable but fails other names", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-operability-catalog-failure-"));
  try {
    for (const [name, input] of [
      ["editor.state", {}],
      ["editor.dialogs", {}],
      ["editor.dialog.click", { dialogId: "dialog_test", button: "Close" }],
    ] as const) {
      const execution = await dispatchTool(name, undefined, { input: JSON.stringify(input), instance: "missing" }, { dataDir }, `req_catalog_${name.replaceAll(".", "_")}`);
      assert.equal(execution.ok, false, name);
      if (!execution.ok) assert.notEqual(execution.error.code, "E_TOOL_NOT_FOUND_FOR_TARGET", name);

      const describe = await dispatchTool("tools.describe", undefined, { input: "{}" }, { dataDir }, `req_catalog_describe_${name.replaceAll(".", "_")}`, { name });
      assert.equal(describe.ok, true, JSON.stringify(describe));
    }

    const ordinary = await dispatchTool("docs.get", undefined, { input: JSON.stringify({ api: "UnityEngine.Camera", offlineOnly: true }), instance: "missing" }, { dataDir }, "req_catalog_ordinary");
    assert.equal(ordinary.ok, false);
    if (!ordinary.ok) assert.ok(["E_INSTANCE_NOT_FOUND", "E_INSTANCE_NOT_RUNNING"].includes(ordinary.error.code), ordinary.error.code);
  } finally {
    await rm(dataDir, { recursive: true, force: true });
  }
});

test("dialog blocker enrichment consumes only the supplied root deadline", async () => {
  const original = failure("req_enrich", "E_INSTANCE_NOT_RUNNING", "offline", ["Open Unity."]);
  let now = 1_000;
  let selectorCalls = 0;
  let observerCalls = 0;
  const selected = {
    instance: {
      instanceId: `unity-${"a".repeat(64)}`,
      canonicalProjectPath: "c:\\repo\\project",
      displayProjectPath: "C:\\repo\\project",
      activeSession: undefined,
    },
    matchedBy: "instanceId",
  } as never;
  const observer = { observe: async (_identity: unknown, deadlineMs: number) => {
    observerCalls += 1;
    assert.ok(deadlineMs <= 1_500);
    return { outcome: "observed-blocked", blockers: [{
      type: "dialog", dialogId: "dialog_test", title: "Blocked", message: "Blocked", buttons: ["OK"],
      requiresUserAction: true, source: "windows-ui", confidence: "exact",
    }] };
  } } as unknown as EditorBlockerObserver;
  const selector = async (_flags: unknown, _dataDir: string, remainingMs: number) => {
    selectorCalls += 1;
    assert.ok(remainingMs <= 500);
    now += 100;
    return selected;
  };
  const enriched = await enrichTargetFailureWithDialogBlocker(original, {}, { dataDir: "test" }, 1_500, observer, selector as never, () => now);
  assert.equal(enriched.ok, false);
  assert.equal(selectorCalls, 1);
  assert.equal(observerCalls, 1);

  selectorCalls = 0;
  observerCalls = 0;
  const exhausted = await enrichTargetFailureWithDialogBlocker(original, {}, { dataDir: "test" }, now, observer, selector as never, () => now);
  assert.deepEqual(exhausted, original);
  assert.equal(selectorCalls, 0);
  assert.equal(observerCalls, 0);
});

test("dialog click keeps its minimum pre-effect reserve before commitEffect", async () => {
  const source = await readFile("src/tools/editor/dialog/click.ts", "utf8");
  const reserve = source.indexOf("deadlineMs - Date.now() < CLICK_DIALOG_MIN_EXECUTION_BUDGET_MS");
  const commit = source.indexOf("await context.commitEffect?.()");
  assert.ok(reserve >= 0);
  assert.ok(commit > reserve);
});

test("command help and execution reuse Editor operability descriptors without a live catalog", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-dialog-recovery-command-"));
  try {
    for (const name of OPERABILITY_TOOLS) {
      const help = await handleCommand([name], { help: true }, { dataDir }, `req_help_${name.replaceAll(".", "_")}`);
      assert.equal(help.ok, true, JSON.stringify(help));
      if (help.ok) {
        const data = help.data as { command: { name: string }; tool: { inputSchema?: unknown } };
        assert.equal(data.command.name, name);
        assert.ok(data.tool.inputSchema);
      }
      const businessTokens = name === "editor.dialog.click" ? ["--dialogId", "dialog_test", "--button", "Close"] : [];
      const execution = await handleCommand([name, ...businessTokens], { "dry-run": true }, { dataDir }, `req_command_${name.replaceAll(".", "_")}`);
      assert.equal(execution.ok, false, name);
      if (!execution.ok) assert.equal(execution.error.code, "E_DRY_RUN_UNSUPPORTED", name);
    }
  } finally {
    await rm(dataDir, { recursive: true, force: true });
  }
});
