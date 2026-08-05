import { Worker, isMainThread, parentPort, workerData } from "node:worker_threads";
import {
  deployPackageSource,
  installPackage,
  removePackage,
  restoreDeployedPackage,
} from "../../package-manager.js";

export type PackageFileMutationOperation = "install" | "remove" | "deploy" | "restore";

interface WorkerInput {
  operation: PackageFileMutationOperation;
  projectPath: string;
  plan: Record<string, unknown>;
  stopAtMs: number;
}

interface WorkerSuccess {
  ok: true;
  result: Record<string, unknown>;
}

interface WorkerFailure {
  ok: false;
  error: {
    code?: string;
    message: string;
    nextActions?: string[];
  };
}

type WorkerResult = WorkerSuccess | WorkerFailure;

/**
 * Package 文件 mutation 在独立 Worker 内执行。调用方只有在 Worker 已退出后才会拿到结果，
 * 因而可以在随后提交 proof/failure 并安全释放跨进程 mutation lease。
 */
export async function runPackageFileMutationWorker(
  operation: PackageFileMutationOperation,
  projectPath: string,
  plan: Record<string, unknown>,
  stopAtMs: number,
  signal: AbortSignal,
): Promise<Record<string, unknown>> {
  if (!Number.isFinite(stopAtMs) || stopAtMs <= Date.now()) throw deadlineError();
  signal.throwIfAborted();

  const worker = new Worker(new URL("./file-mutation-worker.js", import.meta.url), {
    workerData: { operation, projectPath, plan, stopAtMs } satisfies WorkerInput,
  });
  let result: WorkerResult | undefined;
  let workerError: Error | undefined;
  let aborted = false;

  const abort = () => {
    aborted = true;
    void worker.terminate();
  };
  signal.addEventListener("abort", abort, { once: true });

  try {
    await new Promise<void>((resolve, reject) => {
      worker.once("message", (message: WorkerResult) => { result = message; });
      worker.once("error", (error) => { workerError = error; });
      worker.once("exit", (code) => {
        if (aborted) { reject(deadlineError()); return; }
        if (workerError) { reject(workerError); return; }
        if (code !== 0 || !result) { reject(deadlineError()); return; }
        resolve();
      });
    });
  } finally {
    signal.removeEventListener("abort", abort);
  }

  if (!result) throw deadlineError();
  if (!result.ok) {
    const error = new Error(result.error.message) as Error & { code?: string; nextActions?: string[] };
    error.code = result.error.code;
    error.nextActions = result.error.nextActions;
    throw error;
  }
  return result.result;
}

/** Worker 只接受父进程冻结的计划；硬截止时间到达时直接结束当前 Worker。 */
async function executeWorker(input: WorkerInput): Promise<Record<string, unknown>> {
  switch (input.operation) {
    case "install":
      return await installPackage(input.projectPath, {
        name: requiredString(input.plan, "name"),
        version: requiredString(input.plan, "version"),
      }) as unknown as Record<string, unknown>;
    case "remove":
      return await removePackage(input.projectPath, requiredString(input.plan, "name"), {
        force: requiredBoolean(input.plan, "force"),
      }) as unknown as Record<string, unknown>;
    case "deploy": {
      const plannedBackupPath = optionalString(input.plan, "backupPath");
      return await deployPackageSource(input.projectPath, requiredString(input.plan, "sourcePath"), {
        packageName: requiredString(input.plan, "packageName"),
        ...(plannedBackupPath ? { plannedBackupPath } : {}),
      }) as unknown as Record<string, unknown>;
    }
    case "restore":
      return await restoreDeployedPackage(input.projectPath, requiredString(input.plan, "packageName")) as unknown as Record<string, unknown>;
  }
}

/** 读取 Worker 计划中的必填字符串，损坏计划不得进入文件 mutation。 */
function requiredString(plan: Record<string, unknown>, key: string): string {
  const value = plan[key];
  if (typeof value !== "string" || !value) throw new Error(`Package file mutation plan is missing ${key}.`);
  return value;
}

/** 读取 Worker 计划中的可选字符串；非字符串值一律视为损坏计划。 */
function optionalString(plan: Record<string, unknown>, key: string): string | undefined {
  const value = plan[key];
  if (value === undefined) return undefined;
  if (typeof value !== "string" || !value) throw new Error(`Package file mutation plan has an invalid ${key}.`);
  return value;
}

/** 读取 Worker 计划中的必填布尔值。 */
function requiredBoolean(plan: Record<string, unknown>, key: string): boolean {
  const value = plan[key];
  if (typeof value !== "boolean") throw new Error(`Package file mutation plan is missing ${key}.`);
  return value;
}

/** 统一投影 Worker 硬截止时间错误。 */
function deadlineError(): Error & { code: string; nextActions: string[] } {
  const error = new Error("Package file mutation Worker did not settle before its reserved deadline.") as Error & { code: string; nextActions: string[] };
  error.code = "E_EXECUTION_TIMEOUT";
  error.nextActions = ["Inspect package files and the canonical request journal before submitting a new request."];
  return error;
}

if (!isMainThread) {
  const input = workerData as WorkerInput;
  const remainingMs = Math.max(1, input.stopAtMs - Date.now());
  const timer = setTimeout(() => process.exit(124), remainingMs);
  void executeWorker(input)
    .then((result) => parentPort?.postMessage({ ok: true, result } satisfies WorkerSuccess))
    .catch((error: unknown) => {
      const candidate = error as { code?: unknown; message?: unknown; nextActions?: unknown } | undefined;
      const failure: WorkerFailure = {
        ok: false,
        error: {
          ...(typeof candidate?.code === "string" ? { code: candidate.code } : {}),
          message: typeof candidate?.message === "string" && candidate.message ? candidate.message : "Package file mutation Worker failed.",
          ...(Array.isArray(candidate?.nextActions) ? { nextActions: candidate.nextActions.filter((value): value is string => typeof value === "string") } : {}),
        },
      };
      parentPort?.postMessage(failure);
    })
    .finally(() => {
      clearTimeout(timer);
      parentPort?.close();
    });
}
