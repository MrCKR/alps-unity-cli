import { randomUUID } from "node:crypto";
import type { CliError, CliResult } from "./types.js";

export function createRequestId(): string {
  return `req_${randomUUID()}`;
}

export function success<T>(requestId: string, data: T): CliResult<T> {
  return { ok: true, requestId, data };
}

export function failure(requestId: string, code: string, message: string, nextActions: string[] = [], details?: unknown): CliResult {
  const safeDetails = details && typeof details === "object" && !Array.isArray(details) ? details as Record<string, unknown> : undefined;
  const { code: _code, message: _message, requestId: _requestId, next_actions: _nextActions, ...flatDetails } = safeDetails ?? {};
  const error: CliError = { code, message, requestId, ...flatDetails };
  if (details !== undefined) {
    error.details = details;
  }
  if (nextActions.length > 0) {
    error.next_actions = nextActions;
  }
  return { ok: false, requestId, error };
}

export function printJson(result: CliResult): void {
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}
