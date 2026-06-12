import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "./cli.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";

process.env.ALPS_UNITY_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "alps-unity-test-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

async function makeFakeUnity(body: string): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "fake-unity-"));
  const path = join(dir, "Unity.exe");
  await writeFile(path, `#!/usr/bin/env node\n${body}\n`, { mode: 0o755 });
  return path;
}

async function runCli(args: string[], env: NodeJS.ProcessEnv = {}): Promise<{ code: number | null; stdout: string; stderr: string }> {
  const child = spawn(process.execPath, [new URL("cli.js", import.meta.url).pathname, ...args], {
    env: { ...process.env, ALPS_UNITY_ALLOW_NON_WINDOWS: "1", ...env },
    stdio: ["ignore", "pipe", "pipe"]
  });

  let stdout = "";
  let stderr = "";
  child.stdout.on("data", (chunk) => {
    stdout += String(chunk);
  });
  child.stderr.on("data", (chunk) => {
    stderr += String(chunk);
  });

  const code = await new Promise<number | null>((resolve) => {
    child.on("close", resolve);
  });
  return { code, stdout, stderr };
}

test("test run returns a job and status reaches succeeded", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`
const fs = require("node:fs");
const logIndex = process.argv.indexOf("-logFile");
if (logIndex >= 0) fs.appendFileSync(process.argv[logIndex + 1], "tests ok\\n");
process.exit(0);
`);

  const created = await execute(["test", "run", "--unity", unity, "--project", runtime.dataDir], runtime);
  assert.equal(created.ok, true);
  const jobId = (created.data as { jobId: string }).jobId;

  const waited = await runtime.runner.wait(jobId, 3000);
  assert.equal(waited.status, "succeeded");

  const status = await execute(["job", "status", "--id", jobId], runtime);
  assert.equal((status.data as { status: string }).status, "succeeded");
});

test("job logs returns tail lines", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`
const fs = require("node:fs");
const logIndex = process.argv.indexOf("-logFile");
if (logIndex >= 0) fs.appendFileSync(process.argv[logIndex + 1], "one\\ntwo\\nthree\\n");
process.exit(0);
`);

  const created = await execute(["build", "run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  await runtime.runner.wait(jobId, 3000);

  const logs = await execute(["job", "logs", "--id", jobId, "--tail", "2"], runtime);
  assert.deepEqual((logs.data as { lines: string[] }).lines, ["two", "three"]);
});

test("wait timeout returns structured error while job keeps running", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 1000);`);

  const result = await execute(["editor", "refresh", "--compile", "--wait", "--wait-timeout-ms", "10", "--unity", unity, "--project", runtime.dataDir], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "WAIT_TIMEOUT");
});

test("cancel marks running job as cancelled", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 5000);`);

  const created = await execute(["build", "run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  await runtime.runner.wait(jobId, 100);

  const cancelled = await execute(["build", "cancel", "--id", jobId], runtime);
  assert.equal(cancelled.ok, true);
  assert.equal((cancelled.data as { status: string }).status, "cancelled");
});

test("failed Unity exit is captured with summary", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`process.exit(7);`);

  const created = await execute(["test", "run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  const waited = await runtime.runner.wait(jobId, 3000);

  assert.equal(waited.status, "failed");
  assert.equal(waited.exitCode, 7);
  assert.equal(waited.error?.code, "UNITY_BATCHMODE_FAILED");
});

test("test run attaches Unity XML result summary", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`
const fs = require("node:fs");
const resultIndex = process.argv.indexOf("-testResults");
if (resultIndex >= 0) fs.writeFileSync(process.argv[resultIndex + 1], '<test-run total="2" passed="1" failed="1" skipped="0"><test-case fullname="Example.Fail"><failure><message>boom &amp; fail</message></failure></test-case></test-run>');
process.exit(1);
`);

  const created = await execute(["test", "run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  const waited = await runtime.runner.wait(jobId, 3000);
  const summary = (waited.result?.summary ?? {}) as { total: number; failed: number; failures: Array<{ name: string; message: string }> };

  assert.equal(waited.status, "failed");
  assert.equal(summary.total, 2);
  assert.equal(summary.failed, 1);
  assert.deepEqual(summary.failures, [{ name: "Example.Fail", message: "boom & fail" }]);
});

test("build run forwards output path to batch method", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`process.exit(0);`);

  const created = await execute(["build", "run", "--unity", unity, "--project", runtime.dataDir, "--output", "Builds/App.exe"], runtime);
  const command = (created.data as { command: string[] }).command;

  assert.ok(command.includes("-alpsBuildOutput"));
  assert.equal(command[command.indexOf("-alpsBuildOutput") + 1].endsWith("Builds/App.exe"), true);
});

test("non-wait CLI exits quickly and worker persists terminal status", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "alps-unity-cli-process-"));
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 500);`);

  const created = await runCli(["editor", "refresh", "--compile", "--unity", unity, "--project", dataDir], { ALPS_UNITY_DATA_DIR: dataDir });
  assert.equal(created.code, 0, created.stderr);
  const payload = JSON.parse(created.stdout) as { ok: boolean; data: { jobId: string; status: string } };
  assert.equal(payload.ok, true);
  assert.equal(payload.data.status, "queued");

  const runtime = { dataDir, store: new JobStore(dataDir), runner: new JobRunner(new JobStore(dataDir), dataDir) };
  const waited = await runtime.runner.wait(payload.data.jobId, 3000);
  assert.equal(waited.status, "succeeded");
});

test("process timeout marks job as timeout", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 5000);`);

  const created = await execute(["editor", "refresh", "--compile", "--timeout-ms", "50", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  const waited = await runtime.runner.wait(jobId, 3000);

  assert.equal(waited.status, "timeout");
  assert.equal(waited.error?.code, "JOB_TIMEOUT");
});
