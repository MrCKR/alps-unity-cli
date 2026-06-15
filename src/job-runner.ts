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
    const command = [options.unityPath, "-batchmode", "-nographics", "-quit", "-projectPath", options.projectPath, "-logFile", logPath, ...options.args];

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
    const job = await this.store.get(id);
    if (!job) {
      throw new Error(`Job not found: ${id}`);
    }
    if (terminalStatuses.includes(job.status)) {
      throw new Error(`Job is already terminal: ${job.status}`);
    }
    let processKilled = false;
    if (job.pid) {
      try {
        process.kill(job.pid);
        processKilled = true;
      } catch {
        // Process may already be gone; the persisted state is still updated below.
      }
    }
    return this.store.markTerminal(id, "cancelled", { error: { code: "JOB_CANCELLED", message: "Job was cancelled.", details: timeoutDetails(true, true, processKilled, false) } });
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
    child.unref();
  }

  private async spawnJob(job: JobRecord): Promise<JobRecord> {
    const child = spawn(job.unityPath, job.command.slice(1), { windowsHide: true, stdio: ["ignore", "pipe", "pipe"], shell: isWindowsCommandScript(job.unityPath) });
    const logStream = createWriteStream(job.logPath, { flags: "a" });
    child.stdout.pipe(logStream);
    child.stderr.pipe(logStream);

    await this.store.update(job.id, {
      status: "running",
      startedAt: new Date().toISOString(),
      pid: child.pid
    });

    let timeoutHandle: NodeJS.Timeout | undefined;
    let didTimeout = false;
    if (job.timeoutMs && job.timeoutMs > 0) {
      timeoutHandle = setTimeout(() => {
        didTimeout = true;
        void (async () => {
          const processKilled = await killProcessTree(child.pid);
          try {
            await this.store.markTerminal(job.id, "timeout", {
              exitCode: null,
              error: { code: "E_EXECUTION_TIMEOUT", message: "Unity batchmode job exceeded timeout.", details: timeoutDetails(true, false, processKilled, false) }
            });
          } catch {
            // The close handler also resolves timeout if the state was not persisted yet.
          }
        })();
      }, job.timeoutMs);
    }

    return new Promise((resolve, reject) => {
      child.on("error", async (error) => {
        if (timeoutHandle) {
          clearTimeout(timeoutHandle);
        }
        logStream.end();
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
        logStream.end();
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

          const timedOut = didTimeout || Boolean(job.timeoutMs && latest.startedAt && Date.now() - Date.parse(latest.startedAt) >= job.timeoutMs);
          if (timedOut) {
            const timedOutJob = await this.store.markTerminal(job.id, "timeout", {
              exitCode: code,
              error: { code: "E_EXECUTION_TIMEOUT", message: "Unity batchmode job exceeded timeout.", details: timeoutDetails(true, false, true, false) }
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
