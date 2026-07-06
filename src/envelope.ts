/**
 * CLI-side timeout and retry mechanism for bridge requests.
 * Defines timeout and retry-after envelope helpers.
 *
 * Key behaviors:
 * - Per-request timeout via --timeout-ms or command default.
 * - Transient errors (E_UNITY_COMPILING, E_UNITY_DOMAIN_RELOAD, etc.) can be retried automatically.
 * - retryAfterMs from the error response is honored; if absent, uses DEFAULT_RETRY_AFTER_MS.
 * - Non-transient errors and timeout errors are NOT retried automatically.
 */

import { TRANSIENT_ERROR_CODES, DEFAULT_RETRY_AFTER_MS, type CliResult } from "./output.js";

export interface RetryOptions {
  /** Maximum number of automatic retries for transient errors. Default: 0 (no auto-retry). */
  maxRetries?: number;
  /** Total wall-clock budget in ms for retries. 0 or absent = use per-attempt timeout only. */
  retryBudgetMs?: number;
  /** Callback invoked before each retry attempt. */
  onRetry?: (attempt: number, error: CliResult, retryAfterMs: number) => void;
}

/**
 * Execute a bridge request with optional automatic retry on transient errors.
 *
 * @param fn - The async function that performs the bridge request and returns a CliResult.
 * @param options - Retry configuration.
 * @returns The final CliResult (either success, non-transient error, or exhausted retries).
 */
export async function withRetry(
  fn: () => Promise<CliResult>,
  options: RetryOptions = {}
): Promise<CliResult> {
  const maxRetries = options.maxRetries ?? 0;
  const retryBudgetMs = options.retryBudgetMs ?? 0;
  const onRetry = options.onRetry;
  const startMs = Date.now();

  let lastResult = await fn();

  for (let attempt = 0; attempt < maxRetries; attempt += 1) {
    // Only retry on transient errors
    if (lastResult.ok || !lastResult.error || !isTransientError(lastResult.error.code)) {
      return lastResult;
    }

    // Check retry budget
    if (retryBudgetMs > 0 && Date.now() - startMs >= retryBudgetMs) {
      return lastResult;
    }

    const retryAfterMs = lastResult.error.retryAfterMs ?? DEFAULT_RETRY_AFTER_MS[lastResult.error.code] ?? 5000;

    if (onRetry) {
      onRetry(attempt + 1, lastResult, retryAfterMs);
    }

    await sleep(retryAfterMs);
    lastResult = await fn();
  }

  return lastResult;
}

/**
 * Check if an error code is a transient error that may succeed on retry.
 */
export function isTransientError(code: string): boolean {
  return TRANSIENT_ERROR_CODES.has(code);
}

/**
 * Determine if a CliResult represents a transient state that can be retried.
 * Also returns the recommended retryAfterMs.
 */
export function transientRetryInfo(result: CliResult): { isTransient: true; retryAfterMs: number; code: string } | { isTransient: false } {
  if (result.ok || !result.error) {
    return { isTransient: false };
  }
  const code = result.error.code;
  if (!isTransientError(code)) {
    return { isTransient: false };
  }
  return {
    isTransient: true,
    retryAfterMs: result.error.retryAfterMs ?? DEFAULT_RETRY_AFTER_MS[code] ?? 5000,
    code,
  };
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
