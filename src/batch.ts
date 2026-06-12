import { randomUUID } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import type { CliResult } from "./types.js";

export interface BatchRuntime {
  dataDir: string;
}

export interface BatchRunOptions {
  filePath: string;
  requestId: string;
  runtime: BatchRuntime;
  failFast?: boolean;
  parallel?: number;
  dryRun?: boolean;
  executeStep: (argv: string[]) => Promise<CliResult>;
}

type BatchEnv = Record<string, string | number | boolean>;

interface BatchDefaults {
  project?: string;
  unity?: string;
  cwd?: string;
  wait?: boolean;
  timeoutMs?: number;
  waitTimeoutMs?: number;
  env?: BatchEnv;
}

interface BatchStepDefinition {
  id: string;
  command: string[];
  args?: string[];
  dependsOn?: string[];
  timeoutMs?: number;
  waitTimeoutMs?: number;
  env?: BatchEnv;
}

interface BatchDefinition {
  defaults?: BatchDefaults;
  failFast?: boolean;
  parallel?: number;
  envAllowlist?: string[];
  steps: BatchStepDefinition[];
}

interface NormalizedStep {
  id: string;
  argv: string[];
  dependsOn: string[];
  cwd: string;
  env: Record<string, string>;
  commandKey: string;
  parallelSafe: boolean;
}

interface BatchDiagnostic {
  level: "info" | "error";
  code: string;
  message: string;
  stepId?: string;
  command?: string;
}

interface StepEnvelope {
  id: string;
  status: "pending" | "running" | "succeeded" | "failed" | "skipped";
  command: string[];
  requestId: string;
  startedAt?: string;
  finishedAt?: string;
  durationMs?: number;
  ok?: boolean;
  envelope?: CliResult;
  jobId?: string;
  logPath?: string;
  plannedEffects?: unknown;
  error?: {
    code: string;
    message: string;
  };
}

const commandRegistry = new Map<string, { parallelSafe: boolean }>([
  ["job status", { parallelSafe: true }],
  ["job logs", { parallelSafe: true }],
  ["job cancel", { parallelSafe: false }],
  ["scene active", { parallelSafe: true }],
  ["scene hierarchy", { parallelSafe: true }],
  ["scene build-settings", { parallelSafe: true }],
  ["scene screenshot", { parallelSafe: false }],
  ["camera list", { parallelSafe: true }],
  ["camera info", { parallelSafe: true }],
  ["camera screenshot", { parallelSafe: false }],
  ["scene create", { parallelSafe: false }],
  ["scene load", { parallelSafe: false }],
  ["scene save", { parallelSafe: false }],
  ["go find", { parallelSafe: true }],
  ["go inspect", { parallelSafe: true }],
  ["go create", { parallelSafe: false }],
  ["go modify", { parallelSafe: false }],
  ["go delete", { parallelSafe: false }],
  ["component add", { parallelSafe: false }],
  ["component remove", { parallelSafe: false }],
  ["component set", { parallelSafe: false }],
  ["asset search", { parallelSafe: true }],
  ["asset info", { parallelSafe: true }],
  ["asset mkdir", { parallelSafe: false }],
  ["asset import", { parallelSafe: false }],
  ["asset move", { parallelSafe: false }],
  ["asset delete", { parallelSafe: false }],
  ["prefab info", { parallelSafe: true }],
  ["prefab create", { parallelSafe: false }],
  ["prefab stage-open", { parallelSafe: false }],
  ["prefab stage-save", { parallelSafe: false }],
  ["prefab stage-close", { parallelSafe: false }],
  ["prefab hierarchy", { parallelSafe: true }],
  ["material info", { parallelSafe: true }],
  ["material create", { parallelSafe: false }],
  ["material assign", { parallelSafe: false }],
  ["material set", { parallelSafe: false }],
  ["graphics quality", { parallelSafe: false }],
  ["graphics settings", { parallelSafe: false }],
  ["graphics screenshot", { parallelSafe: false }],
  ["physics settings", { parallelSafe: false }],
  ["physics raycast", { parallelSafe: true }],
  ["physics simulation", { parallelSafe: false }],
  ["ui canvas/create", { parallelSafe: false }],
  ["ui text/create", { parallelSafe: false }],
  ["ui button/create", { parallelSafe: false }],
  ["ui image/create", { parallelSafe: false }],
  ["ui document/info", { parallelSafe: true }],
  ["ui document/create", { parallelSafe: false }],
  ["ui hierarchy", { parallelSafe: true }],
  ["script create", { parallelSafe: false }],
  ["script read", { parallelSafe: true }],
  ["script edit", { parallelSafe: false }],
  ["script validate", { parallelSafe: true }],
  ["diagnose run", { parallelSafe: true }],
  ["preflight run", { parallelSafe: true }],
  ["editor console", { parallelSafe: true }],
  ["editor refresh", { parallelSafe: false }],
  ["screenshot capture", { parallelSafe: false }],
  ["test run", { parallelSafe: false }],
  ["test poll", { parallelSafe: true }],
  ["build run", { parallelSafe: false }],
  ["build status", { parallelSafe: true }],
  ["build cancel", { parallelSafe: false }]
]);

export function batchJsonSchema(): Record<string, unknown> {
  return {
    $schema: "https://json-schema.org/draft/2020-12/schema",
    title: "Alps Unity CLI batch file",
    type: "object",
    additionalProperties: false,
    required: ["steps"],
    properties: {
      defaults: {
        type: "object",
        additionalProperties: false,
        properties: {
          project: { type: "string" },
          unity: { type: "string" },
          cwd: { type: "string" },
          wait: { type: "boolean" },
          timeoutMs: { type: "integer", minimum: 1 },
          waitTimeoutMs: { type: "integer", minimum: 1 },
          env: { type: "object", additionalProperties: { type: ["string", "number", "boolean"] } }
        }
      },
      failFast: { type: "boolean", default: true },
      parallel: { type: "integer", minimum: 1, default: 1 },
      envAllowlist: { type: "array", items: { type: "string", pattern: "^[A-Z_][A-Z0-9_]*$" }, uniqueItems: true },
      steps: {
        type: "array",
        minItems: 1,
        items: {
          type: "object",
          additionalProperties: false,
          required: ["id", "command"],
          properties: {
            id: { type: "string", pattern: "^[A-Za-z0-9_.-]+$" },
            command: { type: "array", minItems: 2, items: { type: "string" } },
            args: { type: "array", items: { type: "string" } },
            dependsOn: { type: "array", items: { type: "string" }, uniqueItems: true },
            timeoutMs: { type: "integer", minimum: 1 },
            waitTimeoutMs: { type: "integer", minimum: 1 },
            env: { type: "object", additionalProperties: { type: ["string", "number", "boolean"] } }
          }
        }
      }
    }
  };
}

export async function runBatch(options: BatchRunOptions): Promise<CliResult> {
  const batchPath = resolve(options.filePath);
  const loaded = await loadBatch(batchPath);
  if (!loaded.ok) {
    return batchFailure(options.requestId, loaded.code, loaded.message, {
      batchRequestId: options.requestId,
      preflight: [{ level: "error", code: loaded.code, message: loaded.message }]
    });
  }

  const effectiveFailFast = options.failFast ?? loaded.batch.failFast ?? true;
  const effectiveParallel = options.parallel ?? loaded.batch.parallel ?? 1;
  const normalized = normalizeBatch(loaded.batch, batchPath);
  const preflight = validateBatch(loaded.batch, normalized, effectiveParallel);
  const preflightOk = preflight.every((item) => item.level !== "error");
  const startedAt = new Date().toISOString();

  if (!preflightOk || options.dryRun) {
    const summary = {
      batchRequestId: options.requestId,
      batchPath,
      mode: options.dryRun ? "preflight" : "preflight_failed",
      ok: preflightOk,
      failFast: effectiveFailFast,
      parallel: effectiveParallel,
      preflight,
      steps: normalized.steps.map((step) => ({ id: step.id, command: step.argv, dependsOn: step.dependsOn, cwd: step.cwd, parallelSafe: step.parallelSafe }))
    };
    return preflightOk ? { ok: true, requestId: options.requestId, data: summary } : batchFailure(options.requestId, "BATCH_PREFLIGHT_FAILED", "Batch preflight failed.", summary);
  }

  const stepEnvelopes = new Map<string, StepEnvelope>();
  for (const step of normalized.steps) {
    stepEnvelopes.set(step.id, { id: step.id, status: "pending", command: step.argv, requestId: `step_${randomUUID()}` });
  }

  await executeSchedule({ steps: normalized.steps, stepEnvelopes, parallel: effectiveParallel, failFast: effectiveFailFast, executeStep: options.executeStep });

  const finishedAt = new Date().toISOString();
  const steps = normalized.steps.map((step) => stepEnvelopes.get(step.id)!);
  const counts = countSteps(steps);
  const ok = counts.failed === 0 && counts.skipped === 0;
  const batchRecordPath = resolve(options.runtime.dataDir, "batches", `${options.requestId}.json`);
  const logIndex = {
    batchRequestId: options.requestId,
    batchRecordPath,
    steps: steps.map((step) => ({ id: step.id, stepRequestId: step.requestId, jobId: step.jobId, logPath: step.logPath }))
  };
  const data = {
    batchRequestId: options.requestId,
    batchPath,
    startedAt,
    finishedAt,
    durationMs: Date.parse(finishedAt) - Date.parse(startedAt),
    ok,
    exitCode: ok ? 0 : 1,
    failFast: effectiveFailFast,
    parallel: effectiveParallel,
    preflight,
    summary: counts,
    plannedEffects: steps.flatMap((step) => (Array.isArray(step.plannedEffects) ? step.plannedEffects : [])),
    logIndex,
    steps
  };

  await writeBatchRecord(batchRecordPath, data);
  return ok ? { ok: true, requestId: options.requestId, data } : batchFailure(options.requestId, "BATCH_FAILED", "One or more batch steps failed or were skipped.", data);
}

async function loadBatch(batchPath: string): Promise<{ ok: true; batch: BatchDefinition } | { ok: false; code: string; message: string }> {
  try {
    const raw = await readFile(batchPath, "utf8");
    const parsed = JSON.parse(raw) as unknown;
    if (!isRecord(parsed)) {
      return { ok: false, code: "BATCH_SCHEMA_INVALID", message: "Batch file must contain a JSON object." };
    }
    return { ok: true, batch: parsed as unknown as BatchDefinition };
  } catch (error) {
    const nodeError = error as NodeJS.ErrnoException;
    return {
      ok: false,
      code: nodeError.code === "ENOENT" ? "BATCH_FILE_NOT_FOUND" : "BATCH_FILE_INVALID",
      message: error instanceof Error ? error.message : String(error)
    };
  }
}

function normalizeBatch(batch: BatchDefinition, batchPath: string): { steps: NormalizedStep[]; diagnostics: BatchDiagnostic[] } {
  const diagnostics: BatchDiagnostic[] = [];
  if (!Array.isArray(batch.steps)) {
    return { steps: [], diagnostics: [{ level: "error", code: "BATCH_STEPS_REQUIRED", message: "Batch file requires a non-empty steps array." }] };
  }

  const batchDir = dirname(batchPath);
  const defaults = batch.defaults ?? {};
  const defaultCwd = defaults.cwd ? resolve(batchDir, defaults.cwd) : batchDir;
  const defaultEnv = stringifyEnv(defaults.env ?? {});
  const steps = batch.steps.map((step) => {
    const rawCommand = Array.isArray(step.command) ? step.command : [];
    const rawArgs = Array.isArray(step.args) ? step.args : [];
    const commandKey = rawCommand.slice(0, 2).join(" ");
    const registryItem = commandRegistry.get(commandKey);
    const argv = applyDefaults([...rawCommand, ...rawArgs], defaults, step, defaultCwd);
    return {
      id: typeof step.id === "string" ? step.id : "",
      argv,
      dependsOn: Array.isArray(step.dependsOn) ? step.dependsOn.filter((item): item is string => typeof item === "string") : [],
      cwd: defaultCwd,
      env: { ...defaultEnv, ...stringifyEnv(step.env ?? {}) },
      commandKey,
      parallelSafe: registryItem?.parallelSafe ?? false
    };
  });

  return { steps, diagnostics };
}

function validateBatch(batch: BatchDefinition, normalized: { steps: NormalizedStep[]; diagnostics: BatchDiagnostic[] }, parallel: number): BatchDiagnostic[] {
  const diagnostics: BatchDiagnostic[] = [...normalized.diagnostics];
  if (!Array.isArray(batch.steps) || batch.steps.length === 0) {
    diagnostics.push({ level: "error", code: "BATCH_STEPS_REQUIRED", message: "Batch file requires at least one step." });
    return diagnostics;
  }

  if (!Number.isInteger(parallel) || parallel < 1) {
    diagnostics.push({ level: "error", code: "BATCH_PARALLEL_INVALID", message: "Batch parallel must be an integer >= 1." });
  }

  const allowlist = new Set(Array.isArray(batch.envAllowlist) ? batch.envAllowlist : []);
  const ids = new Set<string>();
  for (const [index, step] of normalized.steps.entries()) {
    if (!step.id) {
      diagnostics.push({ level: "error", code: "BATCH_STEP_ID_REQUIRED", message: `Step at index ${index} requires an id.` });
      continue;
    }
    if (ids.has(step.id)) {
      diagnostics.push({ level: "error", code: "BATCH_STEP_ID_DUPLICATE", message: `Duplicate step id: ${step.id}.`, stepId: step.id });
    }
    ids.add(step.id);

    if (!commandRegistry.has(step.commandKey)) {
      diagnostics.push({
        level: "error",
        code: "BATCH_COMMAND_NOT_ALLOWED",
        message: `Batch step must call a registered Alps Unity CLI command, got: ${step.commandKey || step.argv.join(" ")}.`,
        stepId: step.id,
        command: step.commandKey
      });
    }

    if (step.argv[0] === "raw" || step.argv.includes("raw")) {
      diagnostics.push({ level: "error", code: "BATCH_RAW_COMMAND_FORBIDDEN", message: "Batch does not provide raw MCP or raw tool escape hatches.", stepId: step.id, command: step.argv.join(" ") });
    }

    if (parallel > 1 && !step.parallelSafe) {
      diagnostics.push({ level: "error", code: "BATCH_PARALLEL_CONFLICT", message: `Command is not declared parallel-safe: ${step.commandKey}.`, stepId: step.id, command: step.commandKey });
    }

    if (parallel > 1 && Object.keys(step.env).length > 0) {
      diagnostics.push({ level: "error", code: "BATCH_ENV_PARALLEL_UNSAFE", message: "Step env overrides are process-wide and require serial batch execution.", stepId: step.id });
    }

    if (parallel > 1 && batch.defaults?.cwd) {
      diagnostics.push({ level: "error", code: "BATCH_CWD_PARALLEL_UNSAFE", message: "Batch cwd is process-wide and requires serial batch execution.", stepId: step.id });
    }

    for (const key of Object.keys(step.env)) {
      if (!allowlist.has(key)) {
        diagnostics.push({ level: "error", code: "BATCH_ENV_NOT_ALLOWED", message: `Environment variable is not in envAllowlist: ${key}.`, stepId: step.id });
      }
    }
  }

  for (const step of normalized.steps) {
    for (const dependency of step.dependsOn) {
      if (!ids.has(dependency)) {
        diagnostics.push({ level: "error", code: "BATCH_DEPENDENCY_UNKNOWN", message: `Step ${step.id} depends on unknown step ${dependency}.`, stepId: step.id });
      }
      if (dependency === step.id) {
        diagnostics.push({ level: "error", code: "BATCH_DEPENDENCY_CYCLE", message: `Step ${step.id} cannot depend on itself.`, stepId: step.id });
      }
    }
  }

  for (const cycle of findCycles(normalized.steps)) {
    diagnostics.push({ level: "error", code: "BATCH_DEPENDENCY_CYCLE", message: `Batch dependency cycle detected: ${cycle.join(" -> ")}.` });
  }

  if (diagnostics.length === 0) {
    diagnostics.push({ level: "info", code: "BATCH_PREFLIGHT_OK", message: "Batch schema, dependencies, command allowlist, env allowlist and parallel policy passed." });
  }
  return diagnostics;
}

async function executeSchedule(options: {
  steps: NormalizedStep[];
  stepEnvelopes: Map<string, StepEnvelope>;
  parallel: number;
  failFast: boolean;
  executeStep: (argv: string[]) => Promise<CliResult>;
}): Promise<void> {
  const completed = new Set<string>();
  const failed = new Set<string>();
  const skipped = new Set<string>();
  const running = new Set<string>();

  while (completed.size + skipped.size < options.steps.length) {
    const pending = options.steps.filter((step) => !completed.has(step.id) && !skipped.has(step.id) && !running.has(step.id));
    let progressed = false;

    for (const step of pending) {
      const failedDependency = step.dependsOn.find((dependency) => failed.has(dependency) || skipped.has(dependency));
      if (failedDependency) {
        markSkipped(options.stepEnvelopes.get(step.id)!, "BATCH_DEPENDENCY_FAILED", `Dependency did not succeed: ${failedDependency}.`);
        skipped.add(step.id);
        progressed = true;
      }
    }

    if (options.failFast && failed.size > 0) {
      for (const step of pending) {
        if (!completed.has(step.id) && !skipped.has(step.id)) {
          markSkipped(options.stepEnvelopes.get(step.id)!, "BATCH_FAIL_FAST", "Skipped because failFast stopped the batch after a failed step.");
          skipped.add(step.id);
          progressed = true;
        }
      }
      continue;
    }

    const ready = options.steps.filter(
      (step) => !completed.has(step.id) && !skipped.has(step.id) && !running.has(step.id) && step.dependsOn.every((dependency) => completed.has(dependency))
    );

    if (ready.length === 0) {
      if (!progressed) {
        for (const step of pending) {
          markSkipped(options.stepEnvelopes.get(step.id)!, "BATCH_DEPENDENCY_UNRESOLVED", "Step dependencies could not be resolved.");
          skipped.add(step.id);
        }
      }
      continue;
    }

    const chunk = ready.slice(0, options.parallel);
    await Promise.all(
      chunk.map(async (step) => {
        running.add(step.id);
        const envelope = options.stepEnvelopes.get(step.id)!;
        await executeOneStep(step, envelope, options.executeStep);
        running.delete(step.id);
        completed.add(step.id);
        if (!envelope.ok) {
          failed.add(step.id);
        }
      })
    );
  }
}

async function executeOneStep(step: NormalizedStep, envelope: StepEnvelope, executeStep: (argv: string[]) => Promise<CliResult>): Promise<void> {
  const previousCwd = process.cwd();
  const previousEnv = new Map<string, string | undefined>();
  const started = Date.now();
  envelope.status = "running";
  envelope.startedAt = new Date(started).toISOString();

  process.chdir(step.cwd);
  for (const [key, value] of Object.entries(step.env)) {
    previousEnv.set(key, process.env[key]);
    process.env[key] = value;
  }

  try {
    const result = await executeStep(step.argv);
    envelope.envelope = result;
    envelope.ok = result.ok;
    envelope.status = result.ok ? "succeeded" : "failed";
    envelope.requestId = result.requestId;
    envelope.jobId = readDataString(result, "jobId");
    envelope.logPath = readDataString(result, "logPath");
    envelope.plannedEffects = readDataValue(result, "plannedEffects");
    if (!result.ok && result.error) {
      envelope.error = { code: result.error.code, message: result.error.message };
    }
  } catch (error) {
    envelope.ok = false;
    envelope.status = "failed";
    envelope.error = { code: "BATCH_STEP_FAILED", message: error instanceof Error ? error.message : String(error) };
  } finally {
    for (const [key, value] of previousEnv.entries()) {
      if (value === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = value;
      }
    }
    process.chdir(previousCwd);
    envelope.finishedAt = new Date().toISOString();
    envelope.durationMs = Date.now() - started;
  }
}

function applyDefaults(argv: string[], defaults: BatchDefaults, step: BatchStepDefinition, cwd: string): string[] {
  const result = [...argv];
  appendFlagIfMissing(result, "project", defaults.project ? resolve(cwd, defaults.project) : undefined);
  appendFlagIfMissing(result, "unity", defaults.unity);
  appendFlagIfMissing(result, "timeout-ms", numberToString(step.timeoutMs ?? defaults.timeoutMs));
  appendFlagIfMissing(result, "wait-timeout-ms", numberToString(step.waitTimeoutMs ?? defaults.waitTimeoutMs));
  if (defaults.wait && !hasFlag(result, "wait")) {
    result.push("--wait");
  }
  return result;
}

function appendFlagIfMissing(argv: string[], name: string, value?: string): void {
  if (!value || hasFlag(argv, name)) {
    return;
  }
  argv.push(`--${name}`, value);
}

function hasFlag(argv: string[], name: string): boolean {
  return argv.some((item) => item === `--${name}` || item.startsWith(`--${name}=`));
}

function numberToString(value: number | undefined): string | undefined {
  return typeof value === "number" && Number.isFinite(value) ? String(value) : undefined;
}

function stringifyEnv(env: BatchEnv): Record<string, string> {
  return Object.fromEntries(Object.entries(env).map(([key, value]) => [key, String(value)]));
}

function markSkipped(envelope: StepEnvelope, code: string, message: string): void {
  envelope.status = "skipped";
  envelope.ok = false;
  envelope.error = { code, message };
  envelope.finishedAt = new Date().toISOString();
}

function countSteps(steps: StepEnvelope[]): Record<string, number> {
  return {
    total: steps.length,
    succeeded: steps.filter((step) => step.status === "succeeded").length,
    failed: steps.filter((step) => step.status === "failed").length,
    skipped: steps.filter((step) => step.status === "skipped").length
  };
}

function batchFailure(requestId: string, code: string, message: string, data: unknown): CliResult {
  return {
    ok: false,
    requestId,
    data,
    error: {
      code,
      message,
      requestId,
      next_actions: ["Fix the batch file and rerun alps-unity batch run <file> --preflight."]
    }
  };
}

async function writeBatchRecord(path: string, data: unknown): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, `${JSON.stringify(data, null, 2)}\n`, "utf8");
}

function readDataString(result: CliResult, key: string): string | undefined {
  const value = readDataValue(result, key);
  return typeof value === "string" ? value : undefined;
}

function readDataValue(result: CliResult, key: string): unknown {
  return isRecord(result.data) ? result.data[key] : undefined;
}

function findCycles(steps: NormalizedStep[]): string[][] {
  const byId = new Map(steps.map((step) => [step.id, step]));
  const visiting = new Set<string>();
  const visited = new Set<string>();
  const cycles: string[][] = [];

  function visit(id: string, path: string[]): void {
    if (visiting.has(id)) {
      cycles.push([...path.slice(path.indexOf(id)), id]);
      return;
    }
    if (visited.has(id)) {
      return;
    }
    const step = byId.get(id);
    if (!step) {
      return;
    }
    visiting.add(id);
    for (const dependency of step.dependsOn) {
      visit(dependency, [...path, dependency]);
    }
    visiting.delete(id);
    visited.add(id);
  }

  for (const step of steps) {
    visit(step.id, [step.id]);
  }
  return cycles;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
