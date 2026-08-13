import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/index.js";
import { execute } from "../src/cli.js";
import type { ToolMetadata } from "../src/tools/metadata/index.js";
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
    commandListing: "unity_cli command --project-path <path>",
    toolSearch: "unity_cli tools search <k1|k2|...> --instance <id|projectPath|alias>",
    toolDescribe: "unity_cli tools describe <tool> --instance <id|projectPath|alias>",
  });
});

test("command省略name时投影同一live Tool catalog", async () => {
  const result = await execute(["command"], runtimeFor([attributeEvalTool()], []) as never);
  assert.ok(result.ok, JSON.stringify(result));
  const commands = (result.data as { commands: Array<Record<string, unknown>> }).commands;
  const descriptor = commands.find((item) => item.name === "eval");
  assert.equal(descriptor?.name, "eval");
  assert.equal(descriptor?.group, "UnityPipeline");
  assert.deepEqual(descriptor?.parameters, ["code", "count", "pdb", "ratio", "tags", "config", "payload", "display_name", "nullable"]);
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

  const listing = await execute(["command"], runtime as never);
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
