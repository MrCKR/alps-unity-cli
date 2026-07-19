import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { GENERATED_TOOL_COMPLETION_CONTRACTS } from "../src/tools/.generated-completion-registry.js";
import { staticBridgeToolMetadata } from "../src/tools/catalog.js";
import { defineToolMetadata } from "../src/tools/define.js";
import { toolIsMutation } from "../src/tools/metadata/index.js";

const root = process.cwd();
const contractPath = path.join(root, "contracts", "tool-completion-contract.json");

test("public Tool 与 completion contract 集合严格全等", () => {
  const result = spawnSync(process.execPath, ["scripts/audit-tool-completion-contract.mjs", "--strict"], {
    cwd: root,
    encoding: "utf8",
  });
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
  const report = JSON.parse(result.stdout) as {
    ok: boolean;
    publicTools: number;
    completionContracts: number;
    directTerminal: number;
    jobDeferred: number;
  };
  assert.equal(report.ok, true);
  assert.equal(report.publicTools, report.completionContracts);
  assert.equal(report.directTerminal + report.jobDeferred, report.publicTools);
});

test("生成 completion registry 覆盖 TS 与 C# 且 mutation 分类逐项一致", () => {
  assert.equal(new Set(GENERATED_TOOL_COMPLETION_CONTRACTS.map((entry) => entry.toolName)).size, GENERATED_TOOL_COMPLETION_CONTRACTS.length);
  const byName = new Map(GENERATED_TOOL_COMPLETION_CONTRACTS.map((entry) => [entry.toolName, entry]));
  const descriptors = [...defineToolMetadata(), ...staticBridgeToolMetadata()];
  assert.equal(descriptors.length, GENERATED_TOOL_COMPLETION_CONTRACTS.length);
  for (const descriptor of descriptors) {
    const completion = byName.get(descriptor.name);
    assert.ok(completion, `${descriptor.name} completion contract missing`);
    assert.equal(completion!.mutation, toolIsMutation(descriptor), `${descriptor.name} mutation mismatch`);
    assert.ok(completion!.completionDriverId.endsWith("-v1"), `${descriptor.name} driver version missing`);
  }
  const csharp = fs.readFileSync(path.join(root, "unitypkg", "Editor", "Generated", "UnityCliToolCompletionRegistry.g.cs"), "utf8");
  assert.equal(
    (csharp.match(/Add\(new UnityCliToolCompletionContract\(/g) ?? []).length,
    GENERATED_TOOL_COMPLETION_CONTRACTS.filter((entry) => entry.descriptorOwner === "built-in" || entry.descriptorOwner.startsWith("extension:")).length
  );
});

test("任意执行 Tool 由 completion contract 限制 direct delivery", () => {
  const byName = new Map(GENERATED_TOOL_COMPLETION_CONTRACTS.map((entry) => [entry.toolName, entry]));
  assert.equal(byName.get("code.exec")?.directInvocationPolicy, "deferred-only");
  assert.equal(byName.get("code.exec")?.frozenDeliveryPolicy, "preserved");
  assert.equal(byName.get("reflection.method.call")?.directInvocationPolicy, "prohibited-unprovable");
  assert.equal(byName.get("reflection.method.call")?.frozenDeliveryPolicy, "preserved");
  assert.equal(byName.get("animator.crossfade")?.directInvocationPolicy, "prohibited-unprovable");
  assert.equal(byName.get("animator.crossfade")?.frozenDeliveryPolicy, "preserved");
  assert.equal(byName.get("physics.simulation")?.directInvocationPolicy, "prohibited-unprovable");
  assert.equal(byName.get("physics.simulation")?.frozenDeliveryPolicy, "preserved");
  const nonDirectTools = GENERATED_TOOL_COMPLETION_CONTRACTS
    .filter((entry) => entry.directInvocationPolicy !== "allowed")
    .map((entry) => entry.toolName)
    .sort();
  assert.deepEqual(nonDirectTools, [
    "animator.crossfade", "code.exec", "editor.redo", "editor.undo", "physics.simulation",
    "probuilder.append-vertices-to-edge", "probuilder.auto-smooth", "probuilder.bevel-edges", "probuilder.bridge-edges",
    "probuilder.connect-elements", "probuilder.create-shape", "probuilder.duplicate-and-flip", "probuilder.extrude-edges", "probuilder.extrude-faces",
    "probuilder.flip-normals", "probuilder.insert-vertex", "probuilder.merge-objects", "probuilder.merge-vertices",
    "probuilder.subdivide", "probuilder.weld-vertices", "reflection.method.call", "scene.view-frame"
  ]);
});

test("completion registry generator is idempotent", () => {
  const outputs = [
    path.join(root, "src", "tools", ".generated-completion-registry.ts"),
    path.join(root, "unitypkg", "Editor", "Generated", "UnityCliToolCompletionRegistry.g.cs")
  ];
  const before = outputs.map((file) => fs.readFileSync(file, "utf8"));
  const result = spawnSync(process.execPath, ["scripts/gen-tool-completion-registry.mjs"], { cwd: root, encoding: "utf8" });
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
  assert.deepEqual(outputs.map((file) => fs.readFileSync(file, "utf8")), before);
});

test("completion contract 不允许隐式 handler-returned 成功", () => {
  const contract = JSON.parse(fs.readFileSync(contractPath, "utf8")) as {
    tools: Array<Record<string, unknown>>;
  };
  const serialized = JSON.stringify(contract.tools);
  assert.doesNotMatch(serialized, /handler[- ]returned|ExecuteAsync returned/i);
  assert.doesNotMatch(serialized, /"executionClass":"default"/i);
});
