import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve, sep } from "node:path";
import { defaultDataDir } from "./platform.js";
import type { CliResult } from "./types.js";
import { failure, success } from "./output.js";

export const STALE_AFTER_MS = 30_000;

export interface UnityInstance {
  instanceId: string;
  projectPath: string;
  projectHash: string;
  unityVersion?: string;
  packageVersion?: string;
  pid?: number;
  bridgeEndpoint: string;
  capabilitiesHash?: string;
  status: "healthy" | "stale";
  editorState?: Record<string, unknown>;
  registeredAt: string;
  lastHeartbeatAt: string;
}

interface RegistryFile {
  version?: number;
  instances?: UnityInstance[];
}

export interface InstanceSelection {
  instance: UnityInstance;
  matchedBy: "instanceId" | "projectHash" | "projectPathPrefix" | "cwd" | "singleHealthy";
}

class InstanceRegistryError extends Error {
  public constructor(public readonly code: string, message: string, public readonly nextActions: string[] = []) {
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
    return success(requestId, { registryPath: registryPath(dataDir), instances });
  }

  if (command === "cleanup") {
    const before = await readInstances(dataDir);
    const stale = before.filter((instance) => instance.status === "stale");
    const remaining = before.filter((instance) => instance.status !== "stale");

    // Attempt to kill stale bridge processes to prevent zombie accumulation
    const killResults = stale.map((instance) => {
      const killed = attemptKillPid(instance.pid);
      return { instanceId: instance.instanceId, pid: instance.pid ?? null, killed };
    });

    await writeInstances(dataDir, remaining);
    return success(requestId, {
      registryPath: registryPath(dataDir),
      removed: before.length - remaining.length,
      remaining: remaining.length,
      instances: remaining,
      staleCleaned: killResults
    });
  }

  return failure(requestId, "UNKNOWN_INSTANCES_COMMAND", "Expected instances list or instances cleanup.");
}

export async function selectInstanceForFlags(
  flags: Record<string, string | boolean>,
  dataDir: string,
  cwd = process.cwd()
): Promise<InstanceSelection | undefined> {
  const explicit = stringFlag(flags, "instance");
  if (!explicit && stringFlag(flags, "bridge-endpoint")) {
    return undefined;
  }

  const instances = await readInstances(dataDir);
  if (explicit) {
    return selectExplicitInstance(instances, explicit);
  }

  const healthy = instances.filter((instance) => instance.status === "healthy");
  const cwdMatch = healthy.find((instance) => isPathWithin(cwd, instance.projectPath));
  if (cwdMatch) {
    return { instance: cwdMatch, matchedBy: "cwd" };
  }
  if (healthy.length === 1) {
    return { instance: healthy[0], matchedBy: "singleHealthy" };
  }
  if (healthy.length > 1) {
    throw new InstanceRegistryError("E_INSTANCE_AMBIGUOUS", "Multiple healthy Unity instances are registered; pass --instance <id|projectHash|projectPathPrefix>.", [
      "Run unity_cli instances list to inspect registered instances.",
      "Pass --instance <instanceId> to target one Editor explicitly."
    ]);
  }
  if (instances.some((instance) => instance.status === "stale")) {
    throw new InstanceRegistryError("E_INSTANCE_STALE", "Only stale Unity instances are registered.", [
      "Reopen Unity Editor for the target project.",
      "Run unity_cli instances cleanup to remove stale entries."
    ]);
  }
  return undefined;
}

export function instanceSelectionFailure(requestId: string, error: unknown): CliResult | undefined {
  if (error instanceof InstanceRegistryError) {
    return failure(requestId, error.code, error.message, error.nextActions);
  }
  return undefined;
}

export function registryPath(dataDir = defaultDataDir()): string {
  return resolve(dataDir, "instances.json");
}

export async function readInstances(dataDir = defaultDataDir(), now = Date.now()): Promise<UnityInstance[]> {
  try {
    const raw = await readFile(registryPath(dataDir), "utf8");
    const parsed = JSON.parse(raw) as RegistryFile;
    return normalizeInstances(Array.isArray(parsed.instances) ? parsed.instances : [], now);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === "ENOENT") {
      return [];
    }
    throw error;
  }
}

export async function writeInstances(dataDir: string, instances: UnityInstance[]): Promise<void> {
  const filePath = registryPath(dataDir);
  await mkdir(dirname(filePath), { recursive: true });
  await writeFile(filePath, JSON.stringify({ version: 1, instances }) + "\n", "utf8");
}

export function normalizeInstances(instances: UnityInstance[], now = Date.now()): UnityInstance[] {
  return instances
    .filter((instance) => typeof instance.instanceId === "string" && typeof instance.bridgeEndpoint === "string")
    .map((instance) => ({
      ...instance,
      status: isHeartbeatStale(instance.lastHeartbeatAt, now) ? "stale" : instance.status === "stale" ? "stale" : "healthy"
    }));
}

function selectExplicitInstance(instances: UnityInstance[], selector: string): InstanceSelection {
  const matches = instances.filter((instance) =>
    instance.instanceId === selector ||
    instance.projectHash === selector ||
    instance.projectHash.startsWith(selector) ||
    normalizedPath(instance.projectPath).startsWith(normalizedPath(selector))
  );

  if (matches.length === 0) {
    throw new InstanceRegistryError("E_INSTANCE_NOT_FOUND", `Unity instance not found: ${selector}.`, ["Run unity_cli instances list to inspect registered instances."]);
  }
  if (matches.length > 1) {
    throw new InstanceRegistryError("E_INSTANCE_AMBIGUOUS", `Unity instance selector is ambiguous: ${selector}.`, [
      "Run unity_cli instances list to inspect instanceId values.",
      "Pass a full --instance <instanceId>."
    ]);
  }
  const instance = matches[0];
  if (instance.status === "stale") {
    throw new InstanceRegistryError("E_INSTANCE_STALE", `Unity instance is stale: ${selector}.`, [
      "Reopen Unity Editor for the target project.",
      "Run unity_cli instances cleanup to remove stale entries."
    ]);
  }
  if (instance.instanceId === selector) return { instance, matchedBy: "instanceId" };
  if (instance.projectHash === selector || instance.projectHash.startsWith(selector)) return { instance, matchedBy: "projectHash" };
  return { instance, matchedBy: "projectPathPrefix" };
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
    // Process may already be gone; that's fine.
    return false;
  }
}
