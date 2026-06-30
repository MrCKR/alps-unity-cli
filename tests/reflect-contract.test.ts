import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { readFile } from "node:fs/promises";

/**
 * Reflect 域契约测试：reflect.type / member / search（离线反射缓存）。
 * 对齐 CoplayDev unity_reflect get_type/get_member/search，
 * 验证 metadata 一致、错误恢复提示迁移到 `tools run` 形态、离线 handler。
 * 详细的行为测试（分页/歧义/assembly 诊断）在 docs-reflect.test.ts。
 */

test("reflect group tools exist with offline handler and supported play mode", () => {
  const tools = filterToolsByGroup("reflect").map((t) => t.name).sort();
  assert.deepEqual(tools, ["reflect.member", "reflect.search", "reflect.type"]);

  const type = findToolByName("reflect.type")!;
  assert.equal(type.destructiveHint, false);
  assert.equal(type.handler, "offline");
  assert.equal(type.playModeSupport, "supported");

  const member = findToolByName("reflect.member")!;
  assert.equal(member.handler, "offline");
  assert.equal(member.playModeSupport, "supported");

  const search = findToolByName("reflect.search")!;
  assert.equal(search.handler, "offline");
  assert.equal(search.playModeSupport, "supported");
});

test("reflect tools expose examples and argument descriptions", () => {
  for (const name of ["reflect.type", "reflect.member", "reflect.search"]) {
    const tool = findToolByName(name)!;
    assert.ok(tool.examples?.length, `${name} should expose examples`);
  }
  const search = findToolByName("reflect.search")!;
  const props = search.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.pageSize, "reflect.search should document pageSize");
  assert.ok(props.cursor, "reflect.search should document cursor");
});

test("docs-reflect.ts error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const source = await readFile(new URL("../src/docs-reflect.js", import.meta.url), "utf8");
  // 旧 CLI 命令形态不得出现（环境变量名 UNITY_CLI_* 是合法配置，不在禁列）
  assert.equal(source.includes("unity_cli tools run"), false, "must not reference legacy 'unity_cli tools run' command form");
  assert.match(source, /tools run reflect\.search/, "error hints should route to tools run reflect.search");
  assert.match(source, /tools run reflect\.type/, "error hints should route to tools run reflect.type");
  assert.match(source, /tools run docs\.search/, "docs error hints should route to tools run docs.search");
});
