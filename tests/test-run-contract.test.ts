import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName } from "../src/tools/metadata/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";

/**
 * test.run 契约测试：运行 Unity EditMode/PlayMode 测试的 batch job。
 * 对齐 CoplayDev run_tests。真实执行会启动 Unity batchmode 与 live Editor 冲突（UNITY_BUSY），
 * 故真实执行 DEFERRED；契约+describe 验证 metadata/contract 一致与 routing 语义。
 */

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
  assert.equal(tool.equivalentUnityMcp, "run_tests");
  assert.ok(tool.examples && tool.examples.length > 0);
});

test("test.run input schema documents platform enum", () => {
  const tool = findToolByName("test.run")!;
  const props = tool.inputSchema.properties as Record<string, { enum?: string[]; default?: string }>;
  assert.ok(props.platform?.enum?.includes("EditMode"), "platform should document EditMode");
  assert.ok(props.platform?.enum?.includes("PlayMode"), "platform should document PlayMode");
  assert.equal(props.platform?.default, "EditMode");
  assert.ok(props.project, "project should be documented");
  assert.ok(props.unity, "unity path should be documented");
  assert.ok(props.timeoutMs, "timeoutMs should be documented");
});

test("test.run exposes cancellation semantics", () => {
  const tool = findToolByName("test.run")!;
  assert.ok(tool.cancellationSemantics, "test.run should document cancellation semantics");
  assert.match(tool.cancellationSemantics!, /batchmode pid|cancelled/i, "cancellation semantics should describe pid/cancel");
});

test("contract entry exists for test.run", () => {
  const contract = findCommandContract("test.run");
  assert.ok(contract, "test.run should be in command contract");
});
