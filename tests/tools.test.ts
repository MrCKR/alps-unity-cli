import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { handleToolsCommand, type ToolsRuntime } from "../src/tools.js";
import {
  allRegisteredToolMetadata,
  builtinToolGroups,
  builtinToolMetadata,
  extensionToolMetadata,
  findToolByName,
  searchTools
} from "../src/tools/metadata/index.js";
import { defineToolMetadata, findDefineTool } from "../src/tools/registry.js";
import { projectAuditTool, probuilderDeleteFacesTool, probuilderPingTool, sceneHierarchyTool, makeLiveTool } from "./helpers/live-tool-fixtures.js";
import "../src/extensions/index.js";

const requestId = "test_req_tools_001";
const emptyRuntime: ToolsRuntime = {};

async function makeProject(packages: Record<string, string> = {}): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity_cli-tools-"));
  const pkgDir = join(dir, "Packages");
  await mkdir(pkgDir, { recursive: true });
  await writeFile(join(pkgDir, "manifest.json"), JSON.stringify({ dependencies: packages }, null, 2));
  const lockDeps: Record<string, { version: string }> = {};
  for (const [id, version] of Object.entries(packages)) {
    lockDeps[id] = { version };
  }
  await writeFile(join(pkgDir, "packages-lock.json"), JSON.stringify({ dependencies: lockDeps }, null, 2));
  return dir;
}

test("offline builtin tool metadata only contains TS-local defineTool entries", () => {
  const tools = builtinToolMetadata();
  assert.equal(tools.length, 34);
  assert.equal(allRegisteredToolMetadata().length, 34);
  assert.equal(extensionToolMetadata().length, 0);
  assert.deepEqual(builtinToolGroups(), [
    "asset",
    "build",
    "code",
    "diagnose",
    "docs",
    "editor",
    "package",
    "reflect",
    "test",
    "tools"
  ]);
});

test("offline lookup helpers expose local tools and hide C#/extension tools", () => {
  assert.ok(findToolByName("tools.group"));
  assert.ok(findToolByName("code.exec"));
  assert.ok(findToolByName("editor.refresh"));
  assert.equal(findToolByName("scene.hierarchy"), undefined);
  assert.equal(findToolByName("probuilder.ping"), undefined);

  const docsResults = searchTools("docs");
  assert.ok(docsResults.some((tool) => tool.name === "docs.search"));
  assert.equal(searchTools("hierarchy").length, 0);
  assert.equal(searchTools("probuilder").length, 0);
});

test("representative local metadata keeps expected handlers and risk hints", () => {
  const codeExec = findToolByName("code.exec")!;
  assert.equal(codeExec.handler, "hybrid");
  assert.equal(codeExec.destructiveHint, true);
  assert.equal(codeExec.supportsDryRun, true);
  assert.ok(codeExec.examples?.length);

  const assetRefresh = findToolByName("asset.refresh")!;
  assert.equal(assetRefresh.handler, "hybrid");
  assert.equal(assetRefresh.supportsDryRun, true);

  const editorRefresh = findToolByName("editor.refresh")!;
  assert.equal(editorRefresh.handler, "job");
  assert.equal(editorRefresh.longRunning, true);

  const buildRun = findToolByName("build.run")!;
  assert.equal(buildRun.handler, "job");
  assert.equal(buildRun.longRunning, true);

  const packageResolve = findToolByName("package.resolve")!;
  assert.equal(packageResolve.handler, "hybrid");
});

test("defineTool registry stays aligned with local metadata only", () => {
  const metadataNames = new Set(defineToolMetadata().map((tool) => tool.name));
  assert.equal(metadataNames.size, 34);
  assert.ok(metadataNames.has("tools.group"));
  assert.ok(metadataNames.has("docs.search"));
  assert.equal(metadataNames.has("scene.hierarchy"), false);
  assert.ok(findDefineTool("tools.group"));
  assert.ok(findDefineTool("code.exec"));
});

test("tools group offline only lists TS-local groups", async () => {
  const result = await handleToolsCommand("group", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const groups = (result.data as { groups: Array<{ name: string }> }).groups.map((group) => group.name);
  assert.ok(groups.includes("docs"));
  assert.ok(groups.includes("tools"));
  assert.ok(groups.includes("package"));
  assert.equal(groups.includes("scene"), false);
  assert.equal(groups.includes("probuilder"), false);
});

test("tools group/search/describe include live-discovered built-in C# tools from runtime.projectTools", async () => {
  const runtime: ToolsRuntime = { projectTools: [sceneHierarchyTool()] };

  const group = await handleToolsCommand("group", [], {}, runtime, requestId);
  assert.equal(group.ok, true);
  const sceneGroup = (group.data as { groups: Array<{ name: string; source: string }> }).groups.find((entry) => entry.name === "scene");
  assert.ok(sceneGroup);
  assert.equal(sceneGroup!.source, "built-in");

  const list = await handleToolsCommand("group", ["list", "scene"], {}, runtime, requestId);
  assert.equal(list.ok, true);
  const listData = list.data as { count: number; tools: Array<{ name: string; source: string }> };
  assert.equal(listData.count, 1);
  assert.deepEqual(listData.tools.map((tool) => tool.name), ["scene.hierarchy"]);
  assert.equal(listData.tools[0].source, "built-in");

  const search = await handleToolsCommand("search", ["hierarchy"], {}, runtime, requestId);
  assert.equal(search.ok, true);
  assert.ok((search.data as { tools: Array<{ name: string }> }).tools.some((tool) => tool.name === "scene.hierarchy"));

  const describe = await handleToolsCommand("describe", ["scene.hierarchy"], {}, runtime, requestId);
  assert.equal(describe.ok, true);
  const describeData = describe.data as Record<string, unknown>;
  assert.equal(describeData.name, "scene.hierarchy");
  assert.equal(describeData.group, "scene");
  assert.equal(describeData.source, "built-in");
});

test("tools group/search/describe include live-discovered extension tools from runtime.projectTools", async () => {
  const runtime: ToolsRuntime = { projectTools: [probuilderPingTool()] };

  const group = await handleToolsCommand("group", [], {}, runtime, requestId);
  assert.equal(group.ok, true);
  const probuilderGroup = (group.data as { groups: Array<{ name: string; source: string }> }).groups.find((entry) => entry.name === "probuilder");
  assert.ok(probuilderGroup);
  assert.equal(probuilderGroup!.source, "extension");

  const search = await handleToolsCommand("search", ["probuilder"], {}, runtime, requestId);
  assert.equal(search.ok, true);
  const searchToolsData = (search.data as { tools: Array<{ name: string; source: string }> }).tools;
  assert.ok(searchToolsData.some((tool) => tool.name === "probuilder.ping" && tool.source === "extension"));

  const describe = await handleToolsCommand("describe", ["probuilder.ping"], {}, runtime, requestId);
  assert.equal(describe.ok, true);
  assert.equal((describe.data as Record<string, unknown>).source, "extension");
});

test("tools group/search/describe include project custom tools from runtime.projectTools", async () => {
  const runtime: ToolsRuntime = { projectTools: [projectAuditTool()] };

  const group = await handleToolsCommand("group", [], {}, runtime, requestId);
  assert.equal(group.ok, true);
  assert.ok((group.data as { groups: Array<{ name: string }> }).groups.some((entry) => entry.name === "project"));

  const search = await handleToolsCommand("search", ["audit"], {}, runtime, requestId);
  assert.equal(search.ok, true);
  assert.ok((search.data as { tools: Array<{ name: string }> }).tools.some((tool) => tool.name === "project.audit"));

  const describe = await handleToolsCommand("describe", ["project.audit"], {}, runtime, requestId);
  assert.equal(describe.ok, true);
  assert.equal((describe.data as Record<string, unknown>).source, "project");
});

test("tools describe unknown tool still returns E_TOOL_NOT_FOUND_FOR_TARGET", async () => {
  const result = await handleToolsCommand("describe", ["nonexistent.tool"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});

test("tools describe local TS-owned tools still works offline", async () => {
  const result = await handleToolsCommand("describe", ["build.run"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "build.run");
  assert.equal(data.group, "build");
  assert.equal(data.longRunning, true);
  assert.equal(data.cancellation, "job");
});

test("tools run routes live-discovered built-in C# tools through project-tool.run", async () => {
  const requests: Array<{ command: string; args: unknown }> = [];
  const runtime: ToolsRuntime = {
    projectTools: [sceneHierarchyTool()],
    bridgeExecute: async (payload) => {
      requests.push({ command: payload.command, args: payload.args });
      return { ok: true, data: { command: payload.command, args: payload.args } };
    }
  };

  const result = await handleToolsCommand("run", ["scene.hierarchy"], { input: '{"pageSize":1}' }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.deepEqual(requests, [{ command: "project-tool.run", args: { name: "scene.hierarchy", input: { pageSize: 1 } } }]);
});

test("tools run routes runtime-target live-discovered tools through runtime-tool.run", async () => {
  const requests: Array<{ command: string; args: unknown }> = [];
  const runtimeTool = makeLiveTool({
    name: "runtime.echo",
    summary: "Echo runtime state.",
    description: "Echo runtime state from Play Mode.",
    group: "runtime",
    target: "runtime"
  });
  const runtime: ToolsRuntime = {
    projectTools: [runtimeTool],
    bridgeExecute: async (payload) => {
      requests.push({ command: payload.command, args: payload.args });
      return { ok: true, data: { command: payload.command, args: payload.args } };
    }
  };

  const result = await handleToolsCommand("run", ["runtime.echo"], {}, runtime, requestId);
  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "runtime-tool.run");
});

test("tools run forwards live-discovered extension tools through project-tool.run", async () => {
  const requests: Array<{ command: string; args: unknown }> = [];
  const runtime: ToolsRuntime = {
    projectTools: [probuilderDeleteFacesTool()],
    bridgeExecute: async (payload) => {
      requests.push({ command: payload.command, args: payload.args });
      return { ok: true, data: {} };
    }
  };
  const result = await handleToolsCommand("run", ["probuilder.delete-faces"], { project: await makeProject(), yes: true }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.deepEqual(requests, [{ command: "project-tool.run", args: { name: "probuilder.delete-faces", input: {} } }]);
});

test("tools run forwards live-discovered tool execution to project-tool.run without offline schema validation", async () => {
  const requests: Array<{ command: string; args: unknown }> = [];
  const runtime: ToolsRuntime = {
    projectTools: [sceneHierarchyTool({
      name: "scene.save",
      summary: "Save a scene.",
      description: "Save a scene to disk.",
      inputSchema: {
        type: "object",
        properties: { path: { type: "string" } },
        required: ["path"]
      },
      destructiveHint: true
    })],
    bridgeExecute: async (payload) => {
      requests.push({ command: payload.command, args: payload.args });
      return { ok: true, data: {} };
    }
  };

  const result = await handleToolsCommand("run", ["scene.save"], { input: '{"path":"Assets/Scene.unity"}', yes: true }, runtime, requestId);
  assert.equal(result.ok, true);
  assert.deepEqual(requests, [{ command: "project-tool.run", args: { name: "scene.save", input: { path: "Assets/Scene.unity" } } }]);
});

test("tools run local offline tool still executes through defineTool", async () => {
  const project = await makeProject({ "com.example.tool": "1.0.0" });
  const result = await handleToolsCommand("run", ["package.list"], { project }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { packages: Array<{ name: string }> };
  assert.deepEqual(data.packages.map((entry) => entry.name), ["com.example.tool"]);
});

test("tools run local tools.group and tools.describe wrappers still execute offline", async () => {
  const group = await handleToolsCommand("run", ["tools.group"], {}, emptyRuntime, requestId);
  assert.equal(group.ok, true);
  assert.ok((group.data as { groups: Array<{ name: string }> }).groups.some((entry) => entry.name === "tools"));

  const describe = await handleToolsCommand("run", ["tools.describe"], { input: '{"name":"docs.search"}' }, emptyRuntime, requestId);
  assert.equal(describe.ok, true);
  assert.equal((describe.data as Record<string, unknown>).name, "docs.search");
});

test("tools.run defineTool wrapper can target live-discovered tools without leaking outer wrapper name", async () => {
  const requests: Array<{ command: string; args: unknown }> = [];
  const runtime: ToolsRuntime = {
    projectTools: [sceneHierarchyTool()],
    bridgeExecute: async (payload) => {
      requests.push({ command: payload.command, args: payload.args });
      return { ok: true, data: {} };
    }
  };

  const result = await handleToolsCommand(
    "run",
    ["tools.run"],
    { input: '{"name":"scene.hierarchy","pageSize":2}', yes: true },
    runtime,
    requestId
  );

  assert.equal(result.ok, true);
  assert.deepEqual(requests, [{ command: "project-tool.run", args: { name: "scene.hierarchy", input: { pageSize: 2 } } }]);
});
