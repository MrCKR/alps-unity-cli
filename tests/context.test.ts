import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest, BridgeRuntime } from "../src/bridge/index.js";
import { handleContextCommand } from "../src/contexts/commands.js";
import {
  builtinContextCategories,
  builtinContextDescriptors,
  filterContextsByCategory,
  findBuiltinContextByName,
  searchContexts
} from "../src/contexts/registry.js";
import type { ContextDescriptor } from "../src/contexts/types.js";

const requestId = "test_context_dispatcher";

function descriptor(name: string, overrides: Partial<ContextDescriptor> = {}): ContextDescriptor {
  return {
    name,
    summary: `Read ${name}.`,
    description: `Read ${name} from the live Unity registry without side effects.`,
    category: name.split(".")[0],
    source: "built-in",
    target: "editor",
    availabilityScope: "editorSession",
    playModeSupport: "supported",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    outputSchema: { type: "object", properties: {}, additionalProperties: false },
    examples: [{ description: `Read ${name}.`, input: {}, argv: ["context", "read", name, "--input", "{}"] }],
    cliCommand: `context read ${name}`,
    supportStatus: "live-bridge",
    ...overrides
  };
}

function runtime(bridgeExecute?: BridgeRuntime["bridgeExecute"]): BridgeRuntime {
  return { bridgeExecute };
}

test("context read accepts only strict JSON object input", async () => {
  for (const input of ["{", "{} trailing", "[]", "true", "null"]) {
    const result = await handleContextCommand("read", ["tools.groups"], { input }, runtime(), requestId);
    assert.ok(!result.ok);
    assert.equal(result.error?.code, input === "[]" || input === "true" || input === "null" ? "E_SCHEMA_INVALID" : "E_CONTEXT_INPUT_JSON_INVALID");
  }
});

test("generated registry is the only offline context catalog", async () => {
  assert.deepEqual(builtinContextDescriptors().map((context) => context.name).sort(), ["tools.custom", "tools.groups"]);
  assert.deepEqual(builtinContextCategories(), ["tools"]);
  assert.deepEqual(filterContextsByCategory("tools").map((context) => context.name).sort(), ["tools.custom", "tools.groups"]);
  assert.equal(findBuiltinContextByName("project.info"), undefined);
  assert.ok(searchContexts("custom").some((context) => context.name === "tools.custom"));

  const result = await handleContextCommand("list", [], {}, runtime(), requestId);
  assert.ok(result.ok);
  const data = result.data as { count: number; contexts: ContextDescriptor[] };
  assert.equal(data.count, 2);
  assert.deepEqual(data.contexts.map((context) => context.name).sort(), ["tools.custom", "tools.groups"]);
  assert.ok(data.contexts.every((context) => context.source === "built-in"));
});

test("live list and search merge built-in, extension, and project descriptors", async () => {
  const contexts = [
    descriptor("editor.state"),
    descriptor("probuilder.scene", { source: "extension", category: "probuilder", requiresPackages: ["com.unity.probuilder"] }),
    descriptor("sample.summary", { source: "project", category: "sample" })
  ];
  const bridgeExecute: BridgeRuntime["bridgeExecute"] = async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts } });

  const listed = await handleContextCommand("list", [], {}, runtime(bridgeExecute), requestId);
  assert.ok(listed.ok);
  const listData = listed.data as { count: number; contexts: ContextDescriptor[] };
  assert.equal(listData.count, 5);
  assert.deepEqual(new Set(listData.contexts.map((context) => context.source)), new Set(["built-in", "extension", "project"]));

  const searched = await handleContextCommand("search", ["sample"], {}, runtime(bridgeExecute), requestId);
  assert.ok(searched.ok);
  assert.deepEqual((searched.data as { contexts: ContextDescriptor[] }).contexts.map((context) => context.name), ["sample.summary"]);
});

test("describe returns the exact live descriptor from the shared catalog", async () => {
  const live = descriptor("scene.gameobject", {
    inputSchema: {
      type: "object",
      properties: { target: { type: "string" }, options: { $ref: "#/$defs/options" } },
      required: ["target"],
      additionalProperties: false,
      $defs: { options: { type: "object", properties: { deep: { type: "boolean" } }, additionalProperties: false } }
    },
    outputSchema: { type: "object", properties: { name: { type: "string" } }, required: ["name"], additionalProperties: false },
    examples: [{ description: "Read a scene GameObject.", input: { target: "Main Camera" }, argv: ["context", "read", "scene.gameobject", "--input", "{\"target\":\"Main Camera\"}"] }]
  });
  const result = await handleContextCommand("describe", [live.name], {}, runtime(async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts: [live] } })), requestId);
  assert.ok(result.ok);
  const data = result.data as ContextDescriptor & { command: string };
  assert.equal(data.command, "context.describe");
  assert.deepEqual(data.inputSchema, live.inputSchema);
  assert.deepEqual(data.outputSchema, live.outputSchema);
});

test("duplicate and malformed live descriptors fail closed", async () => {
  const duplicateRuntime = runtime(async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts: [descriptor("editor.state"), descriptor("editor.state")] } }));
  const duplicate = await handleContextCommand("list", [], {}, duplicateRuntime, requestId);
  assert.ok(!duplicate.ok);
  assert.equal(duplicate.error?.code, "E_CONTEXT_REGISTRY_UNHEALTHY");

  const malformedRuntime = runtime(async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts: [{ ...descriptor("editor.state"), inputSchema: undefined }] } }));
  const malformed = await handleContextCommand("list", [], {}, malformedRuntime, requestId);
  assert.ok(!malformed.ok);
  assert.equal(malformed.error?.code, "E_CONTEXT_REGISTRY_UNHEALTHY");
});

test("context read validates refs, tuples, dictionaries, and unknown fields before Bridge execution", async () => {
  const payloads: BridgeRequest[] = [];
  const live = descriptor("scene.inspect", {
    inputSchema: {
      type: "object",
      properties: {
        target: { type: "string" },
        tuple: { type: "array", prefixItems: [{ type: "string" }, { type: "integer" }], minItems: 2, maxItems: 2, items: false },
        labels: { type: "object", additionalProperties: { type: "boolean" } },
        options: { $ref: "#/$defs/options" }
      },
      required: ["target"],
      additionalProperties: false,
      $defs: { options: { type: "object", properties: { deep: { type: "boolean" } }, additionalProperties: false } }
    },
    examples: [{ description: "Inspect a scene object.", input: { target: "Main Camera" }, argv: ["context", "read", "scene.inspect", "--input", "{\"target\":\"Main Camera\"}"] }]
  });
  const bridgeExecute: BridgeRuntime["bridgeExecute"] = async (payload) => {
    payloads.push(payload);
    if (payload.command === "context.list") return { ok: true, requestId: payload.requestId, data: { contexts: [live] } };
    return { ok: true, requestId: payload.requestId, data: { context: live.name, result: { found: true } } };
  };

  const invalid = await handleContextCommand("read", [live.name], { input: JSON.stringify({ target: "Main Camera", tuple: ["x", "bad"], labels: { main: "yes" }, options: { extra: true }, unknown: true }) }, runtime(bridgeExecute), requestId);
  assert.ok(!invalid.ok);
  assert.equal(invalid.error?.code, "E_SCHEMA_INVALID");
  assert.ok((invalid.error?.details as { errors: unknown[] }).errors.length >= 4);
  assert.deepEqual(payloads.map((payload) => payload.command), ["context.list"]);

  const validInput = { target: "Main Camera", tuple: ["x", 2], labels: { main: true }, options: { deep: false } };
  const valid = await handleContextCommand("read", [live.name], { input: JSON.stringify(validInput) }, runtime(bridgeExecute), requestId);
  assert.ok(valid.ok);
  const readPayload = payloads.find((payload) => payload.command === "context.read");
  assert.deepEqual(readPayload?.input, { name: live.name, input: validInput });
  assert.equal("args" in (readPayload as unknown as Record<string, unknown>), false);
});

test("context input arrays and scalars are rejected instead of becoming empty objects", async () => {
  const live = descriptor("editor.state");
  const bridgeExecute: BridgeRuntime["bridgeExecute"] = async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts: [live] } });
  for (const input of ["[]", "42", "null"]) {
    const result = await handleContextCommand("read", [live.name], { input }, runtime(bridgeExecute), requestId);
    assert.ok(!result.ok);
    assert.equal(result.error?.code, "E_SCHEMA_INVALID");
  }
});

test("tools.groups stays offline and tools.custom reports the live project tool registry only", async () => {
  const groups = await handleContextCommand("read", ["tools.groups"], {}, runtime(), requestId);
  assert.ok(groups.ok);
  assert.ok(Array.isArray((groups.data as { groups: unknown[] }).groups));

  const custom = await handleContextCommand("read", ["tools.custom"], {}, {
    projectTools: [{
      name: "sample.echo",
      summary: "Echo a value.",
      description: "Echo a value from the project registry.",
      group: "sample",
      inputSchema: { type: "object", properties: {}, additionalProperties: false },
      destructiveHint: false
    }]
  } as never, requestId);
  assert.ok(custom.ok);
  const customData = custom.data as { count: number; tools: Array<{ name: string }>; mode: string };
  assert.equal(customData.count, 1);
  assert.equal(customData.tools[0].name, "sample.echo");
  assert.equal(customData.mode, "runtime");
});

test("extension unavailable state is consistent for list, describe, and read", async () => {
  const project = await projectWithPackages({});
  const live = descriptor("probuilder.scene", { source: "extension", category: "probuilder", requiresPackages: ["com.unity.probuilder"] });
  const bridgeExecute: BridgeRuntime["bridgeExecute"] = async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts: [live] } });
  const flags = { project };

  const listed = await handleContextCommand("list", [], flags, runtime(bridgeExecute), requestId);
  assert.ok(listed.ok);
  const listedContext = (listed.data as { contexts: ContextDescriptor[] }).contexts.find((context) => context.name === live.name);
  assert.equal(listedContext?.available, false);
  assert.deepEqual(listedContext?.requiresPackages, ["com.unity.probuilder"]);

  const described = await handleContextCommand("describe", [live.name], flags, runtime(bridgeExecute), requestId);
  assert.ok(described.ok);
  assert.equal((described.data as ContextDescriptor).available, false);
  assert.deepEqual((described.data as ContextDescriptor).requiresPackages, ["com.unity.probuilder"]);

  const read = await handleContextCommand("read", [live.name], flags, runtime(bridgeExecute), requestId);
  assert.ok(!read.ok);
  assert.equal(read.error?.code, "E_EXTENSION_DEPENDENCY_MISSING");
  assert.deepEqual((read.error?.details as { missingPackageIds: string[] }).missingPackageIds, ["com.unity.probuilder"]);
});

test("context read supports input files and emits typed BridgeRequest.input", async () => {
  const directory = await mkdtemp(join(tmpdir(), "context-input-"));
  const inputFile = join(directory, "input.json");
  await writeFile(inputFile, JSON.stringify({ search: "File" }), "utf8");
  const live = descriptor("editor.menu-items", { inputSchema: { type: "object", properties: { search: { type: "string" } }, additionalProperties: false } });
  const payloads: BridgeRequest[] = [];
  const result = await handleContextCommand("read", [live.name], { "input-file": inputFile }, runtime(async (payload) => {
    payloads.push(payload);
    return payload.command === "context.list"
      ? { ok: true, requestId: payload.requestId, data: { contexts: [live] } }
      : { ok: true, requestId: payload.requestId, data: { items: [] } };
  }), requestId);
  assert.ok(result.ok);
  assert.deepEqual(payloads[1].input, { name: live.name, input: { search: "File" } });
});

async function projectWithPackages(packages: Record<string, string>): Promise<string> {
  const project = await mkdtemp(join(tmpdir(), "context-extension-"));
  await mkdir(join(project, "Packages"), { recursive: true });
  await writeFile(join(project, "Packages", "manifest.json"), JSON.stringify({ dependencies: packages }), "utf8");
  await writeFile(join(project, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: {} }), "utf8");
  return project;
}
