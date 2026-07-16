import { randomUUID } from "node:crypto";
import { mkdir, open, readFile, rename, stat, unlink, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import type { JobRecord, JobStatus, JobStoreData } from "./types.js";

export class JobStore {
  readonly filePath: string;

  constructor(dataDir: string) {
    this.filePath = join(dataDir, "jobs.json");
  }

  async list(): Promise<JobRecord[]> {
    return (await this.read()).jobs.sort((left, right) => left.createdAt.localeCompare(right.createdAt));
  }

  async get(id: string): Promise<JobRecord | undefined> {
    return (await this.read()).jobs.find((job) => job.id === id);
  }

  async create(record: JobRecord): Promise<JobRecord> {
    return this.withMutationLock(async () => {
      const data = await this.read();
      data.jobs.push(record);
      await this.write(data);
      return record;
    });
  }

  async update(id: string, patch: Partial<JobRecord>): Promise<JobRecord> {
    return this.withMutationLock(async () => {
      const data = await this.read();
      const index = data.jobs.findIndex((job) => job.id === id);
      if (index < 0) {
        throw new Error(`Job not found: ${id}`);
      }

      const updated = { ...data.jobs[index], ...patch, updatedAt: new Date().toISOString() };
      data.jobs[index] = updated;
      await this.write(data);
      return updated;
    });
  }

  /** 仅当当前状态仍在 expected 中时提交状态转换，跨进程保证 terminal 状态不可回退。 */
  async transition(id: string, expected: JobStatus[], patch: Partial<JobRecord>): Promise<{ job: JobRecord; changed: boolean }> {
    return this.withMutationLock(async () => {
      const data = await this.read();
      const index = data.jobs.findIndex((job) => job.id === id);
      if (index < 0) throw new Error(`Job not found: ${id}`);
      const current = data.jobs[index];
      if (!expected.includes(current.status)) return { job: current, changed: false };
      const updated = { ...current, ...patch, updatedAt: new Date().toISOString() };
      data.jobs[index] = updated;
      await this.write(data);
      return { job: updated, changed: true };
    });
  }

  async markTerminal(id: string, status: Exclude<JobStatus, "queued" | "running">, patch: Partial<JobRecord> = {}): Promise<JobRecord> {
    const transition = await this.transition(id, ["queued", "running"], {
      ...patch,
      status,
      finishedAt: patch.finishedAt ?? new Date().toISOString(),
      pid: undefined
    });
    return transition.job;
  }

  private async read(): Promise<JobStoreData> {
    try {
      const raw = await readFile(this.filePath, "utf8");
      const parsed = JSON.parse(raw) as JobStoreData;
      return { jobs: Array.isArray(parsed.jobs) ? parsed.jobs : [] };
    } catch (error) {
      const nodeError = error as NodeJS.ErrnoException;
      if (nodeError.code === "ENOENT") {
        return { jobs: [] };
      }
      throw error;
    }
  }

  /** 跨 CLI/worker 进程串行化 read-modify-write，避免 jobs.json 更新丢失或 rename 互撞。 */
  private async withMutationLock<T>(action: () => Promise<T>): Promise<T> {
    await mkdir(dirname(this.filePath), { recursive: true });
    const lockPath = `${this.filePath}.lock`;
    const token = `${process.pid}:${Date.now()}:${randomUUID()}`;
    const deadline = Date.now() + 15_000;
    let handle: Awaited<ReturnType<typeof open>> | undefined;
    while (!handle) {
      let candidate: Awaited<ReturnType<typeof open>> | undefined;
      try {
        candidate = await open(lockPath, "wx");
        await candidate.writeFile(token, "utf8");
        handle = candidate;
      } catch (error) {
        if (candidate) {
          await candidate.close().catch(() => undefined);
          await removeFileIfExists(lockPath);
        }
        if (!isJobStoreLockContentionError(error)) throw error;
        const lockStat = await stat(lockPath).catch(() => undefined);
        if (lockStat && Date.now() - lockStat.mtimeMs > 30_000) await removeFileIfExists(lockPath);
        if (Date.now() >= deadline) throw new Error(`Job store lock timeout: ${lockPath}`);
        await delay(25);
      }
    }

    try {
      return await action();
    } finally {
      await handle.close();
      const owner = await retryFileOperation(() => readFile(lockPath, "utf8")).catch(() => undefined);
      if (owner === token) await removeFileIfExists(lockPath);
    }
  }

  private async write(data: JobStoreData): Promise<void> {
    await mkdir(dirname(this.filePath), { recursive: true });
    const tempPath = `${this.filePath}.${process.pid}.${Date.now()}.${randomUUID()}.tmp`;
    try {
      await writeFile(tempPath, JSON.stringify(data, null, 2), "utf8");
      await retryFileOperation(() => rename(tempPath, this.filePath));
    } finally {
      await unlink(tempPath).catch(() => undefined);
    }
  }
}

const delay = (milliseconds: number): Promise<void> => new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

/** 判断 exclusive-create 在 Windows 上返回的锁竞争错误；权限异常最终仍由 deadline fail-closed。 */
export function isJobStoreLockContentionError(error: unknown): boolean {
  const code = (error as NodeJS.ErrnoException).code;
  return ["EEXIST", "EPERM", "EACCES", "EBUSY"].includes(code ?? "");
}

/** Windows 防病毒/并发 reader 可能短暂锁住 rename/unlink/read；仅对文件占用错误有限重试。 */
async function retryFileOperation<T>(operation: () => Promise<T>): Promise<T> {
  for (let attempt = 1; ; attempt += 1) {
    try {
      return await operation();
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (!["EPERM", "EACCES", "EBUSY"].includes(code ?? "") || attempt >= 80) throw error;
      await delay(25);
    }
  }
}

async function removeFileIfExists(path: string): Promise<void> {
  try {
    await retryFileOperation(() => unlink(path));
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
  }
}
