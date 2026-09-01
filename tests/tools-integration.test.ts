import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRequest, RootAdmissionLease } from "../src/bridge/index.js";
import { execute } from "../src/cli.js";
import { staticBridgeToolMetadata } from "../src/tools/catalog.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { LocalEffectBarrier, completeUnconsumedRootAdmission, settleToolExecutionAtDeadline } from "../src/tools/dispatcher.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

const disconnected = { dataDir: "Z:/definitely-missing-unity-cli-data" } as const;
const injectedBridge = async (payload: BridgeRequest) => ({ ok: true as const, requestId: payload.requestId, data: {} });
const connected = { ...disconnected, projectTools: staticBridgeToolMetadata(), runtimeTools: [], bridgeExecute: injectedBridge } as const;

test("tools group, search, and describe use one connected instance catalog", async () => {
  const group = await execute(["tools", "group"], connected as never);
  const search = await execute(["tools", "search", "hierarchy"], connected as never);
  const describe = await execute(["tools", "describe", "scene.hierarchy"], connected as never);

  assert.ok(group.ok);
  assert.ok(search.ok);
  assert.ok(describe.ok);
  assert.equal((describe.data as { name: string }).name, "scene.hierarchy");
  assert.ok((describe.data as { inputSchema?: unknown }).inputSchema);
  for (const field of ["outputSchema", "group", "permissions", "available", "contractProfile", "summary"])
    assert.equal(Object.hasOwn(describe.data as object, field), false, field);
});

test("canonical tools run tools.describe resolves its typed input without live catalog discovery", async () => {
  const result = await execute(["tools", "run", "tools.describe", "--input", '{"name":"editor.state"}'], disconnected as never);
  assert.equal(result.ok, true, JSON.stringify(result));
  if (result.ok) {
    assert.equal((result.data as { name: string }).name, "editor.state");
    assert.ok((result.data as { inputSchema?: unknown }).inputSchema);
  }
});

test("tools search returns sorted routing-only pages without input or source echoes", async () => {
  const result = await execute(["tools", "search", "hierarchy|console|screenshot", "--page-size", "100"], connected as never);
  assert.ok(result.ok, JSON.stringify(result));
  const data = result.data as { pageInfo: { cursor: number; pageSize: number; total: number; nextCursor?: number }; tools: Array<Record<string, unknown>> };
  assert.equal(data.pageInfo.cursor, 0);
  assert.equal(data.pageInfo.pageSize, 100);
  assert.equal(data.pageInfo.total, data.tools.length);
  assert.equal(data.pageInfo.nextCursor, undefined);
  for (const tool of data.tools) assert.deepEqual(Object.keys(tool).sort(), ["group", "name", "summary"]);
  const names = data.tools.map((tool) => tool.name as string);
  assert.deepEqual(names, [...names].sort());
  assert.ok(names.includes("scene.hierarchy"));
  assert.ok(names.some((name) => name.includes("console")));
  assert.ok(names.some((name) => name.startsWith("screenshot.") || name === "camera.screenshot"));
});

test("tools search trims and filters tokens, matches every routing field case-insensitively, and handles single/empty/no-match", async () => {
  const runtime = {
    ...connected,
    projectTools: [
      makeLiveTool({ name: "fixture.name-hit", summary: "First route.", description: "Name field fixture.", group: "alpha" }),
      makeLiveTool({ name: "fixture.summary", summary: "Summary-Hit route.", description: "Summary field fixture.", group: "beta" }),
      makeLiveTool({ name: "fixture.description", summary: "Third route.", description: "Contains Description-Hit marker.", group: "gamma" }),
      makeLiveTool({ name: "fixture.group", summary: "Fourth route.", description: "Group field fixture.", group: "group-hit" }),
    ],
    runtimeTools: [],
  };

  const multi = await execute(["tools", "search", "  NAME-HIT || summary-hit | DESCRIPTION-HIT | group-hit | "], runtime as never);
  assert.ok(multi.ok, JSON.stringify(multi));
  const names = new Set((multi.data as { tools: Array<{ name: string }> }).tools.map((tool) => tool.name));
  for (const name of ["fixture.name-hit", "fixture.summary", "fixture.description", "fixture.group"]) assert.ok(names.has(name), name);

  const single = await execute(["tools", "search", "  SuMmArY-Hit  "], runtime as never);
  assert.ok(single.ok, JSON.stringify(single));
  assert.deepEqual((single.data as { tools: Array<{ name: string }> }).tools.map((tool) => tool.name), ["fixture.summary"]);

  const noMatch = await execute(["tools", "search", "definitely-absent-search-token"], runtime as never);
  assert.ok(noMatch.ok, JSON.stringify(noMatch));
  assert.deepEqual(noMatch.data, { pageInfo: { cursor: 0, pageSize: 20, total: 0 }, tools: [] });

  const empty = await execute(["tools", "search", "||  |"], runtime as never);
  assert.ok(!empty.ok);
  assert.equal(empty.error?.code, "TOOLS_SEARCH_KEYWORD_REQUIRED");
});

test("tools group list returns exact sorted names and omits true availability", async () => {
  const result = await execute(["tools", "group", "list", "scene", "--page-size", "100"], connected as never);
  assert.ok(result.ok, JSON.stringify(result));
  const data = result.data as { pageInfo: { total: number }; tools: Array<{ name: string; available?: false }> };
  assert.equal(data.pageInfo.total, data.tools.length);
  const names = data.tools.map((tool) => tool.name);
  assert.deepEqual(names, [...names].sort());
  assert.ok(data.tools.every((tool) => !tool.name.startsWith("scene/") && !Object.hasOwn(tool, "available")));
});

test("discovery pagination returns stable continuation pages and rejects invalid bounds", async () => {
  const projectTools = Array.from({ length: 25 }, (_, index) => makeLiveTool({
    name: `fixture.page-${String(index).padStart(2, "0")}`,
    summary: `Page-hit ${index}.`,
    description: "Pagination fixture.",
    group: "page-fixture",
    ...(index === 24 ? { available: false, unavailableReason: "fixture unavailable" } : {}),
  }));
  const runtime = { ...connected, projectTools, runtimeTools: [] };

  const first = await execute(["tools", "search", "page-hit"], runtime as never);
  assert.ok(first.ok, JSON.stringify(first));
  const firstData = first.data as { pageInfo: { cursor: number; pageSize: number; total: number; nextCursor?: number }; tools: Array<{ name: string }> };
  assert.deepEqual(firstData.pageInfo, { cursor: 0, pageSize: 20, total: 25, nextCursor: 20 });
  assert.equal(firstData.tools.length, 20);

  const second = await execute(["tools", "search", "page-hit", "--cursor", "20"], runtime as never);
  assert.ok(second.ok, JSON.stringify(second));
  const secondData = second.data as { pageInfo: { cursor: number; pageSize: number; total: number }; tools: Array<{ name: string }> };
  assert.deepEqual(secondData.pageInfo, { cursor: 20, pageSize: 20, total: 25 });
  assert.deepEqual(secondData.tools.map((tool) => tool.name), projectTools.slice(20).map((tool) => tool.name));

  const group = await execute(["tools", "group", "list", "page-fixture", "--page-size", "10", "--cursor", "20"], runtime as never);
  assert.ok(group.ok, JSON.stringify(group));
  const groupData = group.data as { pageInfo: { cursor: number; pageSize: number; total: number }; tools: Array<{ name: string; available?: false }> };
  assert.deepEqual(groupData.pageInfo, { cursor: 20, pageSize: 10, total: 25 });
  assert.equal(groupData.tools.length, 5);
  assert.equal(groupData.tools.at(-1)?.available, false);

  const beyond = await execute(["tools", "search", "page-hit", "--cursor", "100"], runtime as never);
  assert.ok(beyond.ok, JSON.stringify(beyond));
  assert.deepEqual(beyond.data, { pageInfo: { cursor: 100, pageSize: 20, total: 25 }, tools: [] });

  for (const argv of [
    ["tools", "search", "page-hit", "--page-size", "101"],
    ["tools", "group", "list", "page-fixture", "--cursor", "-1"],
  ]) {
    const invalid = await execute(argv, runtime as never);
    assert.equal(invalid.ok, false, JSON.stringify(invalid));
    if (!invalid.ok) assert.equal(invalid.error.code, "E_SCHEMA_INVALID");
  }
});

test("target both keeps default editor availability when runtime is not ready", async () => {
  const editor = makeLiveTool({
    name: "sample.both",
    summary: "Read from either execution path.",
    description: "Read from the default editor path or an explicitly requested runtime path.",
    group: "sample",
    source: "project",
    target: "both",
    available: true,
  });
  const runtime = { ...editor, available: false, unavailableReason: "E_RUNTIME_NOT_READY: Enter Play Mode before executing this Tool." };
  const result = await execute(["tools", "describe", "sample.both"], {
    ...connected,
    projectTools: [editor],
    runtimeTools: [runtime],
  } as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.equal(Object.hasOwn(result.data as object, "available"), false);
  assert.equal(Object.hasOwn(result.data as object, "unavailableReason"), false);
});

test("tools catalog对UnityPipeline保留namespace违规fail closed", async () => {
  const malformed = [
    makeLiveTool({
      name: "UnityPipeline_native",
      summary: "Invalid native prefix.",
      description: "A native Tool cannot occupy the official attribute prefix.",
      group: "project",
      source: "project",
      contractProfile: "native",
    }),
    makeLiveTool({
      name: "project.invalid-group",
      summary: "Invalid native group.",
      description: "A native Tool cannot occupy the official attribute group.",
      group: "UnityPipeline",
      source: "project",
      contractProfile: "native",
    }),
    makeLiveTool({
      name: "validation.attribute",
      summary: "Invalid attribute identity.",
      description: "An official attribute Tool must use both reserved identity fields.",
      group: "UnityPipeline",
      source: "project",
      contractProfile: "official-attribute",
    }),
  ];

  for (const tool of malformed) {
    const result = await execute(["tools", "group"], {
      ...disconnected,
      projectTools: [tool],
    } as never);
    assert.equal(result.ok, false, tool.name);
    if (!result.ok) assert.equal(result.error.code, "E_TOOL_SCHEMA_CONFLICT", tool.name);
  }
});

test("重复official attribute原名使整个live catalog fail closed", async () => {
  const first = makeLiveTool({
    name: "UnityPipeline_duplicate",
    summary: "First duplicate attribute.",
    description: "First attributed method with the same original command name.",
    group: "UnityPipeline",
    source: "project",
    contractProfile: "official-attribute",
    liveDescriptorFingerprint: "a".repeat(64),
  });
  const second = makeLiveTool({
    ...first,
    summary: "Second duplicate attribute.",
    description: "Second attributed method with the same original command name.",
    liveDescriptorFingerprint: "b".repeat(64),
  });
  const result = await execute(["command"], {
    ...connected,
    projectTools: [first, second],
  } as never);

  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_TOOL_SCHEMA_CONFLICT");
});

test("tools run fails closed on a local and live ownership conflict", async () => {
  for (const name of ["tools.group", "code.exec"]) {
    const conflicting = makeLiveTool({
      name,
      summary: "Conflicting live owner.",
      description: "Conflicting live registration for a package-local Tool name.",
      group: name.split(".")[0],
      source: "project",
    });
    const result = await execute(["tools", "run", name, "--input", "{}"], {
      ...disconnected,
      projectTools: [conflicting],
      runtimeTools: [],
      bridgeExecute: async () => { throw new Error("ownership conflict must fail before Bridge dispatch"); },
    } as never);

    assert.equal(result.ok, false, name);
    if (!result.ok) assert.equal(result.error.code, "E_TOOL_SCHEMA_CONFLICT", name);
  }
});

test("TS-local Tool returns one validated direct business terminal", async () => {
  const result = await execute(["tools", "run", "tools.group", "--input", "{}"], connected as never);
  assert.ok(result.ok, JSON.stringify(result));
  assert.ok(Array.isArray((result.data as { groups?: unknown[] }).groups));
  assert.equal(Object.hasOwn(result.data as object, "command"), false);
  assert.equal(Object.hasOwn(result.data as object, "execution"), false);
});

test("shared Tool deadline settles a non-returning mutation without claiming cancellation", async () => {
  const startedAt = Date.now();
  const result = await settleToolExecutionAtDeadline(
    "fixture.never",
    "req_deadline_fixture",
    true,
    startedAt + 25,
    () => new Promise(() => undefined),
  );

  assert.equal(result.ok, false);
  if (result.ok) return;
  assert.equal(result.requestId, "req_deadline_fixture");
  assert.equal(result.error.code, "E_EXECUTION_TIMEOUT");
  assert.deepEqual(result.error.details, { outcome: "unknown" });
  assert.equal(result.error.transient, false);
  assert.equal(result.error.retryAfterMs, 0);
  assert.match(result.error.message, /may still be running or partially applied/);
  assert.equal(result.error.nextActions.length, 1);
  assert.match(result.error.nextActions[0], /Inspect editor\.state and the affected business state/);
  assert.doesNotMatch(result.error.nextActions[0], /retry|repeat|replay/i);
  assert.ok(Date.now() - startedAt < 1_000);
});

test("tool-owned timeout result revokes an unconsumed lease exactly once before the framework timer", async () => {
  let cleanupCalls = 0;
  let revokes = 0;
  const admission = {
    requestId: "req_tool_timeout_cleanup",
    command: "fixture.local",
    rootFingerprint: "f".repeat(64),
    completionContractIdentity: "atomic-mutation-v1",
    target: "editor",
    instanceId: `unity-${"a".repeat(64)}`,
    canonicalProjectPath: "c:\\repo\\project",
    sessionId: `session-${"b".repeat(64)}`,
    token: "a".repeat(64),
    consumed: false,
    grantExpiresAtMs: Date.now() + 2_000,
    executionAbsoluteDeadlineMs: Date.now() + 1_000,
    injected: false,
  } as RootAdmissionLease;
  const result = await settleToolExecutionAtDeadline("fixture.local", admission.requestId, true, admission.executionAbsoluteDeadlineMs, async () => ({
    ok: false,
    requestId: admission.requestId,
    error: { code: "E_EXECUTION_TIMEOUT", message: "tool-owned timeout", nextActions: [], transient: false, retryAfterMs: 0 },
  }), {
    cleanupAfterLateSettlement: async () => {
      cleanupCalls += 1;
      await completeUnconsumedRootAdmission(admission, { completeRootAdmission: async () => { revokes += 1; } });
    },
  });
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_EXECUTION_TIMEOUT");
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(cleanupCalls, 1);
  assert.equal(revokes, 1);
});

test("late local settlement cleans an unconsumed lease exactly once without changing the timeout terminal", async () => {
  let settle: (() => void) | undefined;
  let cleanupCalls = 0;
  let revokes = 0;
  let runCalls = 0;
  const admission = {
    requestId: "req_late_cleanup",
    command: "fixture.local",
    rootFingerprint: "f".repeat(64),
    completionContractIdentity: "atomic-read-v1",
    target: "editor",
    instanceId: `unity-${"a".repeat(64)}`,
    canonicalProjectPath: "c:\\repo\\project",
    sessionId: `session-${"b".repeat(64)}`,
    token: "a".repeat(64),
    consumed: false,
    grantExpiresAtMs: Date.now() + 2_000,
    executionAbsoluteDeadlineMs: Date.now() + 100,
    injected: false,
  } as RootAdmissionLease;
  const deferred = new Promise<void>((resolve) => { settle = resolve; });
  const result = await settleToolExecutionAtDeadline("fixture.local", admission.requestId, false, admission.executionAbsoluteDeadlineMs, async () => {
    runCalls += 1;
    await deferred;
    return { ok: true, requestId: admission.requestId, data: { late: true } };
  }, {
    cleanupAfterLateSettlement: async () => {
      cleanupCalls += 1;
      await completeUnconsumedRootAdmission(admission, { completeRootAdmission: async () => { revokes += 1; } });
    },
  });
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_EXECUTION_TIMEOUT");
  settle?.();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(runCalls, 1);
  assert.equal(cleanupCalls, 1);
  assert.equal(revokes, 1);
});

test("late cleanup never revokes a lease consumed before local settlement", async () => {
  let settle: (() => void) | undefined;
  let cleanupCalls = 0;
  let revokes = 0;
  const admission = {
    requestId: "req_consumed_cleanup",
    command: "fixture.local",
    rootFingerprint: "f".repeat(64),
    completionContractIdentity: "atomic-mutation-v1",
    target: "editor",
    instanceId: `unity-${"a".repeat(64)}`,
    canonicalProjectPath: "c:\\repo\\project",
    sessionId: `session-${"b".repeat(64)}`,
    token: "a".repeat(64),
    consumed: false,
    grantExpiresAtMs: Date.now() + 2_000,
    executionAbsoluteDeadlineMs: Date.now() + 100,
    injected: false,
  } as RootAdmissionLease;
  const deferred = new Promise<void>((resolve) => { settle = resolve; });
  const result = await settleToolExecutionAtDeadline("fixture.local", admission.requestId, true, admission.executionAbsoluteDeadlineMs, async () => {
    await deferred;
    return { ok: true, requestId: admission.requestId, data: { late: true } };
  }, {
    cleanupAfterLateSettlement: async () => {
      cleanupCalls += 1;
      await completeUnconsumedRootAdmission(admission, { completeRootAdmission: async () => { revokes += 1; } });
    },
  });
  assert.equal(result.ok, false);
  admission.consumed = true;
  settle?.();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(cleanupCalls, 1);
  assert.equal(revokes, 0);
});

test("public timeout closes the first local effect barrier and absorbs late rejection", async () => {
  let continueRun: (() => void) | undefined;
  const barrier = new LocalEffectBarrier("fixture.effect", Date.now() + 100);
  const deferred = new Promise<void>((resolve) => { continueRun = resolve; });
  const unhandled: unknown[] = [];
  const onUnhandled = (reason: unknown): void => { unhandled.push(reason); };
  process.on("unhandledRejection", onUnhandled);
  try {
    const result = await settleToolExecutionAtDeadline("fixture.effect", "req_effect_barrier", true, Date.now() + 100, async () => {
      await deferred;
      await barrier.commitEffect();
      return { ok: true, requestId: "req_effect_barrier", data: { crossed: true } };
    }, { closeEffectBarrier: barrier.close });
    assert.equal(result.ok, false);
    if (!result.ok) assert.equal(result.error.code, "E_EXECUTION_TIMEOUT");
    continueRun?.();
    await new Promise((resolve) => setImmediate(resolve));
    assert.deepEqual(unhandled, []);
    await assert.rejects(() => barrier.commitEffect(), (error: unknown) => error instanceof Error && (error as { code?: string }).code === "E_EXECUTION_TIMEOUT");
  } finally {
    process.off("unhandledRejection", onUnhandled);
  }
});

test("settlement rechecks the wall-clock deadline before accepting a late success", async () => {
  let now = 10_000;
  let continueRun: (() => void) | undefined;
  let started: (() => void) | undefined;
  let effects = 0;
  const barrier = new LocalEffectBarrier("fixture.clock", 10_100, () => now);
  const ready = new Promise<void>((resolve) => { started = resolve; });
  const deferred = new Promise<void>((resolve) => { continueRun = resolve; });
  const pending = settleToolExecutionAtDeadline("fixture.clock", "req_clock_deadline", true, 10_100, async () => {
    started?.();
    await deferred;
    await barrier.commitEffect();
    effects += 1;
    return { ok: true, requestId: "req_clock_deadline", data: { late: true } };
  }, { now: () => now, closeEffectBarrier: barrier.close });
  await ready;
  now = 10_100;
  continueRun?.();
  const result = await pending;
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_EXECUTION_TIMEOUT");
  assert.equal(effects, 0);
  await assert.rejects(() => barrier.commitEffect(), (error: unknown) => error instanceof Error && (error as { code?: string }).code === "E_EXECUTION_TIMEOUT");
});

test("settlement rejects a late success before the timer callback and does not run its effect", async () => {
  let now = 20_000;
  let continueRun: (() => void) | undefined;
  let started: (() => void) | undefined;
  const ready = new Promise<void>((resolve) => { started = resolve; });
  const deferred = new Promise<void>((resolve) => { continueRun = resolve; });
  const pending = settleToolExecutionAtDeadline("fixture.late", "req_late_success", true, 20_100, async () => {
    started?.();
    await deferred;
    return { ok: true, requestId: "req_late_success", data: { late: true } };
  }, { now: () => now });
  await ready;
  now = 20_100;
  continueRun?.();
  const result = await pending;
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_EXECUTION_TIMEOUT");
});

test("shared Tool deadline preserves a terminal returned before the deadline", async () => {
  const terminal = { ok: true as const, requestId: "req_terminal_fixture", data: { value: 1 } };
  const result = await settleToolExecutionAtDeadline(
    "fixture.fast",
    terminal.requestId,
    false,
    Date.now() + 1_000,
    async () => terminal,
  );
  assert.deepEqual(result, terminal);
});

test("Tool input must be a strict JSON object before dispatch", async () => {
  for (const input of ["[]", "true", "null", "{bad-json"]) {
    let calls = 0;
    const result = await execute(["tools", "run", "go.find", "--input", input], {
      ...connected,
      bridgeExecute: async () => { calls += 1; throw new Error("must not dispatch"); },
    } as never);
    assert.ok(!result.ok, input);
    assert.ok(["E_SCHEMA_INVALID", "E_TOOL_INPUT_JSON_INVALID"].includes(result.error?.code ?? ""), `${input}: ${result.error?.code}`);
    assert.equal(calls, 0, input);
  }
});

test("live built-in Tool routes once through project-tool.run", async () => {
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "go.find", "--input", "{\"name\":\"Nothing\"}"], {
    ...connected,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: {
          result: {
            includeInactive: false,
            items: [],
            matchCount: 0,
            pageInfo: { cursor: 0, pageSize: 100, total: 0 },
            prefabStageOpen: false,
            preferPrefabStage: false,
            query: "Nothing",
            searchMethod: "by_name",
          },
        },
      };
    },
  } as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input, { name: "go.find", input: { name: "Nothing" } });
  assert.equal((result.data as { matchCount: number }).matchCount, 0);
});

test("unknown tools and subcommands return stable errors with current search guidance", async () => {
  for (const name of ["missing.tool", "docs.list", "docs.topic"]) {
    const missing = await execute(["tools", "run", name, "--input", "{}"], connected as never);
    assert.ok(!missing.ok);
    assert.equal(missing.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
    assert.ok(missing.error?.nextActions.includes("Run unitycli tools search <k1|k2|...> to find related tools."));
  }

  for (const argv of [["tools", "run"], ["tools", "describe", "missing.tool"]]) {
    const missing = await execute(argv, connected as never);
    assert.ok(!missing.ok);
    assert.equal(missing.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
    assert.ok(missing.error?.nextActions.some((action) => action.includes("tools search <k1|k2|...>")));
  }

  const subcommand = await execute(["tools", "unknown"], disconnected);
  assert.ok(!subcommand.ok);
  assert.equal(subcommand.error?.code, "UNKNOWN_TOOLS_COMMAND");
});
