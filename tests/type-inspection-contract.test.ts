import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { readFile } from "node:fs/promises";

/**
 * Reflect 域契约测试：type-inspection.get-type / member / search（离线反射缓存）。
 * 对齐 CoplayDev unity_reflect get_type/get_member/search，
 * 验证 metadata 一致、错误恢复提示迁移到 `tools run` 形态、离线 handler。
 * 详细的行为测试（分页/歧义/assembly 诊断）在 docs-reflect.test.ts。
 */

test("type-inspection group tools exist with offline handler and supported play mode", () => {
  const tools = filterToolsByGroup("type-inspection").map((t) => t.name).sort();
  assert.deepEqual(tools, ["type-inspection.get-member", "type-inspection.get-type", "type-inspection.search"]);

  const type = findToolByName("type-inspection.get-type")!;
  assert.equal(type.destructiveHint, false);
  assert.equal(type.handler, "offline");
  assert.equal(type.playModeSupport, "supported");

  const member = findToolByName("type-inspection.get-member")!;
  assert.equal(member.handler, "offline");
  assert.equal(member.playModeSupport, "supported");

  const search = findToolByName("type-inspection.search")!;
  assert.equal(search.handler, "offline");
  assert.equal(search.playModeSupport, "supported");
});

test("type-inspection tools expose examples and argument descriptions", () => {
  for (const name of ["type-inspection.get-type", "type-inspection.get-member", "type-inspection.search"]) {
    const tool = findToolByName(name)!;
    assert.ok(tool.examples?.length, `${name} should expose examples`);
  }
  const search = findToolByName("type-inspection.search")!;
  const props = search.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.pageSize, "type-inspection.search should document pageSize");
  assert.ok(props.cursor, "type-inspection.search should document cursor");
});

test("reflect-service error recovery hints use current tools run form and the current service path", async () => {
  const source = await readFile(new URL("../src/services/reflect-service.js", import.meta.url), "utf8");
  assert.match(source, /tools run type-inspection\.search/, "error hints should route to tools run type-inspection.search");
  assert.match(source, /tools run type-inspection\.get-type/, "error hints should route to tools run type-inspection.get-type");
  assert.doesNotMatch(source, /\btools run reflect\b/, "error hints must not emit removed reflect top-level commands");
});
