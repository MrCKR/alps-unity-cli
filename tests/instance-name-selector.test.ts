import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";
import { instanceNames, type UnityInstance } from "../src/instances.js";

/** 构造只包含名称解析所需字段的实例记录。 */
function instance(canonicalProjectPath: string): UnityInstance {
  const displayProjectPath = canonicalProjectPath;
  const identityHash = createHash("sha256").update(canonicalProjectPath, "utf8").digest("hex");
  return {
    instanceId: `unity-${identityHash}`,
    canonicalizationVersion: 1,
    canonicalProjectPath,
    displayProjectPath,
    projectPath: displayProjectPath,
    projectName: canonicalProjectPath.split("/").filter(Boolean).at(-1) ?? canonicalProjectPath,
    status: "ready",
    revision: 1,
    updatedAt: "2026-10-16T00:00:00.000Z",
    sessions: [],
    allSessions: [],
    progress: [],
    blockers: [],
    claimVerification: "verified",
  };
}

test("unique project names stay unchanged and selectable", () => {
  const alpha = instance("d:/work/alpha");
  const beta = instance("d:/work/beta");
  const names = instanceNames([alpha, beta]);
  assert.equal(names.get(alpha.instanceId), "alpha");
  assert.equal(names.get(beta.instanceId), "beta");
});

test("duplicate project names receive stable numeric suffixes ordered by canonical path", () => {
  const first = instance("d:/a/Shared");
  const second = instance("d:/b/Shared");
  const third = instance("d:/c/Shared");
  const names = instanceNames([third, first, second]);
  // canonicalProjectPath 字典序决定编号，输入顺序不影响结果。
  assert.equal(names.get(first.instanceId), "Shared");
  assert.equal(names.get(second.instanceId), "Shared-2");
  assert.equal(names.get(third.instanceId), "Shared-3");
});

test("duplicate suffixing is deterministic regardless of input order", () => {
  const instances = [instance("d:/x/Game"), instance("d:/y/Game"), instance("d:/z/Game")];
  const forward = instanceNames(instances);
  const backward = instanceNames([...instances].reverse());
  assert.deepEqual([...forward.entries()].sort(), [...backward.entries()].sort());
});

test("a literal suffixed folder name never collides with a generated suffix", () => {
  const plain = instance("d:/one/Game");
  const suffixed = instance("d:/two/Game-2");
  const other = instance("d:/three/Game");
  const names = instanceNames([plain, suffixed, other]);
  const values = [...names.values()];
  assert.equal(new Set(values).size, values.length, `names must stay unique: ${values.join(", ")}`);
  // 按 canonical path 排序后 Game 桶为 one、three；three 的候选项 Game-2 已被占用，因此顺延。
  assert.equal(names.get(plain.instanceId), "Game");
  assert.equal(names.get(other.instanceId), "Game-3");
  assert.equal(names.get(suffixed.instanceId), "Game-2");
});

test("offline records still consume their name so hidden instances cannot shift it", () => {
  const active = instance("d:/p/Project");
  const offline = instance("d:/q/Project");
  offline.status = "offline";
  const withOffline = instanceNames([active, offline]);
  assert.equal(withOffline.get(active.instanceId), "Project");
  assert.equal(withOffline.get(offline.instanceId), "Project-2");
});
