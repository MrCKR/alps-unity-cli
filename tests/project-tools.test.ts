import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/index.js";
import { readFileSync } from "node:fs";
import { execute } from "../src/cli.js";
import { discoverProjectTools, projectDescriptorToToolMetadata } from "../src/project-tool-discovery.js";
import { publicToolDescriptor, staticBridgeToolMetadata } from "../src/tools/catalog.js";
import { builtinToolMetadata } from "../src/tools/metadata/index.js";

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
    contractProfile: "native",
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

/** 构造 sealed official-attribute 保守 descriptor。 */
function officialDescriptor(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return descriptor({
    name: "UnityPipeline_sample.echo",
    group: "UnityPipeline",
    contractProfile: "official-attribute",
    destructiveHint: true,
    sideEffects: ["arbitrary-method-invocation"],
    supportsDryRun: false,
    permissions: ["execute-user-code"],
    requiresPackages: [],
    playModeSupport: "unknown",
    availabilityScope: "editorSession",
    examples: [{ description: "Echo hello.", argv: ["tools", "run", "UnityPipeline_sample.echo", "--input", "{\"value\":\"hello\"}"] }],
    completionContract: {
      executionClass: "external",
      journalPolicy: "required",
      mutation: true,
      completionPredicate: "attributed-method-return-v1",
      proofSource: "official-attributed-method-return",
      reloadPolicy: "outcome-unknown-after-dispatch",
      timeoutError: "E_EXECUTION_TIMEOUT",
      outcomeUnknownPolicy: "method-may-still-be-running",
      completionDriverId: "official-method-return-v1",
    },
    ...overrides,
  });
}

test("static internal descriptors receive completion only from the generated registry", () => {
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
  assert.equal(parsed.contractProfile, "native");

  for (const field of ["description", "group", "inputSchema", "outputSchema", "destructiveHint", "supportsDryRun", "target", "availabilityScope", "playModeSupport", "contractProfile", "examples", "completionContract"]) {
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

test("public custom Tool descriptor exposes only the compact invocation contract", () => {
  const parsed = projectDescriptorToToolMetadata(descriptor());
  assert.ok(parsed);
  const published = publicToolDescriptor(parsed);
  assert.deepEqual(Object.keys(published).sort(), [
    "description", "destructiveHint", "examples", "inputSchema", "name", "playModeSupport", "supportsDryRun", "target",
  ]);
  for (const field of ["outputSchema", "group", "permissions", "available", "internalDescriptorFingerprint", "completionContract", "handler", "contractProfile"])
    assert.equal(Object.hasOwn(published, field), false, field);
  assert.deepEqual(published.examples, [{ description: "Echo hello.", input: { value: "hello" } }]);
  assert.deepEqual(parsed.outputSchema, descriptor().outputSchema);
});

test("public descriptor derives execution controls and emits availability only when false", () => {
  const parsed = projectDescriptorToToolMetadata(descriptor({
    supportsDryRun: true,
    available: false,
    unavailableReason: "E_SAMPLE_UNAVAILABLE: Sample is not ready.",
    examples: [{
      description: "Preview echo.",
      argv: ["tools", "run", "sample.echo", "--input", "{\"value\":\"hello\"}", "--dry-run", "--timeout-ms", "2500"],
    }],
  }));
  assert.ok(parsed);
  const published = publicToolDescriptor(parsed);
  assert.equal(published.available, false);
  assert.equal(published.unavailableReason, "E_SAMPLE_UNAVAILABLE: Sample is not ready.");
  assert.deepEqual(published.examples, [{
    description: "Preview echo.",
    input: { value: "hello" },
    execution: { dryRun: true, timeoutMs: 2500 },
  }]);
});

test("all current public descriptors use the compact field closure while internal output schemas remain complete", () => {
  const tools = [...new Map([...builtinToolMetadata(), ...staticBridgeToolMetadata()].map((tool) => [tool.name, tool])).values()];
  const allowed = new Set([
    "name", "description", "inputSchema", "destructiveHint", "sideEffects", "playModeSupport", "supportsDryRun",
    "examples", "target", "available", "unavailableReason", "requiresPackages",
  ]);
  let compactBytes = 0;
  let expandedBytes = 0;
  for (const tool of tools) {
    assert.ok(tool.outputSchema, `${tool.name} internal outputSchema`);
    const published = publicToolDescriptor(tool);
    for (const field of Object.keys(published)) assert.equal(allowed.has(field), true, `${tool.name}.${field}`);
    for (const field of ["outputSchema", "group", "permissions"]) assert.equal(Object.hasOwn(published, field), false, `${tool.name}.${field}`);
    if ((tool.available ?? true) === true) assert.equal(Object.hasOwn(published, "available"), false, `${tool.name}.available`);
    for (const example of (published.examples ?? []) as Array<Record<string, unknown>>) {
      assert.deepEqual(Object.keys(example).sort().filter((field) => !["description", "execution", "input"].includes(field)), [], `${tool.name}.examples`);
    }
    compactBytes += Buffer.byteLength(JSON.stringify(published), "utf8");
    expandedBytes += Buffer.byteLength(JSON.stringify({
      name: tool.name, description: tool.description, group: tool.group, inputSchema: tool.inputSchema, outputSchema: tool.outputSchema,
      destructiveHint: tool.destructiveHint, sideEffects: tool.sideEffects, playModeSupport: tool.playModeSupport ?? "supported",
      supportsDryRun: tool.supportsDryRun ?? false, permissions: tool.permissions,
      examples: tool.examples?.map(({ description, argv }) => ({ description, argv })), target: tool.target ?? "editor", available: tool.available ?? true,
      requiresPackages: tool.requiresPackages,
    }), "utf8");
  }
  assert.ok(compactBytes < expandedBytes * 0.45, `compact=${compactBytes}, expanded=${expandedBytes}`);
});

test("official attribute descriptors retain their conservative contract profile", () => {
  const parsed = projectDescriptorToToolMetadata(officialDescriptor());
  assert.ok(parsed);
  assert.equal(parsed.contractProfile, "official-attribute");
  assert.equal(parsed.destructiveHint, true);
  assert.deepEqual(parsed.permissions, ["execute-user-code"]);
  assert.equal(parsed.completionContract?.outcomeUnknownPolicy, "method-may-still-be-running");
  assert.equal(publicToolDescriptor(parsed).contractProfile, undefined);
  const omittedEmptyPackages = officialDescriptor();
  delete omittedEmptyPackages.requiresPackages;
  assert.ok(projectDescriptorToToolMetadata(omittedEmptyPackages));
});

test("official attribute descriptors cannot weaken their sealed conservative contract", async () => {
  const malformed: Array<[string, Record<string, unknown>]> = [
    ["name", { name: "sample.attribute" }],
    ["group", { group: "sample" }],
    ["destructiveHint", { destructiveHint: false }],
    ["supportsDryRun", { supportsDryRun: true }],
    ["sideEffects", { sideEffects: [] }],
    ["permissions", { permissions: [] }],
    ["requiresPackages", { requiresPackages: ["com.example.package"] }],
    ["source", { source: "extension" }],
    ["target", { target: "runtime" }],
    ["playModeSupport", { playModeSupport: "supported" }],
    ["availabilityScope", { availabilityScope: "playModeSession" }],
    ["completionContract", { completionContract: { executionClass: "external" } }],
  ];
  for (const [field, override] of malformed)
    assert.equal(projectDescriptorToToolMetadata(officialDescriptor(override)), undefined, field);

  const result = await discoverProjectTools({}, {
    bridgeExecute: async (payload) => ({
      ok: true,
      requestId: payload.requestId,
      data: { tools: [descriptor(), officialDescriptor({ destructiveHint: false })] },
    }),
  }, "req_official_contract_discovery");
  assert.equal(result.ok, false);
  if (result.ok) return;
  assert.equal(result.error.code, "E_TOOL_REGISTRY_UNHEALTHY");
  assert.deepEqual((result.error.details as { invalidFields: string[] }).invalidFields, ["destructiveHint"]);
});

test("unsupported official attribute dry-run fails before Bridge dispatch", async () => {
  let bridgeCalls = 0;
  const tool = projectDescriptorToToolMetadata(officialDescriptor());
  assert.ok(tool);
  const result = await execute(["tools", "run", "UnityPipeline_sample.echo", "--input", "{\"value\":\"hello\"}", "--dry-run"], {
    dataDir: "Z:/missing-unity-cli-data",
    projectTools: [tool],
    bridgeExecute: async () => { bridgeCalls += 1; throw new Error("must not dispatch"); },
  } as never);
  assert.equal(result.ok, false);
  assert.equal(result.error.code, "E_DRY_RUN_UNSUPPORTED");
  assert.equal(bridgeCalls, 0);
});

test("C# registry emits and fingerprints native profiles without partial custom registration", () => {
  const source = readFileSync("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", "utf8");
  assert.match(source, /ContractProfile = "native"/);
  assert.match(source, /\{ "contractProfile", definition\.ContractProfile \}/);
  assert.match(source, /\\ncontractProfile=/);
  assert.match(source, /E_TOOL_REGISTRY_UNHEALTHY/);
  assert.doesNotMatch(source, /custom_tool_registration_rejected|diagnostics\.Contains\("E_TOOL_COMPLETION_CONTRACT_REQUIRED"\)/);
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
