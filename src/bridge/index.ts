import type { CliResult } from "../types.js";
import { failure, success } from "../output.js";
import { instanceSelectionFailure } from "../instances.js";
import { BridgeArgumentError } from "./args.js";
import { bridgeCommandRegistry } from "./registry.js";
import { getBoolean } from "../args.js";
import { observationSelectionFrom, transitionBindingAllowed, transitionResponseAllowed, type BridgeCommandSpec, type BridgeError, type BridgeFlags, type BridgeInvocationOptions, type BridgeRequest, type BridgeResponse, type BridgeRuntime } from "./types.js";
import { bridgeHttpTimeoutMs, SessionSupervisor, SessionSupervisorError } from "./session-supervisor.js";

export { BridgeCommandRegistry, bridgeCommandRegistry } from "./registry.js";
export { rootAdmissionFingerprint } from "./admission.js";
export type { DiscoveryProvenance, RootAdmissionLease, RootAdmissionRequest } from "./admission.js";
export { bridgeHttpTimeoutMs, SessionSupervisor, SessionSupervisorError } from "./session-supervisor.js";
export type { BridgeCommandSpec, BridgeInvocationOptions, BridgeRequest, BridgeResponse, BridgeRuntime } from "./types.js";

export async function executeBridgeCommand(
  requestId: string,
  command: string,
  flags: BridgeFlags,
  positionalTarget: string | undefined,
  runtime: BridgeRuntime,
  invocation: BridgeInvocationOptions = {}
): Promise<CliResult> {
  try {
    const spec = bridgeCommandRegistry.lookupBridgeCommand(command);
    let args = spec ? await spec.buildArgs({ command, flags, positionalTarget }) : {};
    // When the spec routes through catalog.editor.run, wrap the args into
    // { name: <toolName>, input: <original args> } and change the command.
    let bridgeCommand = command;
    if (spec?.toolName) {
      args = { name: spec.toolName, input: args };
      bridgeCommand = spec.bridgeCommand;
    }
    return executeBridgePayload(requestId, bridgeCommand, args, flags, runtime, invocation);
  } catch (error) {
    if (error instanceof BridgeArgumentError) {
      return failure(requestId, error.code, error.message, error.nextActions);
    }
    throw error;
  }
}

export async function executeBridgePayload(
  requestId: string,
  command: string,
  args: Record<string, unknown>,
  flags: BridgeFlags,
  runtime: BridgeRuntime,
  invocation: BridgeInvocationOptions = {}
): Promise<CliResult> {
  const requested = requestedTimeoutMs(flags, args);
  const timeoutMs = requested ?? invocation.defaultTimeoutMs;
  const input = requested !== undefined && Object.prototype.hasOwnProperty.call(args, "timeoutMs")
    ? Object.fromEntries(Object.entries(args).filter(([key]) => key !== "timeoutMs"))
    : args;
  const supervisor = new SessionSupervisor(selectionFlags(command, flags), runtime);

  try {
    const template = {
      requestId,
      command,
      input,
      timeoutMs,
      absoluteDeadlineMs: invocation.absoluteDeadlineMs,
      observationAbsoluteDeadlineMs: invocation.observationAbsoluteDeadlineMs,
      dryRun: getBoolean(flags, "dry-run") || getBoolean(flags, "preflight"),
      mutation: invocation.mutation ?? true,
      rootAdmission: runtime.rootAdmission,
      observationSelection: observationSelectionFrom(invocation),
      allowTransitionBinding: transitionBindingAllowed(invocation),
      returnTransitionResponse: transitionResponseAllowed(invocation),
    };
    const bridgeResult = invocation.mode === "read"
      ? await supervisor.read(template)
      : invocation.mode === "observeNow"
        ? await supervisor.observeNow(template)
        : await supervisor.execute(template);

    if (bridgeResult.ok === false) {
      const bridgeError = bridgeResult.error;
      const details = command === "debug.code.execute" && bridgeError.code === "E_UNITY_BUSY"
        ? liveCodeBusyDetails(requestId, args, bridgeErrorDetails(bridgeError))
        : bridgeError.details;
      return failure(requestId, bridgeError.code, bridgeError.message, bridgeError.nextActions, details, {
        transient: bridgeError.transient,
        retryAfterMs: bridgeError.retryAfterMs
      });
    }

    return success(requestId, projectPublicData(command, bridgeResult.data));
  } catch (error) {
    if (error instanceof PublicTerminalProjectionError) {
      return failure(requestId, "E_BRIDGE_RESPONSE_INVALID", error.message, ["Inspect the selected Unity Editor package revision and compile state before retrying."]);
    }
    const selectionFailure = instanceSelectionFailure(requestId, error);
    if (selectionFailure) return selectionFailure;
    if (error instanceof SessionSupervisorError) {
      return failure(requestId, error.code, error.message, error.nextActions, error.details, {
        transient: error.transient,
        retryAfterMs: error.retryAfterMs,
      });
    }
    return failure(requestId, "BRIDGE_CONNECTION_FAILED", errorMessage(error), [
      "Run unitycli instances list and inspect the selected Unity session.",
      "Run unitycli tools run editor.state for transition or blocker details."
    ], undefined, { transient: true, retryAfterMs: 100 });
  }
}

function requestedTimeoutMs(flags: BridgeFlags, _input: Record<string, unknown>): number | undefined {
  const raw = flags["timeout-ms"];
  const value = typeof raw === "string" ? Number(raw) : undefined;
  return value !== undefined && Number.isFinite(value) && value > 0 ? value : undefined;
}

function selectionFlags(_command: string, flags: BridgeFlags): BridgeFlags {
  return flags;
}

function bridgeErrorDetails(error: BridgeError): Record<string, unknown> {
  if (error.details && typeof error.details === "object" && !Array.isArray(error.details)) {
    return error.details;
  }
  return {};
}

function liveCodeBusyDetails(_requestId: string, _args: Record<string, unknown>, details: Record<string, unknown>): Record<string, unknown> {
  const { audit: _audit, logs: _logs, command: _command, requestId: _requestIdValue, ...publicDetails } = details;
  return publicDetails;
}

class PublicTerminalProjectionError extends Error {}

/** 将 durable Bridge terminal 投影成唯一公开业务 DTO，并拒绝任何不符合内部 terminal 契约的成功响应。 */
function projectPublicData(command: string, data: unknown): unknown {
  if (command !== "catalog.editor.run" && command !== "catalog.player.run") return data;
  if (!data || typeof data !== "object" || Array.isArray(data) || !Object.prototype.hasOwnProperty.call(data, "result")) {
    throw new PublicTerminalProjectionError("Bridge returned a malformed successful terminal.");
  }
  return (data as Record<string, unknown>).result;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
