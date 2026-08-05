import assert from "node:assert/strict";
import test from "node:test";
import { buildTopics, getBuiltinDocTopic, listBuiltinDocs } from "../src/builtin-docs.js";
import { staticBridgeToolMetadata, uniqueByName } from "../src/tools/catalog.js";
import { allRegisteredToolMetadata, toolDocsTopic, type ToolMetadata } from "../src/tools/metadata/index.js";

const requestId = "test_req_builtin_docs_001";

function customTool(overrides: Partial<ToolMetadata> = {}): ToolMetadata {
  return {
    name: "terrain.bake",
    summary: "Terrain Bake",
    description: "Bake terrain data.",
    group: "terrain-tools",
    inputSchema: { type: "object" },
    destructiveHint: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    ...overrides
  };
}

test("buildTopics uses generated custom tool topics", () => {
  const topics = buildTopics([customTool()]);
  const topic = topics.find((candidate) => candidate.key === "terrain-tools");

  assert.ok(topic);
  assert.equal(topic.title, "Terrain Tools");
  assert.equal(topic.summary, "Terrain Tools registered by Unity project custom tools.");
  assert.equal(topic.tools.length, 1);
  assert.ok(topic.tools[0].includes("terrain.bake"));
});

test("buildTopics falls back to unknown group title", () => {
  const topics = buildTopics([customTool({ group: "terrain-tools" })]);
  const topic = topics.find((candidate) => candidate.key === "terrain-tools");

  assert.ok(topic);
  assert.equal(topic.title, "Terrain Tools");
  assert.equal(topic.summary, "Terrain Tools registered by Unity project custom tools.");
});

test("buildTopics lists custom Tool names without duplicating descriptors", () => {
  const topics = buildTopics([customTool()]);
  const topic = topics.find((candidate) => candidate.key === "terrain-tools");
  const [line] = topic?.tools ?? [];

  assert.equal(line, "terrain.bake");
});

test("buildTopics includes every offline-discoverable TS and static catalog Tool exactly once", () => {
  const topics = new Map(buildTopics().map((topic) => [topic.key, topic]));
  const expectedByTopic = new Map<string, string[]>();
  for (const tool of uniqueByName([...allRegisteredToolMetadata(), ...staticBridgeToolMetadata()])) {
    const key = toolDocsTopic(tool);
    const names = expectedByTopic.get(key) ?? [];
    names.push(tool.name);
    expectedByTopic.set(key, names);
  }

  for (const [key, expectedNames] of expectedByTopic) {
    const actualNames = [...(topics.get(key)?.tools ?? [])].sort();
    assert.deepEqual(actualNames, expectedNames.sort(), key);
  }
});

test("built-in topic guides never emit a frozen capability domain as a top-level command", () => {
  const frozenDomains = new Set(["scene", "asset", "camera", "go", "prefab", "package", "build", "test", "editor", "profiler", "reflect"]);
  for (const topic of buildTopics()) {
    for (const match of topic.guide?.matchAll(/`([^`]+)`/g) ?? []) {
      const firstToken = match[1].trim().split(/\s+/, 1)[0];
      assert.equal(frozenDomains.has(firstToken), false, `${topic.key}: ${match[1]}`);
    }
    assert.doesNotMatch(topic.summary, /Handler:|offline fallback/i, topic.key);
  }
});

test("listBuiltinDocs includes runtime custom topics", () => {
  const result = listBuiltinDocs({}, { projectTools: [customTool()] }, requestId);
  assert.ok(result.ok);

  const data = result.data as { topics: Array<{ key: string; toolCount: number }> };
  const topic = data.topics.find((candidate) => candidate.key === "terrain-tools");
  assert.equal(topic?.toolCount, 1);
});

test("getBuiltinDocTopic returns runtime custom topic", () => {
  const result = getBuiltinDocTopic("terrain-tools", {}, { projectTools: [customTool()] }, requestId);
  assert.ok(result.ok);

  const data = result.data as { topic: string; tools: string[] };
  assert.equal(data.topic, "terrain-tools");
  assert.equal(data.tools.length, 1);
  assert.ok(data.tools[0].includes("terrain.bake"));
});
