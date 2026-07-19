/**
 * Tests for the unified public envelope module (output.ts).
 * Covers:
 * - Envelope serialization/deserialization (success and error)
 * - Error code taxonomy validation
 * - Transient error detection and retryAfterMs
 * - Duration tracking
 * - Timeout clamping
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
    assert.ok(result.ok);
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
    assert.ok(result.ok);
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
    assert.ok(!result.ok);
    assert.equal(result.requestId, requestId);
    assert.equal(result.error?.code, "UNKNOWN_COMMAND");
    assert.equal(result.error?.message, "Unknown command: foo");
    assert.deepEqual(result.error?.nextActions, ["Run unity_cli --help."]);
  });

  it("transient errors are marked with transient=true and retryAfterMs", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_UNITY_COMPILING", "Unity Editor is compiling scripts.", ["Wait for Unity to finish compiling and retry."]);
    assert.ok(!result.ok);
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

  it("non-transient errors have transient=false and retryAfterMs=0", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "UNKNOWN_COMMAND", "Unknown command.");
    assert.equal(result.error.transient, false);
    assert.equal(result.error.retryAfterMs, 0);
  });

  it("timeout error E_EXECUTION_TIMEOUT is NOT transient", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_EXECUTION_TIMEOUT", "Execution timed out.", ["Increase --timeout-ms."]);
    assert.equal(result.error.transient, false);
    assert.equal(result.error.retryAfterMs, 0);
  });

  it("keeps details nested and nextActions present", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_EXECUTION_TIMEOUT", "Timed out.", [], {
      jobId: "job_123",
      timeoutMs: 30000,
      bridgeEndpoint: "http://127.0.0.1:47392",
    });
    assert.deepEqual(result.error.details, {
      jobId: "job_123",
      timeoutMs: 30000,
      bridgeEndpoint: "http://127.0.0.1:47392",
    });
    assert.deepEqual(result.error.nextActions, []);
    assert.equal("jobId" in result.error, false);
    assert.equal("requestId" in result.error, false);
  });

  it("golden error envelope has only protocol fields", () => {
    const result = failure("req_golden", "UNKNOWN_COMMAND", "Unknown command.", [], { path: "tools run" });
    assert.deepEqual(JSON.parse(JSON.stringify(result)), {
      ok: false,
      requestId: "req_golden",
      error: {
        code: "UNKNOWN_COMMAND",
        message: "Unknown command.",
        nextActions: [],
        details: { path: "tools run" },
        transient: false,
        retryAfterMs: 0,
      },
    });
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

  it("includes runtime error codes", () => {
    assert.equal(ERROR_CODES.E_RUNTIME_SESSION_STALE, "E_RUNTIME_SESSION_STALE");
    assert.equal(ERROR_CODES.E_RUNTIME_NOT_READY, "E_RUNTIME_NOT_READY");
    assert.equal(ERROR_CODES.E_RUNTIME_TIMEOUT, "E_RUNTIME_TIMEOUT");
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
    assert.ok(parsed.ok);
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
    assert.ok(!parsed.ok);
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
    assert.ok(!parsed.ok);
    assert.equal(parsed.error?.code, "E_EXECUTION_TIMEOUT");
    // timeout is NOT transient
    assert.equal(parsed.error?.transient, false);
    assert.equal(parsed.error?.retryAfterMs, 0);
    assert.deepEqual(parsed.error?.details, {
      jobId: "job_abc",
      timeoutMs: 30000,
      cancelAttempted: true,
      cancelled: false,
      processKilled: true,
    });
    assert.deepEqual(parsed.error?.nextActions, ["Check job status."]);
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
    assert.ok(!result.ok);
    assert.equal(result.error?.transient, true);
    assert.equal(result.error?.retryAfterMs, 3000);
    assert.equal(result.error?.code, "E_RUNTIME_NOT_READY");
  });

  it("DEFAULT_RETRY_AFTER_MS has E_RUNTIME_NOT_READY=3000", () => {
    assert.equal(DEFAULT_RETRY_AFTER_MS[ERROR_CODES.E_RUNTIME_NOT_READY], 3000);
  });
});

describe("E_RUNTIME_TIMEOUT is non-transient", () => {
  it("E_RUNTIME_TIMEOUT is NOT in TRANSIENT_ERROR_CODES", () => {
    assert.equal(TRANSIENT_ERROR_CODES.has(ERROR_CODES.E_RUNTIME_TIMEOUT), false);
  });

  it("E_RUNTIME_TIMEOUT failure envelope has explicit non-transient retry fields", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_RUNTIME_TIMEOUT", "Runtime tool execution timed out.", ["Increase --timeout-ms or check runtime session state."]);
    assert.ok(!result.ok);
    assert.equal(result.error?.transient, false);
    assert.equal(result.error?.retryAfterMs, 0);
    assert.equal(result.error?.code, "E_RUNTIME_TIMEOUT");
  });

});

describe("E_RUNTIME_TOOL_TARGET_MISMATCH is non-transient", () => {
  it("E_RUNTIME_TOOL_TARGET_MISMATCH is NOT in TRANSIENT_ERROR_CODES", () => {
    assert.equal(TRANSIENT_ERROR_CODES.has(ERROR_CODES.E_RUNTIME_TOOL_TARGET_MISMATCH), false);
  });

  it("E_RUNTIME_TOOL_TARGET_MISMATCH failure envelope has explicit non-transient retry fields", () => {
    const requestId = createRequestId();
    const result = failure(requestId, "E_RUNTIME_TOOL_TARGET_MISMATCH", "Tool target mismatch: runtime tool called with editor target.", ["Use --runtime flag or ensure the tool's target matches the execution context."]);
    assert.ok(!result.ok);
    assert.equal(result.error?.transient, false);
    assert.equal(result.error?.retryAfterMs, 0);
    assert.equal(result.error?.code, "E_RUNTIME_TOOL_TARGET_MISMATCH");
  });

});
