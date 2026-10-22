import { createHash, randomUUID } from "node:crypto";
import { mkdir, readFile, readdir, rename, rm, stat, writeFile } from "node:fs/promises";
import { basename, dirname, isAbsolute, join, resolve } from "node:path";
import { spawn, spawnSync } from "node:child_process";
import { defaultDataDir } from "./platform.js";
import type { CliResult } from "./types.js";
import { failure, success } from "./output.js";
import { stripBom } from "./util.js";
import { getBoolean } from "./args.js";
import { stringFlag } from "./bridge/args.js";
import { findUnityProjectProcesses } from "./unity-project-processes.js";
import {
  CANONICALIZATION_VERSION,
  ProjectIdentityError,
  resolveProjectIdentity,
  resolveProjectIdentityAsync,
  resolveProjectIdentityFromCwd,
  resolveProjectIdentityFromCwdAsync,
  type ProjectIdentity,
} from "./project-identity.js";

export const HEALTH_CHECK_TIMEOUT_MS = 300;
export const HEALTH_CACHE_TTL_MS = 1_000;
export const MAIN_THREAD_PULSE_FRESH_MS = 2_000;
const REGISTRY_READ_RETRY_DELAYS_MS = [5, 10, 20, 40] as const;
const INSTANCE_ID_PATTERN = /^unity-[0-9a-f]{64}$/;
const SESSION_ID_PATTERN = /^session-[0-9a-f]{64}$/;
const LISTENER_ID_PATTERN = /^listener-[0-9a-f]{32}$/;
const ALIAS_PATTERN = /^[A-Za-z0-9._-]{1,64}$/;

export type UnityInstanceStatus = "ready" | "transitioning" | "blocked" | "unresponsive" | "unverifiable" | "offline" | "conflict";
export type InstanceTarget = "editor" | "runtime";
export type RegistryLifecycle = "ready" | "transitioning" | "blocked" | "domain-reloading" | "compiling" | "importing" | "building" | "playmode-changing";
const REGISTRY_LIFECYCLES = new Set<RegistryLifecycle>(["ready", "transitioning", "blocked", "domain-reloading", "compiling", "importing", "building", "playmode-changing"]);

export interface RegistryIdentity {
  instanceId: string;
  canonicalProjectPath: string;
  displayProjectPath: string;
}

export interface RegistryEndpoint {
  host: "127.0.0.1";
  preferredPort: number;
  actualPort: number;
  fallback: boolean;
  url: string;
}

export interface RegistryListener {
  listenerId: string;
  generation: number;
  startedAt: string;
  endpoint: RegistryEndpoint;
}

export interface RegistrySession {
  target: InstanceTarget;
  sessionId: string;
  editorSessionId?: string;
  runtimeSessionId?: string;
  processId: number;
  processStartedAt: string;
  publishedAt: string;
  publicationSequence: number;
  lifecycle: RegistryLifecycle;
  lastUnitySnapshot: Record<string, unknown>;
  listener: RegistryListener;
  progress: Array<Record<string, unknown>>;
  blockers: Array<Record<string, unknown>>;
  unityVersion?: string;
  buildStamp?: string;
  toolCatalogFingerprint?: string;
  executablePath?: string;
  buildId?: string;
}

export interface InstanceRegistryV3 {
  version: 3;
  revision: number;
  canonicalizationVersion: 1;
  identity: RegistryIdentity;
  sessions: RegistrySession[];
  publishedAt: string;
}

export interface UnityInstance {
  instanceId: string;
  canonicalizationVersion: 1;
  canonicalProjectPath: string;
  displayProjectPath: string;
  projectPath: string;
  projectName: string;
  status: UnityInstanceStatus;
  target?: InstanceTarget;
  revision: number;
  updatedAt: string;
  sessions: RegistrySession[];
  activeSession?: RegistrySession;
  allSessions: RegistrySession[];
  sessionId?: string;
  editorSessionId?: string;
  runtimeSessionId?: string;
  processId?: number;
  publishedAt?: string;
  lastUnitySnapshot?: Record<string, unknown>;
  progress: Array<Record<string, unknown>>;
  blockers: Array<Record<string, unknown>>;
  unityVersion?: string;
  buildStamp?: string;
  toolCatalogFingerprint?: string;
  /**
   * 当前 live pause publication 证明的故意主线程暂停，独立于 pulse 与 blocker 列表。
   * `blocked` 状态可能来自 Windows modal 或该 publication；调用方只有拿到这份证据
   * 才能把暂停交给 Bridge 的同一控制通道，而不是本地伪装成弹窗阻断。
   */
  mainThreadDebugPause?: MainThreadDebugPause;
  claimVerification: "verified" | "unverifiable" | "none";
  claimReason?: IdentityEvidenceReason;
  processClaimIds?: number[];
}

/**
 * 进程身份证据不可用的有界归因。
 * 只描述观察失败原因供调用方形成恢复决策；不改变 status 归约、identity 证明强度或 fail-closed 语义。
 */
export type IdentityEvidenceReason = "access-denied" | "timeout" | "unavailable" | "mismatch";

const ACCESS_DENIED_MARKERS = ["access is denied", "access denied", "requested registry access is not allowed", "拒绝访问", "没有权限"];

/** 只在明确权限拒绝特征时归因 access-denied；其他失败保持 unavailable，禁止猜测。 */
function classifyIdentityEvidenceFailure(text: string | null | undefined, timedOut: boolean): IdentityEvidenceReason {
  if (timedOut) return "timeout";
  const normalized = (text ?? "").toLowerCase();
  return ACCESS_DENIED_MARKERS.some((marker) => normalized.includes(marker)) ? "access-denied" : "unavailable";
}

/** execFile 失败文本只用于归因，不进入业务结果。 */
function failureReasonFromError(error: unknown): IdentityEvidenceReason {
  if (!error || typeof error !== "object") return "unavailable";
  const record = error as Record<string, unknown>;
  const text = [record.stderr, record.stdout, (error as Error).message].filter((value): value is string => typeof value === "string").join(" ");
  return classifyIdentityEvidenceFailure(text, record.killed === true || record.code === "ETIMEDOUT");
}

/** 取本次观察的首个失败归因；无观察级归因时按 live claim 数量区分 mismatch 与 unavailable。 */
function claimEvidenceReason(claims: RegistrySession[], observations: Map<number, ProcessObservation> | undefined, aliveClaims: number): IdentityEvidenceReason {
  if (!observations) return "unavailable";
  const observed = claims.map((session) => observations.get(session.processId)?.reason).find((reason): reason is IdentityEvidenceReason => !!reason);
  return observed ?? (aliveClaims > 1 ? "mismatch" : "unavailable");
}

/** 权限拒绝与普通观察失败的恢复路径不同；两者都禁止重试结果未知的 mutation。 */
export function unverifiableNextActions(reason: IdentityEvidenceReason | undefined): string[] {
  if (reason === "access-denied") {
    return [
      "This host denied OS process inspection, so Unity process identity cannot be proven from this session.",
      "Run the CLI from a host session that can read Unity process identity, then observe editor.state again.",
      "Do not retry a prior mutation whose outcome is unknown.",
    ];
  }
  return [
    "Run unitycli instances list or tools run editor.state to observe the current identity again.",
    "Do not retry a prior mutation whose outcome is unknown.",
  ];
}

export interface ProjectProcessClaimInspection {
  verification: "verified" | "unverifiable" | "unsupported";
  reason?: IdentityEvidenceReason;
  processIds: number[];
  processClaims?: Array<{ processId: number; processStartedAt: string }>;
}

export interface InstanceSelection {
  instance: UnityInstance;
  matchedBy: "alias" | "name" | "instanceId" | "projectPath" | "cwd";
}

export interface InstanceRegistryReadResult {
  instances: UnityInstance[];
  invalidRecordCount: number;
}

interface RegistryRecordCollection {
  records: InstanceRegistryV3[];
  invalidRecordCount: number;
}

/** journal 等本地 mutation 在 acceptance 前只读取v3 registry identity，不依赖CWD推断。 */
export interface InstanceJournalIdentity {
  instanceId: string;
  canonicalProjectPath: string;
}

interface AliasBinding {
  alias: string;
  instanceId: string;
  canonicalProjectPath: string;
  displayProjectPath: string;
  canonicalizationVersion: 1;
}

interface AliasStoreV2 {
  version: 2;
  revision: number;
  aliases: AliasBinding[];
}

interface ProcessObservation {
  processId: number;
  processStartedAt?: string;
  executablePath?: string;
  state: "alive" | "dead" | "unverifiable";
  reason?: IdentityEvidenceReason;
}

export type SessionProcessVerification = "verified" | "dead" | "unverifiable";

interface MainThreadHealth {
  observed: boolean;
  sequence: number;
  ageMs: number | null;
  listenerGeneration: number;
  lifecycle: RegistryLifecycle;
  /** 当前 live pause publication 证明的故意主线程暂停；null 表示未发布。 */
  debugPause: MainThreadDebugPause | null;
}

/**
 * 由当前 live pause publication 发布的故意主线程暂停；不是文件、不是缓存。
 *
 * `timeoutKind` 与 `remainingMs` 是同一 publication 自带的可达事实：失败归因必须原样带出他们，
 * 才能在 dispatch 已发生时指出现场的实际边界。旧 publication 缺少他们时省略，绝不回填或伪造。
 */
export interface MainThreadDebugPause {
  pauseId: string;
  threadId: number;
  publishedSequence: number;
  timeoutKind?: "bounded" | "unbounded";
  remainingMs?: number;
}

export interface InstanceHealthInspection {
  transportVerified: boolean;
  mainThread?: {
    observed: boolean;
    sequence: number;
    ageMs: number | null;
    lifecycle: RegistryLifecycle;
    fresh: boolean;
    /** 独立于 pulse 的证据：主线程是否被故意暂停。 */
    debugPause: MainThreadDebugPause | null;
  };
}

interface HealthIdentity {
  version: 3;
  target: InstanceTarget;
  instanceId: string;
  canonicalProjectPath: string;
  canonicalizationVersion: 1;
  sessionId: string;
  listenerId: string;
  listenerGeneration: number;
  actualPort: number;
  buildStamp: string;
  buildId?: string;
  mainThread: MainThreadHealth;
  checkedAt: number;
}

export class InstanceRegistryError extends Error {
  public constructor(
    public readonly code: string,
    message: string,
    public readonly nextActions: string[] = [],
    public readonly details?: Record<string, unknown>,
  ) {
    super(message);
    this.name = "InstanceRegistryError";
  }
}

const healthCache = new Map<string, { checkedAt: number; identity: HealthIdentity }>();
const PROJECT_PROCESS_CLAIM_TIMEOUT_MS = 10_000;
const PROCESS_OBSERVATION_TIMEOUT_MS = 3_000;

/** 清空仅成功 health identity 的短缓存；失败探测从不缓存。 */
export function clearInstanceHealthCache(): void {
  healthCache.clear();
}

/** 顶层instances list只读正式registry；writer-owned prune由独立控制面路由。 */
export async function handleInstancesCommand(
  command: string | undefined,
  flags: Record<string, string | boolean>,
  dataDir: string,
  requestId: string,
): Promise<CliResult> {
  const includeAll = getBoolean(flags, "all");
  if (command === "list" || !command) {
    const { instances, invalidRecordCount } = await readInstancesWithProjectProcessClaims(dataDir);
    const aliasByInstanceId = new Map((await readAliasStore(dataDir)).aliases.map((binding) => [binding.instanceId, binding.alias]));
    // 默认只展示有 session 的活跃实例；offline 历史残留用 --all 展开，避免上下文污染。
    const visible = includeAll ? instances : instances.filter((instance) => instance.status !== "offline");
    const hiddenOfflineCount = instances.length - visible.length;
    // 公开选择名在全部已登记实例上计算，使 --all 过滤不影响名称。
    const names = instanceNames(instances);
    return success(requestId, {
      instances: visible.map((instance) => {
        const summary = instanceListSummary(instance, names.get(instance.instanceId) ?? instance.projectName);
        const alias = aliasByInstanceId.get(instance.instanceId);
        return alias ? { ...summary, alias } : summary;
      }),
      ...(includeAll || hiddenOfflineCount === 0 ? {} : { hiddenOfflineCount }),
      ...(invalidRecordCount === 0 ? {} : { invalidRecordCount }),
    });
  }

  return failure(requestId, "UNKNOWN_INSTANCES_COMMAND", "Expected instances list.", [
    "Run unitycli instances list to inspect project identities and Editor sessions.",
  ]);
}

/** 顶层 target 只保留 canonical project identity alias 管理。 */
export async function handleTargetCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  dataDir: string,
  requestId: string,
): Promise<CliResult> {
  if (command !== "alias") {
    return failure(requestId, "UNKNOWN_TARGET_COMMAND", "Only target alias list|set|unset is supported.", [
      "Run unitycli target alias list.",
      "Pass --instance <name|alias|projectPath> or run from a Unity project directory.",
    ]);
  }
  return handleTargetAliasCommand(positional[0], positional.slice(1), flags, dataDir, requestId);
}

/** alias 永久绑定 canonical project identity，可在 Editor offline 时解析。 */
async function handleTargetAliasCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  dataDir: string,
  requestId: string,
): Promise<CliResult> {
  const store = await readAliasStore(dataDir);
  if (!command || command === "list") {
    const instances = await readInstances(dataDir);
    return success(requestId, {
      aliases: store.aliases.map((binding) => ({
        ...binding,
        status: instances.find((instance) => instance.instanceId === binding.instanceId)?.status ?? "offline",
      })),
    });
  }

  if (command === "set") {
    let explicitInstance: string | undefined;
    try {
      explicitInstance = explicitInstanceSelector(flags);
    } catch (error) {
      return instanceSelectionFailure(requestId, error) ?? failure(requestId, "E_INSTANCE_REQUIRED", error instanceof Error ? error.message : String(error));
    }
    const selector = positional[0] ?? explicitInstance ?? stringFlag(flags, "project");
    const alias = positional[1] ?? stringFlag(flags, "alias");
    if (!selector || !alias) {
      return failure(requestId, "E_INSTANCE_NOT_FOUND", "target alias set requires <name|projectPath> <alias>.", [
        "Pass an existing Unity project path or a full deterministic instanceId.",
      ]);
    }
    if (!ALIAS_PATTERN.test(alias)) {
      return failure(requestId, "E_TARGET_ALIAS_INVALID", "Alias must contain 1-64 ASCII letters, digits, dots, underscores, or hyphens.");
    }

    try {
      const identity = await resolveAliasIdentity(selector, dataDir);
      const existing = store.aliases.find((binding) => binding.alias === alias);
      if (existing && existing.instanceId !== identity.instanceId && !getBoolean(flags, "force")) {
        return failure(requestId, "E_TARGET_ALIAS_CONFLICT", `Target alias already points to another project identity: ${alias}.`, [
          "Run unitycli target alias unset <alias> before assigning it.",
          "Pass --force to replace the binding deliberately.",
        ], { alias, existingInstanceId: existing.instanceId, requestedInstanceId: identity.instanceId });
      }

      const aliases = store.aliases.filter((binding) => binding.alias !== alias);
      aliases.push(aliasBinding(alias, identity));
      await writeAliasStore(dataDir, { version: 2, revision: store.revision + 1, aliases: aliases.sort((left, right) => left.alias.localeCompare(right.alias)) });
      return success(requestId, {
        alias,
        identity: aliasBinding(alias, identity),
        replacedInstanceId: existing && existing.instanceId !== identity.instanceId ? existing.instanceId : null,
      });
    } catch (error) {
      return instanceSelectionFailure(requestId, error) ?? failure(requestId, "E_INSTANCE_NOT_FOUND", error instanceof Error ? error.message : String(error));
    }
  }

  if (command === "unset") {
    const alias = positional[0] ?? stringFlag(flags, "alias");
    const existing = alias ? store.aliases.find((binding) => binding.alias === alias) : undefined;
    if (!alias || !existing) {
      return failure(requestId, "E_INSTANCE_NOT_FOUND", `Target alias not found: ${alias ?? ""}.`, ["Run unitycli target alias list."]);
    }
    await writeAliasStore(dataDir, { version: 2, revision: store.revision + 1, aliases: store.aliases.filter((binding) => binding.alias !== alias) });
    return success(requestId, { alias, instanceId: existing.instanceId });
  }

  return failure(requestId, "UNKNOWN_TARGET_ALIAS_COMMAND", "Expected target alias list, target alias set <name|projectPath> <alias>, or target alias unset <alias>.");
}

/** 显式 instance flag 一旦出现就必须携带非空 selector，禁止降级到 CWD。 */
function explicitInstanceSelector(flags: Record<string, string | boolean>): string | undefined {
  if (!Object.prototype.hasOwnProperty.call(flags, "instance")) return undefined;
  const value = flags.instance;
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new InstanceRegistryError("E_INSTANCE_REQUIRED", "--instance requires a non-empty full instance id, project path, or alias.", [
      "Pass --instance <name|alias|projectPath>, or remove the flag to select from the current Unity project CWD."
    ]);
  }
  return value;
}

/** 离线 project constraint/discovery 使用 canonical identity，不依赖 active target 或单实例 fallback。 */
export async function currentInstanceProjectPath(
  flags: Record<string, string | boolean>,
  dataDir: string,
  cwd = process.cwd(),
): Promise<string | undefined> {
  try {
    const explicit = explicitInstanceSelector(flags);
    if (explicit === undefined) return resolveProjectIdentityFromCwd(cwd).displayProjectPath;
    const resolved = await resolveSelectorIdentity(explicit, dataDir);
    return resolved.identity.displayProjectPath;
  } catch {
    return undefined;
  }
}

/** 严格 selector：完整 ID/path/alias，或 CWD 中最近 Unity project root。 */
export async function selectInstanceForFlags(
  flags: Record<string, string | boolean>,
  dataDir: string,
  cwd = process.cwd(),
  target: InstanceTarget = "editor",
): Promise<InstanceSelection> {
  const selection = await resolveInstanceSelectionIdentity(flags, dataDir, cwd);
  const record = await readRegistryRecord(dataDir, selection.identity.instanceId);
  if (!record) throw missingSelectedInstance(selection.identity, selection.explicit, target);
  const observations = await probeProcessObservations(record.sessions.map((session) => session.processId));
  const instance = await reduceRegistryRecord(record, observations, Date.now(), target);
  assertRegistryIdentity(instance, selection.identity);
  // Editor 命令行 claim 只补充发现可正向归因的重复进程；正式启动不要求携带 -projectPath。
  const inspected = target === "editor"
    ? applyProjectProcessClaimInspection(instance, await inspectProjectProcessClaimsWithConvergence(selection.identity))
    : instance;
  if (inspected.status === "offline") throw targetNotRunning(inspected, target);
  if (inspected.status === "conflict") throw targetConflict(inspected, target);
  if (inspected.status === "unverifiable") throw targetUnverifiable(inspected, target);
  return { instance: inspected, matchedBy: selection.matchedBy };
}

/** 即时观察选择器保留 offline/conflict/blocked 状态，绝不把状态归约为 selector 异常。 */
/** 单次 WMI/进程枚举不可验证时只重做一次纯读证明；真实 duplicate claim 不等待。 */
async function inspectProjectProcessClaimsWithConvergence(identity: ProjectIdentity): Promise<ProjectProcessClaimInspection> {
  const first = await inspectProjectProcessClaims(identity, PROJECT_PROCESS_CLAIM_TIMEOUT_MS);
  if (first.verification !== "unverifiable") return first;
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 50));
  return inspectProjectProcessClaims(identity, PROJECT_PROCESS_CLAIM_TIMEOUT_MS);
}

export async function selectObservationInstanceForFlags(
  flags: Record<string, string | boolean>,
  dataDir: string,
  remainingMs?: number,
  cwd = process.cwd(),
  target: InstanceTarget = "editor",
): Promise<InstanceSelection> {
  const observationDeadlineMs = remainingMs === undefined ? undefined : Date.now() + Math.max(0, remainingMs);
  const selection = await resolveInstanceSelectionIdentity(flags, dataDir, cwd, observationDeadlineMs);
  let record: InstanceRegistryV3 | undefined;
  try {
    record = await readRegistryRecord(dataDir, selection.identity.instanceId, observationDeadlineMs);
  } catch (error) {
    if (!(error instanceof InstanceRegistryError)
      || (error.code !== "E_INSTANCE_IDENTITY_MISMATCH" && error.code !== "E_INSTANCE_UNVERIFIABLE")) throw error;
    // 启动中的当前Unity会重建不可读状态；observation只保留已解析的canonical identity供进程证明。
    return { instance: offlineObservationInstance(selection.identity, target), matchedBy: selection.matchedBy };
  }
  // observation 不要求 Bridge 已注册；canonical identity 仍可归因到启动中的 Unity process。
  if (!record) return { instance: offlineObservationInstance(selection.identity, target), matchedBy: selection.matchedBy };
  const processBudgetMs = Math.max(0, observationDeadlineMs === undefined ? PROCESS_OBSERVATION_TIMEOUT_MS : observationDeadlineMs - Date.now());
  const observations = await probeProcessObservations(record.sessions.map((session) => session.processId), processBudgetMs);
  const instance = reduceObservationRegistryRecord(record, observations, target);
  assertRegistryIdentity(instance, selection.identity);
  return { instance, matchedBy: selection.matchedBy };
}

/**
 * durable root 在 domain reload 中读取 journal 时不能依赖瞬时 /health ready。
 * 该 selector 要求唯一 Registry claim 的 PID、process start 与 Unity.exe 成立；可正向归因的命令行 claim 不能与其冲突。
 */
export async function selectTransitionInstanceForFlags(
  flags: Record<string, string | boolean>,
  dataDir: string,
  remainingMs: number,
  cwd = process.cwd(),
  target: InstanceTarget = "editor",
): Promise<InstanceSelection> {
  const deadlineMs = Date.now() + Math.max(0, remainingMs);
  const selection = await selectObservationInstanceForFlags(flags, dataDir, Math.max(0, deadlineMs - Date.now()), cwd, target);
  const instance = selection.instance;
  const session = instance.activeSession;
  if (!session || instance.status === "offline") throw targetNotRunning(instance, target);
  if (instance.status === "conflict") throw targetConflict(instance, target);
  if (instance.status === "unverifiable") throw targetUnverifiable(instance, target);

  const identity: RegistryIdentity = {
    instanceId: instance.instanceId,
    canonicalProjectPath: instance.canonicalProjectPath,
    displayProjectPath: instance.displayProjectPath,
  };
  const processBudgetMs = Math.max(0, deadlineMs - Date.now());
  const [verification, claims] = await Promise.all([
    verifyRegistrySessionProcess(identity, session, processBudgetMs),
    target === "editor" ? inspectProjectProcessClaims(identity, processBudgetMs) : Promise.resolve(undefined),
  ]);
  const contradictoryClaim = target === "editor"
    && claims?.verification === "verified"
    && claims.processIds.length > 0
    && (claims.processIds.length !== 1 || claims.processIds[0] !== session.processId);
  if (verification === "dead") throw targetNotRunning(instance, target);
  if (verification !== "verified" || contradictoryClaim) {
    throw targetUnverifiable({ ...instance, processClaimIds: claims?.processIds ?? [], ...(claims?.reason ? { claimReason: claims.reason } : {}) }, target);
  }
  return { instance: { ...instance, ...(claims ? { processClaimIds: claims.processIds } : {}) }, matchedBy: selection.matchedBy };
}

/** registry 尚未写入时的只读 canonical identity 投影；绝不由 TS 创建 registry。 */
function offlineObservationInstance(identity: ProjectIdentity, target: InstanceTarget): UnityInstance {
  return {
    instanceId: identity.instanceId, canonicalizationVersion: 1,
    canonicalProjectPath: identity.canonicalProjectPath, displayProjectPath: identity.displayProjectPath,
    projectPath: identity.displayProjectPath, projectName: projectNameFromPath(identity.displayProjectPath),
    status: "offline", target, revision: 0, updatedAt: new Date(0).toISOString(), sessions: [], allSessions: [], progress: [], blockers: [],
    claimVerification: "none",
  };
}

/** 两个 selector 共享 identity/flag 契约；仅 reducer 的可信度不同。 */
async function resolveInstanceSelectionIdentity(
  flags: Record<string, string | boolean>,
  dataDir: string,
  cwd: string,
  observationDeadlineMs?: number,
): Promise<{ identity: ProjectIdentity; matchedBy: InstanceSelection["matchedBy"]; explicit: string | undefined }> {
  if (stringFlag(flags, "bridge-endpoint")) {
    throw new InstanceRegistryError("E_SHELL_OPTION_OBSOLETE", "--bridge-endpoint has been removed; transport binding is owned by the session supervisor.", [
      "Pass --instance <name|alias|projectPath> or run from the target Unity project directory.",
    ]);
  }

  const remainingSelectorBudget = () => observationDeadlineMs === undefined
    ? undefined
    : Math.max(0, observationDeadlineMs - Date.now());
  const explicit = explicitInstanceSelector(flags);
  const resolved = explicit === undefined
    ? undefined
    : await resolveSelectorIdentity(explicit, dataDir, remainingSelectorBudget(), observationDeadlineMs);
  const identity = resolved?.identity ?? (observationDeadlineMs === undefined
    ? resolveProjectIdentityFromCwd(cwd)
    : await resolveProjectIdentityFromCwdAsync(cwd, { timeoutMs: remainingSelectorBudget() ?? 0 }));
  const matchedBy: InstanceSelection["matchedBy"] = resolved?.matchedBy ?? "cwd";

  const projectConstraint = stringFlag(flags, "project");
  if (projectConstraint) {
    const constraint = observationDeadlineMs === undefined
      ? resolveProjectIdentity(projectConstraint)
      : await resolveProjectIdentityAsync(projectConstraint, { timeoutMs: remainingSelectorBudget() ?? 0 });
    if (constraint.instanceId !== identity.instanceId) {
      throw new InstanceRegistryError("E_INSTANCE_PROJECT_MISMATCH", "The --project constraint does not match the selected Unity project identity.", [
        "Pass a --project path inside the same Unity project as --instance or CWD.",
      ], { selectedInstanceId: identity.instanceId, projectInstanceId: constraint.instanceId });
    }
  }

  return { identity, matchedBy, explicit };
}

function missingSelectedInstance(_identity: ProjectIdentity, explicit: string | undefined, target: InstanceTarget): InstanceRegistryError {
  const code = explicit && INSTANCE_ID_PATTERN.test(explicit) ? "E_INSTANCE_NOT_FOUND" : target === "runtime" ? "E_RUNTIME_NOT_RUNNING" : "E_INSTANCE_NOT_RUNNING";
  return new InstanceRegistryError(code, `No ${target} registry record exists for the selected project identity.`, [
    target === "runtime" ? "Launch a development Player built from this exact Unity project." : "Open this exact Unity project in Unity Editor.",
    "Run unitycli instances list to inspect known project identities.",
  ]);
}

/** 为 target 离线返回稳定且可执行的选择错误。 */
function targetNotRunning(_instance: UnityInstance, target: InstanceTarget): InstanceRegistryError {
  return new InstanceRegistryError(target === "runtime" ? "E_RUNTIME_NOT_RUNNING" : "E_INSTANCE_NOT_RUNNING", `The selected Unity project has no active ${target} session.`, [
    target === "runtime" ? "Launch a development Player built from this exact Unity project." : "Open this exact Unity project in Unity Editor.",
  ]);
}

/** conflict只表示已证明的同项目plural live claims。 */
function targetConflict(_instance: UnityInstance, target: InstanceTarget): InstanceRegistryError {
  return new InstanceRegistryError("E_INSTANCE_CONFLICT", `Multiple verified ${target} processes claim the selected canonical project identity.`, [
    `Close every duplicate ${target} process except the intended session.`,
    "Run unitycli instances list to inspect all verified claimants.",
  ]);
}

/** identity观察不完整时fail closed，但不得建议关闭并不存在的duplicate。 */
function targetUnverifiable(instance: UnityInstance, target: InstanceTarget): InstanceRegistryError {
  return new InstanceRegistryError("E_INSTANCE_UNVERIFIABLE", `The selected ${target} process identity could not be verified safely.`, unverifiableNextActions(instance.claimReason), instance.claimReason ? { reason: instance.claimReason } : undefined);
}

/** 即时观察只归约原始 registry：不得发 health 请求或扫描旧 PID。 */
export function reduceObservationRegistryRecord(record: InstanceRegistryV3, observations?: Map<number, ProcessObservation>, target: InstanceTarget = "editor"): UnityInstance {
  validateRegistryRecord(record);
  const allSessions = observations
    ? record.sessions.filter((session) => claimIsAlive(session, observations.get(session.processId)))
    : record.sessions;
  const sessions = allSessions.filter((session) => session.target === target);
  const verifiedSessions = observations
    ? sessions.filter((session) => observations.get(session.processId)?.state === "alive" && claimIsAlive(session, observations.get(session.processId)))
    : [];
  const processUnverifiable = observations === undefined || sessions.some((session) => observations.get(session.processId)?.state !== "alive");
  const verifiedProcessIdentities = new Set(verifiedSessions.map(processIdentityKey));
  const duplicateSessionClaim = verifiedSessions.length > 1 && verifiedProcessIdentities.size === 1;
  const activeSession = !processUnverifiable && verifiedSessions.length === 1 ? verifiedSessions[0] : undefined;
  const status: UnityInstanceStatus = verifiedProcessIdentities.size > 1
    ? "conflict"
    : (processUnverifiable || duplicateSessionClaim) && sessions.length > 0
      ? "unverifiable"
      : verifiedSessions.length === 0
        ? "offline"
        : activeSession!.blockers.length > 0 || activeSession!.lifecycle === "blocked"
          ? "blocked"
          : "transitioning";
  return {
    instanceId: record.identity.instanceId,
    canonicalizationVersion: 1,
    canonicalProjectPath: record.identity.canonicalProjectPath,
    displayProjectPath: record.identity.displayProjectPath,
    projectPath: record.identity.displayProjectPath,
    projectName: projectNameFromPath(record.identity.displayProjectPath),
    status,
    target,
    revision: record.revision,
    updatedAt: record.publishedAt,
    sessions,
    activeSession,
    allSessions,
    sessionId: activeSession?.sessionId,
    editorSessionId: activeSession?.editorSessionId,
    runtimeSessionId: activeSession?.runtimeSessionId,
    processId: activeSession?.processId,
    publishedAt: activeSession?.publishedAt,
    lastUnitySnapshot: activeSession?.lastUnitySnapshot,
    progress: activeSession?.progress ?? [],
    blockers: activeSession?.blockers ?? [],
    unityVersion: activeSession?.unityVersion,
    buildStamp: activeSession?.buildStamp,
    toolCatalogFingerprint: activeSession?.toolCatalogFingerprint,
    claimVerification: sessions.length === 0 ? "none" : processUnverifiable ? "unverifiable" : "verified",
    ...(status === "unverifiable" ? { claimReason: claimEvidenceReason(sessions, observations, verifiedSessions.length) } : {}),
  };
}

/**
 * fallback 前重新证明 registry claim 对应原 PID/start 的 Unity.exe。
 * 命令行存在 -projectPath 时必须匹配 canonical identity；正式 Hub/普通启动可以不携带该参数。
 */
export async function verifyRegistrySessionProcess(identity: RegistryIdentity, session: Pick<RegistrySession, "processId" | "processStartedAt" | "target" | "executablePath">, timeoutMs = 900): Promise<SessionProcessVerification> {
  if (process.platform !== "win32" || timeoutMs < 1) return "unverifiable";
  const startedAt = Date.now();
  const command = [
    "$ErrorActionPreference = 'Stop'",
    "Add-Type -TypeDefinition @'",
    "using System; using System.Runtime.InteropServices;",
    "public static class UnityCliArgv { [DllImport(\"shell32.dll\", CharSet=CharSet.Unicode)] public static extern IntPtr CommandLineToArgvW(string commandLine, out int argc); [DllImport(\"kernel32.dll\")] public static extern IntPtr LocalFree(IntPtr value); public static string[] Parse(string commandLine) { int argc; var memory=CommandLineToArgvW(commandLine,out argc); if(memory==IntPtr.Zero) return null; try { var result=new string[argc]; for(var i=0;i<argc;i++) result[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory,i*IntPtr.Size)); return result; } finally { LocalFree(memory); } } }",
    "'@",
    "$p = Get-CimInstance Win32_Process -Filter \"ProcessId=$env:UNITY_CLI_PROCESS_ID\" -ErrorAction Stop",
    "if ($null -eq $p) { @{ state='dead' } | ConvertTo-Json -Compress; exit }",
    "$args = [UnityCliArgv]::Parse($p.CommandLine)",
    "@{ state='alive'; processStartedAt=([System.Diagnostics.Process]::GetProcessById([int]$p.ProcessId).StartTime.ToUniversalTime().ToString('O')); executablePath=$p.ExecutablePath; argv=@($args) } | ConvertTo-Json -Compress -Depth 4",
  ].join("\n");
  const execution = await runPowerShellAsync(command, { UNITY_CLI_PROCESS_ID: String(session.processId) }, timeoutMs);
  if (execution.status !== 0 || execution.error) return "unverifiable";
  try {
    const raw = JSON.parse(execution.stdout.trim()) as Record<string, unknown>;
    if (raw.state === "dead") return "dead";
    if (raw.state !== "alive" || typeof raw.processStartedAt !== "string" || typeof raw.executablePath !== "string" || !Array.isArray(raw.argv)) return "unverifiable";
    if (Date.parse(raw.processStartedAt) !== Date.parse(session.processStartedAt)) return "unverifiable";
    if (session.target === "runtime") {
      if (!session.executablePath) return "unverifiable";
      return resolve(raw.executablePath).toLowerCase() === resolve(session.executablePath).toLowerCase() ? "verified" : "unverifiable";
    }
    if (basename(raw.executablePath).toLowerCase() !== "unity.exe") return "unverifiable";
    const argv = raw.argv.every((value) => typeof value === "string") ? raw.argv as string[] : undefined;
    const index = argv?.findIndex((value) => value.toLowerCase() === "-projectpath") ?? -1;
    if (index < 0) return "verified";
    if (!argv?.[index + 1]) return "unverifiable";
    const remainingMs = Math.max(0, timeoutMs - (Date.now() - startedAt));
    if (remainingMs <= 0) return "unverifiable";
    return (await resolveProjectIdentityAsync(argv[index + 1], { timeoutMs: remainingMs })).instanceId === identity.instanceId ? "verified" : "unverifiable";
  } catch {
    return "unverifiable";
  }
}

/** 将显式 --instance 解析为已验证的 Registry v3 canonical identity，供本地 journal 绑定请求。 */
export async function resolveInstanceJournalIdentity(flags: Record<string, string | boolean>, dataDir: string): Promise<InstanceJournalIdentity | undefined> {
  if (!Object.prototype.hasOwnProperty.call(flags, "instance")) return undefined;
  explicitInstanceSelector(flags);
  const selected = await selectInstanceForFlags(flags, dataDir);
  return {
    instanceId: selected.instance.instanceId,
    canonicalProjectPath: selected.instance.canonicalProjectPath,
  };
}

export async function hasInstanceForFlags(
  flags: Record<string, string | boolean>,
  dataDir: string,
  cwd = process.cwd(),
): Promise<boolean> {
  try {
    await selectInstanceForFlags(flags, dataDir, cwd);
    return true;
  } catch {
    return false;
  }
}

/** 将 identity/registry 失败保留为稳定 CLI envelope。 */
export function instanceSelectionFailure(requestId: string, error: unknown): CliResult | undefined {
  if (error instanceof InstanceRegistryError || error instanceof ProjectIdentityError) {
    const nextActions = error instanceof InstanceRegistryError ? error.nextActions : [
      "Pass --instance <name|alias|projectPath> or run from a local Unity project directory.",
    ];
    const details = error instanceof InstanceRegistryError ? error.details : error.details;
    return failure(requestId, error.code, error.message, nextActions, details, { transient: false, retryAfterMs: 0 });
  }
  return undefined;
}

export function instancesDir(dataDir = defaultDataDir()): string {
  return resolve(dataDir, "instances");
}

export function aliasesV2Path(dataDir = defaultDataDir()): string {
  return resolve(dataDir, "aliases-v2.json");
}

/** 只读全部合法v3 identity records；单个坏文件不能污染其他canonical project。 */
export async function readInstances(dataDir = defaultDataDir(), now = Date.now(), target: InstanceTarget | "all" = "all"): Promise<UnityInstance[]> {
  return (await readInstancesWithRegistryDiagnostics(dataDir, now, target)).instances;
}

/** 控制面可读取无效数量；具体文件名、路径和内容始终保持私有。 */
export async function readInstancesWithRegistryDiagnostics(
  dataDir = defaultDataDir(),
  now = Date.now(),
  target: InstanceTarget | "all" = "all",
): Promise<InstanceRegistryReadResult> {
  const { records, invalidRecordCount } = await readRegistryRecords(dataDir);
  const processObservations = await probeProcessObservations(records.flatMap((record) => record.sessions.map((session) => session.processId)));
  const reduced = await Promise.all(records.map((record) => reduceRegistryRecord(record, processObservations, now, target)));
  return {
    instances: reduced.sort((left, right) => left.canonicalProjectPath.localeCompare(right.canonicalProjectPath)),
    invalidRecordCount,
  };
}

/** CanonicalPath v1 补充进程观察；只返回可由显式 -projectPath 正向归因的 claim。 */
export async function inspectProjectProcessClaims(identity: RegistryIdentity | ProjectIdentity, timeoutMs = PROJECT_PROCESS_CLAIM_TIMEOUT_MS): Promise<ProjectProcessClaimInspection> {
  if (process.platform !== "win32") return { verification: "unsupported", processIds: [] };
  try {
    const processes = await findUnityProjectProcesses(identity.instanceId, identity.canonicalProjectPath, timeoutMs);
    const processClaims = processes.map((item) => ({ processId: item.processId, processStartedAt: item.processStartedAt }))
      .sort((left, right) => left.processId - right.processId);
    return { verification: "verified", processIds: [...new Set(processClaims.map((item) => item.processId))], processClaims };
  } catch (error) {
    return { verification: "unverifiable", reason: failureReasonFromError(error), processIds: [] };
  }
}

/** 将 OS 进程 claim 与 registry session 交叉验证；冲突时清除任何可执行 session 选择。 */
export function applyProjectProcessClaimInspection(instance: UnityInstance, inspection: ProjectProcessClaimInspection): UnityInstance {
  if (inspection.verification === "unsupported") return instance;
  const registeredEditorProcessIds = instance.allSessions.filter((session) => session.target === "editor").map((session) => session.processId);
  // 命令行扫描只提供额外正向证据；不能因正式启动缺少 -projectPath 否定 Registry/health 结论。
  if (registeredEditorProcessIds.length === 0
    || inspection.verification !== "verified"
    || inspection.processIds.length === 0) return instance;
  if (inspection.processIds.length > 1) {
    const blocker = {
      kind: "duplicate-project-process",
      title: "Multiple Unity Editor processes",
      message: `${inspection.processIds.length} Unity.exe processes claim the selected canonical project identity.`,
      requiresUserAction: true,
      canAutoResolve: false,
      source: "registry",
      confidence: "exact",
    };
    return {
      ...instance,
      status: "conflict",
      activeSession: undefined,
      sessionId: undefined,
      editorSessionId: undefined,
      runtimeSessionId: undefined,
      processId: undefined,
      progress: [],
      blockers: [...instance.blockers, blocker],
      claimVerification: "verified",
      processClaimIds: inspection.processIds,
    };
  }
  const exact = inspection.processIds.length === 1
    && registeredEditorProcessIds.length === 1
    && inspection.processIds[0] === registeredEditorProcessIds[0];
  // 命令行 claim 与registry claim互相矛盾才归因 mismatch；观察本身失败时保留原归因。
  return exact ? { ...instance, processClaimIds: inspection.processIds } : unverifiableInstance(instance, inspection.processIds, "mismatch");
}

/** 无法证明唯一ownership时清除任何可执行session，但绝不伪造duplicate blocker。 */
function unverifiableInstance(instance: UnityInstance, processClaimIds: number[], reason: IdentityEvidenceReason): UnityInstance {
  return {
    ...instance,
    status: "unverifiable",
    activeSession: undefined,
    sessionId: undefined,
    editorSessionId: undefined,
    runtimeSessionId: undefined,
    processId: undefined,
    progress: [],
    blockers: [],
    claimVerification: "unverifiable",
    claimReason: reason,
    processClaimIds,
  };
}

/** instances 控制面补充可正向归因的 OS process claims，使启动中的重复进程可见。 */
async function readInstancesWithProjectProcessClaims(dataDir: string): Promise<InstanceRegistryReadResult> {
  const { instances, invalidRecordCount } = await readInstancesWithRegistryDiagnostics(dataDir);
  return {
    instances: await Promise.all(instances.map(async (instance) => applyProjectProcessClaimInspection(instance, await inspectProjectProcessClaims({
      instanceId: instance.instanceId,
      canonicalProjectPath: instance.canonicalProjectPath,
      displayProjectPath: instance.displayProjectPath,
    })))),
    invalidRecordCount,
  };
}

/** 纯 reducer 可由测试注入 OS observations，禁止 TS 回写观察结果。 */
export async function reduceRegistryRecord(
  record: InstanceRegistryV3,
  observations: Map<number, ProcessObservation>,
  now = Date.now(),
  target: InstanceTarget | "all" = "editor",
): Promise<UnityInstance> {
  validateRegistryRecord(record);
  const allClaims = record.sessions.filter((session) => claimIsAlive(session, observations.get(session.processId)));
  const targets: InstanceTarget[] = target === "all" ? ["editor", "runtime"] : [target];
  const evaluations = await Promise.all(targets.map(async (candidateTarget) => {
    const claims = allClaims.filter((session) => session.target === candidateTarget);
    const verifiedClaims = claims.filter((session) => observations.get(session.processId)?.state === "alive" && claimIsAlive(session, observations.get(session.processId)));
    const verifiedProcessIdentities = new Set(verifiedClaims.map(processIdentityKey));
    const unverifiable = claims.some((session) => observations.get(session.processId)?.state !== "alive");
    if (verifiedProcessIdentities.size > 1) return { status: "conflict" as UnityInstanceStatus, claims, active: undefined, debugPause: null };
    if (unverifiable || verifiedClaims.length > 1 || (claims.length > 0 && verifiedClaims.length !== 1)) return { status: "unverifiable" as UnityInstanceStatus, claims, active: undefined, debugPause: null };
    const active = verifiedClaims[0];
    if (!active) return { status: "offline" as UnityInstanceStatus, claims, active: undefined, debugPause: null };
    if (active.blockers.length > 0 || active.lifecycle === "blocked") return { status: "blocked" as UnityInstanceStatus, claims, active, debugPause: null };
    if (lifecycleIsTransitioning(active.lifecycle) || active.progress.length > 0 || snapshotIsTransitioning(active.lastUnitySnapshot)) return { status: "transitioning" as UnityInstanceStatus, claims, active, debugPause: null };
    const health = await probeHealthIdentity(record.identity, active);
    if (!health) return { status: "unresponsive" as UnityInstanceStatus, claims, active, debugPause: null };
    if (lifecycleIsTransitioning(health.mainThread.lifecycle)) return { status: "transitioning" as UnityInstanceStatus, claims, active, debugPause: health.mainThread.debugPause };
    if (healthMainThreadIsFresh(health, now)) return { status: "ready" as UnityInstanceStatus, claims, active, debugPause: health.mainThread.debugPause };
    // pulse 停止推进且有当前 live publication 证明的故意主线程暂停时，只能报告 blocked；
    // 没有该证据时保持 unresponsive，绝不用旧文件或 worker 现场猜测因果。
    return { status: health.mainThread.debugPause ? "blocked" as UnityInstanceStatus : "unresponsive" as UnityInstanceStatus, claims, active, debugPause: health.mainThread.debugPause };
  }));
  // 暂停证据只来自本次 health publication；status 归约不因此改变。
  const mainThreadDebugPause = evaluations.find((evaluation) => evaluation.debugPause)?.debugPause ?? undefined;
  const priority: UnityInstanceStatus[] = ["conflict", "unverifiable", "blocked", "ready", "transitioning", "unresponsive", "offline"];
  const status = priority.find((candidate) => evaluations.some((evaluation) => evaluation.status === candidate)) ?? "offline";
  const claims = target === "all" ? allClaims : evaluations[0].claims;
  const activeCandidates = evaluations.flatMap((evaluation) => evaluation.active ? [evaluation.active] : []);
  const selected = target === "all" ? activeCandidates.length === 1 ? activeCandidates[0] : undefined : evaluations[0].active;
  return {
    instanceId: record.identity.instanceId,
    canonicalizationVersion: 1,
    canonicalProjectPath: record.identity.canonicalProjectPath,
    displayProjectPath: record.identity.displayProjectPath,
    projectPath: record.identity.displayProjectPath,
    projectName: projectNameFromPath(record.identity.displayProjectPath),
    status,
    ...(target === "all" ? {} : { target }),
    revision: record.revision,
    updatedAt: record.publishedAt,
    sessions: claims,
    activeSession: selected,
    allSessions: allClaims,
    sessionId: selected?.sessionId,
    editorSessionId: selected?.editorSessionId,
    runtimeSessionId: selected?.runtimeSessionId,
    processId: selected?.processId,
    publishedAt: selected?.publishedAt,
    lastUnitySnapshot: selected?.lastUnitySnapshot,
    progress: selected?.progress ?? [],
    blockers: selected?.blockers ?? [],
    unityVersion: selected?.unityVersion,
    buildStamp: selected?.buildStamp,
    toolCatalogFingerprint: selected?.toolCatalogFingerprint,
    ...(mainThreadDebugPause ? { mainThreadDebugPause } : {}),
    claimVerification: claims.length === 0 ? "none" : status === "unverifiable" ? "unverifiable" : "verified",
    ...(status === "unverifiable" ? { claimReason: claimEvidenceReason(allClaims, observations, allClaims.filter((session) => observations.get(session.processId)?.state === "alive").length) } : {}),
  };
}

/** public summary 不包含 endpoint、port 或 listener generation；null/空字段不输出以控制上下文。 */
export function instanceListSummary(instance: UnityInstance, name: string): Record<string, unknown> {
  return {
    name,
    canonicalProjectPath: instance.canonicalProjectPath,
    displayProjectPath: instance.displayProjectPath,
    status: instance.status,
    ...(instance.unityVersion ? { unityVersion: instance.unityVersion } : {}),
  };
}

/** 返回 doctor 应报告但产品不得解析的旧本地状态文件。 */
export async function obsoleteLocalStatePaths(dataDir: string): Promise<string[]> {
  const candidates = [resolve(dataDir, "config.json"), resolve(dataDir, "aliases.json"), resolve(dataDir, "instances.json")];
  const existing: string[] = [];
  for (const candidate of candidates) {
    try {
      if ((await stat(candidate)).isFile()) existing.push(candidate);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
  }
  return existing;
}

async function readRegistryRecords(dataDir: string): Promise<RegistryRecordCollection> {
  const names = await readCanonicalRegistryNames(dataDir);
  const records: InstanceRegistryV3[] = [];
  let invalidRecordCount = 0;
  for (const name of names) {
    try {
      const parsed = await readRegistrySnapshot(join(instancesDir(dataDir), name), true);
      if (`${parsed.identity.instanceId}.json` !== name) throw new Error("Registry filename does not match its canonical instanceId.");
      records.push(parsed);
    } catch {
      // 全量视图只隔离该文件；不读取旧格式、不修改现场，也不泄露路径或内容。
      invalidRecordCount += 1;
    }
  }
  return { records, invalidRecordCount };
}

/** 原子replace期间正式文件名可能短暂不可见；只重读目录，不读取或选择固定.tmp。 */
async function readCanonicalRegistryNames(dataDir: string): Promise<string[]> {
  const directory = instancesDir(dataDir);
  for (let attempt = 0; ; attempt += 1) {
    let names: string[];
    try {
      names = await readdir(directory);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return [];
      throw error;
    }
    const canonical = names
      .filter((entry) => entry.endsWith(".json") && INSTANCE_ID_PATTERN.test(entry.slice(0, -".json".length)))
      .sort();
    if (canonical.length > 0 || attempt >= REGISTRY_READ_RETRY_DELAYS_MS.length) return canonical;
    await new Promise((complete) => setTimeout(complete, REGISTRY_READ_RETRY_DELAYS_MS[attempt]));
  }
}

export interface RegistryInstanceDiagnostics {
  canonicalProjectPath: string;
  processStartedAtMs?: number;
}

/** 连接失败诊断专用：只读 registry 记录中的项目路径与本次进程启动时刻；失败一律返回 undefined，不改变原始错误。 */
export async function readRegistryInstanceDiagnostics(dataDir: string, instanceId: string, deadlineMs?: number): Promise<RegistryInstanceDiagnostics | undefined> {
  try {
    const record = await readRegistryRecord(dataDir, instanceId, deadlineMs);
    if (!record) return undefined;
    const starts = record.sessions.map((session) => Date.parse(session.processStartedAt)).filter((value) => Number.isFinite(value));
    return { canonicalProjectPath: record.identity.canonicalProjectPath, processStartedAtMs: starts.length > 0 ? Math.min(...starts) : undefined };
  } catch {
    return undefined;
  }
}

/** Observation paths already know the deterministic v3 filename and must not enumerate unrelated records. */
async function readRegistryRecord(dataDir: string, instanceId: string, deadlineMs?: number): Promise<InstanceRegistryV3 | undefined> {
  const filePath = join(instancesDir(dataDir), `${instanceId}.json`);
  try {
    const parsed = await readRegistrySnapshot(filePath, true, deadlineMs);
    if (parsed.identity.instanceId !== instanceId) throw new Error("Registry filename does not match its canonical instanceId.");
    return parsed;
  } catch (error) {
    const ioCode = (error as NodeJS.ErrnoException).code;
    if (ioCode === "ENOENT") return undefined;
    if (error instanceof InstanceRegistryError) throw error;
    if (ioCode === "EBUSY" || ioCode === "EPERM" || ioCode === "EACCES") {
      throw new InstanceRegistryError(
        "E_INSTANCE_UNVERIFIABLE",
        "The selected instance registry record could not be read safely.",
        ["Wait until the selected Unity project finishes publishing its instance record, then observe it again."],
        { instanceId },
      );
    }
    throw new InstanceRegistryError(
      "E_INSTANCE_IDENTITY_MISMATCH",
      "The selected instance registry record does not satisfy the v3 canonical identity contract.",
      ["Update or restart the selected Unity project with the matching UnityCLI package before selecting it again."],
      { instanceId },
    );
  }
}

function validateRegistryRecord(record: InstanceRegistryV3): void {
  const identity = record?.identity;
  const expectedInstanceId = identity && typeof identity.canonicalProjectPath === "string"
    ? `unity-${createHash("sha256").update(Buffer.from(identity.canonicalProjectPath, "utf8")).digest("hex")}`
    : "";
  if (!record || record.version !== 3 || record.canonicalizationVersion !== CANONICALIZATION_VERSION
    || !identity || !INSTANCE_ID_PATTERN.test(identity.instanceId) || identity.instanceId !== expectedInstanceId
    || typeof identity.canonicalProjectPath !== "string" || identity.canonicalProjectPath.length < 3
    || typeof identity.displayProjectPath !== "string" || identity.displayProjectPath.length < 3
    || !Array.isArray(record.sessions)
    || !Number.isInteger(record.revision) || record.revision < 0
    || !validTimestamp(record.publishedAt)) {
    throw new InstanceRegistryError("E_INSTANCE_IDENTITY_MISMATCH", "Instance registry does not satisfy the v3 canonical identity contract.");
  }

  const sessionIds = new Set<string>();
  const listenerIds = new Set<string>();
  for (const session of record.sessions) {
    const listener = session?.listener;
    const endpoint = listener?.endpoint;
    const snapshot = session?.lastUnitySnapshot as Record<string, unknown> | undefined;
    const expectedUrl = endpoint ? `http://127.0.0.1:${endpoint.actualPort}` : "";
    const targetIdentityValid = session?.target === "editor"
      ? session.editorSessionId === session.sessionId && session.runtimeSessionId === undefined
        && validEditorRegistrySnapshot(snapshot, session.sessionId, listener?.generation)
      : session?.target === "runtime"
        ? session.runtimeSessionId === session.sessionId && session.editorSessionId === undefined
          && typeof session.executablePath === "string" && isAbsolute(session.executablePath)
          && typeof session.buildId === "string" && /^[0-9a-f]{32}$/i.test(session.buildId)
          && validRuntimeRegistrySnapshot(snapshot, session.sessionId, listener?.generation)
        : false;
    if (!session || !SESSION_ID_PATTERN.test(session.sessionId) || sessionIds.has(session.sessionId)
      || !Number.isInteger(session.processId) || session.processId < 1
      || !validTimestamp(session.processStartedAt) || !validTimestamp(session.publishedAt)
      || !Number.isInteger(session.publicationSequence) || session.publicationSequence < 1
      || !REGISTRY_LIFECYCLES.has(session.lifecycle)
      || !listener || !LISTENER_ID_PATTERN.test(listener.listenerId) || listenerIds.has(listener.listenerId)
      || !Number.isInteger(listener.generation) || listener.generation < 1 || !validTimestamp(listener.startedAt)
      || !endpoint || endpoint.host !== "127.0.0.1"
      || !validPort(endpoint.preferredPort) || !validPort(endpoint.actualPort)
      || typeof endpoint.fallback !== "boolean" || endpoint.url !== expectedUrl
      || !targetIdentityValid
      || !Array.isArray(session.progress) || !Array.isArray(session.blockers)
      || typeof session.buildStamp !== "string" || session.buildStamp.length === 0) {
      throw new InstanceRegistryError("E_INSTANCE_IDENTITY_MISMATCH", "Instance registry contains an invalid v3 target session claim.", [], { instanceId: identity.instanceId });
    }
    sessionIds.add(session.sessionId);
    listenerIds.add(listener.listenerId);
  }
}

/** Registry snapshot 是完整v1单包；reader不允许从两次publication拼接字段。 */
function validEditorRegistrySnapshot(snapshot: Record<string, unknown> | undefined, editorSessionId: string, listenerGeneration: number | undefined): boolean {
  if (!snapshot || snapshot.schemaVersion !== 1 || snapshot.sourceEditorSessionId !== editorSessionId
    || snapshot.sourceListenerGeneration !== listenerGeneration || !validTimestamp(String(snapshot.observedAt ?? ""))
    || !hasExactKeys(snapshot, ["schemaVersion", "observedAt", "sourceEditorSessionId", "sourceListenerGeneration", "fields"])) return false;
  const fields = snapshot.fields;
  const names = ["compile", "import", "build", "playMode", "prefabStage", "activeScene", "runtime"];
  if (!fields || typeof fields !== "object" || Array.isArray(fields) || Object.keys(fields).length !== names.length) return false;
  return names.every((name) => {
    const field = (fields as Record<string, unknown>)[name];
    if (!field || typeof field !== "object" || Array.isArray(field)) return false;
    const raw = field as Record<string, unknown>;
    return typeof raw.known === "boolean"
      && Object.keys(raw).every((key) => key === "known" || key === "value")
      && (raw.known ? Object.prototype.hasOwnProperty.call(raw, "value") : !Object.prototype.hasOwnProperty.call(raw, "value"));
  });
}

/** Runtime snapshot 与 session/listener 同包归因；字段采用 bounded known/value 结构供后续状态能力扩展。 */
function validRuntimeRegistrySnapshot(snapshot: Record<string, unknown> | undefined, runtimeSessionId: string, listenerGeneration: number | undefined): boolean {
  if (!snapshot || snapshot.schemaVersion !== 1 || snapshot.sourceRuntimeSessionId !== runtimeSessionId
    || snapshot.sourceListenerGeneration !== listenerGeneration || !validTimestamp(String(snapshot.observedAt ?? ""))
    || !hasExactKeys(snapshot, ["schemaVersion", "observedAt", "sourceRuntimeSessionId", "sourceListenerGeneration", "fields"])) return false;
  const fields = snapshot.fields;
  if (!fields || typeof fields !== "object" || Array.isArray(fields)) return false;
  const entries = Object.entries(fields as Record<string, unknown>);
  if (entries.length < 1 || entries.length > 16) return false;
  return entries.every(([, field]) => {
    if (!field || typeof field !== "object" || Array.isArray(field)) return false;
    const raw = field as Record<string, unknown>;
    return typeof raw.known === "boolean"
      && Object.keys(raw).every((key) => key === "known" || key === "value")
      && (raw.known ? Object.prototype.hasOwnProperty.call(raw, "value") : !Object.prototype.hasOwnProperty.call(raw, "value"));
  });
}

function hasExactKeys(value: Record<string, unknown>, names: readonly string[]): boolean {
  const keys = Object.keys(value);
  return keys.length === names.length && names.every((name) => Object.prototype.hasOwnProperty.call(value, name));
}

function validTimestamp(value: string): boolean {
  return typeof value === "string" && Number.isFinite(Date.parse(value));
}

function validPort(value: number): boolean {
  return Number.isInteger(value) && value >= 1024 && value <= 65535;
}

/** 批量读取 PID start time；access denied 保留 unverifiable claim 并 fail closed。 */
export async function probeProcessObservations(processIds: number[], timeoutMs = PROCESS_OBSERVATION_TIMEOUT_MS): Promise<Map<number, ProcessObservation>> {
  const ids = [...new Set(processIds.filter((value) => Number.isInteger(value) && value > 0))];
  if (ids.length === 0) return new Map();
  const command = [
    "$ErrorActionPreference = 'SilentlyContinue'",
    "$ids = $env:UNITY_CLI_PROCESS_IDS | ConvertFrom-Json",
    "$items = foreach ($id in $ids) {",
    "  $process = Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue",
    "  if ($null -eq $process) { [pscustomobject]@{ processId=[int]$id; state='dead' }; continue }",
    "  try { [pscustomobject]@{ processId=[int]$id; state='alive'; processStartedAt=$process.StartTime.ToUniversalTime().ToString('O'); executablePath=$process.Path } }",
    "  catch { [pscustomobject]@{ processId=[int]$id; state='unverifiable'; error=[string]$_.Exception.Message } }",
    "}",
    "@($items) | ConvertTo-Json -Compress -Depth 3",
  ].join("\n");
  const execution = runPowerShell(command, { UNITY_CLI_PROCESS_IDS: JSON.stringify(ids) }, Math.max(1, timeoutMs));
  if (execution.status !== 0 || execution.error) {
    const reason = classifyIdentityEvidenceFailure(`${execution.stderr ?? ""} ${execution.error?.message ?? ""}`, execution.signal === "SIGTERM" || (execution.error as NodeJS.ErrnoException | undefined)?.code === "ETIMEDOUT");
    return new Map(ids.map((processId) => [processId, { processId, state: "unverifiable" as const, reason }]));
  }
  try {
    const line = execution.stdout.split(/\r?\n/).map((value) => value.trim()).filter(Boolean).at(-1);
    const raw = JSON.parse(line || "[]") as unknown;
    const items = Array.isArray(raw) ? raw : [raw];
    return new Map(items.filter((item): item is Record<string, unknown> => !!item && typeof item === "object").map((item) => {
      const processId = Number(item.processId);
      const state = item.state === "alive" || item.state === "dead" ? item.state : "unverifiable";
      return [processId, {
        processId,
        state,
        processStartedAt: typeof item.processStartedAt === "string" ? item.processStartedAt : undefined,
        executablePath: typeof item.executablePath === "string" ? item.executablePath : undefined,
        // 内层 catch 回传真实异常文本；缺失时退回脚本级 stderr 归因。
        ...(state === "unverifiable" ? { reason: classifyIdentityEvidenceFailure(typeof item.error === "string" ? item.error : execution.stderr, false) } : {}),
      } satisfies ProcessObservation];
    }));
  } catch {
    return new Map(ids.map((processId) => [processId, { processId, state: "unverifiable" as const, reason: "unavailable" as const }]));
  }
}

/** conflict按PID/start process identity计数，不能把同一进程的重复session误报为plural claim。 */
function processIdentityKey(session: RegistrySession): string {
  return `${session.processId}:${Date.parse(session.processStartedAt)}`;
}

function claimIsAlive(session: RegistrySession, observation: ProcessObservation | undefined): boolean {
  if (!observation || observation.state === "unverifiable") return true;
  if (observation.state === "dead") return false;
  const expected = Date.parse(session.processStartedAt);
  const actual = Date.parse(observation.processStartedAt ?? "");
  return Number.isFinite(expected) && Number.isFinite(actual) && expected === actual;
}

/** strict health 必须同时匹配 project/session/listener/port/build identity。 */
async function probeHealthIdentity(identity: RegistryIdentity, session: RegistrySession): Promise<HealthIdentity | undefined> {
  const listener = session.listener;
  const cacheKey = [identity.instanceId, session.target, session.sessionId, listener.listenerId, listener.generation, listener.endpoint.actualPort, session.buildStamp ?? "", session.buildId ?? ""].join("\0");
  const cached = healthCache.get(cacheKey);
  if (cached && Date.now() - cached.checkedAt <= HEALTH_CACHE_TTL_MS) return cached.identity;

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), HEALTH_CHECK_TIMEOUT_MS);
  try {
    const response = await fetch(new URL("/health", listener.endpoint.url), {
      signal: controller.signal,
      headers: {
        "x-unity-cli-expected-target": session.target,
        "x-unity-cli-expected-instance-id": identity.instanceId,
        "x-unity-cli-expected-session-id": session.sessionId,
        "x-unity-cli-expected-listener-id": listener.listenerId,
        "x-unity-cli-expected-listener-generation": String(listener.generation),
      },
    });
    if (!response.ok) return undefined;
    const envelope = await response.json() as { ok?: unknown; data?: unknown };
    if (envelope.ok !== true || !envelope.data || typeof envelope.data !== "object" || Array.isArray(envelope.data)) return undefined;
    const data = envelope.data as Record<string, unknown>;
    const rawMainThread = data.mainThread && typeof data.mainThread === "object" && !Array.isArray(data.mainThread) ? data.mainThread as Record<string, unknown> : undefined;
    const checkedAt = Date.now();
    const health: HealthIdentity = {
      version: Number(data.version) as 3,
      target: String(data.target ?? "") as InstanceTarget,
      instanceId: String(data.instanceId ?? ""),
      canonicalProjectPath: String(data.canonicalProjectPath ?? ""),
      canonicalizationVersion: Number(data.canonicalizationVersion) as 1,
      sessionId: String(data.sessionId ?? ""),
      listenerId: String(data.listenerId ?? ""),
      listenerGeneration: Number(data.listenerGeneration),
      actualPort: Number(data.actualPort),
      buildStamp: String(data.buildStamp ?? ""),
      ...(typeof data.buildId === "string" ? { buildId: data.buildId } : {}),
      mainThread: {
        observed: rawMainThread?.observed === true,
        sequence: Number(rawMainThread?.sequence),
        ageMs: rawMainThread?.ageMs === null ? null : Number(rawMainThread?.ageMs),
        listenerGeneration: Number(rawMainThread?.listenerGeneration),
        lifecycle: String(rawMainThread?.lifecycle ?? "") as RegistryLifecycle,
        debugPause: parseMainThreadDebugPause(rawMainThread?.debugPause),
      },
      checkedAt,
    };
    const pulse = health.mainThread;
    const pulseShapeValid = typeof pulse.observed === "boolean"
      && Number.isInteger(pulse.sequence) && pulse.sequence >= 0
      && (pulse.ageMs === null || Number.isInteger(pulse.ageMs) && pulse.ageMs >= 0)
      && Number.isInteger(pulse.listenerGeneration) && pulse.listenerGeneration >= 1
      && REGISTRY_LIFECYCLES.has(pulse.lifecycle)
      && (pulse.observed ? pulse.sequence > 0 && pulse.ageMs !== null : pulse.sequence === 0 && pulse.ageMs === null);
    if (health.version !== 3
      || health.target !== session.target
      || health.instanceId !== identity.instanceId
      || health.canonicalProjectPath !== identity.canonicalProjectPath
      || health.canonicalizationVersion !== 1
      || health.sessionId !== session.sessionId
      || health.listenerId !== listener.listenerId
      || health.listenerGeneration !== listener.generation
      || health.actualPort !== listener.endpoint.actualPort
      || health.buildStamp !== session.buildStamp
      || health.mainThread.listenerGeneration !== listener.generation
      || !pulseShapeValid
      || (session.target === "runtime" && health.buildId !== session.buildId)) return undefined;
    healthCache.set(cacheKey, { checkedAt, identity: health });
    return health;
  } catch {
    return undefined;
  } finally {
    clearTimeout(timeout);
  }
}

/** doctor 只消费已脱敏的严格 Health v3 结论；transport 与 pulse freshness 分开投影。 */
export async function inspectInstanceHealth(instance: UnityInstance, now = Date.now()): Promise<InstanceHealthInspection> {
  const session = instance.activeSession;
  if (!session) return { transportVerified: false };
  const health = await probeHealthIdentity({
    instanceId: instance.instanceId,
    canonicalProjectPath: instance.canonicalProjectPath,
    displayProjectPath: instance.displayProjectPath,
  }, session);
  if (!health) return { transportVerified: false };
  const ageMs = health.mainThread.ageMs === null
    ? null
    : health.mainThread.ageMs + Math.max(0, now - health.checkedAt);
  return {
    transportVerified: true,
    mainThread: {
      observed: health.mainThread.observed,
      sequence: health.mainThread.sequence,
      ageMs,
      lifecycle: health.mainThread.lifecycle,
      fresh: healthMainThreadIsFresh(health, now),
      debugPause: health.mainThread.debugPause,
    },
  };
}

/**
 * 故意暂停 publication 的严格解析：形状不完整时返回 null。
 * 它独立于 transport identity 与主线程 pulse，因此"可达"或"有 pulse"都不能冒充或否定它。
 */
function parseMainThreadDebugPause(value: unknown): MainThreadDebugPause | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const record = value as Record<string, unknown>;
  const pauseId = record.pauseId;
  const threadId = record.threadId;
  const publishedSequence = record.publishedSequence;
  if (record.mainThread !== true) return null;
  if (typeof pauseId !== "string" || pauseId.length === 0) return null;
  if (!Number.isInteger(threadId) || (threadId as number) < 1) return null;
  if (!Number.isInteger(publishedSequence) || (publishedSequence as number) < 1) return null;
  // 边界事实按存在即严格校验处理：形状非法时省略该字段，但暂停证据本身仍然成立。
  const timeoutKind = record.timeoutKind === "bounded" || record.timeoutKind === "unbounded" ? record.timeoutKind : undefined;
  const remainingMs = typeof record.remainingMs === "number" && Number.isFinite(record.remainingMs) && record.remainingMs >= 0 ? record.remainingMs : undefined;
  return {
    pauseId,
    threadId: threadId as number,
    publishedSequence: publishedSequence as number,
    ...(timeoutKind ? { timeoutKind } : {}),
    ...(remainingMs === undefined ? {} : { remainingMs }),
  };
}

/** cache停留时间必须计入服务端pulse age，避免旧health在TTL内伪装新鲜。 */
function healthMainThreadIsFresh(health: HealthIdentity, now = Date.now()): boolean {
  return health.mainThread.observed && health.mainThread.ageMs !== null
    && health.mainThread.ageMs + Math.max(0, now - health.checkedAt) <= MAIN_THREAD_PULSE_FRESH_MS;
}

function lifecycleIsTransitioning(lifecycle: RegistryLifecycle): boolean {
  return lifecycle !== "ready" && lifecycle !== "blocked";
}

/**
 * 公开选择名：项目名；同名实例按 canonicalProjectPath 稳定排序追加 -2、-3，且不占用其它实例的字面名。
 * 名称在全部已登记实例上计算，且结果内部唯一；instanceId 不参与对外选择。
 */
export function instanceNames(instances: readonly UnityInstance[]): Map<string, string> {
  const buckets = new Map<string, UnityInstance[]>();
  for (const instance of instances) {
    const base = instance.projectName?.trim() || instance.instanceId;
    const bucket = buckets.get(base);
    if (bucket) bucket.push(instance);
    else buckets.set(base, [instance]);
  }
  const names = new Map<string, string>();
  // 字面名先保留给自己的实例，生成的序号不得占用其它实例的字面名。
  const reserved = new Set(buckets.keys());
  const assigned = new Set<string>();
  for (const base of [...buckets.keys()].sort()) {
    const ordered = [...buckets.get(base)!].sort((left, right) => {
      if (left.canonicalProjectPath !== right.canonicalProjectPath) return left.canonicalProjectPath < right.canonicalProjectPath ? -1 : 1;
      return left.instanceId < right.instanceId ? -1 : 1;
    });
    ordered.forEach((instance, index) => {
      let suffix = index + 1;
      let candidate = suffix === 1 ? base : `${base}-${suffix}`;
      while (assigned.has(candidate) || (candidate !== base && reserved.has(candidate))) {
        suffix += 1;
        candidate = `${base}-${suffix}`;
      }
      assigned.add(candidate);
      names.set(instance.instanceId, candidate);
    });
  }
  return names;
}

/** 按公开选择名解析；注册表不可用时返回 undefined，由后续路径分支给出自己的错误。 */
async function resolveInstanceNameSelector(selector: string, dataDir: string): Promise<ProjectIdentity | undefined> {
  if (INSTANCE_ID_PATTERN.test(selector)) return undefined;
  const instances = await readInstances(dataDir, Date.now(), "all");
  const names = instanceNames(instances);
  const match = instances.find((instance) => names.get(instance.instanceId) === selector);
  return match
    ? projectIdentityFromRegistry({
      instanceId: match.instanceId,
      canonicalProjectPath: match.canonicalProjectPath,
      displayProjectPath: match.displayProjectPath,
    })
    : undefined;
}

/** 显式选择器的解析顺序：alias → 公开选择名 → 完整 instanceId（内部）→ 项目路径。 */
async function resolveSelectorIdentity(selector: string, dataDir: string, observationBudgetMs?: number, observationDeadlineMs?: number): Promise<{ identity: ProjectIdentity; matchedBy: "alias" | "name" | "instanceId" | "projectPath" }> {
  const store = await readAliasStore(dataDir);
  const alias = store.aliases.find((binding) => binding.alias === selector);
  if (alias) return { identity: projectIdentityFromBinding(alias), matchedBy: "alias" };
  // 路径形状的选择器不读注册表：公开名称不含路径分隔符，额外读取只会给每次调用增加延迟。
  const byName = looksLikePath(selector) ? undefined : await resolveInstanceNameSelector(selector, dataDir);
  if (byName) return { identity: byName, matchedBy: "name" };
  if (INSTANCE_ID_PATTERN.test(selector)) {
    // instanceId 已决定固定文件名；observation有截止时间时可等待启动中的writer首次发布。
    for (;;) {
      let record: InstanceRegistryV3 | undefined;
      try {
        record = await readRegistryRecord(dataDir, selector, observationDeadlineMs);
      } catch (error) {
        if (observationDeadlineMs === undefined || !(error instanceof InstanceRegistryError)
          || (error.code !== "E_INSTANCE_IDENTITY_MISMATCH" && error.code !== "E_INSTANCE_UNVERIFIABLE")) throw error;
      }
      if (record) return { identity: projectIdentityFromRegistry(record.identity), matchedBy: "instanceId" };
      const remainingMs = observationDeadlineMs === undefined ? 0 : observationDeadlineMs - Date.now();
      if (remainingMs <= 0) {
        throw new InstanceRegistryError("E_INSTANCE_NOT_FOUND", `Unity project identity not found: ${selector}.`, ["Pass an existing project name, alias, or path, or run unitycli instances list."]);
      }
      await new Promise((complete) => setTimeout(complete, Math.min(100, remainingMs)));
    }
  }
  if (!looksLikePath(selector)) {
    throw new InstanceRegistryError("E_INSTANCE_NOT_FOUND", `Unity project name or alias not found: ${selector}.`, ["Run unitycli instances list or unitycli target alias list, or pass a project path."]);
  }
  const identity = observationBudgetMs === undefined
    ? resolveProjectIdentity(selector)
    : await resolveProjectIdentityAsync(selector, { timeoutMs: observationBudgetMs });
  return { identity, matchedBy: "projectPath" };
}

/** target alias 的输入面：公开选择名或项目路径，或完整 instanceId。 */
async function resolveAliasIdentity(selector: string, dataDir: string): Promise<ProjectIdentity> {
  if (!INSTANCE_ID_PATTERN.test(selector)) {
    const byName = await resolveInstanceNameSelector(selector, dataDir);
    return byName ?? resolveProjectIdentity(selector);
  }
  const record = await readRegistryRecord(dataDir, selector);
  if (!record) throw new InstanceRegistryError("E_INSTANCE_NOT_FOUND", `Unity project identity not found: ${selector}.`, ["Pass an existing Unity project name or path."]);
  return projectIdentityFromRegistry(record.identity);
}

function projectIdentityFromBinding(binding: AliasBinding): ProjectIdentity {
  return {
    canonicalizationVersion: 1,
    instanceId: binding.instanceId,
    identityHash: binding.instanceId.slice("unity-".length),
    canonicalProjectPath: binding.canonicalProjectPath,
    displayProjectPath: binding.displayProjectPath,
  };
}

function projectIdentityFromRegistry(identity: RegistryIdentity): ProjectIdentity {
  return {
    canonicalizationVersion: 1,
    instanceId: identity.instanceId,
    identityHash: identity.instanceId.slice("unity-".length),
    canonicalProjectPath: identity.canonicalProjectPath,
    displayProjectPath: identity.displayProjectPath,
  };
}

function assertRegistryIdentity(instance: UnityInstance, identity: ProjectIdentity): void {
  if (instance.canonicalProjectPath !== identity.canonicalProjectPath || instance.canonicalizationVersion !== identity.canonicalizationVersion) {
    throw new InstanceRegistryError("E_INSTANCE_IDENTITY_MISMATCH", "Registry identity differs from CanonicalPath v1 for the selected project.", [], {
      instanceId: identity.instanceId,
      expectedCanonicalProjectPath: identity.canonicalProjectPath,
      registryCanonicalProjectPath: instance.canonicalProjectPath,
    });
  }
}

async function readAliasStore(dataDir: string): Promise<AliasStoreV2> {
  try {
    const parsed = JSON.parse(stripBom(await readFile(aliasesV2Path(dataDir), "utf8"))) as AliasStoreV2;
    if (parsed.version !== 2 || !Number.isInteger(parsed.revision) || !Array.isArray(parsed.aliases)) {
      throw new InstanceRegistryError("E_INSTANCE_IDENTITY_MISMATCH", "Alias store does not satisfy the v2 identity contract.");
    }
    const names = new Set<string>();
    for (const binding of parsed.aliases) {
      if (!ALIAS_PATTERN.test(binding.alias) || names.has(binding.alias)
        || !INSTANCE_ID_PATTERN.test(binding.instanceId)
        || binding.canonicalizationVersion !== 1) {
        throw new InstanceRegistryError("E_INSTANCE_IDENTITY_MISMATCH", "Alias store contains an invalid or duplicate canonical identity binding.");
      }
      names.add(binding.alias);
    }
    return parsed;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return { version: 2, revision: 0, aliases: [] };
    throw error;
  }
}

/** alias store 使用同目录临时文件原子替换；旧 aliases.json 从不读取。 */
async function writeAliasStore(dataDir: string, store: AliasStoreV2): Promise<void> {
  const filePath = aliasesV2Path(dataDir);
  await mkdir(dirname(filePath), { recursive: true });
  const temporaryPath = `${filePath}.${process.pid}.${randomUUID()}.tmp`;
  await writeFile(temporaryPath, `${JSON.stringify(store, null, 2)}\n`, "utf8");
  try {
    await rename(temporaryPath, filePath);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "EEXIST" && (error as NodeJS.ErrnoException).code !== "EPERM") throw error;
    await rm(filePath, { force: true });
    await rename(temporaryPath, filePath);
  } finally {
    await rm(temporaryPath, { force: true });
  }
}

function aliasBinding(alias: string, identity: ProjectIdentity): AliasBinding {
  return {
    alias,
    instanceId: identity.instanceId,
    canonicalProjectPath: identity.canonicalProjectPath,
    displayProjectPath: identity.displayProjectPath,
    canonicalizationVersion: 1,
  };
}

/** reader只消费正式固定路径；瞬时parse failure也在同一absolute deadline内有界重读。 */
async function readRegistrySnapshot(filePath: string, retryMissing = false, deadlineMs?: number): Promise<InstanceRegistryV3> {
  for (let attempt = 0; ; attempt += 1) {
    try {
      const parsed = JSON.parse(stripBom(await readRegistryText(filePath, retryMissing, deadlineMs))) as InstanceRegistryV3;
      validateRegistryRecord(parsed);
      return parsed;
    } catch (error) {
      if (!(error instanceof SyntaxError) || attempt >= REGISTRY_READ_RETRY_DELAYS_MS.length || (deadlineMs !== undefined && Date.now() >= deadlineMs)) throw error;
      const delayMs = Math.min(REGISTRY_READ_RETRY_DELAYS_MS[attempt], deadlineMs === undefined ? REGISTRY_READ_RETRY_DELAYS_MS[attempt] : Math.max(0, deadlineMs - Date.now()));
      if (delayMs <= 0) throw error;
      await new Promise((complete) => setTimeout(complete, delayMs));
    }
  }
}

/** Windows replace/open 的瞬时 sharing error 有界重读。 */
async function readRegistryText(filePath: string, retryMissing = false, deadlineMs?: number): Promise<string> {
  for (let attempt = 0; ; attempt += 1) {
    try {
      return await readFile(filePath, "utf8");
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      const retryable = (retryMissing && code === "ENOENT") || code === "EBUSY" || code === "EPERM" || code === "EACCES";
      if (!retryable || attempt >= REGISTRY_READ_RETRY_DELAYS_MS.length || (deadlineMs !== undefined && Date.now() >= deadlineMs)) throw error;
      const delayMs = Math.min(REGISTRY_READ_RETRY_DELAYS_MS[attempt], deadlineMs === undefined ? REGISTRY_READ_RETRY_DELAYS_MS[attempt] : Math.max(0, deadlineMs - Date.now()));
      if (delayMs <= 0) throw error;
      await new Promise((complete) => setTimeout(complete, delayMs));
    }
  }
}

function looksLikePath(value: string): boolean {
  return isAbsolute(value) || value.includes("\\") || value.includes("/") || value === "." || value.startsWith("..\\");
}

function projectNameFromPath(projectPath: string): string {
  return projectPath.split(/[\\/]/).filter(Boolean).at(-1) ?? projectPath;
}

/** 原始 snapshot 的 busy 字段只用于 observation reducer，不得替代 live Bridge 证明。 */
function snapshotIsTransitioning(snapshot: Record<string, unknown>): boolean {
  const fields = snapshot?.fields;
  if (!fields || typeof fields !== "object" || Array.isArray(fields)) return false;
  const field = fields as Record<string, unknown>;
  for (const name of ["compile", "import", "build"]) {
    const value = field[name];
    if (value && typeof value === "object" && !Array.isArray(value) && (value as Record<string, unknown>).value === true) return true;
  }
  const playMode = field.playMode;
  return !!playMode && typeof playMode === "object" && !Array.isArray(playMode)
    && (playMode as Record<string, unknown>).value === "transitioning";
}

function runPowerShell(command: string, env: Record<string, string>, timeoutMs: number) {
  const executable = process.env.SystemRoot
    ? resolve(process.env.SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    : "powershell.exe";
  return spawnSync(executable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Buffer.from(command, "utf16le").toString("base64")], {
    encoding: "utf8",
    windowsHide: true,
    timeout: timeoutMs,
    maxBuffer: 4 * 1024 * 1024,
    env: { ...process.env, ...env },
  });
}

/** fallback 校验不得阻塞 Node event loop；超时后终止子进程并 fail closed。 */
function runPowerShellAsync(command: string, env: Record<string, string>, timeoutMs: number): Promise<{ status: number | null; error?: Error; stdout: string }> {
  const executable = process.env.SystemRoot
    ? resolve(process.env.SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    : "powershell.exe";
  return new Promise((complete) => {
    const child = spawn(executable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Buffer.from(command, "utf16le").toString("base64")], {
      windowsHide: true,
      env: { ...process.env, ...env },
    });
    let stdout = "";
    let settled = false;
    const timeout = setTimeout(() => {
      child.kill();
      finish({ status: null, error: new Error("PowerShell probe timed out."), stdout });
    }, Math.max(1, timeoutMs));
    const finish = (result: { status: number | null; error?: Error; stdout: string }) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      complete(result);
    };
    child.stdout.on("data", (chunk: Buffer | string) => { stdout += chunk.toString(); });
    child.once("error", (error) => finish({ status: null, error, stdout }));
    child.once("close", (status) => finish({ status, stdout }));
  });
}
