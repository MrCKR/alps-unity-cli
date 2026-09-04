import type { AnyJson } from "../../schema/any-json.js";
import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding, type BridgeFlags } from "../../bridge/types.js";
import type { ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface DurableStatusRequest { requestId: string; }
export interface DurableStatusResult {
  requestId: string;
  state: "running" | "completed";
  phase?: string;
  terminalEnvelope?: AnyJson;
}

/** status调用不做editor.state前置探测，确保compile、reload恢复窗口仍可直接读取durable owner。 */
export async function readDurableStatus(command: string, request: DurableStatusRequest, context: ToolContext): Promise<DurableStatusResult> {
  const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000);
  const result = await executeBridgePayload(
    context.requestId,
    command,
    { requestId: request.requestId },
    { ...(context.flags ?? {}) } as BridgeFlags,
    { dataDir: context.dataDir, rootAdmission: context.rootAdmission, bridgeExecute: context.bridgeExecute },
    withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline }),
  );
  if (!result.ok) throw StructuredToolError.from(result.error ?? { code: "BRIDGE_CONNECTION_FAILED", message: "Durable status bridge read failed." });
  const data = record(result.data);
  if (data.requestId !== request.requestId || (data.state !== "running" && data.state !== "completed")) throw invalid();
  if (data.state === "running") {
    if (typeof data.phase !== "string" || data.terminalEnvelope !== undefined) throw invalid();
    return { requestId: request.requestId, state: "running", phase: data.phase };
  }
  if (data.phase !== undefined || data.terminalEnvelope === undefined) throw invalid();
  return { requestId: request.requestId, state: "completed", terminalEnvelope: data.terminalEnvelope as AnyJson };
}

function record(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

function invalid(): StructuredToolError {
  return new StructuredToolError("E_BRIDGE_PROTOCOL", "Durable status returned an invalid state envelope.", ["Inspect UnityCliBridge.log before retrying the same status read."]);
}
