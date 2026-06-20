/**
 * Unit tests for tools list/groups/search/describe CLI commands.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { bridgeCommandRegistry, type BridgeRequest } from "./bridge/index.js";
import { handleToolsCommand, type ToolsRuntime } from "./tools.js";
import { builtinToolMetadata, builtinToolGroups, findToolByName, filterToolsByGroup, searchTools, supportStatusForTool } from "./tools/metadata/index.js";

const requestId = "test_req_tools_001";
const emptyRuntime: ToolsRuntime = {};

// ── tool-metadata module ───────────────────────────────────

test("builtinToolMetadata returns a non-empty array", () => {
  const tools = builtinToolMetadata();
  assert.ok(Array.isArray(tools));
  assert.ok(tools.length > 0, "Expected at least one built-in tool");
});

test("every tool has the required frozen fields", () => {
  const tools = builtinToolMetadata();
  for (const tool of tools) {
    assert.ok(tool.name, `missing name in ${JSON.stringify(tool)}`);
    assert.ok(tool.title, `missing title in ${tool.name}`);
    assert.ok(tool.description, `missing description in ${tool.name}`);
    assert.ok(tool.group, `missing group in ${tool.name}`);
    assert.ok(tool.inputSchema, `missing inputSchema in ${tool.name}`);
    assert.ok(typeof tool.readOnlyHint === "boolean", `missing readOnlyHint in ${tool.name}`);
    assert.ok(typeof tool.destructiveHint === "boolean", `missing destructiveHint in ${tool.name}`);
  }
});

test("tool names are unique", () => {
  const tools = builtinToolMetadata();
  const names = tools.map((t) => t.name);
  const unique = new Set(names);
  assert.equal(names.length, unique.size, "Duplicate tool names found");
});

test("every built-in tool declares an explicit handler", () => {
  const validHandlers = new Set(["bridge", "job", "offline", "hybrid"]);
  const tools = builtinToolMetadata();
  for (const tool of tools) {
    assert.ok(tool.handler, `${tool.name} should declare handler`);
    assert.ok(validHandlers.has(tool.handler), `${tool.name} has unknown handler ${tool.handler}`);
  }
});

test("live bridge tool metadata resolves through BridgeCommandRegistry", () => {
  const tools = builtinToolMetadata().filter((tool) => tool.handler === "bridge" && supportStatusForTool(tool) === "live-bridge");
  assert.ok(tools.length > 0);
  for (const tool of tools) {
    assert.ok(tool.cliCommand, `${tool.name} is missing cliCommand`);
    const [group, ...commandParts] = tool.cliCommand.split(" ");
    const spec = bridgeCommandRegistry.lookup(group, commandParts.join(" "));
    assert.ok(spec, `${tool.name} cliCommand is missing from BridgeCommandRegistry: ${tool.cliCommand}`);
    assert.equal(spec.bridgeCommand, tool.name);
  }
});

test("builtinToolGroups returns sorted groups", () => {
  const groups = builtinToolGroups();
  assert.ok(groups.length > 0);
  const sorted = [...groups].sort();
  assert.deepEqual(groups, sorted, "Groups should be sorted");
});

test("scene.hierarchy is in the built-in tools", () => {
  const tool = findToolByName("scene.hierarchy");
  assert.ok(tool, "scene.hierarchy should exist");
  assert.equal(tool.group, "scene");
  assert.equal(tool.readOnlyHint, true);
  assert.equal(tool.destructiveHint, false);
});

test("scene.save is marked destructive and planned", () => {
  const tool = findToolByName("scene.save");
  assert.ok(tool, "scene.save should exist");
  assert.equal(tool.destructiveHint, true);
  assert.equal(tool.supportStatus, "planned");
});

test("findToolByName returns undefined for unknown tool", () => {
  const tool = findToolByName("nonexistent.tool");
  assert.equal(tool, undefined);
});

test("filterToolsByGroup returns only tools from that group", () => {
  const sceneTools = filterToolsByGroup("scene");
  assert.ok(sceneTools.length > 0);
  for (const t of sceneTools) {
    assert.equal(t.group, "scene");
  }
});

test("searchTools finds by name substring", () => {
  const results = searchTools("hierarchy");
  assert.ok(results.length > 0);
  const names = results.map((t) => t.name);
  assert.ok(names.includes("scene.hierarchy"), `Expected scene.hierarchy in ${names}`);
});

test("searchTools finds by group", () => {
  const results = searchTools("probuilder");
  assert.ok(results.length > 0);
  for (const t of results) {
    assert.ok(t.name.includes("probuilder") || t.group === "probuilder" || t.description.toLowerCase().includes("probuilder"), `${t.name} should match probuilder`);
  }
});

test("searchTools returns empty for no match", () => {
  const results = searchTools("zzzz_nonexistent_zzzz");
  assert.equal(results.length, 0);
});

// ── tools list ─────────────────────────────────────────────

test("tools list returns all built-in tools", async () => {
  const result = await handleToolsCommand("list", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: Array<{ name: string }> };
  assert.ok(data.count > 0);
  assert.ok(data.tools.length > 0);
  assert.equal((data.tools as Array<{ name: string; supportStatus: string }>).find((t) => t.name === "scene.active")?.supportStatus, "planned");
  const names = data.tools.map((t) => t.name);
  assert.ok(names.includes("scene.hierarchy"), "scene.hierarchy should be in list");
});

test("tools list with --group filters by group", async () => {
  const result = await handleToolsCommand("list", [], { group: "scene" }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: Array<{ name: string; group: string }> };
  assert.ok(data.count > 0);
  for (const t of data.tools) {
    assert.equal(t.group, "scene");
  }
});

test("tools list with --group unknown returns empty", async () => {
  const result = await handleToolsCommand("list", [], { group: "nonexistent" }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: unknown[] };
  assert.equal(data.count, 0);
  assert.equal(data.tools.length, 0);
});

test("tools list --format text includes text field", async () => {
  const result = await handleToolsCommand("list", [], { format: "text" }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { text: string };
  assert.ok(data.text.length > 0);
  assert.ok(data.text.includes("scene.hierarchy"));
});

test("tools list --include-custom with runtime custom tools merges them", async () => {
  const customTool = {
    name: "custom.my-tool",
    title: "My Custom Tool",
    description: "A custom tool for testing",
    group: "custom",
    inputSchema: { type: "object" as const, properties: {} },
    readOnlyHint: true,
    destructiveHint: false
  };
  const runtime: ToolsRuntime = { projectTools: [customTool] };
  const result = await handleToolsCommand("list", [], { "include-custom": true }, runtime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { tools: Array<{ name: string }> };
  const names = data.tools.map((t) => t.name);
  assert.ok(names.includes("custom.my-tool"));
});

// ── tools groups ───────────────────────────────────────────

test("tools groups returns group names", async () => {
  const result = await handleToolsCommand("groups", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; groups: string[] };
  assert.ok(data.count > 0);
  assert.ok(data.groups.includes("scene"));
  assert.ok(data.groups.includes("camera"));
});

test("tools groups keeps built-in groups without custom context", async () => {
  const result = await handleToolsCommand("groups", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { groups: string[] };
  assert.ok(data.groups.includes("scene"));
  assert.equal(data.groups.includes("project"), false);
});

test("tools groups includes runtime custom tool groups", async () => {
  const runtime: ToolsRuntime = { projectTools: [{
    name: "project.bake",
    title: "Project Bake",
    description: "Bakes project data.",
    group: "project",
    inputSchema: { type: "object" },
    readOnlyHint: false,
    destructiveHint: false
  }] };
  const result = await handleToolsCommand("groups", [], {}, runtime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { groups: string[] };
  assert.ok(data.groups.includes("project"));
});

// ── tools search ───────────────────────────────────────────

test("tools search finds tools by keyword", async () => {
  const result = await handleToolsCommand("search", ["hierarchy"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: Array<{ name: string }> };
  assert.ok(data.count > 0);
  const names = data.tools.map((t) => t.name);
  assert.ok(names.includes("scene.hierarchy"));
});

test("tools search without keyword returns error", async () => {
  const result = await handleToolsCommand("search", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "TOOLS_SEARCH_KEYWORD_REQUIRED");
});

test("tools search --format text returns text field", async () => {
  const result = await handleToolsCommand("search", ["scene"], { format: "text" }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { text: string };
  assert.ok(data.text.length > 0);
});

// ── tools describe ─────────────────────────────────────────

test("tools describe scene.hierarchy returns full metadata", async () => {
  const result = await handleToolsCommand("describe", ["scene.hierarchy"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "scene.hierarchy");
  assert.equal(data.group, "scene");
  assert.equal(data.readOnlyHint, true);
  assert.equal(data.destructiveHint, false);
  assert.ok(data.inputSchema);
  assert.ok(data.description);
  assert.ok(Array.isArray(data.examples));
  assert.equal((data as { mode: string }).mode, "offline");
  assert.equal((data as { requiresUnity: boolean }).requiresUnity, false);
});

test("tools describe for unknown tool returns E_TOOL_NOT_FOUND_FOR_TARGET", async () => {
  const result = await handleToolsCommand("describe", ["nonexistent.tool"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});

test("tools describe without name returns error", async () => {
  const result = await handleToolsCommand("describe", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "TOOLS_DESCRIBE_NAME_REQUIRED");
});

test("tools describe for destructive tool shows destructiveHint", async () => {
  const result = await handleToolsCommand("describe", ["go.delete"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.destructiveHint, true);
});

// ── S2.3 metadata: editor, package, test, build ────────────

test("editor.refresh metadata exists with job handler and longRunning", () => {
  const tool = findToolByName("editor.refresh");
  assert.ok(tool, "editor.refresh should exist");
  assert.equal(tool.group, "editor");
  assert.equal(tool.handler, "job");
  assert.equal(tool.longRunning, true);
  assert.equal(tool.readOnlyHint, false);
  assert.equal(tool.destructiveHint, false);
  assert.ok(tool.examples && tool.examples.length > 0);
});

test("cli.batch schema and run metadata cover S3.5 orchestration", () => {
  const schema = findToolByName("cli.batch.schema");
  assert.ok(schema, "cli.batch.schema should exist");
  assert.equal(schema.group, "cli");
  assert.equal(schema.handler, "offline");
  assert.equal(schema.readOnlyHint, true);
  assert.equal(schema.destructiveHint, false);
  assert.ok(schema.outputSchema);

  const run = findToolByName("cli.batch.run");
  assert.ok(run, "cli.batch.run should exist");
  assert.equal(run.handler, "offline");
  assert.equal(run.supportsDryRun, true);
  assert.ok(run.outputSchema);
  assert.match(run.cancellationSemantics ?? "", /child jobs/i);
});

test("representative destructive tools declare dry-run support", () => {
  for (const name of ["asset.delete", "go.delete", "scene.save", "code.exec"]) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool.destructiveHint, true);
    assert.equal(tool.supportsDryRun, true);
  }
});

test("cli.package.resolve metadata exists with hybrid handler", () => {
  const tool = findToolByName("cli.package.resolve");
  assert.ok(tool, "cli.package.resolve should exist");
  assert.equal(tool.group, "cli");
  assert.equal(tool.handler, "hybrid");
  assert.equal(supportStatusForTool(tool), "bridge-job");
  assert.equal(tool.longRunning, true);
  assert.equal(tool.readOnlyHint, false);
});

test("cli.package.registry metadata exists with offline handler", () => {
  const tool = findToolByName("cli.package.registry");
  assert.ok(tool, "cli.package.registry should exist");
  assert.equal(tool.group, "cli");
  assert.equal(tool.handler, "offline");
  assert.equal(tool.readOnlyHint, false);
});

test("cli.package.scoped-registry metadata exists with offline handler", () => {
  const tool = findToolByName("cli.package.scoped-registry");
  assert.ok(tool, "cli.package.scoped-registry should exist");
  assert.equal(tool.group, "cli");
  assert.equal(tool.handler, "offline");
  assert.equal(tool.readOnlyHint, false);
});

test("test.run metadata exists with job handler and longRunning", () => {
  const tool = findToolByName("test.run");
  assert.ok(tool, "test.run should exist");
  assert.equal(tool.group, "test");
  assert.equal(tool.handler, "job");
  assert.equal(tool.longRunning, true);
  assert.equal(tool.readOnlyHint, false);
  assert.equal(tool.destructiveHint, false);
  assert.ok(tool.examples && tool.examples.length > 0);
});

test("test.poll metadata exists with offline handler", () => {
  const tool = findToolByName("test.poll");
  assert.ok(tool, "test.poll should exist");
  assert.equal(tool.group, "test");
  assert.equal(tool.handler, "offline");
  assert.equal(tool.readOnlyHint, true);
  assert.ok(tool.outputSchema);
});

test("build.run metadata exists with job handler and longRunning", () => {
  const tool = findToolByName("build.run");
  assert.ok(tool, "build.run should exist");
  assert.equal(tool.group, "build");
  assert.equal(tool.handler, "job");
  assert.equal(tool.longRunning, true);
  assert.equal(tool.readOnlyHint, false);
  assert.equal(tool.destructiveHint, false);
  assert.ok(tool.examples && tool.examples.length > 0);
});

test("build.status metadata exists with offline handler", () => {
  const tool = findToolByName("build.status");
  assert.ok(tool, "build.status should exist");
  assert.equal(tool.group, "build");
  assert.equal(tool.handler, "offline");
  assert.equal(tool.readOnlyHint, true);
  assert.ok(tool.outputSchema);
});

test("test and build job domains expose status logs and cancel metadata", () => {
  for (const name of ["test.poll", "test.logs", "build.status", "build.logs"]) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool?.handler, "offline");
    assert.equal(tool?.readOnlyHint, true);
    assert.ok(tool?.outputSchema);
  }

  for (const name of ["test.cancel", "build.cancel"]) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool?.handler, "offline");
    assert.equal(tool?.readOnlyHint, false);
    assert.equal(tool?.destructiveHint, true);
    assert.match(tool?.cancellationSemantics ?? "", /job cancel/i);
  }
});

test("S2.3 editor group has all required tools", () => {
  const editorTools = filterToolsByGroup("editor");
  const names = editorTools.map((t) => t.name);
  assert.ok(names.includes("editor.state"));
  assert.ok(names.includes("editor.play"));
  assert.ok(names.includes("editor.pause"));
  assert.ok(names.includes("editor.stop"));
  assert.ok(names.includes("editor.menu"));
  assert.ok(names.includes("editor.console"));
  assert.ok(names.includes("editor.refresh"));
});

test("S2.3 test group has all required tools", () => {
  const testTools = filterToolsByGroup("test");
  const names = testTools.map((t) => t.name);
  assert.ok(names.includes("test.run"));
  assert.ok(names.includes("test.poll"));
  assert.ok(names.includes("test.logs"));
  assert.ok(names.includes("test.cancel"));
});

test("S2.3 build group has all required tools", () => {
  const buildTools = filterToolsByGroup("build");
  const names = buildTools.map((t) => t.name);
  assert.ok(names.includes("build.run"));
  assert.ok(names.includes("build.status"));
  assert.ok(names.includes("build.logs"));
  assert.ok(names.includes("build.cancel"));
});

test("S3.1 creation domains have frozen metadata coverage", () => {
  const expectedByGroup = new Map([
    ["camera", ["camera.list", "camera.info", "camera.screenshot"]],
    ["material", ["material.info", "material.create", "material.assign", "material.set"]],
    ["shader", ["shader.info", "shader.read", "shader.create", "shader.update"]],
    ["texture", ["texture.info", "texture.create", "texture.sprite", "texture.import-settings"]],
    ["ui", ["ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.info", "ui.document.create", "ui.hierarchy"]]
  ]);

  for (const [group, expectedNames] of expectedByGroup) {
    const tools = filterToolsByGroup(group);
    assert.deepEqual(tools.map((tool) => tool.name), expectedNames);
    for (const tool of tools) {
      assert.equal(tool.handler, "bridge", `${tool.name} should be a live bridge handler`);
      assert.equal(tool.docsTopic, group, `${tool.name} should be discoverable from its docs topic`);
      assert.ok(tool.cliCommand, `${tool.name} should expose a CLI command`);
    }
  }
});

test("S3.1 creation-domain write tools are marked mutating but non-destructive", () => {
  const writeTools = ["camera.screenshot", "material.create", "material.assign", "material.set", "shader.create", "shader.update", "texture.create", "texture.sprite", "texture.import-settings", "ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.create"];

  for (const name of writeTools) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool.readOnlyHint, false, `${name} should be marked as mutating`);
    assert.equal(tool.destructiveHint, false, `${name} should not be marked destructive`);
  }
});

test("tools describe test.run returns full metadata", async () => {
  const result = await handleToolsCommand("describe", ["test.run"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "test.run");
  assert.equal(data.group, "test");
  assert.equal(data.handler, "job");
  assert.equal(data.longRunning, true);
});

test("tools describe build.run returns full metadata", async () => {
  const result = await handleToolsCommand("describe", ["build.run"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "build.run");
  assert.equal(data.group, "build");
  assert.equal(data.handler, "job");
  assert.equal(data.longRunning, true);
});

test("tools describe editor.refresh returns job metadata", async () => {
  const result = await handleToolsCommand("describe", ["editor.refresh"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "editor.refresh");
  assert.equal(data.handler, "job");
  assert.equal(data.longRunning, true);
});

// ── S3.2 metadata: graphics, physics, animation, vfx ───────

test("S3.2 graphics group has quality, pipeline, volume and screenshot tools", () => {
  const names = filterToolsByGroup("graphics").map((t) => t.name);
  assert.deepEqual(names, ["graphics.quality", "graphics.settings", "graphics.pipeline", "graphics.volume", "graphics.screenshot"]);
  assert.equal(findToolByName("graphics.pipeline")?.readOnlyHint, true);
  assert.equal(findToolByName("graphics.volume")?.readOnlyHint, true);
});

test("S3.4 profiler domain has frozen metadata coverage", () => {
  const profilerTools = filterToolsByGroup("profiler");
  const expectedNames = ["profiler.status", "profiler.start", "profiler.stop", "profiler.pause", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.capture", "profiler.export"];
  assert.deepEqual(profilerTools.map((tool) => tool.name), expectedNames);

  for (const name of expectedNames) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool?.docsTopic, "profiler");
    assert.ok(tool?.cliCommand, `${name} should expose a CLI command`);
    assert.equal(tool?.architectureSensitive, true, `${name} should be marked architecture sensitive`);
    assert.equal(tool?.frozenDecision, "ALP-165-Q4", `${name} should reference the Profiler frozen decision`);
    assert.deepEqual(tool?.coverageScope, ["session", "counter", "memory", "frame-debugger", "export"], `${name} should declare full frozen coverage scope`);
  }

  for (const name of ["profiler.status", "profiler.counter", "profiler.memory", "profiler.frame"]) {
    const tool = findToolByName(name);
    assert.equal(tool?.readOnlyHint, true, `${name} should be read-only from Unity state perspective`);
    assert.equal(tool?.destructiveHint, false, `${name} should not be destructive`);
  }

  for (const name of ["profiler.start", "profiler.stop", "profiler.pause", "profiler.capture", "profiler.export"]) {
    const tool = findToolByName(name);
    assert.equal(tool?.readOnlyHint, false, `${name} should change profiler session or write an artifact`);
    assert.equal(tool?.destructiveHint, false, `${name} should not be destructive`);
  }

  assert.match(findToolByName("profiler.pause")?.description ?? "", /Profiler\.enabled/);
});

test("tools describe profiler.memory returns full metadata", async () => {
  const result = await handleToolsCommand("describe", ["profiler.memory"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "profiler.memory");
  assert.equal(data.group, "profiler");
  assert.equal(data.readOnlyHint, true);
  assert.equal(data.destructiveHint, false);
  assert.equal(data.architectureSensitive, true);
  assert.equal(data.frozenDecision, "ALP-165-Q4");
  assert.deepEqual(data.coverageScope, ["session", "counter", "memory", "frame-debugger", "export"]);
});

test("planned bridge contract tools expose planned support status", () => {
  const plannedNames = ["scene.active", "scene.build-settings", "scene.create", "scene.load", "scene.save", "go.inspect", "go.create", "go.modify", "go.delete", "component.add", "component.remove", "component.set", "script.create", "script.read", "script.edit", "script.validate"];
  for (const name of plannedNames) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool.handler, "bridge", `${name} keeps bridge handler contract classification`);
    assert.equal(supportStatusForTool(tool), "planned", `${name} should be user-facing planned`);
  }
});

test("hybrid tools expose bridge plus job support status", () => {
  const hybridNames = ["cli.package.install", "cli.package.remove", "cli.package.resolve", "code.exec", "profiler.start", "profiler.capture", "probuilder.create-shape", "probuilder.create-poly-shape", "probuilder.extrude-faces", "probuilder.delete-faces", "probuilder.mesh-info"];
  for (const name of hybridNames) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool.handler, "hybrid", `${name} should keep hybrid handler classification`);
    assert.equal(supportStatusForTool(tool), "bridge-job", `${name} should be user-facing bridge plus job`);
  }
});

test("S3.2 physics group has settings, raycast and simulation tools", () => {
  const names = filterToolsByGroup("physics").map((t) => t.name);
  assert.deepEqual(names, ["physics.settings", "physics.raycast", "physics.simulation"]);
  assert.equal(findToolByName("physics.raycast")?.readOnlyHint, true);
  assert.equal(findToolByName("physics.simulation")?.handler, "bridge");
});

test("S3.2 animation and vfx groups have required tools", () => {
  assert.deepEqual(filterToolsByGroup("animation").map((t) => t.name), ["animation.clip", "animation.state", "animation.play", "animation.stop"]);
  assert.deepEqual(filterToolsByGroup("vfx").map((t) => t.name), ["vfx.graph", "vfx.effect"]);
});

test("tools describe graphics.volume returns bridge metadata", async () => {
  const result = await handleToolsCommand("describe", ["graphics.volume"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "graphics.volume");
  assert.equal(data.group, "graphics");
  assert.equal(data.handler, "bridge");
  assert.equal(data.readOnlyHint, true);
  assert.equal(data.cliCommand, "graphics volume");
});

test("tools describe animation.play returns full bridge metadata", async () => {
  const result = await handleToolsCommand("describe", ["animation.play"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "animation.play");
  assert.equal(data.group, "animation");
  assert.equal(data.handler, "bridge");
  assert.equal(data.cliCommand, "animation play");
});

// ── tools unknown subcommand ───────────────────────────────

test("tools unknown subcommand returns error", async () => {
  const result = await handleToolsCommand("unknown", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_TOOLS_COMMAND");
});

// ── tools list is default subcommand ───────────────────────

test("tools without subcommand defaults to list", async () => {
  const result = await handleToolsCommand(undefined, [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: unknown[] };
  assert.ok(data.count > 0);
});

// ── tools run: handleToolsRun routing ─────────────────────

test("tools run built-in bridge tool routes to native bridge command", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { result: { output: "ok" } } };
    }
  };

  const result = await handleToolsCommand("run", ["scene.hierarchy"], {}, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "scene.hierarchy");
});

test("tools run with target=runtime project tool routes to runtime-tool.run", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    projectTools: [{
      name: "sample.runtime-echo",
      title: "Sample Runtime Echo",
      description: "Echoes input from runtime",
      group: "sample",
      inputSchema: { type: "object", properties: { message: { type: "string" } }, required: ["message"] },
      readOnlyHint: true,
      destructiveHint: false,
      handler: "bridge-runtime",
      target: "runtime",
      availabilityScope: "playModeSession"
    }],
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { result: { requestId: "req", input: {}, isPlaying: true, frameCount: 42 } } };
    }
  };

  const result = await handleToolsCommand("run", ["sample.runtime-echo"], { "include-custom": true, yes: true }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "runtime-tool.run");
});

test("tools run target=both with --runtime flag routes to runtime-tool.run", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    projectTools: [{
      name: "dual.tool",
      title: "Dual Tool",
      description: "Works in both domains",
      group: "sample",
      inputSchema: { type: "object" },
      readOnlyHint: true,
      destructiveHint: false,
      handler: "bridge",
      target: "both",
      availabilityScope: "editorSession"
    }],
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { result: "ok" } };
    }
  };

  const result = await handleToolsCommand("run", ["dual.tool"], { "include-custom": true, runtime: true, yes: true }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "runtime-tool.run");
});

test("tools run custom editor-domain tool routes to project-tool.run", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    projectTools: [{
      name: "dual.tool",
      title: "Dual Tool",
      description: "Works in both domains",
      group: "sample",
      inputSchema: { type: "object" },
      readOnlyHint: true,
      destructiveHint: false,
      handler: "bridge",
      target: "both",
      availabilityScope: "editorSession"
    }],
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { result: "ok" } };
    }
  };

  const result = await handleToolsCommand("run", ["dual.tool"], { "include-custom": true }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "project-tool.run");
});

test("tools run without tool name returns E_TOOL_NOT_FOUND_FOR_TARGET", async () => {
  const result = await handleToolsCommand("run", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});

test("tools run with unknown tool name returns E_TOOL_NOT_FOUND_FOR_TARGET", async () => {
  const result = await handleToolsCommand("run", ["nonexistent.tool"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});

test("tools run custom editor tool routes to project-tool.run", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    projectTools: [{
      name: "my.editor-tool",
      title: "Editor Tool",
      description: "Editor-only tool",
      group: "custom",
      inputSchema: { type: "object" },
      readOnlyHint: true,
      destructiveHint: false,
      handler: "bridge",
      target: "editor",
      availabilityScope: "editorSession"
    }],
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { result: "ok" } };
    }
  };

  const result = await handleToolsCommand("run", ["my.editor-tool"], { "include-custom": true }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "project-tool.run");
});

test("tools run runtime tool discovery uses runtime-tool.list (not project-tool.list)", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      if (payload.command === "runtime-tool.list") {
        return { ok: true, data: { tools: [{ name: "sample.runtime-echo", title: "Sample Runtime Echo", description: "Echoes input from runtime", group: "sample", target: "runtime", handler: "bridge-runtime" }] } };
      }
      return { ok: true, data: { result: { requestId: "req", isPlaying: true } } };
    }
  };

  const result = await handleToolsCommand("run", ["sample.runtime-echo"], { "include-custom": true, yes: true }, runtime, requestId);
  assert.equal(result.ok, true);
  // Discovery should use runtime-tool.list, then execution uses runtime-tool.run
  assert.equal(requests[0].command, "runtime-tool.list");
  assert.equal(requests[1].command, "runtime-tool.run");
});

test("tools run editor-domain tool discovery falls back to project-tool.list", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      if (payload.command === "runtime-tool.list") {
        return { ok: true, data: { tools: [] } };
      }
      if (payload.command === "project-tool.list") {
        return { ok: true, data: { tools: [{ name: "my.editor-tool", title: "Editor Tool", description: "Editor-only", group: "custom", target: "editor" }] } };
      }
      return { ok: true, data: { result: "ok" } };
    }
  };

  const result = await handleToolsCommand("run", ["my.editor-tool"], { "include-custom": true }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "runtime-tool.list");
  assert.equal(requests[1].command, "project-tool.list");
  assert.equal(requests[2].command, "project-tool.run");
  // Verify project-tool.run receives the tool name and input
  assert.equal((requests[2].args as Record<string, unknown>).name, "my.editor-tool");
});

// ── tools.run descriptor is not planned/stub ──────────────

test("tools.run descriptor is not planned/stub", () => {
  const tool = findToolByName("tools.run");
  assert.ok(tool, "tools.run must exist in builtin metadata");
  assert.equal(tool.supportStatus, "live-bridge", "tools.run supportStatus must be live-bridge, not planned");
  assert.equal(tool.handler, "hybrid", "tools.run handler must be hybrid (routes dynamically)");
  assert.ok(!tool.description.includes("Reserved for P3"), "description must not say Reserved for P3");
  assert.ok(!tool.description.includes("currently returns"), "description must not say currently returns stub error");
});

// ── Skill/descriptor/docs CLI parameter consistency ────────

test("skill does not use stale CLI parameter/field/error names", () => {
  const skillPath = fileURLToPath(new URL("../skills/unity_cli/SKILL.md", import.meta.url));
  const skill = readFileSync(skillPath, "utf8");

  // --input-json is not a CLI parameter; the CLI uses --input
  const inputJsonMatches = skill.match(/--input-json/g);
  // Allow exactly 1 occurrence in the "What NOT to Do" section (warning against it)
  const inputJsonAllowed = (skill.match(/Do not.*--input-json/g) ?? []).length;
  assert.ok(
    (inputJsonMatches?.length ?? 0) - inputJsonAllowed <= 0,
    `skill must not teach --input-json (found ${(inputJsonMatches?.length ?? 0)} occurrences beyond the warning)`
  );

  // JSON field next_actions, not nextActions
  assert.ok(!skill.includes("nextActions"), "skill must use next_actions (snake_case), not nextActions (camelCase)");

  // E_RUNTIME_NOT_READY, not E_PLAYMODE_NOT_READY
  assert.ok(!skill.includes("E_PLAYMODE_NOT_READY"), "skill must use E_RUNTIME_NOT_READY, not E_PLAYMODE_NOT_READY");
});

// ── ALP-466 regression: tools run built-in bridge + custom tools ──

test("ALP-466: tools run scene.hierarchy passes --input as bridge args", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { roots: [], count: 0 } };
    }
  };

  const result = await handleToolsCommand("run", ["scene.hierarchy"], { input: '{"pageSize":1}' }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "scene.hierarchy");
  assert.deepEqual((requests[0].args as Record<string, unknown>).pageSize, 1);
});

test("ALP-466: tools run built-in bridge tool without --input sends empty args", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { name: "Untitled", path: "/Assets/Untitled.unity", buildIndex: -1 } };
    }
  };

  const result = await handleToolsCommand("run", ["scene.active"], {}, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "scene.active");
  assert.deepEqual(requests[0].args, {});
});

test("ALP-466: tools run built-in job-handler tool with empty runtime returns E_TOOL_HANDLER_UNSUPPORTED", async () => {
  const result = await handleToolsCommand("run", ["editor.refresh"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_HANDLER_UNSUPPORTED");
});

// ── ALP-477/478: job-handler routing via dispatchJobHandler ──────────

test("ALP-478: tools run test.run routes to handleTest with --input merged", async () => {
  const { JobStore } = await import("./job-store.js");
  const { JobRunner } = await import("./job-runner.js");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const { mkdirSync, rmSync } = await import("node:fs");
  const dataDir = join(tmpdir(), `alp478-test-${Date.now()}`);
  mkdirSync(dataDir, { recursive: true });
  try {
    const store = new JobStore(dataDir);
    const runner = new JobRunner(store, dataDir);
    const runtime: ToolsRuntime = { dataDir, store, runner } as ToolsRuntime;
    // test.run without --unity will fail with UNITY_NOT_FOUND, but the routing
    // should reach handleTest (not E_TOOL_HANDLER_UNSUPPORTED)
    const result = await handleToolsCommand("run", ["test.run"], { input: '{"platform":"EditMode"}' }, runtime, requestId);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "UNITY_NOT_FOUND");
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
});

test("ALP-478: tools run build.run routes to handleBuild with --input merged", async () => {
  const { JobStore } = await import("./job-store.js");
  const { JobRunner } = await import("./job-runner.js");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const { mkdirSync, rmSync } = await import("node:fs");
  const dataDir = join(tmpdir(), `alp478-build-${Date.now()}`);
  mkdirSync(dataDir, { recursive: true });
  try {
    const store = new JobStore(dataDir);
    const runner = new JobRunner(store, dataDir);
    const runtime: ToolsRuntime = { dataDir, store, runner } as ToolsRuntime;
    // build.run without --unity will fail with UNITY_NOT_FOUND
    const result = await handleToolsCommand("run", ["build.run"], { input: '{"target":"StandaloneWindows64"}' }, runtime, requestId);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "UNITY_NOT_FOUND");
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
});

test("ALP-478: tools run editor.refresh routes to handleEditor with --input merged", async () => {
  const { JobStore } = await import("./job-store.js");
  const { JobRunner } = await import("./job-runner.js");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const { mkdirSync, rmSync } = await import("node:fs");
  const dataDir = join(tmpdir(), `alp478-editor-${Date.now()}`);
  mkdirSync(dataDir, { recursive: true });
  try {
    const store = new JobStore(dataDir);
    const runner = new JobRunner(store, dataDir);
    const runtime: ToolsRuntime = { dataDir, store, runner } as ToolsRuntime;
    // editor.refresh without --unity will fail with UNITY_NOT_FOUND
    const result = await handleToolsCommand("run", ["editor.refresh"], { input: '{"compile":true}' }, runtime, requestId);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "UNITY_NOT_FOUND");
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
});

test("ALP-478: mergeInputToFlags converts camelCase to kebab-case", async () => {
  // Verify that --input '{"unity":"/path/Unity.exe","project":"/path/project"}' is merged as --unity and --project
  // by checking that routing reaches the job handler instead of E_TOOL_HANDLER_UNSUPPORTED.
  const { JobStore } = await import("./job-store.js");
  const { JobRunner } = await import("./job-runner.js");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const { mkdirSync, rmSync } = await import("node:fs");
  const dataDir = join(tmpdir(), `alp478-merge-${Date.now()}`);
  mkdirSync(dataDir, { recursive: true });
  try {
    const store = new JobStore(dataDir);
    const runner = new JobRunner(store, dataDir);
    const runtime: ToolsRuntime = { dataDir, store, runner } as ToolsRuntime;
    // Pass unity/project via --input JSON (camelCase keys).
    // Without --wait, the job is created and returned immediately.
    // The key assertion: routing works (not E_TOOL_HANDLER_UNSUPPORTED) and
    // a job was created (jobId is present).
    const result = await handleToolsCommand(
      "run",
      ["build.run"],
      { input: JSON.stringify({ unity: "/nonexistent/Unity.exe", project: "/nonexistent/project", target: "StandaloneWindows64" }) },
      runtime,
      requestId
    );
    assert.notEqual(result.error?.code, "E_TOOL_HANDLER_UNSUPPORTED");
    // Without --wait, the job is queued/running and a jobId is returned
    assert.ok(result.ok, `Expected ok=true, got error: ${result.error?.code} ${result.error?.message}`);
    assert.ok((result.data as Record<string, unknown>)?.jobId, "Expected jobId in result");
  } finally {
    await new Promise(r => setTimeout(r, 200));
    rmSync(dataDir, { recursive: true, force: true });
  }
});

test("ALP-466: tools run custom project tool passes name and input to project-tool.run", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    projectTools: [{
      name: "sample.echo",
      title: "Sample Echo",
      description: "Echoes input",
      group: "sample",
      inputSchema: { type: "object", properties: { message: { type: "string" } } },
      readOnlyHint: true,
      destructiveHint: false,
      handler: "bridge",
      target: "editor",
      availabilityScope: "editorSession"
    }],
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { result: "ok" } };
    }
  };

  const result = await handleToolsCommand("run", ["sample.echo"], { "include-custom": true, input: '{"message":"alp461"}' }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "project-tool.run");
  assert.equal((requests[0].args as Record<string, unknown>).name, "sample.echo");
  assert.deepEqual((requests[0].args as Record<string, unknown>).input, { message: "alp461" });
});

test("ALP-466: tools run built-in hybrid tool routes to native bridge command", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { result: "ok" } };
    }
  };

  // code.exec is a hybrid handler tool with bridgeCommand "code.exec"
  const result = await handleToolsCommand("run", ["code.exec"], { input: '{"code":"return 42;"}' }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "code.exec");
  assert.deepEqual((requests[0].args as Record<string, unknown>).code, "return 42;");
});

// ── ALP-467 Regression: Play Mode tool discovery and --target/--runtime flags ──

test("shouldDiscoverProjectTools returns true when --target flag is provided", async () => {
  // Regression: passing --target without --bridge-endpoint should still discover tools
  // because the target instance has a bridge endpoint.
  const { shouldDiscoverProjectTools } = await import("./project-tool-discovery.js");
  assert.equal(shouldDiscoverProjectTools({ target: "da1e42fad0da" }, {}), true);
});

test("shouldDiscoverProjectTools returns true when --instance flag is provided", async () => {
  const { shouldDiscoverProjectTools } = await import("./project-tool-discovery.js");
  assert.equal(shouldDiscoverProjectTools({ instance: "abc123" }, {}), true);
});

test("shouldDiscoverProjectTools returns true when --runtime flag is provided", async () => {
  // --runtime implies the user wants runtime tools, which must be discovered from the bridge
  const { shouldDiscoverProjectTools } = await import("./project-tool-discovery.js");
  assert.equal(shouldDiscoverProjectTools({ runtime: true }, {}), true);
});

test("shouldDiscoverProjectTools returns false when no bridge indicators are present", async () => {
  const { shouldDiscoverProjectTools } = await import("./project-tool-discovery.js");
  assert.equal(shouldDiscoverProjectTools({}, {}), false);
});

test("tools run discovers runtime tools when --target flag is provided without --bridge-endpoint", async () => {
  // Regression for ALP-467: user passes --target without --bridge-endpoint
  // and expects runtime tool discovery to work.
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      if (payload.command === "runtime-tool.list") {
        return { ok: true, data: { tools: [{ name: "runtime.wait", title: "Runtime Wait", description: "Wait in runtime", group: "runtime", target: "runtime", handler: "bridge-runtime" }] } };
      }
      return { ok: true, data: { result: { requestId: "req", isPlaying: true } } };
    }
  };

  const result = await handleToolsCommand("run", ["runtime.wait"], { target: "da1e42fad0da", runtime: true, yes: true }, runtime, requestId);
  assert.equal(result.ok, true, `Expected ok=true, got error: ${result.error?.code} ${result.error?.message}`);
  assert.equal(requests[0].command, "runtime-tool.list");
  assert.equal(requests[1].command, "runtime-tool.run");
});

test("tools run discovers runtime tools when --runtime flag is provided", async () => {
  // --runtime flag should trigger custom tool discovery even without --include-custom
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      if (payload.command === "runtime-tool.list") {
        return { ok: true, data: { tools: [{ name: "sample.runtime-echo", title: "Sample Runtime Echo", description: "Echoes input from runtime", group: "sample", target: "runtime", handler: "bridge-runtime" }] } };
      }
      return { ok: true, data: { result: { requestId: "req", isPlaying: true } } };
    }
  };

  const result = await handleToolsCommand("run", ["sample.runtime-echo"], { runtime: true, "bridge-endpoint": "http://127.0.0.1:47392", yes: true }, runtime, requestId);
  assert.equal(result.ok, true, `Expected ok=true, got error: ${result.error?.code} ${result.error?.message}`);
  assert.equal(requests[0].command, "runtime-tool.list");
  assert.equal(requests[1].command, "runtime-tool.run");
});
