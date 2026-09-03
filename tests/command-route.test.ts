import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRequest, RootAdmissionLease, RootAdmissionRequest } from "../src/bridge/index.js";
import { execute } from "../src/cli.js";
import { builtinToolMetadata, type ToolMetadata } from "../src/tools/metadata/index.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

const dataDir = "Z:/definitely-missing-unity-cli-data";
const completionContract = {
  executionClass: "atomic" as const,
  completionPredicate: "method-return",
  proofSource: "official-attribute-return",
  reloadPolicy: "fail-closed",
  timeoutError: "E_EXECUTION_TIMEOUT",
  outcomeUnknownPolicy: "fail-closed",
  completionDriverId: "official-attribute.method-return",
  journalPolicy: "none" as const,
  mutation: false,
};

function attributeEvalTool(overrides: Partial<ToolMetadata> = {}): ToolMetadata {
  return makeLiveTool({
    name: "UnityPipeline_eval",
    summary: "Evaluate a test command.",
    description: "Exercise official command argument binding without changing Unity state.",
    group: "UnityPipeline",
    source: "project",
    contractProfile: "official-attribute",
    completionContract,
    inputSchema: {
      type: "object",
      properties: {
        code: { type: "string" },
        count: { type: "integer" },
        pdb: { type: "boolean" },
        ratio: { type: "number" },
        tags: { type: "array", items: { type: "string" } },
        config: { type: "object", additionalProperties: true },
        payload: { "x-unityCliAnyJson": true },
        display_name: { type: "string" },
        nullable: { oneOf: [{ type: "string" }, { type: "null" }] },
      },
      required: ["code"],
      additionalProperties: false,
    },
    ...overrides,
  });
}

function runtimeFor(tools: ToolMetadata[], requests: BridgeRequest[]) {
  return {
    dataDir,
    projectTools: tools,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      const wrapper = payload.input as { input?: unknown };
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: { result: { received: wrapper.input } },
      };
    },
  };
}

test("command help结构化声明native优先、attribute显式名与零映射", async () => {
  const result = await execute(["command", "--help"], runtimeFor([], []) as never);
  assert.ok(result.ok, JSON.stringify(result));
  assert.deepEqual((result.data as { nameResolution: unknown }).nameResolution, {
    nativeExactFirst: true,
    attributeFallback: "UnityPipeline_<name>",
    explicitAttributeName: "UnityPipeline_<name>",
    similarNameMapping: false,
  });
  assert.deepEqual((result.data as { catalogDiscovery: unknown }).catalogDiscovery, {
    commandListing: "unitycli command --project-path <path> [--page-size <n>] [--cursor <nextCursor>]",
    toolSearch: "unitycli tools search <k1|k2|...> --instance <id|projectPath|alias>",
    toolDescribe: "unitycli tools describe <tool> --instance <id|projectPath|alias>",
  });
});

test("command help复用精简Tool descriptor且不公开结果schema", async () => {
  const result = await execute(["command", "UnityPipeline_eval", "--help"], runtimeFor([attributeEvalTool()], []) as never);
  assert.ok(result.ok, JSON.stringify(result));
  const tool = (result.data as { tool: Record<string, unknown> }).tool;
  assert.ok(tool.inputSchema);
  for (const field of ["outputSchema", "group", "permissions", "available", "contractProfile", "summary"])
    assert.equal(Object.hasOwn(tool, field), false, field);
});

test("command省略name时投影分页路由摘要", async () => {
  const result = await execute(["command", "--page-size", "100"], runtimeFor([attributeEvalTool()], []) as never);
  assert.ok(result.ok, JSON.stringify(result));
  const data = result.data as { commands: Array<Record<string, unknown>>; pageInfo: { cursor: string; pageSize: number; total: number; nextCursor?: string } };
  assert.equal(data.pageInfo.cursor, "start");
  assert.equal(data.pageInfo.pageSize, 100);
  assert.equal(data.pageInfo.total, builtinToolMetadata().length + 1);
  assert.equal(data.pageInfo.nextCursor, undefined);
  assert.deepEqual(data.commands.find((item) => item.name === "eval"), { name: "eval", summary: "Evaluate a test command.", group: "UnityPipeline" });
});

test("command listing paginates stable summaries and validates bounds", async () => {
  const tools = Array.from({ length: 25 }, (_, index) => makeLiveTool({
    name: `fixture.command-${String(index).padStart(2, "0")}`,
    summary: `Command page ${index}.`,
    description: "Command pagination fixture.",
    group: "fixture",
    ...(index === 24 ? { available: false, unavailableReason: "fixture unavailable" } : {}),
  }));
  const runtime = runtimeFor(tools, []);
  const expectedNames = [...builtinToolMetadata().map((tool) => tool.name), ...tools.map((tool) => tool.name)].sort();
  const first = await execute(["command", "--page-size", "10"], runtime as never);
  assert.ok(first.ok, JSON.stringify(first));
  const firstData = first.data as { pageInfo: { nextCursor?: string } };
  const page = await execute(["command", "--page-size", "10", "--cursor", firstData.pageInfo.nextCursor!], runtime as never);
  assert.ok(page.ok, JSON.stringify(page));
  const data = page.data as { commands: Array<Record<string, unknown>>; pageInfo: Record<string, unknown> };
  assert.equal(data.pageInfo.cursor, firstData.pageInfo.nextCursor);
  assert.equal(data.pageInfo.pageSize, 10);
  assert.equal(data.pageInfo.total, expectedNames.length);
  assert.deepEqual(data.commands.map((item) => item.name), expectedNames.slice(10, 20));
  assert.ok(data.commands.every((item) => !Object.hasOwn(item, "parameters") && !Object.hasOwn(item, "available")));

  const all = await execute(["command", "--page-size", "100"], runtime as never);
  assert.ok(all.ok, JSON.stringify(all));
  const unavailable = (all.data as { commands: Array<Record<string, unknown>> }).commands.find((item) => item.name === "fixture.command-24");
  assert.equal(unavailable?.available, false);
  assert.equal(unavailable?.unavailableReason, "fixture unavailable");

  const invalidSize = await execute(["command", "--page-size", "0"], runtime as never);
  assert.equal(invalidSize.ok, false, JSON.stringify(invalidSize));
  if (!invalidSize.ok) assert.equal(invalidSize.error.code, "E_SCHEMA_INVALID");
  const invalidCursor = await execute(["command", "--cursor", "-1"], runtime as never);
  assert.equal(invalidCursor.ok, false, JSON.stringify(invalidCursor));
  if (!invalidCursor.ok) assert.equal(invalidCursor.error.code, "E_DISCOVERY_CURSOR_INVALID");
});

test("command按live schema解析positional、named、bare boolean与AnyJSON并只分发一次", async () => {
  const requests: BridgeRequest[] = [];
  const result = await execute([
    "command",
    "eval",
    "return 2+2;",
    "--count=2",
    "--pdb",
    "--ratio", "1.25",
    "--tags", "[\"alpha\",\"beta\"]",
    "--config",
    "{\"mode\":",
    "\"fast\"}",
    "--payload={\"nested\":[1,true,null]}",
    "--display_name", "exact-field",
    "--nullable", "null",
  ], runtimeFor([attributeEvalTool()], requests) as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input, {
    name: "UnityPipeline_eval",
    input: {
      code: "return 2+2;",
      count: 2,
      pdb: true,
      ratio: 1.25,
      tags: ["alpha", "beta"],
      config: { mode: "fast" },
      payload: { nested: [1, true, null] },
      display_name: "exact-field",
      nullable: null,
    },
  });
});

test("command与tools run对同一Tool产生相同typed input和业务结果", async () => {
  const commandRequests: BridgeRequest[] = [];
  const toolsRequests: BridgeRequest[] = [];
  const tool = attributeEvalTool();
  const input = { code: "return 4;", count: 4, pdb: false, ratio: 0.5, tags: ["one"], config: { mode: "strict" }, payload: null, display_name: "exact", nullable: null };

  const commandResult = await execute([
    "command", "UnityPipeline_eval", "--code", input.code, "--count", "4", "--pdb=false",
    "--ratio=0.5", "--tags", JSON.stringify(input.tags), "--config", JSON.stringify(input.config),
    "--payload", "null", "--display_name", "exact", "--nullable", "null",
  ], runtimeFor([tool], commandRequests) as never);
  const toolsResult = await execute([
    "tools", "run", "UnityPipeline_eval", "--input", JSON.stringify(input),
  ], runtimeFor([tool], toolsRequests) as never);

  assert.ok(commandResult.ok, JSON.stringify(commandResult));
  assert.ok(toolsResult.ok, JSON.stringify(toolsResult));
  assert.deepEqual(commandResult.data, toolsResult.data);
  assert.deepEqual(commandRequests[0].input, toolsRequests[0].input);
});

test("command名称选择保持native exact优先且不做相似能力映射", async () => {
  const native = attributeEvalTool({
    name: "eval",
    group: "project",
    contractProfile: "native",
  });
  const attribute = attributeEvalTool();
  const requests: BridgeRequest[] = [];
  const runtime = runtimeFor([native, attribute], requests);

  const listing = await execute(["command", "--page-size", "100"], runtime as never);
  const bare = await execute(["command", "eval", "return 1;"], runtime as never);
  const explicit = await execute(["command", "UnityPipeline_eval", "return 2;"], runtime as never);
  const missing = await execute(["command", "recompile"], runtime as never);

  assert.ok(listing.ok, JSON.stringify(listing));
  const names = (listing.data as { commands: Array<{ name: string }> }).commands
    .filter((item) => item.name === "eval" || item.name === "UnityPipeline_eval")
    .map((item) => item.name);
  assert.deepEqual(names.sort(), ["UnityPipeline_eval", "eval"]);
  assert.ok(bare.ok, JSON.stringify(bare));
  assert.ok(explicit.ok, JSON.stringify(explicit));
  assert.deepEqual(requests.map((request) => (request.input as { name: string }).name), ["eval", "UnityPipeline_eval"]);
  assert.equal(missing.ok, false);
  if (!missing.ok) assert.equal(missing.error.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});

test("online command resolution uses exact Runtime describe without injected projectTools or extra business posts", async () => {
  const native = makeLiveTool({ name: "fixture.native", summary: "Native fixture.", description: "Resolve one native fixture.", group: "fixture" });
  const attribute = attributeEvalTool();
  const descriptors = new Map([native, attribute].map((tool) => [tool.name, {
    name: tool.name,
    summary: tool.summary,
    description: tool.description,
    group: tool.group,
    source: tool.source,
    inputSchema: tool.inputSchema,
    outputSchema: tool.outputSchema,
    destructiveHint: tool.destructiveHint,
    supportsDryRun: tool.supportsDryRun ?? false,
    examples: tool.examples,
    target: tool.target ?? "editor",
    availabilityScope: tool.availabilityScope ?? "editorSession",
    playModeSupport: tool.playModeSupport ?? "supported",
    contractProfile: tool.contractProfile ?? "native",
    defaultTimeoutMs: tool.timeoutMs ?? 30_000,
    completionContract: tool.completionContract,
  }]));
  const requests: BridgeRequest[] = [];
  const runtime = {
    dataDir,
    createSessionSupervisor: () => ({
      acquireRootAdmission: async (request: RootAdmissionRequest): Promise<RootAdmissionLease> => ({
        requestId: request.requestId,
        command: request.command,
        rootFingerprint: (request.target === "editor" ? "a" : "b").repeat(64),
        completionContractIdentity: request.completionContractIdentity,
        target: request.target,
        instanceId: `unity-${(request.target === "editor" ? "c" : "d").repeat(64)}`,
        canonicalProjectPath: "c:\\fixture",
        sessionId: `session-${request.target}`,
        listenerId: `listener-${request.target}`,
        listenerGeneration: 1,
        token: (request.target === "editor" ? "e" : "f").repeat(64),
        consumed: false,
        grantExpiresAtMs: Date.now() + 30_000,
        executionAbsoluteDeadlineMs: Date.now() + 30_000,
        injected: true,
      }),
      completeRootAdmission: async () => undefined,
    }),
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      if (payload.command === "project-tool.describe") {
        const input = payload.input as { name: string; fallbackName?: string };
        const selected = descriptors.get(input.name) ?? (input.fallbackName ? descriptors.get(input.fallbackName) : undefined);
        return { ok: true as const, requestId: payload.requestId, data: {
          catalogGeneration: "e".repeat(64), found: Boolean(selected), ...(selected ? { descriptor: selected } : {}),
        } };
      }
      if (payload.command === "runtime-tool.describe") {
        assert.deepEqual(Object.keys(payload.input as object), ["name"]);
        return { ok: true as const, requestId: payload.requestId, data: { catalogGeneration: "r".repeat(64), found: false } };
      }
      if (payload.command === "runtime-tool.run") return { ok: true as const, requestId: payload.requestId, data: { result: {
        runtimeSessionId: "runtime-session", buildId: "build", observedAt: "2026-01-01T00:00:00Z", unityVersion: "6000.0", platform: "WindowsPlayer", productName: "Fixture", applicationVersion: "1",
        scene: { name: "Scene", buildIndex: 0, isLoaded: true }, frame: { frameCount: 1, realtimeSinceStartup: 1, targetFrameRate: 60 },
        time: { timeScale: 1, time: 1, unscaledTime: 1 }, screen: { width: 1, height: 1, dpi: 96, fullScreen: false, focused: true },
        memory: { totalAllocatedBytes: 1, totalReservedBytes: 1, totalUnusedReservedBytes: 0 },
      } } };
      return { ok: true as const, requestId: payload.requestId, data: { result: {} } };
    },
  };
  assert.equal(Object.hasOwn(runtime, "projectTools"), false);

  for (const argv of [
    ["command", native.name],
    ["command", "runtime.state"],
    ["command", "eval", "--code", "return 1;"],
    ["command", "eval", "--help"],
    ["command", "runtime.state", "--help"],
  ]) {
    const result = await execute(argv, runtime as never);
    assert.equal(result.ok, true, `${argv.join(" ")}: ${JSON.stringify(result)}`);
  }

  const runtimeDescribes = requests.filter((request) => request.command === "runtime-tool.describe");
  assert.ok(runtimeDescribes.length >= 5);
  assert.ok(runtimeDescribes.every((request) => JSON.stringify(request.input) === JSON.stringify({ name: (request.input as { name: string }).name })));
  const editorShort = requests.find((request) => request.command === "project-tool.describe" && (request.input as { name: string }).name === "eval");
  assert.deepEqual(editorShort?.input, { name: "eval", fallbackName: "UnityPipeline_eval" });
  const business = requests.filter((request) => request.command.endsWith("-tool.run"));
  assert.deepEqual(business.map((request) => request.command), ["project-tool.run", "runtime-tool.run", "project-tool.run"]);
});

test("command在effect前拒绝未知、缺值、重复与positional溢出", async () => {
  const cases = [
    { argv: ["command", "eval", "--unknown", "x"], code: "E_UNKNOWN_OPTION" },
    { argv: ["command", "eval", "--displayName", "x"], code: "E_UNKNOWN_OPTION" },
    { argv: ["command", "eval", "--count", "--pdb"], code: "E_OPTION_VALUE_REQUIRED" },
    { argv: ["command", "eval", "--code", "a", "--code", "b"], code: "E_SCHEMA_INVALID" },
    { argv: ["command", "eval", "code", "1", "true", "1.5", "[]", "{}", "null", "display", "null", "extra"], code: "E_SCHEMA_INVALID" },
  ];

  for (const item of cases) {
    const requests: BridgeRequest[] = [];
    const result = await execute(item.argv, runtimeFor([attributeEvalTool()], requests) as never);
    assert.equal(result.ok, false, JSON.stringify(result));
    if (!result.ok) assert.equal(result.error.code, item.code, JSON.stringify(result));
    assert.equal(requests.length, 0);
  }
});

test("command级project-path原样接受并与instance冲突时fail closed", async () => {
  const successResult = await execute([
    "command", "--project-path", "D:/UnityProject", "eval", "return 1;",
  ], runtimeFor([attributeEvalTool()], []) as never);
  assert.ok(successResult.ok, JSON.stringify(successResult));

  const conflict = await execute([
    "--instance", "D:/OtherProject", "command", "--project-path", "D:/UnityProject", "eval", "return 1;",
  ], runtimeFor([attributeEvalTool()], []) as never);
  assert.equal(conflict.ok, false);
  if (!conflict.ok) assert.equal(conflict.error.code, "E_INSTANCE_CONFLICT");
});
