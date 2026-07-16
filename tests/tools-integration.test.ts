import assert from "node:assert/strict";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobStore } from "../src/job-store.js";
import { JobRunner } from "../src/job-runner.js";
import { mkdtemp, mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { probuilderPingTool, sceneHierarchyTool } from "./helpers/live-tool-fixtures.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-integ-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

test("integration: offline tools group via execute() only shows TS-local groups", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "group"], runtime);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "tools.group");
  const groups = data.groups as Array<{ name: string; toolCount: number }>;
  const groupNames = groups.map((g) => g.name);
  assert.ok(groupNames.includes("docs"));
  assert.ok(groupNames.includes("tools"));
  assert.ok(!groupNames.includes("scene"));
});

test("integration: live-discovered built-in group list via execute()", async () => {
  const runtime = { ...(await makeRuntime()), projectTools: [sceneHierarchyTool()] };
  const result = await execute(["tools", "group", "list", "scene"], runtime);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "tools.group.list");
  const tools = data.tools as Array<{ name: string; source: string }>;
  assert.deepEqual(tools.map((tool) => tool.name), ["scene.hierarchy"]);
  assert.equal(tools[0].source, "built-in");
});

test("integration: live-discovered extension tool search via execute()", async () => {
  const runtime = { ...(await makeRuntime()), projectTools: [probuilderPingTool()] };
  const result = await execute(["tools", "search", "probuilder"], runtime);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "tools.search");
  const tools = data.tools as Array<{ name: string; source: string }>;
  assert.ok(tools.some((tool) => tool.name === "probuilder.ping" && tool.source === "extension"));
});

test("integration: live-discovered C# describe via execute()", async () => {
  const runtime = { ...(await makeRuntime()), projectTools: [sceneHierarchyTool()] };
  const result = await execute(["tools", "describe", "scene.hierarchy"], runtime);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "scene.hierarchy");
  assert.equal(data.group, "scene");
  assert.equal(data.destructiveHint, false);
  assert.ok(data.inputSchema);
});

test("integration: unregistered tools subcommand returns unknown-command error", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "catalog"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "UNKNOWN_TOOLS_COMMAND");
});

test("integration: capabilities via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "diagnose.capabilities"], runtime);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "diagnose.capabilities");
  assert.equal(data.mode, "offline");

  const staticData = data.static as Record<string, unknown>;
  assert.ok(typeof staticData.toolCount === "number");
  assert.ok(staticData.toolCount > 0);
});

test("integration: docs built-in topics via canonical docs command", async () => {
  const runtime = await makeRuntime();
  const list = await execute(["tools", "run", "docs.list", "--format", "text"], runtime);
  const topic = await execute(["tools", "run", "docs.topic", "package"], runtime);
  const missing = await execute(["tools", "run", "docs.topic", "missing-topic"], runtime);

  assert.ok(list.ok);
  assert.ok(topic.ok);
  assert.ok(!missing.ok);
  assert.equal((list.data as { command: string }).command, "docs.list");
  assert.match((list.data as { text: string }).text, /package/);
  assert.equal((topic.data as { command: string; topic: string }).command, "docs.topic");
  assert.equal((topic.data as { topic: string }).topic, "package");
  assert.equal(missing.error?.code, "DOCS_TOPIC_NOT_FOUND");
});

test("integration: package read tools run through tools entrypoint", async () => {
  const runtime = await makeRuntime();
  const project = await mkdtemp(join(tmpdir(), "unity_cli-pkg-"));
  await mkdir(join(project, "Packages"), { recursive: true });
  await writeFile(join(project, "Packages", "manifest.json"), `${JSON.stringify({ dependencies: { "com.example.tool": "1.0.0" } }, null, 2)}\n`, "utf8");

  const list = await execute(["tools", "run", "package.list", "--project", project], runtime);
  const status = await execute(["tools", "run", "package.status", "--project", project], runtime);
  const info = await execute(["tools", "run", "package.info", "com.example.tool", "--project", project], runtime);

  assert.ok(list.ok);
  assert.ok(status.ok);
  assert.ok(info.ok);
  const listData = list.data as { packages: Array<{ name: string; requestedVersion?: string }> };
  const statusData = status.data as { packageCount: number };
  const infoData = info.data as { package: { name: string; requestedVersion?: string } };
  assert.deepEqual(listData.packages.map((entry) => entry.name), ["com.example.tool"]);
  assert.equal(statusData.packageCount, 1);
  assert.equal(infoData.package.name, "com.example.tool");
  assert.equal(infoData.package.requestedVersion, "1.0.0");
});

test("integration: tools describe nonexistent returns E_TOOL_NOT_FOUND_FOR_TARGET via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "describe", "nonexistent.tool"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});
