/**
 * Unified envelope types and helpers for all CLI output.
 * Defines request-response envelope, error code, and timeout helpers.
 *
 * Every CLI command must return a CliResult (success or error) through these helpers.
 * The error code taxonomy is defined in ERROR_CODES below.
 */

/** Structured error codes – the taxonomy covers all error categories for the CLI envelope. */
export const ERROR_CODES = {
  // ─── Bridge / connection ───
  BRIDGE_ENDPOINT_REQUIRED: "BRIDGE_ENDPOINT_REQUIRED",
  BRIDGE_CONNECTION_FAILED: "BRIDGE_CONNECTION_FAILED",
  BRIDGE_COMMAND_FAILED: "BRIDGE_COMMAND_FAILED",
  BRIDGE_COMMAND_UNSUPPORTED: "BRIDGE_COMMAND_UNSUPPORTED",
  BRIDGE_REQUEST_EXPIRED: "BRIDGE_REQUEST_EXPIRED",
  BRIDGE_COMPILE_FAILED: "BRIDGE_COMPILE_FAILED",
  E_BRIDGE_READ_BODY_FAILED: "E_BRIDGE_READ_BODY_FAILED",
  E_BRIDGE_INTERNAL_ERROR: "E_BRIDGE_INTERNAL_ERROR",

  // ─── Unity transient state ───
  E_UNITY_BUSY: "E_UNITY_BUSY",
  E_UNITY_COMPILING: "E_UNITY_COMPILING",
  E_UNITY_IMPORTING: "E_UNITY_IMPORTING",
  E_UNITY_DOMAIN_RELOAD: "E_UNITY_DOMAIN_RELOAD",
  E_UNITY_PLAY_MODE: "E_UNITY_PLAY_MODE",

  // ─── Timeout ───
  E_EXECUTION_TIMEOUT: "E_EXECUTION_TIMEOUT",

  // ─── Instance ───
  E_INSTANCE_NOT_FOUND: "E_INSTANCE_NOT_FOUND",
  E_INSTANCE_AMBIGUOUS: "E_INSTANCE_AMBIGUOUS",
  E_INSTANCE_STALE: "E_INSTANCE_STALE",
  E_TARGET_AMBIGUOUS: "E_TARGET_AMBIGUOUS",
  E_TARGET_UNREACHABLE: "E_TARGET_UNREACHABLE",
  E_TARGET_STALE: "E_TARGET_STALE",
  E_TARGET_ALIAS_CONFLICT: "E_TARGET_ALIAS_CONFLICT",
  // Target preflight busy state; E_UNITY_BUSY remains the Bridge execution-layer busy signal.
  E_TARGET_BUSY: "E_TARGET_BUSY",
  // Runtime executor and session lifecycle error codes.
  E_RUNTIME_NOT_READY: "E_RUNTIME_NOT_READY",
  E_RUNTIME_SESSION_STALE: "E_RUNTIME_SESSION_STALE",
  E_RUNTIME_TOOL_TARGET_MISMATCH: "E_RUNTIME_TOOL_TARGET_MISMATCH",
  E_RUNTIME_TIMEOUT: "E_RUNTIME_TIMEOUT",

  // ─── Job ───
  JOB_NOT_FOUND: "JOB_NOT_FOUND",
  JOB_ID_REQUIRED: "JOB_ID_REQUIRED",
  JOB_CANCEL_FAILED: "JOB_CANCEL_FAILED",

  // ─── Schema / validation ───
  E_SCHEMA_INVALID: "E_SCHEMA_INVALID",
  E_PATH_INVALID: "E_PATH_INVALID",

  // ─── Target / resource not found ───
  E_TARGET_NOT_FOUND: "E_TARGET_NOT_FOUND",
  E_CAMERA_NOT_FOUND: "E_CAMERA_NOT_FOUND",
  E_PREFAB_OUTPUT_EXISTS: "E_PREFAB_OUTPUT_EXISTS",
  E_PREFAB_DIRTY_CONFLICT: "E_PREFAB_DIRTY_CONFLICT",
  E_PREFAB_STAGE_OPEN_FAILED: "E_PREFAB_STAGE_OPEN_FAILED",
  E_PREFAB_STAGE_SAVE_FAILED: "E_PREFAB_STAGE_SAVE_FAILED",
  E_PREFAB_STAGE_CLOSE_FAILED: "E_PREFAB_STAGE_CLOSE_FAILED",
  E_SHADER_NOT_FOUND: "E_SHADER_NOT_FOUND",
  E_SHADER_REQUIRED: "E_SHADER_REQUIRED",
  E_MATERIAL_OUTPUT_EXISTS: "E_MATERIAL_OUTPUT_EXISTS",
  E_MATERIAL_PROPERTY_REQUIRED: "E_MATERIAL_PROPERTY_REQUIRED",
  E_MATERIAL_PROPERTY_NOT_FOUND: "E_MATERIAL_PROPERTY_NOT_FOUND",
  E_MATERIAL_PROPERTY_TYPE_UNSUPPORTED: "E_MATERIAL_PROPERTY_TYPE_UNSUPPORTED",
  E_MATERIAL_PROPERTY_TYPE_MISMATCH: "E_MATERIAL_PROPERTY_TYPE_MISMATCH",
  E_MATERIAL_TARGET_UNSUPPORTED: "E_MATERIAL_TARGET_UNSUPPORTED",
  E_MATERIAL_SLOT_INVALID: "E_MATERIAL_SLOT_INVALID",
  E_MATERIAL_ASSIGN_UNSUPPORTED: "E_MATERIAL_ASSIGN_UNSUPPORTED",
  E_PROBUILDER_SHAPE_UNSUPPORTED: "E_PROBUILDER_SHAPE_UNSUPPORTED",

  // ─── Runtime code ───
  E_CODE_SOURCE_INVALID: "E_CODE_SOURCE_INVALID",
  E_CODE_COMPILE_FAILED: "E_CODE_COMPILE_FAILED",
  E_CODE_RUNTIME_EXCEPTION: "E_CODE_RUNTIME_EXCEPTION",
  E_REFERENCE_RESOLUTION_FAILED: "E_REFERENCE_RESOLUTION_FAILED",
  E_ARTIFACT_WRITE_FAILED: "E_ARTIFACT_WRITE_FAILED",
  E_UI_DOCUMENT_PATH_INVALID: "E_UI_DOCUMENT_PATH_INVALID",
  E_UI_DOCUMENT_NOT_FOUND: "E_UI_DOCUMENT_NOT_FOUND",
  E_TOOL_NOT_FOUND: "E_TOOL_NOT_FOUND",
  E_TOOL_NOT_FOUND_FOR_TARGET: "E_TOOL_NOT_FOUND_FOR_TARGET",
  E_TOOL_SCHEMA_CONFLICT: "E_TOOL_SCHEMA_CONFLICT",
  E_PLAYMODE_NOT_READY: "E_PLAYMODE_NOT_READY",
  E_TOOL_AVAILABILITY_STALE: "E_TOOL_AVAILABILITY_STALE",

  // ─── Unity / environment ───
  UNITY_NOT_FOUND: "UNITY_NOT_FOUND",
  UNITY_SPAWN_FAILED: "UNITY_SPAWN_FAILED",
  UNITY_BATCHMODE_FAILED: "UNITY_BATCHMODE_FAILED",
  UNITY_BUSY: "UNITY_BUSY",
  PACKAGE_NOT_FOUND: "PACKAGE_NOT_FOUND",
  PACKAGE_VERSION_CONFLICT: "PACKAGE_VERSION_CONFLICT",
  PACKAGE_RESOLVE_FAILED: "PACKAGE_RESOLVE_FAILED",

  E_EXTENSION_UNAVAILABLE: "E_EXTENSION_UNAVAILABLE",
  E_EXTENSION_NOT_INSTALLED: "E_EXTENSION_NOT_INSTALLED",
  E_EXTENSION_DESCRIPTOR_LOAD_FAILED: "E_EXTENSION_DESCRIPTOR_LOAD_FAILED",

  // ─── Context ───
  E_CONTEXT_NOT_FOUND: "E_CONTEXT_NOT_FOUND",
  E_CONTEXT_NAME_REQUIRED: "E_CONTEXT_NAME_REQUIRED",
  UNKNOWN_CONTEXT_COMMAND: "UNKNOWN_CONTEXT_COMMAND",
  CONTEXT_DESCRIBE_NAME_REQUIRED: "CONTEXT_DESCRIBE_NAME_REQUIRED",

  // ─── Generic ───
  UNKNOWN_COMMAND: "UNKNOWN_COMMAND",
  UNKNOWN_BATCH_COMMAND: "UNKNOWN_BATCH_COMMAND",
  UNKNOWN_JOB_COMMAND: "UNKNOWN_JOB_COMMAND",
  UNKNOWN_INSTANCES_COMMAND: "UNKNOWN_INSTANCES_COMMAND",
  UNKNOWN_PACKAGE_COMMAND: "UNKNOWN_PACKAGE_COMMAND",
  UNKNOWN_PACKAGE_REGISTRY_COMMAND: "UNKNOWN_PACKAGE_REGISTRY_COMMAND",
  UNKNOWN_SCOPED_REGISTRY_COMMAND: "UNKNOWN_SCOPED_REGISTRY_COMMAND",
  UNKNOWN_CODE_COMMAND: "UNKNOWN_CODE_COMMAND",
  UNKNOWN_GROUP_COMMAND: "UNKNOWN_GROUP_COMMAND",
  COMMAND_FAILED: "COMMAND_FAILED",
  BATCH_FILE_REQUIRED: "BATCH_FILE_REQUIRED",
  WORKER_ARGS_REQUIRED: "WORKER_ARGS_REQUIRED",
  WORKER_FAILED: "WORKER_FAILED",
  E_SCENE_VIEW_NOT_AVAILABLE: "E_SCENE_VIEW_NOT_AVAILABLE",
  E_OUTPUT_EXISTS: "E_OUTPUT_EXISTS",
  E_PROFILER_FORMAT_UNSUPPORTED: "E_PROFILER_FORMAT_UNSUPPORTED",
  E_PREFAB_CREATE_FAILED: "E_PREFAB_CREATE_FAILED",
} as const;

export type ErrorCode = typeof ERROR_CODES[keyof typeof ERROR_CODES];

/** Transient error codes – these indicate Unity is temporarily unavailable (compiling, importing, domain reload, play mode).
 * The CLI should return these with transient=true and a retry_after_ms hint rather than generic timeout. */
export const TRANSIENT_ERROR_CODES: ReadonlySet<string> = new Set([
  ERROR_CODES.E_UNITY_BUSY,
  ERROR_CODES.E_UNITY_COMPILING,
  ERROR_CODES.E_UNITY_IMPORTING,
  ERROR_CODES.E_UNITY_DOMAIN_RELOAD,
  ERROR_CODES.E_UNITY_PLAY_MODE,
  ERROR_CODES.E_RUNTIME_NOT_READY,
  ERROR_CODES.UNITY_BUSY,
]);

/** Default retry_after_ms values for transient errors. */
export const DEFAULT_RETRY_AFTER_MS: Record<string, number> = {
  [ERROR_CODES.E_UNITY_COMPILING]: 5000,
  [ERROR_CODES.E_UNITY_IMPORTING]: 5000,
  [ERROR_CODES.E_UNITY_DOMAIN_RELOAD]: 10000,
  [ERROR_CODES.E_UNITY_PLAY_MODE]: 3000,
  [ERROR_CODES.E_UNITY_BUSY]: 5000,
  [ERROR_CODES.E_RUNTIME_NOT_READY]: 3000,
  [ERROR_CODES.UNITY_BUSY]: 15000,
};

/** CLI-side default timeouts by command category. */
export const DEFAULT_TIMEOUT_MS: Record<string, number> = {
  bridge_read: 10_000,
  bridge_write: 30_000,
  bridge_long: 65_000,
  code_live: 60_000,
  code_batch: 300_000,
  job_wait: 600_000,
};

/** Maximum allowed timeouts by category. */
export const MAX_TIMEOUT_MS: Record<string, number> = {
  bridge_read: 60_000,
  bridge_write: 120_000,
  bridge_long: 600_000,
  code_live: 600_000,
  code_batch: 1_800_000,
  job_wait: 3_600_000,
};

export interface CliError {
  code: ErrorCode | string;
  message: string;
  requestId: string;
  details?: unknown;
  next_actions?: string[];
  transient?: boolean;
  retry_after_ms?: number;
  [key: string]: unknown;
}

// CliResult 的唯一定义在 ./types.ts；此处仅按需引入以供 success/failure 等辅助函数使用。
import type { CliResult } from "./types.js";
export type { CliResult };

import { randomUUID } from "node:crypto";

export function createRequestId(): string {
  return `req_${randomUUID()}`;
}

/** Create a success envelope. */
export function success<T>(requestId: string, data: T, options?: { warnings?: string[]; logsRef?: string; jobId?: string; durationMs?: number }): CliResult<T> {
  return {
    ok: true,
    requestId,
    data,
    ...options,
  };
}

/** Create a failure envelope with structured error code.
 * Automatically marks transient errors with transient=true and retry_after_ms. */
export function failure(
  requestId: string,
  code: ErrorCode | string,
  message: string,
  nextActions: string[] = [],
  details?: unknown,
  options?: { durationMs?: number }
): CliResult {
  const isTransient = TRANSIENT_ERROR_CODES.has(code);
  const retryAfterMs = isTransient ? DEFAULT_RETRY_AFTER_MS[code] ?? 5000 : undefined;

  // Flatten details into the error object (excluding reserved keys)
  const safeDetails = details && typeof details === "object" && !Array.isArray(details) ? details as Record<string, unknown> : undefined;
  const { code: _code, message: _message, requestId: _requestId, next_actions: _nextActions, transient: _transient, retry_after_ms: _retryAfterMs, ...flatDetails } = safeDetails ?? {};

  const error: CliError = {
    code,
    message,
    requestId,
    ...(isTransient ? { transient: true, retry_after_ms: retryAfterMs } : {}),
    ...flatDetails,
  };

  if (details !== undefined) {
    error.details = details;
  }
  if (nextActions.length > 0) {
    error.next_actions = nextActions;
  }

  return {
    ok: false,
    requestId,
    error,
    ...options,
  };
}

/** Compute wall-clock durationMs from a start timestamp. */
export function durationMsFrom(startMs: number): number {
  return Date.now() - startMs;
}

/** Wrap an async CLI handler to automatically measure durationMs. */
export async function withDuration<T>(requestId: string, fn: () => Promise<CliResult<T>>): Promise<CliResult<T>> {
  const startMs = Date.now();
  const result = await fn();
  result.durationMs = durationMsFrom(startMs);
  return result;
}

export function printJson(result: CliResult): void {
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}

/** Validate and clamp a user-provided timeout against defaults and maxima. */
export function clampTimeout(requestedMs: number | undefined, category: string): number {
  const fallback = DEFAULT_TIMEOUT_MS[category] ?? 10_000;
  const maximum = MAX_TIMEOUT_MS[category] ?? 600_000;
  if (!requestedMs || requestedMs <= 0) {
    return fallback;
  }
  return Math.min(requestedMs, maximum);
}