/**
 * Unit tests for docs-topic CLI command.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { handleDocsTopicCommand } from "./docs-topic.js";

const requestId = "test_req_docs_topic_001";

test("docs-topic without topic lists all topics", () => {
  const result = handleDocsTopicCommand(undefined, {}, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "docs-topic");
  assert.ok(typeof data.count === "number");
  assert.ok(data.count > 0);

  const topics = data.topics as Array<{ key: string; title: string; summary: string; toolCount: number }>;
  assert.ok(topics.length > 0);
  const keys = topics.map((t) => t.key);
  assert.ok(keys.includes("scene"));
  assert.ok(keys.includes("camera"));
});

test("docs-topic scene returns scene documentation", () => {
  const result = handleDocsTopicCommand("scene", {}, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.topic, "scene");
  assert.equal(data.title, "Scene Commands");
  assert.ok(typeof data.summary === "string");
  assert.ok(Array.isArray(data.tools));
  assert.ok(data.tools.length > 0);
});

test("docs-topic for nonexistent topic returns error", () => {
  const result = handleDocsTopicCommand("nonexistent", {}, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "DOCS_TOPIC_NOT_FOUND");
});

test("docs-topic --format text returns text field", () => {
  const result = handleDocsTopicCommand("scene", { format: "text" }, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.ok(typeof data.text === "string");
  assert.ok(data.text.includes("Scene Commands"));
});

test("docs-topic probuilder includes guide", () => {
  const result = handleDocsTopicCommand("probuilder", {}, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.ok(data.guide !== undefined);
  assert.ok(typeof data.guide === "string");
});