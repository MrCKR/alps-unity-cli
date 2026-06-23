import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { destructiveCommands } from "../src/security-policy.js";

/**
 * Profiler 域契约测试：modules / module-enable / clear / load / areas / object-memory /
 * rendering-stats / script-stats。
 * 对齐 IvanMurzak modules/clear/load/script-stats 与 CoplayDev areas/object-memory/rendering-stats。
 * profiler.export 保留主入口，profiler.save 不新增。profiler.clear 高风险带 dry-run。
 */

test("profiler domain exposes modules/clear/load/areas/object-memory/rendering-stats/script-stats", () => {
  for (const name of ["profiler.modules", "profiler.clear", "profiler.load", "profiler.areas", "profiler.object-memory", "profiler.rendering-stats", "profiler.script-stats"]) {
    assert.ok(findToolByName(name), `${name} should exist`);
  }
  assert.equal(findToolByName("profiler.clear")?.destructiveHint, true);
  assert.equal(findToolByName("profiler.rendering-stats")?.readOnlyHint, true);
  // profiler.export remains main; profiler.save not added
  assert.ok(findToolByName("profiler.export"));
  assert.ok(!findToolByName("profiler.save"));
});

test("profiler tools have correct safety hints", () => {
  const clear = findToolByName("profiler.clear")!;
  assert.equal(clear.destructiveHint, true);
  assert.equal(clear.supportsDryRun, true);
  for (const name of ["profiler.modules", "profiler.areas", "profiler.object-memory", "profiler.rendering-stats", "profiler.script-stats"]) {
    assert.equal(findToolByName(name)?.readOnlyHint, true, `${name} should be read-only`);
  }
});

test("bridge specs resolve for profiler tools", () => {
  const checks = [
    ["profiler", "modules", "profiler.modules"],
    ["profiler", "clear", "profiler.clear"],
    ["profiler", "load", "profiler.load"],
    ["profiler", "areas", "profiler.areas"],
    ["profiler", "object-memory", "profiler.object-memory"],
    ["profiler", "rendering-stats", "profiler.rendering-stats"],
    ["profiler", "script-stats", "profiler.script-stats"]
  ] as const;
  for (const [group, command, bridgeCommand] of checks) {
    const spec = bridgeCommandRegistry.lookup(group, command);
    assert.ok(spec, `${group} ${command} should resolve in bridge registry`);
    assert.equal(spec.bridgeCommand, bridgeCommand);
  }
});

test("contract entries exist for profiler tools with present C# handler", () => {
  for (const name of ["profiler.modules", "profiler.module-enable", "profiler.clear", "profiler.load", "profiler.areas", "profiler.object-memory", "profiler.rendering-stats", "profiler.script-stats"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("destructive security policy covers profiler.clear", () => {
  const commands = new Set(destructiveCommands().map((e) => e.command));
  assert.ok(commands.has("profiler.clear"));
});
