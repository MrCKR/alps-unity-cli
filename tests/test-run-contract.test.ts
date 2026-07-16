import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { findToolByName, toolCancellation } from "../src/tools/metadata/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";

/** test.run 契约测试：TS 本地 job 工具，继续离线可见。 */

test("test.run exists with job handler, longRunning and session-affecting play mode", () => {
  const tool = findToolByName("test.run")!;
  assert.equal(tool.group, "test");
  assert.equal(tool.handler, "job");
  assert.equal(tool.longRunning, true);
  assert.equal(tool.destructiveHint, false);
  assert.equal(tool.playModeSupport, "session-affecting");
  assert.equal(tool.supportStatus, "batch-job");
  assert.ok(tool.sideEffects?.includes("session-affecting"));
  assert.equal(tool.riskLevel, "medium");
  assert.ok(tool.examples && tool.examples.length > 0);
});

test("test.run input schema keeps execution timeout outside business input", async () => {
  const tool = findToolByName("test.run")!;
  const props = tool.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.platform, "platform should be documented in input schema");
  assert.ok(props.project, "project should be documented");
  assert.ok(props.unity, "unity path should be documented");
  assert.equal(props.timeoutMs, undefined, "timeout belongs to --timeout-ms execution controls, not inputSchema");

  const source = await readFile(new URL("../../src/tools/test/run.ts", import.meta.url), "utf8");
  assert.match(source, /platform\?: "EditMode" \| "PlayMode";/, "source should keep the EditMode/PlayMode union");
  assert.match(source, /const platform = request\.platform \?\? "EditMode";/, "source should default platform to EditMode");
  assert.match(source, /execution\.timeoutMs.*flags\["timeout-ms"\]/, "execution timeout should be forwarded as --timeout-ms");
});

test("test.run exposes structured cancellation semantics", () => {
  const tool = findToolByName("test.run")!;
  assert.equal(toolCancellation(tool), "job");
});

test("contract entry exists for test.run", () => {
  const contract = findCommandContract("test.run");
  assert.ok(contract, "test.run should be in command contract");
});
