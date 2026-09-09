import { createHash } from "node:crypto";
import type { InstanceTarget } from "../instances.js";

export interface DiscoveryProvenance {
  readonly target: InstanceTarget;
  readonly instanceId: string;
  readonly sessionId: string;
  readonly editorSessionId?: string;
  readonly listenerId: string;
  readonly listenerGeneration: number;
}

export interface RootAdmissionLease {
  readonly requestId: string;
  readonly command: string;
  readonly rootFingerprint: string;
  readonly completionContractIdentity: string;
  readonly target: InstanceTarget;
  readonly instanceId: string;
  readonly canonicalProjectPath: string;
  readonly sessionId: string;
  readonly processId: number;
  readonly processStartedAt: string;
  readonly toolCatalogFingerprint?: string;
  readonly listenerId: string;
  readonly listenerGeneration: number;
  readonly discoveryProvenance?: DiscoveryProvenance;
  token: string;
  consumed: boolean;
  grantExpiresAtMs: number;
  executionAbsoluteDeadlineMs: number;
  readonly injected: boolean;
}

export interface RootAdmissionRequest {
  requestId: string;
  command: string;
  input: Record<string, unknown>;
  dryRun: boolean;
  target: InstanceTarget;
  completionContractIdentity: string;
  captureDiscoveryProvenance?: boolean;
  expectedDiscoveryProvenance?: DiscoveryProvenance;
  timeoutMs: number;
  admissionAbsoluteDeadlineMs: number;
}

/** 准入指纹绑定业务identity但排除尚未创建的执行deadline。 */
export function rootAdmissionFingerprint(request: Pick<RootAdmissionRequest, "requestId" | "command" | "input" | "dryRun" | "target" | "completionContractIdentity" | "captureDiscoveryProvenance" | "expectedDiscoveryProvenance">, instanceId: string): string {
  return createHash("sha256").update(canonicalJson({
    requestId: request.requestId,
    command: request.command,
    input: request.input,
    dryRun: request.dryRun,
    instanceId,
    target: request.target,
    completionContractIdentity: request.completionContractIdentity,
    captureDiscoveryProvenance: request.captureDiscoveryProvenance === true,
    expectedDiscoveryProvenance: request.expectedDiscoveryProvenance ?? null,
    completionContractVersion: 1,
  }), "utf8").digest("hex");
}

/** 对JSON值递归排序object key，跨CLI进程生成同一稳定指纹。 */
function canonicalJson(value: unknown): string {
  return JSON.stringify(canonicalValue(value));
}

function canonicalValue(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonicalValue);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value as Record<string, unknown>)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([key, item]) => [key, canonicalValue(item)]));
  }
  return value;
}
