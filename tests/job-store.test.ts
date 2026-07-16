import assert from "node:assert/strict";
import { mkdtemp, readdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { isJobStoreLockContentionError, JobStore } from "../src/job-store.js";
import type { JobRecord } from "../src/types.js";

const makeJob = (id: string): JobRecord => {
  const now = new Date().toISOString();
  return {
    id,
    kind: "code",
    status: "queued",
    requestId: `request-${id}`,
    createdAt: now,
    updatedAt: now,
    projectPath: "D:/project",
    unityPath: "C:/Unity/Unity.exe",
    logPath: `D:/logs/${id}.log`,
    command: ["Unity.exe"]
  };
};

test("JobStore recognizes Windows exclusive-create sharing conflicts as lock contention", () => {
  for (const code of ["EEXIST", "EPERM", "EACCES", "EBUSY"]) {
    assert.equal(isJobStoreLockContentionError(Object.assign(new Error(code), { code })), true);
  }
  assert.equal(isJobStoreLockContentionError(Object.assign(new Error("missing"), { code: "ENOENT" })), false);
});

test("JobStore preserves concurrent cross-instance mutations", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-job-store-"));
  const stores = Array.from({ length: 8 }, () => new JobStore(dataDir));
  const jobs = Array.from({ length: 40 }, (_, index) => makeJob(`job-${index}`));

  await Promise.all(jobs.map((job, index) => stores[index % stores.length].create(job)));
  const created = await stores[0].list();
  assert.equal(created.length, jobs.length);
  assert.deepEqual(new Set(created.map((job) => job.id)), new Set(jobs.map((job) => job.id)));

  await Promise.all(jobs.map((job, index) => stores[index % stores.length].update(job.id, { status: "running", pid: 10_000 + index })));
  const updated = await stores[0].list();
  assert.equal(updated.filter((job) => job.status === "running").length, jobs.length);
  assert.equal(updated.every((job) => typeof job.pid === "number"), true);

  const cancelled = await stores[0].markTerminal("job-0", "cancelled");
  assert.equal(cancelled.status, "cancelled");
  const lateFailure = await stores[1].markTerminal("job-0", "failed");
  assert.equal(lateFailure.status, "cancelled", "terminal state must not be overwritten by a late worker result");
  const lateAttach = await stores[2].transition("job-0", ["running"], { pid: 99_999 });
  assert.equal(lateAttach.changed, false);
  assert.equal(lateAttach.job.status, "cancelled");

  const cancelling = await stores[0].transition("job-1", ["running"], { status: "cancelling" });
  assert.equal(cancelling.changed, true);
  const closeRace = await stores[1].markTerminal("job-1", "failed");
  assert.equal(closeRace.status, "cancelling", "worker close must not override a claimed cancellation");
  const cancelledAfterKill = await stores[2].transition("job-1", ["cancelling"], { status: "cancelled" });
  assert.equal(cancelledAfterKill.changed, true);
  assert.equal(cancelledAfterKill.job.status, "cancelled");

  const files = await readdir(dataDir);
  assert.deepEqual(files.filter((file) => file.endsWith(".lock") || file.endsWith(".tmp")), []);
});
