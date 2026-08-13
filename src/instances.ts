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

export const HEARTBEAT_FRESH_MS = 10_000;
export const HEALTH_CHECK_TIMEOUT_MS = 300;
export const HEALTH_CACHE_TTL_MS = 1_000;
const REGISTRY_READ_RETRY_DELAYS_MS = [5, 10, 20, 40] as const;
const INSTANCE_ID_PATTERN = /^unity-[0-9a-f]{64}$/;
const SESSION_ID_PATTERN = /^session-[0-9a-f]{64}$/;
const LISTENER_ID_PATTERN = /^listener-[0-9a-f]{32}$/;
const ALIAS_PATTERN = /^[A-Za-z0-9._-]{1,64}$/;

export type UnityInstanceStatus = "ready" | "transitioning" | "blocked" | "offline" | "conflict";
export type InstanceTarget = "editor" | "runtime";

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
  lastHeartbeatAt: string;
  claimVerification: "verified" | "unverifiable";
  state: "ready" | "transitioning" | "blocked";
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

export interface InstanceRegistryV2 {
  version: 2;
  revision: number;
  canonicalizationVersion: 1;
  identity: RegistryIdentity;
  sessions: RegistrySession[];
  updatedAt: string;
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
  lastHeartbeatAt?: string;
  lastUnitySnapshot?: Record<string, unknown>;
  progress: Array<Record<string, unknown>>;
  blockers: Array<Record<string, unknown>>;
  unityVersion?: string;
  buildStamp?: string;
  toolCatalogFingerprint?: string;
  claimVerification: "verified" | "unverifiable" | "none";
  processClaimIds?: number[];
}

export interface ProjectProcessClaimInspection {
  verification: "verified" | "unverifiable" | "unsupported";
  processIds: number[];
  processClaims?: Array<{ processId: number; processStartedAt: string }>;
}

export interface InstanceSelection {
  instance: UnityInstance;
  matchedBy: "alias" | "instanceId" | "projectPath" | "cwd";
}

/** journal 等本地 mutation 在 acceptance 前只读取 v2 registry identity，不依赖 CWD 推断。 */
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
}

export type SessionProcessVerification = "verified" | "dead" | "unverifiable";

interface HealthIdentity {
  version: 2;
  target: InstanceTarget;
  instanceId: string;
  canonicalProjectPath: string;
  canonicalizationVersion: 1;
  sessionId: string;
  listenerId: string;
  actualPort: number;
  buildStamp: string;
  buildId?: string;
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

/** 顶层 instances 控制面只读 registry；prune 由 C# writer 的 PID/start-time reducer 完成。 */
export async function handleInstancesCommand(
  command: string | undefined,
  _flags: Record<string, string | boolean>,
  dataDir: string,
  requestId: string,
): Promise<CliResult> {
  if (command === "list" || !command) {
    const instances = await readInstancesWithProjectProcessClaims(dataDir);
    return success(requestId, { instances: instances.map(instanceListSummary) });
  }

  if (command === "prune") {
    const instances = await readInstancesWithProjectProcessClaims(dataDir);
    return success(requestId, {
      removed: 0,
      remaining: instances.length,
      writer: "unity-bridge",
      note: "Only the C# registry writer may remove PID/start-time-invalid claims or expired offline records.",
      instances: instances.map(instanceListSummary),
    });
  }

  return failure(requestId, "UNKNOWN_INSTANCES_COMMAND", "Expected instances list or instances prune.", [
    "Run unity_cli instances list to inspect project identities and Editor sessions.",
    "Run unity_cli instances prune to request writer-owned cleanup.",
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
      "Run unity_cli target alias list.",
      "Pass --instance <instanceId|projectPath|alias> or run from a Unity project directory.",
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
      return failure(requestId, "E_INSTANCE_NOT_FOUND", "target alias set requires <projectPath|instanceId> <alias>.", [
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
          "Run unity_cli target alias unset <alias> before assigning it.",
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
      return failure(requestId, "E_INSTANCE_NOT_FOUND", `Target alias not found: ${alias ?? ""}.`, ["Run unity_cli target alias list."]);
    }
    await writeAliasStore(dataDir, { version: 2, revision: store.revision + 1, aliases: store.aliases.filter((binding) => binding.alias !== alias) });
    return success(requestId, { alias, instanceId: existing.instanceId });
  }

  return failure(requestId, "UNKNOWN_TARGET_ALIAS_COMMAND", "Expected target alias list, target alias set <projectPath|instanceId> <alias>, or target alias unset <alias>.");
}

/** 显式 instance flag 一旦出现就必须携带非空 selector，禁止降级到 CWD。 */
function explicitInstanceSelector(flags: Record<string, string | boolean>): string | undefined {
  if (!Object.prototype.hasOwnProperty.call(flags, "instance")) return undefined;
  const value = flags.instance;
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new InstanceRegistryError("E_INSTANCE_REQUIRED", "--instance requires a non-empty full instance id, project path, or alias.", [
      "Pass --instance <full-id|projectPath|alias>, or remove the flag to select from the current Unity project CWD."
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
    const identity = await resolveSelectorIdentity(explicit, dataDir);
    return identity.displayProjectPath;
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
  // Editor 额外证明 -projectPath claim；Player 的项目归因由 build sidecar、进程路径与 health attestation 联合证明。
  const inspected = target === "editor"
    ? applyProjectProcessClaimInspection(instance, await inspectProjectProcessClaimsWithConvergence(selection.identity))
    : instance;
  if (inspected.status === "offline") throw targetNotRunning(inspected, target);
  if (inspected.status === "conflict") throw targetConflict(inspected, target);
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
  const record = await readRegistryRecord(dataDir, selection.identity.instanceId, observationDeadlineMs);
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
 * 该 selector 仍要求唯一 PID、process start、Unity.exe 与 canonical -projectPath 全部独立成立。
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
  const exactClaim = target === "runtime" || (claims?.verification === "verified"
    && claims.processIds.length === 1
    && claims.processIds[0] === session.processId);
  if (verification === "dead") throw targetNotRunning(instance, target);
  if (verification !== "verified" || !exactClaim) {
    throw new InstanceRegistryError("E_INSTANCE_CONFLICT", `The selected ${target} process identity cannot be proven during transition recovery.`, ["Wait for the selected process identity to become uniquely verifiable."], { instance: instanceListSummary(instance), processClaimIds: claims?.processIds ?? [] });
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
    throw new InstanceRegistryError("E_OBSOLETE_OPTION", "--bridge-endpoint has been removed; transport binding is owned by the session supervisor.", [
      "Pass --instance <instanceId|projectPath|alias> or run from the target Unity project directory.",
    ]);
  }

  const remainingSelectorBudget = () => observationDeadlineMs === undefined
    ? undefined
    : Math.max(0, observationDeadlineMs - Date.now());
  const explicit = explicitInstanceSelector(flags);
  const identity = explicit !== undefined
    ? await resolveSelectorIdentity(explicit, dataDir, remainingSelectorBudget(), observationDeadlineMs)
    : observationDeadlineMs === undefined
      ? resolveProjectIdentityFromCwd(cwd)
      : await resolveProjectIdentityFromCwdAsync(cwd, { timeoutMs: remainingSelectorBudget() ?? 0 });
  const matchedBy: InstanceSelection["matchedBy"] = explicit !== undefined
    ? (await readAliasStore(dataDir)).aliases.some((binding) => binding.alias === explicit)
      ? "alias"
      : INSTANCE_ID_PATTERN.test(explicit)
        ? "instanceId"
        : "projectPath"
    : "cwd";

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

function missingSelectedInstance(identity: ProjectIdentity, explicit: string | undefined, target: InstanceTarget): InstanceRegistryError {
  const code = explicit && INSTANCE_ID_PATTERN.test(explicit) ? "E_INSTANCE_NOT_FOUND" : target === "runtime" ? "E_RUNTIME_NOT_RUNNING" : "E_INSTANCE_NOT_RUNNING";
  return new InstanceRegistryError(code, `No ${target} registry record exists for the selected project identity.`, [
    target === "runtime" ? "Launch a development Player built from this exact Unity project." : "Open this exact Unity project in Unity Editor.",
    "Run unity_cli instances list to inspect known project identities.",
  ], { instanceId: identity.instanceId, canonicalProjectPath: identity.canonicalProjectPath, target });
}

/** 为 target 离线返回稳定且可执行的选择错误。 */
function targetNotRunning(instance: UnityInstance, target: InstanceTarget): InstanceRegistryError {
  return new InstanceRegistryError(target === "runtime" ? "E_RUNTIME_NOT_RUNNING" : "E_INSTANCE_NOT_RUNNING", `The selected Unity project has no active ${target} session.`, [
    target === "runtime" ? "Launch a development Player built from this exact Unity project." : "Open this exact Unity project in Unity Editor.",
  ], { instance: instanceListSummary(instance), target });
}

/** 同一 target 的多个或不可验证 claim 必须 fail closed；另一 target 的正常 session 不参与冲突。 */
function targetConflict(instance: UnityInstance, target: InstanceTarget): InstanceRegistryError {
  return new InstanceRegistryError("E_INSTANCE_CONFLICT", `The selected ${target} process identity cannot be proven uniquely.`, [
    `Close every duplicate ${target} process except the intended session.`,
    "Run unity_cli instances list to inspect all claimants.",
  ], { instance: instanceListSummary(instance), target });
}

/** 即时观察只归约原始 registry：不得发 health 请求或扫描旧 PID。 */
export function reduceObservationRegistryRecord(record: InstanceRegistryV2, observations?: Map<number, ProcessObservation>, target: InstanceTarget = "editor"): UnityInstance {
  validateRegistryRecord(record);
  // 传入 OS observation 时只剔除已证明 dead/start-mismatch 的 claim；无法证明的 claim 继续形成 conflict。
  const allSessions = observations
    ? record.sessions.filter((session) => claimIsAlive(session, observations.get(session.processId)))
    : record.sessions;
  const sessions = allSessions.filter((session) => session.target === target);
  const processUnverifiable = observations !== undefined && sessions.some((session) => observations.get(session.processId)?.state === "unverifiable");
  const activeSession = sessions.length === 1 ? sessions[0] : undefined;
  const status: UnityInstanceStatus = sessions.length === 0
    ? "offline"
    : sessions.length > 1 || processUnverifiable
      ? "conflict"
      : activeSession!.claimVerification === "unverifiable"
        ? "conflict"
      : activeSession!.blockers.length > 0 || activeSession!.state === "blocked"
        ? "blocked"
        : activeSession!.state === "transitioning" || activeSession!.progress.length > 0 || snapshotIsTransitioning(activeSession!.lastUnitySnapshot)
          ? "transitioning"
          : "ready";
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
    updatedAt: record.updatedAt,
    sessions,
    activeSession,
    allSessions,
    sessionId: activeSession?.sessionId,
    editorSessionId: activeSession?.editorSessionId,
    runtimeSessionId: activeSession?.runtimeSessionId,
    processId: activeSession?.processId,
    lastHeartbeatAt: activeSession?.lastHeartbeatAt,
    lastUnitySnapshot: activeSession?.lastUnitySnapshot,
    progress: activeSession?.progress ?? [],
    blockers: activeSession?.blockers ?? [],
    unityVersion: activeSession?.unityVersion,
    buildStamp: activeSession?.buildStamp,
    toolCatalogFingerprint: activeSession?.toolCatalogFingerprint,
    claimVerification: sessions.length === 0 ? "none" : sessions.length > 1 || processUnverifiable ? "unverifiable" : activeSession!.claimVerification,
  };
}

/**
 * fallback 前重新证明 registry claim 对应目标 Unity.exe。
 * CommandLineToArgvW 解析 -projectPath，任一无法读取或不匹配均 fail closed。
 */
export async function verifyRegistrySessionProcess(identity: RegistryIdentity, session: RegistrySession, timeoutMs = 900): Promise<SessionProcessVerification> {
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
    if (index < 0 || !argv?.[index + 1]) return "unverifiable";
    const remainingMs = Math.max(0, timeoutMs - (Date.now() - startedAt));
    if (remainingMs <= 0) return "unverifiable";
    return (await resolveProjectIdentityAsync(argv[index + 1], { timeoutMs: remainingMs })).instanceId === identity.instanceId ? "verified" : "unverifiable";
  } catch {
    return "unverifiable";
  }
}

/** 将显式 --instance 解析为已验证的 registry v2 canonical identity，供本地 journal 绑定请求。 */
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
      "Pass --instance <instanceId|canonicalProjectPath|alias> or run from a local Unity project directory.",
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

/** 只读全部 v2 identity records；TS 不提供 registry writer。all 视图只用于控制面展示，业务执行必须显式选择 target。 */
export async function readInstances(dataDir = defaultDataDir(), now = Date.now(), target: InstanceTarget | "all" = "all"): Promise<UnityInstance[]> {
  const records = await readRegistryRecords(dataDir);
  const processObservations = await probeProcessObservations(records.flatMap((record) => record.sessions.map((session) => session.processId)));
  const reduced = await Promise.all(records.map((record) => reduceRegistryRecord(record, processObservations, now, target)));
  return reduced.sort((left, right) => left.canonicalProjectPath.localeCompare(right.canonicalProjectPath));
}

/** CanonicalPath v1 进程 claim 观察；Windows 无法完成枚举时返回 unverifiable 供调用方 fail closed。 */
export async function inspectProjectProcessClaims(identity: RegistryIdentity | ProjectIdentity, timeoutMs = PROJECT_PROCESS_CLAIM_TIMEOUT_MS): Promise<ProjectProcessClaimInspection> {
  if (process.platform !== "win32") return { verification: "unsupported", processIds: [] };
  try {
    const processes = await findUnityProjectProcesses(identity.instanceId, identity.canonicalProjectPath, timeoutMs);
    const processClaims = processes.map((item) => ({ processId: item.processId, processStartedAt: item.processStartedAt }))
      .sort((left, right) => left.processId - right.processId);
    return { verification: "verified", processIds: [...new Set(processClaims.map((item) => item.processId))], processClaims };
  } catch {
    return { verification: "unverifiable", processIds: [] };
  }
}

/** 将 OS 进程 claim 与 registry session 交叉验证；冲突时清除任何可执行 session 选择。 */
export function applyProjectProcessClaimInspection(instance: UnityInstance, inspection: ProjectProcessClaimInspection): UnityInstance {
  if (inspection.verification === "unsupported") return instance;
  const registeredEditorProcessIds = instance.allSessions.filter((session) => session.target === "editor").map((session) => session.processId);
  // registry session 已独立证明 selected PID/start/path/health；补充扫描只能增加 Editor target 的已观察冲突。
  if (inspection.verification !== "verified") return instance;
  const conflicts = inspection.processIds.length > 1
    || (inspection.processIds.length === 1 && registeredEditorProcessIds.length === 1 && inspection.processIds[0] !== registeredEditorProcessIds[0]);
  if (!conflicts) return { ...instance, processClaimIds: inspection.processIds };
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
    claimVerification: "unverifiable",
    processClaimIds: inspection.processIds,
  };
}

/** instances 控制面补充 OS process claims，使未注册 Bridge 的启动中重复进程也可见。 */
async function readInstancesWithProjectProcessClaims(dataDir: string): Promise<UnityInstance[]> {
  const instances = await readInstances(dataDir);
  return Promise.all(instances.map(async (instance) => applyProjectProcessClaimInspection(instance, await inspectProjectProcessClaims({
    instanceId: instance.instanceId,
    canonicalProjectPath: instance.canonicalProjectPath,
    displayProjectPath: instance.displayProjectPath,
  }))));
}

/** 纯 reducer 可由测试注入 OS observations，禁止 TS 回写观察结果。 */
export async function reduceRegistryRecord(
  record: InstanceRegistryV2,
  observations: Map<number, ProcessObservation>,
  now = Date.now(),
  target: InstanceTarget | "all" = "editor",
): Promise<UnityInstance> {
  validateRegistryRecord(record);
  const allClaims = record.sessions.filter((session) => claimIsAlive(session, observations.get(session.processId)));
  const targets: InstanceTarget[] = target === "all" ? ["editor", "runtime"] : [target];
  const evaluations = await Promise.all(targets.map(async (candidateTarget) => {
    const claims = allClaims.filter((session) => session.target === candidateTarget);
    const unverifiable = claims.some((session) => {
      const observation = observations.get(session.processId);
      return session.claimVerification === "unverifiable" || observation?.state === "unverifiable";
    });
    if (claims.length > 1 || unverifiable) return { target: candidateTarget, status: "conflict" as UnityInstanceStatus, claims };
    const active = claims[0];
    if (!active) return { target: candidateTarget, status: "offline" as UnityInstanceStatus, claims };
    if (active.blockers.length > 0 || active.state === "blocked") return { target: candidateTarget, status: "blocked" as UnityInstanceStatus, claims };
    if (active.state === "transitioning") return { target: candidateTarget, status: "transitioning" as UnityInstanceStatus, claims };
    const heartbeatFresh = isFreshTimestamp(active.lastHeartbeatAt, now, HEARTBEAT_FRESH_MS);
    const health = heartbeatFresh ? await probeHealthIdentity(record.identity, active) : undefined;
    return { target: candidateTarget, status: heartbeatFresh && health ? "ready" as UnityInstanceStatus : "transitioning" as UnityInstanceStatus, claims };
  }));
  const priority: UnityInstanceStatus[] = ["conflict", "blocked", "ready", "transitioning", "offline"];
  const status = priority.find((candidate) => evaluations.some((evaluation) => evaluation.status === candidate)) ?? "offline";
  const claims = target === "all" ? allClaims : evaluations[0].claims;
  const selected = target !== "all" && claims.length === 1 ? claims[0] : allClaims.length === 1 ? allClaims[0] : undefined;
  const unverifiable = claims.some((session) => session.claimVerification === "unverifiable" || observations.get(session.processId)?.state === "unverifiable");
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
    updatedAt: record.updatedAt,
    sessions: claims,
    activeSession: selected,
    allSessions: allClaims,
    sessionId: selected?.sessionId,
    editorSessionId: selected?.editorSessionId,
    runtimeSessionId: selected?.runtimeSessionId,
    processId: selected?.processId,
    lastHeartbeatAt: selected?.lastHeartbeatAt,
    lastUnitySnapshot: selected?.lastUnitySnapshot,
    progress: selected?.progress ?? [],
    blockers: selected?.blockers ?? [],
    unityVersion: selected?.unityVersion,
    buildStamp: selected?.buildStamp,
    toolCatalogFingerprint: selected?.toolCatalogFingerprint,
    claimVerification: claims.length === 0 ? "none" : status === "conflict" || unverifiable ? "unverifiable" : "verified",
  };
}

/** public summary 不包含 endpoint、port 或 listener generation。 */
export function instanceListSummary(instance: UnityInstance): Record<string, unknown> {
  return {
    instanceId: instance.instanceId,
    canonicalizationVersion: instance.canonicalizationVersion,
    canonicalProjectPath: instance.canonicalProjectPath,
    displayProjectPath: instance.displayProjectPath,
    projectName: instance.projectName,
    status: instance.status,
    revision: instance.revision,
    updatedAt: instance.updatedAt,
    claimVerification: instance.claimVerification,
    ...(instance.processClaimIds ? { processClaimIds: instance.processClaimIds } : {}),
    sessions: instance.allSessions.map((session) => ({
      target: session.target,
      sessionId: session.sessionId,
      ...(session.editorSessionId ? { editorSessionId: session.editorSessionId } : {}),
      ...(session.runtimeSessionId ? { runtimeSessionId: session.runtimeSessionId } : {}),
      processId: session.processId,
      processStartedAt: session.processStartedAt,
      lastHeartbeatAt: session.lastHeartbeatAt,
      state: session.state,
      claimVerification: session.claimVerification,
    })),
    lastUnitySnapshot: instance.lastUnitySnapshot ? {
      schemaVersion: instance.lastUnitySnapshot.schemaVersion,
      observedAt: instance.lastUnitySnapshot.observedAt,
      fields: instance.lastUnitySnapshot.fields,
    } : null,
    progress: instance.progress,
    blockers: instance.blockers,
    unityVersion: instance.unityVersion ?? null,
    buildStamp: instance.buildStamp ?? null,
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

async function readRegistryRecords(dataDir: string): Promise<InstanceRegistryV2[]> {
  let names: string[];
  try {
    names = await readdir(instancesDir(dataDir));
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return [];
    throw error;
  }

  const records: InstanceRegistryV2[] = [];
  for (const name of names.filter((entry) => INSTANCE_ID_PATTERN.test(entry.replace(/\.json$/i, "")) && entry.endsWith(".json")).sort()) {
    const filePath = join(instancesDir(dataDir), name);
    const raw = await readRegistryText(filePath, true);
    const parsed = JSON.parse(stripBom(raw)) as InstanceRegistryV2;
    validateRegistryRecord(parsed);
    if (`${parsed.identity.instanceId}.json` !== name) {
      throw new InstanceRegistryError("E_INSTANCE_IDENTITY_MISMATCH", "Registry filename does not match its canonical instanceId.", [], { filePath, instanceId: parsed.identity.instanceId });
    }
    records.push(parsed);
  }
  return records;
}

/** Observation paths already know the deterministic v2 filename and must not enumerate unrelated records. */
async function readRegistryRecord(dataDir: string, instanceId: string, deadlineMs?: number): Promise<InstanceRegistryV2 | undefined> {
  const filePath = join(instancesDir(dataDir), `${instanceId}.json`);
  try {
    const parsed = JSON.parse(stripBom(await readRegistryText(filePath, true, deadlineMs))) as InstanceRegistryV2;
    validateRegistryRecord(parsed);
    if (parsed.identity.instanceId !== instanceId) {
      throw new InstanceRegistryError("E_INSTANCE_IDENTITY_MISMATCH", "Registry filename does not match its canonical instanceId.", [], { filePath, instanceId: parsed.identity.instanceId });
    }
    return parsed;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

function validateRegistryRecord(record: InstanceRegistryV2): void {
  const identity = record?.identity;
  const expectedInstanceId = identity && typeof identity.canonicalProjectPath === "string"
    ? `unity-${createHash("sha256").update(Buffer.from(identity.canonicalProjectPath, "utf8")).digest("hex")}`
    : "";
  if (!record || record.version !== 2 || record.canonicalizationVersion !== CANONICALIZATION_VERSION
    || !identity || !INSTANCE_ID_PATTERN.test(identity.instanceId) || identity.instanceId !== expectedInstanceId
    || typeof identity.canonicalProjectPath !== "string" || identity.canonicalProjectPath.length < 3
    || typeof identity.displayProjectPath !== "string" || identity.displayProjectPath.length < 3
    || !Array.isArray(record.sessions)
    || !Number.isInteger(record.revision) || record.revision < 0
    || !validTimestamp(record.updatedAt)) {
    throw new InstanceRegistryError("E_INSTANCE_IDENTITY_MISMATCH", "Instance registry does not satisfy the v2 canonical identity contract.");
  }

  const sessionIds = new Set<string>();
  const listenerIds = new Set<string>();
  for (const session of record.sessions) {
    const listener = session?.listener;
    const endpoint = listener?.endpoint;
    const snapshot = session?.lastUnitySnapshot as Record<string, unknown> | undefined;
    const expectedUrl = endpoint ? `http://127.0.0.1:${endpoint.actualPort}` : "";
    // 新domain尚未启动listener时，Bridge以严格的端口0占位发布transitioning身份；ready仍必须有真实端口。
    const endpointPortValid = endpoint != null && (validPort(endpoint.actualPort)
      || session?.state === "transitioning" && endpoint.actualPort === 0 && endpoint.fallback === true);
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
      || !validTimestamp(session.processStartedAt) || !validTimestamp(session.lastHeartbeatAt)
      || (session.claimVerification !== "verified" && session.claimVerification !== "unverifiable")
      || (session.state !== "ready" && session.state !== "transitioning" && session.state !== "blocked")
      || !listener || !LISTENER_ID_PATTERN.test(listener.listenerId) || listenerIds.has(listener.listenerId)
      || !Number.isInteger(listener.generation) || listener.generation < 1 || !validTimestamp(listener.startedAt)
      || !endpoint || endpoint.host !== "127.0.0.1"
      || !validPort(endpoint.preferredPort) || !endpointPortValid
      || typeof endpoint.fallback !== "boolean" || endpoint.url !== expectedUrl
      || !targetIdentityValid
      || !Array.isArray(session.progress) || !Array.isArray(session.blockers)
      || typeof session.buildStamp !== "string" || session.buildStamp.length === 0) {
      throw new InstanceRegistryError("E_INSTANCE_IDENTITY_MISMATCH", "Instance registry contains an invalid target session claim.", [], { instanceId: identity.instanceId });
    }
    sessionIds.add(session.sessionId);
    listenerIds.add(listener.listenerId);
  }
}

/** registry snapshot 是完整 v1 单包；reader 不允许从两次 heartbeat 拼接字段。 */
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
    "  catch { [pscustomobject]@{ processId=[int]$id; state='unverifiable' } }",
    "}",
    "@($items) | ConvertTo-Json -Compress -Depth 3",
  ].join("\n");
  const execution = runPowerShell(command, { UNITY_CLI_PROCESS_IDS: JSON.stringify(ids) }, Math.max(1, timeoutMs));
  if (execution.status !== 0 || execution.error) {
    return new Map(ids.map((processId) => [processId, { processId, state: "unverifiable" as const }]));
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
      } satisfies ProcessObservation];
    }));
  } catch {
    return new Map(ids.map((processId) => [processId, { processId, state: "unverifiable" as const }]));
  }
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
  const cacheKey = [identity.instanceId, session.target, session.sessionId, listener.listenerId, listener.endpoint.actualPort, session.buildStamp ?? "", session.buildId ?? ""].join("\0");
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
    const health: HealthIdentity = {
      version: Number(data.version) as 2,
      target: String(data.target ?? "") as InstanceTarget,
      instanceId: String(data.instanceId ?? ""),
      canonicalProjectPath: String(data.canonicalProjectPath ?? ""),
      canonicalizationVersion: Number(data.canonicalizationVersion) as 1,
      sessionId: String(data.sessionId ?? ""),
      listenerId: String(data.listenerId ?? ""),
      actualPort: Number(data.actualPort),
      buildStamp: String(data.buildStamp ?? ""),
      ...(typeof data.buildId === "string" ? { buildId: data.buildId } : {}),
    };
    if (health.version !== 2
      || health.target !== session.target
      || health.instanceId !== identity.instanceId
      || health.canonicalProjectPath !== identity.canonicalProjectPath
      || health.canonicalizationVersion !== 1
      || health.sessionId !== session.sessionId
      || health.listenerId !== listener.listenerId
      || health.actualPort !== listener.endpoint.actualPort
      || health.buildStamp !== session.buildStamp
      || (session.target === "runtime" && health.buildId !== session.buildId)) return undefined;
    healthCache.set(cacheKey, { checkedAt: Date.now(), identity: health });
    return health;
  } catch {
    return undefined;
  } finally {
    clearTimeout(timeout);
  }
}

async function resolveSelectorIdentity(selector: string, dataDir: string, observationBudgetMs?: number, observationDeadlineMs?: number): Promise<ProjectIdentity> {
  const store = await readAliasStore(dataDir);
  const alias = store.aliases.find((binding) => binding.alias === selector);
  if (alias) return projectIdentityFromBinding(alias);
  if (INSTANCE_ID_PATTERN.test(selector)) {
    const record = observationDeadlineMs === undefined
      ? (await readRegistryRecords(dataDir)).find((candidate) => candidate.identity.instanceId === selector)
      : await readRegistryRecord(dataDir, selector, observationDeadlineMs);
    if (!record) throw new InstanceRegistryError("E_INSTANCE_NOT_FOUND", `Unity project identity not found: ${selector}.`, ["Pass an existing project path or run unity_cli instances list."]);
    return projectIdentityFromRegistry(record.identity);
  }
  if (!looksLikePath(selector)) {
    throw new InstanceRegistryError("E_INSTANCE_NOT_FOUND", `Unity project alias not found: ${selector}.`, ["Run unity_cli target alias list or pass a project path."]);
  }
  return observationBudgetMs === undefined
    ? resolveProjectIdentity(selector)
    : resolveProjectIdentityAsync(selector, { timeoutMs: observationBudgetMs });
}

async function resolveAliasIdentity(selector: string, dataDir: string): Promise<ProjectIdentity> {
  if (!INSTANCE_ID_PATTERN.test(selector)) return resolveProjectIdentity(selector);
  const record = (await readRegistryRecords(dataDir)).find((candidate) => candidate.identity.instanceId === selector);
  if (!record) throw new InstanceRegistryError("E_INSTANCE_NOT_FOUND", `Unity project identity not found: ${selector}.`, ["Pass an existing Unity project path."]);
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

function isFreshTimestamp(value: string, now: number, maxAgeMs: number): boolean {
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) && now >= timestamp && now - timestamp <= maxAgeMs;
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
