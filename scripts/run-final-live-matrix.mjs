#!/usr/bin/env node
/** Final live matrix; invokes only the public built CLI with shell:false. */
import { appendFile, lstat, mkdir, open, readFile, readdir, rename, rm, unlink, writeFile } from "node:fs/promises";
import { basename, dirname, extname, isAbsolute, relative, resolve, sep } from "node:path";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { fileURLToPath, pathToFileURL } from "node:url";
const FIXTURES = ["concurrency", "timeout_cancel", "stale_target", "domain_reload", "shell_quoting", "prefab_stage", "ui_cache"];
// 会触发 PackageManager.Resolve 的文件 mutation；UPM Resolve 在 PlayMode 下无法完成。
// editor.compile 的公开 PlayMode 拒绝码是契约登记的专属码（docs/error-codes.md），不是框架通用码。
export const PLAYMODE_UNAVAILABLE_CODE_OVERRIDES = new Map([
  ["editor.compile", "E_UNITY_COMPILE_REQUIRES_EDIT_MODE"]
]);
export const PLAYMODE_RESOLVE_BLOCKED_TOOLS = new Set([
  "package.deploy", "package.embed", "package.install", "package.remove", "package.resolve", "package.restore",
  "package.scoped-registry.add", "package.scoped-registry.remove"
]);
// Dev Matrix 的 project/attribute Tool 是固定 fixture inventory；公开 descriptor 不暴露内部 source。
const REQUIRED_PROJECT_TOOL_NAMES = Object.freeze([
  "test", "validation.echo", "validation.ui-cache", "validation.wait", "validation.wait-status",
  "UnityPipeline_test", "UnityPipeline_validation.official.any-output", "UnityPipeline_validation.official.dictionary",
  "UnityPipeline_validation.official.jobject", "UnityPipeline_validation.official.result-getter-throw",
  "UnityPipeline_validation.official.runtime", "UnityPipeline_validation.official.structured",
  "UnityPipeline_validation.official.sync", "UnityPipeline_validation.official.task",
  "UnityPipeline_validation.official.throw", "UnityPipeline_validation.official.timeout",
  "UnityPipeline_validation.official.timeout-status"
]);
const REQUIRED_PROJECT_TOOL_NAME_SET = new Set(REQUIRED_PROJECT_TOOL_NAMES);
const REMOVED_PUBLIC_TOOL_NAMES = Object.freeze(["build.status", "build.cancel"]);
/** These Tools require values returned by a fresh read Tool observation, not descriptor placeholders. */
export const EXTERNAL_STATE_DEPENDENT_TOOL_NAMES = Object.freeze(["editor.dialog.click"]);
const TEST_CANCEL_FIXTURE_NAME = "UnityCliValidation.MatrixEditModeSmokeTests.WaitForExplicitCancellation";
const TEST_CANCEL_PENDING_CODES = new Set(["E_TEST_RUN_NOT_ACTIVE", "E_TEST_CANCEL_IDENTITY_UNPROVEN"]);
export const MATRIX_OWNED_DIALOG = Object.freeze({
  title: "UnityCLI Matrix Dialog Fixture",
  messageMarker: "[unity-cli-matrix-owned-dialog-fixture]",
  button: "Cancel"
});
const EVIDENCE_APPEND_MAX_ATTEMPTS = 120;
const EVIDENCE_APPEND_RETRY_MS = 250;
const OWNED_PROJECT_TREE_ROOTS = Object.freeze(["Assets", "ProjectSettings", "Packages/com.alpsckr.unitycli"]);
const OWNED_PROJECT_FILES = Object.freeze(["Packages/manifest.json", "Packages/packages-lock.json", "Packages/unitycli.install.json"]);
const PROJECT_SNAPSHOT_TRANSITIONS = Object.freeze({
  prepared: new Set(["semantic-reset-complete", "restoring", "cleanup-failed"]),
  "semantic-reset-complete": new Set(["restoring", "cleanup-failed"]),
  restoring: new Set(["restored", "cleanup-failed"]),
  restored: new Set(["reconciled", "cleanup-failed"]),
  reconciled: new Set(["clean", "cleanup-failed"]),
  clean: new Set(),
  "cleanup-failed": new Set(["restoring"])
});

/** Windows 读侧短暂占用不得导致已完成的业务调用丢失证据；这里只重试日志写入，不重放 CLI。 */
export async function appendEvidenceLine(path, line, options = {}) {
  const append = options.append ?? appendFile;
  const wait = options.wait ?? ((delayMs) => new Promise((resolveDelay) => setTimeout(resolveDelay, delayMs)));
  const maxAttempts = options.maxAttempts ?? EVIDENCE_APPEND_MAX_ATTEMPTS;
  for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
    try {
      await append(path, line);
      return;
    } catch (error) {
      const sharingViolation = error?.code === "EBUSY" || error?.code === "EPERM";
      if (!sharingViolation || attempt === maxAttempts) throw error;
      await wait(EVIDENCE_APPEND_RETRY_MS);
    }
  }
}

class MatrixTerminalFailure extends Error {
  constructor(record) {
    super(`Matrix stopped at terminal failure: ${record?.id ?? "unknown"}`);
    this.record = record;
  }
}
function stopAfterTerminalFailure(record) {
  if (record?.assertion?.status === "FAIL") throw new MatrixTerminalFailure(record);
  return record;
}
// Package import 和 PlayMode domain reload 在大型项目中可超过 30 秒，矩阵必须等待完整公开 timeout 窗口。
const TRANSIENT_MAX_ATTEMPTS = 120;
const plain = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const nameOf = (v) => typeof v === "string" ? v : v?.name ?? v?.key;
const descriptorOf = (e) => e?.data?.descriptor ?? e?.data;

/** 从实际 CLI entry 同级 build 产物读取内部结果 schema，不猜测或混用另一编译树。 */
export async function loadInternalResultSchemas(cliEntry = resolve("dist", "cli.js")) {
  const root = dirname(resolve(cliEntry));
  const metadataPath = resolve(root, "tools", "metadata", "index.js");
  const catalogPath = resolve(root, "tools", "catalog.js");
  if (!await lstat(metadataPath).then(() => true).catch(() => false)
      || !await lstat(catalogPath).then(() => true).catch(() => false)) {
    throw new Error(`Compiled Tool metadata is missing beside CLI entry ${resolve(cliEntry)}.`);
  }
  const [metadata, catalog] = await Promise.all([
    import(pathToFileURL(metadataPath).href),
    import(pathToFileURL(catalogPath).href),
  ]);
  const tools = [...new Map([...metadata.builtinToolMetadata(), ...catalog.staticBridgeToolMetadata()].map((tool) => [tool.name, tool])).values()];
  const schemas = new Map();
  for (const tool of tools) {
    if (!plain(tool.outputSchema)) throw new Error(`Internal Tool ${tool.name} is missing outputSchema.`);
    schemas.set(tool.name, tool.outputSchema);
  }
  return schemas;
}

/** 从 runner CLI 配置解析实际编译入口；缺省仍绑定 package 的 dist/cli.js。 */
function compiledCliEntry(options) {
  return [options.cli, ...(options.cli_args ?? [])].find((value) => typeof value === "string" && /(?:^|[\\/])cli\.js$/i.test(value)) ?? resolve("dist", "cli.js");
}

export function parseOptions(argv) {
  const o = { resume: false, include_playmode: false, controls_only: false, timeoutMs: 120000, fixture: {}, cli_args: [], capabilities: [] };
  for (let i = 0; i < argv.length; i += 1) {
    const key = argv[i]; if (!key.startsWith("--")) throw new Error(`Unexpected argument: ${key}`);
    if (key === "--resume") throw new Error("--resume is forbidden; every corrected matrix must start at case zero with fresh evidence.");
    if (key === "--include-playmode") { o.include_playmode = true; continue; }
    if (key === "--controls-only") { o.controls_only = true; continue; }
    const value = argv[++i]; if (value === undefined) throw new Error(`Missing value for ${key}`);
    const name = key.slice(2).replaceAll("-", "_");
    if (name === "fixture_file") o.fixture_file = value;
    else if (name.startsWith("fixture_")) throw new Error("Inline fixture arguments are forbidden; pass --fixture-file.");
    else if (name === "cli_arg") o.cli_args.push(value);
    else if (name === "allow_destructive_project") throw new Error(`Unexpected argument: ${key}`);
    else if (name === "capability") o.capabilities.push(value);
    else if (name === "timeout_ms") o.timeoutMs = Number(value);
    else o[name] = value;
  }
  for (const key of ["cli", "project", "evidence_dir"]) if (!o[key]) throw new Error(`Required --${key.replaceAll("_", "-")} is missing.`);
  if (!Number.isFinite(o.timeoutMs) || o.timeoutMs <= 0) throw new Error("--timeout-ms must be positive.");
  // controls-only 是独立全局扰动切片；与按capability切片互斥，避免同一runner进程混入两套切片语义。
  if (o.controls_only && o.capabilities.length > 0) throw new Error("--controls-only forbids --capability.");
  return o;
}

/** 将公开 structured example 映射为真实 tools run input 与命令层控制项。 */
export function parsePublicExample(example, kind, expectedName) {
  const subject = `${kind} ${expectedName}`;
  if (!plain(example) || !plain(example.input)) throw new Error(`${subject} example must contain an object input.`);
  const unexpected = Object.keys(example).filter((key) => !["description", "input", "execution"].includes(key));
  if (unexpected.length > 0) throw new Error(`${subject} example contains unsupported fields: ${unexpected.join(", ")}.`);
  if (Object.hasOwn(example.input, "dryRun")) throw new Error(`${subject} input cannot contain the execution-only dryRun field.`);
  const flags = [];
  if (example.execution !== undefined) {
    if (!plain(example.execution)) throw new Error(`${subject} execution must be an object.`);
    const unexpectedExecution = Object.keys(example.execution).filter((key) => !["dryRun", "timeoutMs"].includes(key));
    if (unexpectedExecution.length > 0) throw new Error(`${subject} execution contains unsupported fields: ${unexpectedExecution.join(", ")}.`);
    if (example.execution.dryRun !== undefined && typeof example.execution.dryRun !== "boolean") throw new Error(`${subject} execution.dryRun must be boolean.`);
    if (example.execution.dryRun === true) flags.push("--dry-run");
    if (example.execution.timeoutMs !== undefined) {
      if (!Number.isSafeInteger(example.execution.timeoutMs) || example.execution.timeoutMs <= 0) throw new Error(`${subject} execution.timeoutMs must be a positive integer.`);
      flags.push("--timeout-ms", String(example.execution.timeoutMs));
    }
  }
  return { input: example.input, flags };
}

/** 检查 live describe 只返回当前精简公开字段。 */
export function publicDescriptorIssue(descriptor) {
  if (!plain(descriptor) || typeof descriptor.name !== "string" || typeof descriptor.description !== "string" || !plain(descriptor.inputSchema)) return "Describe returned an incomplete invocation descriptor.";
  const forbidden = ["outputSchema", "group", "permissions", "summary", "source", "availabilityScope", "contractProfile", "completionContract", "handler"];
  const leaked = forbidden.filter((field) => Object.hasOwn(descriptor, field));
  if (leaked.length > 0) return `Describe leaked non-public fields: ${leaked.join(", ")}.`;
  if (descriptor.available === true) return "Describe must omit default available:true.";
  if (descriptor.available === false && typeof descriptor.unavailableReason !== "string") return "Unavailable descriptor must include unavailableReason.";
  if (descriptor.examples !== undefined) {
    if (!Array.isArray(descriptor.examples)) return "Descriptor examples must be an array.";
    try { for (const example of descriptor.examples) parsePublicExample(example, "tool", descriptor.name); }
    catch (error) { return error instanceof Error ? error.message : String(error); }
  }
  return null;
}
/** Recursively checks required fields and primitive/object/array schema types. */
function businessPayload(e) { return e?.data; }

/** runner 通过重复纯读观察 readiness；editor.state 本身永不等待。 */
export function isReadyEditorState(envelope, playing) {
  const state = businessPayload(envelope);
  return envelope?.ok === true
    && state?.status === "ready"
    && state?.state?.playMode?.known === true
    && state?.state?.playMode?.value === (playing ? "play" : "edit");
}

/** 成功 envelope 只允许公开协议字段；业务 DTO 的额外字段继续由 output schema 拒绝。 */
export function assertSuccess(e, schema = {}) {
  const payload = businessPayload(e);
  if (e?.ok !== true || typeof e?.requestId !== "string" || !plain(payload)) return "Expected successful JSON envelope with requestId and object business data.";
  const unexpected = Object.keys(e).filter((key) => !["ok", "requestId", "data", "warnings"].includes(key));
  if (unexpected.length > 0) return `Success envelope contains unexpected fields: ${unexpected.sort().join(", ")}.`;
  if (e.warnings !== undefined && (!Array.isArray(e.warnings) || e.warnings.some((value) => typeof value !== "string"))) return "Success envelope warnings must be a string array.";
  return assertSchema(payload, schema, "$.data");
}

/** 正向调用必须返回非失败的最终业务终态。 */
export function assertPositiveSuccess(e, schema = {}) {
  const issue = assertSuccess(e, schema);
  if (issue) return issue;
  const payload = businessPayload(e);
  return new Set(["failed", "timeout"]).has(payload.status) ? `Positive invocation reported terminal status ${payload.status}.` : null;
}
function assertSchema(value, schema, path) {
  if (!plain(schema)) return null;
  if (schema.type && !typeIs(value, schema.type)) return `${path} must be ${schema.type}.`;
  if ((schemaHasType(schema, "object") || schema.properties) && plain(value)) {
    for (const key of schema.required ?? []) if (!Object.hasOwn(value, key)) return `${path}.${key} is required.`;
    const properties = schema.properties ?? {};
    if (schema.additionalProperties === false) {
      const unexpected = Object.keys(value).filter((key) => !Object.hasOwn(properties, key));
      if (unexpected.length > 0) return `${path} contains unexpected fields: ${unexpected.sort().join(", ")}.`;
    }
    for (const [key, child] of Object.entries(properties)) if (Object.hasOwn(value, key)) { const issue = assertSchema(value[key], child, `${path}.${key}`); if (issue) return issue; }
  }
  if (schemaHasType(schema, "array") && Array.isArray(value)) for (let i = 0; i < value.length; i += 1) { const issue = assertSchema(value[i], schema.items ?? {}, `${path}[${i}]`); if (issue) return issue; }
  return null;
}
function schemaHasType(schema, type) { return schema.type === type || (Array.isArray(schema.type) && schema.type.includes(type)); }
function typeIs(v, t) {
  if (Array.isArray(t)) return t.some((type) => typeIs(v, type));
  return t === "null" ? v === null : t === "array" ? Array.isArray(v) : t === "object" ? plain(v) : t === "integer" ? Number.isInteger(v) : typeof v === t;
}

/** 为一个 schema 选择必定不匹配的 JSON 值；any-json 字段返回 undefined。 */
function invalidSchemaValue(schema) {
  if (!plain(schema) || schema.xUnityCliAnyJson === true || schema["x-unityCliAnyJson"] === true) return undefined;
  const branches = [...(schema.oneOf ?? []), ...(schema.anyOf ?? [])];
  const accepts = (type) => schema.type === type || branches.some((branch) => branch.type === type || branch["x-unityCliAnyJson"] === true);
  const candidates = [
    ["object", { invalid: true }],
    ["array", ["invalid"]],
    ["string", "__matrix_invalid_type__"],
    ["number", 1],
    ["boolean", true],
    ["null", null]
  ];
  return candidates.find(([type]) => !accepts(type))?.[1];
}

export function assertError(e, requireSchemaDetails = true) {
  const x = e?.error; if (e?.ok !== false || typeof e?.requestId !== "string" || !plain(x)) return "Expected failed JSON envelope with requestId.";
  const unexpected = Object.keys(e).filter((key) => !["ok", "requestId", "error"].includes(key));
  if (unexpected.length > 0) return `Error envelope contains unexpected fields: ${unexpected.sort().join(", ")}.`;
  if (typeof x.code !== "string" || typeof x.message !== "string" || typeof x.transient !== "boolean" || typeof x.retryAfterMs !== "number" || x.retryAfterMs < 0 || !Array.isArray(x.nextActions) || x.nextActions.some((v) => typeof v !== "string")) return "Error fields have invalid types.";
  if (requireSchemaDetails && (!plain(x.details) || typeof x.details.path !== "string" || typeof x.details.expected !== "string" || typeof x.details.actual !== "string")) return "Error details must contain typed path, expected, and actual.";
  return null;
}
export function runCli(cli, argv, timeoutMs) { return new Promise((done) => {
  const startedAtMs = Date.now();
  const child = spawn(cli, argv, { shell: false, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
  let stdout = "", stderr = ""; let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    // Windows 的 child.kill() 不保证终止孙进程；taskkill 使切片中断后不会遗留 CLI 树。
    if (process.platform === "win32" && child.pid) spawn("taskkill", ["/pid", String(child.pid), "/t", "/f"], { windowsHide: true, stdio: "ignore" });
    else child.kill("SIGKILL");
  }, timeoutMs);
  child.stdout.on("data", (v) => { stdout += v; }); child.stderr.on("data", (v) => { stderr += v; }); child.on("error", (v) => { stderr += String(v); });
  child.on("close", (exitCode) => { const endedAtMs = Date.now(); clearTimeout(timer); done({ exitCode, stdout, stderr, timedOut, startedAtMs, endedAtMs, durationMs: endedAtMs - startedAtMs }); });
}); }

class Matrix {
  constructor(options) {
    this.o = options;
    this.records = [];
    this.prior = new Map();
    this.expected = new Set();
    this.discovered = { tools: [] };
    this.descriptors = new Map();
    this.resultSchemas = new Map();
    this.directRecovery = new Map();
    this.firstTerminalFailure = null;
    const tokenMaterial = `${options.build_stamp ?? ""}\0${resolve(options.project)}\0${options.target ?? ""}\0${resolve(options.evidence_dir)}\0${process.pid}\0${Date.now()}`;
    this.runToken = createHash("sha256").update(tokenMaterial).digest("hex").slice(0, 10);
  }
  async init() {
    await mkdir(this.o.evidence_dir, { recursive: true });
    this.jsonl = resolve(this.o.evidence_dir, "calls.jsonl");
    this.directRecoveryPath = resolve(this.o.evidence_dir, "direct-recovery.json");
    try {
      const document = JSON.parse(await readFile(this.directRecoveryPath, "utf8"));
      for (const [id, entry] of Object.entries(document?.cases ?? {})) if (plain(entry)) this.directRecovery.set(id, entry);
    } catch {}
    this.resultSchemas = await loadInternalResultSchemas(compiledCliEntry(this.o));
    if (!this.o.resume) await writeFile(this.jsonl, "");
    else {
      try {
        for (const line of (await readFile(this.jsonl, "utf8")).split(/\r?\n/)) if (line) {
          const record = JSON.parse(line);
          this.prior.set(record.id, record);
        }
      } catch {}
    }
    // exact project snapshot 必须先于任何 state/runCli，失败时不允许产生 Unity effect。
    this.projectSnapshot = await prepareOwnedProjectSnapshot(this.o.project, this.o.evidence_dir, this.o.build_stamp, { allowExisting: this.o.resume, target: this.o.target ?? null, runId: this.runToken });
  }
  /** resume 仅复用完整 PASS case；失败 terminal case 必须重建其所有前置状态。 */
  invalidateFailedCaseSetup(caseId) {
    if (this.prior.get(caseId)?.assertion?.status === "PASS") return;
    for (const [id, record] of this.prior) {
      const belongsToCase = record?.baseline?.sourceCase === caseId || (id.startsWith("control.") && id.includes(`.${caseId}.`));
      if (belongsToCase) this.prior.delete(id);
    }
  }
  /** 会话设置与全局 PlayMode transition 不能跨 runner 进程复用。 */
  invalidateSessionControls() {
    for (const id of [...this.prior.keys()]) {
      const globalTransition = ["tool.editor.play.transition", "playmode.ready", "tool.editor.stop.transition", "playmode.exit-ready"].includes(id);
      const processLocalFixture = [
        "control.setup.", "control.reset.", "control.final-reset.", "control.pipeline-final-restore.",
        "control.playmode-fast.", "control.playmode-restore.", "control.playmode-initial-restore.",
        "control.play-baseline.initial-editor.", "control.play-baseline.post-playmode."
      ].some((prefix) => id.startsWith(prefix));
      if (globalTransition || processLocalFixture) this.prior.delete(id);
    }
  }
  /** PlayMode coverage 入口再次清除 transition，确保当前进程真实进入并退出。 */
  invalidateFailedPlayModeControls() {
    for (const id of ["tool.editor.play.transition", "playmode.ready", "tool.editor.stop.transition", "playmode.exit-ready"]) this.prior.delete(id);
  }
  argv(base, project = this.o.project) { return publicMatrixArgv(base, project, this.o.project, this.o.target); }
  /** 复用证据必须同时绑定 build、project 与 target，避免跨环境跳过 case。 */
  hasReusablePass(id, project = this.o.project) {
    const old = this.prior.get(id);
    return old?.assertion?.status === "PASS"
      && old.buildStamp === (this.o.build_stamp ?? null)
      && old.project !== undefined
      && resolve(old.project) === resolve(project)
      && old.target === (this.o.target ?? null);
  }
  async state(project) { const argv = this.argv(["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], project); const result = await runCli(this.o.cli, [...this.o.cli_args, ...argv], Math.min(this.o.timeoutMs, 15000)); let envelope; try { envelope = JSON.parse(result.stdout); } catch {} return { capturedAt: new Date().toISOString(), argv, result, envelope, target: this.o.target ?? null, buildStamp: this.o.build_stamp ?? null }; }
  async call(id, base, assertion, meta = {}, project = this.o.project) {
    this.expected.add(id);
    const argv = this.argv(base, project);
    const old = this.prior.get(id);
    if (this.hasReusablePass(id, project)) { this.records.push(old); return old; }

    const existingIndex = this.records.findIndex((record) => record.id === id);
    if (existingIndex >= 0 && this.records[existingIndex].assertion?.status === "PASS") return this.records[existingIndex];

    const attempts = [];
    let final;
    for (let attempt = 1; attempt <= TRANSIENT_MAX_ATTEMPTS; attempt += 1) {
      const preState = base[0] === "tools" && base[1] === "run" && base[2] === "editor.state" ? null : await this.state(project);
      const result = await runCli(this.o.cli, [...this.o.cli_args, ...argv], this.o.timeoutMs);
      let envelope; let parse;
      try { envelope = JSON.parse(result.stdout); } catch (error) { parse = String(error); }
      const issue = parse ? `stdout is not a complete JSON envelope: ${parse}` : assertion(envelope, result);
      attempts.push({ attempt, at: new Date().toISOString(), exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr, envelope, preState, ...(parse ? { parse } : {}), ...(issue ? { issue } : {}) });
      final = { result, envelope, preState, issue };
      if (issue) {
        const checkpoint = { id, at: new Date().toISOString(), argv, input: inputFrom(argv), exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr, envelope, preState, target: this.o.target ?? null, project, buildStamp: this.o.build_stamp ?? null, assertion: { status: "FAIL", issue }, retryCheckpoint: { attempt }, ...meta };
        await appendEvidenceLine(this.jsonl, `${JSON.stringify(checkpoint)}\n`);
      }
      const retryReadiness = meta.retryAssertion === true
        && base[0] === "tools"
        && base[1] === "run"
        && base[2] === "editor.state";
      if (!issue || !retryReadiness || attempt === TRANSIENT_MAX_ATTEMPTS) break;
      const requestedDelay = Number(envelope?.error?.retryAfterMs);
      const retryAfterMs = Number.isFinite(requestedDelay) && requestedDelay > 0 ? requestedDelay : 1000;
      await new Promise((resolveDelay) => setTimeout(resolveDelay, Math.min(2000, Math.max(500, retryAfterMs))));
    }

    const { result, envelope, preState, issue } = final;
    const r = { id, at: new Date().toISOString(), argv, input: inputFrom(argv), exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr, envelope, preState, process: { startedAtMs: result.startedAtMs, endedAtMs: result.endedAtMs, durationMs: result.durationMs }, target: this.o.target ?? null, project, buildStamp: this.o.build_stamp ?? null, assertion: { status: issue ? "FAIL" : "PASS", ...(issue ? { issue } : {}) }, ...(attempts.length > 1 ? { attempts } : {}), ...meta };
    if (r.assertion.status === "FAIL" && !this.firstTerminalFailure) this.firstTerminalFailure = r;
    const journalIssue = await journalLifecycleIssue(project, base, r.envelope, this.o.target);
    if (journalIssue && !r.assertion.issue) {
      r.assertion = { status: "FAIL", issue: journalIssue };
      if (!this.firstTerminalFailure) this.firstTerminalFailure = r;
    }
    if (existingIndex >= 0) this.records[existingIndex] = r; else this.records.push(r);
    await appendEvidenceLine(this.jsonl, `${JSON.stringify(r)}\n`);
    return r;
  }
  async blocked(id, issue, meta = {}) { this.expected.add(id); const r = { id, at: new Date().toISOString(), project: this.o.project, target: this.o.target ?? null, buildStamp: this.o.build_stamp ?? null, assertion: { status: "BLOCKED", issue }, ...meta }; this.records.push(r); await appendEvidenceLine(this.jsonl, `${JSON.stringify(r)}\n`); return r; }
  async notApplicable(id, reason, meta = {}) { this.expected.add(id); const r = { id, at: new Date().toISOString(), project: this.o.project, target: this.o.target ?? null, buildStamp: this.o.build_stamp ?? null, applicability: "not-applicable", assertion: { status: "PASS", reason }, ...meta }; this.records.push(r); await appendEvidenceLine(this.jsonl, `${JSON.stringify(r)}\n`); return r; }
  async failed(id, issue, meta = {}) { this.expected.add(id); const r = { id, at: new Date().toISOString(), project: this.o.project, target: this.o.target ?? null, buildStamp: this.o.build_stamp ?? null, assertion: { status: "FAIL", issue }, ...meta }; if (!this.firstTerminalFailure) this.firstTerminalFailure = r; this.records.push(r); await appendEvidenceLine(this.jsonl, `${JSON.stringify(r)}\n`); return r; }
}
/** Tool/Context 只按 instance 路由；project 仅属于显式项目控制面。 */
export function publicMatrixArgv(base, project, primaryProject, target) {
  const argv = [...base];
  if (base[0] === "extensions" || base[0] === "init") argv.push("--project", project);
  else if ((base[0] === "tools" || base[0] === "doctor") && resolve(project) === resolve(primaryProject) && target && !base.includes("--instance")) argv.push("--instance", target);
  return argv;
}
function inputFrom(argv) { const i = argv.indexOf("--input"); try { return i < 0 ? {} : JSON.parse(argv[i + 1]); } catch { return null; } }

const ACTIVE_OPERATION_SCOPES = new Map([
  ["editor.compile", "compile"], ["package.resolve", "package"], ["editor.refresh", "refresh"],
  ["test.run", "test-run"], ["build.run", "build"]
]);

/** 普通调用不产生请求级档案；跨重载协调器只允许短暂 active 文件，终态必须删除。 */
export async function journalLifecycleIssue(project, base, envelope, target) {
  const name = base?.[2];
  const scope = ACTIVE_OPERATION_SCOPES.get(name);
  if (scope) {
    const activePath = resolve(project, "Library", "UnityCli", `active-${scope}.json`);
    if (await lstat(activePath).then(() => true).catch(() => false)) return `Cross-reload active operation was not cleared at terminal state: ${activePath}.`;
    return null;
  }
  const requestId = envelope?.requestId;
  if (typeof requestId !== "string" || requestId.length === 0) return null;
  const candidates = [resolve(project, "Library", "UnityCli", "requests", `${requestId}.json`)];
  if (process.env.LOCALAPPDATA && target) candidates.push(resolve(process.env.LOCALAPPDATA, "UnityCli", "requests", target, `${requestId}.json`));
  for (const path of candidates) if (await lstat(path).then(() => true).catch(() => false)) return `Request-level journal must not be produced: ${path}.`;
  return null;
}

/** 只接受当前 extensions doctor 的 checks 契约，字段缺失或物理投影不一致都 fail closed。 */
export function extensionAvailabilityIssues(listData, doctorData) {
  const checks = Array.isArray(doctorData?.checks) ? doctorData.checks : [];
  const byId = new Map(checks.filter((value) => typeof value?.id === "string").map((value) => [value.id, value]));
  const issues = [];
  for (const extension of Array.isArray(listData?.extensions) ? listData.extensions : []) {
    const id = typeof extension?.id === "string" ? extension.id : "unknown";
    const toolCount = Number.isInteger(extension?.toolCount) ? extension.toolCount : Array.isArray(extension?.providedTools) ? extension.providedTools.length : 0;
    if (toolCount === 0) continue;
    const check = byId.get(id);
    const arrays = ["missingPackageIds", "missingFiles", "changedFiles", "unknownFiles"];
    const malformed = !check || arrays.some((field) => !Array.isArray(check[field]));
    const inconsistent = malformed || check.availability !== "available" || check.installed !== true || arrays.some((field) => check[field].length > 0);
    if (inconsistent) issues.push({ id, reason: `Extension ${id} contribution is not physically installed with an available, exact dependency and file projection.` });
  }
  return issues;
}

/** 验证基线来自内部 completion contract；公开 inventory 必须由 live instance 完整返回。 */
export async function loadRuntimeInventoryBaseline() {
  const completion = JSON.parse(await readFile(new URL("../contracts/tool-completion-contract.json", import.meta.url), "utf8"));
  const catalog = JSON.parse(await readFile(new URL("../contracts/static-capability-catalog.json", import.meta.url), "utf8"));
  if (!Array.isArray(completion?.tools) || !completion.tools.every((value) => typeof value?.toolName === "string")) throw new Error("Tool completion inventory is malformed.");
  if (Object.hasOwn(catalog ?? {}, "contexts")) throw new Error("Static inventory contains a forbidden legacy contexts field.");
  return { tools: [...new Set(completion.tools.map((value) => value.toolName))].sort() };
}

/** Dev project fixtures 是当前 matrix 的固定额外 inventory；产品 Tool 必须与 contracts 精确一致。 */
export function runtimeInventoryIssues(discovered, baseline) {
  const issues = [];
  const actualTools = new Set(discovered.tools ?? []);
  const expectedTools = new Set(baseline.tools ?? []);
  const missingTools = [...expectedTools].filter((name) => !actualTools.has(name));
  const unexpectedTools = [...actualTools].filter((name) => !expectedTools.has(name) && !REQUIRED_PROJECT_TOOL_NAME_SET.has(name));
  const missingFrozen = FROZEN_DIRECT_TOOL_NAMES.filter((name) => !actualTools.has(name));
  const removedPresent = REMOVED_PUBLIC_TOOL_NAMES.filter((name) => actualTools.has(name));
  const missingProjectTools = REQUIRED_PROJECT_TOOL_NAMES.filter((name) => !actualTools.has(name));
  if (missingTools.length) issues.push(`Missing current public Tools: ${missingTools.join(", ")}.`);
  if (unexpectedTools.length) issues.push(`Unexpected non-project Tools: ${unexpectedTools.sort().join(", ")}.`);
  if (missingFrozen.length) issues.push(`Missing frozen direct Tools: ${missingFrozen.join(", ")}.`);
  if (removedPresent.length) issues.push(`Removed public Tools are discoverable: ${removedPresent.join(", ")}.`);
  if (missingProjectTools.length) issues.push(`Missing required project fixture Tools: ${missingProjectTools.join(", ")}.`);
  return issues;
}

function inventoryHash(names) { return createHash("sha256").update([...names].sort().join("\n")).digest("hex"); }

async function discover(m) {
  const groups = await m.call("catalog.groups", ["tools", "group"], (e) => assertSuccess(e, { required: ["groups"], properties: { groups: { type: "array" } } }));
  for (const item of groups.envelope?.data?.groups ?? []) {
    const group = nameOf(item); if (!group) continue;
    let cursor;
    do {
      const argv = ["tools", "group", "list", group, "--page-size", "100", ...(cursor === undefined ? [] : ["--cursor", String(cursor)])];
      const suffix = cursor === undefined ? "first" : String(cursor);
      const listed = await m.call(`tools.group.${group}.${suffix}`, argv, (e) => assertSuccess(e, { required: ["tools", "pageInfo"] }));
      for (const tool of listed.envelope?.data?.tools ?? []) {
        const name = nameOf(tool); if (!name) continue;
        if (!m.discovered.tools.includes(name)) m.discovered.tools.push(name);
      }
      cursor = listed.envelope?.data?.pageInfo?.nextCursor;
    } while (Number.isSafeInteger(cursor) && cursor >= 0);
  }
  const ext = await m.call("extensions.list", ["extensions", "list"], (e) => assertSuccess(e, {}));
  const doc = await m.call("extensions.doctor", ["extensions", "doctor"], (e) => assertSuccess(e, {}));
  for (const issue of extensionAvailabilityIssues(ext.envelope?.data, doc.envelope?.data)) await m.blocked(`extension.${issue.id}.availability`, issue.reason);

  const baseline = await loadRuntimeInventoryBaseline();
  const inventoryIssues = runtimeInventoryIssues(m.discovered, baseline);
  const productTools = m.discovered.tools.filter((name) => !REQUIRED_PROJECT_TOOL_NAME_SET.has(name));
  const projectTools = m.discovered.tools.filter((name) => REQUIRED_PROJECT_TOOL_NAME_SET.has(name));
  await m.call("inventory.current-tree", ["tools", "run", "editor.state", "--input", "{}"], (e) => {
    const envelopeIssue = assertSuccess(e, {});
    return envelopeIssue ?? (inventoryIssues.length > 0 ? inventoryIssues.join(" ") : null);
  }, {
    inventory: {
      expectedToolCount: baseline.tools.length,
      actualToolCount: productTools.length,
      expectedToolsSha256: inventoryHash(baseline.tools),
      actualToolsSha256: inventoryHash(productTools),
      projectTools,
      issues: inventoryIssues
    }
  });
}
async function getDescriptor(m, kind, name) {
  const cached = m.descriptors.get(`${kind}:${name}`);
  if (cached) return cached;
  const r = await m.call(`${kind}.${name}.describe`, ["tools", "describe", name], (e) => assertSuccess(e, {}) ?? publicDescriptorIssue(descriptorOf(e)));
  const descriptor = descriptorOf(r.envelope);
  if (!descriptor) return descriptor;
  const resultSchema = m.resultSchemas instanceof Map ? m.resultSchemas.get(name) : undefined;
  const resolved = resultSchema ? { ...descriptor, resultSchema } : descriptor;
  m.descriptors.set(`${kind}:${name}`, resolved);
  return resolved;
}
function matrixExampleFlags(flags) {
  const result = [];
  const routed = new Set(["--project", "--instance"]);
  for (let index = 0; index < flags.length; index += 1) {
    if (routed.has(flags[index])) { index += 1; continue; }
    result.push(flags[index]);
  }
  return result;
}
function argvFor(_kind, name, input, flags = []) { return ["tools", "run", name, "--input", JSON.stringify(input), ...matrixExampleFlags(flags)]; }
export function capabilityProject(m, _kind, descriptor) {
  if (descriptor?.name === "package.bridge-remove" && typeof m.o.offline_project === "string") return m.o.offline_project;
  return m.o.project;
}
function matrixCaseId(kind, name, id, phase) { return phase === "play" ? `${kind}.${name}.play.${id}` : `${kind}.${name}.${id}`; }

export const FROZEN_DIRECT_TOOL_NAMES = Object.freeze([
  "animator.crossfade", "code.exec", "editor.undo", "editor.redo", "physics.simulation", "navmesh.surface.bake",
  "prefab.overrides.apply", "prefab.overrides.revert", "prefab.variant.create", "reflection.method.call", "scene.view-frame",
  "timeline.clip.add", "timeline.create", "timeline.info", "timeline.track.add",
  "probuilder.append-vertices-to-edge", "probuilder.auto-smooth", "probuilder.bevel-edges", "probuilder.bridge-edges", "probuilder.connect-elements",
  "probuilder.create-shape", "probuilder.duplicate-and-flip", "probuilder.extrude-edges", "probuilder.extrude-faces", "probuilder.flip-normals",
  "probuilder.insert-vertex", "probuilder.merge-objects", "probuilder.merge-vertices", "probuilder.subdivide", "probuilder.weld-vertices"
]);

const step = (id, name, input = {}, options = {}) => ({ id, kind: "tool", name, input, ...options });
const readStep = (id, name, input = {}, options = {}) => ({ id, kind: "tool", name, input, ...options });
const trianglePoints = [
  { x: 2, y: 0, z: 0 },
  { x: 0, y: 0, z: 0 },
  { x: 1, y: 0, z: 1 }
];
const closeTrianglePoints = [
  { x: 0, y: 0, z: 0 },
  { x: 0.005, y: 0, z: 0 },
  { x: 0, y: 0, z: 1 }
];
const completionProbe = (target, axis, before, after, tolerance = 0.0001) => ({
  readTool: "scene.gameobject",
  input: { target, includeComponents: false },
  assertions: [{ path: `/gameObject/transform/position/${axis}`, operator: "approx", before, after, tolerance }]
});
const meshInfoStep = (id, prefix, target = "${target}") => step(id, "probuilder.mesh-info", { target }, {
  capture: {
    [`${prefix}VertexCount`]: "/data/vertexCount",
    [`${prefix}EdgeCount`]: "/data/edgeCount",
    [`${prefix}FaceCount`]: "/data/faceCount",
    [`${prefix}Bounds`]: "/data/bounds"
  }
});
const validateMeshStep = (target = "${target}") => step("validate", "probuilder.validate-mesh", { target }, { expect: { equals: { "/data/healthy": true } } });
const deleteTargetStep = (id, target = "${target}") => step(id, "go.delete", { target }, {
  acceptErrorCodes: ["E_TARGET_NOT_FOUND", "E_GAME_OBJECT_NOT_FOUND", "E_OBJECT_NOT_FOUND"]
});

const deleteAssetStep = (id, path) => step(id, "asset.delete", { path }, {
  acceptErrorCodes: ["E_ASSET_NOT_FOUND"]
});
const manualUvStep = (id, target) => step(id, "component.modify", {
  path: target,
  componentType: "UnityEngine.ProBuilder.ProBuilderMesh",
  pathPatches: [{ op: "replace", path: "m_Faces.Array.data[0].m_ManualUV", value: true }]
});

/** 30 个 frozen Tool 的正向调用闭集；每项拥有自己的前态、调用、fresh readback 和 cleanup。 */
export const DIRECT_LIVE_CASES = Object.freeze({
  "animator.crossfade": {
    setup: [
      step("create-target", "go.create", { name: "${target}", x: 0, y: 0, z: 0 }, { capture: { targetId: "/data/gameObject/id" } }),
      step("add-animator", "component.add", { target: "${target}", component: "Animator", properties: {} }),
      step("create-controller", "controller.create", { controllerPath: "${assetPath}" }),
      step("add-idle", "controller.add-state", { controllerPath: "${assetPath}", stateName: "Idle", isDefault: true }),
      step("add-run", "controller.add-state", { controllerPath: "${assetPath}", stateName: "Run" }),
      step("assign-controller", "controller.assign", { target: "${target}", controllerPath: "${assetPath}" }),
      step("play-idle", "animation.play", { target: "${target}", state: "Idle", layer: 0, normalizedTime: 0 })
    ],
    before: [
      readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { beforeTargetId: "/data/gameObject/id" } }),
      step("state", "animation.state", { target: "${target}", layer: 0 }, { capture: { beforeCurrentStateHash: "/data/animator/currentState/fullPathHash" } })
    ],
    invocation: step("invoke", "animator.crossfade", { target: "${target}", state: "Run", duration: 0, layer: 0 }, {
      expect: { equals: { "/data/changed": true, "/data/transition/state": "Run", "/data/transition/layer": 0 }, exists: ["/data/state/currentStateHash", "/data/state/nextStateHash"] },
      capture: { invocationCurrentStateHash: "/data/state/currentStateHash" }
    }),
    after: [
      readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { afterTargetId: "/data/gameObject/id" } }),
      step("state", "animation.state", { target: "${target}", layer: 0 }, { capture: { afterCurrentStateHash: "/data/animator/currentState/fullPathHash" } })
    ],
    invariants: [
      { left: "targetId", operator: "equals", right: "beforeTargetId" },
      { left: "beforeTargetId", operator: "equals", right: "afterTargetId" },
      { left: "beforeCurrentStateHash", operator: "notEquals", right: "invocationCurrentStateHash" },
      { left: "invocationCurrentStateHash", operator: "equals", right: "afterCurrentStateHash" }
    ],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "code.exec": {
    setup: [step("create-target", "go.create", { name: "${target}", x: 0, y: 0, z: 0 }, { capture: { targetId: "/data/gameObject/id" } })],
    before: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { beforeX: "/data/gameObject/transform/position/x", beforeTargetId: "/data/gameObject/id" } })],
    preflight: [
      step("dry-run", "code.exec", {
        code: "var go = GameObject.Find(\"${target}\"); go.transform.position = new Vector3(1f, go.transform.position.y, go.transform.position.z); return null;",
        completionProbe: completionProbe("${target}", "x", 0, 1)
      }, { flags: ["--dry-run"], expect: { equals: { "/data/dryRun": true } } })
    ],
    invocation: step("invoke", "code.exec", {
      code: "var go = GameObject.Find(\"${target}\"); go.transform.position = new Vector3(1f, go.transform.position.y, go.transform.position.z); return null;",
      completionProbe: completionProbe("${target}", "x", 0, 1)
    }, { expect: { equals: { "/data/completion/assertions/0/value": 1 } }, capture: { completionX: "/data/completion/assertions/0/value" } }),
    after: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { afterX: "/data/gameObject/transform/position/x", afterTargetId: "/data/gameObject/id" } })],
    invariants: [{ left: "beforeX", operator: "equals", value: 0 }, { left: "completionX", operator: "equals", value: 1 }, { left: "afterX", operator: "equals", value: 1 }, { left: "beforeTargetId", operator: "equals", right: "afterTargetId" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "editor.undo": {
    setup: [
      step("create-target", "go.create", { name: "${target}", x: 0, y: 0, z: 0 }),
      step("move-target", "go.modify", { target: "${target}", x: 1, y: 0, z: 0 })
    ],
    before: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { beforeX: "/data/gameObject/transform/position/x", beforeTargetId: "/data/gameObject/id" } })],
    invocation: step("invoke", "editor.undo", { completionProbe: completionProbe("${target}", "x", 1, 0) }, { expect: { equals: { "/data/performed": true, "/data/completion/assertions/0/value": 0 } } }),
    after: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { afterX: "/data/gameObject/transform/position/x", afterTargetId: "/data/gameObject/id" } })],
    invariants: [{ left: "beforeX", operator: "equals", value: 1 }, { left: "afterX", operator: "equals", value: 0 }, { left: "beforeTargetId", operator: "equals", right: "afterTargetId" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "editor.redo": {
    setup: [
      step("create-target", "go.create", { name: "${target}", x: 0, y: 0, z: 0 }),
      step("move-target", "go.modify", { target: "${target}", x: 1, y: 0, z: 0 }),
      step("build-redo-stack", "editor.undo", { completionProbe: completionProbe("${target}", "x", 1, 0) }, { expect: { equals: { "/data/performed": true, "/data/completion/assertions/0/value": 0 } } })
    ],
    before: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { beforeX: "/data/gameObject/transform/position/x", beforeTargetId: "/data/gameObject/id" } })],
    invocation: step("invoke", "editor.redo", { completionProbe: completionProbe("${target}", "x", 0, 1) }, { expect: { equals: { "/data/performed": true, "/data/completion/assertions/0/value": 1 } } }),
    after: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { afterX: "/data/gameObject/transform/position/x", afterTargetId: "/data/gameObject/id" } })],
    invariants: [{ left: "beforeX", operator: "equals", value: 0 }, { left: "afterX", operator: "equals", value: 1 }, { left: "beforeTargetId", operator: "equals", right: "afterTargetId" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "physics.simulation": {
    setup: [
      step("read-settings", "physics.settings", {}, { capture: { priorAutoSimulation: "/data/physics/autoSimulation", priorSimulationMode2D: "/data/physics/simulationMode2D", priorGravity: "/data/physics/gravity" } }),
      step("manual-settings", "physics.settings", { autoSimulation: false, simulationMode2D: "Script", gravity: { x: 0, y: -9.81, z: 0 } }, { expect: { equals: { "/data/physics/autoSimulation": false, "/data/physics/simulationMode2D": "Script", "/data/physics/gravity/y": -9.8100004196167 } } }),
      step("create-target", "go.create", { name: "${target}", primitive: "cube", x: 0, y: 5, z: 0 }),
      step("add-rigidbody", "component.add", { target: "${target}", component: "Rigidbody", properties: { useGravity: true, isKinematic: false } })
    ],
    before: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { beforeY: "/data/gameObject/transform/position/y", beforeTargetId: "/data/gameObject/id" } })],
    invocation: step("invoke", "physics.simulation", { steps: 1, deltaTime: 0.02, simulate: true, completionProbe: completionProbe("${target}", "y", 5, 4.996076, 0.001) }, {
      expect: { equals: { "/data/simulation/simulate": true, "/data/simulation/steps": 1 }, exists: ["/data/completion/assertions/0/value"] },
      capture: { completionY: "/data/completion/assertions/0/value" }
    }),
    after: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { afterY: "/data/gameObject/transform/position/y", afterTargetId: "/data/gameObject/id" } })],
    invariants: [{ left: "beforeY", operator: "equals", value: 5 }, { left: "completionY", operator: "approx", value: 4.996076, tolerance: 0.001 }, { left: "afterY", operator: "approx", right: "completionY", tolerance: 0.000001 }, { left: "beforeTargetId", operator: "equals", right: "afterTargetId" }],
    cleanup: [
      deleteTargetStep("delete-target"),
      step("restore-settings", "physics.settings", { autoSimulation: { $var: "priorAutoSimulation" }, simulationMode2D: { $var: "priorSimulationMode2D" }, gravity: { $var: "priorGravity" } }, { when: ["priorAutoSimulation", "priorSimulationMode2D", "priorGravity"] })
    ]
  },
  "navmesh.surface.bake": {
    setup: [
      step("create-surface", "go.create", { name: "${target}", x: 0, y: 0, z: 0 }),
      step("add-surface", "component.add", { target: "${target}", component: "Unity.AI.Navigation.NavMeshSurface", properties: {} }),
      step("create-floor", "go.create", { name: "${targetA}", primitive: "cube", x: 0, y: -0.5, z: 0, scaleX: 10, scaleY: 1, scaleZ: 10 }),
      step("save-prerequisites", "scene.save", {})
    ],
    before: [
      step("dry-plan", "navmesh.surface.bake", {}, {
        flags: ["--dry-run"],
        expect: { equals: { "/data/dryRun": true, "/data/kind": "surfaces", "/data/targetCount": 1, "/data/plannedEffects/0/targetCount": 1 }, exists: ["/data/scenes/0/path", "/data/targets/0/globalId"] },
        capture: { plannedScenePath: "/data/scenes/0/path", plannedSurfaceGlobalId: "/data/targets/0/globalId" }
      })
    ],
    invocation: step("invoke", "navmesh.surface.bake", {}, {
      expect: { equals: { "/data/kind": "surfaces", "/data/status": "completed", "/data/surfaces/0/name": "${target}", "/data/surfaces/0/sceneDirty": false }, exists: ["/data/operationId", "/data/scenes/0/path", "/data/surfaces/0/globalId", "/data/surfaces/0/dataPath", "/data/surfaces/0/dataGuid", "/data/surfaces/0/dataHash"] },
      capture: { operationId: "/data/operationId", invocationScenePath: "/data/scenes/0/path", surfaceGlobalId: "/data/surfaces/0/globalId", dataPath: "/data/surfaces/0/dataPath", dataGuid: "/data/surfaces/0/dataGuid" }
    }),
    after: [
      readStep("status", "navmesh.bake-status", {}, { expect: { equals: { "/data/state": "completed", "/data/active": false, "/data/kind": "surfaces" } }, capture: { statusOperationId: "/data/operationId" } }),
      readStep("data-asset", "asset.info", { path: { $var: "dataPath" } }, { expect: { equals: { "/data/asset/exists": true, "/data/asset/path": { $var: "dataPath" } } }, capture: { assetGuid: "/data/asset/guid" } })
    ],
    invariants: [
      { left: "plannedScenePath", operator: "equals", right: "invocationScenePath" },
      { left: "plannedSurfaceGlobalId", operator: "equals", right: "surfaceGlobalId" },
      { left: "operationId", operator: "equals", right: "statusOperationId" },
      { left: "dataGuid", operator: "equals", right: "assetGuid" }
    ],
    cleanup: [
      deleteTargetStep("delete-surface"),
      deleteTargetStep("delete-floor", "${targetA}"),
      step("delete-data", "asset.delete", { path: { $var: "dataPath" } }, { when: ["dataPath"], acceptErrorCodes: ["E_ASSET_NOT_FOUND"] }),
      step("save-cleanup", "scene.save", {})
    ]
  },
  "prefab.overrides.apply": {
    setup: [
      step("copy-base", "asset.duplicate", { source: "Assets/UnityCLIValidation/Matrix.prefab", destination: "${prefabPath}" }),
      step("instantiate", "prefab.instantiate", { path: "${prefabPath}", name: "${target}", x: 0, y: 0, z: 0 }),
      step("add-child-override", "go.create", { name: "${targetA}", parent: "${target}", x: 1, y: 2, z: 3 })
    ],
    before: [
      step("dry-plan", "prefab.overrides.apply", { instance: "${target}" }, {
        flags: ["--dry-run"],
        expect: { equals: { "/data/action": "apply", "/data/dryRun": true, "/data/changed": true, "/data/before/hasOverrides": true, "/data/after/hasOverrides": true, "/data/source/path": "${prefabPath}" } },
        capture: { beforeSourceGuid: "/data/source/guid", beforeSourceHash: "/data/sourceHashBefore" }
      })
    ],
    invocation: step("invoke", "prefab.overrides.apply", { instance: "${target}" }, {
      expect: { equals: { "/data/action": "apply", "/data/dryRun": false, "/data/changed": true, "/data/before/hasOverrides": true, "/data/after/hasOverrides": false, "/data/source/path": "${prefabPath}" } },
      capture: { appliedSourceGuid: "/data/source/guid", appliedSourceHashBefore: "/data/sourceHashBefore", appliedSourceHashAfter: "/data/sourceHashAfter" }
    }),
    after: [
      step("instantiate-proof", "prefab.instantiate", { path: "${prefabPath}", name: "${targetB}", x: 0, y: 0, z: 0 }),
      readStep("child-proof", "scene.gameobject", { target: "${targetB}/${targetA}", includeComponents: false }, { capture: { appliedChildId: "/data/gameObject/id", appliedChildX: "/data/gameObject/transform/position/x" } })
    ],
    invariants: [
      { left: "beforeSourceGuid", operator: "equals", right: "appliedSourceGuid" },
      { left: "beforeSourceHash", operator: "equals", right: "appliedSourceHashBefore" },
      { left: "appliedSourceHashBefore", operator: "notEquals", right: "appliedSourceHashAfter" },
      { left: "appliedChildX", operator: "equals", value: 1 }
    ],
    cleanup: [
      deleteTargetStep("delete-source-instance"),
      deleteTargetStep("delete-proof-instance", "${targetB}"),
      deleteAssetStep("delete-base", "${prefabPath}"),
      step("reload-scene", "scene.load", { path: "Assets/UnityCLIValidation/MatrixScene.unity" })
    ]
  },
  "prefab.overrides.revert": {
    setup: [
      step("copy-base", "asset.duplicate", { source: "Assets/UnityCLIValidation/Matrix.prefab", destination: "${prefabPath}" }),
      step("instantiate", "prefab.instantiate", { path: "${prefabPath}", name: "${target}", x: 0, y: 0, z: 0 }),
      step("add-child-override", "go.create", { name: "${targetA}", parent: "${target}", x: 1, y: 2, z: 3 })
    ],
    before: [
      step("dry-plan", "prefab.overrides.revert", { instance: "${target}" }, {
        flags: ["--dry-run"],
        expect: { equals: { "/data/action": "revert", "/data/dryRun": true, "/data/changed": true, "/data/before/hasOverrides": true, "/data/after/hasOverrides": true, "/data/source/path": "${prefabPath}" } },
        capture: { beforeSourceGuid: "/data/source/guid", beforeSourceHash: "/data/sourceHashBefore" }
      })
    ],
    invocation: step("invoke", "prefab.overrides.revert", { instance: "${target}" }, {
      expect: { equals: { "/data/action": "revert", "/data/dryRun": false, "/data/changed": true, "/data/before/hasOverrides": true, "/data/after/hasOverrides": false, "/data/source/path": "${prefabPath}" } },
      capture: { revertedSourceGuid: "/data/source/guid", revertedSourceHashBefore: "/data/sourceHashBefore", revertedSourceHashAfter: "/data/sourceHashAfter" }
    }),
    after: [
      readStep("child-removed", "scene.gameobject", { target: "${target}/${targetA}", includeComponents: false }, { expectedErrorCode: "E_TARGET_NOT_FOUND" }),
      readStep("instance-proof", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { revertedInstanceId: "/data/gameObject/id" } })
    ],
    invariants: [
      { left: "beforeSourceGuid", operator: "equals", right: "revertedSourceGuid" },
      { left: "beforeSourceHash", operator: "equals", right: "revertedSourceHashBefore" },
      { left: "revertedSourceHashBefore", operator: "equals", right: "revertedSourceHashAfter" }
    ],
    cleanup: [
      deleteTargetStep("delete-instance"),
      deleteAssetStep("delete-base", "${prefabPath}"),
      step("reload-scene", "scene.load", { path: "Assets/UnityCLIValidation/MatrixScene.unity" })
    ]
  },
  "prefab.variant.create": {
    before: [
      readStep("variant-absent", "asset.info", { path: "${variantPath}" }, { expectedErrorCode: "E_ASSET_NOT_FOUND" })
    ],
    preflight: [
      step("dry-plan", "prefab.variant.create", { base: "Assets/UnityCLIValidation/Matrix.prefab", path: "${variantPath}" }, { flags: ["--dry-run"], expect: { equals: { "/data/dryRun": true, "/data/created": false, "/data/base/path": "Assets/UnityCLIValidation/Matrix.prefab", "/data/variant/path": "${variantPath}" } } })
    ],
    invocation: step("invoke", "prefab.variant.create", { base: "Assets/UnityCLIValidation/Matrix.prefab", path: "${variantPath}" }, {
      expect: { equals: { "/data/dryRun": false, "/data/created": true, "/data/base/path": "Assets/UnityCLIValidation/Matrix.prefab", "/data/variant/path": "${variantPath}", "/data/variant/isVariant": true } },
      capture: { createdVariantGuid: "/data/variant/guid", createdBaseGuid: "/data/base/guid" }
    }),
    after: [
      readStep("variant-proof", "prefab.info", { path: "${variantPath}" }, { expect: { equals: { "/data/prefab/path": "${variantPath}", "/data/prefab/isVariant": true, "/data/prefab/variant/basePath": "Assets/UnityCLIValidation/Matrix.prefab" } }, capture: { observedVariantGuid: "/data/prefab/guid", observedBaseGuid: "/data/prefab/variant/baseGuid" } })
    ],
    invariants: [
      { left: "createdVariantGuid", operator: "equals", right: "observedVariantGuid" },
      { left: "createdBaseGuid", operator: "equals", right: "observedBaseGuid" }
    ],
    cleanup: [deleteAssetStep("delete-variant", "${variantPath}")]
  },
  "timeline.create": {
    before: [
      readStep("timeline-absent", "asset.info", { path: "${timelinePath}" }, { expectedErrorCode: "E_ASSET_NOT_FOUND" })
    ],
    preflight: [
      step("dry-plan", "timeline.create", { path: "${timelinePath}", frameRate: 30, overwrite: false }, { flags: ["--dry-run"], expect: { equals: { "/data/dryRun": true, "/data/created": false, "/data/timeline/path": "${timelinePath}", "/data/timeline/frameRate": 30 } } })
    ],
    invocation: step("invoke", "timeline.create", { path: "${timelinePath}", frameRate: 30, overwrite: false }, {
      expect: { equals: { "/data/dryRun": false, "/data/created": true, "/data/timeline/path": "${timelinePath}", "/data/timeline/frameRate": 30, "/data/timeline/trackCount": 0 } },
      capture: { createdTimelineGuid: "/data/timeline/guid" }
    }),
    after: [
      readStep("timeline-proof", "timeline.info", { timeline: "${timelinePath}", pageSize: 10, maxDepth: 2, maxClipsPerTrack: 10 }, { expect: { equals: { "/data/timeline/path": "${timelinePath}", "/data/timeline/frameRate": 30, "/data/timeline/trackCount": 0, "/data/pageInfo/total": 0 } }, capture: { observedTimelineGuid: "/data/timeline/guid" } })
    ],
    invariants: [{ left: "createdTimelineGuid", operator: "equals", right: "observedTimelineGuid" }],
    cleanup: [deleteAssetStep("delete-timeline", "${timelinePath}")]
  },
  "timeline.info": {
    setup: [
      step("create-timeline", "timeline.create", { path: "${timelinePath}", frameRate: 24, overwrite: false }),
      step("add-track", "timeline.track.add", { timeline: "${timelinePath}", trackType: "Activation", name: "${target}" })
    ],
    before: [
      readStep("asset-before", "asset.info", { path: "${timelinePath}" }, { expect: { equals: { "/data/asset/exists": true, "/data/asset/path": "${timelinePath}" } }, capture: { beforeTimelineGuid: "/data/asset/guid" } })
    ],
    invocation: step("invoke", "timeline.info", { timeline: "${timelinePath}", pageSize: 10, maxDepth: 2, maxClipsPerTrack: 10 }, {
      expect: { equals: { "/data/timeline/path": "${timelinePath}", "/data/timeline/trackCount": 1, "/data/pageInfo/total": 1, "/data/tracks/0/name": "${target}", "/data/tracks/0/trackType": "Activation", "/data/tracks/0/clipCount": 0 } },
      capture: { infoTimelineGuid: "/data/timeline/guid", infoTrackLocalId: "/data/tracks/0/identity/localId" }
    }),
    after: [
      readStep("asset-after", "asset.info", { path: "${timelinePath}" }, { expect: { equals: { "/data/asset/exists": true, "/data/asset/path": "${timelinePath}" } }, capture: { afterTimelineGuid: "/data/asset/guid" } })
    ],
    invariants: [
      { left: "beforeTimelineGuid", operator: "equals", right: "infoTimelineGuid" },
      { left: "infoTimelineGuid", operator: "equals", right: "afterTimelineGuid" }
    ],
    cleanup: [deleteAssetStep("delete-timeline", "${timelinePath}")]
  },
  "timeline.track.add": {
    setup: [step("create-timeline", "timeline.create", { path: "${timelinePath}", frameRate: 24, overwrite: false })],
    before: [
      readStep("before-info", "timeline.info", { timeline: "${timelinePath}", pageSize: 10, maxDepth: 2, maxClipsPerTrack: 10 }, { expect: { equals: { "/data/timeline/trackCount": 0, "/data/pageInfo/total": 0 } }, capture: { beforeTimelineGuid: "/data/timeline/guid" } })
    ],
    preflight: [
      step("dry-plan", "timeline.track.add", { timeline: "${timelinePath}", trackType: "Activation", name: "${target}" }, { flags: ["--dry-run"], expect: { equals: { "/data/dryRun": true, "/data/added": false, "/data/track/name": "${target}", "/data/track/trackType": "Activation" } } })
    ],
    invocation: step("invoke", "timeline.track.add", { timeline: "${timelinePath}", trackType: "Activation", name: "${target}" }, {
      expect: { equals: { "/data/dryRun": false, "/data/added": true, "/data/track/name": "${target}", "/data/track/trackType": "Activation", "/data/timeline/trackCount": 1 } },
      capture: { addedTimelineGuid: "/data/timeline/guid", addedTrackLocalId: "/data/track/identity/localId" }
    }),
    after: [
      readStep("after-info", "timeline.info", { timeline: "${timelinePath}", pageSize: 10, maxDepth: 2, maxClipsPerTrack: 10 }, { expect: { equals: { "/data/timeline/trackCount": 1, "/data/pageInfo/total": 1, "/data/tracks/0/name": "${target}" } }, capture: { afterTimelineGuid: "/data/timeline/guid", afterTrackLocalId: "/data/tracks/0/identity/localId" } })
    ],
    invariants: [
      { left: "beforeTimelineGuid", operator: "equals", right: "addedTimelineGuid" },
      { left: "addedTimelineGuid", operator: "equals", right: "afterTimelineGuid" },
      { left: "addedTrackLocalId", operator: "equals", right: "afterTrackLocalId" }
    ],
    cleanup: [deleteAssetStep("delete-timeline", "${timelinePath}")]
  },
  "timeline.clip.add": {
    setup: [
      step("create-timeline", "timeline.create", { path: "${timelinePath}", frameRate: 24, overwrite: false }),
      step("add-track", "timeline.track.add", { timeline: "${timelinePath}", trackType: "Activation", name: "${target}" }, { capture: { trackLocalId: "/data/track/identity/localId" } })
    ],
    before: [
      readStep("before-info", "timeline.info", { timeline: "${timelinePath}", pageSize: 10, maxDepth: 2, maxClipsPerTrack: 10 }, { expect: { equals: { "/data/timeline/trackCount": 1, "/data/tracks/0/clipCount": 0 } }, capture: { beforeTimelineGuid: "/data/timeline/guid", beforeTrackLocalId: "/data/tracks/0/identity/localId" } })
    ],
    preflight: [
      step("dry-plan", "timeline.clip.add", { timeline: "${timelinePath}", trackLocalId: { $var: "trackLocalId" }, start: 0, duration: 2.5 }, { flags: ["--dry-run"], expect: { equals: { "/data/dryRun": true, "/data/added": false, "/data/clip/start": 0, "/data/clip/duration": 2.5 } } })
    ],
    invocation: step("invoke", "timeline.clip.add", { timeline: "${timelinePath}", trackLocalId: { $var: "trackLocalId" }, start: 0, duration: 2.5 }, {
      expect: { equals: { "/data/dryRun": false, "/data/added": true, "/data/clip/start": 0, "/data/clip/duration": 2.5, "/data/clip/track/name": "${target}", "/data/timeline/trackCount": 1 } },
      capture: { addedTimelineGuid: "/data/timeline/guid", addedTrackLocalId: "/data/clip/track/identity/localId", addedClipDuration: "/data/clip/duration" }
    }),
    after: [
      readStep("after-info", "timeline.info", { timeline: "${timelinePath}", pageSize: 10, maxDepth: 2, maxClipsPerTrack: 10 }, { expect: { equals: { "/data/tracks/0/clipCount": 1, "/data/tracks/0/clips/0/start": 0, "/data/tracks/0/clips/0/duration": 2.5 } }, capture: { afterTimelineGuid: "/data/timeline/guid", afterTrackLocalId: "/data/tracks/0/identity/localId", afterClipDuration: "/data/tracks/0/clips/0/duration" } })
    ],
    invariants: [
      { left: "beforeTimelineGuid", operator: "equals", right: "addedTimelineGuid" },
      { left: "addedTimelineGuid", operator: "equals", right: "afterTimelineGuid" },
      { left: "beforeTrackLocalId", operator: "equals", right: "addedTrackLocalId" },
      { left: "addedTrackLocalId", operator: "equals", right: "afterTrackLocalId" },
      { left: "addedClipDuration", operator: "equals", right: "afterClipDuration" }
    ],
    cleanup: [deleteAssetStep("delete-timeline", "${timelinePath}")]
  },
  "reflection.method.call": {
    setup: [step("create-target", "go.create", { name: "${target}", x: 0, y: 0, z: 0 })],
    before: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { beforeX: "/data/gameObject/transform/position/x", beforeTargetId: "/data/gameObject/id" } })],
    preflight: [
      step("dry-run", "reflection.method.call", {
        typeName: "UnityEngine.Transform", methodName: "Translate", parameterTypes: ["UnityEngine.Vector3", "UnityEngine.Space"], args: [{ x: 1, y: 0, z: 0 }, "World"], instanceRef: { path: "${target}" }, completionProbe: completionProbe("${target}", "x", 0, 1)
      }, { flags: ["--dry-run"], expect: { equals: { "/data/typeName": "UnityEngine.Transform", "/data/methodName": "Translate" } } })
    ],
    invocation: step("invoke", "reflection.method.call", {
      typeName: "UnityEngine.Transform", methodName: "Translate", parameterTypes: ["UnityEngine.Vector3", "UnityEngine.Space"], args: [{ x: 1, y: 0, z: 0 }, "World"], instanceRef: { path: "${target}" }, completionProbe: completionProbe("${target}", "x", 0, 1)
    }, { expect: { equals: { "/data/completion/assertions/0/value": 1 } }, capture: { completionX: "/data/completion/assertions/0/value" } }),
    after: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { afterX: "/data/gameObject/transform/position/x", afterTargetId: "/data/gameObject/id" } })],
    invariants: [{ left: "beforeX", operator: "equals", value: 0 }, { left: "completionX", operator: "equals", value: 1 }, { left: "afterX", operator: "equals", value: 1 }, { left: "beforeTargetId", operator: "equals", right: "afterTargetId" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "scene.view-frame": {
    setup: [
      step("create-anchor", "go.create", { name: "${anchor}", primitive: "cube", x: 0, y: 0, z: 0 }),
      step("frame-anchor", "scene.view-frame", { target: "${anchor}" }),
      step("create-target", "go.create", { name: "${target}", primitive: "cube", x: 10000, y: 10000, z: 10000 })
    ],
    before: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { beforeTargetId: "/data/gameObject/id", beforePosition: "/data/gameObject/transform/position" } })],
    invocation: step("invoke", "scene.view-frame", { target: "${target}" }, { expect: { equals: { "/data/framed": true, "/data/changed": true, "/data/view/boundsContained": true }, exists: ["/data/view/pivot", "/data/view/rotation", "/data/view/size"] } }),
    after: [readStep("target", "scene.gameobject", { target: "${target}", includeComponents: false }, { capture: { afterTargetId: "/data/gameObject/id", afterPosition: "/data/gameObject/transform/position" } })],
    invariants: [{ left: "beforeTargetId", operator: "equals", right: "afterTargetId" }, { left: "beforePosition", operator: "deepEquals", right: "afterPosition" }],
    cleanup: [deleteTargetStep("delete-target"), deleteTargetStep("delete-anchor", "${anchor}")]
  },
  "probuilder.append-vertices-to-edge": {
    setup: [step("create-cube", "probuilder.create-shape", { shapeType: "Cube", name: "${target}", x: 0, y: 0, z: 0 })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.append-vertices-to-edge", { target: "${target}", edgeIndices: [0], count: 1 }, { expect: { exists: ["/data/insertedCount"] } }),
    after: [meshInfoStep("mesh", "after"), validateMeshStep()],
    invariants: [{ left: "afterVertexCount", operator: "greaterThan", right: "beforeVertexCount" }, { left: "afterEdgeCount", operator: "greaterThan", right: "beforeEdgeCount" }, { left: "afterFaceCount", operator: "equals", right: "beforeFaceCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.auto-smooth": {
    setup: [step("create-plane", "probuilder.create-shape", { shapeType: "Plane", name: "${target}", x: 0, y: 0, z: 0 })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.auto-smooth", { target: "${target}", angleThreshold: 30 }, { expect: { equals: { "/data/changed": true }, exists: ["/data/smoothedFaceCount"] } }),
    after: [meshInfoStep("mesh", "after"), validateMeshStep()],
    invariants: [{ left: "afterVertexCount", operator: "equals", right: "beforeVertexCount" }, { left: "afterEdgeCount", operator: "equals", right: "beforeEdgeCount" }, { left: "afterFaceCount", operator: "equals", right: "beforeFaceCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.bevel-edges": {
    setup: [
      step("create-a", "probuilder.create-poly-shape", { points: trianglePoints, flipNormals: false, name: "${targetA}" }),
      step("create-b", "probuilder.create-poly-shape", { points: [{ x: 1, y: 0, z: 1 }, { x: 0, y: 0, z: 0 }, { x: -1, y: 0, z: 1 }], flipNormals: false, name: "${targetB}" }),
      manualUvStep("manual-uv-a", "${targetA}"),
      manualUvStep("manual-uv-b", "${targetB}"),
      step("combine", "probuilder.merge-objects", { targets: ["${targetA}", "${targetB}"], name: "${target}" }),
      step("merge-shared-a", "probuilder.merge-vertices", { target: "${target}", vertexIndices: [1, 4], collapseToFirst: false }),
      step("merge-shared-b", "probuilder.merge-vertices", { target: "${target}", vertexIndices: [2, 3], collapseToFirst: false }),
      step("form-wedge", "probuilder.move-vertices", { target: "${target}", vertexIndices: [5], x: 0, y: 1, z: 0 })
    ],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.bevel-edges", { target: "${target}", edges: [{ a: 1, b: 2 }], amount: 0.1 }, { expect: { equals: { "/data/modifiedEdgeCount": 1 } } }),
    after: [meshInfoStep("mesh", "after"), validateMeshStep()],
    invariants: [{ left: "afterVertexCount", operator: "greaterThan", right: "beforeVertexCount" }, { left: "afterEdgeCount", operator: "greaterThan", right: "beforeEdgeCount" }, { left: "afterFaceCount", operator: "greaterThan", right: "beforeFaceCount" }],
    cleanup: [deleteTargetStep("delete-target"), deleteTargetStep("delete-a", "${targetA}"), deleteTargetStep("delete-b", "${targetB}")]
  },
  "probuilder.bridge-edges": {
    setup: [step("create-cube", "probuilder.create-shape", { shapeType: "Cube", name: "${target}", x: 0, y: 0, z: 0 })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.bridge-edges", { target: "${target}", edgeA: { a: 0, b: 1 }, edgeB: { a: 5, b: 7 }, allowNonManifold: true }, { expect: { exists: ["/data/newFaceCount"] } }),
    after: [meshInfoStep("mesh", "after"), validateMeshStep()],
    invariants: [{ left: "afterFaceCount", operator: "deltaEquals", right: "beforeFaceCount", value: 1 }, { left: "afterVertexCount", operator: "greaterThan", right: "beforeVertexCount" }, { left: "afterEdgeCount", operator: "greaterThan", right: "beforeEdgeCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.connect-elements": {
    setup: [step("create-triangle", "probuilder.create-poly-shape", { points: trianglePoints, flipNormals: false, name: "${target}" })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.connect-elements", { target: "${target}", faceIndices: [0] }, { expect: { equals: { "/data/connectedCount": 1 } } }),
    after: [meshInfoStep("mesh", "after"), validateMeshStep()],
    invariants: [{ left: "beforeFaceCount", operator: "equals", value: 1 }, { left: "afterFaceCount", operator: "equals", value: 3 }, { left: "afterVertexCount", operator: "greaterThan", right: "beforeVertexCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.create-shape": {
    before: [readStep("absent", "scene.gameobject", { target: "${target}", includeComponents: false }, { expectedErrorCode: "E_TARGET_NOT_FOUND" })],
    invocation: step("invoke", "probuilder.create-shape", { shapeType: "CurvedStair", name: "${target}", width: 2, height: 3, depth: 4, x: 7, y: 8, z: 9 }, { expect: { equals: { "/data/object/name": "${target}", "/data/object/hasProBuilderMesh": true }, exists: ["/data/object/id", "/data/object/path"] }, capture: { createdTargetId: "/data/object/id", createdTargetPath: "/data/object/path" } }),
    after: [
      readStep("identity", "scene.gameobject", { target: { $var: "createdTargetPath" }, includeComponents: false }, { capture: { afterTargetId: "/data/gameObject/id", afterPosition: "/data/gameObject/transform/position" } }),
      meshInfoStep("mesh", "after", { $var: "createdTargetPath" }),
      validateMeshStep({ $var: "createdTargetPath" })
    ],
    invariants: [{ left: "createdTargetId", operator: "equals", right: "afterTargetId" }, { left: "afterPosition", operator: "deepEquals", value: { x: 7, y: 8, z: 9 } }, { left: "afterVertexCount", operator: "greaterThan", value: 0 }, { left: "afterFaceCount", operator: "greaterThan", value: 0 }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.duplicate-and-flip": {
    setup: [step("create-triangle", "probuilder.create-poly-shape", { points: trianglePoints, flipNormals: false, name: "${target}" })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.duplicate-and-flip", { target: "${target}", faceIndices: [0] }, { expect: { equals: { "/data/duplicatedCount": 1 } } }),
    after: [meshInfoStep("mesh", "after"), validateMeshStep()],
    invariants: [{ left: "afterFaceCount", operator: "deltaEquals", right: "beforeFaceCount", value: 1 }, { left: "afterVertexCount", operator: "greaterThan", right: "beforeVertexCount" }, { left: "afterEdgeCount", operator: "greaterThan", right: "beforeEdgeCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.extrude-edges": {
    setup: [step("create-triangle", "probuilder.create-poly-shape", { points: trianglePoints, flipNormals: false, name: "${target}" })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.extrude-edges", { target: "${target}", edges: [{ a: 0, b: 1 }], distance: 0.5, asGroup: true }, { expect: { equals: { "/data/modifiedEdgeCount": 1 } } }),
    after: [meshInfoStep("mesh", "after"), validateMeshStep()],
    invariants: [{ left: "afterFaceCount", operator: "deltaEquals", right: "beforeFaceCount", value: 1 }, { left: "afterVertexCount", operator: "greaterThan", right: "beforeVertexCount" }, { left: "afterEdgeCount", operator: "greaterThan", right: "beforeEdgeCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.extrude-faces": {
    setup: [step("create-triangle", "probuilder.create-poly-shape", { points: trianglePoints, flipNormals: false, name: "${target}" })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.extrude-faces", { target: "${target}", faceIndices: [0], distance: 0.5, method: "faceNormal" }, { expect: { equals: { "/data/modifiedFaceCount": 1, "/data/newVertexCount": 12 } } }),
    after: [meshInfoStep("mesh", "after"), validateMeshStep()],
    invariants: [{ left: "afterFaceCount", operator: "greaterThan", right: "beforeFaceCount" }, { left: "afterVertexCount", operator: "greaterThan", right: "beforeVertexCount" }, { left: "afterEdgeCount", operator: "greaterThan", right: "beforeEdgeCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.flip-normals": {
    setup: [step("create-triangle", "probuilder.create-poly-shape", { points: trianglePoints, flipNormals: false, name: "${target}" })],
    before: [
      meshInfoStep("mesh", "before"),
      step("up-faces", "probuilder.select-faces", { target: "${target}", direction: "up", flood: false, loop: false, ring: false }, { expect: { contains: { "/data/faceIndices": 0 } } })
    ],
    invocation: step("invoke", "probuilder.flip-normals", { target: "${target}", faceIndices: [0] }, { expect: { equals: { "/data/flippedCount": 1 } } }),
    after: [
      meshInfoStep("mesh", "after"),
      step("down-faces", "probuilder.select-faces", { target: "${target}", direction: "down", flood: false, loop: false, ring: false }, { expect: { contains: { "/data/faceIndices": 0 } } }),
      validateMeshStep()
    ],
    invariants: [{ left: "afterVertexCount", operator: "equals", right: "beforeVertexCount" }, { left: "afterEdgeCount", operator: "equals", right: "beforeEdgeCount" }, { left: "afterFaceCount", operator: "equals", right: "beforeFaceCount" }, { left: "afterBounds", operator: "deepEquals", right: "beforeBounds" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.insert-vertex": {
    setup: [step("create-triangle", "probuilder.create-poly-shape", { points: trianglePoints, flipNormals: false, name: "${target}" })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.insert-vertex", { target: "${target}", edge: { a: 1, b: 2 }, x: 0.25, y: 0, z: 0.25 }, { expect: { equals: { "/data/inserted": true }, exists: ["/data/vertexCountBefore", "/data/vertexCountAfter"] } }),
    after: [meshInfoStep("mesh", "after"), validateMeshStep()],
    invariants: [{ left: "afterVertexCount", operator: "greaterThan", right: "beforeVertexCount" }, { left: "afterEdgeCount", operator: "greaterThan", right: "beforeEdgeCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.merge-objects": {
    setup: [
      step("create-a", "probuilder.create-poly-shape", { points: trianglePoints, flipNormals: false, name: "${targetA}" }),
      step("create-b", "probuilder.create-poly-shape", { points: [{ x: 5, y: 0, z: 0 }, { x: 3, y: 0, z: 0 }, { x: 4, y: 0, z: 1 }], flipNormals: false, name: "${targetB}" }),
      manualUvStep("manual-uv-a", "${targetA}"),
      manualUvStep("manual-uv-b", "${targetB}")
    ],
    before: [meshInfoStep("mesh-a", "beforeA", "${targetA}"), meshInfoStep("mesh-b", "beforeB", "${targetB}")],
    invocation: step("invoke", "probuilder.merge-objects", { targets: ["${targetA}", "${targetB}"], name: "${resultName}" }, { expect: { equals: { "/data/mergedObject/name": "${resultName}" }, exists: ["/data/mergedObject/id", "/data/mergedObject/path"] }, capture: { mergedTargetId: "/data/mergedObject/id", mergedTargetPath: "/data/mergedObject/path" } }),
    after: [
      readStep("identity", "scene.gameobject", { target: { $var: "mergedTargetPath" }, includeComponents: false }, { capture: { afterTargetId: "/data/gameObject/id" } }),
      readStep("source-removed", "scene.gameobject", { target: "${targetB}", includeComponents: false }, { expectedErrorCode: "E_TARGET_NOT_FOUND" }),
      meshInfoStep("mesh", "after", { $var: "mergedTargetPath" }),
      validateMeshStep({ $var: "mergedTargetPath" })
    ],
    invariants: [{ left: "mergedTargetId", operator: "equals", right: "afterTargetId" }, { left: "afterVertexCount", operator: "sumEquals", operands: ["beforeAVertexCount", "beforeBVertexCount"] }, { left: "afterFaceCount", operator: "sumEquals", operands: ["beforeAFaceCount", "beforeBFaceCount"] }],
    cleanup: [deleteTargetStep("delete-result", "${resultName}"), deleteTargetStep("delete-a", "${targetA}"), deleteTargetStep("delete-b", "${targetB}")]
  },
  "probuilder.merge-vertices": {
    setup: [step("create-triangle", "probuilder.create-poly-shape", { points: trianglePoints, flipNormals: false, name: "${target}" })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.merge-vertices", { target: "${target}", vertexIndices: [0, 1], collapseToFirst: false }, { expect: { equals: { "/data/changed": true, "/data/sourceVertexCount": 2 }, exists: ["/data/mergedToIndex"] } }),
    after: [meshInfoStep("mesh", "after")],
    invariants: [{ left: "afterVertexCount", operator: "lessThan", right: "beforeVertexCount" }, { left: "afterFaceCount", operator: "lessThan", right: "beforeFaceCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.subdivide": {
    setup: [step("create-triangle", "probuilder.create-poly-shape", { points: trianglePoints, flipNormals: false, name: "${target}" })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.subdivide", { target: "${target}", faceIndices: [0] }, { expect: { equals: { "/data/beforeFaceCount": 1, "/data/afterFaceCount": 3 } } }),
    after: [meshInfoStep("mesh", "after"), validateMeshStep()],
    invariants: [{ left: "beforeFaceCount", operator: "equals", value: 1 }, { left: "afterFaceCount", operator: "equals", value: 3 }, { left: "afterVertexCount", operator: "greaterThan", right: "beforeVertexCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  },
  "probuilder.weld-vertices": {
    setup: [step("create-close-triangle", "probuilder.create-poly-shape", { points: closeTrianglePoints, flipNormals: false, name: "${target}" })],
    before: [meshInfoStep("mesh", "before")],
    invocation: step("invoke", "probuilder.weld-vertices", { target: "${target}", vertexIndices: [0, 1], radius: 0.01 }, { expect: { equals: { "/data/changed": true }, exists: ["/data/weldedCount"] } }),
    after: [meshInfoStep("mesh", "after")],
    invariants: [{ left: "afterVertexCount", operator: "lessThan", right: "beforeVertexCount" }, { left: "afterFaceCount", operator: "lessThan", right: "beforeFaceCount" }],
    cleanup: [deleteTargetStep("delete-target")]
  }
});

/** 隔离项目调用必须同步重写业务 project 字段，禁止 example 绕回主 Dev 项目。 */
export function routeCapabilityInput(input, project, primaryProject) {
  if (!plain(input) || resolve(project) === resolve(primaryProject)) return input;
  const routed = { ...input };
  for (const key of ["project", "projectPath"]) if (typeof routed[key] === "string") routed[key] = project;
  return routed;
}

/** 本地 docs/reflect 示例绑定本次 evidence 内的确定性只读 cache。 */
export function routeCapabilityFixtureInput(input, name, options) {
  if (!plain(input)) return input;
  if (["docs.get", "docs.search"].includes(name) && typeof options?.docs_cache_dir === "string")
    return { ...input, docsDir: options.docs_cache_dir };
  if (["reflect.member", "reflect.search", "reflect.type"].includes(name) && typeof options?.reflect_cache_dir === "string")
    return { ...input, reflectDir: options.reflect_cache_dir };
  if ([
    "package.registry.clear", "package.registry.set",
    "package.scoped-registry.add", "package.scoped-registry.remove"
  ].includes(name) && typeof options?.project === "string" && !Object.hasOwn(input, "project")) return { ...input, project: options.project };
  return input;
}

/** editor.play cleanup失败必须终止coverage；超时后是否重试由调用方决定。 */
async function cleanupEditorPlay(m, caseId, name, phase, record) {
  if (name !== "editor.play" || phase !== "editor" || record.envelope?.ok !== true) return null;
  const descriptor = await getDescriptor(m, "tool", "editor.stop");
  let input = {};
  try { input = parsePublicExample(descriptor?.examples?.[0], "tool", "editor.stop").input; } catch { }
  const stopArgv = argvFor("tool", "editor.stop", input);
  const stopRecord = await m.call(`${caseId}.cleanup.stop`, stopArgv, (e, r) => r.exitCode === 0 ? assertSuccess(e, descriptor?.resultSchema ?? {}) : "editor.stop cleanup failed.", { cleanup: { sourceCase: caseId, session: "edit-mode" } });
  const readyRecord = await m.call(`${caseId}.cleanup.ready`, ["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], (e, r) => {
    return r.exitCode === 0 && isReadyEditorState(e, false) ? null : "Editor did not return to ready Edit Mode after cleanup.";
  }, { cleanup: { sourceCase: caseId, session: "edit-mode-ready" }, retryAssertion: true });
  if (stopRecord.assertion?.status !== "PASS") return stopRecord;
  return readyRecord.assertion?.status === "PASS" ? null : readyRecord;
}

/**
 * package.embed 只允许清理本次成功响应声明的精确 Packages/<packageId> 目录。
 * 路径逃逸、ID 欺骗或非 project-relative 业务输出一律在任何文件删除前失败。
 */
export function ownedEmbeddedPackageCleanupTarget(project, packageResult) {
  if (typeof project !== "string" || project.length === 0 || !plain(packageResult)) throw new Error("package.embed cleanup requires a project and terminal result.");
  const packageId = packageResult.packageId;
  const embeddedPath = packageResult.embeddedPath;
  if (typeof packageId !== "string" || !/^[a-z0-9][a-z0-9._-]*$/.test(packageId)) throw new Error("package.embed cleanup returned an invalid package id.");
  if (typeof embeddedPath !== "string" || embeddedPath.length === 0) throw new Error("package.embed cleanup returned no embedded path.");
  const expectedPublicPath = `Packages/${packageId}`;
  if (embeddedPath !== expectedPublicPath) throw new Error(`package.embed cleanup path is not its exact project-relative package directory: ${embeddedPath}`);
  return { packageId, embeddedPath: resolve(project, "Packages", packageId), publicPath: expectedPublicPath };
}

function slashPath(value) { return value.replaceAll("\\", "/"); }

/** 将active scene限定到项目Assets下对应的Unity lighting目录。 */
export function sceneBakeArtifactDirectory(project, scenePath) {
  if (typeof project !== "string" || project.length === 0 || typeof scenePath !== "string" || !/^Assets\/.+\.unity$/i.test(scenePath))
    throw new Error("graphics.bake-start cleanup requires an active Assets/*.unity scene.");
  const projectRoot = resolve(project);
  const sceneAbsolute = resolve(projectRoot, ...scenePath.replaceAll("\\", "/").split("/"));
  const relativeScene = relative(projectRoot, sceneAbsolute);
  if (relativeScene.startsWith(`..${sep}`) || relativeScene === ".." || isAbsolute(relativeScene)) throw new Error("Scene path escapes the selected project.");
  const publicScene = slashPath(relativeScene);
  if (!publicScene.startsWith("Assets/") || publicScene.split("/").includes("..")) throw new Error("Scene path is outside Assets/.");
  const absoluteDirectory = resolve(dirname(sceneAbsolute), basename(sceneAbsolute, extname(sceneAbsolute)));
  const publicDirectory = slashPath(relative(projectRoot, absoluteDirectory));
  if (!publicDirectory.startsWith("Assets/") || publicDirectory.split("/").includes("..")) throw new Error("Scene lighting directory is outside Assets/.");
  return { projectRoot, sceneAbsolute, absoluteDirectory, publicDirectory };
}

/** 所有读写根及其祖先都必须是普通目录，禁止junction/symlink把cleanup导向owner外。 */
export async function assertNoSymbolicAncestor(target, ownerRoot, label) {
  const root = resolve(ownerRoot);
  let current = resolve(target);
  const ownedRelative = relative(root, current);
  if (ownedRelative.startsWith(`..${sep}`) || ownedRelative === ".." || isAbsolute(ownedRelative)) throw new Error(`${label} escapes its owner root.`);
  while (true) {
    try {
      const metadata = await lstat(current);
      if (metadata.isSymbolicLink()) throw new Error(`${label} contains a symbolic link or junction: ${current}`);
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
    }
    if (current === root) break;
    const parent = dirname(current);
    if (parent === current) throw new Error(`${label} ancestor walk escaped its owner root.`);
    current = parent;
  }
}

function projectRelativePath(projectRoot, absolutePath, label) {
  const value = relative(resolve(projectRoot), resolve(absolutePath));
  if (!value || value === "." || value === ".." || value.startsWith(`..${sep}`) || isAbsolute(value)) throw new Error(`${label} is not a project child.`);
  return value.split(sep).join("/");
}
function comparePath(left, right) { return left.path < right.path ? -1 : left.path > right.path ? 1 : 0; }
function sha256Bytes(bytes) { return createHash("sha256").update(bytes).digest("hex"); }
function ownedProjectTreeHash(files, directories) {
  const records = [
    ...directories.map((path) => `D\0${path}\n`),
    ...files.map((file) => file.exists
      ? `F\0${file.path}\0${file.sha256}\0${file.sizeBytes}\n`
      : `M\0${file.path}\n`)
  ].sort();
  return createHash("sha256").update(records.join(""), "utf8").digest("hex");
}
function ownedProjectBaselineDigest(document) {
  return createHash("sha256").update(JSON.stringify({
    schemaVersion: document.schemaVersion,
    project: document.project,
    evidenceDir: document.evidenceDir,
    target: document.target,
    runId: document.runId,
    buildStamp: document.buildStamp,
    treeRoots: document.treeRoots,
    explicitFiles: document.explicitFiles,
    treeHash: document.treeHash,
    backupTreeHash: document.backupTreeHash,
    files: document.files,
    directories: document.directories
  }), "utf8").digest("hex");
}
function assertOwnedProjectBaselineDigest(snapshot, document) {
  const actual = ownedProjectBaselineDigest(document);
  if (document.baselineDigest !== actual) throw new Error("Owned project snapshot baseline digest is invalid.");
  if (snapshot.baselineDigest && snapshot.baselineDigest !== actual) throw new Error("Owned project snapshot immutable baseline changed during the run.");
  snapshot.baselineDigest = actual;
}
async function pathState(path) {
  try { return await lstat(path); }
  catch (error) { if (error?.code === "ENOENT") return undefined; throw error; }
}

/** 枚举授权 Dev project 闭包；任何 reparse、特殊文件或大小写碰撞都 fail closed。 */
async function captureOwnedProjectState(project) {
  const projectRoot = resolve(project);
  await assertNoSymbolicAncestor(projectRoot, projectRoot, "Owned project root");
  const files = [];
  const directories = [];
  const canonicalPaths = new Map();
  const addPath = (path, kind) => {
    const key = path.toLowerCase();
    const old = canonicalPaths.get(key);
    if (old && old !== path) throw new Error(`Owned project ${kind} has a case-insensitive path collision: ${old} / ${path}`);
    canonicalPaths.set(key, path);
  };
  const walk = async (absoluteDirectory) => {
    const relativeDirectory = projectRelativePath(projectRoot, absoluteDirectory, "Owned project directory");
    addPath(relativeDirectory, "directory");
    directories.push(relativeDirectory);
    const children = await readdir(absoluteDirectory, { withFileTypes: true });
    children.sort((left, right) => left.name.localeCompare(right.name));
    for (const child of children) {
      const absoluteChild = resolve(absoluteDirectory, child.name);
      await assertNoSymbolicAncestor(absoluteChild, projectRoot, "Owned project entry");
      const metadata = await lstat(absoluteChild);
      const relativeChild = projectRelativePath(projectRoot, absoluteChild, "Owned project entry");
      if (metadata.isSymbolicLink()) throw new Error(`Owned project entry contains a symbolic link or junction: ${relativeChild}`);
      if (metadata.isDirectory()) { await walk(absoluteChild); continue; }
      if (!metadata.isFile()) throw new Error(`Owned project entry is not a regular file: ${relativeChild}`);
      addPath(relativeChild, "file");
      const bytes = await readFile(absoluteChild);
      files.push({ path: relativeChild, exists: true, sizeBytes: bytes.length, sha256: sha256Bytes(bytes) });
    }
  };
  for (const root of OWNED_PROJECT_TREE_ROOTS) {
    const absoluteRoot = resolve(projectRoot, ...root.split("/"));
    await assertNoSymbolicAncestor(absoluteRoot, projectRoot, "Owned project tree root");
    const metadata = await pathState(absoluteRoot);
    if (!metadata?.isDirectory() || metadata.isSymbolicLink()) throw new Error(`Owned project tree root must be one regular directory: ${root}`);
    await walk(absoluteRoot);
  }
  for (const path of OWNED_PROJECT_FILES) {
    const absolutePath = resolve(projectRoot, ...path.split("/"));
    await assertNoSymbolicAncestor(absolutePath, projectRoot, "Owned project file");
    const metadata = await pathState(absolutePath);
    addPath(path, "file");
    if (!metadata) { files.push({ path, exists: false }); continue; }
    if (!metadata.isFile() || metadata.isSymbolicLink()) throw new Error(`Owned project file must be regular: ${path}`);
    const bytes = await readFile(absolutePath);
    files.push({ path, exists: true, sizeBytes: bytes.length, sha256: sha256Bytes(bytes) });
  }
  files.sort(comparePath);
  directories.sort();
  return { files, directories, treeHash: ownedProjectTreeHash(files, directories) };
}

function ownedProjectSnapshotLocation(project, evidenceDir) {
  const projectRoot = resolve(project);
  const evidenceRoot = resolve(evidenceDir);
  const evidenceRelative = relative(projectRoot, evidenceRoot);
  if (!evidenceRelative.startsWith(`..${sep}`) && evidenceRelative !== ".." && !isAbsolute(evidenceRelative)) throw new Error("Owned project snapshot evidence must be outside the Dev project.");
  const snapshotRoot = resolve(evidenceRoot, "owned-project-snapshot");
  return {
    projectRoot,
    evidenceRoot,
    snapshotRoot,
    backupRoot: resolve(snapshotRoot, "files"),
    manifestPath: resolve(snapshotRoot, "manifest.json")
  };
}
async function writeOwnedProjectSnapshotDocument(snapshot, document) {
  validateOwnedProjectSnapshotDocument(document, snapshot);
  assertOwnedProjectBaselineDigest(snapshot, document);
  await assertNoSymbolicAncestor(snapshot.manifestPath, snapshot.evidenceRoot, "Owned project snapshot manifest");
  await mkdir(dirname(snapshot.manifestPath), { recursive: true });
  const tempPath = `${snapshot.manifestPath}.${process.pid}.${Date.now()}.tmp`;
  await writeFile(tempPath, `${JSON.stringify(document, null, 2)}\n`, "utf8");
  await rename(tempPath, snapshot.manifestPath);
  snapshot.document = document;
  return snapshot;
}
function validOwnedSnapshotPath(path, allowFile) {
  if (typeof path !== "string" || !path || path.includes("\\") || path.startsWith("/") || path.endsWith("/") || path.split("/").some((part) => !part || part === "." || part === "..")) return false;
  if (allowFile && OWNED_PROJECT_FILES.includes(path)) return true;
  return OWNED_PROJECT_TREE_ROOTS.some((root) => path === root || path.startsWith(`${root}/`));
}
function validateOwnedProjectSnapshotDocument(document, snapshot) {
  if (!plain(document) || document.schemaVersion !== 1 || resolve(document.project ?? "") !== snapshot.projectRoot
    || resolve(document.evidenceDir ?? "") !== snapshot.evidenceRoot || !Array.isArray(document.files) || !Array.isArray(document.directories)
    || typeof document.treeHash !== "string" || !/^[a-f0-9]{64}$/.test(document.treeHash) || !PROJECT_SNAPSHOT_TRANSITIONS[document.status]) throw new Error("Owned project snapshot manifest is invalid.");
  if (JSON.stringify(document.treeRoots) !== JSON.stringify(OWNED_PROJECT_TREE_ROOTS)
    || JSON.stringify(document.explicitFiles) !== JSON.stringify(OWNED_PROJECT_FILES)) throw new Error("Owned project snapshot policy differs from the current runner.");
  const paths = new Set();
  for (const directory of document.directories) {
    if (!validOwnedSnapshotPath(directory, false)) throw new Error(`Owned project snapshot directory path is invalid: ${directory}`);
    const key = directory.toLowerCase();
    if (paths.has(key)) throw new Error(`Owned project snapshot contains a duplicate path: ${directory}`);
    paths.add(key);
  }
  for (const file of document.files) {
    if (!plain(file) || !validOwnedSnapshotPath(file.path, true) || typeof file.exists !== "boolean") throw new Error("Owned project snapshot file metadata is invalid.");
    const key = file.path.toLowerCase();
    if (paths.has(key)) throw new Error(`Owned project snapshot contains a duplicate path: ${file.path}`);
    paths.add(key);
    if (file.exists) {
      if (!Number.isInteger(file.sizeBytes) || file.sizeBytes < 0 || typeof file.sha256 !== "string" || !/^[a-f0-9]{64}$/.test(file.sha256)
        || file.backupPath !== `files/${file.path}`) throw new Error(`Owned project snapshot file proof is invalid: ${file.path}`);
    } else if (file.sizeBytes !== undefined || file.sha256 !== undefined || file.backupPath !== undefined) {
      throw new Error(`Owned project absent file has unexpected proof fields: ${file.path}`);
    }
  }
  if (ownedProjectTreeHash(document.files, document.directories) !== document.treeHash || document.backupTreeHash !== document.treeHash) throw new Error("Owned project snapshot manifest tree hash is invalid.");
  if (typeof document.baselineDigest !== "string" || !/^[a-f0-9]{64}$/.test(document.baselineDigest) || ownedProjectBaselineDigest(document) !== document.baselineDigest) throw new Error("Owned project snapshot baseline digest is invalid.");
}
async function readOwnedProjectSnapshot(project, evidenceDir) {
  const snapshot = ownedProjectSnapshotLocation(project, evidenceDir);
  await assertNoSymbolicAncestor(snapshot.snapshotRoot, snapshot.evidenceRoot, "Owned project snapshot");
  const document = JSON.parse(await readFile(snapshot.manifestPath, "utf8"));
  validateOwnedProjectSnapshotDocument(document, snapshot);
  assertOwnedProjectBaselineDigest(snapshot, document);
  snapshot.document = document;
  return snapshot;
}
async function updateOwnedProjectSnapshotStatus(snapshot, nextStatus, extra = {}) {
  const current = await readOwnedProjectSnapshot(snapshot.projectRoot, snapshot.evidenceRoot);
  assertOwnedProjectBaselineDigest(snapshot, current.document);
  current.baselineDigest = snapshot.baselineDigest;
  const currentStatus = current.document.status;
  if (currentStatus !== nextStatus && !PROJECT_SNAPSHOT_TRANSITIONS[currentStatus]?.has(nextStatus)) throw new Error(`Owned project snapshot transition is invalid: ${currentStatus} -> ${nextStatus}`);
  const history = Array.isArray(current.document.history) ? [...current.document.history] : [];
  history.push({ status: nextStatus, at: new Date().toISOString() });
  return writeOwnedProjectSnapshotDocument(current, { ...current.document, ...extra, status: nextStatus, history, updatedAt: new Date().toISOString() });
}
async function verifyOwnedProjectSnapshotBackups(snapshot) {
  const loaded = await readOwnedProjectSnapshot(snapshot.projectRoot, snapshot.evidenceRoot);
  assertOwnedProjectBaselineDigest(snapshot, loaded.document);
  const document = loaded.document;
  for (const file of document.files) {
    if (!plain(file) || typeof file.path !== "string" || typeof file.exists !== "boolean") throw new Error("Owned project snapshot file metadata is invalid.");
    if (!file.exists) continue;
    if (!Number.isInteger(file.sizeBytes) || file.sizeBytes < 0 || typeof file.sha256 !== "string" || typeof file.backupPath !== "string") throw new Error(`Owned project snapshot file proof is invalid: ${file.path}`);
    const backupPath = resolve(snapshot.snapshotRoot, ...file.backupPath.split("/"));
    await assertNoSymbolicAncestor(backupPath, snapshot.snapshotRoot, "Owned project snapshot backup");
    const bytes = await readFile(backupPath);
    if (bytes.length !== file.sizeBytes || sha256Bytes(bytes) !== file.sha256) throw new Error(`Owned project snapshot backup digest changed: ${file.path}`);
  }
}

/** 第一条 UnityCLI 前持久化授权 Dev project 的精确 file/path/absence 基线。 */
export async function prepareOwnedProjectSnapshot(project, evidenceDir, buildStamp, options = {}) {
  const snapshot = ownedProjectSnapshotLocation(project, evidenceDir);
  await assertNoSymbolicAncestor(snapshot.snapshotRoot, snapshot.evidenceRoot, "Owned project snapshot");
  const existing = await pathState(snapshot.manifestPath);
  if (existing) throw new Error("Owned project snapshot already exists; a matrix evidence directory must never be resumed or reused.");
  await mkdir(snapshot.backupRoot, { recursive: true });
  const state = await captureOwnedProjectState(snapshot.projectRoot);
  const files = [];
  for (const file of state.files) {
    if (!file.exists) { files.push(file); continue; }
    const absolutePath = resolve(snapshot.projectRoot, ...file.path.split("/"));
    const backupPath = resolve(snapshot.backupRoot, ...file.path.split("/"));
    await assertNoSymbolicAncestor(absolutePath, snapshot.projectRoot, "Owned project snapshot source");
    await assertNoSymbolicAncestor(backupPath, snapshot.snapshotRoot, "Owned project snapshot backup");
    const bytes = await readFile(absolutePath);
    if (bytes.length !== file.sizeBytes || sha256Bytes(bytes) !== file.sha256) throw new Error(`Owned project changed while snapshotting: ${file.path}`);
    await mkdir(dirname(backupPath), { recursive: true });
    await writeFile(backupPath, bytes);
    files.push({ ...file, backupPath: projectRelativePath(snapshot.snapshotRoot, backupPath, "Owned project snapshot backup") });
  }
  const document = {
    schemaVersion: 1,
    status: "prepared",
    project: snapshot.projectRoot,
    evidenceDir: snapshot.evidenceRoot,
    target: options.target ?? null,
    runId: options.runId ?? null,
    buildStamp: buildStamp ?? null,
    treeRoots: [...OWNED_PROJECT_TREE_ROOTS],
    explicitFiles: [...OWNED_PROJECT_FILES],
    treeHash: state.treeHash,
    backupTreeHash: state.treeHash,
    baselineDigest: "",
    fileCount: files.filter((file) => file.exists).length,
    directoryCount: state.directories.length,
    totalBytes: files.reduce((sum, file) => sum + (file.sizeBytes ?? 0), 0),
    files,
    directories: state.directories,
    history: [{ status: "prepared", at: new Date().toISOString() }],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };
  document.baselineDigest = ownedProjectBaselineDigest(document);
  snapshot.baselineDigest = document.baselineDigest;
  await writeOwnedProjectSnapshotDocument(snapshot, document);
  await verifyOwnedProjectSnapshotBackups(snapshot);
  return snapshot;
}

function ownedProjectClosureIssues(expected, actual) {
  const issues = [];
  if (expected.treeHash !== actual.treeHash) issues.push(`tree hash ${actual.treeHash} != ${expected.treeHash}`);
  const expectedFiles = new Map(expected.files.map((file) => [file.path, file]));
  const actualFiles = new Map(actual.files.map((file) => [file.path, file]));
  for (const [path, file] of expectedFiles) {
    const observed = actualFiles.get(path);
    if (!observed || observed.exists !== file.exists) { issues.push(`${path} existence differs`); continue; }
    if (file.exists && (observed.sizeBytes !== file.sizeBytes || observed.sha256 !== file.sha256)) issues.push(`${path} digest differs`);
  }
  for (const path of actualFiles.keys()) if (!expectedFiles.has(path)) issues.push(`${path} is unexpected`);
  const expectedDirectories = new Set(expected.directories);
  const actualDirectories = new Set(actual.directories);
  for (const path of expectedDirectories) if (!actualDirectories.has(path)) issues.push(`${path}/ is missing`);
  for (const path of actualDirectories) if (!expectedDirectories.has(path)) issues.push(`${path}/ is unexpected`);
  return issues;
}
export async function verifyOwnedProjectSnapshotClosure(snapshot) {
  const loaded = await readOwnedProjectSnapshot(snapshot.projectRoot, snapshot.evidenceRoot);
  assertOwnedProjectBaselineDigest(snapshot, loaded.document);
  const actual = await captureOwnedProjectState(loaded.projectRoot);
  const issues = ownedProjectClosureIssues(loaded.document, actual);
  return { ok: issues.length === 0, issues, treeHash: actual.treeHash };
}

/** terminal cleanup 先验完整 backup，再幂等恢复 exact bytes、目录和缺失闭包。 */
export async function restoreOwnedProjectSnapshot(snapshot) {
  let loaded = await readOwnedProjectSnapshot(snapshot.projectRoot, snapshot.evidenceRoot);
  assertOwnedProjectBaselineDigest(snapshot, loaded.document);
  loaded.baselineDigest = snapshot.baselineDigest;
  await verifyOwnedProjectSnapshotBackups(loaded);
  const before = await captureOwnedProjectState(loaded.projectRoot);
  const issuesBefore = ownedProjectClosureIssues(loaded.document, before);
  loaded = await updateOwnedProjectSnapshotStatus(loaded, "restoring", { issuesBeforeRestore: issuesBefore });
  try {
    const expectedFiles = new Map(loaded.document.files.map((file) => [file.path, file]));
    const expectedDirectories = new Set(loaded.document.directories);
    for (const file of before.files) {
      const expected = expectedFiles.get(file.path);
      if (file.exists && (!expected || !expected.exists)) {
        const absolutePath = resolve(loaded.projectRoot, ...file.path.split("/"));
        await assertNoSymbolicAncestor(absolutePath, loaded.projectRoot, "Owned project restore removal");
        await rm(absolutePath, { force: false });
      }
    }
    for (const directory of before.directories.filter((path) => !expectedDirectories.has(path)).sort((left, right) => right.length - left.length)) {
      const absolutePath = resolve(loaded.projectRoot, ...directory.split("/"));
      await assertNoSymbolicAncestor(absolutePath, loaded.projectRoot, "Owned project restore directory");
      await rm(absolutePath, { recursive: true, force: false });
    }
    for (const directory of loaded.document.directories) {
      const absolutePath = resolve(loaded.projectRoot, ...directory.split("/"));
      await assertNoSymbolicAncestor(absolutePath, loaded.projectRoot, "Owned project restore directory");
      await mkdir(absolutePath, { recursive: true });
    }
    for (const file of loaded.document.files) {
      const absolutePath = resolve(loaded.projectRoot, ...file.path.split("/"));
      await assertNoSymbolicAncestor(absolutePath, loaded.projectRoot, "Owned project restore file");
      if (!file.exists) { await rm(absolutePath, { force: true }); continue; }
      const backupPath = resolve(loaded.snapshotRoot, ...file.backupPath.split("/"));
      const bytes = await readFile(backupPath);
      if (bytes.length !== file.sizeBytes || sha256Bytes(bytes) !== file.sha256) throw new Error(`Owned project snapshot backup digest changed during restore: ${file.path}`);
      await mkdir(dirname(absolutePath), { recursive: true });
      await writeFile(absolutePath, bytes);
    }
    const closure = await verifyOwnedProjectSnapshotClosure(loaded);
    if (!closure.ok) throw new Error(`Owned project restore closure failed: ${closure.issues.join("; ")}`);
    return updateOwnedProjectSnapshotStatus(loaded, "restored", {
      restoredAt: new Date().toISOString(),
      restoredTreeHash: closure.treeHash,
      changedPaths: issuesBefore,
      packageStateChanged: issuesBefore.some((issue) => issue.startsWith("Packages/"))
    });
  } catch (error) {
    await updateOwnedProjectSnapshotStatus(loaded, "cleanup-failed", { cleanupError: error instanceof Error ? error.message : String(error) }).catch(() => undefined);
    throw error;
  }
}

export async function markOwnedProjectSnapshotReconciled(snapshot, reconciliation) {
  return updateOwnedProjectSnapshotStatus(snapshot, "reconciled", { reconciliation, reconciledAt: new Date().toISOString() });
}
export async function markOwnedProjectSnapshotClean(snapshot) {
  const closure = await verifyOwnedProjectSnapshotClosure(snapshot);
  if (!closure.ok) throw new Error(`Owned project final closure failed: ${closure.issues.join("; ")}`);
  return updateOwnedProjectSnapshotStatus(snapshot, "clean", { finalTreeHash: closure.treeHash, cleanAt: new Date().toISOString() });
}

async function readSceneBakeState(project, scenePath) {
  const location = sceneBakeArtifactDirectory(project, scenePath);
  await assertNoSymbolicAncestor(location.sceneAbsolute, location.projectRoot, "Scene path");
  await assertNoSymbolicAncestor(location.absoluteDirectory, location.projectRoot, "Scene lighting directory");
  const files = [];
  async function visit(directory) {
    let entries;
    try { entries = await readdir(directory, { withFileTypes: true }); }
    catch (error) { if (error?.code === "ENOENT") return; throw error; }
    for (const entry of entries) {
      if (entry.isSymbolicLink()) throw new Error("Scene lighting directory contains a symbolic link.");
      const absolutePath = resolve(directory, entry.name);
      if (entry.isDirectory()) { await visit(absolutePath); continue; }
      if (!entry.isFile()) throw new Error("Scene lighting directory contains an unsupported entry.");
      const content = await readFile(absolutePath);
      const relativePath = slashPath(relative(location.absoluteDirectory, absolutePath));
      files.push({
        path: `${location.publicDirectory}/${relativePath}`,
        relativePath,
        absolutePath,
        sha256: createHash("sha256").update(content).digest("hex"),
        sizeBytes: content.length,
        content
      });
    }
  }
  await visit(location.absoluteDirectory);
  files.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
  return { ...location, scenePath, files };
}

async function writeSceneBakeSnapshotStatus(snapshot, status, terminalRequestId = null) {
  if (!new Set(["prepared", "effect-terminal", "clean"]).has(status)) throw new Error(`Invalid bake snapshot status: ${status}`);
  await assertNoSymbolicAncestor(snapshot.backupRoot, snapshot.evidenceRoot, "Bake snapshot root");
  const document = {
    schemaVersion: 1,
    status,
    sourceCase: snapshot.sourceCase,
    scenePath: snapshot.scenePath,
    publicDirectory: snapshot.publicDirectory,
    terminalRequestId,
    files: snapshot.files.map(({ path, relativePath, sha256, sizeBytes }) => ({ path, relativePath, sha256, sizeBytes }))
  };
  await writeFile(resolve(snapshot.backupRoot, "snapshot.json"), `${JSON.stringify(document, null, 2)}\n`, "utf8");
  snapshot.status = status;
  snapshot.terminalRequestId = terminalRequestId;
}

async function persistSceneBakeSnapshot(state, backupRoot, evidenceRoot, sourceCase) {
  await assertNoSymbolicAncestor(backupRoot, evidenceRoot, "Bake snapshot root");
  await rm(backupRoot, { recursive: true, force: true });
  const files = [];
  for (const file of state.files) {
    const backupPath = resolve(backupRoot, ...file.relativePath.split("/"));
    const relativeBackup = relative(backupRoot, backupPath);
    if (relativeBackup.startsWith(`..${sep}`) || relativeBackup === ".." || isAbsolute(relativeBackup)) throw new Error("Bake snapshot backup path escapes its evidence root.");
    await assertNoSymbolicAncestor(backupPath, evidenceRoot, "Bake snapshot file");
    await mkdir(dirname(backupPath), { recursive: true });
    await writeFile(backupPath, file.content);
    files.push({ ...file, backupPath, content: undefined });
  }
  const snapshot = { ...state, files, backupRoot, evidenceRoot: resolve(evidenceRoot), sourceCase, status: "prepared", terminalRequestId: null };
  await writeSceneBakeSnapshotStatus(snapshot, "prepared");
  return snapshot;
}

async function loadSceneBakeSnapshot(project, backupRoot, evidenceRoot) {
  const snapshotPath = resolve(backupRoot, "snapshot.json");
  let document;
  try { document = JSON.parse(await readFile(snapshotPath, "utf8")); }
  catch (error) { if (error?.code === "ENOENT") return null; throw error; }
  if (document?.schemaVersion !== 1 || !new Set(["prepared", "effect-terminal", "clean"]).has(document.status) || typeof document.sourceCase !== "string" || !Array.isArray(document.files))
    throw new Error("Persisted bake snapshot metadata is invalid.");
  await assertNoSymbolicAncestor(backupRoot, evidenceRoot, "Bake snapshot root");
  const location = sceneBakeArtifactDirectory(project, document.scenePath);
  if (document.publicDirectory !== location.publicDirectory) throw new Error("Persisted bake snapshot scene directory changed.");
  const files = [];
  for (const file of document.files) {
    if (!plain(file) || typeof file.path !== "string" || typeof file.relativePath !== "string" || typeof file.sha256 !== "string" || !Number.isInteger(file.sizeBytes) || file.sizeBytes < 0)
      throw new Error("Persisted bake snapshot file metadata is invalid.");
    const backupPath = resolve(backupRoot, ...file.relativePath.split("/"));
    const absolutePath = resolve(location.absoluteDirectory, ...file.relativePath.split("/"));
    if (file.path !== `${location.publicDirectory}/${slashPath(file.relativePath)}`) throw new Error(`Persisted bake snapshot path changed: ${file.path}`);
    await assertNoSymbolicAncestor(backupPath, evidenceRoot, "Bake snapshot file");
    await assertNoSymbolicAncestor(absolutePath, location.projectRoot, "Scene bake restore file");
    const bytes = await readFile(backupPath);
    if (bytes.length !== file.sizeBytes || createHash("sha256").update(bytes).digest("hex") !== file.sha256) throw new Error(`Persisted bake snapshot digest changed for ${file.path}`);
    files.push({ ...file, backupPath, absolutePath });
  }
  return {
    ...location,
    scenePath: document.scenePath,
    files,
    backupRoot: resolve(backupRoot),
    evidenceRoot: resolve(evidenceRoot),
    sourceCase: document.sourceCase,
    status: document.status,
    terminalRequestId: document.terminalRequestId ?? null
  };
}

/** 根据effect前后path+digest差异生成最小恢复计划，禁止猜测孤立meta的owner。 */
export function ownedSceneBakeCleanupPlan(beforeFiles, afterFiles) {
  const before = new Map(beforeFiles.map((file) => [file.path, file]));
  const after = new Map(afterFiles.map((file) => [file.path, file]));
  if (before.size !== beforeFiles.length || after.size !== afterFiles.length) throw new Error("Scene bake snapshot contains duplicate paths.");
  for (const file of beforeFiles) {
    if (file.path.endsWith(".meta") && !before.has(file.path.slice(0, -5))) throw new Error(`Scene bake baseline contains orphan meta: ${file.path}`);
    if (!file.path.endsWith(".meta") && !before.has(`${file.path}.meta`)) throw new Error(`Scene bake baseline asset has no meta: ${file.path}`);
  }
  const additions = afterFiles.filter((file) => !before.has(file.path));
  const addedAssets = new Set(additions.filter((file) => !file.path.endsWith(".meta")).map((file) => file.path));
  const addedMeta = new Set(additions.filter((file) => file.path.endsWith(".meta")).map((file) => file.path));
  for (const file of additions.filter((entry) => entry.path.endsWith(".meta"))) {
    if (!addedAssets.has(file.path.slice(0, -5))) throw new Error(`Scene bake cleanup cannot attribute orphan meta: ${file.path}`);
  }
  for (const path of addedAssets) {
    if (!addedMeta.has(`${path}.meta`)) throw new Error(`Scene bake cleanup cannot attribute an added asset without meta: ${path}`);
  }
  const restoreFiles = beforeFiles.filter((file) => !after.has(file.path) || after.get(file.path).sha256 !== file.sha256);
  const refreshAssets = new Set();
  for (const file of restoreFiles) {
    const assetPath = file.path.endsWith(".meta") ? file.path.slice(0, -5) : file.path;
    if (!before.has(assetPath)) throw new Error(`Scene bake cleanup cannot refresh a baseline meta without its asset: ${file.path}`);
    refreshAssets.add(assetPath);
  }
  return {
    deleteAssets: [...addedAssets].sort(),
    restoreFiles: restoreFiles.map((file) => file.path).sort((left, right) => Number(right.endsWith(".meta")) - Number(left.endsWith(".meta"))),
    refreshAssets: [...refreshAssets].sort()
  };
}

async function prepareOwnedBakeStartSnapshot(m, caseId, project) {
  const state = await m.call(`${caseId}.baseline.graphics.bake-start`, ["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], (e, r) => {
    const path = e?.data?.state?.activeScene?.value?.path;
    return r.exitCode === 0 && isReadyEditorState(e, false) && typeof path === "string" && path.length > 0 ? null : "graphics.bake-start baseline scene is not a ready saved Assets scene.";
  }, { baseline: { sourceCase: caseId, capability: "graphics.bake-start", artifactState: true } }, project);
  stopAfterTerminalFailure(state);
  const scenePath = state.envelope.data.state.activeScene.value.path;
  const evidenceRoot = resolve(m.o.evidence_dir);
  const backupRoot = resolve(evidenceRoot, "owned-bake-snapshots", caseId);
  try { return await persistSceneBakeSnapshot(await readSceneBakeState(project, scenePath), backupRoot, evidenceRoot, caseId); }
  catch (error) {
    const failure = await m.failed(`${caseId}.baseline.graphics.bake-start.snapshot`, `Unable to freeze scene bake artifacts before effect: ${String(error?.message ?? error)}`, { baseline: { sourceCase: caseId, capability: "graphics.bake-start", artifactState: true } });
    return stopAfterTerminalFailure(failure);
  }
}

/** pending snapshot必须在任何case baseline mutation前恢复；prepared且无terminal响应时保持outcome unknown。 */
async function recoverPendingOwnedBakeStartSnapshot(m, caseId, project) {
  const evidenceRoot = resolve(m.o.evidence_dir);
  const backupRoot = resolve(evidenceRoot, "owned-bake-snapshots", caseId);
  let snapshot;
  try { snapshot = await loadSceneBakeSnapshot(project, backupRoot, evidenceRoot); }
  catch (error) {
    const failure = await m.failed(`${caseId}.recovery.graphics.bake-start.snapshot`, `Persisted bake snapshot cannot be trusted: ${String(error?.message ?? error)}`, { recovery: { sourceCase: caseId, artifactState: true } });
    return stopAfterTerminalFailure(failure);
  }
  if (!snapshot || snapshot.status === "clean") return null;
  if (snapshot.status === "prepared") {
    const prior = m.prior.get(caseId);
    const knownTerminal = prior?.assertion?.status === "PASS" && prior?.envelope?.ok === true && prior?.exitCode === 0;
    if (!knownTerminal) {
      const failure = await m.failed(`${caseId}.recovery.graphics.bake-start.terminal-unproven`, "A prepared graphics.bake-start snapshot has no proven terminal success; inspect the scene before deciding whether to retry.", { recovery: { sourceCase: caseId, artifactState: true, terminalUnproven: true } });
      return stopAfterTerminalFailure(failure);
    }
    await writeSceneBakeSnapshotStatus(snapshot, "effect-terminal", prior.envelope.requestId ?? null);
  }
  const recoveryCaseId = `${caseId}.recovery.${m.runToken}`;
  const recovered = await cleanupOwnedBakeStartCase(m, recoveryCaseId, "graphics.bake-start", {
    assertion: { status: "PASS" }, envelope: { ok: true, requestId: snapshot.terminalRequestId }, exitCode: 0
  }, [], project, snapshot);
  if (recovered) stopAfterTerminalFailure(recovered);
  return recovered;
}

/** runner启动后先收敛所有已知terminal bake snapshot，unknown snapshot禁止任何后续setup mutation。 */
async function recoverAllPendingOwnedBakeSnapshots(m) {
  const root = resolve(m.o.evidence_dir, "owned-bake-snapshots");
  await assertNoSymbolicAncestor(root, resolve(m.o.evidence_dir), "Bake snapshots directory");
  let entries;
  try { entries = await readdir(root, { withFileTypes: true }); }
  catch (error) { if (error?.code === "ENOENT") return; throw error; }
  for (const entry of entries) {
    if (entry.isSymbolicLink() || !entry.isDirectory()) {
      const failure = await m.failed("control.bake-recovery.ownership", `Bake snapshots directory contains an unsupported entry: ${entry.name}`, { recovery: { artifactState: true } });
      return stopAfterTerminalFailure(failure);
    }
    await recoverPendingOwnedBakeStartSnapshot(m, entry.name, m.o.project);
  }
}

/** 成功bake-start必须等待只读收敛，再精确恢复effect前文件闭包。 */
async function cleanupOwnedBakeStartCase(m, caseId, name, record, flags, project, snapshot) {
  if (name !== "graphics.bake-start" || record.envelope?.ok !== true || record.exitCode !== 0 || flags.includes("--dry-run")) return null;
  if (!snapshot || snapshot.status !== "effect-terminal") return m.failed(`${caseId}.cleanup.graphics.bake-start.snapshot`, "graphics.bake-start terminal PASS has no effect-before terminal snapshot.", { cleanup: { sourceCase: caseId, artifactState: true } });
  const statusDescriptor = await getDescriptor(m, "tool", "graphics.bake-status");
  if (!statusDescriptor) return m.failed(`${caseId}.cleanup.graphics.bake-status.descriptor`, "graphics.bake-status descriptor is unavailable before cleanup.", { cleanup: { sourceCase: caseId, artifactState: true } });
  const deadline = Date.now() + Math.min(m.o.timeoutMs, 120000);
  let terminal = null;
  for (let attempt = 1; Date.now() < deadline; attempt += 1) {
    const statusTimeoutMs = Math.max(1000, Math.min(10000, deadline - Date.now()));
    const status = await m.call(`${caseId}.cleanup.graphics.bake-status.${attempt}`, argvFor("tool", "graphics.bake-status", {}, ["--timeout-ms", String(statusTimeoutMs)]), (e, r) => {
      const issue = r.exitCode === 0 ? assertPositiveSuccess(e, statusDescriptor?.resultSchema ?? {}) : `graphics.bake-status cleanup read exited ${r.exitCode}.`;
      return issue ?? (typeof e?.data?.baking === "boolean" ? null : "graphics.bake-status did not return a boolean baking state.");
    }, { cleanup: { sourceCase: caseId, capability: "graphics.bake-status", artifactState: true, attempt } }, project);
    stopAfterTerminalFailure(status);
    if (status.envelope.data.baking === false) { terminal = status; break; }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 500));
  }
  if (!terminal) return m.failed(`${caseId}.cleanup.graphics.bake-status.timeout`, "graphics.bake-start did not converge before the cleanup deadline.", { cleanup: { sourceCase: caseId, artifactState: true } });

  const ready = await m.call(`${caseId}.cleanup.graphics.bake-start.ready`, ["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], (e, r) => {
    return r.exitCode === 0 && isReadyEditorState(e, false) ? null : "Editor did not become ready after graphics.bake-start completed.";
  }, { cleanup: { sourceCase: caseId, artifactState: true }, retryAssertion: true }, project);
  stopAfterTerminalFailure(ready);

  let after;
  let plan;
  try {
    after = await readSceneBakeState(project, snapshot.scenePath);
    plan = ownedSceneBakeCleanupPlan(snapshot.files, after.files);
  } catch (error) {
    return m.failed(`${caseId}.cleanup.graphics.bake-start.plan`, `Unable to build exact bake artifact cleanup: ${String(error?.message ?? error)}`, { cleanup: { sourceCase: caseId, artifactState: true } });
  }

  const deleteDescriptor = plan.deleteAssets.length > 0 ? await getDescriptor(m, "tool", "asset.delete") : {};
  if (!deleteDescriptor) return m.failed(`${caseId}.cleanup.asset.delete.descriptor`, "asset.delete descriptor is unavailable before bake cleanup mutation.", { cleanup: { sourceCase: caseId, artifactState: true } });
  const refreshDescriptor = plan.refreshAssets.length > 0 ? await getDescriptor(m, "tool", "asset.refresh") : {};
  if (!refreshDescriptor) return m.failed(`${caseId}.cleanup.asset.refresh.descriptor`, "asset.refresh descriptor is unavailable before bake cleanup mutation.", { cleanup: { sourceCase: caseId, artifactState: true } });
  for (const path of plan.deleteAssets) {
    const deleted = await m.call(`${caseId}.cleanup.asset.delete.${createHash("sha256").update(path).digest("hex").slice(0, 12)}`, argvFor("tool", "asset.delete", { path }, []), (e, r) => {
      const issue = r.exitCode === 0 ? assertPositiveSuccess(e, deleteDescriptor?.resultSchema ?? {}) : `asset.delete cleanup exited ${r.exitCode}.`;
      return issue ?? (e?.data?.asset?.exists === false ? null : `asset.delete did not prove ${path} absent.`);
    }, { cleanup: { sourceCase: caseId, capability: "asset.delete", path, artifactState: true } }, project);
    stopAfterTerminalFailure(deleted);
  }

  const baseline = new Map(snapshot.files.map((file) => [file.path, file]));
  try {
    for (const path of plan.restoreFiles) {
      const file = baseline.get(path);
      if (!file?.backupPath) throw new Error(`Missing persisted baseline bytes for ${path}`);
      await assertNoSymbolicAncestor(file.backupPath, snapshot.evidenceRoot, "Bake snapshot file");
      await assertNoSymbolicAncestor(file.absolutePath, snapshot.projectRoot, "Scene bake restore file");
      const bytes = await readFile(file.backupPath);
      if (bytes.length !== file.sizeBytes || createHash("sha256").update(bytes).digest("hex") !== file.sha256) throw new Error(`Persisted baseline digest changed for ${path}`);
      await mkdir(dirname(file.absolutePath), { recursive: true });
      await writeFile(file.absolutePath, bytes);
    }
  } catch (error) {
    return m.failed(`${caseId}.cleanup.graphics.bake-start.restore`, `Exact bake artifact byte restore failed: ${String(error?.message ?? error)}`, { cleanup: { sourceCase: caseId, artifactState: true } });
  }

  for (const path of plan.refreshAssets) {
    const refreshed = await m.call(`${caseId}.cleanup.asset.refresh.${createHash("sha256").update(path).digest("hex").slice(0, 12)}`, argvFor("tool", "asset.refresh", { path, forceUpdate: true }), (e, r) => {
      const issue = r.exitCode === 0 ? assertPositiveSuccess(e, refreshDescriptor?.resultSchema ?? {}) : `asset.refresh cleanup exited ${r.exitCode}.`;
      return issue ?? (e?.data?.path === path && e?.data?.refreshed === true ? null : `asset.refresh did not prove ${path} reimported.`);
    }, { cleanup: { sourceCase: caseId, capability: "asset.refresh", path, artifactState: true } }, project);
    stopAfterTerminalFailure(refreshed);
  }

  const finalReady = await m.call(`${caseId}.cleanup.graphics.bake-start.final-ready`, ["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], (e, r) => {
    return r.exitCode === 0 && isReadyEditorState(e, false) ? null : "Editor did not become ready after bake artifact restore.";
  }, { cleanup: { sourceCase: caseId, artifactState: true }, retryAssertion: true }, project);
  stopAfterTerminalFailure(finalReady);

  try {
    const final = await readSceneBakeState(project, snapshot.scenePath);
    const expected = snapshot.files.map((file) => `${file.path}\0${file.sha256}`).sort();
    const actual = final.files.map((file) => `${file.path}\0${file.sha256}`).sort();
    if (expected.length !== actual.length || expected.some((value, index) => value !== actual[index])) throw new Error("Final scene bake artifact path/digest closure differs from the effect-before snapshot.");
    const proof = await m.call(`${caseId}.cleanup.graphics.bake-start.proof`, ["tools", "run", "editor.state", "--input", "{}"], (e, r) => r.exitCode === 0 && isReadyEditorState(e, false) ? null : "Final bake cleanup proof is not ready Edit Mode.", {
      cleanup: { sourceCase: caseId, capability: "graphics.bake-start", artifactState: true, baselineFileCount: expected.length, finalFileCount: actual.length, restored: plan }
    }, project);
    stopAfterTerminalFailure(proof);
    await writeSceneBakeSnapshotStatus(snapshot, "clean", snapshot.terminalRequestId);
    return proof;
  } catch (error) {
    return m.failed(`${caseId}.cleanup.graphics.bake-start.proof`, `Bake artifact cleanup proof failed: ${String(error?.message ?? error)}`, { cleanup: { sourceCase: caseId, artifactState: true } });
  }
}

/** Package 正向 case 只在 terminal PASS 后执行精确逆操作，未知 root 绝不猜测性清理。 */
async function cleanupOwnedPackageCase(m, caseId, name, record, flags, project) {
  if (record.assertion?.status !== "PASS" || record.envelope?.ok !== true || flags.includes("--dry-run")) return null;
  if (name === "package.embed") {
    let target;
    try {
      target = ownedEmbeddedPackageCleanupTarget(project, record.envelope.data);
    } catch (error) {
      return m.failed(`${caseId}.cleanup.package.embed.path`, String(error?.message ?? error), { cleanup: { sourceCase: caseId, packageState: true } });
    }
    try {
      await rm(target.embeddedPath, { recursive: true, force: false });
    } catch (error) {
      return m.failed(`${caseId}.cleanup.package.embed.remove`, `Owned embedded package removal failed: ${String(error?.message ?? error)}`, { cleanup: { sourceCase: caseId, packageId: target.packageId, embeddedPath: target.embeddedPath, packageState: true } });
    }

    const resolveDescriptor = await getDescriptor(m, "tool", "package.resolve");
    const resolved = await m.call(`${caseId}.cleanup.package.resolve`, argvFor("tool", "package.resolve", {}, []), (e, r) => {
      const issue = r.exitCode === 0 ? assertPositiveSuccess(e, resolveDescriptor?.resultSchema ?? {}) : `package.resolve cleanup exited ${r.exitCode}.`;
      if (issue) return issue;
      const restored = e?.data?.packages?.find((entry) => entry?.name === target.packageId);
      return restored?.source === "registry" ? null : `package.resolve did not restore ${target.packageId} to registry source.`;
    }, { cleanup: { sourceCase: caseId, capability: "package.resolve", packageId: target.packageId, embeddedPath: target.embeddedPath, packageState: true } }, project);
    stopAfterTerminalFailure(resolved);

    const ready = await m.call(`${caseId}.cleanup.ready`, ["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], (e, r) => {
      return r.exitCode === 0 && isReadyEditorState(e, false) ? null : "Editor did not return to ready Edit Mode after package.embed cleanup.";
    }, { cleanup: { sourceCase: caseId, packageId: target.packageId, packageState: true }, retryAssertion: true }, project);
    stopAfterTerminalFailure(ready);

    return ready;
  }

  const cleanups = {
    "package.deploy": { name: "package.restore", input: { packageName: "com.acme.tool" } },
    "package.install": { name: "package.remove", input: { name: "com.unity.textmeshpro" } },
    "package.registry.set": { name: "package.registry.clear", input: { project: m.o.project } },
    "package.scoped-registry.add": { name: "package.scoped-registry.remove", input: { name: "Acme", project: m.o.project } },
  };
  const cleanup = cleanups[name];
  if (!cleanup) return null;
  const descriptor = await getDescriptor(m, "tool", cleanup.name);
  return m.call(`${caseId}.cleanup.${cleanup.name}`, argvFor("tool", cleanup.name, cleanup.input, []), (e, r) => {
    return r.exitCode === 0 ? assertPositiveSuccess(e, descriptor?.resultSchema ?? {}) : `${cleanup.name} cleanup exited ${r.exitCode}.`;
  }, { cleanup: { sourceCase: caseId, capability: cleanup.name, packageState: true } }, project);
}

/** 通过公开 Context 先确认当前状态，仅在确需变化时调用 PlayMode Tool。 */
async function setCasePlayMode(m, caseId, playing) {
  // 状态感知读必须按目标态分 id：同一 caseId 的 enter-edit 与 enter-play 状态不同，
  // 共用 id 会被 m.call 的 PASS 复用返回陈旧快照，导致跳过真实 transition。
  const current = await m.call(`control.play-baseline.${caseId}.${playing ? "enter-play" : "enter-edit"}.current`, ["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], (e, r) => {
    if (r.exitCode !== 0 || e?.ok !== true || e.data?.status !== "ready" || e.data?.state?.playMode?.known !== true) return "PlayMode baseline state is not ready and known.";
    const value = e.data.state.playMode.value;
    return value === "edit" || value === "play" ? null : "PlayMode baseline state is outside edit/play.";
  }, { baseline: { sourceCase: caseId, expectedPlaying: playing }, retryAssertion: true });
  if (current.assertion?.status !== "PASS") return false;
  if (current.envelope.data.state.playMode.value === (playing ? "play" : "edit")) return true;

  const name = playing ? "editor.play" : "editor.stop";
  const descriptor = await getDescriptor(m, "tool", name);
  let example;
  try { example = parsePublicExample(descriptor?.examples?.[0], "tool", name); }
  catch (error) { await m.failed(`control.play-baseline.${caseId}.${name}`, `Transition example is invalid: ${error.message}`); return false; }
  const transition = await m.call(`control.play-baseline.${caseId}.${name}`, argvFor("tool", name, example.input, example.flags), (e, r) => r.exitCode === 0 ? assertSuccess(e, descriptor.resultSchema ?? {}) : `${name} baseline transition exited ${r.exitCode}.`, { baseline: { sourceCase: caseId, expectedPlaying: playing } });
  if (transition.assertion?.status !== "PASS") return false;
  const ready = await m.call(`control.play-baseline.${caseId}.${name}.ready`, ["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], (e, r) => {
    return r.exitCode === 0 && isReadyEditorState(e, playing) ? null : `Editor did not reach ready ${playing ? "Play" : "Edit"} Mode.`;
  }, { baseline: { sourceCase: caseId, expectedPlaying: playing }, retryAssertion: true });
  return ready.assertion?.status === "PASS";
}

/** 返回能力专属夹具；只承载与公共 reset 冲突的前置状态。 */
export function capabilityFixtureId(name) {
  if (["cinemachine.aim", "cinemachine.body"].includes(name)) return "cinemachine_pipeline";
  if (name === "cinemachine.extension.add") return "cinemachine_extension_absent";
  if (name === "scene.create") return "scene_create_absent";
  if (name === "scene.move-object") return "scene_gameplay_open";
  if (name === "scene.set-active") return "scene_main_open";
  if (name === "scene.unload") return "scene_level2_open";
  if (name === "prefab.create") return "prefab_create";
  if (name === "prefab.unpack") return "prefab_unpack";
  if (["prefab.stage-close", "prefab.stage-save"].includes(name)) return "prefab_stage_open";
  if (name === "profiler.load") return "profiler_capture_baseline";
  if (name === "profiler.hierarchy") return "profiler_hierarchy_capture";
  if (name === "profiler.frame-debugger.disable") return "profiler_frame_debugger_enabled";
  if (name === "profiler.memory-snapshot.compare") return "profiler_snapshots";
  if (name === "package.remove") return "package_remove";
  if (name === "package.restore") return "package_restore";
  if (name === "package.scoped-registry.remove") return "package_scoped_registry";
  if ([
    "probuilder.center-pivot", "probuilder.combine-meshes", "probuilder.convert-to-probuilder", "probuilder.create-polygon",
    "probuilder.delete-faces", "probuilder.detach-faces", "probuilder.freeze-transform", "probuilder.merge-faces",
    "probuilder.mesh-info", "probuilder.move-vertices", "probuilder.repair-mesh", "probuilder.select-faces",
    "probuilder.set-face-color", "probuilder.set-face-material", "probuilder.set-face-uvs", "probuilder.set-pivot",
    "probuilder.set-smoothing", "probuilder.split-vertices", "probuilder.validate-mesh"
  ].includes(name)) return "probuilder_generic";
  if (name === "ui.create-panel-settings") return "ui_panel_settings_absent";
  if (name === "ui.document.create") return "ui_document_absent";
  if (name === "ui.detach-document") return "ui_detach_target";
  if (name === "ui.modify-visual-element") return "ui_visual_element";
  if (name === "scriptable-object.create") return "scriptable_object_create_absent";
  if (name.startsWith("scriptable-object.")) return "scriptable_object";
  if (["vfx.line-create", "vfx.line-positions"].includes(name)) return "vfx_line_target";
  if (name === "vfx.parameter") return "vfx_parameter";
  if (name === "vfx.template.create") return "vfx_template";
  return null;
}

/** 执行能力专属夹具，并使用其固定语义断言。 */
async function prepareCapabilityFixture(m, caseId, name, project) {
  const fixtureId = capabilityFixtureId(name);
  if (!fixtureId) return true;
  const fixture = m.o.fixture[fixtureId];
  return Boolean(fixture && await runFixtureSequence(m, `case-${fixtureId}.${caseId}`, fixture, project, fixtureId));
}

// 依赖运行中会话的前置状态必须在 editor.play ready 之后建立。
async function preparePlayCapabilityFixture(m, caseId, name, project) {
  const fixtureId = name === "profiler.frame-debugger.enable" ? "profiler_play_paused" : null;
  if (!fixtureId) return true;
  const fixture = m.o.fixture[fixtureId];
  return Boolean(fixture && await runFixtureSequence(m, `case-${fixtureId}.${caseId}`, fixture, project, fixtureId));
}

/** 正向 case 从可复现基线开始；PlayMode case 额外重建一次独立运行会话。 */
async function prepareCaseBaseline(m, caseId, name, positive, phase, project) {
  // PlayMode 负例同样必须建立真实运行会话；只有非 PlayMode 负例可跳过昂贵 fixture。
  if (!positive && phase !== "play") return true;
  // terminal case 已由同一 build/project/target 的 PASS 证据覆盖时，不重复执行昂贵 fixture。
  if (m.hasReusablePass(caseId, project)) return true;
  m.invalidateFailedCaseSetup(caseId);
  if (resolve(project) !== resolve(m.o.project)) return true;
  if (phase === "play" && !await setCasePlayMode(m, caseId, false)) return false;
  if (phase !== "editor" && phase !== "play") return true;
  const fixtureId = "reset";
  const fixture = m.o.fixture[fixtureId];
  if (!fixture || !await runFixtureSequence(m, `case-${fixtureId}.${caseId}`, fixture, project, fixtureId)) return false;
  const pipelineId = name.startsWith("urp.") ? "pipeline_urp" : name.startsWith("hdrp.") ? "pipeline_hdrp" : null;
  if (pipelineId) {
    const pipelineFixture = m.o.fixture[pipelineId];
    if (!pipelineFixture || !await runFixtureSequence(m, `case-${pipelineId}.${caseId}`, pipelineFixture, project, pipelineId)) return false;
  }
  if (!await prepareCapabilityFixture(m, caseId, name, project)) return false;
  if (phase !== "play") return true;
  if (!await setCasePlayMode(m, caseId, true)) return false;
  return preparePlayCapabilityFixture(m, caseId, name, project);
}

/** 递归解析 direct case 变量；完整占位符保留 number、boolean、array 和 object 类型。 */
export function resolveCaseTemplate(value, variables) {
  if (Array.isArray(value)) return value.map((item) => resolveCaseTemplate(item, variables));
  if (plain(value)) {
    if (Object.keys(value).length === 1 && typeof value.$var === "string") {
      if (!Object.hasOwn(variables, value.$var)) throw new Error(`Direct case variable is unavailable: ${value.$var}`);
      return variables[value.$var];
    }
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, resolveCaseTemplate(item, variables)]));
  }
  if (typeof value !== "string") return value;
  const complete = /^\$\{([A-Za-z][A-Za-z0-9_]*)\}$/.exec(value);
  if (complete) {
    if (!Object.hasOwn(variables, complete[1])) throw new Error(`Direct case variable is unavailable: ${complete[1]}`);
    return variables[complete[1]];
  }
  return value.replace(/\$\{([A-Za-z][A-Za-z0-9_]*)\}/g, (_, name) => {
    if (!Object.hasOwn(variables, name)) throw new Error(`Direct case variable is unavailable: ${name}`);
    const replacement = variables[name];
    if (!["string", "number", "boolean"].includes(typeof replacement)) throw new Error(`Direct case variable cannot be embedded in a string: ${name}`);
    return String(replacement);
  });
}

/** capture 只读取当前公开 data，并保持证据路径与业务 DTO 一致。 */
export function readDirectCapture(envelope, pointer) {
  if (typeof pointer !== "string" || (!pointer.startsWith("/data/") && pointer !== "/data")) throw new Error(`Direct capture must use an absolute /data JSON Pointer: ${pointer}`);
  if (pointer === "/data/result" || pointer.startsWith("/data/result/")) throw new Error(`Legacy /data/result capture is forbidden: ${pointer}`);
  return pointer.split("/").slice(1).reduce((current, token) => {
    const key = token.replaceAll("~1", "/").replaceAll("~0", "~");
    return current == null ? undefined : current[key];
  }, envelope);
}

function directExpectationIssue(e, r, expect = {}, schema = {}) {
  if (expect.expectedErrorCode) {
    if (r.exitCode === 0) return `Expected error ${expect.expectedErrorCode}.`;
    const issue = assertError(e, expect.expectedErrorCode === "E_SCHEMA_INVALID");
    if (issue) return issue;
    return e?.error?.code === expect.expectedErrorCode ? null : `Expected error ${expect.expectedErrorCode}, received ${e?.error?.code ?? "unknown"}.`;
  }
  if (r.exitCode !== 0 && Array.isArray(expect.acceptErrorCodes) && expect.acceptErrorCodes.includes(e?.error?.code)) return null;
  if (r.exitCode !== 0) return `Direct case step exited ${r.exitCode}.`;
  const issue = assertPositiveSuccess(e, schema);
  if (issue) return issue;
  for (const [pointer, expected] of Object.entries(expect.equals ?? {})) {
    if (!Object.is(readDirectCapture(e, pointer), expected)) return `Direct case assertion failed for ${pointer}.`;
  }
  for (const [pointer, expected] of Object.entries(expect.contains ?? {})) {
    const actual = readDirectCapture(e, pointer);
    if (!Array.isArray(actual) || !actual.some((item) => Object.is(item, expected))) return `Direct case assertion failed for ${pointer}: expected item is absent.`;
  }
  for (const pointer of expect.exists ?? []) if (readDirectCapture(e, pointer) === undefined) return `Direct case assertion failed for ${pointer}: value is absent.`;
  return null;
}

/** 比较 phase capture；这些关系只补充公开 readback，Tool 内 fresh request proof 仍是几何终态权威。 */
export function directInvariantIssue(invariants, variables) {
  const readOperand = (rule, key) => {
    if (typeof rule[key] === "string") {
      if (!Object.hasOwn(variables, rule[key])) throw new Error(`Direct invariant variable is unavailable: ${rule[key]}`);
      return variables[rule[key]];
    }
    return rule[key];
  };
  for (const rule of invariants ?? []) {
    let left;
    try { left = readOperand(rule, "left"); }
    catch (error) { return error.message; }
    let right;
    try { right = Object.hasOwn(rule, "right") ? readOperand(rule, "right") : rule.value; }
    catch (error) { return error.message; }
    const tolerance = Number(rule.tolerance ?? 0);
    let passed = false;
    if (rule.operator === "equals") passed = Object.is(left, right);
    else if (rule.operator === "notEquals") passed = !Object.is(left, right);
    else if (rule.operator === "deepEquals") passed = JSON.stringify(left) === JSON.stringify(right);
    else if (rule.operator === "greaterThan") passed = typeof left === "number" && typeof right === "number" && left > right;
    else if (rule.operator === "lessThan") passed = typeof left === "number" && typeof right === "number" && left < right;
    else if (rule.operator === "approx") passed = Number.isFinite(left) && Number.isFinite(right) && Number.isFinite(tolerance) && Math.abs(left - right) <= tolerance;
    else if (rule.operator === "deltaEquals") passed = typeof left === "number" && typeof right === "number" && left - right === rule.value;
    else if (rule.operator === "sumEquals") {
      const values = (rule.operands ?? []).map((name) => variables[name]);
      passed = values.length > 0 && values.every((value) => typeof value === "number") && left === values.reduce((sum, value) => sum + value, 0);
    }
    else return `Unsupported direct invariant operator: ${rule.operator}`;
    if (!passed) return `Direct invariant failed: ${rule.left} ${rule.operator} ${Object.hasOwn(rule, "right") ? rule.right : JSON.stringify(rule.value)}.`;
  }
  return null;
}

function directCaseVariables(m, name) {
  const slug = name.replace(/^probuilder\./, "pb_").replaceAll(".", "_").replaceAll("-", "_");
  const stem = `__ucli_fm_${m.runToken}_${slug}`;
  return {
    runToken: m.runToken,
    target: stem,
    targetA: `${stem}_a`,
    targetB: `${stem}_b`,
    resultName: `${stem}_merged`,
    anchor: `${stem}_anchor`,
    assetPath: `Assets/UnityCliMatrix/${m.runToken}_${slug}.controller`,
    prefabPath: `Assets/UnityCliMatrix/${m.runToken}_${slug}.prefab`,
    variantPath: `Assets/UnityCliMatrix/${m.runToken}_${slug}_variant.prefab`,
    timelinePath: `Assets/UnityCliMatrix/${m.runToken}_${slug}.playable`
  };
}

function directRecipeHash(recipe) {
  return createHash("sha256").update(JSON.stringify(recipe)).digest("hex");
}

async function persistDirectRecovery(m) {
  const cases = Object.fromEntries([...m.directRecovery.entries()]);
  await writeFile(m.directRecoveryPath, `${JSON.stringify({ version: 1, cases }, null, 2)}\n`);
}

export function directPhaseIds(sourceCase, recipe) {
  const phases = [
    ["setup", recipe.setup],
    ["before", recipe.before],
    ["preflight", recipe.preflight],
    ["invocation", [recipe.invocation]],
    ["after", recipe.after],
    ["cleanup", recipe.cleanup]
  ];
  return phases.flatMap(([phase, definitions]) => (definitions ?? []).map((definition, index) => `${sourceCase}.${phase}.${definition.id ?? index + 1}`));
}

export function hasReusableDirectCase(m, sourceCase, recipe, recipeHash, project) {
  const parent = m.prior.get(sourceCase);
  const recovery = m.directRecovery.get(sourceCase);
  return m.hasReusablePass(sourceCase, project)
    && parent?.directCase?.recipeHash === recipeHash
    && recovery?.status === "complete"
    && recovery?.recipeHash === recipeHash
    && directPhaseIds(sourceCase, recipe).every((id) => m.hasReusablePass(id, project));
}

/** 进程中断后先恢复旧 run 的对象、资产与全局设置；恢复失败时禁止开始新 effect。 */
async function recoverInterruptedDirectCase(m, sourceCase, recipe, recipeHash, project) {
  const entry = m.directRecovery.get(sourceCase);
  if (!entry || !["in_progress", "cleanup_failed"].includes(entry.status)) return true;
  if (entry.recipeHash !== recipeHash || resolve(entry.project) !== resolve(project) || entry.target !== (m.o.target ?? null)) {
    await m.failed(`${sourceCase}.recovery`, "Interrupted direct case is bound to a different recipe, project, or target; automatic cleanup is unsafe.", { directRecovery: entry });
    return false;
  }
  const recoveryCase = `${sourceCase}.recovery.${entry.runToken}`;
  const recovered = await runDirectCasePhase(m, recoveryCase, "cleanup", recipe.cleanup, { ...entry.variables }, project);
  entry.status = recovered ? "recovered" : "cleanup_failed";
  entry.recoveredAt = new Date().toISOString();
  await persistDirectRecovery(m);
  if (!recovered) await m.failed(`${sourceCase}.recovery`, "Interrupted direct case cleanup did not complete; new effects are blocked.", { directRecovery: entry });
  return recovered;
}

async function runDirectCaseStep(m, sourceCase, phase, definition, index, variables, project, outputSchema = {}) {
  if ((definition.when ?? []).some((name) => !Object.hasOwn(variables, name))) return true;
  let input;
  let flags;
  let expect;
  try {
    input = resolveCaseTemplate(definition.input ?? {}, variables);
    flags = resolveCaseTemplate(definition.flags ?? [], variables);
    expect = resolveCaseTemplate({
      ...(definition.expect ?? {}),
      ...(definition.expectedErrorCode ? { expectedErrorCode: definition.expectedErrorCode } : {}),
      ...(definition.acceptErrorCodes ? { acceptErrorCodes: definition.acceptErrorCodes } : {})
    }, variables);
  } catch (error) {
    await m.failed(`${sourceCase}.${phase}.${definition.id ?? index + 1}`, `Direct case template is invalid: ${error instanceof Error ? error.message : String(error)}`, { directCase: { sourceCase, phase } });
    return false;
  }
  const id = `${sourceCase}.${phase}.${definition.id ?? index + 1}`;
  const argv = argvFor(definition.kind, definition.name, input, flags);
  const record = await m.call(id, argv, (e, r) => directExpectationIssue(e, r, expect, outputSchema), {
    baseline: { sourceCase },
    directCase: { sourceCase, phase, step: definition.id ?? index + 1, capability: definition.name }
  }, project);
  if (record.assertion?.status !== "PASS") return false;
  for (const [name, pointer] of Object.entries(definition.capture ?? {})) {
    let captured;
    try { captured = readDirectCapture(record.envelope, pointer); }
    catch (error) { await m.failed(`${id}.capture.${name}`, error.message, { directCase: { sourceCase, phase } }); return false; }
    if (captured === undefined) {
      await m.failed(`${id}.capture.${name}`, `Direct capture path is absent: ${pointer}`, { directCase: { sourceCase, phase } });
      return false;
    }
    variables[name] = captured;
  }
  const recovery = m.directRecovery.get(sourceCase);
  if (recovery) {
    recovery.variables = structuredClone(variables);
    recovery.updatedAt = new Date().toISOString();
    await persistDirectRecovery(m);
  }
  return true;
}

export async function runDirectCasePhase(m, sourceCase, phase, definitions, variables, project, invocationSchema = {}) {
  let succeeded = true;
  for (let index = 0; index < (definitions ?? []).length; index += 1) {
    if (!succeeded && phase !== "cleanup") continue;
    const schema = phase === "invocation" ? invocationSchema : {};
    const stepSucceeded = await runDirectCaseStep(m, sourceCase, phase, definitions[index], index, variables, project, schema);
    if (!stepSucceeded) succeeded = false;
  }
  return succeeded;
}

/** 一项 direct Tool 只有 setup、before、preflight、invoke、after、关系断言与 cleanup 全部通过才记 PASS。 */
async function runDirectLiveCase(m, name, descriptor, phase, project) {
  const sourceCase = matrixCaseId("tool", name, "direct", phase);
  const recipe = DIRECT_LIVE_CASES[name];
  const recipeHash = directRecipeHash(recipe);
  if (!await recoverInterruptedDirectCase(m, sourceCase, recipe, recipeHash, project)) {
    const record = await m.failed(sourceCase, "Interrupted direct case recovery failed; the capability was not invoked.", { directCase: { capability: name, recipeHash } });
    return stopAfterTerminalFailure(record);
  }
  if (hasReusableDirectCase(m, sourceCase, recipe, recipeHash, project)) {
    return m.call(sourceCase, ["tools", "run", "editor.state", "--input", "{}"], () => null, { directCase: { reused: true, recipeHash } }, project);
  }
  for (const [id, record] of [...m.prior.entries()]) {
    if (id === sourceCase || record?.baseline?.sourceCase === sourceCase) m.prior.delete(id);
  }
  const variables = directCaseVariables(m, name);
  m.directRecovery.set(sourceCase, {
    status: "in_progress",
    recipeHash,
    runToken: m.runToken,
    project,
    target: m.o.target ?? null,
    buildStamp: m.o.build_stamp ?? null,
    variables: structuredClone(variables),
    startedAt: new Date().toISOString()
  });
  await persistDirectRecovery(m);
  let businessSucceeded = true;
  let invariantIssue = null;
  let cleanupSucceeded = true;
  try {
    businessSucceeded = await runDirectCasePhase(m, sourceCase, "setup", recipe.setup, variables, project);
    if (businessSucceeded) businessSucceeded = await runDirectCasePhase(m, sourceCase, "before", recipe.before, variables, project);
    if (businessSucceeded) businessSucceeded = await runDirectCasePhase(m, sourceCase, "preflight", recipe.preflight, variables, project);
    if (businessSucceeded) businessSucceeded = await runDirectCasePhase(m, sourceCase, "invocation", [recipe.invocation], variables, project, descriptor.resultSchema ?? {});
    if (businessSucceeded) businessSucceeded = await runDirectCasePhase(m, sourceCase, "after", recipe.after, variables, project);
    if (businessSucceeded) {
      invariantIssue = directInvariantIssue(recipe.invariants, variables);
      businessSucceeded = invariantIssue == null;
    }
  } finally {
    cleanupSucceeded = await runDirectCasePhase(m, sourceCase, "cleanup", recipe.cleanup, variables, project);
    const recovery = m.directRecovery.get(sourceCase);
    recovery.status = cleanupSucceeded ? "complete" : "cleanup_failed";
    recovery.variables = structuredClone(variables);
    recovery.completedAt = new Date().toISOString();
    await persistDirectRecovery(m);
  }
  const record = await m.call(sourceCase, ["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], (e, r) => {
    if (r.exitCode !== 0 || !isReadyEditorState(e, false)) return "Editor is not ready in Edit Mode after the direct case.";
    if (!businessSucceeded) return invariantIssue ?? "Direct case setup, invocation, or fresh readback failed.";
    if (!cleanupSucceeded) return "Direct case cleanup failed.";
    return null;
  }, { directCase: { capability: name, runToken: m.runToken, recipeHash, requestSpecific: true, cleanupAttempted: true }, retryAssertion: businessSucceeded && cleanupSucceeded }, project);
  return stopAfterTerminalFailure(record);
}

async function invoke(m, kind, name, id, d, input, flags, positive, phase, project = m.o.project, expectedErrorCode = null) {
  const caseId = matrixCaseId(kind, name, id, phase);
  const ownsBakeArtifacts = kind === "tool" && positive && name === "graphics.bake-start" && !flags.includes("--dry-run");
  if (ownsBakeArtifacts) await recoverPendingOwnedBakeStartSnapshot(m, caseId, project);
  if (!await prepareCaseBaseline(m, caseId, name, positive, phase, project)) {
    const baselineFailure = await m.failed(caseId, "Case baseline setup failed.", { capability: { kind, name }, phase });
    return stopAfterTerminalFailure(baselineFailure);
  }
  const bakeSnapshot = ownsBakeArtifacts ? await prepareOwnedBakeStartSnapshot(m, caseId, project) : null;
  const dynamicInput = input;
  const projectInput = routeCapabilityInput(dynamicInput, project, m.o.project);
  const routedInput = routeCapabilityFixtureInput(projectInput, name, m.o);
  const record = await m.call(caseId, argvFor(kind, name, routedInput, flags), (e, r) => {
    if (positive) return r.exitCode === 0 ? assertPositiveSuccess(e, d.resultSchema ?? {}) : `Positive invocation exited ${r.exitCode}.`;
    if (r.exitCode === 0) return "Negative invocation must exit nonzero.";
    const issue = assertError(e, expectedErrorCode === "E_SCHEMA_INVALID");
    if (issue) return issue;
    return expectedErrorCode && e?.error?.code !== expectedErrorCode ? `Expected error ${expectedErrorCode}, received ${e?.error?.code ?? "unknown"}.` : null;
  }, { capability: { kind, name }, phase, ...(expectedErrorCode ? { expectedErrorCode } : {}) }, project);
  if (bakeSnapshot && record.envelope?.ok === true && record.exitCode === 0) {
    try { await writeSceneBakeSnapshotStatus(bakeSnapshot, "effect-terminal", record.envelope.requestId ?? null); }
    catch (error) {
      const failure = await m.failed(`${caseId}.cleanup.graphics.bake-start.snapshot`, `Unable to persist terminal bake cleanup ownership: ${String(error?.message ?? error)}`, { cleanup: { sourceCase: caseId, artifactState: true } });
      return stopAfterTerminalFailure(failure);
    }
  }
  const editorPlayCleanup = await cleanupEditorPlay(m, caseId, name, phase, record);
  if (editorPlayCleanup) throw new MatrixTerminalFailure(editorPlayCleanup);
  const packageCleanup = kind === "tool" && positive ? await cleanupOwnedPackageCase(m, caseId, name, record, flags, project) : null;
  if (packageCleanup) stopAfterTerminalFailure(packageCleanup);
  const bakeCleanup = kind === "tool" && positive ? await cleanupOwnedBakeStartCase(m, caseId, name, record, flags, project, bakeSnapshot) : null;
  if (bakeCleanup) stopAfterTerminalFailure(bakeCleanup);
  return stopAfterTerminalFailure(record);
}
/** Returns a matrix-owned dialog only when its public identity is uniquely observed. */
export function selectMatrixOwnedDialog(dialogs) {
  if (!Array.isArray(dialogs)) return { outcome: "blocked", reason: "editor.dialogs returned an invalid dialogs collection." };
  if (dialogs.length === 0) return { outcome: "not-applicable", reason: "No matrix-owned dialog fixture was injected." };
  const matches = dialogs.filter((dialog) => dialog?.title === MATRIX_OWNED_DIALOG.title
    && typeof dialog.message === "string"
    && dialog.message.includes(MATRIX_OWNED_DIALOG.messageMarker)
    && typeof dialog.dialogId === "string"
    && Array.isArray(dialog.buttons)
    && dialog.buttons.includes(MATRIX_OWNED_DIALOG.button));
  if (matches.length === 1) return { outcome: "matched", dialog: matches[0] };
  if (matches.length === 0) return { outcome: "blocked", reason: "Unknown external modal is present; no matrix-owned dialog fixture matched the fresh observation." };
  return { outcome: "blocked", reason: "Multiple matrix-owned dialog fixtures matched the fresh observation." };
}

/**
 * Dialog clicks may only target the uniquely observed matrix-owned fixture. Descriptor examples
 * are explanatory only and cannot form a valid positive matrix invocation.
 */
export async function coverExternalStateDependentTool(m, name, descriptor, phase, project, invokeTool = invoke) {
  const capability = { kind: "tool", name };
  await m.notApplicable(matrixCaseId("tool", name, "default", phase), "This Tool requires a fresh editor.dialogs result with a real dialogId and enabled button; descriptor examples are not executable matrix inputs.", { capability, phase, externalState: true });
  await m.notApplicable(matrixCaseId("tool", name, "optional", phase), "This Tool requires a fresh editor.dialogs result with a real dialogId and enabled button; descriptor examples are not executable matrix inputs.", { capability, phase, externalState: true });
  if (phase !== "editor") return;

  const dialogsDescriptor = await getDescriptor(m, "tool", "editor.dialogs");
  const observed = await m.call(`tool.${name}.external-state.dialogs`, ["tools", "run", "editor.dialogs", "--input", "{}"], (e, r) => {
    return r.exitCode === 0 ? assertPositiveSuccess(e, dialogsDescriptor?.resultSchema ?? {}) : `editor.dialogs observation exited ${r.exitCode}.`;
  }, { capability, phase, externalState: true });
  if (observed.assertion?.status !== "PASS") return;

  const selection = selectMatrixOwnedDialog(observed.envelope?.data?.dialogs);
  if (selection.outcome === "not-applicable") {
    await m.notApplicable(`tool.${name}.external-state.click`, selection.reason, { capability, phase, externalState: true, matrixOwnedDialog: MATRIX_OWNED_DIALOG });
    return;
  }
  if (selection.outcome === "blocked") {
    await m.blocked(`tool.${name}.external-state.click`, selection.reason, { capability, phase, externalState: true, matrixOwnedDialog: MATRIX_OWNED_DIALOG });
    return;
  }
  await invokeTool(m, "tool", name, "external-state.click", descriptor, { dialogId: selection.dialog.dialogId, button: MATRIX_OWNED_DIALOG.button }, [], true, phase, project);
}

/**
 * test.cancel 只能针对当前 coordinator 拥有且 GUID 唯一的 active test.run。
 * workflow 先启动隔离夹具，再以 dry-run 冻结身份，actual 后同时核对 cancel 与原 run 的终态。
 */
export async function coverTestCancelDependentTool(m, descriptor, phase, project) {
  const capability = { kind: "tool", name: "test.cancel" };
  const idle = await invoke(m, "tool", "test.cancel", "external-state.idle", descriptor, {}, [], false, phase, project, "E_TEST_RUN_NOT_ACTIVE");
  stopAfterTerminalFailure(idle);

  const saved = await m.call("tool.test.cancel.external-state.save-scene", ["tools", "run", "scene.save", "--input", JSON.stringify({ path: "Assets/UnityCLIValidation/MatrixScene.unity" })], (envelope, result) => {
    if (result.exitCode !== 0) return `Pre-cancellation scene.save exited ${result.exitCode}.`;
    const issue = assertPositiveSuccess(envelope, {});
    return issue ?? (envelope.data?.scene?.path === "Assets/UnityCLIValidation/MatrixScene.unity" && envelope.data.scene.isDirty === false ? null : "Pre-cancellation scene.save did not prove a clean MatrixScene.");
  }, { capability, phase, externalState: true, fixturePreparation: true }, project);
  stopAfterTerminalFailure(saved);

  let plan = null;
  let actual = null;
  const runPromise = m.call("tool.test.cancel.external-state.root-run", [
    "tools", "run", "test.run", "--input", JSON.stringify({ platform: "EditMode", testNames: [TEST_CANCEL_FIXTURE_NAME] }), "--timeout-ms", "120000"
  ], (envelope, result) => {
    if (result.exitCode === 0) return "The cancellation fixture test.run completed instead of reaching the cancelled terminal.";
    const issue = assertError(envelope, false);
    if (issue) return issue;
    if (envelope.error.code !== "E_TEST_RUN_CANCELLED") return `Expected E_TEST_RUN_CANCELLED, received ${envelope.error.code}.`;
    const details = envelope.error.details;
    if (!details?.cancelled || details?.dryRun || !Array.isArray(details?.activeRunIds) || details.activeRunIds.length !== 0) return "Cancelled root run has no terminal inactive-GUID proof.";
    if (plan && (details.rootRequestId !== plan.envelope?.data?.rootRequestId || details.runId !== plan.envelope?.data?.runId)) return "Cancelled root run identity differs from the dry-run cancellation plan.";
    return null;
  }, { capability, phase, externalState: true, rootOperation: "test.run", fixtureTestName: TEST_CANCEL_FIXTURE_NAME }, project);

  try {
    for (let attempt = 1; attempt <= 20; attempt += 1) {
      const observed = await m.call(`tool.test.cancel.external-state.plan.${attempt}`, ["tools", "run", "test.cancel", "--input", "{}", "--dry-run"], (envelope, result) => {
        if (result.exitCode === 0) {
          const issue = assertPositiveSuccess(envelope, descriptor.resultSchema ?? {});
          if (issue) return issue;
          const data = envelope.data;
          return data?.dryRun === true && data?.cancelled === false && typeof data?.rootRequestId === "string" && typeof data?.runId === "string"
            && Array.isArray(data?.activeRunIds) && data.activeRunIds.length === 1 && data.activeRunIds[0] === data.runId
            ? null : "test.cancel dry-run did not freeze one exact active run identity.";
        }
        const issue = assertError(envelope, false);
        if (issue) return issue;
        return TEST_CANCEL_PENDING_CODES.has(envelope.error.code) ? null : `Unexpected cancellation readiness error ${envelope.error.code}.`;
      }, { capability, phase, externalState: true, convergenceAttempt: attempt }, project);
      stopAfterTerminalFailure(observed);
      if (observed.envelope?.ok === true) { plan = observed; break; }
    }
    if (!plan) stopAfterTerminalFailure(await m.failed("tool.test.cancel.external-state.plan", "Active test.run identity did not become uniquely cancellable within 20 bounded observations.", { capability, phase, externalState: true }));

    actual = await m.call(matrixCaseId("tool", "test.cancel", "default", phase), ["tools", "run", "test.cancel", "--input", "{}"], (envelope, result) => {
      if (result.exitCode !== 0) return `test.cancel actual exited ${result.exitCode}.`;
      const issue = assertPositiveSuccess(envelope, descriptor.resultSchema ?? {});
      if (issue) return issue;
      const data = envelope.data;
      return data?.cancelled === true && data?.dryRun === false && Array.isArray(data?.activeRunIds) && data.activeRunIds.length === 0
        && data.rootRequestId === plan.envelope.data.rootRequestId && data.runId === plan.envelope.data.runId
        ? null : "test.cancel actual did not prove the planned GUID inactive.";
    }, { capability, phase, externalState: true, plannedRootRequestId: plan.envelope.data.rootRequestId, plannedRunId: plan.envelope.data.runId }, project);
    stopAfterTerminalFailure(actual);
    await m.notApplicable(matrixCaseId("tool", "test.cancel", "optional", phase), "The public dry-run category is the successful exact-identity plan recorded by this request-specific workflow.", { capability, phase, externalState: true, evidenceCaseId: plan.id });
  } finally {
    if (plan && actual?.assertion?.status !== "PASS") {
      await m.call("tool.test.cancel.external-state.cleanup", ["tools", "run", "test.cancel", "--input", "{}"], (envelope, result) => {
        if (result.exitCode === 0) return envelope?.data?.cancelled === true && Array.isArray(envelope?.data?.activeRunIds) && envelope.data.activeRunIds.length === 0 ? null : "Cleanup cancel has no inactive-GUID proof.";
        const issue = assertError(envelope, false);
        return issue ?? (envelope.error.code === "E_TEST_RUN_NOT_ACTIVE" ? null : `Cleanup cancel returned ${envelope.error.code}.`);
      }, { capability, phase, externalState: true, cleanup: true }, project);
    }
  }

  const root = await runPromise;
  stopAfterTerminalFailure(root);
  const postIdle = await m.call("tool.test.cancel.external-state.post-idle", ["tools", "run", "test.cancel", "--input", "{}"], (envelope, result) => {
    if (result.exitCode === 0) return "test.cancel remained active after the cancelled root run reached terminal.";
    const issue = assertError(envelope, false);
    return issue ?? (envelope.error.code === "E_TEST_RUN_NOT_ACTIVE" ? null : `Expected E_TEST_RUN_NOT_ACTIVE, received ${envelope.error.code}.`);
  }, { capability, phase, externalState: true, freshReadback: true }, project);
  stopAfterTerminalFailure(postIdle);
}

async function cover(m, kind, name, phase = "editor") {
  const d = await getDescriptor(m, kind, name); if (!d) return m.blocked(`${kind}.${name}.descriptor`, "Describe returned no descriptor."); let first;
  try { first = parsePublicExample(d.examples?.[0], kind, name); } catch (e) { if ((d.inputSchema?.required ?? []).length) return m.blocked(matrixCaseId(kind, name, "default", phase), `Cannot construct default input: ${e.message}`); first = { input: {}, flags: [] }; }
  if (d.available === false) {
    const expectedErrorCode = unavailableDescriptorErrorCode(d);
    await invoke(m, kind, name, "unavailable", d, first.input, matrixExampleFlags(first.flags), false, phase, capabilityProject(m, kind, d), expectedErrorCode);
    for (const category of ["default", "optional", "unknown", "type", "dry-run"]) {
      await m.notApplicable(matrixCaseId(kind, name, category, phase), `The selected Unity generation reports ${name} unavailable; execution is covered by the explicit ${expectedErrorCode} probe.`, { capability: { kind, name }, phase, unavailable: true });
    }
    return;
  }
  const unavailable = phase === "play" ? d.playModeSupport === "editor-only" : d.playModeSupport === "runtime-only";
  const project = capabilityProject(m, kind, d);
  // PackageManager.Resolve 在 PlayMode 下无法完成；这些 mutation 的 resolve 阶段会永久阻塞主线程。
  // editor phase 已完整正反覆盖；PlayMode 下必须 notApplicable，绝不发起实际 resolve。
  if (phase === "play" && kind === "tool" && PLAYMODE_RESOLVE_BLOCKED_TOOLS.has(name)) {
    for (const category of ["default", "optional", "unknown", "type", "dry-run"]) {
      await m.notApplicable(matrixCaseId(kind, name, category, phase), "PackageManager.Resolve cannot complete under PlayMode; package file mutations are covered in the editor phase and must never block the Editor main thread.", { capability: { kind, name }, phase, playmodeResolveBlocked: true });
    }
    return;
  }
  if (phase === "play" && resolve(project) !== resolve(m.o.project)) {
    for (const category of ["default", "optional", "unknown", "type"]) await m.notApplicable(matrixCaseId(kind, name, category, phase), "Capability executes outside the primary live Editor session, so Dev PlayMode is not applicable.", { capability: { kind, name }, phase, project });
    return;
  }
  // runtime-only 的 Editor 反向不能沿用会直接请求 runtime 的示例 flag。
  const phaseFlags = unavailable && phase === "editor" ? first.flags.filter((v) => v !== "--runtime") : first.flags;
  const unavailableCode = phase === "play" ? (PLAYMODE_UNAVAILABLE_CODE_OVERRIDES.get(name) ?? "E_TOOL_PLAYMODE_UNAVAILABLE") : "E_RUNTIME_NOT_RUNNING";
  const invocationIsPositive = () => !unavailable;
  const externalStateDependent = kind === "tool" && EXTERNAL_STATE_DEPENDENT_TOOL_NAMES.includes(name);
  const testCancelDependent = kind === "tool" && name === "test.cancel" && phase === "editor";
  if (externalStateDependent && !unavailable) {
    await coverExternalStateDependentTool(m, name, d, phase, project);
  } else if (testCancelDependent && !unavailable) {
    await coverTestCancelDependentTool(m, d, phase, project);
  }
  const frozenRecipe = kind === "tool" ? DIRECT_LIVE_CASES[name] : null;
  const directRecipe = phase === "editor" && !unavailable ? frozenRecipe : null;
  const suppressFrozenPlayPositive = phase === "play" && frozenRecipe && !unavailable;
  if ((externalStateDependent || testCancelDependent) && !unavailable) {
    // The dedicated path above owns all positive coverage; continue with negative schema probes below.
  } else if (directRecipe) {
    await runDirectLiveCase(m, name, d, phase, project);
    await m.notApplicable(matrixCaseId(kind, name, "optional", phase), "The frozen direct Tool is covered by its request-specific closed-set case; descriptor-example replay is not independent evidence.", { capability: { kind, name }, phase, directCase: true });
  } else if (suppressFrozenPlayPositive) {
    await m.notApplicable(matrixCaseId(kind, name, "default", phase), "A frozen direct Tool cannot use a descriptor example as PlayMode success evidence; PlayMode requires a separate request-specific case.", { capability: { kind, name }, phase, directCase: true });
    await m.notApplicable(matrixCaseId(kind, name, "optional", phase), "A frozen direct Tool cannot use a descriptor example as PlayMode success evidence; PlayMode requires a separate request-specific case.", { capability: { kind, name }, phase, directCase: true });
  } else {
    const defaultPositive = invocationIsPositive();
    await invoke(m, kind, name, "default", d, first.input, phaseFlags, defaultPositive, phase, project, defaultPositive ? null : unavailableCode);
    let optional;
    if (d.examples?.[1]) {
      try { optional = parsePublicExample(d.examples[1], kind, name); }
      catch (e) { return m.blocked(matrixCaseId(kind, name, "optional", phase), `Second public example is invalid: ${e.message}`); }
    } else if (plain(d.inputSchema?.properties) && Object.keys(d.inputSchema.properties).some((key) => !(d.inputSchema.required ?? []).includes(key))) {
      optional = { input: { ...first.input }, flags: [...first.flags] };
    }
    if (!optional) await m.notApplicable(matrixCaseId(kind, name, "optional", phase), "Input schema has no optional field and no second public usage category.", { capability: { kind, name }, phase });
    else {
      const rawOptionalFlags = unavailable && phase === "editor" ? optional.flags.filter((v) => v !== "--runtime") : optional.flags;
      const optionalPositive = invocationIsPositive();
      await invoke(m, kind, name, "optional", d, optional.input, rawOptionalFlags, optionalPositive, phase, project, optionalPositive ? null : unavailableCode);
    }
  }
  const schemaProbeFlags = kind === "tool" && d.destructiveHint && d.supportsDryRun ? ["--dry-run"] : [];
  await invoke(m, kind, name, "unknown", d, { ...first.input, __matrix_unknown: true }, schemaProbeFlags, false, phase, project, "E_SCHEMA_INVALID");
  const typeProbe = Object.entries(d.inputSchema?.properties ?? {})
    .map(([key, schema]) => ({ key, value: invalidSchemaValue(schema) }))
    .find((entry) => entry.value !== undefined);
  if (!typeProbe) await m.notApplicable(matrixCaseId(kind, name, "type", phase), "Input schema has no field with a rejectable JSON type.", { capability: { kind, name }, phase });
  else await invoke(m, kind, name, "type", d, { ...first.input, [typeProbe.key]: typeProbe.value }, schemaProbeFlags, false, phase, project, "E_SCHEMA_INVALID");
  if (kind === "tool" && phase === "editor" && d.destructiveHint && !directRecipe && d.supportsDryRun) {
    await invoke(m, "tool", name, "dry-run", d, first.input, ["--dry-run"], true, "editor", project);
  }
}
export function unavailableDescriptorErrorCode(descriptor) {
  const match = typeof descriptor?.unavailableReason === "string" ? /^([A-Z][A-Z0-9_]+):/.exec(descriptor.unavailableReason) : null;
  return match?.[1] ?? "E_TOOL_UNAVAILABLE";
}

function capabilitySelected(m, name) {
  return m.o.capabilities.length === 0 || m.o.capabilities.includes(name);
}

async function coverToolsWithPipelineFixtures(m, phase, excluded = new Set()) {
  const prepared = new Set();
  for (const name of m.discovered.tools) {
    if (excluded.has(name) || !capabilitySelected(m, name)) continue;
    const pipeline = name.startsWith("urp.") ? "pipeline_urp" : name.startsWith("hdrp.") ? "pipeline_hdrp" : null;
    if (pipeline && phase === "editor" && !prepared.has(pipeline)) {
      const fixture = m.o.fixture[pipeline];
      if (!fixture) await blockMissingRequiredFixture(m, `control.${pipeline}.${phase}`, `No ${pipeline} activation fixture supplied.`);
      else await runRequiredFixtureSequence(m, `${pipeline}.${phase}`, fixture, m.o.project, pipeline);
      prepared.add(pipeline);
    }
    await cover(m, "tool", name, phase);
  }
}
async function playMode(m) {
  m.invalidateFailedPlayModeControls();
  const play = m.discovered.tools.find((n) => n === "editor.play"); const stop = m.discovered.tools.find((n) => n === "editor.stop"); if (!play || !stop) return blockTerminal(m, "playmode.transition", "Public editor.play/editor.stop capabilities are not discoverable.");
  const pd = await getDescriptor(m, "tool", play), sd = await getDescriptor(m, "tool", stop); let p, s; try { p = parsePublicExample(pd.examples?.[0], "tool", play); s = parsePublicExample(sd.examples?.[0], "tool", stop); } catch (e) { return blockTerminal(m, "playmode.transition", `Transition example invalid: ${e.message}`); }
  await invoke(m, "tool", play, "transition", pd, p.input, p.flags, true, "transition");
  const playReady = await m.call("playmode.ready", ["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], (e) => isReadyEditorState(e, true) ? null : "Editor state did not report ready Play Mode after editor.play.", { retryAssertion: true });
  stopAfterTerminalFailure(playReady);
  await coverToolsWithPipelineFixtures(m, "play", new Set([play, stop]));
  await invoke(m, "tool", stop, "transition", sd, s.input, s.flags, true, "transition");
  const exitReady = await m.call("playmode.exit-ready", ["tools", "run", "editor.state", "--input", "{}", "--timeout-ms", "10000"], (e) => isReadyEditorState(e, false) ? null : "Editor state did not report ready Edit Mode after editor.stop.", { retryAssertion: true });
  stopAfterTerminalFailure(exitReady);
}
const FIXTURE_ASSERTIONS = {
  setup: "ready",
  reset: "ready",
  pipeline_urp: "pipelineReady",
  pipeline_hdrp: "pipelineReady",
  pipeline_restore: "pipelineRestored",
  timeout_cancel: "timeoutOrCancel",
  stale_target: "staleTarget",
  domain_reload: "recoveredAfterReload",
  shell_quoting: "quotedPath",
  prefab_stage: "prefabStage",
  ui_cache: "cacheInvalidated",
  cinemachine_pipeline: "cinemachinePipelineReady",
  cinemachine_extension_absent: "cinemachineExtensionAbsent",
  probuilder_generic: "probuilderGenericReady",
  ui_panel_settings_absent: "panelSettingsOutputAbsent",
  vfx_parameter: "vfxParametersReady",
  scene_create_absent: "sceneOutputAbsent",
  scene_main_open: "sceneOpen",
  scene_gameplay_open: "sceneOpen",
  scene_level2_open: "sceneOpen",
  ui_document_absent: "uiDocumentOutputAbsent",
  ui_detach_target: "uiDetachTargetReady",
  ui_visual_element: "uiVisualElementReady",
  scriptable_object: "scriptableObjectReady",
  scriptable_object_create_absent: "scriptableObjectOutputAbsent",
  vfx_line_target: "lineTargetReady",
  vfx_template: "vfxTemplateReady",
  prefab_create: "prefabOutputAbsent",
  prefab_unpack: "prefabInstanceReady",
  prefab_stage_open: "prefabStageReady",
  profiler_capture_baseline: "profilerCaptureReady",
  profiler_hierarchy_capture: "profilerHierarchyReady",
  profiler_frame_debugger_enabled: "frameDebuggerEnabled",
  profiler_snapshots: "profilerSnapshotsReady",
  profiler_play_paused: "playPaused",
  package_remove: "packageInstalled",
  package_restore: "packageDeploymentReady",
  package_scoped_registry: "scopedRegistryReady",
  playmode_fast: "fastPlayMode",
  playmode_restore: "playModeRestored"
};

function valueAt(value, path) {
  return String(path).split(".").filter(Boolean).reduce((current, key) => current == null ? undefined : current[key], value);
}
function fixtureArg(value, variables) {
  return String(value).replace(/\$\{([A-Za-z][A-Za-z0-9_]*)\}/g, (_, name) => {
    if (!Object.hasOwn(variables, name)) throw new Error(`Fixture variable is unavailable: ${name}`);
    return String(variables[name]);
  });
}
export function fixtureExpectationIssue(e, r, expect = {}) {
  const acceptedErrors = new Set(Array.isArray(expect.acceptErrorCodes) ? expect.acceptErrorCodes : []);
  if (r.exitCode !== 0 && e?.ok === false && acceptedErrors.has(e.error?.code)) return null;
  if (expect.errorCode) {
    if (r.exitCode === 0) return `Expected error ${expect.errorCode}.`;
    const issue = assertError(e, expect.schemaError === true); if (issue) return issue;
    if (e.error?.code !== expect.errorCode) return `Expected error ${expect.errorCode}, received ${e.error?.code ?? "unknown"}.`;
  } else {
    if (r.exitCode !== 0) return `Fixture exited ${r.exitCode}.`;
    const issue = assertSuccess(e, expect.outputSchema ?? {}); if (issue) return issue;
  }
  for (const [path, expected] of Object.entries(expect.equals ?? {})) if (!Object.is(valueAt(e, path), expected)) return `Fixture assertion failed for ${path}.`;
  for (const [path, choices] of Object.entries(expect.oneOf ?? {})) if (!Array.isArray(choices) || !choices.some((choice) => Object.is(valueAt(e, path), choice))) return `Fixture assertion failed for ${path}: value is outside oneOf.`;
  for (const [path, expected] of Object.entries(expect.contains ?? {})) {
    const value = valueAt(e, path);
    if (!Array.isArray(value) || !value.some((item) => Object.is(item, expected))) return `Fixture assertion failed for ${path}: expected item is absent.`;
  }
  for (const path of expect.exists ?? []) if (valueAt(e, path) === undefined) return `Fixture assertion failed for ${path}: value is absent.`;
  return null;
}
/** Editor ready 是短暂收敛状态；只对这一种明确 expectation 做 bounded assertion retry。 */
export function fixtureStepRetriesReadyAssertion(step) {
  return step?.argv?.[0] === "tools"
    && step?.argv?.[1] === "run"
    && step?.argv?.[2] === "editor.state"
    && step?.argv?.[3] === "--input"
    && step?.argv?.[4] === "{}"
    && step?.expect?.equals?.["data.status"] === "ready";
}
export function fixtureError(f, id) {
  if (!plain(f) || !plain(f.assert)) return "Fixture must declare an assert object.";
  if (id === "concurrency") {
    const serial = f.assert.serial === true;
    const overlap = Number.isFinite(f.assert.minOverlapMs) && f.assert.minOverlapMs > 0;
    if (serial === overlap) return "Concurrency fixture must select exactly one serial or positive minOverlapMs proof.";
    return Array.isArray(f.calls) && f.calls.length === 2 ? null : "Concurrency fixture requires exactly two calls.";
  }
  const semantic = FIXTURE_ASSERTIONS[id]; if (!semantic || f.assert[semantic] !== true) return `Fixture assert.${semantic ?? "semantic"} must be true.`;
  if (!Array.isArray(f.steps) || f.steps.length === 0) return "Fixture must declare one or more steps.";
  return f.steps.some((step) => !plain(step) || !Array.isArray(step.argv) || !plain(step.expect)) ? "Every fixture step must declare argv and expect." : null;
}

// 执行可重试的真实 fixture 步骤，并把每次尝试及动态捕获写入同一证据流。
async function runFixtureStep(m, fixtureId, step, index, variables, project) {
  if (step.delayBeforeMs > 0) await new Promise((resolveDelay) => setTimeout(resolveDelay, step.delayBeforeMs));
  let argv;
  try { argv = step.argv.map((value) => fixtureArg(value, variables)); }
  catch (error) { await m.failed(`control.${fixtureId}.${step.id ?? index + 1}`, `Fixture arguments are invalid: ${error instanceof Error ? error.message : String(error)}`, { fixture: fixtureId, step: step.id ?? index + 1 }); return false; }
  const maxAttempts = Math.max(1, Number(step.retry?.attempts ?? 1));
  const retryCodes = new Set(step.retry?.errorCodes ?? []);
  let finalRecord;
  for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
    const suffix = maxAttempts === 1 ? "" : `.attempt-${attempt}`;
    const id = `control.${fixtureId}.${step.id ?? index + 1}${suffix}`;
    finalRecord = await m.call(id, argv, (e, r) => {
      const issue = fixtureExpectationIssue(e, r, step.expect);
      if (issue && attempt < maxAttempts && retryCodes.has(e?.error?.code)) return null;
      return issue;
    }, { fixture: fixtureId, step: step.id ?? index + 1, attempt, retryAssertion: fixtureStepRetriesReadyAssertion(step) }, project);
    const issue = fixtureExpectationIssue(finalRecord.envelope, finalRecord.result ?? { exitCode: finalRecord.exitCode }, step.expect);
    if (!issue) break;
    if (attempt >= maxAttempts || !retryCodes.has(finalRecord.envelope?.error?.code)) break;
    await new Promise((resolveDelay) => setTimeout(resolveDelay, Number(step.retry?.delayMs ?? 1000)));
  }
  if (finalRecord?.assertion?.status !== "PASS") return false;
  for (const [name, path] of Object.entries(step.capture ?? {})) {
    const captured = valueAt(finalRecord.envelope, path);
    if (captured === undefined) { await m.failed(`control.${fixtureId}.${step.id ?? index + 1}.capture.${name}`, `Capture path is absent: ${path}`); return false; }
    variables[name] = captured;
  }
  if (step.delayAfterMs > 0) await new Promise((resolveDelay) => setTimeout(resolveDelay, step.delayAfterMs));
  return true;
}
export async function runFixtureSequence(m, id, fixture, project = m.o.project, assertionId = id) {
  const bad = fixtureError(fixture, assertionId); if (bad) { await m.failed(`control.${id}`, bad); return false; }
  const variables = {};
  let succeeded = true;
  for (let index = 0; index < fixture.steps.length; index += 1) {
    const step = fixture.steps[index];
    if (!succeeded && step.always !== true) continue;
    const stepSucceeded = await runFixtureStep(m, id, step, index, variables, project);
    if (!stepSucceeded) succeeded = false;
  }
  return succeeded;
}

/** 必需夹具失败后立即停止coverage；后续只进入统一owned recovery。 */
async function runRequiredFixtureSequence(m, id, fixture, project = m.o.project, assertionId = id) {
  if (await runFixtureSequence(m, id, fixture, project, assertionId)) return true;
  const failure = m.firstTerminalFailure ?? await m.failed(`control.${id}.terminal`, "Required fixture failed before coverage could continue.", { fixture: id });
  return stopAfterTerminalFailure(failure);
}

/** BLOCKED一旦影响执行前提就必须停止coverage，只允许进入统一owned recovery。 */
async function blockTerminal(m, id, issue, meta = {}) {
  const record = await m.blocked(id, issue, meta);
  throw new MatrixTerminalFailure(record);
}

/** 必需夹具缺失时禁止继续任何coverage effect。 */
async function blockMissingRequiredFixture(m, id, issue) {
  return blockTerminal(m, id, issue, { fixture: id });
}
const concurrencyInterval = (record) => {
  const startedAt = Number(record.process?.startedAtMs);
  const endedAt = Number(record.process?.endedAtMs);
  return Number.isFinite(startedAt) && Number.isFinite(endedAt) && endedAt >= startedAt ? { startedAt, endedAt } : null;
};
export function concurrencyOverlapMs(a, b, _elapsedMs) {
  const left = concurrencyInterval(a), right = concurrencyInterval(b);
  return left && right ? Math.max(0, Math.min(left.endedAt, right.endedAt) - Math.max(left.startedAt, right.startedAt)) : 0;
}
export function concurrencySerialProof(a, b) {
  const left = concurrencyInterval(a), right = concurrencyInterval(b);
  if (!left || !right) return { serial: false, left, right };
  // 并发提交在串行Bridge下的合法终态：两个执行区间互不重叠，先后均可证明。
  return { serial: right.startedAt >= left.endedAt || left.startedAt >= right.endedAt, left, right };
}

async function runConcurrencyFixture(m, fixture) {
  const bad = fixtureError(fixture, "concurrency"); if (bad) return m.failed("control.concurrency", bad);
  const started = Date.now();
  const [a, b] = await Promise.all(fixture.calls.map((call, index) => m.call(`control.concurrency.${index + 1}`, call.argv, (e, r) => fixtureExpectationIssue(e, r, call.expect), { fixture: "concurrency", call: index + 1 })));
  const failedCall = [a, b].find((record) => record.assertion?.status !== "PASS");
  if (failedCall) return failedCall;
  const elapsed = Date.now() - started;
  if (fixture.assert?.serial === true) {
    const proof = concurrencySerialProof(a, b);
    return m.call("control.concurrency.serial", ["tools", "run", "editor.state", "--input", "{}"], () => proof.serial ? null : "Fixture calls did not prove serialized execution under concurrent submission.", { fixture: "concurrency", elapsedMs: elapsed, intervals: { a: proof.left, b: proof.right } });
  }
  const overlapMs = concurrencyOverlapMs(a, b, elapsed);
  const minimumOverlap = Number(fixture.assert.minOverlapMs ?? 1);
  return m.call("control.concurrency.overlap", ["tools", "run", "editor.state", "--input", "{}"], () => overlapMs >= minimumOverlap ? null : "Fixture calls did not prove overlapping waits.", { fixture: "concurrency", elapsedMs: elapsed, overlapMs, minimumOverlapMs: minimumOverlap });
}
async function controls(m) {
  const concurrency = m.o.fixture.concurrency;
  if (!concurrency) await blockMissingRequiredFixture(m, "control.concurrency", "No concurrency fixture supplied.");
  const concurrencyRecord = await runConcurrencyFixture(m, concurrency);
  if (concurrencyRecord.assertion?.status !== "PASS") throw new MatrixTerminalFailure(concurrencyRecord);
  for (const id of FIXTURES.filter((name) => name !== "concurrency")) {
    const fixture = m.o.fixture[id]; if (!fixture) await blockMissingRequiredFixture(m, `control.${id}`, "No fixture supplied for this perturbation."); else await runRequiredFixtureSequence(m, id, fixture);
  }
}
/** full resume 汇总同一执行身份的全部 terminal checkpoint，避免 expected set 受切片边界影响。 */
function mergeFullResumeCheckpoints(m) {
  if (m.o.capabilities.length !== 0) return;
  const projects = new Set([resolve(m.o.project)]);
  const records = new Map(m.records.map((record) => [record.id, record]));
  for (const record of m.prior.values()) {
    if (record.buildStamp !== (m.o.build_stamp ?? null) || record.project === undefined || !projects.has(resolve(record.project))) continue;
    const targetMatches = record.target === (m.o.target ?? null);
    if (!targetMatches || records.has(record.id)) continue;
    records.set(record.id, record);
    m.expected.add(record.id);
  }
  m.records = [...records.values()];
}
async function report(m) {
  mergeFullResumeCheckpoints(m);
  const missing = [...m.expected].filter((id) => !m.records.some((r) => r.id === id));
  for (const id of missing) m.records.push({ id, assertion: { status: "FAIL", issue: "Expected case is absent from resume evidence." } });
  const counts = m.records.reduce((a, r) => { const s = r.assertion?.status ?? "FAIL"; a[s] = (a[s] ?? 0) + 1; return a; }, {});
  const summary = { version: 2, generatedAt: new Date().toISOString(), expectedCaseIds: [...m.expected], discovered: m.discovered, records: m.records.length, counts, complete: !counts.FAIL && !counts.BLOCKED };
  await writeFile(resolve(m.o.evidence_dir, "matrix.json"), `${JSON.stringify(m.records, null, 2)}\n`);
  await writeFile(resolve(m.o.evidence_dir, "summary.json"), `${JSON.stringify(summary, null, 2)}\n`);
  const lines = ["# Final Live Matrix", "", `- PASS: ${counts.PASS ?? 0}`, `- FAIL: ${counts.FAIL ?? 0}`, `- BLOCKED: ${counts.BLOCKED ?? 0}`, "", "| Case | Status | Issue |", "| --- | --- | --- |"];
  for (const r of m.records) lines.push(`| ${r.id} | ${r.assertion?.status ?? "FAIL"} | ${(r.assertion?.issue ?? "").replaceAll("|", "\\|")} |`);
  await writeFile(resolve(m.o.evidence_dir, "matrix.md"), `${lines.join("\n")}\n`);
  return summary;
}
export async function acquireRunnerLock(evidenceDir) {
  await mkdir(evidenceDir, { recursive: true });
  const lockPath = resolve(evidenceDir, ".runner.lock");
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      const handle = await open(lockPath, "wx");
      const token = `${process.pid}:${Date.now()}`;
      await handle.writeFile(token, "utf8");
      return async () => {
        await handle.close();
        const owner = await readFile(lockPath, "utf8").catch(() => undefined);
        if (owner === token) await unlink(lockPath).catch(() => undefined);
      };
    } catch (error) {
      if (error?.code !== "EEXIST") throw error;
      const owner = await readFile(lockPath, "utf8").catch(() => "");
      const pid = Number(owner.split(":", 1)[0]);
      let active = Number.isInteger(pid) && pid > 0;
      if (active) {
        try { process.kill(pid, 0); }
        catch (probeError) { active = probeError?.code === "EPERM"; }
      }
      if (active) throw new Error(`E_MATRIX_RUNNER_ACTIVE: matrix runner PID ${pid} already owns ${lockPath}`);
      await unlink(lockPath).catch((unlinkError) => { if (unlinkError?.code !== "ENOENT") throw unlinkError; });
    }
  }
  throw new Error(`E_MATRIX_RUNNER_LOCK_FAILED: could not acquire ${lockPath}`);
}

async function unlinkWithSharingRetry(path) {
  for (let attempt = 1; attempt <= EVIDENCE_APPEND_MAX_ATTEMPTS; attempt += 1) {
    try { await unlink(path); return; }
    catch (error) {
      if (error?.code === "ENOENT") return;
      if ((error?.code !== "EBUSY" && error?.code !== "EPERM") || attempt === EVIDENCE_APPEND_MAX_ATTEMPTS) throw error;
      await new Promise((resolveDelay) => setTimeout(resolveDelay, EVIDENCE_APPEND_RETRY_MS));
    }
  }
}

/** project lease 防止两个 evidence runner 同时恢复同一 Dev sandbox。 */
export async function acquireProjectSourceLock(project, evidenceDir) {
  const projectRoot = resolve(project);
  const lockDirectory = resolve(projectRoot, "Library", "UnityCliValidation");
  const lockPath = resolve(lockDirectory, "matrix-source.lock");
  await assertNoSymbolicAncestor(lockDirectory, projectRoot, "Matrix project lease");
  await mkdir(lockDirectory, { recursive: true });
  await assertNoSymbolicAncestor(lockDirectory, projectRoot, "Matrix project lease");
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      await assertNoSymbolicAncestor(lockPath, projectRoot, "Matrix project lease file");
      const handle = await open(lockPath, "wx");
      const token = `${process.pid}:${Date.now()}:${resolve(evidenceDir)}`;
      await handle.writeFile(token, "utf8");
      return async () => {
        await handle.close();
        await assertNoSymbolicAncestor(lockPath, projectRoot, "Matrix project lease file");
        const owner = await readFile(lockPath, "utf8").catch(() => undefined);
        if (owner === token) await unlinkWithSharingRetry(lockPath);
      };
    } catch (error) {
      if (error?.code !== "EEXIST") throw error;
      const owner = await readFile(lockPath, "utf8").catch(() => "");
      const pid = Number(owner.split(":", 1)[0]);
      let active = Number.isInteger(pid) && pid > 0;
      if (active) {
        try { process.kill(pid, 0); }
        catch (probeError) { active = probeError?.code === "EPERM"; }
      }
      if (active) throw new Error(`E_MATRIX_PROJECT_ACTIVE: matrix runner PID ${pid} already owns ${lockPath}`);
      await unlink(lockPath).catch((unlinkError) => { if (unlinkError?.code !== "ENOENT") throw unlinkError; });
    }
  }
  throw new Error(`E_MATRIX_PROJECT_LOCK_FAILED: could not acquire ${lockPath}`);
}

/** 最终恢复调用与普通调用相同；超时后重试决策属于调用方。 */
export async function callRecoveryEffect(m, id, argv, assertion, meta) { return m.call(id, argv, assertion, meta); }

/** package.resolve必须以完整fresh PackageInfo与文件hash终态完成，不能只接受generic success。 */
export function packageResolveReconciliationIssue(e, r, expected = {}) {
  if (r.exitCode !== 0) return `package.resolve reconciliation exited ${r.exitCode}.`;
  const issue = assertPositiveSuccess(e);
  if (issue) return issue;
  const data = e?.data;
  const fields = ["status", "manifestHash", "lockHash", "packageSnapshotHash", "packageCount", "packages"];
  if (Object.keys(data).some((field) => !fields.includes(field)) || fields.some((field) => !Object.hasOwn(data, field))) return "package.resolve reconciliation output fields do not match the terminal contract.";
  const sha256 = /^[0-9a-f]{64}$/;
  if (data.status !== "resolved") return "package.resolve reconciliation did not reach resolved terminal state.";
  for (const field of ["manifestHash", "lockHash", "packageSnapshotHash"]) if (!sha256.test(data[field] ?? "")) return `package.resolve reconciliation has invalid ${field}.`;
  if (expected.manifestHash && data.manifestHash !== expected.manifestHash) return "package.resolve reconciliation manifestHash differs from the restored snapshot.";
  if (expected.lockHash && data.lockHash !== expected.lockHash) return "package.resolve reconciliation lockHash differs from the restored snapshot.";
  if (!Number.isInteger(data.packageCount) || data.packageCount < 0 || !Array.isArray(data.packages) || data.packages.length !== data.packageCount) return "package.resolve reconciliation package snapshot is incomplete.";
  if (data.packages.some((entry) => !plain(entry) || Object.keys(entry).sort().join("\0") !== ["name", "source", "version"].join("\0") || typeof entry.name !== "string" || typeof entry.version !== "string" || typeof entry.source !== "string")) return "package.resolve reconciliation package entry is malformed.";
  return null;
}

function restoredPackageFileHash(snapshot, path) {
  const file = snapshot?.document?.files?.find((entry) => entry.path === path);
  return file?.exists === true && /^[0-9a-f]{64}$/.test(file.sha256 ?? "") ? file.sha256 : null;
}

/** 最终清理先恢复业务语义，再恢复 frozen bytes 并通过公开 Tool reconcile Editor。 */
async function recoverFinalMatrixState(m) {
  // 恢复 fixture（pipeline/reset/scene.load）只在 EditMode 下有效；
  // 终态失败可能发生在 PlayMode case 之后，必须先证明回到 EditMode 再进入任何恢复 fixture。
  if (!await setCasePlayMode(m, "recovery-enter-edit", false)) {
    await m.blocked("control.recovery-enter-edit", "Editor could not return to ready EditMode before final recovery fixtures.");
    return false;
  }
  const pipelinePassed = m.o.fixture.pipeline_restore
    ? await runFixtureSequence(m, "pipeline-final-restore", m.o.fixture.pipeline_restore, m.o.project, "pipeline_restore")
    : true;
  let resetPassed = false;
  if (!m.o.fixture.reset) await m.blocked("control.final-reset", "No reproducible final reset fixture supplied.");
  else resetPassed = await runFixtureSequence(m, "final-reset", m.o.fixture.reset, m.o.project, "reset");

  try {
    m.projectSnapshot = await updateOwnedProjectSnapshotStatus(m.projectSnapshot, "semantic-reset-complete", { semanticReset: { pipelinePassed, resetPassed } });
    m.projectSnapshot = await restoreOwnedProjectSnapshot(m.projectSnapshot);
  } catch (error) {
    await m.failed("control.source-restore.filesystem", `Exact Dev project restore failed: ${error instanceof Error ? error.message : String(error)}`, { cleanup: { projectSnapshot: true } });
    return false;
  }

  const reconciliation = { packageResolve: "pending", editorRefresh: "pending", sceneReload: "pending", editorState: "pending" };
  let reconciliationPassed = true;
  // 即使 restore 得到相同 bytes，PackageManager/AssetDatabase 内存也可能仍持有 effect 期间状态；公开reconcile必须绑定刚恢复的文件摘要。
  const expectedPackageHashes = {
    manifestHash: restoredPackageFileHash(m.projectSnapshot, "Packages/manifest.json"),
    lockHash: restoredPackageFileHash(m.projectSnapshot, "Packages/packages-lock.json")
  };
  const packageRecord = !expectedPackageHashes.manifestHash || !expectedPackageHashes.lockHash
    ? await m.failed("control.source-restore.package-resolve", "Restored Package manifest/lock hashes are unavailable; package.resolve reconciliation is forbidden.", { cleanup: { projectSnapshot: true, reconcile: "package.resolve" } })
    : await callRecoveryEffect(m, "control.source-restore.package-resolve", ["tools", "run", "package.resolve", "--input", "{}"], (e, r) => packageResolveReconciliationIssue(e, r, expectedPackageHashes), { cleanup: { projectSnapshot: true, reconcile: "package.resolve" }, expectedPackageHashes });
  reconciliation.packageResolve = packageRecord.assertion?.status === "PASS" ? "PASS" : "FAIL";
  reconciliationPassed = reconciliationPassed && packageRecord.assertion?.status === "PASS";

  const refreshRecord = await callRecoveryEffect(m, "control.source-restore.editor-refresh", ["tools", "run", "editor.refresh", "--input", "{}"], (e, r) => {
    if (r.exitCode !== 0) return `editor.refresh reconciliation exited ${r.exitCode}.`;
    const issue = assertPositiveSuccess(e);
    return issue ?? (e?.data?.editorState?.status === "ready" && e?.data?.editorState?.compile === false && e?.data?.editorState?.import === false ? null : "editor.refresh did not prove stable ready state.");
  }, { cleanup: { projectSnapshot: true, reconcile: "editor.refresh" } });
  reconciliation.editorRefresh = refreshRecord.assertion?.status === "PASS" ? "PASS" : "FAIL";
  reconciliationPassed = reconciliationPassed && refreshRecord.assertion?.status === "PASS";
  const sceneRecord = await callRecoveryEffect(m, "control.source-restore.scene-load", ["tools", "run", "scene.load", "--input", JSON.stringify({ path: "Assets/UnityCLIValidation/MatrixScene.unity" })], (e, r) => {
    if (r.exitCode !== 0) return `scene.load reconciliation exited ${r.exitCode}.`;
    const issue = assertPositiveSuccess(e);
    return issue ?? (e?.data?.scene?.path === "Assets/UnityCLIValidation/MatrixScene.unity" && e?.data?.scene?.isDirty === false ? null : "Restored MatrixScene was not loaded cleanly.");
  }, { cleanup: { projectSnapshot: true, reconcile: "scene.load" } });
  reconciliation.sceneReload = sceneRecord.assertion?.status === "PASS" ? "PASS" : "FAIL";
  reconciliationPassed = reconciliationPassed && sceneRecord.assertion?.status === "PASS";

  const stateRecord = await m.call("control.source-restore.editor-state", ["tools", "run", "editor.state", "--input", "{}"], (e, r) => {
    if (r.exitCode !== 0 || !isReadyEditorState(e, false)) return "Final project reconciliation did not prove ready EditMode.";
    return e?.data?.state?.activeScene?.value?.path === "Assets/UnityCLIValidation/MatrixScene.unity"
      && e?.data?.state?.activeScene?.value?.isDirty === false
      && Array.isArray(e?.data?.blockers) && e.data.blockers.length === 0
      ? null : "Final project reconciliation did not prove clean MatrixScene with no blockers.";
  }, { cleanup: { projectSnapshot: true, reconcile: "editor.state" }, retryAssertion: true });
  reconciliation.editorState = stateRecord.assertion?.status === "PASS" ? "PASS" : "FAIL";
  reconciliationPassed = reconciliationPassed && stateRecord.assertion?.status === "PASS";

  let closure;
  try { closure = await verifyOwnedProjectSnapshotClosure(m.projectSnapshot); }
  catch (error) { closure = { ok: false, issues: [error instanceof Error ? error.message : String(error)] }; }
  reconciliation.closure = closure.ok ? "PASS" : "FAIL";
  if (!reconciliationPassed || !closure.ok) {
    await updateOwnedProjectSnapshotStatus(m.projectSnapshot, "cleanup-failed", { reconciliation, closureIssues: closure.issues }).catch(() => undefined);
    await m.failed("control.source-restore.reconcile", `Exact Dev project reconciliation failed: ${closure.issues.join("; ") || "one or more public reconciliation calls failed"}`, { cleanup: { projectSnapshot: true }, reconciliation });
    return false;
  }
  try {
    m.projectSnapshot = await markOwnedProjectSnapshotReconciled(m.projectSnapshot, reconciliation);
    m.projectSnapshot = await markOwnedProjectSnapshotClean(m.projectSnapshot);
    return pipelinePassed && resetPassed;
  } catch (error) {
    await updateOwnedProjectSnapshotStatus(m.projectSnapshot, "cleanup-failed", { reconciliation, cleanupError: error instanceof Error ? error.message : String(error) }).catch(() => undefined);
    await m.failed("control.source-restore.clean", `Exact Dev project clean proof failed: ${error instanceof Error ? error.message : String(error)}`, { cleanup: { projectSnapshot: true } });
    return false;
  }
}

async function recordRunnerFailure(m, id, error, meta = {}) {
  const record = {
    id,
    at: new Date().toISOString(),
    project: m.o.project,
    target: m.o.target ?? null,
    buildStamp: m.o.build_stamp ?? null,
    assertion: { status: "FAIL", issue: error instanceof Error ? error.message : String(error) },
    ...meta
  };
  m.expected.add(id);
  m.records.push(record);
  await appendEvidenceLine(m.jsonl, `${JSON.stringify(record)}\n`).catch(() => undefined);
  return record;
}

export async function runMatrix(options) {
  const m = new Matrix({ ...options, capabilities: options.capabilities ?? [] });
  await m.init();
  m.invalidateSessionControls();
  let primaryError;
  try {
    await recoverAllPendingOwnedBakeSnapshots(m);
    if (!await setCasePlayMode(m, "initial-editor", false)) {
      await m.blocked("control.initial-edit-mode", "Editor did not reach EditMode before editor-phase setup.");
    } else {
      if (!m.o.fixture.setup) await blockMissingRequiredFixture(m, "control.setup", "No reproducible Dev setup fixture supplied.");
      else await runRequiredFixtureSequence(m, "setup", m.o.fixture.setup);
      const initialRestore = m.o.fixture.playmode_restore;
      const initialRestoreReady = !initialRestore || await runRequiredFixtureSequence(m, "playmode-initial-restore", initialRestore, m.o.project, "playmode_restore");
      if (!initialRestoreReady) {
        await m.blocked("control.initial-edit-mode", "PlayMode options could not be restored before editor-phase discovery.");
      } else {
        await discover(m);
        // controls-only 切片只执行全局扰动fixture；capability coverage与PlayMode coverage由各自独立切片进程完成。
        if (!m.o.controls_only) {
          await coverToolsWithPipelineFixtures(m, "editor");
          if (m.o.capabilities.length === 0 || m.o.include_playmode) {
          if (!m.o.fixture.reset) await blockMissingRequiredFixture(m, "control.reset", "No reproducible pre-PlayMode reset fixture supplied.");
          else await runRequiredFixtureSequence(m, "reset", m.o.fixture.reset);
          const fastPlayMode = m.o.fixture.playmode_fast;
          m.invalidateSessionControls();
          const fastPlayModeReady = fastPlayMode
            ? await runRequiredFixtureSequence(m, "playmode-fast", fastPlayMode, m.o.project, "playmode_fast")
            : true;
          if (fastPlayModeReady) await playMode(m);
          else await m.blocked("control.playmode-fast", "Bulk PlayMode options fixture failed; PlayMode cases were not executed.");
          if (!await setCasePlayMode(m, "post-playmode", false)) stopAfterTerminalFailure(await m.failed("control.post-playmode-edit", "Editor did not return to EditMode after bulk PlayMode coverage."));
          const restorePlayMode = m.o.fixture.playmode_restore;
          if (fastPlayMode && !restorePlayMode) stopAfterTerminalFailure(await m.failed("control.playmode-restore", "PlayMode options were not restored after bulk PlayMode coverage."));
          else if (fastPlayMode) await runRequiredFixtureSequence(m, "playmode-restore", restorePlayMode, m.o.project, "playmode_restore");
          }
        }
        if (m.o.capabilities.length === 0 || m.o.controls_only) await controls(m);
      }
    }
  } catch (error) {
    if (!(error instanceof MatrixTerminalFailure)) {
      primaryError = error;
      await recordRunnerFailure(m, "control.runner.exception", error, { runner: { phase: "coverage" } });
    }
  }
  try {
    await recoverFinalMatrixState(m);
  } catch (cleanupError) {
    await recordRunnerFailure(m, "control.source-restore.exception", cleanupError, { cleanup: { projectSnapshot: true, unhandled: true } });
  }
  const result = { summary: await report(m), records: m.records };
  if (primaryError) throw primaryError;
  return result;
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  let releaseLock;
  let releaseProjectLock;
  try {
    const options = parseOptions(process.argv.slice(2));
    if (!options.fixture_file) throw new Error("Required --fixture-file is missing.");
    const fixtureDocument = JSON.parse(await readFile(resolve(options.fixture_file), "utf8"));
    if (!plain(fixtureDocument)) throw new Error("--fixture-file must contain one JSON object.");
    options.fixture = fixtureDocument;
    releaseLock = await acquireRunnerLock(options.evidence_dir);
    releaseProjectLock = await acquireProjectSourceLock(options.project, options.evidence_dir);
    const result = await runMatrix(options);
    process.exitCode = result.summary.complete ? 0 : 1;
  } catch (error) {
    console.error(error instanceof Error ? error.stack : String(error));
    process.exitCode = 2;
  } finally {
    let releaseError;
    try { if (releaseProjectLock) await releaseProjectLock(); }
    catch (error) { releaseError = error; }
    try { if (releaseLock) await releaseLock(); }
    catch (error) { releaseError ??= error; }
    if (releaseError) {
      console.error(releaseError instanceof Error ? releaseError.stack : String(releaseError));
      process.exitCode = 2;
    }
  }
}
