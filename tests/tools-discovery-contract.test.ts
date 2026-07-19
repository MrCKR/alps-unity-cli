import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";

/** Tools 域只保留只读 discovery Tool；执行入口唯一属于顶层 `tools run <name>` dispatcher。 */
test("tools group exposes only the four discovery meta tools", () => {
  const tools = filterToolsByGroup("tools").map((t) => t.name).sort();
  assert.deepEqual(tools, ["tools.describe", "tools.group", "tools.group-list", "tools.search"]);
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
