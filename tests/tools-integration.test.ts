/**
 * Integration tests: execute() → tools/capabilities/docs commands through the full CLI pipeline.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobStore } from "../src/job-store.js";
import { JobRunner } from "../src/job-runner.js";
import { mkdtemp, mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-integ-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

test("integration: tools group via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "group"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "tools.group");
  assert.ok(typeof data.count === "number");
  assert.ok(data.count > 0);

  const groups = data.groups as Array<{ name: string; toolCount: number }>;
  const groupNames = groups.map((g) => g.name);
  assert.ok(groupNames.includes("scene"));
});

test("integration: tools group list scene via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "group", "list", "scene"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "tools.group.list");
  const tools = data.tools as Array<{ name: string; source: string }>;
  assert.ok(tools.length > 0);
  for (const t of tools) {
    assert.equal(t.source, "built-in");
  }
});

test("integration: unregistered tools subcommand returns unknown-command error", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "catalog"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_TOOLS_COMMAND");
});

test("integration: tools search hierarchy via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "search", "hierarchy"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "tools.search");
  const tools = data.tools as Array<{ name: string }>;
  const names = tools.map((t) => t.name);
  assert.ok(names.includes("scene.hierarchy"));
});

test("integration: tools describe scene.hierarchy via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "describe", "scene.hierarchy"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "scene.hierarchy");
  assert.equal(data.group, "scene");
  assert.equal(data.destructiveHint, false);
  assert.ok(data.inputSchema);
  assert.ok(Array.isArray(data.examples));
  assert.equal((data as { mode: string }).mode, "offline");
});

test("integration: capabilities via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "diagnose.capabilities"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "capabilities");
  assert.equal(data.mode, "offline");

  const staticData = data.static as Record<string, unknown>;
  assert.ok(typeof staticData.toolCount === "number");
  assert.ok(staticData.toolCount > 0);
});

test("integration: docs built-in topics via canonical docs command", async () => {
  const runtime = await makeRuntime();
  const list = await execute(["tools", "run", "docs.list"], runtime);
  const topic = await execute(["tools", "run", "docs.topic", "scene"], runtime);

  assert.equal(list.ok, true);
  assert.equal(topic.ok, true);
  assert.equal((list.data as { command: string }).command, "docs.topics");
  assert.equal((topic.data as { command: string; topic: string }).command, "docs.topic");
  assert.equal((topic.data as { topic: string }).topic, "scene");
});

test("integration: package.list runs through tools entrypoint", async () => {
  const runtime = await makeRuntime();
  const project = await mkdtemp(join(tmpdir(), "unity_cli-pkg-"));
  await mkdir(join(project, "Packages"), { recursive: true });
  await writeFile(join(project, "Packages", "manifest.json"), `${JSON.stringify({ dependencies: { "com.example.tool": "1.0.0" } }, null, 2)}\n`, "utf8");

  const result = await execute(["tools", "run", "package.list", "--project", project], runtime);

  assert.equal(result.ok, true);
  const data = result.data as { packages: Array<{ name: string; requestedVersion?: string }> };
  assert.deepEqual(data.packages.map((entry) => entry.name), ["com.example.tool"]);
});



test("integration: tools describe nonexistent returns E_TOOL_NOT_FOUND_FOR_TARGET via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "describe", "nonexistent.tool"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});