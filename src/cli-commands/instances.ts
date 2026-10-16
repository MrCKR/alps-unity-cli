import { executeBridgePayload, SessionSupervisor, SessionSupervisorError } from "../bridge/index.js";
import { handleInstancesCommand, instanceSelectionFailure } from "../instances.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";

const DEFAULT_PRUNE_TIMEOUT_MS = 30_000;

/** 顶层instances控制面：list纯读取，prune只授权当前Unity writer执行同一清理函数。 */
export async function handleInstances(
  command: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: Runtime,
  requestId: string,
): Promise<CliResult> {
  const subCommand = command ?? "list";
  if (subCommand !== "prune") return handleInstancesCommand(subCommand, flags, runtime.dataDir, requestId);
  return runWriterOwnedPrune(flags, runtime, requestId);
}

async function runWriterOwnedPrune(flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const rawTimeout = flags["timeout-ms"];
  const timeoutMs = rawTimeout === undefined ? DEFAULT_PRUNE_TIMEOUT_MS : typeof rawTimeout === "string" ? Number(rawTimeout) : Number.NaN;
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1) {
    return failure(requestId, "E_SCHEMA_INVALID", "instances prune --timeout-ms must be a positive integer.", ["Pass --timeout-ms <positive-integer>."]);
  }

  const startedAtMs = Date.now();
  const supervisor = new SessionSupervisor(flags, runtime);
  try {
    const admission = await supervisor.acquireRootAdmission({
      requestId,
      command: "instances.prune",
      input: {},
      dryRun: false,
      target: "editor",
      completionContractIdentity: "instances-prune-v1",
      timeoutMs,
      admissionAbsoluteDeadlineMs: startedAtMs + timeoutMs,
    });
    const result = await executeBridgePayload(requestId, "internal.instances.prune", {}, { ...flags, instance: admission.instanceId }, { ...runtime, rootAdmission: admission }, {
      mode: "execute",
      mutation: true,
      absoluteDeadlineMs: admission.executionAbsoluteDeadlineMs,
    });
    await supervisor.completeRootAdmission(admission);
    if (!result.ok) return result;
    const normalized = pruneResult(result.data);
    if (!normalized) {
      return failure(requestId, "E_BRIDGE_RESPONSE_INVALID", "Unity returned an invalid instances prune result.", [
        "Update the selected UnityCLI Package so it matches the current CLI, then retry instances prune.",
      ]);
    }
    return success(requestId, normalized);
  } catch (error) {
    const selection = instanceSelectionFailure(requestId, error);
    if (selection) return selection;
    if (error instanceof SessionSupervisorError) {
      return failure(requestId, error.code, error.message, error.nextActions, error.details, {
        transient: error.transient,
        retryAfterMs: error.retryAfterMs,
      });
    }
    return failure(requestId, "BRIDGE_CONNECTION_FAILED", error instanceof Error ? error.message : String(error), [
      "Open the target Unity project, then retry unitycli instances prune from that project or pass --instance <name|alias|projectPath>.",
    ], undefined, { transient: true, retryAfterMs: 100 });
  }
}

interface InstancesPruneResult {
  removed: number;
  remaining: number;
  deferred: number;
  scanned: number;
  truncated: boolean;
  retention: {
    offlineMax: 100;
    damagedMax: 20;
    maxAgeDays: 30;
    scanLimit: 2000;
    deleteLimit: 20;
  };
}

function pruneResult(value: unknown): InstancesPruneResult | undefined {
  if (!exactObject(value, ["removed", "remaining", "deferred", "scanned", "truncated", "retention"])) return undefined;
  const root = value as Record<string, unknown>;
  if (![root.removed, root.remaining, root.deferred, root.scanned].every(nonnegativeInteger) || typeof root.truncated !== "boolean") return undefined;
  if (!exactObject(root.retention, ["offlineMax", "damagedMax", "maxAgeDays", "scanLimit", "deleteLimit"])) return undefined;
  const retention = root.retention as Record<string, unknown>;
  if (retention.offlineMax !== 100 || retention.damagedMax !== 20 || retention.maxAgeDays !== 30
    || retention.scanLimit !== 2000 || retention.deleteLimit !== 20) return undefined;
  return {
    removed: root.removed as number,
    remaining: root.remaining as number,
    deferred: root.deferred as number,
    scanned: root.scanned as number,
    truncated: root.truncated,
    retention: { offlineMax: 100, damagedMax: 20, maxAgeDays: 30, scanLimit: 2000, deleteLimit: 20 },
  };
}

function exactObject(value: unknown, keys: string[]): value is Record<string, unknown> {
  return !!value && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).length === keys.length && keys.every((key) => Object.prototype.hasOwnProperty.call(value, key));
}

function nonnegativeInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}
