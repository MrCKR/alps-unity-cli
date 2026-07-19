import { readFile, stat } from "node:fs/promises";
import { resolve } from "node:path";
import type { CliResult } from "../types.js";
import { durationMsFrom, failure, success } from "../output.js";
import { instanceSelectionFailure } from "../instances.js";
import { BridgeArgumentError } from "./args.js";
import { bridgeCommandRegistry } from "./registry.js";
import { confirmDestructiveExecution, destructiveSecurityProof, isDestructiveConfirmed, isDryRunRequested, type SecurityProof } from "../security.js";
import { ensurePackageBridge, PackageManagerError } from "../package-manager.js";
import { isProjectLocked, startUnityJob } from "../cli-commands/unity-job.js";
import { discoverUnity } from "../platform.js";
import { resolveProjectIdentity } from "../project-identity.js";
import type { JobRunner } from "../job-runner.js";
import type { JobStore } from "../job-store.js";
import { observationSelectionFrom, transitionBindingAllowed, type BridgeCommandSpec, type BridgeError, type BridgeFlags, type BridgeInvocationOptions, type BridgeRequest, type BridgeResponse, type BridgeRuntime, type BridgeSecurityDescriptor } from "./types.js";
import { bridgeHttpTimeoutMs, SessionSupervisor, SessionSupervisorError } from "./session-supervisor.js";

export { BridgeCommandRegistry, bridgeCommandRegistry } from "./registry.js";
export { bridgeHttpTimeoutMs, SessionSupervisor, SessionSupervisorError } from "./session-supervisor.js";
export type { BridgeCommandSpec, BridgeInvocationOptions, BridgeRequest, BridgeResponse, BridgeRuntime, BridgeSecurityDescriptor } from "./types.js";

export interface BridgeJobRuntime extends BridgeRuntime {
  dataDir: string;
  store: JobStore;
  runner: JobRunner;
}

export async function executeBridgeCommand(
  requestId: string,
  command: string,
  flags: BridgeFlags,
  positionalTarget: string | undefined,
  runtime: BridgeRuntime,
  securityDescriptor?: BridgeSecurityDescriptor,
  invocation: BridgeInvocationOptions = {}
): Promise<CliResult> {
  try {
    const spec = bridgeCommandRegistry.lookupBridgeCommand(command);
    const confirmation = await confirmDestructiveExecution(flags, {
      command,
      summary: command,
      destructiveHint: spec?.destructiveHint,
      supportsDryRun: spec?.supportsDryRun,
      requestId
    });
    if (confirmation) {
      return confirmation;
    }
    let args = spec ? await spec.buildArgs({ command, flags, positionalTarget }) : {};
    // When the spec routes through project-tool.run, wrap the args into
    // { name: <toolName>, input: <original args> } and change the command.
    let bridgeCommand = command;
    if (spec?.toolName) {
      args = { name: spec.toolName, input: args };
      bridgeCommand = spec.bridgeCommand;
    }
    return executeBridgePayload(requestId, bridgeCommand, args, flags, runtime, securityDescriptor, invocation);
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
  securityDescriptor?: BridgeSecurityDescriptor,
  invocation: BridgeInvocationOptions = {}
): Promise<CliResult> {
  const startMs = Date.now();
  const requested = requestedTimeoutMs(flags, args);
  const timeoutMs = requested ?? invocation.defaultTimeoutMs;
  const rawInput = requested !== undefined && Object.prototype.hasOwnProperty.call(args, "timeoutMs")
    ? Object.fromEntries(Object.entries(args).filter(([key]) => key !== "timeoutMs"))
    : args;
  const input = securityDescriptor?.liveDescriptorFingerprint && (command === "project-tool.run" || command === "runtime-tool.run")
    ? { ...rawInput, expectedDescriptorFingerprint: securityDescriptor.liveDescriptorFingerprint }
    : rawInput;
  const securityProof = bridgeSecurity(command, flags, input, securityDescriptor);
  const supervisor = new SessionSupervisor(selectionFlags(command, flags), runtime);

  try {
    const template = {
      requestId,
      command,
      input,
      timeoutMs,
      absoluteDeadlineMs: invocation.absoluteDeadlineMs,
      observationAbsoluteDeadlineMs: invocation.observationAbsoluteDeadlineMs,
      dryRun: isDryRunRequested(flags),
      securityProof,
      mutation: invocation.mutation ?? true,
      observationSelection: observationSelectionFrom(invocation),
      allowTransitionBinding: transitionBindingAllowed(invocation),
    };
    const bridgeResult = invocation.mode === "read"
      ? await supervisor.read(template)
      : invocation.mode === "observeNow"
        ? await supervisor.observeNow(template)
        : await supervisor.execute(template);

    if (bridgeResult.ok === false) {
      const bridgeError = bridgeResult.error;
      const details = command === "code.exec" && bridgeError.code === "E_UNITY_BUSY"
        ? liveCodeBusyDetails(requestId, args, bridgeErrorDetails(bridgeError))
        : bridgeError.details;
      const result = failure(requestId, bridgeError.code, bridgeError.message, bridgeError.nextActions, details, {
        transient: bridgeError.transient,
        retryAfterMs: bridgeError.retryAfterMs
      });
      result.durationMs = durationMsFrom(startMs);
      return result;
    }

    const result = success(requestId, { command, ...bridgeResult.data });
    result.durationMs = durationMsFrom(startMs);
    return result;
  } catch (error) {
    const selectionFailure = instanceSelectionFailure(requestId, error);
    if (selectionFailure) {
      if (command === "project-tool.run" && args.name === "ui.document.info") {
        const offlineInput = (args.input ?? {}) as Record<string, unknown>;
        if (typeof offlineInput.path === "string") return readUiDocumentOffline(requestId, "ui.document.info", offlineInput.path);
      }
      selectionFailure.durationMs = durationMsFrom(startMs);
      return selectionFailure;
    }
    if (error instanceof SessionSupervisorError) {
      const result = failure(requestId, error.code, error.message, error.nextActions, error.details, {
        transient: error.transient,
        retryAfterMs: error.retryAfterMs,
      });
      result.durationMs = durationMsFrom(startMs);
      return result;
    }
    const result = failure(requestId, "BRIDGE_CONNECTION_FAILED", errorMessage(error), [
      "Run unity_cli instances list and inspect the selected Unity session.",
      "Run unity_cli context read editor.state for transition or blocker details."
    ], undefined, { transient: true, retryAfterMs: 100 });
    result.durationMs = durationMsFrom(startMs);
    return result;
  }
}

/** 通过 Unity batchmode 执行任意支持 job 的 Bridge command。 */
export async function startBridgeCommandJob(
  requestId: string,
  toolName: string,
  bridgeCommand: string,
  args: Record<string, unknown>,
  flags: BridgeFlags,
  runtime: BridgeJobRuntime,
  spec?: BridgeCommandSpec
): Promise<CliResult> {
  const projectPath = resolve(typeof flags.project === "string" ? flags.project : process.cwd());
  const unityPath = typeof flags.unity === "string" ? flags.unity : discoverUnity();
  if (!unityPath) {
    return failure(requestId, "UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }
  if (isProjectLocked(projectPath)) {
    return failure(requestId, "UNITY_BUSY", `Cannot start Unity batchmode: the project is currently open in the Unity Editor (${projectPath}).`, [
      "Close the Unity Editor and retry.",
      "Wait for the process holding <project>/Temp/UnityLockfile to exit, then retry",
      "Run 'unity_cli jobs status <jobId>' to check existing jobs."
    ], {
      projectPath,
      kind: "bridge",
      lockFilePath: resolve(projectPath, "Temp", "UnityLockfile")
    });
  }
  try {
    const bridge = await ensurePackageBridge(projectPath);
    const timeoutMs = requestedTimeoutMs(flags, args);
    const input = timeoutMs !== undefined && Object.prototype.hasOwnProperty.call(args, "timeoutMs")
      ? Object.fromEntries(Object.entries(args).filter(([key]) => key !== "timeoutMs"))
      : args;
    const security = bridgeSecurity(bridgeCommand, flags, input);
    const identity = resolveProjectIdentity(projectPath);
    const absoluteDeadlineMs = Date.now() + (timeoutMs ?? 60_000);
    const payload: BridgeRequest = {
      protocolVersion: 2,
      command: bridgeCommand,
      requestId,
      instanceId: identity.instanceId,
      input,
      execution: {
        absoluteDeadlineUtc: new Date(absoluteDeadlineMs).toISOString(),
        completionContractVersion: 1,
        requestDigestVersion: 1,
        ...(isDryRunRequested(flags) ? { dryRun: true } : {}),
        ...(security ? { securityProof: security } : {})
      }
    };
    return startUnityJob("bridge", flags, runtime, requestId, [
      "-executeMethod", "UnityCli.UnityCliBatch.RunBridgeCommand",
      "-unityCliBridgeRequest", JSON.stringify(payload)
    ], {
      command: toolName,
      bridgeCommand,
      mode: "bridge-job",
      execution: { channel: "batchmode", exitState: "queued" },
      supportsJob: spec?.supportsJob === true,
      args,
      dryRun: isDryRunRequested(flags),
      bridge
    });
  } catch (error) {
    if (error instanceof PackageManagerError) {
      return failure(requestId, error.code, error.message, error.nextActions);
    }
    throw error;
  }
}

function bridgeSecurity(
  command: string,
  flags: BridgeFlags,
  input: Record<string, unknown>,
  explicitDescriptor?: BridgeSecurityDescriptor
): SecurityProof | undefined {
  if (explicitDescriptor) {
    return explicitDescriptor.destructiveHint
      ? destructiveSecurityProof(flags, explicitDescriptor.name, explicitDescriptor, input)
      : undefined;
  }

  const routeSpec = bridgeCommandRegistry.lookupBridgeCommand(command);
  let toolSpec = routeSpec;
  let toolName = routeSpec?.toolName ?? command;
  if ((command === "project-tool.run" || command === "runtime-tool.run") && typeof input.name === "string") {
    toolName = input.name;
    toolSpec = bridgeCommandRegistry.specs().find((candidate) => candidate.toolName === toolName);
  }
  if (!toolSpec?.destructiveHint && !(command === "runtime-tool.run" && isDestructiveConfirmed(flags))) return undefined;
  const descriptor = {
    name: toolName,
    destructiveHint: toolSpec?.destructiveHint === true,
    supportsDryRun: toolSpec?.supportsDryRun === true,
    bridgeCommand: toolSpec?.bridgeCommand ?? command
  };
  return destructiveSecurityProof(flags, toolName, descriptor, input);
}

function requestedTimeoutMs(flags: BridgeFlags, _input: Record<string, unknown>): number | undefined {
  const raw = flags["timeout-ms"];
  const value = typeof raw === "string" ? Number(raw) : undefined;
  return value !== undefined && Number.isFinite(value) && value > 0 ? value : undefined;
}

function selectionFlags(_command: string, flags: BridgeFlags): BridgeFlags {
  return flags;
}

async function readUiDocumentOffline(requestId: string, command: string, path: string): Promise<CliResult> {
  const documentPath = resolve(path);
  try {
    const documentStat = await stat(documentPath);
    if (!documentStat.isFile()) {
      return failure(requestId, "E_UI_DOCUMENT_PATH_INVALID", `UI document path is not a file: ${path}`, ["Pass a .uxml or .uss file path."]);
    }
    const content = await readFile(documentPath, "utf8");
    const extension = documentPath.toLowerCase().endsWith(".uss") ? "uss" : documentPath.toLowerCase().endsWith(".uxml") ? "uxml" : "unknown";
    const diagnostics = uiDocumentDiagnostics(content, extension);
    return success(requestId, {
      command,
      mode: "offline-reader",
      document: {
        path: documentPath,
        kind: extension,
        bytes: Buffer.byteLength(content, "utf8"),
        lines: content.length === 0 ? 0 : content.split(/\r?\n/).length
      },
      diagnostics
    });
  } catch (error) {
    return failure(requestId, "E_UI_DOCUMENT_NOT_FOUND", `UI document file not found or unreadable: ${path}`, [error instanceof Error ? error.message : "Check the path and permissions."]);
  }
}

function uiDocumentDiagnostics(content: string, extension: string): Array<Record<string, string>> {
  const diagnostics: Array<Record<string, string>> = [];
  if (extension === "unknown") {
    diagnostics.push({ level: "warning", code: "UI_DOCUMENT_EXTENSION_UNKNOWN", message: "Expected a .uxml or .uss file." });
  }
  if (extension === "uxml" && !/<ui:UXML|<UXML|<engine:UXML/.test(content)) {
    diagnostics.push({ level: "warning", code: "UI_UXML_ROOT_MISSING", message: "UXML root element was not detected." });
  }
  if (extension === "uss" && !/[.#]?[A-Za-z_][\w-]*\s*\{[^}]*\}/m.test(content)) {
    diagnostics.push({ level: "warning", code: "UI_USS_RULE_MISSING", message: "USS style rule was not detected." });
  }
  if (diagnostics.length === 0) {
    diagnostics.push({ level: "info", code: "UI_DOCUMENT_READ_OK", message: "UI document file is readable and passed basic checks." });
  }
  return diagnostics;
}

function bridgeErrorDetails(error: BridgeError): Record<string, unknown> {
  if (error.details && typeof error.details === "object" && !Array.isArray(error.details)) {
    return error.details;
  }
  return {};
}

function liveCodeBusyDetails(requestId: string, args: Record<string, unknown>, details: Record<string, unknown>): Record<string, unknown> {
  const timeoutMs = typeof args.timeoutMs === "number" && Number.isFinite(args.timeoutMs) ? args.timeoutMs : null;
  const logPath = typeof details.logPath === "string" ? details.logPath : "Unity project Logs/UnityCliBridge.log";
  return {
    requestId,
    command: "code.exec",
    channel: "live",
    timeoutMs,
    audit: { requestId, command: "code.exec", channel: "live", timeoutMs },
    logs: { bridge: logPath },
    ...details
  };
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
