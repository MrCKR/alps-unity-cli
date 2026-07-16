import { createHash, randomUUID } from "node:crypto";
import { createWriteStream } from "node:fs";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { join, relative } from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import type { JobRecord, JobStatus, RunJobOptions } from "./types.js";
import { JobStore } from "./job-store.js";
import { readUnityTestSummary } from "./test-results.js";

const terminalStatuses: JobStatus[] = ["succeeded", "failed", "cancelled", "timeout"];

/** Default process timeout when none is provided. Unity builds can be slow, but never-ending
 * jobs are worse — orphan processes must be killed. 30 minutes matches the job_wait max category. */
const DEFAULT_JOB_TIMEOUT_MS = 1_800_000;

/** Number of trailing log lines to embed in the job result so callers can see the failure
 * reason without a separate `job logs` round-trip. */
const EMBEDDED_LOG_TAIL = 40;

export class JobRunner {
  constructor(
    private readonly store: JobStore,
    private readonly dataDir: string
  ) {}

  async start(options: RunJobOptions): Promise<JobRecord> {
    const now = new Date().toISOString();
    const id = `job_${randomUUID()}`;
    const logDir = join(this.dataDir, "logs");
    const logPath = join(logDir, `${id}.log`);
    const command = [options.unityPath, "-batchmode", "-nographics", "-quit", "-projectPath", options.projectPath, "-logFile", "-", ...options.args];

    await mkdir(logDir, { recursive: true });
    const job = await this.store.create({
      id,
      kind: options.kind,
      status: "queued",
      requestId: options.requestId,
      createdAt: now,
      updatedAt: now,
      projectPath: options.projectPath,
      unityPath: options.unityPath,
      logPath,
      timeoutMs: options.timeoutMs,
      command,
      result: options.result
    });

    this.spawnWorker(job.id);
    return job;
  }

  async wait(id: string, timeoutMs: number): Promise<JobRecord> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
      const job = await this.store.get(id);
      if (!job) {
        throw new Error(`Job not found: ${id}`);
      }
      if (terminalStatuses.includes(job.status)) {
        return job;
      }
      await new Promise((resolve) => setTimeout(resolve, 200));
    }

    const job = await this.store.get(id);
    if (!job) {
      throw new Error(`Job not found: ${id}`);
    }
    return job;
  }

  async cancel(id: string): Promise<JobRecord> {
    const claimed = await this.store.transition(id, ["queued", "running"], { status: "cancelling" });
    if (!claimed.changed && terminalStatuses.includes(claimed.job.status)) {
      throw new Error(`Job is already terminal: ${claimed.job.status}`);
    }

    // 先原子声明取消所有权，再等待进程树退出；worker 的 close/timeout 只能结束 queued/running，不能抢先发布 failed。
    const processKilled = claimed.job.pid ? await killProcessTree(claimed.job.pid) : false;
    const completed = await this.store.transition(id, ["cancelling"], {
      status: "cancelled",
      finishedAt: new Date().toISOString(),
      pid: undefined,
      error: { code: "JOB_CANCELLED", message: "Job was cancelled.", details: timeoutDetails(true, true, processKilled, false) }
    });
    return completed.job;
  }

  async logs(id: string, tail = 100): Promise<{ logPath: string; lines: string[] }> {
    const job = await this.store.get(id);
    if (!job) {
      throw new Error(`Job not found: ${id}`);
    }

    try {
      const raw = await readFile(job.logPath, "utf8");
      const lines = raw.split(/\r?\n/).filter(Boolean);
      return { logPath: job.logPath, lines: lines.slice(Math.max(0, lines.length - tail)) };
    } catch (error) {
      const nodeError = error as NodeJS.ErrnoException;
      if (nodeError.code === "ENOENT") {
        return { logPath: job.logPath, lines: [] };
      }
      throw error;
    }
  }

  async runWorker(id: string): Promise<JobRecord> {
    const job = await this.store.get(id);
    if (!job) {
      throw new Error(`Job not found: ${id}`);
    }
    if (terminalStatuses.includes(job.status)) {
      return job;
    }
    return this.spawnJob(job);
  }

  private spawnWorker(id: string): void {
    const cliPath = fileURLToPath(new URL("cli.js", import.meta.url));
    const child = spawn(process.execPath, [cliPath, "__run-job", "--id", id, "--data-dir", this.dataDir], {
      detached: true,
      stdio: "ignore",
      windowsHide: true
    });

    // worker 自行持久化 job 状态和 Unity 日志；父 CLI 不持有任何子进程 stream。
    child.unref();
  }

  private async spawnJob(job: JobRecord): Promise<JobRecord> {
    // 先原子 claim queued，避免 cancel 已发布 terminal 后 worker 又把状态写回 running。
    const claimed = await this.store.transition(job.id, ["queued"], {
      status: "running",
      startedAt: new Date().toISOString()
    });
    if (!claimed.changed) return claimed.job;

    const child = spawn(job.unityPath, job.command.slice(1), { windowsHide: true, stdio: ["ignore", "pipe", "pipe"], shell: isWindowsCommandScript(job.unityPath) });

    // Unity -logFile - writes all output to stdout. Pipe stdout to the log file AND
    // accumulate in memory so the last N lines can be embedded in the result.
    const logStream = createWriteStream(job.logPath, { flags: "a" });
    const logChunks: Buffer[] = [];
    child.stdout.on("data", (chunk: Buffer) => {
      logChunks.push(chunk);
      logStream.write(chunk);
    });
    child.stderr.on("data", (chunk: Buffer) => {
      logChunks.push(chunk);
      logStream.write(chunk);
    });

    // Close logStream when the process finishes; also write accumulated log to a
    // snapshot so logs() can always read from disk.
    const closeLogStream = (): void => {
      logStream.end();
    };

    const attached = await this.store.transition(job.id, ["running"], { pid: child.pid });
    if (!attached.changed) {
      await killProcessTree(child.pid);
      closeLogStream();
      return attached.job;
    }

    // Always apply an internal timeout — orphan processes are unacceptable.
    const effectiveTimeout = job.timeoutMs && job.timeoutMs > 0 ? job.timeoutMs : DEFAULT_JOB_TIMEOUT_MS;

    let timeoutHandle: NodeJS.Timeout | undefined;
    let didTimeout = false;
    timeoutHandle = setTimeout(() => {
      didTimeout = true;
      void (async () => {
        const processKilled = await killProcessTree(child.pid);
        try {
          await this.store.markTerminal(job.id, "timeout", {
            exitCode: null,
            error: { code: "E_EXECUTION_TIMEOUT", message: `Unity batchmode job exceeded timeout (${effectiveTimeout}ms).`, details: timeoutDetails(true, false, processKilled, false) }
          });
        } catch {
          // The close handler also resolves timeout if the state was not persisted yet.
        }
      })();
    }, effectiveTimeout);

    return new Promise((resolve, reject) => {
      child.on("error", async (error) => {
        if (timeoutHandle) {
          clearTimeout(timeoutHandle);
        }
        closeLogStream();
        try {
          const failed = await this.store.markTerminal(job.id, "failed", {
            error: { code: "UNITY_SPAWN_FAILED", message: error.message },
            exitCode: null
          });
          resolve(failed);
        } catch (storeError) {
          reject(storeError);
        }
      });

      child.on("close", async (code) => {
        if (timeoutHandle) {
          clearTimeout(timeoutHandle);
        }
        closeLogStream();

        // Flush the accumulated log to disk so that logs() can always read
        // from the file path even when -logFile - was used.
        await this.flushLogSnapshot(job.logPath, logChunks);

        try {
          const latest = await this.store.get(job.id);
          if (!latest) {
            reject(new Error(`Job not found: ${job.id}`));
            return;
          }
          if (terminalStatuses.includes(latest.status)) {
            resolve(latest);
            return;
          }

          const timedOut = didTimeout || Boolean(effectiveTimeout && latest.startedAt && Date.now() - Date.parse(latest.startedAt) >= effectiveTimeout);
          if (timedOut) {
            const timedOutJob = await this.store.markTerminal(job.id, "timeout", {
              exitCode: code,
              error: { code: "E_EXECUTION_TIMEOUT", message: `Unity batchmode job exceeded timeout (${effectiveTimeout}ms).`, details: timeoutDetails(true, false, true, false) }
            });
            resolve(timedOutJob);
            return;
          }

          const result = await this.finalResult(latest);
          const diagnostic = code === 0 ? undefined : await this.failureDiagnostic(latest, code);
          const completed = await this.store.markTerminal(job.id, code === 0 ? "succeeded" : "failed", {
            exitCode: code,
            result: diagnostic ? { ...result, diagnostic } : result,
            error: code === 0 ? undefined : diagnostic ?? { code: "UNITY_BATCHMODE_FAILED", message: `Unity exited with code ${code}.` }
          });
          resolve(completed);
        } catch (error) {
          reject(error);
        }
      });
    });
  }

  /** Write accumulated log chunks back to the log file so that `logs()` can
   *  always read from the on-disk path, even when -logFile - was used. */
  private async flushLogSnapshot(logPath: string, chunks: Buffer[]): Promise<void> {
    if (chunks.length === 0) return;
    try {
      const content = Buffer.concat(chunks);
      await writeFile(logPath, content);
    } catch {
      // Best-effort — the in-memory data was already streamed during the run.
    }
  }

  private async finalResult(job: JobRecord): Promise<Record<string, unknown> | undefined> {
    if (job.kind === "test" && typeof job.result?.resultPath === "string") {
      const summary = await readUnityTestSummary(job.result.resultPath);
      return summary ? { ...job.result, summary } : job.result;
    }
    if (job.kind === "code" && typeof job.result?.resultPath === "string") {
      const resultArtifact = await this.resultArtifact(job.result);
      const codeResult = await this.readCodeResult(job.result.resultPath);
      const result = codeResult ? { ...job.result, ...codeResult } : job.result;
      if (resultArtifact) {
        await this.updateArtifactManifest(job.result, resultArtifact);
        const artifacts = [...artifactList(result.artifacts), resultArtifact];
        result.artifacts = artifacts;
        if (result.data && typeof result.data === "object" && !Array.isArray(result.data)) {
          result.data = { ...result.data, artifacts };
        }
      }
      return result;
    }
    if (job.kind === "bridge") {
      const bridgeResult = await this.readBridgeResult(job.logPath);
      if (!bridgeResult) {
        return job.result;
      }
      const result: Record<string, unknown> = {
        ...job.result,
        execution: { ...objectRecord(job.result?.execution), exitState: bridgeResult.ok === true ? "succeeded" : "failed" },
        bridgeEnvelope: bridgeResult
      };
      if (bridgeResult.ok === true && isRecord(bridgeResult.data)) {
        result.data = bridgeResult.data;
      }
      if (bridgeResult.ok === false && isRecord(bridgeResult.error)) {
        result.bridgeError = bridgeResult.error;
      }
      return result;
    }
    return job.result;
  }

  private async failureDiagnostic(job: JobRecord, code: number | null): Promise<{ code: string; message: string }> {
    if (job.kind === "code") {
      const resultPath = typeof job.result?.resultPath === "string" ? job.result.resultPath : undefined;
      const codeResult = resultPath ? await this.readCodeResult(resultPath) : undefined;
      const error = codeResult?.error as { code?: string; message?: string } | undefined;
      if (error?.code && error?.message) {
        return { code: error.code, message: error.message };
      }
      const log = await this.readLog(job.logPath);
      return { code: codeDiagnosticCode(log), message: firstMatchingLine(log, /(E_CODE_[A-Z_]+:[^\r\n]*)/i) ?? `Unity code execution failed with exit code ${code}.` };
    }
    if (job.kind === "bridge") {
      const bridgeResult = await this.readBridgeResult(job.logPath);
      const error = isRecord(bridgeResult?.error) ? bridgeResult.error : undefined;
      if (typeof error?.code === "string" && typeof error?.message === "string") {
        return { code: error.code, message: error.message };
      }
      const log = await this.readLog(job.logPath);
      return { code: "BRIDGE_COMMAND_FAILED", message: firstMatchingLine(log, /(UNITY_CLI_BRIDGE_RESULT\s+\{[^\r\n]*\})/i) ?? `Unity bridge job failed with exit code ${code}.` };
    }
    if (job.kind !== "package") {
      return { code: "UNITY_BATCHMODE_FAILED", message: `Unity exited with code ${code}.` };
    }

    const log = await this.readLog(job.logPath);
    const message = firstMatchingLine(log, /([^\r\n]*Packages[\\/]com\.unity_cli[^\r\n]*error CS\d+:[^\r\n]*)/i)
      ?? firstMatchingLine(log, /([^\r\n]*error CS\d+:[^\r\n]*)/i)
      ?? firstMatchingLine(log, /E_PACKAGE_RESOLVE_FAILED:\s*(.+)/i)
      ?? firstMatchingLine(log, /(Another Unity instance is running[^\r\n]*|project is already open[^\r\n]*|AssetDatabase is busy[^\r\n]*|Refresh busy[^\r\n]*)/i)
      ?? firstMatchingLine(log, /(Package Manager[^\r\n]+)/i)
      ?? `Unity PackageManager resolve failed with exit code ${code}.`;

    return { code: packageDiagnosticCode(log), message };
  }

  private async readLog(path: string): Promise<string> {
    try {
      return await readFile(path, "utf8");
    } catch {
      return "";
    }
  }

  private async readCodeResult(path: string): Promise<Record<string, unknown> | undefined> {
    try {
      return JSON.parse(await readFile(path, "utf8")) as Record<string, unknown>;
    } catch {
      return undefined;
    }
  }

  /** 从 batchmode 日志提取 Bridge 命令的统一 JSON envelope。 */
  private async readBridgeResult(path: string): Promise<Record<string, unknown> | undefined> {
    const log = await this.readLog(path);
    const matches = [...log.matchAll(/UNITY_CLI_BRIDGE_RESULT\s+(\{[^\r\n]*\})/g)];
    const raw = matches.at(-1)?.[1];
    if (!raw) {
      return undefined;
    }
    try {
      const parsed = JSON.parse(raw) as unknown;
      return isRecord(parsed) ? parsed : undefined;
    } catch {
      return undefined;
    }
  }

  private async resultArtifact(result: Record<string, unknown>): Promise<Record<string, unknown> | undefined> {
    if (typeof result.resultPath !== "string" || typeof result.artifactDir !== "string") {
      return undefined;
    }
    try {
      const content = await readFile(result.resultPath);
      return {
        kind: "result",
        path: result.resultPath,
        relativePath: normalizeRelativePath(relative(result.artifactDir, result.resultPath) || "."),
        sha256: createHash("sha256").update(content).digest("hex"),
        sizeBytes: content.byteLength
      };
    } catch {
      return undefined;
    }
  }

  private async updateArtifactManifest(result: Record<string, unknown>, artifact: Record<string, unknown>): Promise<void> {
    if (typeof result.artifactDir !== "string") {
      return;
    }
    const manifestPath = join(result.artifactDir, "manifest.json");
    try {
      const manifest = JSON.parse(await readFile(manifestPath, "utf8")) as Record<string, unknown>;
      const artifacts = [...artifactList(manifest.artifacts).filter((entry) => entry.relativePath !== artifact.relativePath), artifact];
      manifest.artifact = artifacts;
      manifest.artifacts = artifacts;
      await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
    } catch {
    }
  }
}

function artifactList(value: unknown): Record<string, unknown>[] {
  return Array.isArray(value) ? value.filter((item): item is Record<string, unknown> => Boolean(item) && typeof item === "object" && !Array.isArray(item)) : [];
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function objectRecord(value: unknown): Record<string, unknown> {
  return isRecord(value) ? value : {};
}

function normalizeRelativePath(path: string): string {
  return path.split("\\").join("/");
}

async function killProcessTree(pid: number | undefined): Promise<boolean> {
  if (!pid) {
    return false;
  }
  if (process.platform !== "win32") {
    try {
      process.kill(pid);
      return true;
    } catch {
      return false;
    }
  }
  return new Promise((resolve) => {
    const killer = spawn("taskkill", ["/pid", String(pid), "/t", "/f"], { windowsHide: true, stdio: "ignore" });
    killer.on("error", () => resolve(false));
    killer.on("close", (code) => resolve(code === 0));
  });
}

function isWindowsCommandScript(path: string): boolean {
  return process.platform === "win32" && /\.(?:cmd|bat)$/i.test(path);
}

function timeoutDetails(cancelAttempted: boolean, cancelled: boolean, processKilled: boolean, domainReloadObserved: boolean): Record<string, boolean> {
  return { cancelAttempted, cancelled, processKilled, domainReloadObserved };
}

function codeDiagnosticCode(log: string): string {
  if (/E_CODE_COMPILE_FAILED|error CS\d+/i.test(log)) {
    return "E_CODE_COMPILE_FAILED";
  }
  if (/E_REFERENCE_RESOLUTION_FAILED/i.test(log)) {
    return "E_REFERENCE_RESOLUTION_FAILED";
  }
  if (/E_EXECUTION_TIMEOUT|timed out|timeout/i.test(log)) {
    return "E_EXECUTION_TIMEOUT";
  }
  if (/E_UNITY_BUSY|Another Unity instance is running|project is already open|AssetDatabase is busy/i.test(log)) {
    return "E_UNITY_BUSY";
  }
  if (/E_CODE_RUNTIME_EXCEPTION|Exception/i.test(log)) {
    return "E_CODE_RUNTIME_EXCEPTION";
  }
  return "E_CODE_RUNTIME_EXCEPTION";
}

function packageDiagnosticCode(log: string): string {
  if (/Packages[\\/]com\.unity_cli[^\r\n]*error CS\d+|error CS\d+/i.test(log)) {
    return "BRIDGE_COMPILE_FAILED";
  }
  if (/version conflict|dependency conflict|conflict/i.test(log)) {
    return "PACKAGE_VERSION_CONFLICT";
  }
  if (/Another Unity instance is running|project is already open|AssetDatabase is busy|Refresh busy/i.test(log)) {
    return "UNITY_BUSY";
  }
  if (/E_PACKAGE_RESOLVE_FAILED/i.test(log)) {
    return "PACKAGE_RESOLVE_FAILED";
  }
  return "PACKAGE_RESOLVE_FAILED";
}

function firstMatchingLine(log: string, pattern: RegExp): string | undefined {
  const match = pattern.exec(log);
  return match?.[1]?.trim() || match?.[0]?.trim();
}
