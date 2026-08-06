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
  parseLiveDescriptor,
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
    ...overrides
  };
}

test("static public Context descriptors accept argv-only examples", () => {
  const parsed = parseLiveDescriptor({
    ...descriptor("scene.sample"),
    examples: [{ description: "Read scene sample.", argv: ["context", "read", "scene.sample", "--input", "{}"] }]
  });
  assert.notEqual(typeof parsed, "string");
  if (typeof parsed === "string") return;
  assert.deepEqual(parsed.examples, [{ description: "Read scene sample.", argv: ["context", "read", "scene.sample", "--input", "{}"] }]);
});

function runtime(bridgeExecute?: BridgeRuntime["bridgeExecute"]): BridgeRuntime {
  return { bridgeExecute };
}

test("context read accepts only strict JSON object input", async () => {
  const bridgeExecute: BridgeRuntime["bridgeExecute"] = async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts: [] } });
  for (const input of ["{", "{} trailing", "[]", "true", "null"]) {
    const result = await handleContextCommand("read", ["editor.state"], { input }, runtime(bridgeExecute), requestId);
    assert.ok(!result.ok);
    assert.equal(result.error?.code, input === "[]" || input === "true" || input === "null" ? "E_SCHEMA_INVALID" : "E_CONTEXT_INPUT_JSON_INVALID");
  }
});

test("static Context snapshots remain audit-only and public discovery requires a live instance", async () => {
  assert.equal(builtinContextDescriptors().length, 24);
  assert.ok(builtinContextCategories().includes("editor"));
  assert.deepEqual(filterContextsByCategory("tools"), []);
  assert.equal(findBuiltinContextByName("project.info")?.source, "built-in");
  assert.equal(searchContexts("custom").some((context) => context.name === "tools.custom"), false);

  const result = await handleContextCommand("list", [], {}, runtime(), requestId);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_INSTANCE_REQUIRED");
});

test("live list and search merge built-in, extension, and project descriptors", async () => {
  const contexts = [
    descriptor("scene.opened"),
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
  const data = result.data as ContextDescriptor & { command?: string; supportStatus?: string; cliCommand?: string };
  assert.equal(data.command, undefined);
  assert.equal(data.supportStatus, undefined);
  assert.equal(data.cliCommand, undefined);
  assert.deepEqual(data.inputSchema, live.inputSchema);
  assert.deepEqual(data.outputSchema, live.outputSchema);
});

test("runtime Context remains discoverable in EditMode and read returns E_RUNTIME_NOT_READY", async () => {
  const live = descriptor("runtime.player-state", {
    target: "runtime",
    availabilityScope: "playModeSession",
    playModeSupport: "runtime-only",
    available: false,
    unavailableReason: "E_RUNTIME_NOT_READY: Enter Play Mode before reading this Context."
  });
  const bridgeExecute: BridgeRuntime["bridgeExecute"] = async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts: [live] } });

  const listed = await handleContextCommand("list", [], {}, runtime(bridgeExecute), requestId);
  assert.ok(listed.ok);
  assert.ok((listed.data as { contexts: ContextDescriptor[] }).contexts.some((context) => context.name === live.name));

  const described = await handleContextCommand("describe", [live.name], {}, runtime(bridgeExecute), requestId);
  assert.ok(described.ok);
  assert.equal((described.data as ContextDescriptor).available, false);

  const read = await handleContextCommand("read", [live.name], {}, runtime(bridgeExecute), requestId);
  assert.ok(!read.ok);
  assert.equal(read.error?.code, "E_RUNTIME_NOT_READY");
  assert.equal(read.error?.transient, true);
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
  const bridgeExecute: BridgeRuntime["bridgeExecute"] = async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts: [] } });
  for (const input of ["[]", "42", "null"]) {
    const result = await handleContextCommand("read", ["editor.state"], { input }, runtime(bridgeExecute), requestId);
    assert.ok(!result.ok);
    assert.equal(result.error?.code, "E_SCHEMA_INVALID");
  }
});

test("removed tools.groups and tools.custom Contexts do not resolve through the live catalog", async () => {
  const bridgeExecute: BridgeRuntime["bridgeExecute"] = async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts: [] } });
  for (const name of ["tools.groups", "tools.custom"]) {
    const result = await handleContextCommand("read", [name], {}, runtime(bridgeExecute), requestId);
    assert.ok(!result.ok);
    assert.equal(result.error?.code, "E_CONTEXT_NOT_FOUND");
  }
});

test("extension physical-state uncertainty is consistent for list, describe, and read", async () => {
  const project = await projectWithPackages({});
  const live = descriptor("probuilder.scene", { source: "extension", category: "probuilder", requiresPackages: ["com.unity.probuilder"] });
  const bridgeExecute: BridgeRuntime["bridgeExecute"] = async (payload) => ({ ok: true, requestId: payload.requestId, data: { contexts: [live] } });
  const flags = { instance: project };

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
  assert.equal(read.error?.code, "E_EXTENSION_PHYSICAL_STATE_UNKNOWN");
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
      : { ok: true, requestId: payload.requestId, data: { result: { items: [] } } };
  }), requestId);
  assert.ok(result.ok);
  assert.deepEqual(payloads[1].input, { name: live.name, input: { search: "File" } });
});

async function projectWithPackages(packages: Record<string, string>): Promise<string> {
  const project = await mkdtemp(join(tmpdir(), "context-extension-"));
  await mkdir(join(project, "Packages"), { recursive: true });
  await mkdir(join(project, "ProjectSettings"), { recursive: true });
  await writeFile(join(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.61f1\n", "utf8");
  await writeFile(join(project, "Packages", "manifest.json"), JSON.stringify({ dependencies: packages }), "utf8");
  await writeFile(join(project, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: {} }), "utf8");
  return project;
}
