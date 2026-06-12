import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "./cli.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";
import type { BridgeRequest } from "./bridge-commands.js";

process.env.ALPS_UNITY_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "alps-unity-batch-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

async function writeBatch(dataDir: string, batch: unknown): Promise<string> {
  const filePath = join(dataDir, "batch.json");
  await writeFile(filePath, `${JSON.stringify(batch, null, 2)}\n`, "utf8");
  return filePath;
}

test("batch schema exposes defaults, steps and env allowlist", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["batch", "schema"], runtime);

  assert.equal(result.ok, true);
  const schema = result.data as { properties: Record<string, unknown> };
  assert.ok(schema.properties.defaults);
  assert.ok(schema.properties.steps);
  assert.ok(schema.properties.envAllowlist);
});

test("batch run orders steps by dependsOn and aggregates envelopes", async () => {
  const runtime = await makeRuntime();
  const unity = await writeFakeUnity(runtime.dataDir, "process.exit(0);");
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { project: ".", unity },
    steps: [
      { id: "test", command: ["test", "run"], dependsOn: ["refresh"] },
      { id: "refresh", command: ["editor", "refresh"], args: ["--compile"] }
    ]
  });

  const result = await execute(["batch", "run", batch], runtime);

  assert.equal(result.ok, true);
  const data = result.data as { summary: { succeeded: number }; steps: Array<{ id: string; jobId?: string; logPath?: string; envelope: { ok: boolean } }>; logIndex: { steps: Array<{ jobId?: string; logPath?: string }> } };
  assert.equal(data.summary.succeeded, 2);
  assert.deepEqual(data.steps.map((step) => step.id), ["test", "refresh"]);
  assert.equal(data.steps.every((step) => step.envelope.ok), true);
  assert.equal(data.steps.every((step) => typeof step.jobId === "string"), true);
  assert.equal(data.logIndex.steps.every((step) => typeof step.logPath === "string"), true);
});

test("batch runs query edit test and screenshot commands from one TS registry", async () => {
  const runtime = await makeRuntime();
  const bridgeRequests: BridgeRequest[] = [];
  const unity = await writeFakeUnity(runtime.dataDir, "process.exit(0);");
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { project: ".", unity },
    steps: [
      { id: "query-player", command: ["go", "find"], args: ["--name", "Player"] },
      { id: "edit-player", command: ["component", "set", "Player"], args: ["--component", "Camera", "--property", "enabled", "--value", "false"], dependsOn: ["query-player"] },
      { id: "run-tests", command: ["test", "run"], args: ["--platform", "EditMode"], dependsOn: ["edit-player"] },
      { id: "camera", command: ["camera", "list"] },
      { id: "capture", command: ["camera", "screenshot", "Main Camera"], args: ["--output", "Artifacts/player.png", "--overwrite"], dependsOn: ["run-tests", "camera"] }
    ]
  });
  const bridgeRuntime = {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      bridgeRequests.push(payload);
      return {
        ok: true,
        data: {
          plannedEffects: payload.command.includes("set") || payload.command.includes("screenshot") ? [{ type: payload.command, args: payload.args }] : [],
          items: payload.command === "go.find" ? [{ name: "Player" }] : undefined,
          cameras: payload.command === "camera.list" ? [{ name: "Main Camera" }] : undefined,
          screenshotPath: payload.command === "camera.screenshot" ? payload.args.path : undefined
        }
      };
    }
  };

  const result = await execute(["batch", "run", batch], bridgeRuntime);

  assert.equal(result.ok, true);
  const data = result.data as { summary: { succeeded: number }; plannedEffects: Array<{ type: string }>; steps: Array<{ id: string; jobId?: string; envelope: { data: { command?: string } } }> };
  assert.equal(data.summary.succeeded, 5);
  assert.deepEqual(bridgeRequests.map((request) => request.command), ["go.find", "component.set", "camera.list", "camera.screenshot"]);
  assert.deepEqual(bridgeRequests[1].args, { target: "Player", component: "Camera", property: "enabled", value: "false" });
  assert.ok(typeof data.steps.find((step) => step.id === "run-tests")?.jobId === "string");
  assert.deepEqual(data.plannedEffects.map((effect) => effect.type), ["component.set", "camera.screenshot"]);
});

test("batch preflight accepts query edit test and screenshot example", async () => {
  const runtime = await makeRuntime();
  const unity = await writeFakeUnity(runtime.dataDir, "process.exit(0);");
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { project: ".", unity },
    steps: [
      { id: "query", command: ["asset", "search"], args: ["--query", "Player", "--type", "Prefab"] },
      { id: "edit", command: ["script", "edit", "Assets/Scripts/Player.cs"], args: ["--find", "speed = 1", "--replace", "speed = 2"], dependsOn: ["query"] },
      { id: "test", command: ["test", "run"], args: ["--platform", "EditMode"], dependsOn: ["edit"] },
      { id: "camera", command: ["camera", "info", "Main Camera"] },
      { id: "screenshot", command: ["scene", "screenshot", "Artifacts/after.png"], dependsOn: ["test", "camera"] }
    ]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.equal(result.ok, true);
  const data = result.data as { preflight: Array<{ code: string }>; steps: Array<{ id: string; parallelSafe: boolean }> };
  assert.deepEqual(data.preflight.map((item) => item.code), ["BATCH_PREFLIGHT_OK"]);
  assert.deepEqual(data.steps.map((step) => step.id), ["query", "edit", "test", "camera", "screenshot"]);
});

test("batch preflight rejects unknown and raw commands", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    steps: [
      { id: "raw", command: ["raw", "mcp"], args: ["{}"] },
      { id: "unknown", command: ["mcp", "call"] }
    ]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "BATCH_PREFLIGHT_FAILED");
  const data = result.data as { preflight: Array<{ code: string }> };
  assert.ok(data.preflight.some((item) => item.code === "BATCH_RAW_COMMAND_FORBIDDEN"));
  assert.ok(data.preflight.some((item) => item.code === "BATCH_COMMAND_NOT_ALLOWED"));
});

test("batch parallel rejects commands not declared parallel-safe", async () => {
  const runtime = await makeRuntime();
  const unity = await writeFakeUnity(runtime.dataDir, "process.exit(0);");
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { project: ".", unity },
    steps: [{ id: "build", command: ["build", "run"] }]
  });

  const result = await execute(["batch", "run", batch, "--parallel", "2", "--preflight"], runtime);

  assert.equal(result.ok, false);
  const data = result.data as { preflight: Array<{ code: string }> };
  assert.ok(data.preflight.some((item) => item.code === "BATCH_PARALLEL_CONFLICT"));
});

test("batch fail-fast skips remaining ready steps after a failure", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    steps: [
      { id: "missing-job", command: ["job", "status"], args: ["--id", "missing"] },
      { id: "logs", command: ["job", "logs"], args: ["--id", "missing"] }
    ]
  });

  const result = await execute(["batch", "run", batch], runtime);

  assert.equal(result.ok, false);
  const data = result.data as { summary: { failed: number; skipped: number }; steps: Array<{ id: string; status: string; error?: { code: string } }> };
  assert.equal(data.summary.failed, 1);
  assert.equal(data.summary.skipped, 1);
  assert.equal(data.steps.find((step) => step.id === "logs")?.status, "skipped");
  assert.equal(data.steps.find((step) => step.id === "logs")?.error?.code, "BATCH_FAIL_FAST");
});

test("batch defaults cwd is used to resolve relative project paths", async () => {
  const runtime = await makeRuntime();
  const unity = await writeFakeUnity(runtime.dataDir, "process.exit(0);");
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { cwd: ".", project: ".", unity },
    steps: [{ id: "refresh", command: ["editor", "refresh"], args: ["--compile"] }]
  });

  const result = await execute(["batch", "run", batch], runtime);

  assert.equal(result.ok, true);
  const data = result.data as { steps: Array<{ envelope: { data: { projectPath: string } } }> };
  assert.equal(data.steps[0].envelope.data.projectPath, runtime.dataDir);
});

test("batch env entries must be explicitly allowlisted", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { env: { UNITY_EXE: "C:/Unity/Unity.exe" } },
    steps: [{ id: "status", command: ["job", "status"], args: ["--id", "missing"] }]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.equal(result.ok, false);
  const data = result.data as { preflight: Array<{ code: string }> };
  assert.ok(data.preflight.some((item) => item.code === "BATCH_ENV_NOT_ALLOWED"));
});

async function writeFakeUnity(dataDir: string, body: string): Promise<string> {
  const path = join(dataDir, "Unity.exe");
  await writeFile(path, `#!/usr/bin/env node\n${body}\n`, { mode: 0o755 });
  return path;
}
