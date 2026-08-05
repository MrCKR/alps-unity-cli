import assert from "node:assert/strict";
import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { execute } from "../src/cli.js";
import type { BridgeRequest } from "../src/bridge/index.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

/** 为 direct CLI 安全测试建立隔离数据目录。 */
async function runtime(overrides: Record<string, unknown> = {}) {
  return { dataDir: await mkdtemp(join(tmpdir(), "unity-cli-security-")), ...overrides } as never;
}

test("destructive Tool calls dispatch without confirmation", async () => {
  const requests: BridgeRequest[] = [];
  const result = await execute([
    "tools", "run", "asset.delete", "--input", "{\"path\":\"Assets/DeleteMe.asset\"}",
  ], await runtime({
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: { result: { deleted: true, path: "Assets/DeleteMe.asset" } },
      };
    },
  }));

  assert.ok(result.ok);
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "project-tool.run");
  assert.equal(requests[0].input?.name, "asset.delete");
  assert.equal(Object.hasOwn(requests[0].execution, "securityProof"), false);
});

test("dry-run remains an optional execution mode without a confirmation proof", async () => {
  const requests: BridgeRequest[] = [];
  const result = await execute([
    "tools", "run", "asset.delete", "--input", "{\"path\":\"Assets/DeleteMe.asset\"}", "--dry-run",
  ], await runtime({
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: { result: { dryRun: true, plannedEffects: [{ type: "asset.delete", path: "Assets/DeleteMe.asset" }] } },
      };
    },
  }));

  assert.ok(result.ok);
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "project-tool.run");
  assert.equal(requests[0].input?.name, "asset.delete");
  assert.equal(requests[0].execution?.dryRun, true);
  assert.equal(Object.hasOwn(requests[0].execution, "securityProof"), false);
});

test("Unity tool executors preserve dry-run without a confirmation gate", async () => {
  const sources = await Promise.all([
    readFile(resolve("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs"), "utf8"),
    readFile(resolve("unitypkg/Editor/Infrastructure/UnityCliRuntimeExecutor.cs"), "utf8"),
  ]);

  for (const source of sources) {
    assert.match(source, /var dryRun = request\.Execution != null && request\.Execution\.DryRun == true;/);
    assert.doesNotMatch(source, /SecurityConfirmed|E_DESTRUCTIVE_CONFIRMATION_REQUIRED|securityProof/i);
  }
});
