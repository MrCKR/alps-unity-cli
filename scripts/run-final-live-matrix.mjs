#!/usr/bin/env node
/** Final live matrix; invokes only the public built CLI with shell:false. */
import { access, appendFile, cp, mkdir, open, readFile, rm, unlink, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const FLAGS = new Set(["--yes", "--dry-run", "--runtime", "--wait", "--timeout-ms", "--wait-timeout-ms", "--instance", "--target", "--bridge-endpoint", "--artifact-dir", "--project", "--raw"]);
const VALUE_FLAGS = new Set(["--timeout-ms", "--wait-timeout-ms", "--instance", "--target", "--bridge-endpoint", "--artifact-dir", "--project"]);
const FIXTURES = ["concurrency", "timeout_cancel", "stale_target", "domain_reload", "shell_quoting", "prefab_stage", "ui_cache"];
// Package import 和 PlayMode domain reload 在大型项目中可超过 30 秒，矩阵必须等待完整公开 timeout 窗口。
const TRANSIENT_MAX_ATTEMPTS = 120;
export const BUILD_CANCEL_UNITY_FIXTURE = resolve(".plan/evidence/final-cleanup/delayed-unity.cmd");
const plain = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const nameOf = (v) => typeof v === "string" ? v : v?.name ?? v?.key;
const descriptorOf = (e) => e?.data?.descriptor ?? e?.data;
const CLOSED_PROJECT_STATE = ["Assets", "Packages", "ProjectSettings"];
const CLOSED_PROJECT_OUTPUTS = ["Artifacts", "Builds"];
const CLOSED_PROJECT_BASELINE_MARKER = ".unity-cli-baseline-complete.json";

/** 验证 closed project baseline 完整；恢复前目录与完成标记必须全部有效。 */
export async function validateClosedProjectBaseline(baseline) {
  for (const name of CLOSED_PROJECT_STATE) await access(resolve(baseline, name));
  const marker = JSON.parse(await readFile(resolve(baseline, CLOSED_PROJECT_BASELINE_MARKER), "utf8"));
  if (marker?.version !== 1 || JSON.stringify(marker.stateDirectories) !== JSON.stringify(CLOSED_PROJECT_STATE)) {
    throw new Error("Closed project baseline completion marker is invalid.");
  }
}

/** 保存 closed project 的持久化源码状态；完成标记最后写入，半份快照永远不可恢复。 */
export async function snapshotClosedProjectBaseline(project, baseline) {
  await rm(baseline, { recursive: true, force: true });
  await mkdir(baseline, { recursive: true });
  for (const name of CLOSED_PROJECT_STATE) {
    const source = resolve(project, name);
    await access(source);
    await cp(source, resolve(baseline, name), { recursive: true, force: true });
  }
  await writeFile(resolve(baseline, CLOSED_PROJECT_BASELINE_MARKER), JSON.stringify({ version: 1, stateDirectories: CLOSED_PROJECT_STATE }), "utf8");
  await validateClosedProjectBaseline(baseline);
}

/** 确保 baseline 可恢复；已有 closed history 时禁止从可能已变异的项目重建。 */
export async function ensureClosedProjectBaseline(project, baseline, allowSnapshot) {
  try {
    await validateClosedProjectBaseline(baseline);
    return "existing";
  } catch (error) {
    if (!allowSnapshot) throw new Error(`Closed project baseline is missing or incomplete: ${error instanceof Error ? error.message : String(error)}`);
    await snapshotClosedProjectBaseline(project, baseline);
    return "created";
  }
}

/** 恢复 closed project 持久化状态，不触碰 UnityLockfile 和 Library 派生缓存。 */
export async function restoreClosedProjectBaseline(project, baseline) {
  await validateClosedProjectBaseline(baseline);
  for (const name of CLOSED_PROJECT_STATE) {
    const source = resolve(baseline, name);
    const destination = resolve(project, name);
    await rm(destination, { recursive: true, force: true });
    await cp(source, destination, { recursive: true, force: true });
  }
  for (const name of CLOSED_PROJECT_OUTPUTS) await rm(resolve(project, name), { recursive: true, force: true });
}

export function parseOptions(argv) {
  const o = { resume: false, include_playmode: false, timeoutMs: 120000, fixture: {}, cli_args: [], allow_destructive_projects: [], capabilities: [] };
  for (let i = 0; i < argv.length; i += 1) {
    const key = argv[i]; if (!key.startsWith("--")) throw new Error(`Unexpected argument: ${key}`);
    if (key === "--resume") { o.resume = true; continue; }
    if (key === "--include-playmode") { o.include_playmode = true; continue; }
    const value = argv[++i]; if (value === undefined) throw new Error(`Missing value for ${key}`);
    const name = key.slice(2).replaceAll("-", "_");
    if (name.startsWith("fixture_")) o.fixture[name.slice(8)] = JSON.parse(value);
    else if (name === "cli_arg") o.cli_args.push(value);
    else if (name === "allow_destructive_project") o.allow_destructive_projects.push(value);
    else if (name === "capability") o.capabilities.push(value);
    else if (name === "timeout_ms") o.timeoutMs = Number(value);
    else o[name] = value;
  }
  for (const key of ["cli", "project", "evidence_dir", "closed_job_project"]) if (!o[key]) throw new Error(`Required --${key.replaceAll("_", "-")} is missing.`);
  if (!Number.isFinite(o.timeoutMs) || o.timeoutMs <= 0) throw new Error("--timeout-ms must be positive.");
  return o;
}

/** Parses public argv and separates allowed execution flags from JSON input. */
export function parsePublicExampleArgv(example, kind, expectedName) {
  const argv = Array.isArray(example) ? example : example?.argv;
  if (!Array.isArray(argv) || argv.some((v) => typeof v !== "string")) throw new Error("Descriptor example must contain a string argv array.");
  const start = argv.findIndex((v) => v === "tools" || v === "context"); const command = argv.slice(start < 0 ? 0 : start);
  const expected = kind === "tool" ? ["tools", "run", expectedName] : ["context", "read", expectedName];
  if (command.slice(0, 3).join("\0") !== expected.join("\0")) throw new Error(`Example must use public ${expected.join(" ")} argv.`);
  let input = {}; const flags = [];
  for (let i = 3; i < command.length; i += 1) {
    const token = command[i];
    if (token === "--input") { if (++i >= command.length) throw new Error("--input requires JSON."); input = JSON.parse(command[i]); }
    else if (token.startsWith("--input=")) input = JSON.parse(token.slice(8));
    else if (token === "--args" || token === "--dryRun" || token.startsWith("--args=") || token.startsWith("--dryRun=")) throw new Error("Non-contract args/dryRun wire fields are forbidden.");
    else if (FLAGS.has(token)) { flags.push(token); if (VALUE_FLAGS.has(token)) { if (++i >= command.length) throw new Error(`${token} requires a value.`); flags.push(command[i]); } }
    else if ([...FLAGS].some((f) => token.startsWith(`${f}=`))) flags.push(token);
    else if (token.startsWith("--")) {
      const key = token.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
      if (!key) throw new Error("Business flag name cannot be empty.");
      const next = command[i + 1];
      if (next !== undefined && !next.startsWith("--")) { input[key] = exampleFlagValue(next); i += 1; }
      else input[key] = true;
    }
    else throw new Error(`Unsupported positional example token: ${token}`);
  }
  if (!plain(input) || Object.hasOwn(input, "dryRun")) throw new Error("Input must be an object without the non-contract dryRun field.");
  return { input, flags };
}
function exampleFlagValue(value) {
  if (value === "true") return true;
  if (value === "false") return false;
  if (/^-?(?:0|[1-9]\d*)(?:\.\d+)?$/.test(value)) return Number(value);
  return value;
}
/** Recursively checks required fields and primitive/object/array schema types. */
function businessPayload(e) {
  const data = e?.data;
  if (plain(data) && Object.hasOwn(data, "result") && (plain(data.tool) || plain(data.context) || data.command === "project-tool.run" || data.command === "runtime-tool.run" || data.command === "context.read")) return data.result;
  return data;
}
export function assertSuccess(e, schema = {}) { const payload = businessPayload(e); if (e?.ok !== true || !plain(payload)) return "Expected successful JSON envelope with object business data."; return assertSchema(payload, schema, "$.data.result"); }

/** 正向调用除 schema 外还必须排除已明确失败的同步等待/job 终态。 */
export function assertPositiveSuccess(e, schema = {}) {
  const issue = assertSuccess(e, schema);
  if (issue) return issue;
  const payload = businessPayload(e);
  const failedStatuses = new Set(["failed", "timeout"]);
  if (failedStatuses.has(payload.status)) return `Positive invocation reported terminal status ${payload.status}.`;
  const failedJob = Array.isArray(payload.jobs) ? payload.jobs.find((job) => plain(job) && failedStatuses.has(job.status)) : null;
  return failedJob ? `Positive invocation reported failed child job ${failedJob.jobId ?? "unknown"}.` : null;
}
function assertSchema(value, schema, path) {
  if (!plain(schema)) return null;
  if (schema.type && !typeIs(value, schema.type)) return `${path} must be ${schema.type}.`;
  if (schema.type === "object" || schema.properties) {
    if (!plain(value)) return `${path} must be object.`;
    for (const key of schema.required ?? []) if (!Object.hasOwn(value, key)) return `${path}.${key} is required.`;
    for (const [key, child] of Object.entries(schema.properties ?? {})) if (Object.hasOwn(value, key)) { const issue = assertSchema(value[key], child, `${path}.${key}`); if (issue) return issue; }
  }
  if (schema.type === "array" && Array.isArray(value)) for (let i = 0; i < value.length; i += 1) { const issue = assertSchema(value[i], schema.items ?? {}, `${path}[${i}]`); if (issue) return issue; }
  return null;
}
function typeIs(v, t) { return t === "array" ? Array.isArray(v) : t === "object" ? plain(v) : t === "integer" ? Number.isInteger(v) : typeof v === t; }

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
  const x = e?.error; if (e?.ok !== false || !plain(x)) return "Expected failed JSON envelope.";
  if (typeof x.code !== "string" || typeof x.message !== "string" || typeof x.transient !== "boolean" || typeof x.retryAfterMs !== "number" || x.retryAfterMs < 0 || !Array.isArray(x.nextActions) || x.nextActions.some((v) => typeof v !== "string")) return "Error fields have invalid types.";
  if (requireSchemaDetails && (!plain(x.details) || typeof x.details.path !== "string" || typeof x.details.expected !== "string" || typeof x.details.actual !== "string")) return "Error details must contain typed path, expected, and actual.";
  return null;
}
export function runCli(cli, argv, timeoutMs) { return new Promise((done) => {
  const child = spawn(cli, argv, { shell: false, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
  let stdout = "", stderr = ""; let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    // Windows 的 child.kill() 不保证终止孙进程；taskkill 使切片中断后不会遗留 CLI 树。
    if (process.platform === "win32" && child.pid) spawn("taskkill", ["/pid", String(child.pid), "/t", "/f"], { windowsHide: true, stdio: "ignore" });
    else child.kill("SIGKILL");
  }, timeoutMs);
  child.stdout.on("data", (v) => { stdout += v; }); child.stderr.on("data", (v) => { stderr += v; }); child.on("error", (v) => { stderr += String(v); });
  child.on("close", (exitCode) => { clearTimeout(timer); done({ exitCode, stdout, stderr, timedOut }); });
}); }

class Matrix {
  constructor(options) { this.o = options; this.records = []; this.prior = new Map(); this.expected = new Set(); this.discovered = { tools: [], contexts: [] }; this.descriptors = new Map(); this.closedBaselineReady = false; }
  async init() { await mkdir(this.o.evidence_dir, { recursive: true }); this.jsonl = resolve(this.o.evidence_dir, "calls.jsonl"); this.closedBaseline = resolve(this.o.evidence_dir, "closed-project-baseline"); if (!this.o.resume) { await writeFile(this.jsonl, ""); await rm(this.closedBaseline, { recursive: true, force: true }); } else { try { for (const line of (await readFile(this.jsonl, "utf8")).split(/\r?\n/)) if (line) { const r = JSON.parse(line); this.prior.set(r.id, r); } } catch {} } }
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
      if (globalTransition || id.startsWith("control.playmode-fast.") || id.startsWith("control.playmode-restore.") || id.startsWith("control.playmode-initial-restore.") || id.startsWith("control.play-baseline.initial-editor.") || id.startsWith("control.play-baseline.post-playmode.")) this.prior.delete(id);
    }
  }
  /** PlayMode coverage 入口再次清除 transition，确保当前进程真实进入并退出。 */
  invalidateFailedPlayModeControls() {
    for (const id of ["tool.editor.play.transition", "playmode.ready", "tool.editor.stop.transition", "playmode.exit-ready"]) this.prior.delete(id);
  }
  async restoreClosedBaseline(caseId, project) {
    const id = `control.closed-baseline.${caseId}`;
    this.expected.add(id);
    try {
      if (!this.closedBaselineReady) {
        const hasClosedHistory = [...this.prior.values(), ...this.records]
          .some((record) => record?.project && resolve(record.project) === resolve(project));
        await ensureClosedProjectBaseline(project, this.closedBaseline, !this.o.resume || !hasClosedHistory);
        this.closedBaselineReady = true;
      }
      await restoreClosedProjectBaseline(project, this.closedBaseline);
      const record = { id, at: new Date().toISOString(), project, target: null, endpoint: null, buildStamp: this.o.build_stamp ?? null, baseline: { path: this.closedBaseline, stateDirectories: CLOSED_PROJECT_STATE, preserved: ["Library", "Temp", "UnityLockfile"] }, assertion: { status: "PASS" } };
      this.records.push(record); await appendFile(this.jsonl, `${JSON.stringify(record)}\n`); return true;
    } catch (error) {
      const record = { id, at: new Date().toISOString(), project, target: null, endpoint: null, buildStamp: this.o.build_stamp ?? null, assertion: { status: "FAIL", issue: `Closed project baseline restore failed: ${error instanceof Error ? error.message : String(error)}` } };
      this.records.push(record); await appendFile(this.jsonl, `${JSON.stringify(record)}\n`); return false;
    }
  }
  argv(base, project = this.o.project) { const a = [...base, "--project", project]; if (resolve(project) === resolve(this.o.project)) { if (this.o.target) a.push("--instance", this.o.target); if (this.o.endpoint) a.push("--bridge-endpoint", this.o.endpoint); } return [...a, "--raw"]; }
  /** 复用证据必须同时绑定 build、project 与 target，避免跨环境跳过 case。 */
  hasReusablePass(id, project = this.o.project) {
    const old = this.prior.get(id);
    return old?.assertion?.status === "PASS"
      && old.buildStamp === (this.o.build_stamp ?? null)
      && old.project !== undefined
      && resolve(old.project) === resolve(project)
      && old.target === (this.o.target ?? null);
  }
  async state(project) { const argv = this.argv(["context", "read", "editor.state", "--input", "{}", "--timeout-ms", "10000"], project); const result = await runCli(this.o.cli, [...this.o.cli_args, ...argv], Math.min(this.o.timeoutMs, 15000)); let envelope; try { envelope = JSON.parse(result.stdout); } catch {} return { capturedAt: new Date().toISOString(), argv, result, envelope, target: this.o.target ?? null, endpoint: this.o.endpoint ?? null, buildStamp: this.o.build_stamp ?? null }; }
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
      const preState = base[0] === "context" && base[2] === "editor.state" ? null : await this.state(project);
      const result = await runCli(this.o.cli, [...this.o.cli_args, ...argv], this.o.timeoutMs);
      let envelope; let parse;
      try { envelope = JSON.parse(result.stdout); } catch (error) { parse = String(error); }
      const issue = parse ? `stdout is not a complete JSON envelope: ${parse}` : assertion(envelope, result);
      attempts.push({ attempt, at: new Date().toISOString(), exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr, envelope, preState, ...(parse ? { parse } : {}), ...(issue ? { issue } : {}) });
      final = { result, envelope, preState, issue };
      if (issue) {
        const checkpoint = { id, at: new Date().toISOString(), argv, input: inputFrom(argv), exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr, envelope, preState, target: this.o.target ?? null, endpoint: this.o.endpoint ?? null, project, buildStamp: this.o.build_stamp ?? null, assertion: { status: "FAIL", issue }, retryCheckpoint: { attempt }, ...meta };
        await appendFile(this.jsonl, `${JSON.stringify(checkpoint)}\n`);
      }
      const retryAssertion = meta.retryAssertion === true && envelope?.ok === true;
      if (!issue || (!retryAssertion && envelope?.error?.transient !== true) || attempt === TRANSIENT_MAX_ATTEMPTS) break;
      const requestedDelay = Number(envelope?.error?.retryAfterMs);
      const retryAfterMs = Number.isFinite(requestedDelay) && requestedDelay > 0 ? requestedDelay : 1000;
      await new Promise((resolveDelay) => setTimeout(resolveDelay, Math.min(2000, Math.max(500, retryAfterMs))));
    }

    const { result, envelope, preState, issue } = final;
    const r = { id, at: new Date().toISOString(), argv, input: inputFrom(argv), exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr, envelope, preState, target: this.o.target ?? null, endpoint: this.o.endpoint ?? null, project, buildStamp: this.o.build_stamp ?? null, assertion: { status: issue ? "FAIL" : "PASS", ...(issue ? { issue } : {}) }, ...(attempts.length > 1 ? { attempts } : {}), ...meta };
    if (existingIndex >= 0) this.records[existingIndex] = r; else this.records.push(r);
    await appendFile(this.jsonl, `${JSON.stringify(r)}\n`);
    return r;
  }
  async blocked(id, issue, meta = {}) { this.expected.add(id); const r = { id, at: new Date().toISOString(), project: this.o.project, target: this.o.target ?? null, endpoint: this.o.endpoint ?? null, buildStamp: this.o.build_stamp ?? null, assertion: { status: "BLOCKED", issue }, ...meta }; this.records.push(r); await appendFile(this.jsonl, `${JSON.stringify(r)}\n`); return r; }
  async notApplicable(id, reason, meta = {}) { this.expected.add(id); const r = { id, at: new Date().toISOString(), project: this.o.project, target: this.o.target ?? null, endpoint: this.o.endpoint ?? null, buildStamp: this.o.build_stamp ?? null, applicability: "not-applicable", assertion: { status: "PASS", reason }, ...meta }; this.records.push(r); await appendFile(this.jsonl, `${JSON.stringify(r)}\n`); return r; }
  async failed(id, issue, meta = {}) { this.expected.add(id); const r = { id, at: new Date().toISOString(), project: this.o.project, target: this.o.target ?? null, endpoint: this.o.endpoint ?? null, buildStamp: this.o.build_stamp ?? null, assertion: { status: "FAIL", issue }, ...meta }; this.records.push(r); await appendFile(this.jsonl, `${JSON.stringify(r)}\n`); return r; }
}
function inputFrom(argv) { const i = argv.indexOf("--input"); try { return i < 0 ? {} : JSON.parse(argv[i + 1]); } catch { return null; } }
async function discover(m) {
  const groups = await m.call("tools.groups", ["tools", "group"], (e) => assertSuccess(e, { required: ["groups"], properties: { groups: { type: "array" } } }));
  for (const item of groups.envelope?.data?.groups ?? []) { const group = nameOf(item); if (!group) continue; const listed = await m.call(`tools.group.${group}`, ["tools", "group", "list", group, "--include-custom"], (e) => assertSuccess(e, { required: ["tools"] })); for (const tool of listed.envelope?.data?.tools ?? []) { const n = nameOf(tool); if (n && !m.discovered.tools.includes(n)) m.discovered.tools.push(n); } }
  const contexts = await m.call("contexts.list", ["context", "list", "--include-custom"], (e) => assertSuccess(e, { required: ["contexts"] })); for (const item of contexts.envelope?.data?.contexts ?? []) { const n = nameOf(item); if (n && !m.discovered.contexts.includes(n)) m.discovered.contexts.push(n); }
  const ext = await m.call("extensions.list", ["extensions", "list"], (e) => assertSuccess(e, {})); const doc = await m.call("extensions.doctor", ["extensions", "doctor"], (e) => assertSuccess(e, {})); const doctor = new Map((doc.envelope?.data?.extensions ?? doc.envelope?.data?.results ?? []).map((v) => [nameOf(v), v]));
  for (const e of ext.envelope?.data?.extensions ?? []) { const contributed = e?.tools ?? e?.contexts ?? e?.providesTools ?? []; const d = doctor.get(nameOf(e)); if (contributed.length && (!d || d.available === false || d.physicalInstalled === false || d.missingDependencies?.length)) await m.blocked(`extension.${nameOf(e)}.availability`, "Extension contribution is unavailable, missing dependencies, or not physically installed."); }
}
async function getDescriptor(m, kind, name) { const cached = m.descriptors.get(`${kind}:${name}`); if (cached) return cached; const r = await m.call(`${kind}.${name}.describe`, kind === "tool" ? ["tools", "describe", name] : ["context", "describe", name], (e) => assertSuccess(e, {})); const d = descriptorOf(r.envelope); if (d) m.descriptors.set(`${kind}:${name}`, d); return d; }
function matrixExampleFlags(flags) {
  const result = [];
  const routed = new Set(["--project", "--instance", "--target", "--bridge-endpoint"]);
  for (let index = 0; index < flags.length; index += 1) {
    if (flags[index] === "--raw") continue;
    if (routed.has(flags[index])) { index += 1; continue; }
    result.push(flags[index]);
  }
  return result;
}
function argvFor(kind, name, input, flags = []) { return [...(kind === "tool" ? ["tools", "run", name] : ["context", "read", name]), "--input", JSON.stringify(input), ...matrixExampleFlags(flags)]; }
export function capabilityProject(m, kind, descriptor) {
  if (kind !== "tool") return m.o.project;
  if (["build.cancel", "build.status"].includes(descriptor?.name)) return m.o.closed_job_project;
  if (descriptor?.supportStatus === "batch-job" || descriptor?.handler === "job") return m.o.closed_job_project;
  return descriptor?.group === "package" && descriptor?.destructiveHint && descriptor?.supportStatus !== "live-bridge" ? m.o.closed_job_project : m.o.project;
}
function matrixCaseId(kind, name, id, phase) { return phase === "play" ? `${kind}.${name}.play.${id}` : `${kind}.${name}.${id}`; }

/** 隔离项目调用必须同步重写业务 project 字段，禁止 example 绕回主 Dev 项目。 */
export function routeCapabilityInput(input, project, primaryProject) {
  if (!plain(input) || resolve(project) === resolve(primaryProject)) return input;
  const routed = { ...input };
  for (const key of ["project", "projectPath"]) if (typeof routed[key] === "string") routed[key] = project;
  return routed;
}

function startedJobs(record) {
  const data = record.envelope?.data;
  const candidates = [data, ...(Array.isArray(data?.jobs) ? data.jobs : [])];
  return candidates.filter((item) => plain(item) && typeof item.jobId === "string" && ["queued", "running"].includes(item.status));
}

/** capability case 不得把后台进程泄漏到后续 case；先读取终态，只有活跃 job 才执行取消。 */
export function jobCleanupCancelIssue(envelope, result, jobId) {
  if (result.exitCode === 0 && envelope?.ok === true && envelope?.data?.status === "cancelled") return null;
  const terminalRace = result.exitCode !== 0
    && envelope?.ok === false
    && envelope?.error?.code === "JOB_CANCEL_FAILED"
    && /^Job is already terminal: (?:succeeded|failed|cancelled|timeout)$/.test(envelope?.error?.message ?? "");
  return terminalRace ? null : `Failed to cancel leaked job ${jobId}.`;
}

async function cleanupStartedJobs(m, caseId, record, project) {
  const jobs = startedJobs(record);
  for (let index = 0; index < jobs.length; index += 1) {
    const job = jobs[index];
    const status = await m.call(`${caseId}.cleanup.job-${index + 1}.status`, ["jobs", "status", job.jobId], (e, r) => r.exitCode === 0 && e?.ok === true && typeof e?.data?.status === "string" ? null : `Failed to read cleanup status for job ${job.jobId}.`, { cleanup: { sourceCase: caseId, jobId: job.jobId } }, project);
    if (status.assertion?.status !== "PASS") continue;
    if (["succeeded", "failed", "cancelled", "timeout"].includes(status.envelope.data.status)) continue;
    await m.call(`${caseId}.cleanup.job-${index + 1}.cancel`, ["jobs", "cancel", job.jobId, "--yes"], (e, r) => jobCleanupCancelIssue(e, r, job.jobId), { cleanup: { sourceCase: caseId, jobId: job.jobId } }, project);
  }
}

/** editor-phase 的 play 示例执行后立即回到 Edit Mode，避免污染后续 capability。 */
async function cleanupEditorPlay(m, caseId, name, phase, record) {
  if (name !== "editor.play" || phase !== "editor" || record.envelope?.ok !== true) return;
  const descriptor = await getDescriptor(m, "tool", "editor.stop");
  let input = {};
  try { input = parsePublicExampleArgv(descriptor?.examples?.[0], "tool", "editor.stop").input; } catch { }
  await m.call(`${caseId}.cleanup.stop`, argvFor("tool", "editor.stop", input), (e, r) => r.exitCode === 0 ? assertSuccess(e, descriptor?.outputSchema ?? {}) : "editor.stop cleanup failed.", { cleanup: { sourceCase: caseId, session: "edit-mode" } });
  await m.call(`${caseId}.cleanup.ready`, ["context", "read", "editor.state", "--input", "{\"waitReady\":true}", "--timeout-ms", "10000"], (e, r) => {
    const state = businessPayload(e);
    return r.exitCode === 0 && state?.timedOut === false && state?.state?.busy?.isBusy === false && state?.state?.playMode?.isPlaying === false ? null : "Editor did not return to ready Edit Mode after cleanup.";
  }, { cleanup: { sourceCase: caseId, session: "edit-mode-ready" }, retryAssertion: true });
}

/** 通过公开 Tool 切换 PlayMode，并把过渡和 ready 状态纳入 case 证据。 */
async function setCasePlayMode(m, caseId, playing) {
  const name = playing ? "editor.play" : "editor.stop";
  const descriptor = await getDescriptor(m, "tool", name);
  let example;
  try { example = parsePublicExampleArgv(descriptor?.examples?.[0], "tool", name); }
  catch (error) { await m.failed(`control.play-baseline.${caseId}.${name}`, `Transition example is invalid: ${error.message}`); return false; }
  const transition = await m.call(`control.play-baseline.${caseId}.${name}`, argvFor("tool", name, example.input, example.flags), (e, r) => r.exitCode === 0 ? assertSuccess(e, descriptor.outputSchema) : `${name} baseline transition exited ${r.exitCode}.`, { baseline: { sourceCase: caseId, expectedPlaying: playing } });
  if (transition.assertion?.status !== "PASS") return false;
  const ready = await m.call(`control.play-baseline.${caseId}.${name}.ready`, ["context", "read", "editor.state", "--input", "{\"waitReady\":true}", "--timeout-ms", "10000"], (e, r) => {
    const state = businessPayload(e);
    return r.exitCode === 0 && state?.timedOut === false && state?.state?.busy?.isBusy === false && state?.state?.playMode?.isPlaying === playing ? null : `Editor did not reach ready ${playing ? "Play" : "Edit"} Mode.`;
  }, { baseline: { sourceCase: caseId, expectedPlaying: playing }, retryAssertion: true });
  return ready.assertion?.status === "PASS";
}

/** 返回能力专属夹具；只承载与公共 reset 冲突的前置状态。 */
export function capabilityFixtureId(name) {
  if (name.startsWith("probuilder.")) return "probuilder_reset";
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
  if (name === "profiler.memory-snapshot.compare") return "profiler_snapshots";
  if (name === "package.restore") return "package_restore";
  if (name === "package.scoped-registry.remove") return "package_scoped_registry";
  if (name === "ui.document.create") return "ui_document_absent";
  if (name === "ui.detach-document") return "ui_detach_target";
  if (name === "ui.modify-visual-element") return "ui_visual_element";
  if (name === "scriptable-object.create") return "scriptable_object_create_absent";
  if (name.startsWith("scriptable-object.")) return "scriptable_object";
  if (["vfx.line-create", "vfx.line-positions"].includes(name)) return "vfx_line_target";
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
  if (resolve(project) === resolve(m.o.closed_job_project)) {
    if (!await m.restoreClosedBaseline(caseId, project)) return false;
    return prepareCapabilityFixture(m, caseId, name, project);
  }
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

async function prepareDynamicCaseInput(m, caseId, name, input, positive) {
  if (!positive || !["build.cancel", "build.status"].includes(name) || input?.jobId !== "job_xxx") return input;
  const fixtureInput = JSON.stringify({ target: "StandaloneWindows64", output: "Builds/MatrixFixture.exe", unity: BUILD_CANCEL_UNITY_FIXTURE });
  const fixture = await m.call(`${caseId}.fixture.build-job`, ["tools", "run", "build.run", "--input", fixtureInput, "--yes"], (e, r) => r.exitCode === 0 && e?.ok === true && typeof e?.data?.jobId === "string" && ["queued", "running"].includes(e?.data?.status) ? null : "Failed to create build job fixture.", { fixture: "build-job", sourceCase: caseId }, m.o.closed_job_project);
  if (fixture.assertion?.status !== "PASS") return null;
  return { ...input, jobId: fixture.envelope.data.jobId };
}

async function invoke(m, kind, name, id, d, input, flags, positive, phase, project = m.o.project, expectedErrorCode = null) {
  const caseId = matrixCaseId(kind, name, id, phase);
  if (!await prepareCaseBaseline(m, caseId, name, positive, phase, project)) return m.failed(caseId, "Case baseline setup failed.", { capability: { kind, name }, phase });
  const dynamicInput = await prepareDynamicCaseInput(m, caseId, name, input, positive);
  if (dynamicInput == null) return m.failed(caseId, "Dynamic case fixture failed.", { capability: { kind, name }, phase });
  const routedInput = routeCapabilityInput(dynamicInput, project, m.o.project);
  const record = await m.call(caseId, argvFor(kind, name, routedInput, flags), (e, r) => {
    if (positive) return r.exitCode === 0 ? assertPositiveSuccess(e, d.outputSchema) : `Positive invocation exited ${r.exitCode}.`;
    if (r.exitCode === 0) return "Negative invocation must exit nonzero.";
    const issue = assertError(e, expectedErrorCode === "E_SCHEMA_INVALID");
    if (issue) return issue;
    return expectedErrorCode && e?.error?.code !== expectedErrorCode ? `Expected error ${expectedErrorCode}, received ${e?.error?.code ?? "unknown"}.` : null;
  }, { capability: { kind, name }, phase, ...(expectedErrorCode ? { expectedErrorCode } : {}) }, project);
  await cleanupStartedJobs(m, caseId, record, project);
  await cleanupEditorPlay(m, caseId, name, phase, record);
  if (name === "package.embed" && record.envelope?.ok === true && flags.includes("--yes")) {
    const fixture = m.o.fixture.package_unembed;
    if (!fixture || !await runFixtureSequence(m, `cleanup-package-unembed.${caseId}`, fixture, project, "package_unembed")) {
      await m.failed(`${caseId}.cleanup.package-unembed`, "package.embed cleanup failed.", { cleanup: { sourceCase: caseId } });
    }
  }
  return record;
}
async function cover(m, kind, name, phase = "editor") {
  const d = await getDescriptor(m, kind, name); if (!d) return m.blocked(`${kind}.${name}.descriptor`, "Describe returned no descriptor."); let first;
  try { first = parsePublicExampleArgv(d.examples?.[0], kind, name); } catch (e) { if ((d.inputSchema?.required ?? []).length) return m.blocked(matrixCaseId(kind, name, "default", phase), `Cannot construct default input: ${e.message}`); first = { input: {}, flags: [] }; }
  const unavailable = phase === "play" ? d.playModeSupport === "editor-only" : d.playModeSupport === "runtime-only";
  const project = capabilityProject(m, kind, d);
  if (phase === "play" && resolve(project) !== resolve(m.o.project)) {
    for (const category of ["default", "optional", "unknown", "type"]) await m.notApplicable(matrixCaseId(kind, name, category, phase), "Capability executes outside the primary live Editor session, so Dev PlayMode is not applicable.", { capability: { kind, name }, phase, project });
    return;
  }
  // runtime-only 的 Editor 反向不能沿用会直接请求 runtime 的示例 flag。
  const phaseFlags = unavailable && phase === "editor" ? first.flags.filter((v) => v !== "--runtime") : first.flags;
  const unavailableCode = kind === "tool"
    ? (phase === "play" ? "E_TOOL_PLAYMODE_UNAVAILABLE" : "E_RUNTIME_TOOL_TARGET_MISMATCH")
    : (phase === "play" ? "E_CONTEXT_PLAYMODE_UNAVAILABLE" : "E_CONTEXT_RUNTIME_TARGET_MISMATCH");
  const modeProbeFlags = (flags) => unavailable && kind === "tool" && d.destructiveHint && !flags.includes("--yes") && !flags.includes("--dry-run") ? [...flags, "--yes"] : flags;
  // destructive example 只有携带 --yes 或有效 --dry-run 时才是正向调用。
  const invocationIsPositive = (flags) => !unavailable && !(kind === "tool" && d.destructiveHint && !flags.includes("--dry-run") && !flags.includes("--yes"));
  const defaultFlags = modeProbeFlags(phaseFlags);
  const defaultPositive = invocationIsPositive(defaultFlags);
  await invoke(m, kind, name, "default", d, first.input, defaultFlags, defaultPositive, phase, project, defaultPositive ? null : (unavailable ? unavailableCode : "E_DESTRUCTIVE_CONFIRMATION_REQUIRED"));
  let optional;
  if (d.examples?.[1]) {
    try { optional = parsePublicExampleArgv(d.examples[1], kind, name); }
    catch (e) { return m.blocked(matrixCaseId(kind, name, "optional", phase), `Second public example is invalid: ${e.message}`); }
  } else if (plain(d.inputSchema?.properties) && Object.keys(d.inputSchema.properties).some((key) => !(d.inputSchema.required ?? []).includes(key))) {
    optional = { input: { ...first.input }, flags: [...first.flags] };
  }
  if (!optional) await m.notApplicable(matrixCaseId(kind, name, "optional", phase), "Input schema has no optional field and no second public usage category.", { capability: { kind, name }, phase }); else {
    const rawOptionalFlags = unavailable && phase === "editor" ? optional.flags.filter((v) => v !== "--runtime") : optional.flags;
    const optionalFlags = modeProbeFlags(rawOptionalFlags);
    const optionalPositive = invocationIsPositive(optionalFlags);
    await invoke(m, kind, name, "optional", d, optional.input, optionalFlags, optionalPositive, phase, project, optionalPositive ? null : (unavailable ? unavailableCode : "E_DESTRUCTIVE_CONFIRMATION_REQUIRED"));
  }
  const schemaProbeFlags = kind === "tool" && d.destructiveHint ? (d.supportsDryRun ? ["--dry-run"] : ["--yes"]) : [];
  await invoke(m, kind, name, "unknown", d, { ...first.input, __matrix_unknown: true }, schemaProbeFlags, false, phase, project, "E_SCHEMA_INVALID");
  const typeProbe = Object.entries(d.inputSchema?.properties ?? {})
    .map(([key, schema]) => ({ key, value: invalidSchemaValue(schema) }))
    .find((entry) => entry.value !== undefined);
  if (!typeProbe) await m.notApplicable(matrixCaseId(kind, name, "type", phase), "Input schema has no field with a rejectable JSON type.", { capability: { kind, name }, phase });
  else await invoke(m, kind, name, "type", d, { ...first.input, [typeProbe.key]: typeProbe.value }, schemaProbeFlags, false, phase, project, "E_SCHEMA_INVALID");
  if (kind === "tool" && phase === "editor" && d.destructiveHint) await security(m, name, d, first.input, project);
}
async function security(m, name, d, input, project) {
  await invoke(m, "tool", name, "confirmation", d, input, [], false, "editor", project, "E_DESTRUCTIVE_CONFIRMATION_REQUIRED");
  if (d.supportsDryRun) await invoke(m, "tool", name, "dry-run", d, input, ["--dry-run"], true, "editor", project);
  const allowed = m.o.allow_destructive_projects ?? (m.o.allow_destructive_project ? [m.o.allow_destructive_project] : []);
  if (!allowed.some((candidate) => resolve(candidate) === resolve(project))) return m.blocked(`tool.${name}.yes`, "Actual destructive invocation requires --allow-destructive-project equal to the invocation project.");
  await invoke(m, "tool", name, "yes", d, input, ["--yes"], true, "editor", project);
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
      if (!fixture) await m.blocked(`control.${pipeline}.${phase}`, `No ${pipeline} activation fixture supplied.`);
      else await runFixtureSequence(m, `${pipeline}.${phase}`, fixture, m.o.project, pipeline);
      prepared.add(pipeline);
    }
    await cover(m, "tool", name, phase);
  }
}
async function playMode(m) {
  m.invalidateFailedPlayModeControls();
  const play = m.discovered.tools.find((n) => n === "editor.play"); const stop = m.discovered.tools.find((n) => n === "editor.stop"); if (!play || !stop) return m.blocked("playmode.transition", "Public editor.play/editor.stop capabilities are not discoverable.");
  const pd = await getDescriptor(m, "tool", play), sd = await getDescriptor(m, "tool", stop); let p, s; try { p = parsePublicExampleArgv(pd.examples?.[0], "tool", play); s = parsePublicExampleArgv(sd.examples?.[0], "tool", stop); } catch (e) { return m.blocked("playmode.transition", `Transition example invalid: ${e.message}`); }
  await invoke(m, "tool", play, "transition", pd, p.input, p.flags, true, "transition");
  await m.call("playmode.ready", ["context", "read", "editor.state", "--input", "{\"waitReady\":true}", "--timeout-ms", "10000"], (e) => { const state = businessPayload(e); return e?.ok && state?.timedOut === false && state?.state?.busy?.isBusy === false && state?.state?.playMode?.isPlaying === true ? null : "Editor state did not report ready Play Mode after editor.play."; }, { retryAssertion: true });
  await coverToolsWithPipelineFixtures(m, "play", new Set([play, stop]));
  for (const name of m.discovered.contexts) if (capabilitySelected(m, name)) await cover(m, "context", name, "play");
  await invoke(m, "tool", stop, "transition", sd, s.input, s.flags, true, "transition");
  await m.call("playmode.exit-ready", ["context", "read", "editor.state", "--input", "{\"waitReady\":true}", "--timeout-ms", "10000"], (e) => { const state = businessPayload(e); return e?.ok && state?.timedOut === false && state?.state?.busy?.isBusy === false && state?.state?.playMode?.isPlaying === false ? null : "Editor state did not report ready Edit Mode after editor.stop."; }, { retryAssertion: true });
}
const FIXTURE_ASSERTIONS = {
  setup: "ready",
  reset: "ready",
  pipeline_urp: "pipelineReady",
  pipeline_hdrp: "pipelineReady",
  concurrency: "parallelOverlap",
  timeout_cancel: "timeoutOrCancel",
  stale_target: "staleTarget",
  domain_reload: "recoveredAfterReload",
  shell_quoting: "quotedPath",
  prefab_stage: "prefabStage",
  ui_cache: "cacheInvalidated",
  closed_job: "jobLifecycle",
  probuilder_reset: "meshReady",
  cinemachine_pipeline: "cinemachinePipelineReady",
  cinemachine_extension_absent: "cinemachineExtensionAbsent",
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
  profiler_snapshots: "profilerSnapshotsReady",
  profiler_play_paused: "playPaused",
  package_restore: "packageDeploymentReady",
  package_scoped_registry: "scopedRegistryReady",
  package_unembed: "packageUnembedded",
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
function fixtureExpectationIssue(e, r, expect = {}) {
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
function fixtureError(f, id) {
  if (!plain(f) || !plain(f.assert)) return "Fixture must declare an assert object.";
  const semantic = FIXTURE_ASSERTIONS[id]; if (!semantic || f.assert[semantic] !== true) return `Fixture assert.${semantic ?? "semantic"} must be true.`;
  if (id === "concurrency") return Array.isArray(f.calls) && f.calls.length === 2 ? null : "Concurrency fixture requires exactly two calls.";
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
    }, { fixture: fixtureId, step: step.id ?? index + 1, attempt }, project);
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
async function runFixtureSequence(m, id, fixture, project = m.o.project, assertionId = id) {
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
export function concurrencyOverlapMs(a, b, elapsedMs) {
  const interval = (record) => {
    const audit = record.envelope?.data?.audit;
    const startedAt = Date.parse(audit?.startedAt ?? "");
    const endedAt = Date.parse(audit?.endedAt ?? "");
    return Number.isFinite(startedAt) && Number.isFinite(endedAt) && endedAt >= startedAt ? { startedAt, endedAt } : null;
  };
  const left = interval(a), right = interval(b);
  if (left && right) return Math.max(0, Math.min(left.endedAt, right.endedAt) - Math.max(left.startedAt, right.startedAt));
  const duration = (record) => Number(record.envelope?.data?.result?.durationMs ?? record.envelope?.data?.durationMs ?? record.envelope?.durationMs ?? 0);
  return Math.max(0, duration(a) + duration(b) - elapsedMs);
}
async function runConcurrencyFixture(m, fixture) {
  const bad = fixtureError(fixture, "concurrency"); if (bad) return m.failed("control.concurrency", bad);
  const started = Date.now();
  const [a, b] = await Promise.all(fixture.calls.map((call, index) => m.call(`control.concurrency.${index + 1}`, call.argv, (e, r) => fixtureExpectationIssue(e, r, call.expect), { fixture: "concurrency", call: index + 1 })));
  const elapsed = Date.now() - started;
  const overlapMs = concurrencyOverlapMs(a, b, elapsed);
  const minimumOverlap = Number(fixture.assert.minOverlapMs ?? 1);
  await m.call("control.concurrency.overlap", ["context", "read", "editor.state", "--input", "{}"], () => overlapMs >= minimumOverlap ? null : "Fixture calls did not prove overlapping waits.", { fixture: "concurrency", elapsedMs: elapsed, overlapMs, minimumOverlapMs: minimumOverlap });
}
async function controls(m) {
  const concurrency = m.o.fixture.concurrency;
  if (!concurrency) await m.blocked("control.concurrency", "No concurrency fixture supplied."); else await runConcurrencyFixture(m, concurrency);
  for (const id of FIXTURES.filter((name) => name !== "concurrency")) {
    const fixture = m.o.fixture[id]; if (!fixture) await m.blocked(`control.${id}`, "No fixture supplied for this perturbation."); else await runFixtureSequence(m, id, fixture);
  }
  const closed = m.o.fixture.closed_job;
  if (!closed) await m.blocked("control.closed_job", "No closed-job fixture supplied.");
  else if (await m.restoreClosedBaseline("closed-job-fixture", m.o.closed_job_project)) await runFixtureSequence(m, "closed_job", closed, m.o.closed_job_project);
}
/** full resume 汇总同一执行身份的全部 terminal checkpoint，避免 expected set 受切片边界影响。 */
function mergeFullResumeCheckpoints(m) {
  if (m.o.capabilities.length !== 0) return;
  const projects = new Set([resolve(m.o.project), resolve(m.o.closed_job_project)]);
  const records = new Map(m.records.map((record) => [record.id, record]));
  for (const record of m.prior.values()) {
    if (record.buildStamp !== (m.o.build_stamp ?? null) || record.project === undefined || !projects.has(resolve(record.project))) continue;
    const targetMatches = record.target === (m.o.target ?? null) || (record.target === null && record.id.startsWith("control.closed-baseline."));
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

export async function runMatrix(options) {
  const m = new Matrix({ ...options, capabilities: options.capabilities ?? [] });
  await m.init();
  m.invalidateSessionControls();
  const initialRestore = m.o.fixture.playmode_restore;
  if (initialRestore && !await runFixtureSequence(m, "playmode-initial-restore", initialRestore, m.o.project, "playmode_restore")) {
    await m.blocked("control.initial-edit-mode", "PlayMode options could not be restored before editor-phase setup.");
    return { summary: await report(m), records: m.records };
  }
  if (!await setCasePlayMode(m, "initial-editor", false)) {
    await m.blocked("control.initial-edit-mode", "Editor did not reach EditMode before editor-phase setup.");
    return { summary: await report(m), records: m.records };
  }
  if (!m.o.fixture.setup) await m.blocked("control.setup", "No reproducible Dev setup fixture supplied.");
  else await runFixtureSequence(m, "setup", m.o.fixture.setup);
  await discover(m);
  await coverToolsWithPipelineFixtures(m, "editor");
  for (const name of m.discovered.contexts) if (capabilitySelected(m, name)) await cover(m, "context", name);
  if (m.o.capabilities.length === 0 || m.o.include_playmode) {
    if (!m.o.fixture.reset) await m.blocked("control.reset", "No reproducible pre-PlayMode reset fixture supplied.");
    else await runFixtureSequence(m, "reset", m.o.fixture.reset);
    const fastPlayMode = m.o.fixture.playmode_fast;
    m.invalidateSessionControls();
    const fastPlayModeReady = fastPlayMode
      ? await runFixtureSequence(m, "playmode-fast", fastPlayMode, m.o.project, "playmode_fast")
      : true;
    if (fastPlayModeReady) await playMode(m);
    else await m.blocked("control.playmode-fast", "Bulk PlayMode options fixture failed; PlayMode cases were not executed.");
    if (!await setCasePlayMode(m, "post-playmode", false)) {
      await m.failed("control.post-playmode-edit", "Editor did not return to EditMode after bulk PlayMode coverage.");
    }
    const restorePlayMode = m.o.fixture.playmode_restore;
    if (fastPlayMode && (!restorePlayMode || !await runFixtureSequence(m, "playmode-restore", restorePlayMode, m.o.project, "playmode_restore"))) {
      await m.failed("control.playmode-restore", "PlayMode options were not restored after bulk PlayMode coverage.");
    }
  }
  if (m.o.capabilities.length === 0) await controls(m);
  if (m.closedBaselineReady) await m.restoreClosedBaseline("final-restore", m.o.closed_job_project);
  return { summary: await report(m), records: m.records };
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  let releaseLock;
  try {
    const options = parseOptions(process.argv.slice(2));
    releaseLock = await acquireRunnerLock(options.evidence_dir);
    const result = await runMatrix(options);
    process.exitCode = result.summary.complete ? 0 : 1;
  } catch (error) {
    console.error(error instanceof Error ? error.stack : String(error));
    process.exitCode = 2;
  } finally {
    if (releaseLock) await releaseLock();
  }
}
