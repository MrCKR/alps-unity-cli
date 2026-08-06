import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/index.js";
import { execute } from "../src/cli.js";
import { discoverProjectTools, projectDescriptorToToolMetadata } from "../src/project-tool-discovery.js";
import { publicToolDescriptor, staticBridgeToolMetadata } from "../src/tools/catalog.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

/** 构造符合 live Project Tool 闭集契约的 descriptor。 */
function descriptor(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    name: "sample.echo",
    summary: "Echo one value.",
    description: "Return the requested value from the selected live Unity Editor.",
    group: "sample",
    source: "project",
    inputSchema: {
      type: "object",
      properties: { value: { type: "string" } },
      required: ["value"],
      additionalProperties: false,
    },
    outputSchema: {
      type: "object",
      properties: { value: { type: "string" } },
      required: ["value"],
      additionalProperties: false,
    },
    destructiveHint: false,
    supportsDryRun: false,
    target: "editor",
    availabilityScope: "editorSession",
    playModeSupport: "supported",
    examples: [{ description: "Echo hello.", argv: ["tools", "run", "sample.echo", "--input", "{\"value\":\"hello\"}"] }],
    completionContract: {
      executionClass: "atomic",
      journalPolicy: "none",
      mutation: false,
      completionPredicate: "typed-result-schema-v1",
      proofSource: "typed-handler-result",
      reloadPolicy: "fail-before-effect",
      timeoutError: "E_EXECUTION_TIMEOUT",
      outcomeUnknownPolicy: "not-applicable-read-only",
      completionDriverId: "direct-typed-result-v1",
    },
    internalDescriptorFingerprint: "a".repeat(64),
    available: true,
    ...overrides,
  };
}

test("static public descriptors receive completion only from the generated internal registry", () => {
  const tools = staticBridgeToolMetadata();
  assert.ok(tools.length > 300);
  assert.ok(tools.every((tool) => tool.completionContract));
  assert.ok(tools.every((tool) => tool.examples?.every((example) => example.argv[0] === "tools" && example.argv[1] === "run" && example.argv[2] === tool.name)));
});

test("project Tool parser requires a complete self-contained descriptor", () => {
  const parsed = projectDescriptorToToolMetadata(descriptor());
  assert.ok(parsed);
  assert.equal(parsed.name, "sample.echo");
  assert.ok(parsed.outputSchema);
  assert.equal(parsed.availabilityScope, "editorSession");
  assert.equal(parsed.liveDescriptorFingerprint, "a".repeat(64));
  assert.equal(parsed.completionContract?.completionDriverId, "direct-typed-result-v1");

  for (const field of ["description", "group", "inputSchema", "outputSchema", "destructiveHint", "supportsDryRun", "target", "availabilityScope", "playModeSupport", "examples", "completionContract"]) {
    const invalid = descriptor();
    delete invalid[field];
    assert.equal(projectDescriptorToToolMetadata(invalid), undefined, `missing ${field}`);
  }
});

test("project Tool parser preserves dynamic availability without using source for routing", () => {
  const parsed = projectDescriptorToToolMetadata(descriptor({
    target: "runtime",
    availabilityScope: "playModeSession",
    playModeSupport: "runtime-only",
    available: false,
    unavailableReason: "E_RUNTIME_NOT_READY: Enter Play Mode before executing this Tool.",
    source: "project"
  }));
  assert.ok(parsed);
  assert.equal(parsed.available, false);
  assert.equal(parsed.availabilityScope, "playModeSession");
  assert.match(parsed.unavailableReason ?? "", /^E_RUNTIME_NOT_READY:/);
});

test("project Tool parser rejects wrong argv target and invalid example input", () => {
  assert.equal(projectDescriptorToToolMetadata(descriptor({
    examples: [{ description: "Wrong Tool.", argv: ["tools", "run", "other.echo", "--input", "{\"value\":\"hello\"}"] }],
  })), undefined);
  assert.equal(projectDescriptorToToolMetadata(descriptor({
    examples: [{ description: "Wrong input.", argv: ["tools", "run", "sample.echo", "--input", "{\"unknown\":true}"] }],
  })), undefined);
});

test("public custom Tool descriptor hides internal execution and fingerprint metadata", () => {
  const parsed = projectDescriptorToToolMetadata(descriptor());
  assert.ok(parsed);
  const published = publicToolDescriptor(parsed);
  assert.equal(published.internalDescriptorFingerprint, undefined);
  assert.equal(published.completionContract, undefined);
  assert.equal(published.handler, undefined);
  assert.deepEqual(published.outputSchema, parsed.outputSchema);
  assert.deepEqual(published.examples, [{ description: "Echo hello.", argv: ["tools", "run", "sample.echo", "--input", "{\"value\":\"hello\"}"] }]);
});

test("project Tool discovery fails closed on malformed registry entries", async () => {
  const result = await discoverProjectTools({}, {
    bridgeExecute: async (payload) => ({
      ok: true,
      requestId: payload.requestId,
      data: { tools: [descriptor(), descriptor({ name: "broken.tool", outputSchema: undefined })] },
    }),
  }, "req_project_discovery");
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_TOOL_REGISTRY_UNHEALTHY");
});

test("custom Tool rejects a malformed successful Bridge terminal without exposing wrapper data", async () => {
  const tool = projectDescriptorToToolMetadata(descriptor());
  assert.ok(tool);
  const result = await execute(["tools", "run", "sample.echo", "--input", "{\"value\":\"hello\"}"], {
    dataDir: "Z:/missing-unity-cli-data",
    projectTools: [tool],
    bridgeExecute: async (payload: BridgeRequest) => ({ ok: true, requestId: payload.requestId, data: { command: "project-tool.run" } }),
  } as never);
  assert.equal(result.ok, false);
  if (result.ok) return;
  assert.equal(result.error.code, "E_BRIDGE_RESPONSE_INVALID");
  assert.equal(JSON.stringify(result).includes("project-tool.run"), false);
  assert.equal(JSON.stringify(result).includes("descriptorFingerprint"), false);
});

test("instance selector never becomes a Tool business field", async () => {
  const tool = projectDescriptorToToolMetadata(descriptor({
    inputSchema: {
      type: "object",
      properties: { value: { type: "string" }, instance: { type: "string" } },
      required: ["value"],
      additionalProperties: false,
    },
  }));
  assert.ok(tool);
  const requests: BridgeRequest[] = [];
  const result = await execute([
    "tools", "run", "sample.echo", "--input", "{\"value\":\"hello\",\"instance\":\"business-only\"}", "--instance", "selector-only",
  ], {
    dataDir: "Z:/missing-unity-cli-data",
    projectTools: [tool],
    runtimeTools: [],
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, requestId: payload.requestId, data: { result: { value: "hello" } } };
    },
  } as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.equal(requests.length, 1);
  assert.deepEqual((requests[0].input as { input: Record<string, unknown> }).input, { value: "hello", instance: "business-only" });
});

test("custom Tool runs once through direct project-tool.run and returns business data", async () => {
  const tool = projectDescriptorToToolMetadata(descriptor());
  assert.ok(tool);
  const requests: BridgeRequest[] = [];
  const result = await execute([
    "tools", "run", "sample.echo", "--input", "{\"value\":\"hello\"}",
  ], {
    dataDir: "Z:/missing-unity-cli-data",
    projectTools: [tool],
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, requestId: payload.requestId, data: { result: { value: "hello" } } };
    },
  } as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.deepEqual(result.data, { value: "hello" });
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input, {
    name: "sample.echo",
    input: { value: "hello" },
    expectedDescriptorFingerprint: "a".repeat(64),
  });
});
