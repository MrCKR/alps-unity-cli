/**
 * Unit tests for context command family: context list, context describe, context read.
 */

import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { handleContextCommand } from "../src/context/commands.js";
import { builtinContextDescriptors, builtinContextCategories, findBuiltinContextByName, filterContextsByCategory, searchContexts } from "../src/context/registry.js";
import { defineContextMetadata, findDefineContext } from "../src/contexts/registry.js";
import type { ContextDescriptor } from "../src/context/types.js";
import type { BridgeRuntime } from "../src/bridge/index.js";

const requestId = "test_req_ctx_001";

function makeRuntime(bridgeExecute?: BridgeRuntime["bridgeExecute"]): BridgeRuntime {
  return { bridgeExecute };
}

function bridgeContextDescriptor(name: string, overrides: Partial<ContextDescriptor> = {}): ContextDescriptor {
  const category = name.split(".")[0] || "custom";
  return {
    name,
    summary: `Read ${name}.`,
    description: `Read ${name} from the live Unity Bridge without modifying state.`,
    category,
    source: "built-in",
    target: "editor",
    availabilityScope: "editorSession",
    playModeSupport: "supported",
    inputSchema: { type: "object", properties: {} },
    outputSchema: { type: "object", properties: {} },
    cliCommand: `context read ${name}`,
    supportStatus: "live-bridge",
    ...overrides,
  };
}

// ── Built-in context descriptors ──────────────────────────────────────────

test("builtin context descriptors cover local TS-owned contexts", () => {
  const contexts = builtinContextDescriptors();
  assert.deepEqual(contexts.map((context) => context.name).sort(), ["tools.custom", "tools.groups"]);
});

test("Stage 6 defineContext registry exposes TS-owned tools contexts", () => {
  const groupsDefinition = findDefineContext("tools.groups");
  const customDefinition = findDefineContext("tools.custom");
  assert.ok(groupsDefinition, "tools.groups should be registered as a defineContext");
  assert.ok(customDefinition, "tools.custom should be registered as a defineContext");

  const groupsMetadata = defineContextMetadata().find((context) => context.name === "tools.groups");
  const customMetadata = defineContextMetadata().find((context) => context.name === "tools.custom");
  assert.equal(groupsMetadata?.supportStatus, "cli-only");
  assert.equal(groupsMetadata?.availabilityScope, "always");
  assert.equal((groupsMetadata?.outputSchema.properties as Record<string, { type?: string }>).groups?.type, "array");
  assert.equal(customMetadata?.supportStatus, "live-bridge");
  assert.equal(customMetadata?.refreshBehavior, "on-demand");
  assert.equal((customMetadata?.outputSchema.properties as Record<string, { type?: string }>).tools?.type, "array");

  const resolvedGroups = findBuiltinContextByName("tools.groups");
  assert.equal(resolvedGroups?.supportStatus, "cli-only", "local context discovery should expose defineContext metadata");
});

test("all builtin contexts have required fields", () => {
  for (const ctx of builtinContextDescriptors()) {
    assert.ok(ctx.name, "name required");
    assert.ok(ctx.summary, "summary required");
    assert.ok(ctx.description, "description required");
    assert.equal("title" in ctx, false, `${ctx.name} should not expose title`);
    assert.ok(ctx.category, "category required");
    assert.ok(ctx.inputSchema, "inputSchema required");
    assert.ok(ctx.outputSchema, "outputSchema required");
    assert.ok(ctx.cliCommand, "cliCommand required");
    assert.equal("bridgeCommand" in ctx, false, `${ctx.name} should not expose bridgeCommand`);
    assert.equal("destructiveHint" in ctx, false, `${ctx.name} should not expose destructiveHint`);
  }
});

test("builtin contexts cover local TS-owned categories", () => {
  const categories = builtinContextCategories();
  assert.deepEqual(categories, ["tools"]);
});

test("specific local TS-owned contexts exist", () => {
  const required = ["tools.groups", "tools.custom"];
  for (const name of required) {
    const ctx = findBuiltinContextByName(name);
    assert.ok(ctx, `Missing required local context: ${name}`);
  }
  assert.equal(findBuiltinContextByName("project.info"), undefined, "C# built-in contexts must not be duplicated in TS metadata");
});

test("no duplicate context names", () => {
  const names = builtinContextDescriptors().map((c) => c.name);
  const unique = new Set(names);
  assert.equal(names.length, unique.size, "Duplicate context names found");
});

// ── Category filter ───────────────────────────────────────────────────────

test("filterContextsByCategory returns local TS-owned subset", () => {
  const toolsContexts = filterContextsByCategory("tools");
  assert.deepEqual(toolsContexts.map((context) => context.name).sort(), ["tools.custom", "tools.groups"]);
  assert.deepEqual(filterContextsByCategory("project"), []);
});

// ── Search ────────────────────────────────────────────────────────────────

test("searchContexts finds matching local contexts", () => {
  const results = searchContexts("custom");
  assert.ok(results.length > 0, "Should find tools.custom");
  assert.ok(results.some((c) => c.name === "tools.custom"));
  assert.equal(searchContexts("camera").length, 0, "C# built-in contexts are only discoverable from live Bridge descriptors");
});

test("searchContexts returns empty for no match", () => {
  const results = searchContexts("nonexistent_context_xyz");
  assert.equal(results.length, 0);
});

// ── context list CLI ──────────────────────────────────────────────────────

test("context list returns local TS-owned and extension manifest contexts offline", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("list", [], {}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "context.list");
  assert.equal(data.count, 3);

  const contexts = data.contexts as Array<Record<string, unknown>>;
  assert.equal(contexts.length, 3);
  assert.ok(contexts[0].name);
  assert.ok(contexts[0].summary);
  assert.ok(contexts[0].description);
  assert.equal("title" in contexts[0], false);
  assert.ok(contexts[0].category);
  assert.ok(contexts[0].source);
  assert.ok(contexts.some((context) => context.name === "tools.groups" && context.source === "built-in"));
  assert.ok(contexts.some((context) => context.name === "probuilder.scene" && context.source === "extension"));
});

test("context list with category filter", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("list", [], { category: "tools" }, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  const contexts = data.contexts as Array<Record<string, unknown>>;
  assert.ok(contexts.length > 0);
  assert.ok(contexts.every((c) => c.category === "tools"));
});

test("context list with text format", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("list", [], { format: "text" }, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.ok(typeof data.text === "string");
  assert.ok(data.text.length > 0);
});

test("context list filters extension contexts by extension id category", async () => {
  const projectDir = await makeProjectWithPackages({ "com.unity.probuilder": "5.2.2" });
  const runtime = makeRuntime();

  const result = await handleContextCommand("list", [], { project: projectDir, category: "probuilder" }, runtime, requestId);

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  const contexts = data.contexts as Array<Record<string, unknown>>;
  assert.equal(data.count, 1);
  assert.equal(contexts[0].name, "probuilder.scene");
  assert.equal(contexts[0].category, "probuilder");
  assert.equal(contexts[0].source, "extension");
});

test("context list (no command) defaults to list", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand(undefined, [], {}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "context.list");
});

// ── context describe CLI ──────────────────────────────────────────────────

test("context describe returns full descriptor for local builtin context", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("describe", ["tools.groups"], {}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "context.describe");
  assert.equal(data.name, "tools.groups");
  assert.equal("destructiveHint" in data, false);
  assert.equal("bridgeCommand" in data, false);
  assert.equal("title" in data, false);
  assert.ok(data.summary);
  assert.ok(data.inputSchema);
  assert.ok(data.outputSchema);
});

test("context describe without name returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("describe", [], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "CONTEXT_DESCRIBE_NAME_REQUIRED");
});

test("context describe resolves C# built-in descriptors from live Bridge discovery", async () => {
  const runtime = makeRuntime(async (payload) => {
    if (payload.command === "context.list") {
      return { ok: true, data: { contexts: [bridgeContextDescriptor("editor.selection", {
        description: "Read current Editor selection with reusable object details without changing selection.",
        category: "editor",
        outputSchema: { type: "object", properties: { activeObjectDetail: {}, objectDetails: {}, gameObjectDetails: {} } }
      })] } };
    }
    return { ok: true, data: {} };
  });
  const result = await handleContextCommand("describe", ["editor.selection"], {}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as { outputSchema: { properties: Record<string, unknown> }; description: string; source: string };
  assert.equal(data.source, "built-in");
  assert.match(data.description, /reusable object details/i);
  assert.ok(data.outputSchema.properties.activeObjectDetail);
  assert.ok(data.outputSchema.properties.objectDetails);
  assert.ok(data.outputSchema.properties.gameObjectDetails);
});

test("context describe for unknown context returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("describe", ["nonexistent.xyz"], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_CONTEXT_NOT_FOUND");
});

test("context describe resolves extension contexts from manifest descriptors without project", async () => {
  const runtime = makeRuntime();

  const result = await handleContextCommand("describe", ["probuilder.scene"], {}, runtime, requestId);

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "context.describe");
  assert.equal(data.name, "probuilder.scene");
  assert.equal(data.category, "probuilder");
  assert.equal(data.source, "extension");
  assert.equal(data.extensionAvailability, "unknown");
  assert.equal(data.extensionId, "probuilder");
  assert.ok((data.inputSchema as { properties: Record<string, unknown> }).properties.pageSize);
  assert.ok((data.inputSchema as { properties: Record<string, unknown> }).properties.namePattern);
  assert.ok((data.outputSchema as { properties: Record<string, unknown> }).properties.objects);
});

test("context describe resolves available extension contexts from manifest descriptors", async () => {
  const projectDir = await makeProjectWithPackages({ "com.unity.probuilder": "5.2.2" });
  const runtime = makeRuntime();

  const result = await handleContextCommand("describe", ["probuilder.scene"], { project: projectDir }, runtime, requestId);

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "context.describe");
  assert.equal(data.name, "probuilder.scene");
  assert.equal(data.category, "probuilder");
  assert.equal(data.source, "extension");
  assert.equal(data.extensionAvailability, "available");
  assert.equal(data.extensionId, "probuilder");
  assert.ok((data.inputSchema as { properties: Record<string, unknown> }).properties.pageSize);
  assert.ok((data.inputSchema as { properties: Record<string, unknown> }).properties.namePattern);
  assert.ok((data.outputSchema as { properties: Record<string, unknown> }).properties.objects);
});

// ── context read CLI ──────────────────────────────────────────────────────

test("context read without name returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("read", [], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_CONTEXT_NAME_REQUIRED");
});

test("context read for unknown context returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("read", ["nonexistent.xyz"], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_CONTEXT_NOT_FOUND");
});

test("context read routes C# built-in contexts through bridge command after live descriptor discovery", async () => {
  const bridgeExecute = async (payload: { command: string }) => {
    if (payload.command === "context.list") {
      return { ok: true, data: { contexts: [bridgeContextDescriptor("project.info", { category: "project" })] } };
    }
    return {
      ok: true,
      data: {
        context: "project.info",
        data: { projectRoot: "/path/to/project", projectName: "TestProject", unityVersion: "2022.3" },
        readAt: new Date().toISOString(),
      },
    };
  };
  const runtime = makeRuntime(bridgeExecute);
  const result = await handleContextCommand("read", ["project.info"], {}, runtime, requestId);
  assert.equal(result.ok, true);
});

test("context read tools.groups uses offline CLI registry", async () => {
  const payloads: unknown[] = [];
  const runtime = makeRuntime(async (payload) => {
    payloads.push(payload);
    return { ok: true, data: {} };
  });

  const result = await handleContextCommand("read", ["tools.groups"], {}, runtime, requestId);

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.context, "tools.groups");
  assert.ok(Array.isArray(data.groups), "tools.groups must return groups array");
  assert.equal(typeof data.count, "number", "tools.groups must return count");
  assert.deepEqual(payloads, [], "tools.groups must not call the Unity bridge");
});

test("context read tools.custom uses TS defineContext project tool discovery", async () => {
  const runtime = {
    projectTools: [{
      name: "sample.echo",
      summary: "Echo a message.",
      description: "Echo a message from a project custom tool.",
      group: "sample",
      inputSchema: {},
      destructiveHint: false,
      playModeSupport: "supported"
    }]
  } as unknown as BridgeRuntime;

  const result = await handleContextCommand("read", ["tools.custom"], {}, runtime, requestId);

  assert.equal(result.ok, true);
  const data = result.data as { context: string; count: number; tools: Array<{ name: string }>; mode: string };
  assert.equal(data.context, "tools.custom");
  assert.equal(data.count, 1);
  assert.equal(data.mode, "runtime");
  assert.equal(data.tools[0].name, "sample.echo");
});

test("context read forwards structured input and recovers PowerShell escaped JSON", async () => {
  const payloads: Array<{ command: string; args: Record<string, unknown> }> = [];
  const bridgeExecute = async (payload: { command: string; args: Record<string, unknown> }) => {
    payloads.push(payload);
    if (payload.command === "context.list") {
      return { ok: true, data: { contexts: [bridgeContextDescriptor("scene.gameobject", { category: "scene" })] } };
    }
    return { ok: true, data: { context: "scene.gameobject", gameObject: { name: "Main Camera" } } };
  };
  const runtime = makeRuntime(bridgeExecute);

  const regular = await handleContextCommand("read", ["scene.gameobject"], { input: JSON.stringify({ target: "Main Camera", includeComponents: false }) }, runtime, requestId);
  const pwshEscaped = await handleContextCommand("read", ["scene.gameobject"], { input: "{\\ target\\:\\Main Camera\\,\\includeComponents\\:false}" }, runtime, requestId);
  const backslashQuoted = await handleContextCommand("read", ["scene.gameobject"], { input: '{\\"target\\":\\"Main Camera\\",\\"includeComponents\\":false}' }, runtime, requestId);

  assert.equal(regular.ok, true);
  assert.equal(pwshEscaped.ok, true);
  assert.equal(backslashQuoted.ok, true);
  const readPayloads = payloads.filter((payload) => payload.command === "context.read");
  assert.deepEqual(readPayloads[0].args, { name: "scene.gameobject", input: { target: "Main Camera", includeComponents: false }, target: "Main Camera", includeComponents: false });
  assert.deepEqual(readPayloads[1].args, { name: "scene.gameobject", input: { target: "Main Camera", includeComponents: false }, target: "Main Camera", includeComponents: false });
  assert.deepEqual(readPayloads[2].args, { name: "scene.gameobject", input: { target: "Main Camera", includeComponents: false }, target: "Main Camera", includeComponents: false });
});

test("context read keeps descriptor name when structured input uses a name field", async () => {
  const payloads: Array<{ command: string; args: Record<string, unknown> }> = [];
  const bridgeExecute = async (payload: { command: string; args: Record<string, unknown> }) => {
    payloads.push(payload);
    if (payload.command === "context.list") {
      return { ok: true, data: { contexts: [bridgeContextDescriptor("scene.gameobject.component", { category: "scene" })] } };
    }
    return { ok: true, data: { context: "scene.gameobject.component", component: { type: "Transform" } } };
  };
  const runtime = makeRuntime(bridgeExecute);

  const result = await handleContextCommand("read", ["scene.gameobject.component"], { input: JSON.stringify({ target: "Main Camera", name: "Transform" }) }, runtime, requestId);

  assert.equal(result.ok, true);
  const readPayload = payloads.find((payload) => payload.command === "context.read")!;
  assert.deepEqual(readPayload.args, {
    name: "scene.gameobject.component",
    input: { target: "Main Camera", name: "Transform" },
    target: "Main Camera"
  });
});

test("context read rejects invalid --input JSON after descriptor resolution but before bridge read execution", async () => {
  const payloads: Array<{ command: string }> = [];
  const runtime = makeRuntime(async (payload: { command: string }) => {
    payloads.push(payload);
    if (payload.command === "context.list") {
      return { ok: true, data: { contexts: [bridgeContextDescriptor("scene.gameobject", { category: "scene" })] } };
    }
    return { ok: true, data: {} };
  });

  const result = await handleContextCommand("read", ["scene.gameobject"], { input: "{not-json" }, runtime, requestId);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_CONTEXT_INPUT_JSON_INVALID");
  assert.deepEqual(payloads.map((payload) => payload.command), ["context.list"]);
});

test("context read supports --input-file", async () => {
  const tempDir = await mkdtemp(join(tmpdir(), "unity-cli-context-"));
  const inputPath = join(tempDir, "input.json");
  await writeFile(inputPath, JSON.stringify({ search: "File" }), "utf8");
  const payloads: Array<{ command: string; args: Record<string, unknown> }> = [];
  const runtime = makeRuntime(async (payload: { command: string; args: Record<string, unknown> }) => {
    payloads.push(payload);
    if (payload.command === "context.list") {
      return { ok: true, data: { contexts: [bridgeContextDescriptor("editor.menu-items", { category: "editor" })] } };
    }
    return { ok: true, data: { items: [], count: 0 } };
  });

  const result = await handleContextCommand("read", ["editor.menu-items"], { "input-file": inputPath }, runtime, requestId);

  assert.equal(result.ok, true);
  const readPayload = payloads.find((payload) => payload.command === "context.read")!;
  assert.deepEqual(readPayload.args, { name: "editor.menu-items", input: { search: "File" }, search: "File" });
});

test("context read resolves project contexts when a project or bridge target is provided", async () => {
  const payloads: Array<{ command: string; args: Record<string, unknown> }> = [];
  const runtime = makeRuntime(async (payload: { command: string; args: Record<string, unknown> }) => {
    payloads.push(payload);
    if (payload.command === "context.list") {
      return {
        ok: true,
        data: {
          contexts: [
            {
              name: "sample.project-summary",
              description: "Read a sample project summary.",
              category: "sample",
              source: "project",
              inputSchema: { type: "object", properties: {} },
              outputSchema: { type: "object", properties: { ok: { type: "boolean" } } }
            }
          ]
        }
      };
    }
    return { ok: true, data: { context: "sample.project-summary", ok: true } };
  });

  const result = await handleContextCommand("read", ["sample.project-summary"], { project: "D:/tmp/UnityProject" }, runtime, requestId);

  assert.equal(result.ok, true);
  assert.equal(payloads[0].command, "context.list");
  assert.equal(payloads[1].command, "context.read");
  assert.deepEqual(payloads[1].args, { input: {}, name: "sample.project-summary" });
});

test("context read routes available extension contexts through bridge", async () => {
  const projectDir = await makeProjectWithPackages({ "com.unity.probuilder": "5.2.2" });
  const payloads: Array<{ command: string; args: Record<string, unknown> }> = [];
  const runtime = makeRuntime(async (payload: { command: string; args: Record<string, unknown> }) => {
    payloads.push(payload);
    return { ok: true, data: { context: "probuilder.scene", objectCount: 0, objects: [] } };
  });

  const result = await handleContextCommand("read", ["probuilder.scene"], { project: projectDir, input: JSON.stringify({ pageSize: 5 }) }, runtime, requestId);

  assert.equal(result.ok, true);
  assert.equal(payloads[0].command, "context.read");
  assert.deepEqual(payloads[0].args, { input: { pageSize: 5 }, name: "probuilder.scene", pageSize: 5 });
});

// ── unknown subcommand ────────────────────────────────────────────────────

test("unknown context subcommand returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("unknown", [], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_CONTEXT_COMMAND");
});

/** 创建带 packages-lock 的最小 Unity project，用于扩展 Context 可用性测试。 */
async function makeProjectWithPackages(packages: Record<string, string>): Promise<string> {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-context-ext-"));
  const packagesDir = join(projectDir, "Packages");
  await mkdir(packagesDir, { recursive: true });
  await writeFile(join(packagesDir, "manifest.json"), JSON.stringify({ dependencies: packages }), "utf8");
  await writeFile(join(packagesDir, "packages-lock.json"), JSON.stringify({
    dependencies: Object.fromEntries(Object.entries(packages).map(([id, version]) => [id, { version }]))
  }), "utf8");
  return projectDir;
}
