import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";

/**
 * Tools 域（meta 发现/执行）契约测试：tools.group / group-list / search / describe / run。
 * 这些是 CLI 自身的工具发现与执行机制（offline/hybrid），不对应参考项目的 Unity 工具，
 * 而是实现“陌生 AI 一次性自发现调用”的基础设施。行为细节由 tools.test.ts / tools-integration.test.ts 覆盖。
 */

test("tools group exposes the 5 discovery/execution meta tools", () => {
  const tools = filterToolsByGroup("tools").map((t) => t.name).sort();
  assert.deepEqual(tools, ["tools.describe", "tools.group", "tools.group-list", "tools.run", "tools.search"]);
});

test("tools discovery tools are offline/non-destructive/supported in any play mode", () => {
  for (const name of ["tools.group", "tools.group-list", "tools.search", "tools.describe"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.destructiveHint, false, `${name} non-destructive`);
    assert.equal(tool.handler, "offline", `${name} offline`);
    assert.equal(tool.supportStatus, "cli-only", `${name} cli-only support status`);
    assert.equal(tool.playModeSupport, "supported", `${name} play mode supported`);
    assert.ok(tool.examples?.length, `${name} should expose examples`);
  }
});

test("tools.run is the unified hybrid execution entry", () => {
  const run = findToolByName("tools.run")!;
  assert.equal(run.destructiveHint, false);
  assert.equal(run.handler, "hybrid");
  assert.equal(run.supportStatus, "live-bridge");
  assert.equal(run.playModeSupport, "supported");
  assert.equal(run.target, "both");
  const props = run.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.name, "tools.run requires name");
  assert.ok(props.input, "tools.run accepts input");
  assert.ok(props.timeoutMs, "tools.run accepts timeoutMs");
  assert.ok(props.fingerprintMode, "tools.run accepts fingerprintMode");
  assert.ok(run.description.toLowerCase().includes("runtime") || run.description.includes("Play Mode"), "tools.run describes runtime/Play Mode routing");
});

test("tools discovery tools declare equivalentUnityMcp routing labels", () => {
  // 这些对应 MCP 标准的 tools/list, tools/search 等语义，而非具体 Unity 工具
  assert.equal(findToolByName("tools.group")!.equivalentUnityMcp, "tools/list-groups");
  assert.equal(findToolByName("tools.group-list")!.equivalentUnityMcp, "tools/list");
  assert.equal(findToolByName("tools.search")!.equivalentUnityMcp, "tools/search");
  assert.equal(findToolByName("tools.describe")!.equivalentUnityMcp, "tools/describe");
});
