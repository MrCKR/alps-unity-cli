/**
 * Tests for the unified envelope module (output.ts) and envelope/retry module (envelope.ts).
 * Covers:
 * - Envelope serialization/deserialization (success and error)
 * - Error code taxonomy validation
 * - Transient error detection and retryAfterMs
 * - Duration tracking
 * - Timeout clamping
 * - Retry mechanism (withRetry)
 */

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  success,
  failure,
  createRequestId,
  ERROR_CODES,
  TRANSIENT_ERROR_CODES,
  DEFAULT_RETRY_AFTER_MS,
  DEFAULT_TIMEOUT_MS,
  MAX_TIMEOUT_MS,
  clampTimeout,
  durationMsFrom,
  type CliError,
  type CliResult,
} from "../src/output.js";
import {
  withRetry,
  isTransientError,
  transientRetryInfo,
} from "../src/envelope.js";

// ─── Request ID ───

describe("createRequestId", () => {
  it("returns a string starting with 'req_'", () => {
    const id = createRequestId();
    assert.ok(id.startsWith("req_"));
    assert.ok(id.length > 4);
  });

  it("returns unique IDs", () => {
    const ids = Array.from({ length: 100 }, () => createRequestId());
    const unique = new Set(ids);
    assert.equal(unique.size, 100);
  });
});

// ─── Success Envelope ───

describe("success envelope", () => {
  it("creates a success envelope with ok=true, requestId, and data", () => {
    const requestId = createRequestId();
    const result = success(requestId, { tools: ["scene.hierarchy"] });
    assert.equal(result.ok, true);
    assert.equal(result.requestId, requestId);
    assert.deepEqual(result.data, { tools: ["scene.hierarchy"] });
  });

  it("includes optional fields: warnings, logsRef, jobId, durationMs", () => {
    const requestId = createRequestId();
    const result = success(requestId, { instances: 1 }, {
      warnings: ["slow query"],
      logsRef: "/path/to/log",
      jobId: "job_abc123",
      durationMs: 150,
    });
    assert.equal(result.ok, true);
    assert.deepEqual(result.warnings, ["slow query"]);
    assert.equal(result.logsRef, "/path/to/log");
    assert.equal(result.jobId, "job_abc123");
    assert.equal(result.durationMs, 150);
  });

  it("data can be any object type", () => {
    const requestId = createRequestId();
    const result = success(requestId, { version: "0.1.0" });
    assert.deepEqual(result.data, { version: "0.1.0" });
  });
});

// ─── Failure Envelope ───

describe("failure envelope", () => {
  it("creates a failure envelope with ok=false, requestId, and error", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "UNKNOWN_COMMAND", "Unknown command: foo", ["Run unity_cli --help."]);
    assert.equal(result.ok, false);
    assert.equal(result.requestId, requestId);
    assert.equal(result.error?.code, "UNKNOWN_COMMAND");
    assert.equal(result.error?.message, "Unknown command: foo");
    assert.deepEqual(result.error?.nextActions, ["Run unity_cli --help."]);
  });

  it("transient errors are marked with transient=true and retryAfterMs", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_UNITY_COMPILING", "Unity Editor is compiling scripts.", ["Wait for Unity to finish compiling and retry."]);
    assert.equal(result.ok, false);
    assert.equal(result.error?.transient, true);
    assert.equal(result.error?.retryAfterMs, 5000);
    assert.equal(result.error?.code, "E_UNITY_COMPILING");
  });

  it("E_UNITY_DOMAIN_RELOAD transient error has retryAfterMs=10000", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_UNITY_DOMAIN_RELOAD", "Unity Editor is reloading assemblies.");
    assert.equal(result.error?.transient, true);
    assert.equal(result.error?.retryAfterMs, 10000);
  });

  it("E_UNITY_PLAY_MODE transient error has retryAfterMs=3000", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_UNITY_PLAY_MODE", "Unity Editor is entering Play mode.");
    assert.equal(result.error?.transient, true);
    assert.equal(result.error?.retryAfterMs, 3000);
  });

  it("E_UNITY_IMPORTING transient error has retryAfterMs=5000", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_UNITY_IMPORTING", "Unity Editor is importing assets.");
    assert.equal(result.error?.transient, true);
    assert.equal(result.error?.retryAfterMs, 5000);
  });

  it("E_UNITY_BUSY transient error has retryAfterMs=5000", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_UNITY_BUSY", "Unity Editor is busy.");
    assert.equal(result.error?.transient, true);
    assert.equal(result.error?.retryAfterMs, 5000);
  });

  it("non-transient errors have transient=undefined and no retryAfterMs", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "UNKNOWN_COMMAND", "Unknown command.");
    assert.equal(result.error?.transient, undefined);
    assert.equal(result.error?.retryAfterMs, undefined);
  });

  it("timeout error E_EXECUTION_TIMEOUT is NOT transient", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_EXECUTION_TIMEOUT", "Execution timed out.", ["Increase --timeout-ms."]);
    assert.equal(result.error?.transient, undefined);
  });

  it("details are flattened into the error object", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_EXECUTION_TIMEOUT", "Timed out.", [], {
      jobId: "job_123",
      timeoutMs: 30000,
      bridgeEndpoint: "http://127.0.0.1:47392",
    });
    assert.equal(result.error?.jobId, "job_123");
    assert.equal(result.error?.timeoutMs, 30000);
    assert.equal(result.error?.bridgeEndpoint, "http://127.0.0.1:47392");
    // details is also preserved as-is
    assert.ok(result.error?.details);
  });

  it("failure envelope includes optional durationMs", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "BRIDGE_CONNECTION_FAILED", "Connection failed.", [], undefined, { durationMs: 250 });
    assert.equal(result.durationMs, 250);
  });
});

// ─── Error Code Taxonomy ───

describe("ERROR_CODES taxonomy", () => {
  it("all error codes follow the pattern: uppercase with underscores", () => {
    for (const [key, code] of Object.entries(ERROR_CODES)) {
      assert.match(code, /^[A-Z][A-Z0-9_]*(\.[A-Z][A-Z0-9_]*)?$/, `Error code ${code} (key: ${key}) does not match pattern`);
    }
  });

  it("every error code value equals its key (self-referencing const)", () => {
    for (const [key, code] of Object.entries(ERROR_CODES)) {
      assert.equal(code, key, `Error code value ${code} does not equal key ${key}`);
    }
  });

  it("transient error codes are in the TRANSIENT_ERROR_CODES set", () => {
    const expectedTransient = [
      "E_UNITY_BUSY",
      "E_UNITY_COMPILING",
      "E_UNITY_IMPORTING",
      "E_UNITY_DOMAIN_RELOAD",
      "E_UNITY_PLAY_MODE",
    ];
    for (const code of expectedTransient) {
      assert.ok(TRANSIENT_ERROR_CODES.has(code), `Missing transient code: ${code}`);
    }
  });

  it("no non-transient codes are in the TRANSIENT_ERROR_CODES set", () => {
    const nonTransient = ["UNKNOWN_COMMAND", "E_EXECUTION_TIMEOUT", "BRIDGE_CONNECTION_FAILED", "JOB_NOT_FOUND"];
    for (const code of nonTransient) {
      assert.ok(!TRANSIENT_ERROR_CODES.has(code as string), `Non-transient code ${code} incorrectly in TRANSIENT_ERROR_CODES`);
    }
  });

  it("includes target/runtime error codes", () => {
    assert.equal(ERROR_CODES.E_TARGET_BUSY, "E_TARGET_BUSY");
    assert.equal(ERROR_CODES.E_RUNTIME_SESSION_STALE, "E_RUNTIME_SESSION_STALE");
    assert.equal(ERROR_CODES.E_RUNTIME_NOT_READY, "E_RUNTIME_NOT_READY");
    assert.equal(ERROR_CODES.E_RUNTIME_TIMEOUT, "E_RUNTIME_TIMEOUT");
    assert.equal(TRANSIENT_ERROR_CODES.has(ERROR_CODES.E_TARGET_BUSY), false);
    assert.equal(TRANSIENT_ERROR_CODES.has(ERROR_CODES.E_RUNTIME_SESSION_STALE), false);
    assert.equal(TRANSIENT_ERROR_CODES.has(ERROR_CODES.E_RUNTIME_NOT_READY), true);
  });
});

// ─── Timeout Defaults ───

describe("timeout defaults and clamping", () => {
  it("clampTimeout returns default when no value provided", () => {
    assert.equal(clampTimeout(undefined, "bridge_read"), 10000);
    assert.equal(clampTimeout(undefined, "bridge_write"), 30000);
    assert.equal(clampTimeout(0, "bridge_read"), 10000);
  });

  it("clampTimeout clamps to maximum", () => {
    assert.equal(clampTimeout(999999, "bridge_read"), 60000);
    assert.equal(clampTimeout(999999, "code_live"), 600000);
  });

  it("clampTimeout returns requested value when within range", () => {
    assert.equal(clampTimeout(15000, "bridge_read"), 15000);
    assert.equal(clampTimeout(50000, "bridge_write"), 50000);
  });

  it("DEFAULT_TIMEOUT_MS has entries for all categories", () => {
    for (const category of ["bridge_read", "bridge_write", "bridge_long", "code_live", "code_batch", "job_wait"]) {
      assert.ok(DEFAULT_TIMEOUT_MS[category] > 0, `Missing default timeout for ${category}`);
      assert.ok(MAX_TIMEOUT_MS[category] > 0, `Missing max timeout for ${category}`);
      assert.ok(MAX_TIMEOUT_MS[category] >= DEFAULT_TIMEOUT_MS[category], `Max must be >= default for ${category}`);
    }
  });
});

// ─── DurationMs ───

describe("durationMsFrom", () => {
  it("computes elapsed time from a start timestamp", () => {
    const startMs = Date.now() - 500;
    const elapsed = durationMsFrom(startMs);
    // Allow small variance for test execution
    assert.ok(elapsed >= 490 && elapsed <= 600, `Expected ~500ms, got ${elapsed}`);
  });

  it("returns 0 for same timestamp", () => {
    const now = Date.now();
    assert.equal(durationMsFrom(now), 0);
  });
});

// ─── Envelope Serialization ───

describe("envelope serialization round-trip", () => {
  it("success envelope can be serialized to JSON and back", () => {
    const requestId = createRequestId();
    const result = success(requestId, { tools: ["scene.hierarchy", "camera.list"] }, {
      warnings: ["no custom tools found"],
      logsRef: "/var/data/logs/req_123.log",
      durationMs: 42,
    });
    const json = JSON.stringify(result);
    const parsed = JSON.parse(json) as CliResult;
    assert.equal(parsed.ok, true);
    assert.equal(parsed.requestId, requestId);
    assert.deepEqual(parsed.data, { tools: ["scene.hierarchy", "camera.list"] });
    assert.deepEqual(parsed.warnings, ["no custom tools found"]);
    assert.equal(parsed.logsRef, "/var/data/logs/req_123.log");
    assert.equal(parsed.durationMs, 42);
  });

  it("error envelope can be serialized to JSON and back", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_UNITY_COMPILING", "Unity is compiling.", ["Wait and retry."], { jobId: "job_xyz" }, { durationMs: 100 });
    const json = JSON.stringify(result);
    const parsed = JSON.parse(json) as CliResult;
    assert.equal(parsed.ok, false);
    assert.equal(parsed.error?.code, "E_UNITY_COMPILING");
    assert.equal(parsed.error?.message, "Unity is compiling.");
    assert.equal(parsed.error?.transient, true);
    assert.equal(parsed.error?.retryAfterMs, 5000);
    assert.equal(parsed.durationMs, 100);
  });

  it("timeout error envelope round-trip preserves details", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_EXECUTION_TIMEOUT", "Job timed out.", ["Check job status."], {
      jobId: "job_abc",
      timeoutMs: 30000,
      cancelAttempted: true,
      cancelled: false,
      processKilled: true,
    });
    const json = JSON.stringify(result);
    const parsed = JSON.parse(json) as CliResult;
    assert.equal(parsed.ok, false);
    assert.equal(parsed.error?.code, "E_EXECUTION_TIMEOUT");
    // timeout is NOT transient
    assert.equal(parsed.error?.transient, undefined);
    // details are flattened
    assert.equal(parsed.error?.jobId, "job_abc");
    assert.equal(parsed.error?.timeoutMs, 30000);
  });
});

// ─── Retry Mechanism ───

describe("withRetry", () => {
  it("returns result immediately on success (no retry)", async () => {
    let calls = 0;
    const result = await withRetry(async () => {
      calls += 1;
      return success("req_1", { ok: true });
    }, { maxRetries: 3 });
    assert.equal(calls, 1);
    assert.equal(result.ok, true);
  });

  it("returns result immediately on non-transient error (no retry)", async () => {
    let calls = 0;
    const result = await withRetry(async () => {
      calls += 1;
      return failure("req_1", "UNKNOWN_COMMAND", "Bad command.");
    }, { maxRetries: 3 });
    assert.equal(calls, 1);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "UNKNOWN_COMMAND");
  });

  it("retries on transient error and succeeds on second attempt", async () => {
    let calls = 0;
    const result = await withRetry(async () => {
      calls += 1;
      if (calls === 1) {
        return failure("req_1", "E_UNITY_COMPILING", "Unity is compiling.", ["Wait and retry."]);
      }
      return success("req_1", { status: "healthy" });
    }, { maxRetries: 3 });
    assert.equal(calls, 2);
    assert.equal(result.ok, true);
  });

  it("retries transient errors up to maxRetries then returns last error", async () => {
    let calls = 0;
    const result = await withRetry(async () => {
      calls += 1;
      return failure("req_1", "E_UNITY_COMPILING", "Still compiling.");
    }, { maxRetries: 2 });
    assert.equal(calls, 3); // 1 initial + 2 retries
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "E_UNITY_COMPILING");
  });

  it("honors retry_budget_ms and stops retrying when exhausted", async () => {
    let calls = 0;
    const startMs = Date.now();
    const result = await withRetry(async () => {
      calls += 1;
      return failure("req_1", "E_UNITY_COMPILING", "Still compiling.");
    }, { maxRetries: 10, retryBudgetMs: 100 });
    // Should stop after budget is exhausted (at least 1 attempt, no more than a few retries)
    assert.ok(calls <= 5, `Too many calls: ${calls}`);
    assert.equal(result.ok, false);
  });

  it("invokes onRetry callback before each retry", async () => {
    const retryLog: Array<{ attempt: number; code: string; retryAfterMs: number }> = [];
    let calls = 0;
    const result = await withRetry(async () => {
      calls += 1;
      if (calls <= 2) {
        return failure("req_1", "E_UNITY_COMPILING", "Compiling.");
      }
      return success("req_1", { status: "healthy" });
    }, {
      maxRetries: 5,
      onRetry: (attempt: number, error: CliResult, retryAfterMs: number) => {
        retryLog.push({ attempt, code: error.error?.code ?? "", retryAfterMs });
      },
    });
    assert.equal(result.ok, true);
    assert.equal(retryLog.length, 2);
    assert.equal(retryLog[0].code, "E_UNITY_COMPILING");
    assert.equal(retryLog[0].attempt, 1);
    assert.equal(retryLog[1].attempt, 2);
  });

  it("does not retry E_EXECUTION_TIMEOUT (non-transient)", async () => {
    let calls = 0;
    const result = await withRetry(async () => {
      calls += 1;
      return failure("req_1", "E_EXECUTION_TIMEOUT", "Timed out.");
    }, { maxRetries: 3 });
    assert.equal(calls, 1);
    assert.equal(result.error?.code, "E_EXECUTION_TIMEOUT");
  });
});

// ─── Transient Retry Info ───

describe("transientRetryInfo", () => {
  it("returns isTransient=true for transient error results", () => {
    const result = failure("req_1", "E_UNITY_COMPILING", "Compiling.");
    const info = transientRetryInfo(result);
    assert.equal(info.isTransient, true);
    if (info.isTransient) {
      assert.equal(info.code, "E_UNITY_COMPILING");
      assert.equal(info.retryAfterMs, 5000);
    }
  });

  it("returns isTransient=false for success results", () => {
    const result = success("req_1", { ok: true });
    const info = transientRetryInfo(result);
    assert.equal(info.isTransient, false);
  });

  it("returns isTransient=false for non-transient error results", () => {
    const result = failure("req_1", "UNKNOWN_COMMAND", "Bad command.");
    const info = transientRetryInfo(result);
    assert.equal(info.isTransient, false);
  });

  it("uses retryAfterMs from error if present", () => {
    const error: CliError = {
      code: "E_UNITY_COMPILING",
      message: "Compiling.",
      requestId: "req_1",
      transient: true,
      retryAfterMs: 8000,
    };
    const result: CliResult = { ok: false, requestId: "req_1", error };
    const info = transientRetryInfo(result);
    assert.equal(info.isTransient, true);
    if (info.isTransient) {
      assert.equal(info.retryAfterMs, 8000);
    }
  });
});

// ─── isTransientError ───

describe("isTransientError", () => {
  it("returns true for all known transient codes", () => {
    assert.equal(isTransientError("E_UNITY_BUSY"), true);
    assert.equal(isTransientError("E_UNITY_COMPILING"), true);
    assert.equal(isTransientError("E_UNITY_IMPORTING"), true);
    assert.equal(isTransientError("E_UNITY_DOMAIN_RELOAD"), true);
    assert.equal(isTransientError("E_UNITY_PLAY_MODE"), true);
  });

  it("returns false for non-transient codes", () => {
    assert.equal(isTransientError("UNKNOWN_COMMAND"), false);
    assert.equal(isTransientError("E_EXECUTION_TIMEOUT"), false);
    assert.equal(isTransientError("BRIDGE_CONNECTION_FAILED"), false);
    assert.equal(isTransientError("JOB_NOT_FOUND"), false);
  });
});

// ─── DEFAULT_RETRY_AFTER_MS ───

describe("DEFAULT_RETRY_AFTER_MS", () => {
  it("has entries for all transient error codes", () => {
    for (const code of TRANSIENT_ERROR_CODES) {
      assert.ok(DEFAULT_RETRY_AFTER_MS[code] !== undefined, `Missing DEFAULT_RETRY_AFTER_MS for ${code}`);
      assert.ok(DEFAULT_RETRY_AFTER_MS[code] > 0, `DEFAULT_RETRY_AFTER_MS for ${code} must be > 0`);
    }
  });
});
// ─── Runtime Error Codes ───

describe("E_RUNTIME_NOT_READY is transient with retryAfterMs=3000", () => {
  it("E_RUNTIME_NOT_READY is in TRANSIENT_ERROR_CODES", () => {
    assert.ok(TRANSIENT_ERROR_CODES.has(ERROR_CODES.E_RUNTIME_NOT_READY), "E_RUNTIME_NOT_READY should be transient");
  });

  it("E_RUNTIME_NOT_READY failure envelope has transient=true and retryAfterMs=3000", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_RUNTIME_NOT_READY", "Runtime is not ready (Play Mode not active).", ["Wait for Play Mode to become active and retry."]);
    assert.equal(result.ok, false);
    assert.equal(result.error?.transient, true);
    assert.equal(result.error?.retryAfterMs, 3000);
    assert.equal(result.error?.code, "E_RUNTIME_NOT_READY");
  });

  it("E_RUNTIME_NOT_READY is retried by withRetry", async () => {
    let calls = 0;
    const result = await withRetry(async () => {
      calls += 1;
      if (calls === 1) {
        return failure("req_1", "E_RUNTIME_NOT_READY", "Runtime not ready.");
      }
      return success("req_1", { status: "ready" });
    }, { maxRetries: 3 });
    assert.equal(calls, 2);
    assert.equal(result.ok, true);
  });

  it("DEFAULT_RETRY_AFTER_MS has E_RUNTIME_NOT_READY=3000", () => {
    assert.equal(DEFAULT_RETRY_AFTER_MS[ERROR_CODES.E_RUNTIME_NOT_READY], 3000);
  });
});

describe("E_RUNTIME_TIMEOUT is non-transient", () => {
  it("E_RUNTIME_TIMEOUT is NOT in TRANSIENT_ERROR_CODES", () => {
    assert.equal(TRANSIENT_ERROR_CODES.has(ERROR_CODES.E_RUNTIME_TIMEOUT), false);
  });

  it("E_RUNTIME_TIMEOUT failure envelope has transient=undefined", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_RUNTIME_TIMEOUT", "Runtime tool execution timed out.", ["Increase --timeout-ms or check runtime session state."]);
    assert.equal(result.ok, false);
    assert.equal(result.error?.transient, undefined);
    assert.equal(result.error?.retryAfterMs, undefined);
    assert.equal(result.error?.code, "E_RUNTIME_TIMEOUT");
  });

  it("E_RUNTIME_TIMEOUT is not retried by withRetry", async () => {
    let calls = 0;
    const result = await withRetry(async () => {
      calls += 1;
      return failure("req_1", "E_RUNTIME_TIMEOUT", "Timed out.");
    }, { maxRetries: 3 });
    assert.equal(calls, 1);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "E_RUNTIME_TIMEOUT");
  });
});

describe("E_RUNTIME_TOOL_TARGET_MISMATCH is non-transient", () => {
  it("E_RUNTIME_TOOL_TARGET_MISMATCH is NOT in TRANSIENT_ERROR_CODES", () => {
    assert.equal(TRANSIENT_ERROR_CODES.has(ERROR_CODES.E_RUNTIME_TOOL_TARGET_MISMATCH), false);
  });

  it("E_RUNTIME_TOOL_TARGET_MISMATCH failure envelope has transient=undefined", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_RUNTIME_TOOL_TARGET_MISMATCH", "Tool target mismatch: runtime tool called with editor target.", ["Use --runtime flag or ensure the tool's target matches the execution context."]);
    assert.equal(result.ok, false);
    assert.equal(result.error?.transient, undefined);
    assert.equal(result.error?.retryAfterMs, undefined);
    assert.equal(result.error?.code, "E_RUNTIME_TOOL_TARGET_MISMATCH");
  });

  it("E_RUNTIME_TOOL_TARGET_MISMATCH is not retried by withRetry", async () => {
    let calls = 0;
    const result = await withRetry(async () => {
      calls += 1;
      return failure("req_1", "E_RUNTIME_TOOL_TARGET_MISMATCH", "Target mismatch.");
    }, { maxRetries: 3 });
    assert.equal(calls, 1);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "E_RUNTIME_TOOL_TARGET_MISMATCH");
  });
});
