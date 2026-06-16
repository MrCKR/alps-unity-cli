import assert from "node:assert/strict";
import test from "node:test";
import { buildTopics, getBuiltinDocTopic, listBuiltinDocs } from "./builtin-docs.js";
import type { ToolMetadata } from "./tools/metadata/index.js";

const requestId = "test_req_builtin_docs_001";

function customTool(overrides: Partial<ToolMetadata> = {}): ToolMetadata {
  return {
    name: "terrain.bake",
    title: "Terrain Bake",
    description: "Bake terrain data.",
    group: "terrain-tools",
    inputSchema: { type: "object" },
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "custom-tool run terrain.bake",
    ...overrides
  };
}

test("buildTopics includes dynamic custom tool topics", () => {
  const topics = buildTopics([customTool({ docsTopic: "terrain" })]);
  const topic = topics.find((candidate) => candidate.key === "terrain");

  assert.ok(topic);
  assert.equal(topic.title, "Terrain");
  assert.equal(topic.summary, "Terrain registered by Unity project custom tools.");
  assert.equal(topic.tools.length, 1);
  assert.ok(topic.tools[0].includes("terrain.bake"));
});

test("buildTopics falls back to unknown group title", () => {
  const topics = buildTopics([customTool({ docsTopic: undefined })]);
  const topic = topics.find((candidate) => candidate.key === "terrain-tools");

  assert.ok(topic);
  assert.equal(topic.title, "Terrain Tools");
  assert.equal(topic.summary, "Terrain Tools registered by Unity project custom tools.");
});

test("buildTopics formats custom tool lines", () => {
  const topics = buildTopics([customTool()]);
  const topic = topics.find((candidate) => candidate.key === "terrain-tools");
  const [line] = topic?.tools ?? [];

  assert.ok(line);
  assert.ok(line.includes("terrain.bake (custom-tool run terrain.bake) — Terrain Bake"));
  assert.ok(line.includes("已路由可执行"));
  assert.ok(line.includes("write"));
  assert.ok(line.includes("⚠ destructive"));
  assert.ok(line.includes("Bundled Unity Editor Bridge serves this command"));
});

test("listBuiltinDocs includes runtime custom topics", () => {
  const result = listBuiltinDocs({}, { customTools: [customTool()] }, requestId);
  assert.equal(result.ok, true);

  const data = result.data as { topics: Array<{ key: string; toolCount: number }> };
  const topic = data.topics.find((candidate) => candidate.key === "terrain-tools");
  assert.equal(topic?.toolCount, 1);
});

test("getBuiltinDocTopic returns runtime custom topic", () => {
  const result = getBuiltinDocTopic("terrain-tools", {}, { customTools: [customTool()] }, requestId);
  assert.equal(result.ok, true);

  const data = result.data as { topic: string; tools: string[] };
  assert.equal(data.topic, "terrain-tools");
  assert.equal(data.tools.length, 1);
  assert.ok(data.tools[0].includes("terrain.bake"));
});
