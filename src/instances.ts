import { mkdir, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, join, resolve, sep } from "node:path";
import { defaultDataDir } from "./platform.js";
import type { CliResult } from "./types.js";
import { failure, success } from "./output.js";

export const STALE_AFTER_MS = 30_000;

export interface UnityInstance {
  instanceId: string;
  alias?: string;
  projectName?: string;
  projectPath: string;
  projectHash?: string;
  unityVersion?: string;
  packageVersion?: string;
  processId?: number;
  pid?: number;
  host?: string;
  port?: number;
  bridgeEndpoint: string;
  capabilitiesHash?: string;
  status: "ready" | "healthy" | "stale" | "unreachable";
  editorState?: Record<string, unknown> | string;
  playModeState?: Record<string, unknown> | string;
  registeredAt?: string;
  lastHeartbeatAt?: string;
  lastSeenAt?: string;
}

interface RegistryFile {
  version?: number;
  instances?: UnityInstance[];
}

interface TargetConfig {
  activeTarget?: string;
}

export interface InstanceSelection {
  instance: UnityInstance;
  matchedBy: "target" | "alias" | "instanceId" | "projectHash" | "projectPathPrefix" | "activeTarget" | "singleReady" | "cwd" | "singleHealthy";
}

class InstanceRegistryError extends Error {
  public constructor(public readonly code: string, message: string, public readonly nextActions: string[] = [], public readonly details?: Record<string, unknown>) {
    super(message);
  }
}

export async function handleInstancesCommand(
  command: string | undefined,
  flags: Record<string, string | boolean>,
  dataDir: string,
  requestId: string
): Promise<CliResult> {
  if (command === "list" || !command) {
    const instances = await readInstances(dataDir);
    return success(requestId, { registryPath: instancesDir(dataDir), instances: instances.map(instanceListSummary) });
  }

  if (command === "prune" || command === "cleanup") {
    const before = await readInstances(dataDir);
    const stale = before.filter((instance) => instance.status === "stale" || instance.status === "unreachable");
    const staleIds = new Set(stale.map((instance) => instance.instanceId));
    const legacyRemaining = before.filter((instance) => !staleIds.has(instance.instanceId));

    const killResults = stale.map((instance) => {
      const killed = attemptKillPid(instance.processId ?? instance.pid);
      return { instanceId: instance.instanceId, pid: instance.processId ?? instance.pid ?? null, killed };
    });

    await Promise.all(stale.map((instance) => deleteInstanceFile(dataDir, instance.instanceId)));
    await writeLegacyInstances(dataDir, legacyRemaining);
    return success(requestId, {
      registryPath: instancesDir(dataDir),
      removed: stale.length,
      remaining: legacyRemaining.length,
      instances: legacyRemaining.map(instanceListSummary),
      staleCleaned: killResults
    });
  }

  return failure(requestId, "UNKNOWN_INSTANCES_COMMAND", "Expected instances list or instances prune.", [
    "Run unity_cli instances list to inspect registered targets.",
    "Run unity_cli instances prune to remove stale or unreachable targets."
  ]);
}

export async function handleTargetCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  dataDir: string,
  requestId: string
): Promise<CliResult> {
  if (!command || command === "get") {
    const config = await readTargetConfig(dataDir);
    const aliases = await readAliases(dataDir);
    const instances = await readInstances(dataDir);
    const active = config.activeTarget ? resolveInstance(instances, aliases, config.activeTarget) : undefined;
    return success(requestId, {
      command: "target.get",
      activeTarget: config.activeTarget ?? null,
      target: active ? targetSummary(active.instance, aliases) : null,
      aliases
    });
  }

  if (command === "set") {
    const selector = positional[0] ?? stringFlag(flags, "target") ?? stringFlag(flags, "id") ?? stringFlag(flags, "alias");
    if (!selector) {
      return failure(requestId, "E_TARGET_NOT_FOUND", "target set requires <id|alias>.", ["Run unity_cli instances list or unity_cli target alias list."]);
    }
    try {
      const instances = await readInstances(dataDir);
      const aliases = await readAliases(dataDir);
      const selection = resolveRequiredTarget(instances, aliases, selector, "target");
      await writeTargetConfig(dataDir, { activeTarget: selection.instance.instanceId });
      return success(requestId, { command: "target.set", target: targetSummary(selection.instance, aliases), matchedBy: selection.matchedBy });
    } catch (error) {
      const result = instanceSelectionFailure(requestId, error);
      if (result) return result;
      throw error;
    }
  }

  if (command === "clear") {
    await writeTargetConfig(dataDir, {});
    return success(requestId, { command: "target.clear", activeTarget: null });
  }

  if (command === "alias") {
    return handleTargetAliasCommand(positional[0], positional.slice(1), flags, dataDir, requestId);
  }

  return failure(requestId, "UNKNOWN_TARGET_COMMAND", "Expected target get, target set <id|alias>, target clear, or target alias set|unset|list.");
}

async function handleTargetAliasCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  dataDir: string,
  requestId: string
): Promise<CliResult> {
  const aliases = await readAliases(dataDir);
  if (!command || command === "list") {
    const instances = await readInstances(dataDir);
    const items = Object.entries(aliases).map(([alias, instanceId]) => ({ alias, instanceId, target: instances.find((instance) => instance.instanceId === instanceId) ? targetSummary(instances.find((instance) => instance.instanceId === instanceId)!, aliases) : null }));
    return success(requestId, { command: "target.alias.list", aliases: items });
  }

  if (command === "set") {
    const instanceId = positional[0] ?? stringFlag(flags, "instance") ?? stringFlag(flags, "instance-id") ?? stringFlag(flags, "id");
    const alias = positional[1] ?? stringFlag(flags, "alias");
    if (!instanceId || !alias) {
      return failure(requestId, "E_TARGET_NOT_FOUND", "target alias set requires <instanceId> <alias>.", ["Run unity_cli instances list to inspect instance ids."]);
    }
    const instances = await readInstances(dataDir);
    const instance = instances.find((candidate) => candidate.instanceId === instanceId);
    if (!instance) {
      return failure(requestId, "E_TARGET_NOT_FOUND", `Unity target not found: ${instanceId}.`, ["Run unity_cli instances list to inspect registered targets."]);
    }
    aliases[alias] = instance.instanceId;
    await writeAliases(dataDir, aliases);
    return success(requestId, { command: "target.alias.set", alias, target: targetSummary(instance, aliases) });
  }

  if (command === "unset") {
    const alias = positional[0] ?? stringFlag(flags, "alias");
    if (!alias || aliases[alias] === undefined) {
      return failure(requestId, "E_TARGET_NOT_FOUND", `Target alias not found: ${alias ?? ""}.`, ["Run unity_cli target alias list."]);
    }
    delete aliases[alias];
    await writeAliases(dataDir, aliases);
    return success(requestId, { command: "target.alias.unset", alias });
  }

  return failure(requestId, "UNKNOWN_TARGET_ALIAS_COMMAND", "Expected target alias set <instanceId> <alias>, target alias unset <alias>, or target alias list.");
}

export async function selectInstanceForFlags(
  flags: Record<string, string | boolean>,
  dataDir: string,
  cwd = process.cwd()
): Promise<InstanceSelection | undefined> {
  const explicit = stringFlag(flags, "target") ?? stringFlag(flags, "instance");
  if (!explicit && stringFlag(flags, "bridge-endpoint")) {
    return undefined;
  }

  const instances = await readInstances(dataDir);
  const aliases = await readAliases(dataDir);
  if (explicit) {
    return resolveRequiredTarget(instances, aliases, explicit, "target");
  }

  const config = await readTargetConfig(dataDir);
  if (config.activeTarget) {
    return resolveRequiredTarget(instances, aliases, config.activeTarget, "activeTarget");
  }

  const ready = readyInstances(instances);
  const cwdMatch = ready.find((instance) => isPathWithin(cwd, instance.projectPath));
  if (cwdMatch) {
    return { instance: cwdMatch, matchedBy: "cwd" };
  }
  if (ready.length === 1) {
    return { instance: ready[0], matchedBy: "singleReady" };
  }
  if (ready.length > 1) {
    throw new InstanceRegistryError("E_TARGET_AMBIGUOUS", "Multiple ready Unity targets are registered; pass --target <id|alias> or run unity_cli target set <id|alias>.", [
      "Run unity_cli instances list to inspect registered targets.",
      "Run unity_cli target alias set <instanceId> <alias> for a stable alias.",
      "Run unity_cli target set <id|alias> to set an active target."
    ], { targets: ready.map((instance) => targetSummary(instance, aliases)) });
  }
  if (instances.some((instance) => instance.status === "stale")) {
    throw new InstanceRegistryError("E_TARGET_STALE", "Only stale Unity targets are registered.", [
      "Reopen Unity Editor for the target project.",
      "Run unity_cli instances prune to remove stale entries."
    ]);
  }
  if (instances.some((instance) => instance.status === "unreachable")) {
    throw new InstanceRegistryError("E_TARGET_UNREACHABLE", "Only unreachable Unity targets are registered.", [
      "Check whether the Unity Editor bridge process is still running.",
      "Run unity_cli instances prune to remove unreachable entries."
    ]);
  }
  return undefined;
}

export function instanceSelectionFailure(requestId: string, error: unknown): CliResult | undefined {
  if (error instanceof InstanceRegistryError) {
    return failure(requestId, error.code, error.message, error.nextActions, error.details);
  }
  return undefined;
}

export function registryPath(dataDir = defaultDataDir()): string {
  return resolve(dataDir, "instances.json");
}

export function instancesDir(dataDir = defaultDataDir()): string {
  return resolve(dataDir, "instances");
}

export async function readInstances(dataDir = defaultDataDir(), now = Date.now()): Promise<UnityInstance[]> {
  const [fileInstances, legacyInstances] = await Promise.all([readInstanceFiles(dataDir), readLegacyInstances(dataDir)]);
  return normalizeInstances([...legacyInstances, ...fileInstances], now);
}

export async function writeInstances(dataDir: string, instances: UnityInstance[]): Promise<void> {
  await writeLegacyInstances(dataDir, instances);
}

export function normalizeInstances(instances: UnityInstance[], now = Date.now()): UnityInstance[] {
  const normalized = instances
    .filter((instance) => typeof instance.instanceId === "string" && (typeof instance.bridgeEndpoint === "string" || (typeof instance.host === "string" && typeof instance.port === "number")))
    .map((instance) => {
      const endpoint = instance.bridgeEndpoint ?? `http://${instance.host}:${instance.port}`;
      const processId = instance.processId ?? instance.pid;
      const stale = isHeartbeatStale(instance.lastSeenAt ?? instance.lastHeartbeatAt, now);
      const status = stale ? "stale" as const : instance.status === "stale" ? "stale" as const : instance.status === "unreachable" ? "unreachable" as const : "ready" as const;
      return {
        ...instance,
        processId,
        pid: processId,
        host: instance.host ?? endpointHost(endpoint),
        port: instance.port ?? endpointPort(endpoint),
        bridgeEndpoint: endpoint,
        projectHash: instance.projectHash ?? projectHash(instance.projectPath),
        status,
        lastSeenAt: instance.lastSeenAt ?? instance.lastHeartbeatAt,
        lastHeartbeatAt: instance.lastHeartbeatAt ?? instance.lastSeenAt
      };
    });

  const byInstanceId = new Map<string, UnityInstance>();
  for (const instance of normalized) {
    const current = byInstanceId.get(instance.instanceId);
    if (!current || isNewerInstance(instance, current)) {
      byInstanceId.set(instance.instanceId, instance);
    }
  }
  return [...byInstanceId.values()];
}

export function targetSummary(instance: UnityInstance, aliases: Record<string, string> = {}): Record<string, unknown> {
  const alias = Object.entries(aliases).find(([, instanceId]) => instanceId === instance.instanceId)?.[0];
  return {
    id: instance.instanceId,
    alias: alias ?? instance.alias ?? null,
    projectName: instance.projectName ?? projectNameFromPath(instance.projectPath),
    projectPath: instance.projectPath,
    processId: instance.processId ?? instance.pid ?? null,
    state: instance.status === "healthy" ? "ready" : instance.status,
    endpoint: instance.bridgeEndpoint
  };
}

async function readInstanceFiles(dataDir: string): Promise<UnityInstance[]> {
  try {
    const dir = instancesDir(dataDir);
    const names = await readdir(dir);
    const files = names.filter((name) => name.endsWith(".json"));
    const values = await Promise.all(files.map(async (name) => {
      try {
        const parsed = JSON.parse(await readFile(join(dir, name), "utf8")) as UnityInstance;
        return parsed;
      } catch {
        return undefined;
      }
    }));
    return values.filter((value): value is UnityInstance => value !== undefined);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return [];
    throw error;
  }
}

async function readLegacyInstances(dataDir: string): Promise<UnityInstance[]> {
  try {
    const raw = await readFile(registryPath(dataDir), "utf8");
    const parsed = JSON.parse(raw) as RegistryFile;
    return Array.isArray(parsed.instances) ? parsed.instances : [];
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return [];
    throw error;
  }
}

async function writeLegacyInstances(dataDir: string, instances: UnityInstance[]): Promise<void> {
  const filePath = registryPath(dataDir);
  await mkdir(dirname(filePath), { recursive: true });
  await writeFile(filePath, JSON.stringify({ version: 1, instances }) + "\n", "utf8");
}

async function deleteInstanceFile(dataDir: string, instanceId: string): Promise<void> {
  await rm(resolve(instancesDir(dataDir), `${safeFileName(instanceId)}.json`), { force: true });
}

async function readTargetConfig(dataDir: string): Promise<TargetConfig> {
  try {
    const raw = await readFile(resolve(dataDir, "config.json"), "utf8");
    const parsed = JSON.parse(raw) as TargetConfig;
    return typeof parsed.activeTarget === "string" ? { activeTarget: parsed.activeTarget } : {};
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return {};
    throw error;
  }
}

async function writeTargetConfig(dataDir: string, config: TargetConfig): Promise<void> {
  const filePath = resolve(dataDir, "config.json");
  await mkdir(dirname(filePath), { recursive: true });
  await writeFile(filePath, JSON.stringify(config, null, 2) + "\n", "utf8");
}

async function readAliases(dataDir: string): Promise<Record<string, string>> {
  try {
    const raw = await readFile(resolve(dataDir, "aliases.json"), "utf8");
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    return Object.fromEntries(Object.entries(parsed).filter((entry): entry is [string, string] => typeof entry[1] === "string"));
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return {};
    throw error;
  }
}

async function writeAliases(dataDir: string, aliases: Record<string, string>): Promise<void> {
  const filePath = resolve(dataDir, "aliases.json");
  await mkdir(dirname(filePath), { recursive: true });
  await writeFile(filePath, JSON.stringify(aliases, null, 2) + "\n", "utf8");
}

function resolveRequiredTarget(instances: UnityInstance[], aliases: Record<string, string>, selector: string, matchedBy: "target" | "activeTarget"): InstanceSelection {
  const selection = resolveInstance(instances, aliases, selector, matchedBy);
  if (!selection) {
    throw new InstanceRegistryError("E_TARGET_NOT_FOUND", `Unity target not found: ${selector}.`, ["Run unity_cli instances list to inspect registered targets."]);
  }
  if (selection.instance.status === "stale") {
    throw new InstanceRegistryError("E_TARGET_STALE", `Unity target is stale: ${selector}.`, [
      "Reopen Unity Editor for the target project.",
      "Run unity_cli instances prune to remove stale entries."
    ], { target: targetSummary(selection.instance, aliases) });
  }
  if (selection.instance.status === "unreachable") {
    throw new InstanceRegistryError("E_TARGET_UNREACHABLE", `Unity target is unreachable: ${selector}.`, [
      "Check the Unity Editor bridge endpoint and process.",
      "Run unity_cli instances prune to remove unreachable entries."
    ], { target: targetSummary(selection.instance, aliases) });
  }
  return selection;
}

function resolveInstance(instances: UnityInstance[], aliases: Record<string, string>, selector: string, matchedBy: "target" | "activeTarget" = "target"): InstanceSelection | undefined {
  const aliasInstanceId = aliases[selector];
  const effectiveSelector = aliasInstanceId ?? selector;
  const matches = instances.filter((instance) =>
    instance.instanceId === effectiveSelector ||
    instance.instanceId.startsWith(effectiveSelector) ||
    instance.projectHash === effectiveSelector ||
    (instance.projectHash?.startsWith(effectiveSelector) ?? false) ||
    normalizedPath(instance.projectPath).startsWith(normalizedPath(effectiveSelector))
  );

  if (matches.length === 0) return undefined;
  if (matches.length > 1) {
    throw new InstanceRegistryError("E_TARGET_AMBIGUOUS", `Unity target selector is ambiguous: ${selector}.`, [
      "Run unity_cli instances list to inspect target ids.",
      "Pass a full --target <instanceId> or set an alias."
    ], { targets: matches.map((instance) => targetSummary(instance, aliases)) });
  }
  if (aliasInstanceId) return { instance: matches[0], matchedBy: "alias" };
  if (matches[0].instanceId === selector || matches[0].instanceId.startsWith(selector)) return { instance: matches[0], matchedBy: matchedBy === "activeTarget" ? "activeTarget" : "instanceId" };
  if (matches[0].projectHash === selector || matches[0].projectHash?.startsWith(selector)) return { instance: matches[0], matchedBy: "projectHash" };
  return { instance: matches[0], matchedBy: "projectPathPrefix" };
}

function readyInstances(instances: UnityInstance[]): UnityInstance[] {
  return instances.filter((instance) => instance.status === "ready" || instance.status === "healthy");
}

function instanceListSummary(instance: UnityInstance): Record<string, unknown> {
  return {
    id: instance.instanceId,
    alias: instance.alias ?? null,
    project: instance.projectName ?? projectNameFromPath(instance.projectPath),
    projectPath: instance.projectPath,
    pid: instance.processId ?? instance.pid ?? null,
    instanceId: instance.instanceId,
    state: instance.status === "healthy" ? "ready" : instance.status,
    status: instance.status === "ready" ? "healthy" : instance.status,
    endpoint: instance.bridgeEndpoint,
    bridgeEndpoint: instance.bridgeEndpoint,
    unityVersion: instance.unityVersion ?? null,
    packageVersion: instance.packageVersion ?? null,
    lastSeenAt: instance.lastSeenAt ?? instance.lastHeartbeatAt ?? null
  };
}

function isNewerInstance(candidate: UnityInstance, current: UnityInstance): boolean {
  const candidateTime = instanceTimestamp(candidate);
  const currentTime = instanceTimestamp(current);
  if (candidateTime !== currentTime) {
    return candidateTime > currentTime;
  }
  return (current.status === "stale" || current.status === "unreachable") && (candidate.status === "ready" || candidate.status === "healthy");
}

function instanceTimestamp(instance: UnityInstance): number {
  const heartbeat = Date.parse(instance.lastSeenAt ?? instance.lastHeartbeatAt ?? "");
  if (Number.isFinite(heartbeat)) {
    return heartbeat;
  }
  const registered = Date.parse(instance.registeredAt ?? "");
  return Number.isFinite(registered) ? registered : 0;
}

function isHeartbeatStale(value: string | undefined, now: number): boolean {
  if (!value) {
    return true;
  }
  const timestamp = Date.parse(value);
  return !Number.isFinite(timestamp) || now - timestamp > STALE_AFTER_MS;
}

function isPathWithin(child: string, parent: string): boolean {
  const normalizedChild = normalizedPath(child);
  const normalizedParent = normalizedPath(parent);
  return normalizedChild === normalizedParent || normalizedChild.startsWith(normalizedParent.endsWith(sep) ? normalizedParent : normalizedParent + sep);
}

function normalizedPath(path: string): string {
  return resolve(path).toLowerCase();
}

function stringFlag(flags: Record<string, string | boolean>, key: string): string | undefined {
  return typeof flags[key] === "string" ? flags[key] : undefined;
}

function attemptKillPid(pid: number | undefined): boolean {
  if (!pid) {
    return false;
  }
  try {
    process.kill(pid);
    return true;
  } catch {
    return false;
  }
}

function projectNameFromPath(path: string): string {
  const parts = resolve(path).split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] ?? path;
}

function endpointHost(endpoint: string): string | undefined {
  try {
    return new URL(endpoint).hostname;
  } catch {
    return undefined;
  }
}

function endpointPort(endpoint: string): number | undefined {
  try {
    const port = Number(new URL(endpoint).port);
    return Number.isFinite(port) ? port : undefined;
  } catch {
    return undefined;
  }
}

function safeFileName(value: string): string {
  return value.replace(/[^A-Za-z0-9._-]/g, "_");
}

function projectHash(projectPath: string): string {
  let hash = 0;
  for (const char of projectPath.toLowerCase()) {
    hash = ((hash << 5) - hash + char.charCodeAt(0)) | 0;
  }
  return Math.abs(hash).toString(16).padStart(8, "0");
}
