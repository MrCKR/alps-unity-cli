import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import type { BridgeRequest } from "../src/bridge/index.js";
import type { CliResult } from "../src/types.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

function dataOf(result: CliResult): unknown {
  assert.ok(result.ok);
  return result.data;
}

function errorDetailsOf(result: CliResult): unknown {
  assert.ok(!result.ok);
  return result.error.details;
}

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-batch-"));
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

  assert.ok(result.ok);
  const schema = dataOf(result) as { properties: Record<string, unknown> };
  assert.ok(schema.properties.defaults);
  assert.ok(schema.properties.steps);
  assert.ok(schema.properties.envAllowlist);
  assert.ok(schema.properties.failFast);
  assert.ok(schema.properties.fail_fast);
  const steps = schema.properties.steps as { items?: { properties?: Record<string, unknown> } };
  assert.ok(steps.items?.properties?.input);
  assert.ok(steps.items?.properties?.job);
});

test("batch run accepts inline JSON input", async () => {
  const runtime = await makeRuntime();
  const inline = JSON.stringify({
    steps: [{ id: "groups", command: ["tools", "run", "tools.group"] }]
  });

  const result = await execute(["batch", "run", "--input", inline, "--yes"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { batchPath: string; summary: { succeeded: number }; steps: Array<{ id: string; envelope: { ok: boolean } }> };
  assert.match(data.batchPath, /batches[\\/]inline[\\/]/);
  assert.equal(data.summary.succeeded, 1);
  assert.equal(data.steps[0].id, "groups");
  assert.equal(data.steps[0].envelope.ok, true);
});

test("batch step input is serialized as --input JSON", async () => {
  const runtime = await makeRuntime();
  const inputPath = "D:\\workspace\\alps-unity-cli\\Dev\\BridgeJobGateProject\\capture.raw";
  const batch = await writeBatch(runtime.dataDir, {
    steps: [{ id: "describe", command: ["tools", "run", "tools.describe"], input: { name: "profiler.capture", path: inputPath } }]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { steps: Array<{ command: string[] }> };
  const argv = data.steps[0].command;
  assert.ok(argv.includes("--input"));
  assert.deepEqual(JSON.parse(argv[argv.indexOf("--input") + 1]) as { name: string; path: string }, { name: "profiler.capture", path: inputPath });
});

test("batch step input does not override explicit input args", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    // 选 TS 本地工具,避免依赖 bridge 才能 describe 的 C# 工具。
    steps: [{ id: "describe", command: ["tools", "run", "tools.describe"], args: ["--input", JSON.stringify({ name: "tools.group" })], input: { name: "profiler.capture" } }]
  });

  const result = await execute(["batch", "run", batch, "--yes"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { steps: Array<{ envelope: { data: { name: string } } }> };
  assert.equal(data.steps[0].envelope.data.name, "tools.group");
});

test("batch step job appends --job for tools run steps", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    steps: [{ id: "capture", command: ["tools", "run", "profiler.capture"], job: true, input: { path: "profile.json" } }]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { steps: Array<{ command: string[] }> };
  const argv = data.steps[0].command;
  assert.ok(argv.includes("--job"));
  assert.equal(argv.filter((item) => item === "--job").length, 1);
  assert.ok(argv.includes("--input"));
});


test("batch run inline JSON reports parse errors", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["batch", "run", "--input", "{bad", "--yes"], runtime);

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "BATCH_INPUT_JSON_INVALID");
});

test("batch fail_fast false allows independent ready steps to continue", async () => {
  const runtime = await makeRuntime();
  const inline = JSON.stringify({
    fail_fast: false,
    steps: [
      { id: "missing", command: ["jobs", "status", "missing"] },
      { id: "groups", command: ["tools", "run", "tools.group"] }
    ]
  });

  const result = await execute(["batch", "run", "--input", inline, "--yes"], runtime);

  assert.ok(!result.ok);
  const data = result.error.details as { failFast: boolean; summary: { failed: number; succeeded: number; skipped: number } };
  assert.equal(data.failFast, false);
  assert.equal(data.summary.failed, 1);
  assert.equal(data.summary.succeeded, 1);
  assert.equal(data.summary.skipped, 0);
});

test("batch run orders steps by dependsOn and aggregates envelopes", async () => {
  const runtime = await makeRuntime();
  const unity = await writeFakeUnity(runtime.dataDir, "process.exit(0);");
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { project: ".", unity },
    steps: [
      { id: "test", command: ["tools", "run", "test.run"], dependsOn: ["refresh"] },
      { id: "refresh", command: ["tools", "run", "editor.refresh"], args: ["--compile"] }
    ]
  });

  const result = await execute(["batch", "run", batch, "--yes"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { summary: { succeeded: number }; steps: Array<{ id: string; jobId?: string; logPath?: string; envelope: { ok: boolean } }>; logIndex: { steps: Array<{ jobId?: string; logPath?: string }> } };
  assert.equal(data.summary.succeeded, 2);
  assert.deepEqual(data.steps.map((step) => step.id), ["test", "refresh"]);
  assert.equal(data.steps.every((step) => step.envelope.ok), true);
  assert.equal(data.steps.every((step) => typeof step.jobId === "string"), true);
  assert.equal(data.logIndex.steps.every((step) => typeof step.logPath === "string"), true);
});

test("batch can orchestrate long job status logs and cancel chain", async () => {
  const runtime = await makeRuntime();
  const unity = await writeFakeUnity(runtime.dataDir, `
// With -logFile -, Unity writes its log to stdout. The runner captures stdout
// and writes it to the job's logPath.
process.stdout.write("build started\\n");
setTimeout(() => process.exit(0), 15000);
`);
  const created = await execute(["tools", "run", "build.run", "--yes", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (dataOf(created) as { jobId: string }).jobId;
  let readyForBatch = false;
  for (let attempt = 0; attempt < 300; attempt += 1) {
    const job = await runtime.store.get(jobId);
    const logs = await runtime.runner.logs(jobId);
    if (job?.status === "running" && logs.lines.includes("build started")) {
      readyForBatch = true;
      break;
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  assert.equal(readyForBatch, true, "build job should be running and have emitted its first log before batch status/logs/cancel checks");
  const batch = await writeBatch(runtime.dataDir, {
    steps: [
      { id: "status", command: ["jobs", "status", jobId] },
      { id: "logs", command: ["jobs", "logs", jobId], args: ["--tail", "10"], dependsOn: ["status"] },
      { id: "cancel", command: ["jobs", "cancel", jobId], args: ["--yes"], dependsOn: ["logs"] }
    ]
  });

  const result = await execute(["batch", "run", batch, "--fail-fast", "true", "--yes"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { summary: { succeeded: number }; steps: Array<{ id: string; envelope: { data: { status?: string; lines?: string[]; cancellation?: { mode: string } } } }> };
  assert.equal(data.summary.succeeded, 3);
  assert.equal(data.steps.find((step) => step.id === "status")?.envelope.data.status, "running");
  const logLines = data.steps.find((step) => step.id === "logs")?.envelope.data.lines ?? [];
  assert.ok(logLines.includes("build started"), `expected logs to include 'build started', got ${JSON.stringify(logLines)}`);
  assert.equal(data.steps.find((step) => step.id === "cancel")?.envelope.data.status, "cancelled");
  assert.equal(data.steps.find((step) => step.id === "cancel")?.envelope.data.cancellation?.mode, "immediate");
});

test("batch runs query edit test and screenshot commands from one TS registry", async () => {
  const runtime = await makeRuntime();
  const bridgeRequests: BridgeRequest[] = [];
  const unity = await writeFakeUnity(runtime.dataDir, "process.exit(0);");
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { project: ".", unity },
    steps: [
      { id: "query-player", command: ["tools", "run", "go.find"], args: ["--name", "Player"] },
      { id: "edit-player", command: ["tools", "run", "component.set", "Player"], args: ["--component", "Camera", "--property", "enabled", "--value", "false"], dependsOn: ["query-player"] },
      { id: "run-tests", command: ["tools", "run", "test.run"], args: ["--platform", "EditMode"], dependsOn: ["edit-player"] },
      { id: "camera", command: ["tools", "run", "camera.list"] },
      { id: "capture", command: ["tools", "run", "camera.screenshot", "Main Camera"], args: ["--output", "Artifacts/player.png", "--overwrite", "--yes"], dependsOn: ["run-tests", "camera"] }
    ]
  });
  const bridgeRuntime = {
    ...runtime,
    projectTools: ["go.find", "component.set", "camera.list", "camera.screenshot"].map((name) => ({
      name, summary: `${name} tool`, description: `${name} tool`, group: name.split(".")[0], source: "built-in" as const,
      target: "editor" as const, playModeSupport: "supported" as const, inputSchema: { type: "object" as const, properties: {}, additionalProperties: true },
      outputSchema: { type: "object" as const, properties: {}, additionalProperties: true }, destructiveHint: name === "camera.screenshot",
      ...(name === "camera.screenshot" ? { liveDescriptorFingerprint: "b".repeat(64) } : {})
    })),
    bridgeExecute: async (payload: BridgeRequest) => {
      bridgeRequests.push(payload);
      const toolName = payload.input!.name as string;
      const input = (payload.input!.input ?? {}) as Record<string, unknown>;
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: {
          plannedEffects: toolName.includes("set") || toolName.includes("screenshot") ? [{ type: toolName, args: input }] : [],
          items: toolName === "go.find" ? [{ name: "Player" }] : undefined,
          cameras: toolName === "camera.list" ? [{ name: "Main Camera" }] : undefined,
          screenshotPath: toolName === "camera.screenshot" ? input.path : undefined
        }
      };
    }
  };

  const result = await execute(["batch", "run", batch, "--yes"], bridgeRuntime);

  assert.ok(result.ok, JSON.stringify(result));
  const data = dataOf(result) as { summary: { succeeded: number }; plannedEffects: Array<{ type: string }>; steps: Array<{ id: string; jobId?: string; envelope: { data: { command?: string } } }> };
  assert.equal(data.summary.succeeded, 5);
  assert.deepEqual(bridgeRequests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(bridgeRequests[1].input!, { name: "component.set", input: { target: "Player", component: "Camera", property: "enabled", value: "false" } });
  assert.ok(typeof data.steps.find((step) => step.id === "run-tests")?.jobId === "string");
  assert.deepEqual(data.plannedEffects.map((effect) => effect.type), ["component.set", "camera.screenshot"]);
});

test("batch preflight accepts query edit test and screenshot example", async () => {
  const runtime = await makeRuntime();
  const unity = await writeFakeUnity(runtime.dataDir, "process.exit(0);");
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { project: ".", unity },
    steps: [
      { id: "query", command: ["tools", "run", "asset.search"], args: ["--query", "Player", "--type", "Prefab"] },
      { id: "edit", command: ["tools", "run", "material.set", "Assets/Materials/Player.mat"], args: ["--property", "_Color", "--value", "1,0,0,1"], dependsOn: ["query"] },
      { id: "test", command: ["tools", "run", "test.run"], args: ["--platform", "EditMode"], dependsOn: ["edit"] },
      { id: "code", command: ["tools", "run", "code.exec"], args: ["--code", "return 1;"], dependsOn: ["edit"] },
      { id: "camera", command: ["tools", "run", "camera.info", "Main Camera"] },
      { id: "compile", command: ["tools", "run", "editor.compile"], input: { refresh: false, waitReady: false } },
      { id: "compile-diagnostics", command: ["tools", "run", "editor.compile-diagnostics"], input: { refresh: false, waitReady: false, count: 20 } },
      { id: "launch-dialog", command: ["tools", "run", "editor.launch-dialog.dismiss"], input: { timeoutMs: 0 } },
      { id: "shader", command: ["tools", "run", "shader.read", "Assets/Shaders/Unlit.shader"] },
      { id: "texture", command: ["tools", "run", "texture.import-settings", "Assets/Textures/Icon.png"], args: ["--readable", "false"], dependsOn: ["shader"] },
      { id: "screenshot", command: ["tools", "run", "screenshot.scene-view", "Artifacts/after.png"], dependsOn: ["test", "camera", "texture"] }
    ]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { preflight: Array<{ code: string }>; steps: Array<{ id: string; parallelSafe: boolean }> };
  assert.deepEqual(data.preflight.map((item) => item.code), ["BATCH_PREFLIGHT_OK"]);
  assert.deepEqual(data.steps.map((step) => step.id), ["query", "edit", "test", "code", "camera", "compile", "compile-diagnostics", "launch-dialog", "shader", "texture", "screenshot"]);
  assert.equal(data.steps.find((step) => step.id === "code")?.parallelSafe, false);
  assert.equal(data.steps.find((step) => step.id === "compile")?.parallelSafe, false);
  assert.equal(data.steps.find((step) => step.id === "compile-diagnostics")?.parallelSafe, false);
  assert.equal(data.steps.find((step) => step.id === "launch-dialog")?.parallelSafe, false);
  assert.equal(data.steps.find((step) => step.id === "shader")?.parallelSafe, true);
  assert.equal(data.steps.find((step) => step.id === "texture")?.parallelSafe, false);
});

test("batch preflight rejects unknown and raw commands", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    steps: [
      { id: "raw", command: ["tools", "run", "raw.mcp"], args: ["{}"] },
      { id: "unknown", command: ["mcp", "call"] }
    ]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.ok(!result.ok);
  assert.equal(result.error.code, "BATCH_PREFLIGHT_FAILED");
  const data = result.error.details as { preflight: Array<{ code: string }> };
  assert.ok(data.preflight.some((item) => item.code === "BATCH_RAW_COMMAND_FORBIDDEN"));
  assert.ok(data.preflight.some((item) => item.code === "BATCH_COMMAND_NOT_ALLOWED"));
});

test("batch preflight marks read commands parallel-safe and writes serial", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    parallel: 2,
    steps: [
      { id: "read-material", command: ["tools", "run", "material.info", "Assets/Materials/Player.mat"] },
      { id: "read-animation", command: ["tools", "run", "animation.clip", "Assets/Animations/Idle.anim"] },
      { id: "write-animation", command: ["tools", "run", "animation.clip.create", "Assets/Animations/Run.anim"], args: ["--yes"] },
      { id: "modify-animation", command: ["tools", "run", "animation.clip.modify", "Assets/Animations/Run.anim"], args: ["--yes", "--loop-time", "true"] },
      { id: "write-vfx", command: ["tools", "run", "vfx.effect.add", "Effects/Emitter"] },
      { id: "write-material", command: ["tools", "run", "material.set", "Assets/Materials/Player.mat"], args: ["--property", "_Color", "--value", "#ffffffff"] }
    ]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.ok(!result.ok);
  const data = errorDetailsOf(result) as { steps: Array<{ id: string; parallelSafe: boolean }>; preflight: Array<{ code: string; stepId?: string }> };
  assert.equal(data.steps.find((step) => step.id === "read-material")?.parallelSafe, true);
  assert.equal(data.steps.find((step) => step.id === "read-animation")?.parallelSafe, true);
  assert.equal(data.steps.find((step) => step.id === "write-animation")?.parallelSafe, false);
  assert.equal(data.steps.find((step) => step.id === "modify-animation")?.parallelSafe, false);
  assert.equal(data.steps.find((step) => step.id === "write-vfx")?.parallelSafe, false);
  assert.equal(data.steps.find((step) => step.id === "write-material")?.parallelSafe, false);
  assert.ok(data.preflight.some((item) => item.code === "BATCH_PARALLEL_CONFLICT" && item.stepId === "write-animation"));
  assert.ok(data.preflight.some((item) => item.code === "BATCH_PARALLEL_CONFLICT" && item.stepId === "write-vfx"));
  assert.ok(data.preflight.some((item) => item.code === "BATCH_PARALLEL_CONFLICT" && item.stepId === "write-material"));
});

test("batch parallel rejects commands not declared parallel-safe", async () => {
  const runtime = await makeRuntime();
  const unity = await writeFakeUnity(runtime.dataDir, "process.exit(0);");
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { project: ".", unity },
    steps: [{ id: "build", command: ["tools", "run", "build.run"] }]
  });

  const result = await execute(["batch", "run", batch, "--parallel", "2", "--preflight"], runtime);

  assert.ok(!result.ok);
  const data = errorDetailsOf(result) as { preflight: Array<{ code: string }> };
  assert.ok(data.preflight.some((item) => item.code === "BATCH_PARALLEL_CONFLICT"));
});

test("batch fail-fast skips remaining ready steps after a failure", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    steps: [
      { id: "missing-job", command: ["jobs", "status", "missing"] },
      { id: "logs", command: ["jobs", "logs", "missing"] }
    ]
  });

  const result = await execute(["batch", "run", batch, "--yes"], runtime);

  assert.ok(!result.ok);
  const data = errorDetailsOf(result) as { summary: { failed: number; skipped: number }; steps: Array<{ id: string; status: string; error?: { code: string } }> };
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
    steps: [{ id: "refresh", command: ["tools", "run", "editor.refresh"], args: ["--compile"] }]
  });

  const result = await execute(["batch", "run", batch, "--yes"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { steps: Array<{ envelope: { data: { projectPath: string } } }> };
  assert.equal(data.steps[0].envelope.data.projectPath, runtime.dataDir);
});

test("batch env entries must be explicitly allowlisted", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { env: { UNITY_EXE: "C:/Unity/Unity.exe" } },
    steps: [{ id: "status", command: ["jobs", "status", "missing"] }]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.ok(!result.ok);
  const data = errorDetailsOf(result) as { preflight: Array<{ code: string }> };
  assert.ok(data.preflight.some((item) => item.code === "BATCH_ENV_NOT_ALLOWED"));
});

test("batch editor play/stop/state and tools run are registered commands", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { project: ".", unity: "/path/to/Unity" },
    steps: [
      { id: "enter-play", command: ["tools", "run", "editor.play"] },
      { id: "check-state", command: ["tools", "run", "editor.state"], "dependsOn": ["enter-play"] },
      { id: "run-echo", command: ["tools", "run", "sample.echo"], "dependsOn": ["check-state"] },
      { id: "exit-play", command: ["tools", "run", "editor.stop"], "dependsOn": ["run-echo"] }
    ]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { preflight: Array<{ code: string }> };
  assert.ok(data.preflight.some((item) => item.code === "BATCH_PREFLIGHT_OK"));
});

test("batch playModeSessionId and fingerprintMode are converted to CLI flags", async () => {
  const runtime = await makeRuntime();
  const batch = await writeBatch(runtime.dataDir, {
    defaults: { project: ".", unity: "/path/to/Unity" },
    steps: [
      {
        id: "runtime-tool",
        command: ["tools", "run"],
        args: ["sample.runtime-echo"],
        playModeSessionId: "session_test_001",
        fingerprintMode: "strict"
      }
    ]
  });

  const result = await execute(["batch", "run", batch, "--preflight"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { steps: Array<{ command: string[] }> };
  const argv = data.steps[0].command;
  assert.ok(argv.includes("--play-mode-session-id"));
  assert.ok(argv.includes("session_test_001"));
  assert.ok(argv.includes("--fingerprint-mode"));
  assert.ok(argv.includes("strict"));
});

async function writeFakeUnity(dataDir: string, body: string): Promise<string> {
  if (process.platform === "win32") {
    const script = join(dataDir, "UnityFake.js");
    const path = join(dataDir, "Unity.cmd");
    await writeFile(script, body, "utf8");
    await writeFile(path, `@echo off\r\n"${process.execPath}" "%~dp0UnityFake.js" %*\r\nif errorlevel 1 exit /b %errorlevel%\r\n`, "utf8");
    return path;
  }
  const path = join(dataDir, "Unity.exe");
  await writeFile(path, `#!/usr/bin/env node\n${body}\n`, { mode: 0o755 });
  return path;
}
