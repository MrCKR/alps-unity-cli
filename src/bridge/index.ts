import { readFile, stat } from "node:fs/promises";
import { resolve } from "node:path";
import type { CliResult } from "../types.js";
import { durationMsFrom, failure, success } from "../output.js";
import { withRetry, type RetryOptions } from "../envelope.js";
import { instanceSelectionFailure, selectInstanceForFlags, type InstanceSelection } from "../instances.js";
import { BridgeArgumentError } from "./args.js";
import { bridgeCommandRegistry } from "./registry.js";
import type { BridgeError, BridgeFlags, BridgeRequest, BridgeResponse, BridgeRuntime } from "./types.js";

export { BridgeCommandRegistry, bridgeCommandRegistry } from "./registry.js";
export type { BridgeCommandSpec, BridgeRequest, BridgeResponse, BridgeRuntime } from "./types.js";

export async function executeBridgeCommand(
  requestId: string,
  command: string,
  flags: BridgeFlags,
  positionalTarget: string | undefined,
  runtime: BridgeRuntime
): Promise<CliResult> {
  try {
    const spec = bridgeCommandRegistry.lookupBridgeCommand(command);
    const args = spec ? await spec.buildArgs({ command, flags, positionalTarget }) : {};
    return executeBridgePayload(requestId, command, args, flags, runtime);
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
  runtime: BridgeRuntime
): Promise<CliResult> {
  const startMs = Date.now();
  let selectedInstance: InstanceSelection | undefined;
  let endpoint: string | undefined;
  try {
    selectedInstance = command.startsWith("custom-tool.") ? undefined : await selectInstanceForFlags(flags, runtime.dataDir ?? process.cwd());
    endpoint = bridgeEndpoint(flags, runtime, selectedInstance);
  } catch (error) {
    const result = instanceSelectionFailure(requestId, error);
    if (result) {
      return result;
    }
    throw error;
  }
  if (!runtime.bridgeExecute && !endpoint) {
    if (command === "ui.document.info" && typeof args.path === "string") {
      return readUiDocumentOffline(requestId, command, args.path);
    }
    return failure(requestId, "BRIDGE_ENDPOINT_REQUIRED", "Pass --bridge-endpoint <url> or set UNITY_CLI_BRIDGE_ENDPOINT for live query/edit/screenshot/code commands.", [
      "Open Unity with the Unity CLI Bridge package installed.",
      "Pass --bridge-endpoint http://127.0.0.1:<port>."
    ]);
  }

  const maxRetries = typeof flags.retry === "string" ? Number(flags.retry) : flags.retry === true ? 3 : 0;
  const retryOptions: RetryOptions = { maxRetries, onRetry: (attempt, error, retryAfterMs) => {
    process.stderr.write(`[retry] attempt ${attempt}/${maxRetries} after ${retryAfterMs}ms: ${error.error?.code} – ${error.error?.message}\n`);
  } };

  const result = await withRetry(async () => {
    try {
      const bridgeResult = runtime.bridgeExecute
        ? await runtime.bridgeExecute({ requestId, command, args })
        : await postBridgeExecute(endpoint!, { requestId, command, args });

      if (bridgeResult.ok === false) {
        const code = bridgeResult.error?.code ?? "BRIDGE_COMMAND_FAILED";
        const details = command === "code.exec" && code === "E_UNITY_BUSY" ? liveCodeBusyDetails(requestId, endpoint, args, bridgeErrorDetails(bridgeResult.error)) : bridgeErrorDetails(bridgeResult.error);
        return failure(requestId, code, bridgeResult.error?.message ?? "Unity Bridge rejected the command.", bridgeResult.nextActions ?? [], details);
      }

      if (command === "code.exec" && bridgeResult.data?.status === "running" && bridgeResult.data?.sideEffectSemantics === "started-before-http-timeout") {
        return liveCodeTimeoutFailure(requestId, endpoint, args, bridgeResult.data);
      }

      return success(requestId, {
        command,
        bridgeEndpoint: endpoint,
        ...(selectedInstance ? { selectedInstance: selectedInstanceSummary(selectedInstance) } : {}),
        ...bridgeResult.data
      });
    } catch (error) {
      if (command === "code.exec" && isAbortLikeError(error)) {
        return liveCodeTimeoutFailure(requestId, endpoint, args, { cause: errorMessage(error) });
      }
      if (command === "custom-tool.run" && isAbortLikeError(error)) {
        return failure(requestId, "E_EXECUTION_TIMEOUT", "custom-tool.run exceeded the requested timeout before the Unity Bridge returned a response.", [
          "Inspect UnityCliBridge.log and the requestId before retrying; the Editor may still be finishing a long-running tool.",
          "Use --job only as a preference marker in this release; Local Agent asynchronous custom-tool jobs are not implemented yet."
        ], { requestId, command, timeoutMs: typeof args.timeoutMs === "number" ? args.timeoutMs : 30_000, bridgeEndpoint: endpoint ?? null });
      }
      return failure(requestId, "BRIDGE_CONNECTION_FAILED", errorMessage(error), ["Check Unity Bridge endpoint and logs."]);
    }
  }, retryOptions);

  result.durationMs = durationMsFrom(startMs);
  return result;
}

function bridgeEndpoint(flags: BridgeFlags, runtime: BridgeRuntime, selectedInstance?: InstanceSelection): string | undefined {
  return typeof flags["bridge-endpoint"] === "string" ? flags["bridge-endpoint"] : runtime.bridgeEndpoint ?? process.env.UNITY_CLI_BRIDGE_ENDPOINT ?? selectedInstance?.instance.bridgeEndpoint;
}

function selectedInstanceSummary(selection: InstanceSelection): Record<string, string> {
  return {
    matchedBy: selection.matchedBy,
    instanceId: selection.instance.instanceId,
    projectPath: selection.instance.projectPath,
    projectHash: selection.instance.projectHash,
    bridgeEndpoint: selection.instance.bridgeEndpoint
  };
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

function bridgeErrorDetails(error: BridgeError | undefined): Record<string, unknown> {
  if (!error) {
    return {};
  }
  const { code: _code, message: _message, details, ...rest } = error;
  if (details !== undefined && typeof details === "object" && !Array.isArray(details) && Object.keys(rest).length === 0) {
    return details as Record<string, unknown>;
  }
  return details === undefined ? rest : { ...rest, details };
}

function bridgeHttpTimeoutMs(payload: BridgeRequest): number {
  if (payload.command === "code.exec" && typeof payload.args.timeoutMs === "number" && Number.isFinite(payload.args.timeoutMs) && payload.args.timeoutMs > 0) {
    return payload.args.timeoutMs + 5000;
  }
  if (payload.command === "custom-tool.run") {
    const timeoutMs = typeof payload.args.timeoutMs === "number" && Number.isFinite(payload.args.timeoutMs) ? payload.args.timeoutMs : 30_000;
    return Math.max(timeoutMs + 5000, 65_000);
  }
  return 10_000;
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
