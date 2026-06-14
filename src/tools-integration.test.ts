/**
 * Integration tests: execute() → tools/capabilities/docs-topic commands through the full CLI pipeline.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { execute } from "./cli.js";
import { JobStore } from "./job-store.js";
import { JobRunner } from "./job-runner.js";
import { mkdtemp } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

process.env.ALPS_UNITY_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-integ-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

test("integration: tools list via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "list"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "tools.list");
  assert.ok(typeof data.count === "number");
  assert.ok(data.count > 0);

  const tools = data.tools as Array<{ name: string; group: string }>;
  const names = tools.map((t) => t.name);
  assert.ok(names.includes("scene.hierarchy"));
});

test("integration: tools list --group scene", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "list", "--group", "scene"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  const tools = data.tools as Array<{ name: string; group: string }>;
  for (const t of tools) {
    assert.equal(t.group, "scene");
  }
});

test("integration: tools groups via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "groups"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "tools.groups");
  const groups = data.groups as string[];
  assert.ok(groups.includes("scene"));
  assert.ok(groups.includes("camera"));
  assert.ok(groups.includes("probuilder"));
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
  assert.equal(data.readOnlyHint, true);
  assert.equal(data.destructiveHint, false);
  assert.ok(data.inputSchema);
  assert.ok(Array.isArray(data.examples));
  assert.equal((data as { mode: string }).mode, "offline");
});

test("integration: capabilities via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["capabilities"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "capabilities");
  assert.equal(data.mode, "offline");

  const staticData = data.static as Record<string, unknown>;
  assert.ok(typeof staticData.toolCount === "number");
  assert.ok(staticData.toolCount > 0);
});

test("integration: docs-topic scene via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["docs-topic", "scene"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.topic, "scene");
  assert.equal(data.title, "Scene Commands");
});

test("integration: docs-topic list via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["docs-topic"], runtime);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.ok(typeof data.count === "number");
  assert.ok(data.count > 0);
});

test("integration: tools describe nonexistent returns TOOL_NOT_FOUND via execute()", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "describe", "nonexistent.tool"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "TOOL_NOT_FOUND");
});