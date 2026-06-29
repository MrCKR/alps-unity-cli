/**
 * Unit tests for tools group/search/describe/run CLI commands.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { bridgeCommandRegistry, type BridgeRequest } from "../src/bridge/index.js";
import { handleToolsCommand, type ToolsRuntime } from "../src/tools.js";
import { builtinToolMetadata, builtinToolGroups, findToolByName, filterToolsByGroup, searchTools, allRegisteredToolMetadata, extensionToolMetadata, allRegisteredToolGroups, EXTENSION_GROUPS, supportStatusForTool, normalizeToolMetadata } from "../src/tools/metadata/index.js";
import "../src/extensions/index.js";

const requestId = "test_req_tools_001";
const emptyRuntime: ToolsRuntime = {};

function makeTempDir(prefix: string): string {
  const dir = join(tmpdir(), `${prefix}-${Date.now()}-${Math.random().toString(16).slice(2)}`);
  mkdirSync(dir, { recursive: true });
  return dir;
}

function makeUnityProject(root: string, packages: Record<string, string> = {}): string {
  const project = join(root, "Project");
  mkdirSync(join(project, "Packages"), { recursive: true });
  writeFileSync(join(project, "Packages", "manifest.json"), JSON.stringify({ dependencies: packages }, null, 2), "utf8");
  writeFileSync(join(project, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: Object.fromEntries(Object.entries(packages).map(([name, version]) => [name, { version }])) }, null, 2), "utf8");
  return project;
}

function makeFakeUnity(root: string, body: string): string {
  if (process.platform === "win32") {
    const script = join(root, "UnityFake.js");
    const path = join(root, "Unity.cmd");
    writeFileSync(script, body, "utf8");
    writeFileSync(path, `@echo off\r\n"${process.execPath}" "%~dp0UnityFake.js" %*\r\nif errorlevel 1 exit /b %errorlevel%\r\n`, "utf8");
    return path;
  }
  const path = join(root, "Unity.exe");
  writeFileSync(path, `#!/usr/bin/env node\n${body}\n`, { mode: 0o755 });
  return path;
}

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
    assert.ok(tool.summary, `missing summary in ${tool.name}`);
    assert.ok(tool.description, `missing description in ${tool.name}`);
    assert.ok(tool.group, `missing group in ${tool.name}`);
    assert.ok(tool.inputSchema, `missing inputSchema in ${tool.name}`);
    assert.ok(typeof tool.destructiveHint === "boolean", `missing destructiveHint in ${tool.name}`);
    assert.ok(["supported", "runtime-only", "session-affecting", "editor-only", "unknown"].includes(tool.playModeSupport ?? ""), `missing playModeSupport in ${tool.name}`);
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
    assert.equal(tool.cliCommand, `tools run ${tool.name}`);
    const spec = bridgeCommandRegistry.specs().find((candidate) => candidate.bridgeCommand === tool.name || candidate.toolName === tool.name);
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
  assert.equal(tool.destructiveHint, false);
});

test("scene.save is marked destructive and live-bridge", () => {
  const tool = findToolByName("scene.save");
  assert.ok(tool, "scene.save should exist");
  assert.equal(tool.destructiveHint, true);
  assert.equal(tool.supportStatus, "live-bridge");
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

// ── tools group ────────────────────────────────────────────

test("tools group returns all tool groups", async () => {
  const result = await handleToolsCommand("group", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; groups: Array<{ name: string; toolCount: number }> };
  assert.ok(data.count > 0);
  assert.ok(data.groups.length > 0);
  const groupNames = data.groups.map((g) => g.name);
  assert.ok(groupNames.includes("scene"), "scene group should exist");
  assert.ok(groupNames.includes("camera"), "camera group should exist");
});

test("tools group list <group> filters by group", async () => {
  const result = await handleToolsCommand("group", ["list", "scene"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: Array<{ name: string; source: string }> };
  assert.ok(data.count > 0);
  for (const t of data.tools) {
    assert.equal(t.source, "built-in");
  }
});

test("tools group list <unknown> returns error", async () => {
  const result = await handleToolsCommand("group", ["list", "nonexistent"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
});

test("tools group list includes tools with short summaries", async () => {
  const result = await handleToolsCommand("group", ["list", "scene"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: Array<{ name: string; summary: string }> };
  assert.ok(data.count > 0);
  assert.ok(data.tools.some((t) => t.name === "scene.hierarchy"), "scene.hierarchy should be in scene group");
});

test("tools group list includes custom project tools when available", async () => {
  const customTool = {
    name: "custom.my-tool",
    summary: "My Custom Tool",
    description: "A custom tool for testing",
    group: "custom",
    inputSchema: { type: "object" as const, properties: {} },
    destructiveHint: false,
    playModeSupport: "editor-only" as const
  };
  const runtime: ToolsRuntime = { projectTools: [customTool] };
  const result = await handleToolsCommand("group", ["list", "custom"], { "include-custom": true }, runtime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { tools: Array<{ name: string; playModeSupport?: string }> };
  const names = data.tools.map((t) => t.name);
  assert.ok(names.includes("custom.my-tool"));
  assert.equal(data.tools.find((t) => t.name === "custom.my-tool")?.playModeSupport, "editor-only");
});

// ── tools group summary ────────────────────────────────────

test("tools group returns group names with counts", async () => {
  const result = await handleToolsCommand("group", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; groups: Array<{ name: string; toolCount: number }> };
  assert.ok(data.count > 0);
  const groupNames = data.groups.map((g) => g.name);
  assert.ok(groupNames.includes("scene"));
  assert.ok(groupNames.includes("camera"));
});

test("tools group keeps built-in groups without custom context", async () => {
  const result = await handleToolsCommand("group", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { groups: Array<{ name: string }> };
  const groupNames = data.groups.map((g) => g.name);
  assert.ok(groupNames.includes("scene"));
  assert.equal(groupNames.includes("project"), true);
});

test("tools group includes runtime custom tool groups", async () => {
  const runtime: ToolsRuntime = { projectTools: [{
    name: "project.bake",
    summary: "Project Bake",
    description: "Bakes project data.",
    group: "project",
    inputSchema: { type: "object" },
    destructiveHint: false,
    playModeSupport: "editor-only"
  }] };
  const result = await handleToolsCommand("group", [], {}, runtime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { groups: Array<{ name: string }> };
  const groupNames = data.groups.map((g) => g.name);
  assert.ok(groupNames.includes("project"));
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
  assert.equal(data.destructiveHint, false);
  assert.equal(data.playModeSupport, "supported");
  assert.ok(data.inputSchema);
  assert.ok(data.description);
  assert.ok(Array.isArray(data.examples));
  assert.equal("mode" in data, false);
  assert.equal("requiresUnity" in data, false);
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
  const result = await handleToolsCommand("describe", ["asset.delete"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.destructiveHint, true);
  assert.equal(typeof data.playModeSupport, "string");
});

// ── S2.3 metadata: editor, package, test, build ────────────

test("editor.refresh metadata exists with job handler and longRunning", () => {
  const tool = findToolByName("editor.refresh");
  assert.ok(tool, "editor.refresh should exist");
  assert.equal(tool.group, "editor");
  assert.equal(tool.handler, "job");
  assert.equal(tool.longRunning, true);
  assert.equal(tool.destructiveHint, false);
  assert.ok(tool.examples && tool.examples.length > 0);
});

test("editor.compile-diagnostics metadata documents the live compile loop", () => {
  const tool = findToolByName("editor.compile-diagnostics");
  assert.ok(tool, "editor.compile-diagnostics should exist");
  assert.equal(tool.group, "editor");
  assert.equal(tool.handler, "hybrid");
  assert.equal(tool.supportStatus, "live-bridge");
  assert.equal(tool.longRunning, true);
  assert.equal(tool.supportsDryRun, true);
  assert.equal(tool.destructiveHint, false);
  assert.equal(tool.playModeSupport, "session-affecting");
  assert.deepEqual(tool.sideEffects, ["asset-refresh", "script-compile", "domain-reload", "console-read"]);
  assert.match(tool.description, /asset\.refresh/);
  assert.match(tool.description, /console\.get/);
  assert.match(JSON.stringify(tool.inputSchema), /waitTimeoutMs/);
  assert.match(JSON.stringify(tool.outputSchema), /diagnostics/);
});

test("batch control-plane tools are not exposed through tools metadata", () => {
  assert.equal(findToolByName("batch.schema"), undefined, "batch.schema should be top-level batch control-plane only");
  assert.equal(findToolByName("batch.run"), undefined, "batch.run should be top-level batch control-plane only");
});

test("representative destructive tools declare dry-run support", () => {
  for (const name of ["asset.delete", "scene.save", "code.exec"]) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool.destructiveHint, true);
    assert.equal(tool.supportsDryRun, true);
  }
});

test("code.exec exposes critical execution risk metadata", () => {
  const tool = findToolByName("code.exec")!;
  assert.equal(tool.riskLevel, "critical");
  assert.equal(tool.playModeSupport, "session-affecting");
  assert.equal(tool.supportStatus, "bridge-job");
  assert.deepEqual(tool.permissions, ["execute-user-code", "write-artifacts", "unity-editor-api"]);
  assert.ok(tool.sideEffects?.includes("user-code-execution"));
  assert.ok(tool.outputSchema);
  assert.ok(tool.examples?.some((example) => example.argv.includes("--dry-run")));
  assert.ok(tool.examples?.some((example) => example.argv.includes("--yes")));
});

test("go tools expose search paging and scene mutation metadata", () => {
  const find = findToolByName("go.find")!;
  assert.equal(find.playModeSupport, "supported");
  assert.equal(find.equivalentUnityMcp, "find_gameobjects / gameobject-find");
  const findProps = find.inputSchema.properties as Record<string, unknown>;
  assert.ok(findProps.searchMethod, "go.find should expose searchMethod");
  assert.ok(findProps.includeInactive, "go.find should expose includeInactive");
  assert.ok(findProps.pageSize, "go.find should expose pageSize");
  assert.ok(findProps.cursor, "go.find should expose cursor");
  assert.ok(find.examples?.some((example) => example.argv.includes("--input")), "go.find should show --input usage");

  const inspect = findToolByName("go.inspect")!;
  assert.equal(inspect.playModeSupport, "supported");
  assert.ok(inspect.outputSchema, "go.inspect should describe output shape");

  for (const name of ["go.create", "go.modify", "go.duplicate", "go.move-relative", "go.look-at", "go.delete", "go.set-parent"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} mutates the current scene/session`);
    assert.ok(tool.sideEffects?.includes("scene-modify"), `${name} should expose scene side effects`);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  const create = findToolByName("go.create")!;
  assert.match(create.description, /returned hierarchy path or instance id/);
  assert.match(JSON.stringify(create.inputSchema), /returned path\/id/);

  const modify = findToolByName("go.modify")!;
  const modifyProps = modify.inputSchema.properties as Record<string, unknown>;
  assert.ok(modifyProps.targets, "go.modify should expose targets[] for batch edits");
  assert.equal(Array.isArray(modify.inputSchema.required), false, "go.modify should allow either target or targets[] instead of requiring target only");
  assert.match(modify.description, /all-or-none/, "go.modify should document batch all-or-none behavior");

  const deleteTool = findToolByName("go.delete")!;
  assert.match(deleteTool.description, /Prefer hierarchy paths or instance ids/);
  assert.ok(deleteTool.examples?.some((example) => example.argv.join(" ").includes("TempRoot/GeneratedChild")), "go.delete should show hierarchy path deletion");
  assert.ok(deleteTool.examples?.some((example) => example.argv.join(" ").includes("\"target\":\"12345\"")), "go.delete should show instance id deletion");

  const setParent = findToolByName("go.set-parent")!;
  assert.match(setParent.description, /Prefer hierarchy paths or instance ids/);
  assert.ok(setParent.examples?.every((example) => !example.argv.join(" ").includes("\"target\":\"Sword\"")), "go.set-parent examples should avoid ambiguous plain names");
});

test("asset CRUD tools expose Play Mode side effects and examples", () => {
  const readOnly = new Map([
    ["asset.info", "manage_asset.get_info"],
    ["asset.search-built-in", "assets-find-built-in"]
  ]);
  for (const [name, equivalent] of readOnly) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "supported");
    assert.equal(tool.destructiveHint, false);
    assert.equal(tool.equivalentUnityMcp, equivalent);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  const mutating = new Map<string, string[]>([
    ["asset.create", ["asset-write", "asset-import"]],
    ["asset.import", ["asset-write", "asset-import"]],
    ["asset.modify", ["asset-write", "asset-import"]],
    ["asset.duplicate", ["asset-write", "asset-import"]],
    ["asset.move", ["asset-move", "asset-import"]],
    ["asset.rename", ["asset-move", "asset-import"]]
  ]);
  for (const [name, sideEffects] of mutating) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} imports or writes project assets`);
    assert.equal(tool.destructiveHint, false);
    assert.deepEqual(tool.sideEffects, sideEffects);
    assert.ok(tool.equivalentUnityMcp?.startsWith("manage_asset."), `${name} should link to manage_asset`);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  const deletion = findToolByName("asset.delete")!;
  assert.equal(deletion.playModeSupport, "session-affecting");
  assert.equal(deletion.destructiveHint, true);
  assert.equal(deletion.supportsDryRun, true);
  assert.deepEqual(deletion.sideEffects, ["asset-delete", "asset-import"]);
  assert.ok(deletion.examples?.some((example) => example.argv.includes("--dry-run")));
  assert.ok(deletion.examples?.some((example) => example.argv.includes("--yes")));
});

test("package.resolve metadata exists with hybrid handler", () => {
  const tool = findToolByName("package.resolve");
  assert.ok(tool, "package.resolve should exist");
  assert.equal(tool.group, "package");
  assert.equal(tool.handler, "hybrid");
  assert.equal(supportStatusForTool(tool), "bridge-job");
  assert.equal(tool.longRunning, true);
});

test("package.registry metadata exists with offline handler", () => {
  const tool = findToolByName("package.registry");
  assert.ok(tool, "package.registry should exist");
  assert.equal(tool.group, "package");
  assert.equal(tool.handler, "offline");
});

test("package.scoped-registry metadata exists with offline handler", () => {
  const tool = findToolByName("package.scoped-registry");
  assert.ok(tool, "package.scoped-registry should exist");
  assert.equal(tool.group, "package");
  assert.equal(tool.handler, "offline");
});

test("test.run metadata exists with job handler and longRunning", () => {
  const tool = findToolByName("test.run");
  assert.ok(tool, "test.run should exist");
  assert.equal(tool.group, "test");
  assert.equal(tool.handler, "job");
  assert.equal(tool.longRunning, true);
  assert.equal(tool.destructiveHint, false);
  assert.ok(tool.examples && tool.examples.length > 0);
});

test("build.run metadata exists with job handler and longRunning", () => {
  const tool = findToolByName("build.run");
  assert.ok(tool, "build.run should exist");
  assert.equal(tool.group, "build");
  assert.equal(tool.handler, "job");
  assert.equal(tool.longRunning, true);
  assert.equal(tool.destructiveHint, false);
  assert.equal(tool.playModeSupport, "editor-only");
  assert.deepEqual(tool.sideEffects, ["build-output-write", "asset-import", "compile"]);
  assert.equal(tool.equivalentUnityMcp, "manage_build.build");
  assert.ok(tool.examples?.some((example) => example.argv.includes("--input")));
});

test("build bridge tools expose Play Mode and dry-run semantics", () => {
  const scenes = findToolByName("build.scenes")!;
  assert.equal(scenes.playModeSupport, "session-affecting");
  assert.equal(scenes.supportsDryRun, true);
  assert.deepEqual(scenes.sideEffects, ["build-settings-modify"]);
  assert.ok(scenes.examples?.some((example) => example.argv.includes("--dry-run")));

  const platform = findToolByName("build.platform")!;
  assert.equal(platform.playModeSupport, "session-affecting");
  assert.equal(platform.supportsDryRun, true);
  assert.deepEqual(platform.sideEffects, ["build-target-switch", "asset-import", "domain-reload"]);
  assert.ok(platform.examples?.some((example) => example.argv.includes("--dry-run")));

  const profiles = findToolByName("build.profiles")!;
  assert.equal(profiles.playModeSupport, "session-affecting");
  assert.equal(profiles.supportsDryRun, true);
  assert.equal(profiles.equivalentUnityMcp, "manage_build.profiles");
  assert.ok(profiles.examples?.some((example) => example.argv.includes("--dry-run")));

  const settings = findToolByName("build.settings")!;
  assert.equal(settings.playModeSupport, "supported");
  assert.equal(settings.destructiveHint, false);
  assert.equal(settings.equivalentUnityMcp, "manage_build.settings");
  assert.ok(settings.examples?.some((example) => example.argv.includes("--input")));
});

test("generic job control-plane tools are not exposed through tools metadata", () => {
  for (const name of ["job.status", "job.logs", "job.describe", "job.cancel", "test.poll", "test.logs", "test.cancel", "build.logs"]) {
    assert.equal(findToolByName(name), undefined, `${name} should be top-level jobs control-plane only`);
  }
});

test("build.status and build.cancel expose build-domain job controls", () => {
  const status = findToolByName("build.status")!;
  const cancel = findToolByName("build.cancel")!;
  assert.equal(status.handler, "offline");
  assert.equal(status.destructiveHint, false);
  assert.equal(cancel.handler, "offline");
  assert.equal(cancel.destructiveHint, false);
  assert.deepEqual(cancel.sideEffects, ["job-cancel"]);
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
  assert.ok(names.includes("editor.compile-diagnostics"));
  assert.ok(names.includes("editor.launch-dialog.dismiss"));
});

test("S2.3 test group exposes only test execution tool", () => {
  const testTools = filterToolsByGroup("test");
  assert.deepEqual(testTools.map((t) => t.name), ["test.run"]);
});

test("S2.3 build group exposes run plus Editor build settings tools", () => {
  const names = filterToolsByGroup("build").map((t) => t.name);
  assert.deepEqual(names, ["build.scenes", "build.platform", "build.profiles", "build.settings", "build.run", "build.status", "build.cancel"]);
});

test("S3.1 creation domains have frozen metadata coverage", () => {
  const expectedByGroup = new Map([
    ["camera", ["camera.list", "camera.info", "camera.screenshot", "camera.create", "camera.target", "camera.lens", "camera.multiview"]],
    ["material", ["material.info", "material.create", "material.assign", "material.set", "material.set-renderer-color"]],
    ["shader", ["shader.info", "shader.read", "shader.create", "shader.update", "shader.delete", "shader.list", "shader.get-data"]],
    ["texture", ["texture.info", "texture.create", "texture.sprite", "texture.modify", "texture.delete", "texture.import-settings", "texture.generate"]],
    ["ui", ["ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.info", "ui.document.create", "ui.document.update", "ui.document.delete", "ui.hierarchy", "ui.attach-document", "ui.detach-document", "ui.create-panel-settings", "ui.update-panel-settings", "ui.get-visual-tree", "ui.modify-visual-element", "ui.link-stylesheet", "ui.list-documents", "ui.render-preview"]]
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
  const writeTools = ["camera.screenshot", "material.create", "material.assign", "material.set", "material.set-renderer-color", "shader.create", "shader.update", "texture.create", "texture.sprite", "texture.import-settings", "ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.create"];

  for (const name of writeTools) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool.destructiveHint, false, `${name} should not be marked destructive`);
  }
});

test("camera tools expose Play Mode semantics, examples and output contracts", () => {
  const readOnly = ["camera.list", "camera.info"];
  for (const name of readOnly) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "supported", `${name} is read-only scene inspection`);
    assert.equal(tool.destructiveHint, false);
    assert.ok(tool.outputSchema, `${name} should describe returned camera data`);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  const sessionTools = new Map([
    ["camera.create", ["scene-modify", "component-add"]],
    ["camera.target", ["scene-modify"]],
    ["camera.lens", ["scene-modify"]],
    ["camera.screenshot", ["artifact-write", "render-texture"]],
    ["camera.multiview", ["artifact-write", "render-texture"]]
  ]);
  for (const [name, sideEffects] of sessionTools) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} changes scene/session state or writes artifacts`);
    assert.deepEqual(tool.sideEffects, sideEffects);
    assert.ok(tool.equivalentUnityMcp, `${name} should link to reference behavior`);
    assert.ok(tool.outputSchema, `${name} should describe returned data`);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  const lensProps = findToolByName("camera.lens")!.inputSchema.properties as Record<string, { enum?: string[] }>;
  assert.deepEqual(lensProps.clearFlags.enum, ["Skybox", "SolidColor", "Depth", "Nothing"]);
});

test("editor selection tools expose UI session metadata", () => {
  const selection = findToolByName("editor.selection.set")!;
  assert.equal(selection.playModeSupport, "session-affecting");
  assert.equal(selection.destructiveHint, false);
  assert.equal(selection.supportsDryRun, true);
  assert.deepEqual(selection.sideEffects, ["editor-selection"]);
  assert.ok(selection.outputSchema);
  assert.ok(selection.examples?.some((example) => example.argv.includes("--dry-run")));

  const activeTool = findToolByName("editor.active-tool.set")!;
  assert.equal(activeTool.playModeSupport, "session-affecting");
  assert.equal(activeTool.destructiveHint, false);
  assert.deepEqual(activeTool.sideEffects, ["editor-active-tool"]);
  assert.ok(activeTool.outputSchema);
  assert.match(activeTool.description, /not unity_cli tool activation/);
});

test("editor operation tools expose side-effect and recovery metadata", () => {
  const undoRedo = new Map([
    ["editor.undo", ["undo-stack", "scene-or-asset-modify"]],
    ["editor.redo", ["undo-stack", "scene-or-asset-modify"]]
  ]);
  for (const [name, sideEffects] of undoRedo) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting");
    assert.deepEqual(tool.sideEffects, sideEffects);
    assert.ok(tool.outputSchema);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")));
  }

  const resolution = findToolByName("editor.resolution")!;
  assert.equal(resolution.playModeSupport, "session-affecting");
  assert.deepEqual(resolution.sideEffects, ["player-settings", "project-settings-write"]);
  assert.ok(resolution.outputSchema);

  const menu = findToolByName("editor.menu")!;
  assert.equal(menu.destructiveHint, true);
  assert.equal(menu.playModeSupport, "session-affecting");
  assert.deepEqual(menu.sideEffects, ["editor-menu", "unknown-editor-side-effect"]);
  assert.equal(menu.equivalentUnityMcp, "execute_menu_item");
  assert.ok(menu.examples?.some((example) => example.argv.includes("--yes")));

  const refresh = findToolByName("editor.refresh")!;
  assert.equal(refresh.handler, "job");
  assert.equal(refresh.supportStatus, "batch-job");
  assert.equal(refresh.playModeSupport, "editor-only");
  assert.deepEqual(refresh.sideEffects, ["asset-refresh", "script-compile", "domain-reload"]);
  assert.equal(refresh.equivalentUnityMcp, "refresh_unity");
  assert.ok(refresh.longRunning);

  const compileDiagnostics = findToolByName("editor.compile-diagnostics")!;
  assert.equal(compileDiagnostics.handler, "hybrid");
  assert.equal(compileDiagnostics.supportStatus, "live-bridge");
  assert.equal(compileDiagnostics.destructiveHint, false);
  assert.equal(compileDiagnostics.supportsDryRun, true);
  assert.deepEqual(compileDiagnostics.sideEffects, ["asset-refresh", "script-compile", "domain-reload", "console-read"]);
  assert.match(compileDiagnostics.description, /single call|one call|in one call/);

  const launchDialog = findToolByName("editor.launch-dialog.dismiss")!;
  assert.equal(launchDialog.handler, "offline");
  assert.equal(launchDialog.supportStatus, "cli-only");
  assert.equal(launchDialog.playModeSupport, "editor-only");
  assert.equal(launchDialog.destructiveHint, false);
  assert.equal(launchDialog.supportsDryRun, true);
  assert.deepEqual(launchDialog.sideEffects, ["desktop-dialog-click"]);
  assert.match(launchDialog.description, /Safe Mode/);
  assert.match(launchDialog.description, /processId/);
  assert.ok(launchDialog.examples?.some((example) => example.argv.includes("--dry-run")));
});

test("editor state and play controls expose Play Mode transition metadata", () => {
  const state = findToolByName("editor.state")!;
  assert.equal(state.playModeSupport, "supported");
  assert.equal(state.supportStatus, "live-bridge");
  assert.ok(state.outputSchema);
  assert.ok(state.examples?.some((example) => example.argv.includes("--input")));
  assert.equal(state.equivalentUnityMcp, "editor-application-get-state / editor_state");

  const consoleAlias = findToolByName("editor.console")!;
  assert.equal(consoleAlias.playModeSupport, "supported");
  assert.equal(consoleAlias.destructiveHint, false);
  assert.ok(consoleAlias.outputSchema);
  assert.match(consoleAlias.description, /read-only/);
  assert.ok(!("clear" in (consoleAlias.inputSchema.properties as Record<string, unknown>)), "editor.console must not expose destructive clear input");

  const sessionTools = new Map([
    ["editor.play", ["playmode-transition", "domain-reload"]],
    ["editor.pause", ["playmode-pause"]],
    ["editor.stop", ["playmode-transition", "domain-reload"]]
  ]);
  for (const [name, sideEffects] of sessionTools) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} changes editor session state`);
    assert.deepEqual(tool.sideEffects, sideEffects);
    assert.equal(tool.supportStatus, "live-bridge");
    assert.ok(tool.outputSchema, `${name} should describe transition output`);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }
});

test("docs and diagnose tools expose offline/live usability metadata", () => {
  for (const name of ["docs.list", "docs.topic", "docs.get", "docs.search", "diagnose.capabilities"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "supported", `${name} is safe in or out of Play Mode`);
    assert.equal(tool.destructiveHint, false);
    assert.ok(tool.outputSchema, `${name} should describe returned data`);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  const preflight = findToolByName("diagnose.preflight")!;
  assert.equal(preflight.playModeSupport, "supported");
  assert.equal(preflight.handler, "bridge");
  assert.equal(preflight.supportStatus, "live-bridge");
  assert.ok(preflight.outputSchema, "diagnose.preflight should describe live diagnostics output");
  assert.ok(preflight.examples?.some((example) => example.argv.includes("--input")));

  const docsSearchProps = findToolByName("docs.search")!.inputSchema.properties as Record<string, { description?: string; default?: unknown }>;
  assert.equal(docsSearchProps.pageSize.default, 20);
  assert.equal(docsSearchProps.cursor.default, "0");
  assert.match(docsSearchProps.docsDir.description ?? "", /offline Unity docs cache/);

  const docsGet = findToolByName("docs.get")!;
  assert.match(docsGet.description, /offline docs cache/);
  assert.equal(docsGet.equivalentUnityMcp, "unity_docs.get_doc");

  const docsTopicProps = findToolByName("docs.topic")!.inputSchema.properties as Record<string, { description?: string }>;
  assert.match(docsTopicProps.topic.description ?? "", /docs\.list/);
});

test("tools describe test.run returns full metadata", async () => {
  const result = await handleToolsCommand("describe", ["test.run"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "test.run");
  assert.equal(data.group, "test");
  assert.equal("handler" in data, false);
  assert.equal("longRunning" in data, false);
});

test("tools describe build.run returns full metadata", async () => {
  const result = await handleToolsCommand("describe", ["build.run"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "build.run");
  assert.equal(data.group, "build");
  assert.equal("handler" in data, false);
  assert.equal("longRunning" in data, false);
});

test("tools describe editor.refresh returns job metadata", async () => {
  const result = await handleToolsCommand("describe", ["editor.refresh"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "editor.refresh");
  assert.equal("handler" in data, false);
  assert.equal("longRunning" in data, false);
});

test("tools describe editor.compile-diagnostics returns live bridge metadata", async () => {
  const result = await handleToolsCommand("describe", ["editor.compile-diagnostics"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "editor.compile-diagnostics");
  assert.equal(data.supportStatus, "live-bridge");
  assert.equal(data.supportsDryRun, true);
  assert.equal("handler" in data, false);
  assert.equal("longRunning" in data, false);
  assert.match(JSON.stringify(data.inputSchema), /waitTimeoutMs/);
  assert.match(JSON.stringify(data.outputSchema), /diagnostics/);
});

test("tools describe editor.launch-dialog.dismiss returns CLI-only metadata", async () => {
  const result = await handleToolsCommand("describe", ["editor.launch-dialog.dismiss"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "editor.launch-dialog.dismiss");
  assert.equal(data.supportStatus, "cli-only");
  assert.equal(data.supportsDryRun, true);
  assert.equal("handler" in data, false);
  assert.match(JSON.stringify(data.inputSchema), /processId/);
});

// ── S3.2 metadata: graphics, physics, animation, vfx ───────

test("S3.2 graphics group has quality, pipeline, volume and screenshot tools", () => {
  const names = filterToolsByGroup("graphics").map((t) => t.name);
  assert.deepEqual(names, ["graphics.quality", "graphics.settings", "graphics.pipeline", "graphics.volume", "graphics.screenshot", "graphics.bake-start", "graphics.bake-cancel", "graphics.bake-status", "graphics.bake-clear", "graphics.bake-reflection-probe", "graphics.bake-settings", "graphics.light-probe-group", "graphics.reflection-probe", "graphics.probe-positions", "graphics.stats", "graphics.stats-counters", "graphics.stats-memory", "graphics.scene-debug", "graphics.skybox-get", "graphics.skybox-set-material", "graphics.skybox-set-properties", "graphics.skybox-set-ambient", "graphics.skybox-set-fog", "graphics.skybox-set-reflection", "graphics.skybox-set-sun", "graphics.pipeline-settings", "graphics.pipeline-quality", "graphics.pipeline-info"]);
});

test("S3.4 profiler domain exposes expected tools and safety hints", () => {
  const profilerTools = filterToolsByGroup("profiler");
  const expectedNames = ["profiler.status", "profiler.start", "profiler.stop", "profiler.pause", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.capture", "profiler.export", "profiler.modules", "profiler.module-enable", "profiler.clear", "profiler.load", "profiler.areas", "profiler.object-memory", "profiler.rendering-stats", "profiler.script-stats", "profiler.deep", "profiler.hierarchy", "profiler.memory-snapshot.take", "profiler.memory-snapshot.list", "profiler.memory-snapshot.compare", "profiler.frame-debugger.enable", "profiler.frame-debugger.disable", "profiler.frame-debugger.events"];
  assert.deepEqual(profilerTools.map((tool) => tool.name), expectedNames);

  for (const name of expectedNames) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool?.docsTopic, "profiler");
    assert.ok(tool?.cliCommand, `${name} should expose a CLI command`);
  }

  for (const name of ["profiler.status", "profiler.counter", "profiler.memory", "profiler.frame"]) {
    const tool = findToolByName(name);
    assert.equal(tool?.destructiveHint, false, `${name} should not be destructive`);
  }

  for (const name of ["profiler.start", "profiler.stop", "profiler.pause", "profiler.capture", "profiler.export"]) {
    const tool = findToolByName(name);
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
  assert.equal(data.destructiveHint, false);
});

test("core bridge contract tools expose live-bridge support status", () => {
  const liveBridgeNames = ["scene.active", "scene.build-settings", "scene.create", "scene.load", "scene.save", "go.inspect", "go.create", "go.modify", "go.delete", "component.add", "component.remove", "component.set"];
  for (const name of liveBridgeNames) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool.handler, "bridge", `${name} keeps bridge handler contract classification`);
    assert.equal(supportStatusForTool(tool), "live-bridge", `${name} should be user-facing live-bridge`);
  }
});

test("hybrid tools expose bridge plus job support status", () => {
  const hybridNames = ["package.install", "package.remove", "package.resolve", "code.exec", "profiler.start", "profiler.capture", "probuilder.create-shape", "probuilder.create-poly-shape", "probuilder.extrude-faces", "probuilder.delete-faces", "probuilder.mesh-info"];
  for (const name of hybridNames) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool.handler, "hybrid", `${name} should keep hybrid handler classification`);
    assert.equal(supportStatusForTool(tool), "bridge-job", `${name} should be user-facing bridge plus job`);
  }
});

test("S3.2 physics group has settings, queries and mutation tools", () => {
  const names = filterToolsByGroup("physics").map((t) => t.name);
  assert.deepEqual(names, ["physics.settings", "physics.collision-matrix", "physics.material", "physics.assign-material", "physics.joint", "physics.force", "physics.overlap", "physics.rigidbody", "physics.raycast", "physics.simulation", "physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.validate"]);
  assert.equal(findToolByName("physics.simulation")?.handler, "bridge");
});

test("S3.2 animation and vfx groups have required tools", () => {
  assert.deepEqual(filterToolsByGroup("animation").map((t) => t.name), ["animation.clip", "animation.clip.create", "animation.clip.modify", "animation.state", "animation.play", "animation.stop"]);
  assert.deepEqual(filterToolsByGroup("vfx").map((t) => t.name), ["vfx.graph", "vfx.effect", "vfx.parameter", "vfx.control", "vfx.event", "vfx.template", "vfx.particle-create", "vfx.particle-info", "vfx.particle-main", "vfx.particle-emission", "vfx.particle-shape", "vfx.particle-play", "vfx.particle-module", "vfx.line-create", "vfx.line-positions", "vfx.trail"]);
});

test("animation tools expose Play Mode side-effect semantics", () => {
  const clip = findToolByName("animation.clip")!;
  const clipProps = clip.inputSchema.properties as Record<string, unknown>;
  const clipOutput = clip.outputSchema?.properties as Record<string, unknown>;
  assert.equal(clip.playModeSupport, "supported");
  assert.equal(clip.destructiveHint, false);
  assert.ok(clipProps.maxCurveKeys, "animation.clip should expose bounded curve key reads");
  assert.ok(clipOutput.clip, "animation.clip should document clip output");
  assert.equal(clip.equivalentUnityMcp, "manage_animation.clip_get_info");

  const createClip = findToolByName("animation.clip.create")!;
  const createProps = createClip.inputSchema.properties as Record<string, unknown>;
  assert.equal(createClip.playModeSupport, "session-affecting");
  assert.equal(createClip.destructiveHint, true);
  assert.equal(createClip.supportsDryRun, true);
  assert.deepEqual(createClip.sideEffects, ["asset-write", "asset-import"]);
  assert.ok(createProps.wrapMode, "animation.clip.create should expose wrapMode");
  assert.ok(createProps.loopTime, "animation.clip.create should expose loopTime");
  assert.ok(createProps.length, "animation.clip.create should expose length");
  assert.equal(createClip.equivalentUnityMcp, "manage_animation.clip_create");

  const modifyClip = findToolByName("animation.clip.modify")!;
  const modifyProps = modifyClip.inputSchema.properties as Record<string, unknown>;
  assert.equal(modifyClip.playModeSupport, "session-affecting");
  assert.equal(modifyClip.destructiveHint, true);
  assert.equal(modifyClip.supportsDryRun, true);
  assert.deepEqual(modifyClip.sideEffects, ["asset-write", "asset-import"]);
  assert.ok(modifyProps.wrapMode, "animation.clip.modify should expose wrapMode");
  assert.ok(modifyProps.loopTime, "animation.clip.modify should expose loopTime");
  assert.equal(modifyClip.equivalentUnityMcp, "manage_animation.clip_set_properties");

  const state = findToolByName("animation.state")!;
  assert.equal(state.playModeSupport, "supported");
  assert.equal(state.equivalentUnityMcp, "manage_animation.animator_get_info");

  for (const name of ["animation.play", "animation.stop"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} changes Animator session state`);
    assert.deepEqual(tool.sideEffects, ["animation-state", "scene-modify"]);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }
});

test("animator parameter tools expose read/write Play Mode semantics", () => {
  const getInfo = findToolByName("animator.get-info")!;
  assert.equal(getInfo.playModeSupport, "supported");
  assert.equal(getInfo.equivalentUnityMcp, "manage_animation.animator_get_info");

  const getParameter = findToolByName("animator.get-parameter")!;
  assert.equal(getParameter.playModeSupport, "supported");
  assert.equal(getParameter.equivalentUnityMcp, "manage_animation.animator_get_parameter");

  const setParameter = findToolByName("animator.set-parameter")!;
  assert.equal(setParameter.playModeSupport, "session-affecting");
  assert.deepEqual(setParameter.sideEffects, ["animator-controller-modify", "asset-write"]);
  assert.equal(setParameter.equivalentUnityMcp, "manage_animation.animator_set_parameter");
  const props = setParameter.inputSchema.properties as Record<string, { enum?: string[] }>;
  assert.deepEqual(props.parameterType.enum, ["float", "int", "bool", "trigger"]);

  for (const name of ["animator.get-info", "animator.get-parameter", "animator.set-parameter"]) {
    const tool = findToolByName(name)!;
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }
});

test("animator session tools expose state-changing Play Mode semantics", () => {
  const expected = new Map([
    ["animator.crossfade", "manage_animation.animator_crossfade"],
    ["animator.set-speed", "manage_animation.animator_set_speed"],
    ["animator.set-enabled", "manage_animation.animator_set_enabled"]
  ]);

  for (const [name, equivalent] of expected) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} changes Animator component/session state`);
    assert.deepEqual(tool.sideEffects, ["animation-state", "scene-modify"]);
    assert.equal(tool.equivalentUnityMcp, equivalent);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }
});

test("controller core tools expose asset and scene Play Mode semantics", () => {
  const create = findToolByName("controller.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.deepEqual(create.sideEffects, ["asset-write", "asset-import"]);
  assert.equal(create.equivalentUnityMcp, "manage_animation.controller_create");

  const getInfo = findToolByName("controller.get-info")!;
  assert.equal(getInfo.playModeSupport, "supported");
  assert.equal(getInfo.equivalentUnityMcp, "manage_animation.controller_get_info");

  const addParameter = findToolByName("controller.add-parameter")!;
  assert.equal(addParameter.playModeSupport, "session-affecting");
  assert.deepEqual(addParameter.sideEffects, ["animator-controller-modify", "asset-write"]);
  assert.equal(addParameter.equivalentUnityMcp, "manage_animation.controller_add_parameter");
  const props = addParameter.inputSchema.properties as Record<string, { enum?: string[] }>;
  assert.deepEqual(props.parameterType.enum, ["float", "int", "bool", "trigger"]);

  const assign = findToolByName("controller.assign")!;
  assert.equal(assign.playModeSupport, "session-affecting");
  assert.deepEqual(assign.sideEffects, ["component-add", "component-modify", "scene-modify"]);
  assert.equal(assign.equivalentUnityMcp, "manage_animation.controller_assign");

  for (const name of ["controller.create", "controller.get-info", "controller.add-parameter", "controller.assign"]) {
    const tool = findToolByName(name)!;
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }
});

test("controller state and layer tools expose asset mutation semantics", () => {
  const expected = new Map([
    ["controller.add-state", "manage_animation.controller_add_state"],
    ["controller.add-transition", "manage_animation.controller_add_transition"],
    ["controller.add-layer", "manage_animation.controller_add_layer"],
    ["controller.set-layer-weight", "manage_animation.controller_set_layer_weight"]
  ]);

  for (const [name, equivalent] of expected) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} writes AnimatorController asset state`);
    assert.deepEqual(tool.sideEffects, ["animator-controller-modify", "asset-write"]);
    assert.equal(tool.equivalentUnityMcp, equivalent);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  const transitionProps = findToolByName("controller.add-transition")!.inputSchema.properties as Record<string, { type?: string; items?: unknown }>;
  assert.equal(transitionProps.conditions.type, "array");
  assert.ok(transitionProps.conditions.items);

  const addLayerProps = findToolByName("controller.add-layer")!.inputSchema.properties as Record<string, { enum?: string[] }>;
  assert.deepEqual(addLayerProps.blendingMode.enum, ["override", "additive"]);

  const removeLayer = findToolByName("controller.remove-layer")!;
  assert.equal(removeLayer.playModeSupport, "session-affecting");
  assert.equal(removeLayer.destructiveHint, true);
  assert.deepEqual(removeLayer.sideEffects, ["animator-controller-modify", "asset-write", "asset-delete-nested"]);
  assert.equal(removeLayer.equivalentUnityMcp, "manage_animation.controller_remove_layer");
  assert.ok(removeLayer.examples?.some((example) => example.argv.includes("--input") && example.argv.includes("--yes")));
});

test("controller blend tree tools expose nested asset mutation semantics", () => {
  const expected = new Map([
    ["controller.create-blend-tree-1d", "manage_animation.controller_create_blend_tree_1d"],
    ["controller.create-blend-tree-2d", "manage_animation.controller_create_blend_tree_2d"],
    ["controller.add-blend-tree-child", "manage_animation.controller_add_blend_tree_child"]
  ]);

  for (const [name, equivalent] of expected) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} writes nested BlendTree assets`);
    assert.deepEqual(tool.sideEffects, ["animator-controller-modify", "asset-write", "asset-add-nested"]);
    assert.equal(tool.equivalentUnityMcp, equivalent);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  const blend2dProps = findToolByName("controller.create-blend-tree-2d")!.inputSchema.properties as Record<string, { enum?: string[] }>;
  assert.deepEqual(blend2dProps.blendType.enum, ["simpledirectional2d", "freeformdirectional2d", "freeformcartesian2d"]);
});

test("graphics tools expose Play Mode, safety and output metadata", () => {
  const readOnly = [
    "graphics.pipeline", "graphics.volume", "graphics.bake-status", "graphics.stats", "graphics.stats-counters",
    "graphics.stats-memory", "graphics.skybox-get", "graphics.pipeline-info"
  ];
  for (const name of readOnly) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "supported", `${name} should be read-only in Play Mode`);
    assert.deepEqual(tool.sideEffects, [], `${name} should disclose no side effects`);
    assert.ok(tool.outputSchema, `${name} should describe output shape`);
    assert.equal(tool.supportStatus, "live-bridge", `${name} should disclose live bridge status`);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  const mutating = [
    "graphics.quality", "graphics.settings", "graphics.screenshot", "graphics.bake-settings", "graphics.light-probe-group",
    "graphics.reflection-probe", "graphics.probe-positions", "graphics.scene-debug", "graphics.skybox-set-material",
    "graphics.skybox-set-properties", "graphics.skybox-set-ambient", "graphics.skybox-set-fog", "graphics.skybox-set-reflection",
    "graphics.skybox-set-sun", "graphics.pipeline-quality", "graphics.pipeline-settings"
  ];
  for (const name of mutating) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} changes editor/session/project graphics state`);
    assert.ok(tool.sideEffects && tool.sideEffects.length > 0, `${name} should expose side effects`);
    assert.ok(tool.outputSchema, `${name} should describe output shape`);
    assert.equal(tool.supportStatus, "live-bridge", `${name} should disclose live bridge status`);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  for (const name of ["graphics.bake-start", "graphics.bake-cancel", "graphics.bake-reflection-probe", "graphics.bake-clear"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "editor-only", `${name} should disclose editor-only bake semantics`);
    assert.ok(tool.outputSchema, `${name} should describe output shape`);
    assert.ok(tool.sideEffects && tool.sideEffects.length > 0, `${name} should expose bake/probe side effects`);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${name} should show --input usage`);
  }

  const bakeStart = findToolByName("graphics.bake-start")!;
  assert.equal(bakeStart.destructiveHint, true);
  assert.equal(bakeStart.supportsDryRun, undefined, "graphics.bake-start should require --yes rather than advertise unsupported dry-run");
  assert.ok(bakeStart.examples?.some((example) => example.argv.includes("--yes")), "graphics.bake-start should show explicit confirmation");

  const bakeClear = findToolByName("graphics.bake-clear")!;
  assert.equal(bakeClear.destructiveHint, true);
  assert.equal(bakeClear.supportsDryRun, undefined, "graphics.bake-clear should require --yes rather than advertise unsupported dry-run");
  assert.ok(bakeClear.examples?.some((example) => example.argv.includes("--yes")), "graphics.bake-clear should show explicit confirmation");

  const bakeReflectionProbe = findToolByName("graphics.bake-reflection-probe")!;
  assert.equal(bakeReflectionProbe.destructiveHint, true);
  assert.equal(bakeReflectionProbe.supportsDryRun, undefined, "graphics.bake-reflection-probe should require --yes rather than advertise unsupported dry-run");
  assert.ok(bakeReflectionProbe.examples?.some((example) => example.argv.includes("--yes")), "graphics.bake-reflection-probe should show explicit confirmation");

  const qualityProps = findToolByName("graphics.quality")!.inputSchema.properties as Record<string, unknown>;
  assert.ok(qualityProps.level, "graphics.quality should accept level alias");
  const pipelineQualityProps = findToolByName("graphics.pipeline-quality")!.inputSchema.properties as Record<string, unknown>;
  assert.ok(pipelineQualityProps.level, "graphics.pipeline-quality should accept level alias");
});

test("tools describe graphics.volume returns bridge metadata", async () => {
  const result = await handleToolsCommand("describe", ["graphics.volume"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "graphics.volume");
  assert.equal(data.group, "graphics");
  assert.equal("handler" in data, false);
  assert.equal(data.cliCommand, "tools run graphics.volume");
});

test("tools describe animation.play returns full bridge metadata", async () => {
  const result = await handleToolsCommand("describe", ["animation.play"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "animation.play");
  assert.equal(data.group, "animation");
  assert.equal("handler" in data, false);
  assert.equal(data.cliCommand, "tools run animation.play");
});

// ── tools unknown subcommand ───────────────────────────────

test("tools unknown subcommand returns error", async () => {
  const result = await handleToolsCommand("unknown", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_TOOLS_COMMAND");
});

test("tools without subcommand returns unknown-command error", async () => {
  const result = await handleToolsCommand(undefined, [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_TOOLS_COMMAND");
});

test("unregistered tools subcommand returns unknown-command error", async () => {
  const result = await handleToolsCommand("catalog", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_TOOLS_COMMAND");
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
      summary: "Sample Runtime Echo",
      description: "Echoes input from runtime",
      group: "sample",
      inputSchema: { type: "object", properties: { message: { type: "string" } }, required: ["message"] },
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
      summary: "Dual Tool",
      description: "Works in both domains",
      group: "sample",
      inputSchema: { type: "object" },
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
      summary: "Dual Tool",
      description: "Works in both domains",
      group: "sample",
      inputSchema: { type: "object" },
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
      summary: "Editor Tool",
      description: "Editor-only tool",
      group: "custom",
      inputSchema: { type: "object" },
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
        return { ok: true, data: { tools: [{ name: "sample.runtime-echo", summary: "Sample Runtime Echo", description: "Echoes input from runtime", group: "sample", target: "runtime", handler: "bridge-runtime" }] } };
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
        return { ok: true, data: { tools: [{ name: "my.editor-tool", summary: "Editor Tool", description: "Editor-only", group: "custom", target: "editor" }] } };
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

// ── tools.run descriptor is executable ────────────────────

test("tools.run descriptor is executable", () => {
  const tool = findToolByName("tools.run");
  assert.ok(tool, "tools.run must exist in builtin metadata");
  assert.equal(tool.supportStatus, "live-bridge", "tools.run supportStatus must be live-bridge, not planned");
  assert.equal(tool.handler, "hybrid", "tools.run handler must be hybrid (routes dynamically)");
  assert.ok(!tool.description.includes("Reserved for"), "description must not contain reserved-status wording");
  assert.ok(!tool.description.includes("currently returns"), "description must not say currently returns stub error");
});

// ── Skill/descriptor/docs CLI parameter consistency ────────

test("skill does not use stale CLI parameter/field/error names", () => {
  const skillPath = fileURLToPath(new URL("../../skills/unity_cli/SKILL.md", import.meta.url));
  const skill = readFileSync(skillPath, "utf8");

  // --input-json is not a CLI parameter; the CLI uses --input
  const inputJsonMatches = skill.match(/--input-json/g);
  // Allow occurrences in "Do not / 不要 / 不得 / 使用" warning context
  const inputJsonAllowed = (skill.match(/Do not.*--input-json|不要.*--input-json|不得.*--input-json|用 `--input-json`/g) ?? []).length;
  assert.ok(
    (inputJsonMatches?.length ?? 0) - inputJsonAllowed <= 0,
    `skill must not teach --input-json (found ${(inputJsonMatches?.length ?? 0)} occurrences beyond the warning)`
  );

  // JSON field next_actions, not nextActions (except in warning table)
  const nextActionsMatches = skill.match(/nextActions/g);
  const nextActionsAllowed = (skill.match(/使用 `nextActions`/g) ?? []).length;
  assert.ok(
    (nextActionsMatches?.length ?? 0) - nextActionsAllowed <= 0,
    "skill must use next_actions (snake_case), not nextActions (camelCase) outside warning context"
  );

  // E_RUNTIME_NOT_READY, not E_PLAYMODE_NOT_READY (except in warning table)
  const playmodeMatches = skill.match(/E_PLAYMODE_NOT_READY/g);
  const playmodeAllowed = (skill.match(/使用 `E_PLAYMODE_NOT_READY`/g) ?? []).length;
  assert.ok(
    (playmodeMatches?.length ?? 0) - playmodeAllowed <= 0,
    "skill must use E_RUNTIME_NOT_READY, not E_PLAYMODE_NOT_READY outside warning context"
  );
});

// ── tools run regression: tools run built-in bridge + custom tools ──

test("tools run scene.hierarchy passes --input as bridge args", async () => {
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

test("tools run validates required input fields before bridge execution", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: {} };
    }
  };

  const result = await handleToolsCommand("run", ["go.inspect"], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_SCHEMA_INVALID");
  assert.equal(requests.length, 0);
});

test("tools run required field validation accepts explicit CLI flags", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { gameObject: { path: "Player" } } };
    }
  };

  const result = await handleToolsCommand("run", ["go.modify"], { target: "Player" }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests.length, 1);
  assert.equal((requests[0].args as Record<string, unknown>).target, "Player");
});

test("tools run --stdin reads JSON input for offline tools", () => {
  const cliPath = fileURLToPath(new URL("../src/cli.js", import.meta.url));
  const child = spawnSync(process.execPath, [cliPath, "tools", "run", "tools.describe", "--stdin"], {
    input: JSON.stringify({ name: "scene.hierarchy" }),
    encoding: "utf8"
  });
  assert.equal(child.status, 0, child.stderr || child.stdout);
  const output = JSON.parse(child.stdout) as { ok: boolean; data?: { name?: string } };
  assert.equal(output.ok, true);
  assert.equal(output.data?.name, "scene.hierarchy");
});

test("tools run built-in bridge tool without --input sends empty args", async () => {
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

test("tools run preserves boolean false from --input", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { enabled: false } };
    }
  };

  const result = await handleToolsCommand("run", ["animator.set-enabled"], { input: '{"target":"Probe","value":false}' }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "animator.set-enabled");
  assert.deepEqual(requests[0].args, { target: "Probe", value: false });
});

test("tools run built-in job-handler tool with empty runtime returns E_TOOL_HANDLER_UNSUPPORTED", async () => {
  const result = await handleToolsCommand("run", ["editor.refresh"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_HANDLER_UNSUPPORTED");
});

test("tools run editor.compile-diagnostics orchestrates refresh wait and console", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      if (payload.command === "asset.refresh") {
        return { ok: true, data: { command: "asset.refresh", refreshed: true, path: payload.args.path } };
      }
      if (payload.command === "context.read") {
        return { ok: true, data: { schemaVersion: "1.0", state: { busy: { isBusy: false } } } };
      }
      if (payload.command === "console.get") {
        return { ok: true, data: { command: "console.get", total: 1, items: [{ index: 4, type: "error", message: "Assets/Scripts/Broken.cs(3,9): error CS0103: The name 'missing' does not exist", file: "Assets/Scripts/Broken.cs", line: 3 }], pageInfo: { total: 1 } } };
      }
      return { ok: false, error: { code: "UNEXPECTED", message: String(payload.command) } };
    }
  };

  const result = await handleToolsCommand("run", ["editor.compile-diagnostics"], { input: '{"path":"Assets/Scripts/Broken.cs","waitTimeoutMs":100,"pollIntervalMs":1}' }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["asset.refresh", "context.read", "console.get"]);
  assert.deepEqual(requests[0].args, { path: "Assets/Scripts/Broken.cs" });
  assert.equal((requests[1].args.input as Record<string, unknown>).waitReady, true);
  assert.deepEqual(requests[2].args, { type: "error", count: 100, cursor: 0 });
  const data = result.data as Record<string, unknown>;
  assert.equal(data.status, "compile_errors");
  const compile = data.compile as { hasErrors: boolean; errorCount: number; diagnostics: Array<{ id?: string }> };
  assert.equal(compile.hasErrors, true);
  assert.equal(compile.errorCount, 1);
  assert.equal(compile.diagnostics[0].id, "CS0103");
});

test("tools run editor.compile-diagnostics dry-run does not call bridge", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = { bridgeExecute: async (payload: BridgeRequest) => { requests.push(payload); return { ok: true, data: {} }; } };
  const result = await handleToolsCommand("run", ["editor.compile-diagnostics"], { "dry-run": true, input: '{"refresh":false,"count":20}' }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests.length, 0);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.status, "dry_run");
  assert.equal(data.dryRun, true);
  assert.match(JSON.stringify(data.plannedEffects), /console\.get/);
});

test("tools run editor.compile-diagnostics rejects job mode", async () => {
  const result = await handleToolsCommand("run", ["editor.compile-diagnostics"], { job: true }, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_JOB_UNSUPPORTED");
});

test("tools run editor.launch-dialog.dismiss supports no-op probe through --input", async () => {
  const result = await handleToolsCommand("run", ["editor.launch-dialog.dismiss"], { input: '{"timeoutMs":0,"pid":44960}' }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "editor.launch-dialog.dismiss");
  assert.equal(data.dismissed, false);
  assert.equal(data.status, "not_found");
  assert.equal(data.attempts, 0);
  assert.equal(data.processId, 44960);
});

// ── job-routing: job-handler routing via dispatchJobHandler ──────────

test("tools run test.run routes to handleTest with --input merged", async () => {
  const { JobStore } = await import("../src/job-store.js");
  const { JobRunner } = await import("../src/job-runner.js");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const { mkdirSync, rmSync } = await import("node:fs");
  const dataDir = join(tmpdir(), `tools-run-test-${Date.now()}`);
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

test("tools run build.run routes to handleBuild with --input merged", async () => {
  const { JobStore } = await import("../src/job-store.js");
  const { JobRunner } = await import("../src/job-runner.js");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const { mkdirSync, rmSync } = await import("node:fs");
  const dataDir = join(tmpdir(), `tools-run-build-${Date.now()}`);
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

test("tools run editor.refresh routes to handleEditor with --input merged", async () => {
  const { JobStore } = await import("../src/job-store.js");
  const { JobRunner } = await import("../src/job-runner.js");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const { mkdirSync, rmSync } = await import("node:fs");
  const dataDir = join(tmpdir(), `tools-run-editor-${Date.now()}`);
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

test("mergeInputToFlags converts camelCase to kebab-case", async () => {
  // Verify that --input '{"unity":"/path/Unity.exe","project":"/path/project"}' is merged as --unity and --project
  // by checking that routing reaches the job handler instead of E_TOOL_HANDLER_UNSUPPORTED.
  const { JobStore } = await import("../src/job-store.js");
  const { JobRunner } = await import("../src/job-runner.js");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const { mkdirSync, rmSync } = await import("node:fs");
  const dataDir = join(tmpdir(), `tools-run-merge-${Date.now()}`);
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

test("tools run custom project tool passes name and input to project-tool.run", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    projectTools: [{
      name: "sample.echo",
      summary: "Sample Echo",
      description: "Echoes input",
      group: "sample",
      inputSchema: { type: "object", properties: { message: { type: "string" } } },
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

  const result = await handleToolsCommand("run", ["sample.echo"], { "include-custom": true, input: '{"message":"hello"}' }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "project-tool.run");
  assert.equal((requests[0].args as Record<string, unknown>).name, "sample.echo");
  assert.deepEqual((requests[0].args as Record<string, unknown>).input, { message: "hello" });
});

test("tools run built-in hybrid tool routes to native bridge command", async () => {
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { result: "ok" } };
    }
  };

  // profiler.start is a hybrid handler tool with bridgeCommand "profiler.start".
  // --input is merged into flags and buildArgs translates deepProfiling → deepProfiling.
  const result = await handleToolsCommand("run", ["profiler.start"], { input: '{"deepProfiling":true}' }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "profiler.start");
  assert.deepEqual((requests[0].args as Record<string, unknown>).deepProfiling, true);
});

test("tools run bridge tool returns structured error when --job is unsupported", async () => {
  const result = await handleToolsCommand("run", ["scene.active"], { job: true }, emptyRuntime, requestId);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_JOB_UNSUPPORTED");
  assert.equal((result.error?.details as { tool: string }).tool, "scene.active");
});

test("tools run supported bridge --job queues batchmode without live bridge endpoint", async () => {
  const { JobStore } = await import("../src/job-store.js");
  const { JobRunner } = await import("../src/job-runner.js");
  const dataDir = makeTempDir("tools-bridge-job");
  try {
    const project = makeUnityProject(dataDir);
    const unity = makeFakeUnity(dataDir, `process.exit(0);`);
    const store = new JobStore(dataDir);
    const runner = new JobRunner(store, dataDir);
    const runtime = { dataDir, store, runner } as ToolsRuntime;

    const result = await handleToolsCommand("run", ["profiler.capture", "profile.json"], { job: true, unity, project, "timeout-ms": "1234" }, runtime, requestId);

    assert.equal(result.ok, true);
    const data = result.data as { kind: string; command: string[]; result: { command: string; bridgeCommand: string; mode: string; args: { path: string }; bridge: { dependencyName: string } } };
    assert.equal(data.kind, "bridge");
    assert.equal(data.result.command, "profiler.capture");
    assert.equal(data.result.bridgeCommand, "profiler.capture");
    assert.equal(data.result.mode, "bridge-job");
    assert.equal(data.result.args.path, "profile.json");
    assert.equal(data.result.bridge.dependencyName, "com.unity_cli");
    assert.ok(data.command.includes("UnityCli.UnityCliBatch.RunBridgeCommand"));
    const argsJson = data.command[data.command.indexOf("-unityCliBridgeArgs") + 1];
    const envelope = JSON.parse(argsJson) as { __unityCliBridgeEnvelope: boolean; args: { path: string } };
    assert.equal(envelope.__unityCliBridgeEnvelope, true);
    assert.deepEqual(envelope.args, { path: "profile.json" });

    await runner.wait((result.data as { jobId: string }).jobId, 3000);
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
});

test("tools run bridge --job returns UNITY_BUSY before bridge package deployment", async () => {
  const { JobStore } = await import("../src/job-store.js");
  const { JobRunner } = await import("../src/job-runner.js");
  const dataDir = makeTempDir("tools-bridge-job-locked");
  try {
    const project = makeUnityProject(dataDir);
    mkdirSync(join(project, "Temp"), { recursive: true });
    writeFileSync(join(project, "Temp", "UnityLockfile"), "locked", "utf8");
    const unity = makeFakeUnity(dataDir, `process.exit(0);`);
    const store = new JobStore(dataDir);
    const runner = new JobRunner(store, dataDir);
    const runtime = { dataDir, store, runner } as ToolsRuntime;

    const result = await handleToolsCommand("run", ["profiler.capture"], { job: true, unity, project, input: JSON.stringify({ path: "profile.json" }) }, runtime, requestId);

    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "UNITY_BUSY");
    assert.equal(readFileSync(join(project, "Packages", "manifest.json"), "utf8").includes("com.unity_cli"), false);
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
});

test("tools run project-tool bridge --job wraps tool name and input", async () => {
  const { JobStore } = await import("../src/job-store.js");
  const { JobRunner } = await import("../src/job-runner.js");
  const dataDir = makeTempDir("tools-project-tool-job");
  try {
    const project = makeUnityProject(dataDir, { "com.unity.probuilder": "5.0.7" });
    const unity = makeFakeUnity(dataDir, `process.exit(0);`);
    const store = new JobStore(dataDir);
    const runner = new JobRunner(store, dataDir);
    const runtime = { dataDir, store, runner } as ToolsRuntime;

    const result = await handleToolsCommand("run", ["probuilder.create-shape"], { job: true, unity, project, input: JSON.stringify({ shapeType: "Cube", name: "JobCube" }) }, runtime, requestId);

    assert.equal(result.ok, true);
    const data = result.data as { command: string[]; result: { bridgeCommand: string; args: { name: string; input: { shapeType: string; name: string } } } };
    assert.equal(data.result.bridgeCommand, "project-tool.run");
    assert.deepEqual(data.result.args, { name: "probuilder.create-shape", input: { shapeType: "Cube", name: "JobCube" } });
    const argsJson = data.command[data.command.indexOf("-unityCliBridgeArgs") + 1];
    const envelope = JSON.parse(argsJson) as { args: { name: string; input: { shapeType: string; name: string } } };
    assert.deepEqual(envelope.args, { name: "probuilder.create-shape", input: { shapeType: "Cube", name: "JobCube" } });

    await runner.wait((result.data as { jobId: string }).jobId, 3000);
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
});

test("bridge job summary reads UNITY_CLI_BRIDGE_RESULT from logs", async () => {
  const { JobStore } = await import("../src/job-store.js");
  const { JobRunner } = await import("../src/job-runner.js");
  const dataDir = makeTempDir("tools-bridge-job-log");
  try {
    const project = makeUnityProject(dataDir);
    const unity = makeFakeUnity(dataDir, `
process.stdout.write('UNITY_CLI_BRIDGE_RESULT {"ok":true,"data":{"captured":true,"path":"profile.json"}}\\n');
process.exit(0);
`);
    const store = new JobStore(dataDir);
    const runner = new JobRunner(store, dataDir);
    const runtime = { dataDir, store, runner } as ToolsRuntime;

    const created = await handleToolsCommand("run", ["profiler.capture"], { job: true, wait: true, unity, project, input: JSON.stringify({ path: "profile.json" }) }, runtime, requestId);

    assert.equal(created.ok, true);
    const data = created.data as { status: string; result: { data: { captured: boolean; path: string }; bridgeEnvelope: { ok: boolean } } };
    assert.equal(data.status, "succeeded");
    assert.deepEqual(data.result.data, { captured: true, path: "profile.json" });
    assert.equal(data.result.bridgeEnvelope.ok, true);
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
});

test("bridge job failure diagnostic uses Bridge error envelope", async () => {
  const { JobStore } = await import("../src/job-store.js");
  const { JobRunner } = await import("../src/job-runner.js");
  const dataDir = makeTempDir("tools-bridge-job-fail");
  try {
    const project = makeUnityProject(dataDir);
    const unity = makeFakeUnity(dataDir, `
process.stdout.write('UNITY_CLI_BRIDGE_RESULT {"ok":false,"error":{"code":"E_PROFILER_CAPTURE_FAILED","message":"capture failed"}}\\n');
process.exit(1);
`);
    const store = new JobStore(dataDir);
    const runner = new JobRunner(store, dataDir);
    const runtime = { dataDir, store, runner } as ToolsRuntime;

    const created = await handleToolsCommand("run", ["profiler.capture"], { job: true, unity, project, input: JSON.stringify({ path: "profile.json" }) }, runtime, requestId);
    const waited = await runner.wait((created.data as { jobId: string }).jobId, 3000);

    assert.equal(waited.status, "failed");
    assert.equal(waited.error?.code, "E_PROFILER_CAPTURE_FAILED");
    assert.equal(waited.error?.message, "capture failed");
    assert.deepEqual(waited.result?.bridgeError, { code: "E_PROFILER_CAPTURE_FAILED", message: "capture failed" });
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
  }
});

// ── Play Mode regression: Play Mode tool discovery and --target/--runtime flags ──

test("shouldDiscoverProjectTools returns true when --target flag is provided", async () => {
  // Regression: passing --target without --bridge-endpoint should still discover tools
  // because the target instance has a bridge endpoint.
  const { shouldDiscoverProjectTools } = await import("../src/project-tool-discovery.js");
  assert.equal(shouldDiscoverProjectTools({ target: "da1e42fad0da" }, {}), true);
});

test("shouldDiscoverProjectTools returns true when --instance flag is provided", async () => {
  const { shouldDiscoverProjectTools } = await import("../src/project-tool-discovery.js");
  assert.equal(shouldDiscoverProjectTools({ instance: "abc123" }, {}), true);
});

test("shouldDiscoverProjectTools returns true when --runtime flag is provided", async () => {
  // --runtime implies the user wants runtime tools, which must be discovered from the bridge
  const { shouldDiscoverProjectTools } = await import("../src/project-tool-discovery.js");
  assert.equal(shouldDiscoverProjectTools({ runtime: true }, {}), true);
});

test("shouldDiscoverProjectTools returns false when no bridge indicators are present", async () => {
  const { shouldDiscoverProjectTools } = await import("../src/project-tool-discovery.js");
  assert.equal(shouldDiscoverProjectTools({}, {}), false);
});

test("tools run discovers runtime tools when --target flag is provided without --bridge-endpoint", async () => {
  // Regression for user passes --target without --bridge-endpoint
  // and expects runtime tool discovery to work.
  const requests: BridgeRequest[] = [];
  const runtime: ToolsRuntime = {
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      if (payload.command === "runtime-tool.list") {
        return { ok: true, data: { tools: [{ name: "runtime.wait", summary: "Runtime Wait", description: "Wait in runtime", group: "runtime", target: "runtime", handler: "bridge-runtime" }] } };
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
        return { ok: true, data: { tools: [{ name: "sample.runtime-echo", summary: "Sample Runtime Echo", description: "Echoes input from runtime", group: "sample", target: "runtime", handler: "bridge-runtime" }] } };
      }
      return { ok: true, data: { result: { requestId: "req", isPlaying: true } } };
    }
  };

  const result = await handleToolsCommand("run", ["sample.runtime-echo"], { runtime: true, "bridge-endpoint": "http://127.0.0.1:47392", yes: true }, runtime, requestId);
  assert.equal(result.ok, true, `Expected ok=true, got error: ${result.error?.code} ${result.error?.message}`);
  assert.equal(requests[0].command, "runtime-tool.list");
  assert.equal(requests[1].command, "runtime-tool.run");
});

// ── normalizeToolMetadata defaults and extension source ───────

test("normalizeToolMetadata fills default kind and source", () => {
  const normalized = normalizeToolMetadata({
    name: "test.tool",
    summary: "Test",
    description: "Test tool",
    group: "test",
    inputSchema: {},
    destructiveHint: false
  });
  assert.equal(normalized.kind, "tool");
  assert.equal(normalized.source, "built-in");
});

test("probuilder and vfx tools have source: extension", () => {
  const probuilder = findToolByName("probuilder.ping");
  assert.ok(probuilder, "probuilder.ping should exist");
  assert.equal(probuilder.source, "extension");

  const vfx = findToolByName("vfx.graph");
  assert.ok(vfx, "vfx.graph should exist");
  assert.equal(vfx.source, "extension");
});

test("tools.group and tools.group-list descriptors replace tools.list and tools.groups", () => {
  const group = findToolByName("tools.group");
  assert.ok(group, "tools.group should exist");
  assert.equal(group.cliCommand, "tools group");

  const groupList = findToolByName("tools.group-list");
  assert.ok(groupList, "tools.group-list should exist");
  assert.equal(groupList.cliCommand, "tools group list");

  // Old names should no longer exist
  assert.equal(findToolByName("tools.list"), undefined, "tools.list should be removed");
  assert.equal(findToolByName("tools.groups"), undefined, "tools.groups should be removed");
});
