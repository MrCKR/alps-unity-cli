import { mkdir, readFile, rename, writeFile } from "node:fs/promises";
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
    const data = await this.read();
    data.jobs.push(record);
    await this.write(data);
    return record;
  }

  async update(id: string, patch: Partial<JobRecord>): Promise<JobRecord> {
    const data = await this.read();
    const index = data.jobs.findIndex((job) => job.id === id);
    if (index < 0) {
      throw new Error(`Job not found: ${id}`);
    }

    const updated = { ...data.jobs[index], ...patch, updatedAt: new Date().toISOString() };
    data.jobs[index] = updated;
    await this.write(data);
    return updated;
  }

  async markTerminal(id: string, status: Exclude<JobStatus, "queued" | "running">, patch: Partial<JobRecord> = {}): Promise<JobRecord> {
    return this.update(id, {
      ...patch,
      status,
      finishedAt: patch.finishedAt ?? new Date().toISOString(),
      pid: undefined
    });
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

  private async write(data: JobStoreData): Promise<void> {
    await mkdir(dirname(this.filePath), { recursive: true });
    const tempPath = `${this.filePath}.${process.pid}.${Date.now()}.tmp`;
    await writeFile(tempPath, JSON.stringify(data, null, 2), "utf8");
    await rename(tempPath, this.filePath);
  }
}
