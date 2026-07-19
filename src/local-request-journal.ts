import { execFile } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { link, mkdir, open, readFile, rename, rm } from "node:fs/promises";
import { promisify } from "node:util";
import { dirname, join } from "node:path";
import { computeRequestDigestV1 } from "./bridge/session-supervisor.js";
import { failure } from "./output.js";
import { resolveProjectIdentity } from "./project-identity.js";
import { StructuredToolError } from "./tools/errors.js";
import type { CliResult } from "./types.js";

interface LocalJournalRecord {
  version: 1;
  requestId: string;
  instanceId: string;
  editorSessionId: string;
  command: string;
  toolName: string;
  digestVersion: 1;
  requestDigest: string;
  executionClass: "atomic" | "observed-state" | "resumable" | "external";
  completionDriverId: string;
  absoluteDeadlineUtc: string;
  state: "accepted" | "running" | "completed" | "failed" | "outcome_unknown";
  phase: "accepted_before_effect" | "effect_started" | "effect_proven" | "result_persisted";
  acceptedAt: string;
  startedAt?: string;
  endedAt?: string;
  proofKind?: string;
  proofSha256?: string;
  provenEnvelope?: CliResult;
  terminalEnvelope?: CliResult;
}

export interface LocalJournalExecution {
  /** 隔离测试可注入已验证的 canonical identity；生产调用始终由 projectPath 解析。 */
  identity?: Pick<ReturnType<typeof resolveProjectIdentity>, "instanceId">;
  requestId: string;
  /** 公开控制面也必须使用同一 durable journal；默认保持 TS Tool 协议。 */
  command?: string;
  toolName: string;
  input: Record<string, unknown>;
  projectPath: string;
  dataDir: string;
  absoluteDeadlineMs: number;
  executionClass: LocalJournalRecord["executionClass"];
  completionDriverId: string;
}

const POLL_MS = 25;
const execFileAsync = promisify(execFile);
let localProcessStartIdentity: Promise<string | undefined> | undefined;

/**
 * TS mutation 的唯一 durable execution wrapper。
 * operation 只有在显式 commitEffect 完成 durable write 后才能产生第一个业务 effect。
 */
export async function executeWithLocalRequestJournal(
  execution: LocalJournalExecution,
  operation: (commitEffect: () => Promise<void>) => Promise<CliResult>,
): Promise<CliResult> {
  const identity = execution.identity ?? resolveProjectIdentity(execution.projectPath);
  const command = execution.command ?? "local-tool.run";
  const body = JSON.stringify({
    protocolVersion: 2,
    requestId: execution.requestId,
    instanceId: identity.instanceId,
    command,
    input: { name: execution.toolName, input: execution.input },
    execution: {
      absoluteDeadlineUtc: new Date(execution.absoluteDeadlineMs).toISOString(),
      completionContractVersion: 1,
      requestDigestVersion: 1,
    },
  });
  const requestDigest = computeRequestDigestV1(identity.instanceId, Buffer.from(body, "utf8"));
  const requestDir = join(execution.dataDir, "requests", identity.instanceId);
  const journalPath = join(requestDir, `${execution.requestId}.json`);
  const lockPath = `${journalPath}.lock`;
  const resourceLockPath = join(requestDir, ".instance-resource.lock");
  await mkdir(requestDir, { recursive: true, mode: 0o700 });

  const lock = await acquireRequestLock(lockPath, journalPath, execution.requestId, requestDigest, execution.absoluteDeadlineMs);
  if (lock.replay) return lock.replay;
  let resourceLock: DurableLock | undefined;
  try {
    const existing = await readJournal(journalPath);
    if (existing && existing.requestDigest !== requestDigest) {
      return failure(execution.requestId, "E_REQUEST_ID_REUSED", "requestId is already bound to different exact request bytes.", [
        "Submit the new operation with a new requestId."
      ], { toolName: execution.toolName });
    }
    if (existing?.terminalEnvelope) return existing.terminalEnvelope;
    if (existing?.phase === "effect_proven") {
      if (!isValidProvenEnvelope(existing, execution.requestId)) {
        const unknown = outcomeUnknown(execution.requestId, execution.toolName, "The durable effect proof hash or envelope does not match this request.");
        await bestEffortWriteJournal(journalPath, { ...existing, state: "outcome_unknown", phase: "result_persisted", endedAt: nowIso(), provenEnvelope: undefined, terminalEnvelope: unknown });
        return unknown;
      }
      const recovered = { ...existing, state: "completed" as const, phase: "result_persisted" as const, endedAt: nowIso(), provenEnvelope: undefined, terminalEnvelope: existing.provenEnvelope };
      await bestEffortWriteJournal(journalPath, recovered);
      return existing.provenEnvelope;
    }
    if (existing?.phase === "effect_started") {
      const unknown = outcomeUnknown(execution.requestId, execution.toolName, "A previous local owner stopped after the durable effect_started barrier.");
      await bestEffortWriteJournal(journalPath, { ...existing, state: "outcome_unknown", phase: "result_persisted", endedAt: nowIso(), terminalEnvelope: unknown });
      return unknown;
    }

    // 固定顺序为 request lock -> instance resource lock，避免同项目不同请求并发产生业务 effect。
    resourceLock = await acquireDurableLock(resourceLockPath, execution.absoluteDeadlineMs);

    let record: LocalJournalRecord = existing ?? {
      version: 1,
      requestId: execution.requestId,
      instanceId: identity.instanceId,
      editorSessionId: localSessionId(identity.instanceId),
      command,
      toolName: execution.toolName,
      digestVersion: 1,
      requestDigest,
      executionClass: execution.executionClass,
      completionDriverId: execution.completionDriverId,
      absoluteDeadlineUtc: new Date(execution.absoluteDeadlineMs).toISOString(),
      state: "accepted",
      phase: "accepted_before_effect",
      acceptedAt: nowIso(),
    };
    await writeJournal(journalPath, record);

    let committed = false;
    let commitInProgress = false;
    const commitEffect = async (): Promise<void> => {
      if (committed || commitInProgress) throw new Error("E_TOOL_EFFECT_BARRIER_DUPLICATE: local Tool crossed its mutation barrier more than once.");
      if (Date.now() >= execution.absoluteDeadlineMs) throw new Error("E_EXECUTION_TIMEOUT: immutable deadline elapsed before local effect commit.");
      commitInProgress = true;
      const committedRecord = { ...record, state: "running" as const, phase: "effect_started" as const, startedAt: nowIso() };
      try {
        await writeJournal(journalPath, committedRecord);
        record = committedRecord;
        committed = true;
      } finally {
        commitInProgress = false;
      }
    };

    let result: CliResult;
    try {
      result = await operation(commitEffect);
    } catch (error) {
      result = failure(execution.requestId, "E_TOOL_EFFECT_PRECOMMIT_FAILED", `Tool ${execution.toolName} failed before returning a terminal result.`, [
        "Inspect the request input and local filesystem state before retrying."
      ], { toolName: execution.toolName, reason: error instanceof Error ? error.message : String(error) });
    }
    if (result.ok && !committed) {
      result = failure(execution.requestId, "E_TOOL_COMPLETION_PROOF_INVALID", `Tool ${execution.toolName} returned success without crossing its durable local effect barrier.`, [
        "Call await context.commitEffect() after all prevalidation and immediately before the first business effect."
      ], { toolName: execution.toolName });
    } else if (!result.ok && committed) {
      result = outcomeUnknown(execution.requestId, execution.toolName, result.error.message);
    }
    if (result.ok && Date.now() >= execution.absoluteDeadlineMs) {
      result = outcomeUnknown(execution.requestId, execution.toolName, "The immutable deadline elapsed after the business effect completed.");
    }

    const terminalAt = nowIso();
    if (result.ok) {
      const proofSha256 = sha256(JSON.stringify(result));
      record = {
        ...record,
        state: "running",
        phase: "effect_proven",
        proofKind: `local-${execution.completionDriverId}`,
        proofSha256,
        provenEnvelope: result,
      };
      try {
        await writeJournal(journalPath, record);
      } catch (error) {
        const unknown = outcomeUnknown(execution.requestId, execution.toolName, `Effect proof persistence failed: ${errorMessage(error)}`);
        await bestEffortWriteJournal(journalPath, {
          ...record,
          state: "outcome_unknown",
          phase: "result_persisted",
          endedAt: terminalAt,
          provenEnvelope: undefined,
          terminalEnvelope: unknown,
        });
        return unknown;
      }
      record = { ...record, state: "completed", phase: "result_persisted", endedAt: terminalAt, provenEnvelope: undefined, terminalEnvelope: result };
    } else {
      record = { ...record, state: result.error.code === "E_REQUEST_OUTCOME_UNKNOWN" ? "outcome_unknown" : "failed", phase: "result_persisted", endedAt: terminalAt, terminalEnvelope: result };
    }
    try {
      await writeJournal(journalPath, record);
    } catch (error) {
      if (result.ok) return result;
      if (committed) {
        const unknown = outcomeUnknown(execution.requestId, execution.toolName, `Terminal result persistence failed: ${errorMessage(error)}`);
        await bestEffortWriteJournal(journalPath, { ...record, state: "outcome_unknown", terminalEnvelope: unknown });
        return unknown;
      }
      throw error;
    }
    return result;
  } finally {
    await resourceLock?.release();
    await lock.release();
  }
}

/** 等待同 requestId 的现有 owner；dead owner 的 lock 会被回收并由 journal phase 决定重放或 unknown。 */
async function acquireRequestLock(lockPath: string, journalPath: string, requestId: string, requestDigest: string, absoluteDeadlineMs: number): Promise<{ replay?: CliResult; release(): Promise<void> }> {
  while (true) {
    const token = randomUUID();
    const processStartIdentity = await currentProcessStartIdentity();
    if (await createLockFile(lockPath, { pid: process.pid, token, processStartIdentity, requestDigest, createdAt: nowIso() })) {
      return { release: () => releaseOwnedLock(lockPath, token) };
    }
    {
      const terminal = await readJournal(journalPath);
      if (terminal && terminal.requestDigest !== requestDigest) return { replay: requestIdReused(requestId), release: async () => undefined };
      if (terminal?.terminalEnvelope) return { replay: terminal.terminalEnvelope, release: async () => undefined };
      if (await recoverObservedDeadLock(lockPath)) continue;
      if (Date.now() >= absoluteDeadlineMs) {
        const code = terminal?.phase === "effect_started" ? "E_REQUEST_OUTCOME_UNKNOWN" : "E_REQUEST_IN_PROGRESS";
        return {
          replay: failure(terminal?.requestId ?? requestId, code, "The same local request is still owned by another process at the immutable deadline.", [
            "Inspect the durable request journal before submitting a new request."
          ]),
          release: async () => undefined,
        };
      }
      await new Promise((resolve) => setTimeout(resolve, Math.min(POLL_MS, absoluteDeadlineMs - Date.now())));
    }
  }
}

interface DurableLock { release(): Promise<void>; }

/** PID 探测而非 mtime 判定 owner；恢复者先原子隔离旧锁，避免删除新 owner 的锁。 */
async function acquireDurableLock(lockPath: string, absoluteDeadlineMs: number): Promise<DurableLock> {
  while (true) {
    const token = randomUUID();
    const processStartIdentity = await currentProcessStartIdentity();
    if (await createLockFile(lockPath, { pid: process.pid, token, processStartIdentity, createdAt: nowIso() })) {
      return { release: () => releaseOwnedLock(lockPath, token) };
    }
    {
      if (await recoverObservedDeadLock(lockPath)) continue;
      if (Date.now() >= absoluteDeadlineMs) {
        throw new StructuredToolError("E_EXECUTION_TIMEOUT", "The immutable deadline elapsed waiting for the canonical instance resource lock.", [
          "Wait for the current local operation to finish, then submit a new request."
        ], { lockPath, absoluteDeadlineUtc: new Date(absoluteDeadlineMs).toISOString() });
      }
      await new Promise((resolve) => setTimeout(resolve, Math.min(POLL_MS, absoluteDeadlineMs - Date.now())));
    }
  }
}

/** 完整 owner record 先 fsync 到临时文件，再用 hard link 原子发布，避免暴露半写锁。 */
async function createLockFile(lockPath: string, owner: Record<string, unknown>): Promise<boolean> {
  const tempPath = `${lockPath}.create-${process.pid}-${randomUUID()}`;
  const handle = await open(tempPath, "wx", 0o600);
  try {
    await handle.writeFile(JSON.stringify(owner), "utf8");
    await handle.sync();
  } finally {
    await handle.close();
  }
  try {
    await link(tempPath, lockPath);
    return true;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "EEXIST") return false;
    throw error;
  } finally {
    await rm(tempPath, { force: true });
  }
}

/** 仅持有相同 token 的 owner 可以释放锁，防止迟到的 finally 删除后继 owner。 */
async function releaseOwnedLock(lockPath: string, token: string): Promise<void> {
  const owner = await readLockOwner(lockPath);
  if (owner?.token !== token) return;
  const quarantinePath = `${lockPath}.release-${token}`;
  try {
    await rename(lockPath, quarantinePath);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return;
    throw error;
  }
  await rm(quarantinePath, { force: true });
}

/**
 * 对观测到的 lock 原文建立唯一 recovery claim，claim owner 重读完全相同的字节后才可隔离。
 * claim 不能自动回收；无法证明恢复者已退出时必须在 deadline fail closed。
 */
async function recoverObservedDeadLock(lockPath: string): Promise<boolean> {
  const observed = await observeLock(lockPath);
  if (!observed || !observed.recoverable || !(await isRecoverableOwner(observed.owner))) return false;
  const claimPath = `${lockPath}.recovery-${observed.fingerprint}`;
  let claim;
  try {
    claim = await open(claimPath, "wx", 0o600);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "EEXIST" || (error as NodeJS.ErrnoException).code === "ENOENT") return false;
    throw error;
  }
  try {
    await claim.writeFile(JSON.stringify({ pid: process.pid, observedFingerprint: observed.fingerprint, createdAt: nowIso() }), "utf8");
    await claim.sync();
    const current = await observeLock(lockPath);
    if (!current || !current.raw.equals(observed.raw)) return false;
    const quarantinePath = `${lockPath}.quarantine-${observed.fingerprint}-${randomUUID()}`;
    try {
      await rename(lockPath, quarantinePath);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return false;
      throw error;
    }
    await rm(quarantinePath, { force: true });
    return true;
  } finally {
    await claim.close();
    await rm(claimPath, { force: true });
  }
}

function requestIdReused(requestId: string): CliResult {
  return failure(requestId, "E_REQUEST_ID_REUSED", "requestId is already bound to different exact request bytes.", [
    "Submit the new operation with a new requestId."
  ]);
}

/** 原子 replace + fsync，确保 commitEffect 返回前 effect_started 已 durable。 */
async function writeJournal(path: string, record: LocalJournalRecord): Promise<void> {
  const tempPath = `${path}.${process.pid}.${Date.now()}.tmp`;
  await mkdir(dirname(path), { recursive: true, mode: 0o700 });
  const handle = await open(tempPath, "wx", 0o600);
  try {
    await handle.writeFile(`${JSON.stringify(record)}\n`, "utf8");
    await handle.sync();
  } finally {
    await handle.close();
  }
  await rename(tempPath, path);
}

async function bestEffortWriteJournal(path: string, record: LocalJournalRecord): Promise<void> {
  try {
    await writeJournal(path, record);
  } catch {
    // effect_started 或 effect_proven 已是恢复的唯一依据。
  }
}

async function readJournal(path: string): Promise<LocalJournalRecord | undefined> {
  try {
    return JSON.parse(await readFile(path, "utf8")) as LocalJournalRecord;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

interface LockOwner {
  pid: number;
  token?: string;
  processStartIdentity?: string;
}

interface ObservedLock {
  raw: Buffer;
  fingerprint: string;
  owner?: LockOwner;
  recoverable: boolean;
}

/** 读取原始字节并计算 fingerprint；解析失败也保留原文以便安全地单恢复。 */
async function observeLock(path: string): Promise<ObservedLock | undefined> {
  let raw: Buffer;
  try {
    raw = await readFile(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
  const fingerprint = sha256(raw.toString("base64"));
  if (raw.length === 0) return { raw, fingerprint, recoverable: false };
  try {
    const parsed = JSON.parse(raw.toString("utf8")) as { pid?: number; token?: string; processStartIdentity?: string };
    if (!Number.isInteger(parsed.pid) || (parsed.pid ?? 0) <= 0) return { raw, fingerprint, recoverable: true };
    return { raw, fingerprint, recoverable: true, owner: { pid: parsed.pid!, token: parsed.token, processStartIdentity: parsed.processStartIdentity } };
  } catch {
    return { raw, fingerprint, recoverable: true };
  }
}

/** PID 已退出或 start identity 不同才允许恢复；身份缺失或无法验证均按活锁处理。 */
async function isRecoverableOwner(owner: LockOwner | undefined): Promise<boolean> {
  if (!owner) return true;
  try {
    process.kill(owner.pid, 0);
  } catch (error) {
    // 只有明确的“不存在进程”可证明 owner 已退出；权限或探测异常必须保守保留锁。
    return (error as NodeJS.ErrnoException).code === "ESRCH";
  }
  if (!owner.processStartIdentity) return false;
  const currentStartIdentity = await processStartIdentity(owner.pid);
  return currentStartIdentity !== undefined && currentStartIdentity !== owner.processStartIdentity;
}

/** 读取 lock owner 时保留 token，供释放路径确认自己仍是当前 owner。 */
async function readLockOwner(path: string): Promise<LockOwner | undefined> {
  const observed = await observeLock(path);
  return observed?.owner;
}

/** 获取进程启动身份，Windows 使用 StartTime ticks，其他平台使用 /proc start tick。 */
async function processStartIdentity(pid: number): Promise<string | undefined> {
  try {
    if (process.platform === "win32") {
      const { stdout } = await execFileAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", `(Get-Process -Id ${pid}).StartTime.ToUniversalTime().Ticks`], { windowsHide: true });
      const ticks = stdout.trim();
      return /^\d+$/.test(ticks) ? `win32:${ticks}` : undefined;
    }
    const stat = await readFile(`/proc/${pid}/stat`, "utf8");
    const fields = stat.slice(stat.lastIndexOf(")") + 2).trim().split(/\s+/);
    return /^\d+$/.test(fields[19] ?? "") ? `proc:${fields[19]}` : undefined;
  } catch {
    return undefined;
  }
}

/** 当前 owner 的启动身份在本进程生命周期内不变；仅缓存自身，外部 PID 每次都重新验证。 */
function currentProcessStartIdentity(): Promise<string | undefined> {
  localProcessStartIdentity ??= processStartIdentity(process.pid);
  return localProcessStartIdentity;
}

function outcomeUnknown(requestId: string, toolName: string, reason: string): CliResult {
  return failure(requestId, "E_REQUEST_OUTCOME_UNKNOWN", `Tool ${toolName} crossed effect_started but terminal proof is unavailable.`, [
    "Inspect the durable request journal and target business state; do not replay this request blindly."
  ], { toolName, reason });
}

function isValidProvenEnvelope(record: LocalJournalRecord, requestId: string): record is LocalJournalRecord & { proofSha256: string; provenEnvelope: CliResult } {
  const envelope = record.provenEnvelope;
  return typeof record.proofSha256 === "string"
    && /^[a-f0-9]{64}$/i.test(record.proofSha256)
    && envelope !== undefined
    && envelope.requestId === requestId
    && typeof envelope.ok === "boolean"
    && sha256(JSON.stringify(envelope)) === record.proofSha256;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function localSessionId(instanceId: string): string {
  return `session-${sha256(`unity-cli-local-session-v1\0${instanceId}`)}`;
}

function sha256(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex");
}

function nowIso(): string {
  return new Date().toISOString();
}
