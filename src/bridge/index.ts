import { readFile, stat } from "node:fs/promises";
import { resolve } from "node:path";
import type { CliResult } from "../types.js";
import { durationMsFrom, failure, success } from "../output.js";
import { withRetry, type RetryOptions } from "../envelope.js";
import { instanceSelectionFailure, selectInstanceForFlags, type InstanceSelection } from "../instances.js";
import { BridgeArgumentError } from "./args.js";
import { bridgeCommandRegistry } from "./registry.js";
import { confirmDestructiveExecution, destructiveSecurityProof, isDestructiveConfirmed, isDryRunRequested, type SecurityProof } from "../security.js";
import { ensurePackageBridge, PackageManagerError } from "../package-manager.js";
import { isProjectLocked, startUnityJob } from "../cli-commands/unity-job.js";
import { discoverUnity } from "../platform.js";
import type { JobRunner } from "../job-runner.js";
import type { JobStore } from "../job-store.js";
import type { BridgeCommandSpec, BridgeError, BridgeFlags, BridgeRequest, BridgeResponse, BridgeRuntime, BridgeSecurityDescriptor } from "./types.js";

export { BridgeCommandRegistry, bridgeCommandRegistry } from "./registry.js";
export type { BridgeCommandSpec, BridgeRequest, BridgeResponse, BridgeRuntime, BridgeSecurityDescriptor } from "./types.js";

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
  securityDescriptor?: BridgeSecurityDescriptor
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
    return executeBridgePayload(requestId, bridgeCommand, args, flags, runtime, securityDescriptor);
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
  securityDescriptor?: BridgeSecurityDescriptor
): Promise<CliResult> {
  const startMs = Date.now();
  let selectedInstance: InstanceSelection | undefined;
  let endpoint: string | undefined;
  try {
    selectedInstance = await selectInstanceForFlags(selectionFlags(command, flags), runtime.dataDir ?? process.cwd());
    endpoint = bridgeEndpoint(flags, runtime, selectedInstance);
  } catch (error) {
    const result = instanceSelectionFailure(requestId, error);
    if (result) {
      return result;
    }
    throw error;
  }
  if (!runtime.bridgeExecute && !endpoint) {
    // 当前 project-tool.run 路由允许 ui.document.info 直接读取磁盘上的 UXML。
    if (command === "project-tool.run" && args.name === "ui.document.info") {
      const offlineInput = (args.input ?? {}) as Record<string, unknown>;
      if (typeof offlineInput.path === "string") {
        return readUiDocumentOffline(requestId, "ui.document.info", offlineInput.path);
      }
    }
    return failure(requestId, "BRIDGE_ENDPOINT_REQUIRED", "Pass --bridge-endpoint <url> or set UNITY_CLI_BRIDGE_ENDPOINT for live query/edit/screenshot/code commands.", [
      "Open Unity with the Unity CLI Bridge package installed.",
      "Pass --bridge-endpoint http://127.0.0.1:<port>."
    ]);
  }

  const maxRetries = typeof flags.retry === "string" ? Number(flags.retry) : flags.retry === true ? 3 : 0;
  const retryOptions: RetryOptions = { maxRetries, onRetry: (attempt, error, retryAfterMs) => {
    if (!error.ok) {
      process.stderr.write(`[retry] attempt ${attempt}/${maxRetries} after ${retryAfterMs}ms: ${error.error.code} – ${error.error.message}\n`);
    }
  } };

  const result = await withRetry(async () => {
    try {
      const timeoutMs = requestedTimeoutMs(flags, args);
      const input = timeoutMs !== undefined && Object.prototype.hasOwnProperty.call(args, "timeoutMs")
        ? Object.fromEntries(Object.entries(args).filter(([key]) => key !== "timeoutMs"))
        : args;
      const security = bridgeSecurity(command, flags, input, securityDescriptor);
      const payload: BridgeRequest = {
        requestId,
        command,
        input,
        ...((isDryRunRequested(flags) || timeoutMs !== undefined) ? {
          execution: {
            ...(isDryRunRequested(flags) ? { dryRun: true } : {}),
            ...(timeoutMs !== undefined ? { timeoutMs } : {})
          }
        } : {}),
        ...(security ? { security } : {})
      };
      const bridgeResult = runtime.bridgeExecute
        ? await runtime.bridgeExecute(payload)
        : await postBridgeExecute(endpoint!, payload);

      if (bridgeResult.ok === false) {
        const bridgeError = bridgeResult.error;
        const code = bridgeError.code;
        const details = command === "code.exec" && code === "E_UNITY_BUSY"
          ? liveCodeBusyDetails(requestId, endpoint, args, bridgeErrorDetails(bridgeError))
          : bridgeError.details;
        return failure(requestId, code, bridgeError.message, bridgeError.nextActions, details, {
          transient: bridgeError.transient,
          retryAfterMs: bridgeError.retryAfterMs
        });
      }

      if (command === "code.exec" && bridgeResult.data?.status === "running" && bridgeResult.data?.sideEffectSemantics === "started-before-http-timeout") {
        return liveCodeTimeoutFailure(requestId, endpoint, args, bridgeResult.data);
      }

      return success(requestId, {
        command,
        bridgeEndpoint: endpoint,
        ...bridgeResult.data
      });
    } catch (error) {
      if (command === "code.exec" && isAbortLikeError(error)) {
        return liveCodeTimeoutFailure(requestId, endpoint, args, { cause: errorMessage(error) });
      }

      // live transport 在 domain reload、PlayMode 切换或 listener 重启时可能短暂断开，调用方应按 envelope 重试。
      return failure(requestId, "BRIDGE_CONNECTION_FAILED", errorMessage(error), ["Check Unity Bridge endpoint and logs."], undefined, {
        transient: true,
        retryAfterMs: 1000
      });
    }
  }, retryOptions);

  result.durationMs = durationMsFrom(startMs);
  return result;
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
    const payload: BridgeRequest = {
      command: bridgeCommand,
      requestId,
      input,
      ...((isDryRunRequested(flags) || timeoutMs !== undefined) ? {
        execution: {
          ...(isDryRunRequested(flags) ? { dryRun: true } : {}),
          ...(timeoutMs !== undefined ? { timeoutMs } : {})
        }
      } : {}),
      ...(security ? { security } : {})
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

function bridgeEndpoint(flags: BridgeFlags, runtime: BridgeRuntime, selectedInstance?: InstanceSelection): string | undefined {
  const explicit = flags["bridge-endpoint"];
  return typeof explicit === "string" && explicit.length > 0
    ? explicit
    : runtime.bridgeEndpoint ?? process.env.UNITY_CLI_BRIDGE_ENDPOINT ?? selectedInstance?.instance.bridgeEndpoint;
}

function selectionFlags(command: string, flags: BridgeFlags): BridgeFlags {
  const { target: _target, ...rest } = flags;
  return rest;
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

async function postBridgeExecute(endpoint: string, payload: BridgeRequest): Promise<BridgeResponse> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), bridgeHttpTimeoutMs(payload));
  try {
    const response = await fetch(new URL("/bridge/execute", endpoint), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(payload),
      signal: controller.signal
    });
    if (!response.ok) {
      throw new Error(`Bridge returned HTTP ${response.status}.`);
    }
    return (await response.json()) as BridgeResponse;
  } finally {
    clearTimeout(timeout);
  }
}

function bridgeErrorDetails(error: BridgeError): Record<string, unknown> {
  if (error.details && typeof error.details === "object" && !Array.isArray(error.details)) {
    return error.details;
  }
  return {};
}

export function bridgeHttpTimeoutMs(payload: BridgeRequest): number {
  const timeoutMs = payload.execution?.timeoutMs;
  return typeof timeoutMs === "number" && Number.isFinite(timeoutMs) && timeoutMs > 0 ? timeoutMs + 5000 : 65_000;
}

function liveCodeBusyDetails(requestId: string, endpoint: string | undefined, args: Record<string, unknown>, details: Record<string, unknown>): Record<string, unknown> {
  const timeoutMs = typeof args.timeoutMs === "number" && Number.isFinite(args.timeoutMs) ? args.timeoutMs : null;
  const logPath = typeof details.logPath === "string" ? details.logPath : "Unity project Logs/UnityCliBridge.log";
  return {
    requestId,
    command: "code.exec",
    channel: "live",
    timeoutMs,
    bridgeEndpoint: endpoint ?? null,
    audit: { requestId, command: "code.exec", channel: "live", timeoutMs },
    logs: { bridge: logPath },
    ...details
  };
}

function liveCodeTimeoutFailure(requestId: string, endpoint: string | undefined, args: Record<string, unknown>, details: Record<string, unknown>): CliResult {
  const timeoutMs = typeof args.timeoutMs === "number" && Number.isFinite(args.timeoutMs) ? args.timeoutMs : null;
  const logPath = typeof details.logPath === "string" ? details.logPath : "Unity project Logs/UnityCliBridge.log";
  return failure(requestId, "E_EXECUTION_TIMEOUT", timeoutMs === null ? "Runtime Code live execution did not complete before the bridge HTTP timeout." : `Runtime Code live execution did not complete within ${timeoutMs}ms.`, [
    "Inspect the Unity Editor for a long-running script or busy dialog.",
    "Inspect the Unity project Logs/UnityCliBridge.log for bridge_request_timeout_started and requestId.",
    "Retry after Unity is responsive, or increase --timeout-ms for intentionally long-running code."
  ], {
    requestId,
    command: "code.exec",
    channel: "live",
    timeoutMs,
    bridgeEndpoint: endpoint ?? null,
    audit: { requestId, command: "code.exec", channel: "live", timeoutMs },
    logs: { bridge: logPath },
    ...details
  });
}

function isAbortLikeError(error: unknown): boolean {
  const name = error instanceof Error ? error.name : "";
  const message = errorMessage(error);
  return name === "AbortError" || /\b(abort|aborted|timed? out|timeout)\b/i.test(message);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
