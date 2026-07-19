import assert from "node:assert/strict";
import { appendFile, chmod, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { acquireRunnerLock, assertPositiveSuccess, BUILD_CANCEL_UNITY_FIXTURE, capabilityFixtureId, capabilityProject, concurrencyOverlapMs, ensureClosedProjectBaseline, jobCleanupCancelIssue, parsePublicExampleArgv, restoreClosedProjectBaseline, routeCapabilityInput, runCli, runMatrix, snapshotClosedProjectBaseline } from "./run-final-live-matrix.mjs";

const schemaError = { code: "E_SCHEMA_INVALID", message: "invalid input", transient: false, retryAfterMs: 0, nextActions: ["Use describe."], details: { path: "$.input", expected: "string", actual: "object" } };
const securityError = { code: "E_DESTRUCTIVE_CONFIRMATION_REQUIRED", message: "confirmation required", transient: false, retryAfterMs: 0, nextActions: ["Pass --yes."] };
const playModeError = { code: "E_TOOL_PLAYMODE_UNAVAILABLE", message: "editor-only tool", transient: false, retryAfterMs: 0, nextActions: ["Exit Play Mode."] };
const runtimeTargetError = { code: "E_RUNTIME_TOOL_TARGET_MISMATCH", message: "runtime target required", transient: false, retryAfterMs: 0, nextActions: ["Use --runtime."] };
async function makeMock(dir) {
  const file = join(dir, "mock cli.mjs");
  await writeFile(file, `#!/usr/bin/env node
import { appendFileSync, existsSync, readFileSync, writeFileSync } from "node:fs";
const a=process.argv.slice(2), at=a.indexOf("--input"), input=at<0?{}:JSON.parse(a[at+1]); appendFileSync(process.env.MATRIX_LOG,JSON.stringify(a)+"\\n");
const stateFile=process.env.MATRIX_STATE; let stateValue=existsSync(stateFile)?readFileSync(stateFile,"utf8"):"false", playing=stateValue==="true";
const transientName=process.env.MATRIX_TRANSIENT_DESCRIBE, transientFile=process.env.MATRIX_TRANSIENT_FILE;
const ok=(data)=>console.log(JSON.stringify({ok:true,requestId:"mock",data})); const fail=(error)=>{console.log(JSON.stringify({ok:false,requestId:"mock",error}));process.exitCode=1};
const ex=(name,kind)=>({argv:[kind==="tool"?"tools":"context",kind==="tool"?"run":"read",name,"--input","{\\"path\\":\\"Assets/Unicode 空格\\"}","--wait","--timeout-ms","20",...(name==="runtime.only"?["--runtime"]:[])]});
const d=(name,kind)=>({name,inputSchema:{type:"object",properties:{path:{type:"string"},optional:{type:"boolean"}},required:["path"]},outputSchema:{type:"object",required:["value","nested"],properties:{value:{type:"string"},nested:{type:"object",properties:{items:{type:"array",items:{type:"object",required:["name"],properties:{name:{type:"string"}}}}}}}},examples:[ex(name,kind),ex(name,kind)],destructiveHint:name==="asset.write",supportsDryRun:name==="asset.write",playModeSupport:name==="runtime.only"?"runtime-only":name==="asset.write"?"editor-only":"supported"});
if(a[0]==="tools"&&a[1]==="group") a[2]==="list"?ok({tools:a[3]==="asset"?[{name:"asset.write"}]:[{name:"runtime.only"},{name:"editor.play"},{name:"editor.stop"}]}):ok({groups:[{name:"asset"},{name:"editor"}]});
else if(a[0]==="context"&&a[1]==="list")ok({contexts:[{name:"editor.state"}]}); else if(a[0]==="extensions")ok(a[1]==="doctor"?{extensions:[]}:{extensions:[]});
else if(a[1]==="describe"&&a[2]===transientName&&transientFile&&!existsSync(transientFile)){writeFileSync(transientFile,"failed-once");fail({code:"E_UNITY_COMPILING",message:"compiling",transient:true,retryAfterMs:1,nextActions:["Retry."]});}
else if(a[1]==="describe")ok(d(a[2],a[0]==="tools"?"tool":"context")); else if(a[0]==="context"&&a[1]==="read"&&a[2]==="editor.state"){ if(input.__matrix_unknown||typeof input.path==="object")fail(${JSON.stringify(schemaError)}); else { if(stateValue==="pending-play"){writeFileSync(stateFile,"true");playing=false;} ok({ready:true,value:"state",durationMs:500,timedOut:false,state:{busy:{isBusy:false},playMode:{isPlaying:playing}},nested:{items:[{name:"x"}]}}); } }
else if(a[0]==="jobs"){if(a[1]==="status"&&process.env.MATRIX_FAIL_JOB_STATUS==="1")fail(${JSON.stringify(schemaError)});else ok({value:"job",jobId:"job-1",status:a[1]==="cancel"?"cancelled":process.env.MATRIX_TERMINAL_JOB==="1"?"succeeded":"running",nested:{items:[{name:"x"}]}});} else if(a[0]==="tools"&&a[1]==="run"&&a[2]==="editor.play"){if(input.__matrix_unknown||typeof input.path!=="string")fail(${JSON.stringify(schemaError)});else{writeFileSync(stateFile,process.env.MATRIX_DELAY_PLAY_STATE==="1"?"pending-play":"true");ok({value:"ok",nested:{items:[{name:"x"}]}});}} else if(a[0]==="tools"&&a[1]==="run"&&a[2]==="editor.stop"){if(input.__matrix_unknown||typeof input.path!=="string")fail(${JSON.stringify(schemaError)});else{writeFileSync(stateFile,"false");ok({value:"ok",nested:{items:[{name:"x"}]}});}} else if(a[1]==="run"||a[1]==="read"){  if(input.__matrix_unknown||typeof input.path!=="string")fail(${JSON.stringify(schemaError)}); else if(a[2]==="asset.write"&&!a.includes("--yes")&&!a.includes("--dry-run"))fail(${JSON.stringify(securityError)}); else if(a[2]==="asset.write"&&playing)fail(${JSON.stringify(playModeError)}); else if(a[2]==="runtime.only"&&!a.includes("--runtime"))fail(${JSON.stringify(runtimeTargetError)}); else if(a[2]==="runtime.only"&&!playing)fail({code:"E_RUNTIME_NOT_READY",message:"runtime not ready",transient:false,retryAfterMs:0,nextActions:["Enter Play Mode."]}); else ok({value:"ok",...(a[2]==="asset.write"&&process.env.MATRIX_TERMINAL_JOB==="1"?{jobId:"job-capability",status:"queued"}:{}),nested:{items:[{name:"x"}]}}); } else fail(${JSON.stringify(schemaError)});`, "utf8");
  await chmod(file, 0o755);
  process.env.MATRIX_STATE = join(dir, "play-state.txt");
  await writeFile(process.env.MATRIX_STATE, "false", "utf8");
  return file;
}
function fixtures() {
  const stateStep = (id) => ({ id, argv: ["context", "read", "editor.state", "--input", "{}"], expect: { outputSchema: { type: "object", required: ["value"] }, equals: { "data.value": "state" } } });
  const sequence = (semantic) => ({ assert: { [semantic]: true }, steps: [stateStep("verify")] });
  const jobExpect = { equals: { "data.value": "job" } };
  return {
    setup: sequence("ready"),
    reset: sequence("ready"),
    playmode_fast: sequence("fastPlayMode"),
    playmode_restore: sequence("playModeRestored"),
    concurrency: { assert: { parallelOverlap: true, minOverlapMs: 0 }, calls: [stateStep("a"), stateStep("b")] },
    timeout_cancel: sequence("timeoutOrCancel"),
    stale_target: sequence("staleTarget"),
    domain_reload: sequence("recoveredAfterReload"),
    shell_quoting: sequence("quotedPath"),
    prefab_stage: sequence("prefabStage"),
    ui_cache: sequence("cacheInvalidated"),
    closed_job: {
      assert: { jobLifecycle: true },
      steps: [
        { id: "start", argv: ["jobs", "start"], expect: { ...jobExpect, exists: ["data.jobId"] }, capture: { jobId: "data.jobId" } },
        { id: "status", argv: ["jobs", "status", "${jobId}"], expect: jobExpect },
        { id: "wait", argv: ["jobs", "wait", "${jobId}"], expect: jobExpect },
        { id: "cancel", always: true, argv: ["jobs", "cancel", "${jobId}"], expect: jobExpect }
      ]
    }
  };
}

async function prepareClosedProject(project) {
  for (const name of ["Assets", "Packages", "ProjectSettings"]) await mkdir(join(project, name), { recursive: true });
}

test("matrix covers authorization, second example, editor state, concurrency, closed-project argv, and Unicode", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix Unicode ")); const cli = await makeMock(dir); const log = join(dir, "calls.log"); process.env.MATRIX_LOG = log;
  const project = join(dir, "Project 空格"), closed = join(dir, "Closed Project");
  await prepareClosedProject(closed);
  const result = await runMatrix({ cli: process.execPath, cli_args: [cli], project, allow_destructive_project: project, target: "target 空格", evidence_dir: join(dir, "evidence 空格"), closed_job_project: closed, fixture: fixtures(), timeoutMs: 5000 });
  assert.equal(result.summary.complete, true, JSON.stringify(result.records.filter((record) => record.assertion?.status !== "PASS"))); const calls = (await readFile(log, "utf8")).trim().split("\n").map(JSON.parse);
  assert.equal(calls.filter((a) => a[0] === "tools" && a[1] === "describe" && a[2] === "asset.write").length, 1);
  assert.ok(calls.some((a) => a[0] === "jobs" && a.includes(closed))); assert.ok(calls.some((a) => a.includes("target 空格")));
  const records = result.records; assert.equal(records.find((r) => r.id === "tool.asset.write.yes").assertion.status, "PASS"); assert.ok(records.some((r) => r.id === "tool.runtime.only.play.default")); assert.ok(records.some((r) => r.preState?.envelope?.data?.ready === true));
  for (const id of [
    "control.play-baseline.tool.runtime.only.play.default.editor.stop",
    "control.play-baseline.tool.runtime.only.play.default.editor.stop.ready",
    "control.case-reset.tool.runtime.only.play.default.verify",
    "control.play-baseline.tool.runtime.only.play.default.editor.play",
    "control.play-baseline.tool.runtime.only.play.default.editor.play.ready",
    "control.play-baseline.tool.asset.write.play.default.editor.stop",
    "control.play-baseline.tool.asset.write.play.default.editor.stop.ready",
    "control.case-reset.tool.asset.write.play.default.verify",
    "control.play-baseline.tool.asset.write.play.default.editor.play",
    "control.play-baseline.tool.asset.write.play.default.editor.play.ready"
  ]) assert.equal(records.find((record) => record.id === id)?.assertion?.status, "PASS", `${id} must prove an isolated PlayMode baseline`);
  assert.equal(records.find((record) => record.id === "control.playmode-initial-restore.verify")?.assertion?.status, "PASS");
  assert.equal(records.find((record) => record.id === "control.play-baseline.initial-editor.editor.stop.ready")?.assertion?.status, "PASS");
  assert.ok(records.findIndex((record) => record.id === "control.play-baseline.initial-editor.editor.stop.ready") < records.findIndex((record) => record.id === "control.setup.verify"));
  assert.equal(records.find((record) => record.id === "control.playmode-fast.verify")?.assertion?.status, "PASS");
  assert.equal(records.find((record) => record.id === "control.playmode-restore.verify")?.assertion?.status, "PASS");
  assert.equal(records.find((record) => record.id === "control.play-baseline.post-playmode.editor.stop.ready")?.assertion?.status, "PASS");
  assert.ok(records.findIndex((record) => record.id === "control.playmode-fast.verify") < records.findIndex((record) => record.id === "tool.runtime.only.play.default"));
  assert.ok(records.findIndex((record) => record.id === "control.playmode-restore.verify") > records.findIndex((record) => record.id === "playmode.exit-ready"));
  assert.equal(records.find((record) => record.id === "control.closed-baseline.final-restore")?.assertion?.status, "PASS");
  assert.equal(records.find((record) => record.id === "tool.asset.write.play.default")?.envelope?.error?.code, "E_TOOL_PLAYMODE_UNAVAILABLE");
  assert.equal(records.find((record) => record.id === "tool.runtime.only.default")?.envelope?.error?.code, "E_RUNTIME_TOOL_TARGET_MISMATCH");
  assert.equal(records.find((record) => record.id === "tool.asset.write.unknown")?.envelope?.error?.code, "E_SCHEMA_INVALID");
  assert.equal(records.find((record) => record.id === "tool.asset.write.confirmation")?.envelope?.error?.code, "E_DESTRUCTIVE_CONFIRMATION_REQUIRED");
});
test("targeted replay covers only selected capabilities and skips global controls", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix targeted "));
  const cli = await makeMock(dir);
  process.env.MATRIX_LOG = join(dir, "calls.log");
  const project = join(dir, "Project");
  const result = await runMatrix({ cli: process.execPath, cli_args: [cli], project, allow_destructive_project: project, target: "target", evidence_dir: join(dir, "evidence"), closed_job_project: join(dir, "Closed"), fixture: fixtures(), capabilities: ["asset.write"], timeoutMs: 5000 });
  assert.equal(result.summary.complete, true, JSON.stringify(result.records.filter((record) => record.assertion?.status !== "PASS")));
  assert.ok(result.records.some((record) => record.id === "tool.asset.write.yes"));
  assert.equal(result.records.some((record) => record.id.startsWith("tool.runtime.only.")), false);
  assert.equal(result.records.some((record) => record.id === "control.concurrency"), false);
});

test("targeted replay can opt into PlayMode without running perturbation controls", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix targeted play "));
  const cli = await makeMock(dir);
  process.env.MATRIX_LOG = join(dir, "calls.log");
  const project = join(dir, "Project");
  const result = await runMatrix({ cli: process.execPath, cli_args: [cli], project, allow_destructive_project: project, target: "target", evidence_dir: join(dir, "evidence"), closed_job_project: join(dir, "Closed"), fixture: fixtures(), capabilities: ["asset.write"], include_playmode: true, timeoutMs: 5000 });
  assert.equal(result.summary.complete, true, JSON.stringify(result.records.filter((record) => record.assertion?.status !== "PASS")));
  assert.ok(result.records.some((record) => record.id === "tool.asset.write.play.default"));
  assert.ok(result.records.some((record) => record.id === "playmode.ready"));
  assert.equal(result.records.some((record) => record.id.startsWith("context.")), false);
  assert.equal(result.records.some((record) => record.id === "control.concurrency"), false);
});

test("transient describe failures retry without duplicate terminal case ids", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix transient "));
  const cli = await makeMock(dir);
  process.env.MATRIX_LOG = join(dir, "calls.log");
  process.env.MATRIX_TRANSIENT_DESCRIBE = "editor.play";
  process.env.MATRIX_TRANSIENT_FILE = join(dir, "transient-once.txt");
  try {
    const project = join(dir, "Project");
    const closed = join(dir, "Closed");
    const evidence = join(dir, "evidence");
    await prepareClosedProject(closed);
    const result = await runMatrix({ cli: process.execPath, cli_args: [cli], project, allow_destructive_project: project, target: "target", evidence_dir: evidence, closed_job_project: closed, fixture: fixtures(), timeoutMs: 5000 });
    assert.equal(result.summary.complete, true, JSON.stringify(result.records.filter((record) => record.assertion?.status !== "PASS")));
    const describes = result.records.filter((record) => record.id === "tool.editor.play.describe");
    assert.equal(describes.length, 1);
    assert.equal(describes[0].attempts?.length, 2);
    const checkpoints = (await readFile(join(evidence, "calls.jsonl"), "utf8")).trim().split(/\r?\n/).map(JSON.parse).filter((record) => record.id === "tool.editor.play.describe" && record.retryCheckpoint);
    assert.equal(checkpoints.length, 1);
    assert.equal(checkpoints[0].retryCheckpoint.attempt, 1);
  } finally {
    delete process.env.MATRIX_TRANSIENT_DESCRIBE;
    delete process.env.MATRIX_TRANSIENT_FILE;
  }
});

test("transient retry budget covers the full reload window", async () => {
  const source = await readFile(new URL("./run-final-live-matrix.mjs", import.meta.url), "utf8");
  assert.match(source, /const TRANSIENT_MAX_ATTEMPTS = 120/);
  assert.match(source, /requestedDelay[\s\S]*requestedDelay > 0 \? requestedDelay : 1000/);
  assert.match(source, /envelope\?\.error\?\.transient !== true/);
  assert.match(source, /attempt === TRANSIENT_MAX_ATTEMPTS/);
});

test("PlayMode baseline polls through the pre-transition ready window", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix play transition "));
  const cli = await makeMock(dir);
  process.env.MATRIX_LOG = join(dir, "calls.log");
  process.env.MATRIX_DELAY_PLAY_STATE = "1";
  try {
    const project = join(dir, "P");
    const closed = join(dir, "C");
    await prepareClosedProject(closed);
    const result = await runMatrix({ cli: process.execPath, cli_args: [cli], project, allow_destructive_project: project, evidence_dir: join(dir, "e"), closed_job_project: closed, fixture: fixtures(), timeoutMs: 5000 });
    assert.equal(result.summary.complete, true, JSON.stringify(result.records.filter((record) => record.assertion?.status !== "PASS")));
    const ready = result.records.find((record) => record.id === "control.play-baseline.tool.runtime.only.play.default.editor.play.ready");
    assert.ok(ready.attempts?.length >= 2, "ready state must be polled until PlayMode actually converges");
    assert.equal(ready.argv[ready.argv.indexOf("--timeout-ms") + 1], "10000");
  } finally {
    delete process.env.MATRIX_DELAY_PLAY_STATE;
  }
});

test("destructive actual call is blocked without project authorization and missing fixture is nonzero", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix noauth ")); const cli = await makeMock(dir); const result = await runMatrix({ cli: process.execPath, cli_args: [cli], project: join(dir, "P"), evidence_dir: join(dir, "e"), closed_job_project: join(dir, "C"), fixture: {}, timeoutMs: 5000 });
  assert.equal(result.summary.complete, false); assert.equal(result.records.find((r) => r.id === "tool.asset.write.yes").assertion.status, "BLOCKED"); assert.equal(result.records.find((r) => r.id === "control.concurrency").assertion.status, "BLOCKED");
});

test("closed-job cleanup runs after an earlier fixture failure", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix cleanup "));
  const cli = await makeMock(dir);
  const log = join(dir, "calls.log");
  process.env.MATRIX_LOG = log;
  process.env.MATRIX_FAIL_JOB_STATUS = "1";
  try {
    const project = join(dir, "Project");
    const closed = join(dir, "Closed");
    for (const name of ["Assets", "Packages", "ProjectSettings"]) await mkdir(join(closed, name), { recursive: true });
    const result = await runMatrix({ cli: process.execPath, cli_args: [cli], project, allow_destructive_project: project, target: "target", evidence_dir: join(dir, "evidence"), closed_job_project: closed, fixture: fixtures(), timeoutMs: 5000 });
    assert.equal(result.summary.complete, false);
    const calls = (await readFile(log, "utf8")).trim().split("\n").map(JSON.parse);
    assert.ok(calls.some((argv) => argv[0] === "jobs" && argv[1] === "cancel" && argv[2] === "job-1"), "cleanup cancel must run after status failure");
  } finally {
    delete process.env.MATRIX_FAIL_JOB_STATUS;
  }
});
test("matrix runner lock rejects an overlapping process", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix runner lock "));
  const release = await acquireRunnerLock(dir);
  try {
    await assert.rejects(acquireRunnerLock(dir), /E_MATRIX_RUNNER_ACTIVE/);
  } finally {
    await release();
  }
  const releaseAgain = await acquireRunnerLock(dir);
  await releaseAgain();
});

test("runCli timeout terminates the child process tree", async () => {
  const startedAt = Date.now();
  const result = await runCli(process.execPath, ["-e", "setInterval(() => {}, 1000)"], 100);
  assert.equal(result.timedOut, true);
  assert.ok(Date.now() - startedAt < 5000, "timed out child must terminate promptly");
});

test("build cancel uses a delayed Unity fixture", async () => {
  assert.match(BUILD_CANCEL_UNITY_FIXTURE, /delayed-unity\.cmd$/);
  assert.match(await readFile(BUILD_CANCEL_UNITY_FIXTURE, "utf8"), /ping -n 31/);
});

test("pipeline volume fixture persists overrides across Scene Reload", async () => {
  const source = await readFile(new URL("../.plan/evidence/final-cleanup/live-pipeline-setup.cs", import.meta.url), "utf8");
  assert.match(source, /AssetDatabase\.AddObjectToAsset\(effect, profile\)/);
  assert.match(source, /AssetDatabase\.LoadAllAssetsAtPath\(profilePath\)/);
});

test("concurrency overlap uses audited execution intervals", () => {
  const record = (startedAt, endedAt) => ({ envelope: { data: { audit: { startedAt, endedAt } } } });
  const a = record("2026-07-14T12:33:09.057Z", "2026-07-14T12:33:10.267Z");
  const b = record("2026-07-14T12:33:09.169Z", "2026-07-14T12:33:10.370Z");
  assert.equal(concurrencyOverlapMs(a, b, 2096), 1098);
});

test("job cleanup accepts only an exact terminal race", () => {
  assert.equal(jobCleanupCancelIssue({ ok: false, error: { code: "JOB_CANCEL_FAILED", message: "Job is already terminal: failed" } }, { exitCode: 1 }, "job-1"), null);
  assert.match(jobCleanupCancelIssue({ ok: false, error: { code: "JOB_CANCEL_FAILED", message: "cancel failed" } }, { exitCode: 1 }, "job-1"), /Failed to cancel leaked job/);
  assert.match(jobCleanupCancelIssue({ ok: false, error: { code: "OTHER", message: "Job is already terminal: failed" } }, { exitCode: 1 }, "job-1"), /Failed to cancel leaked job/);
});

test("positive result rejects failed terminal job states", () => {
  assert.equal(assertPositiveSuccess({ ok: true, data: { status: "succeeded" } }), null);
  assert.match(assertPositiveSuccess({ ok: true, data: { status: "failed" } }), /terminal status failed/);
  assert.match(assertPositiveSuccess({ ok: true, data: { jobs: [{ jobId: "job-child", status: "timeout" }] } }), /job-child/);
});

test("example parser retains execution flags, allows business args, and rejects legacy wire fields", () => {
  assert.deepEqual(parsePublicExampleArgv({ argv: ["tools", "run", "x.y", "--input", "{\"path\":\"A B\"}", "--wait"] }, "tool", "x.y"), { input: { path: "A B" }, flags: ["--wait"] });
  assert.deepEqual(parsePublicExampleArgv({ argv: ["tools", "run", "reflection.method.call", "--input", "{\"args\":[1],\"methodName\":\"InitState\",\"typeName\":\"UnityEngine.Random\"}"] }, "tool", "reflection.method.call"), { input: { args: [1], methodName: "InitState", typeName: "UnityEngine.Random" }, flags: [] });
  assert.deepEqual(parsePublicExampleArgv({ argv: ["tools", "run", "code.exec", "--file", "script.cs", "--yes"] }, "tool", "code.exec"), { input: { file: "script.cs" }, flags: ["--yes"] });
  assert.throws(() => parsePublicExampleArgv({ argv: ["tools", "run", "x.y", "--args", "{}"] }, "tool", "x.y"), /forbidden/);
  assert.throws(() => parsePublicExampleArgv({ argv: ["tools", "run", "x.y", "--input", "{\"dryRun\":true}"] }, "tool", "x.y"), /dryRun/);
});
test("capability fixture mapping isolates stateful positive cases", async () => {
  assert.equal(capabilityFixtureId("cinemachine.extension.add"), "cinemachine_extension_absent");
  assert.equal(capabilityFixtureId("scriptable-object.create"), "scriptable_object_create_absent");
  assert.equal(capabilityFixtureId("scriptable-object.modify"), "scriptable_object");
  assert.equal(capabilityFixtureId("ui.detach-document"), "ui_detach_target");
  assert.equal(capabilityFixtureId("ui.modify-visual-element"), "ui_visual_element");
  assert.equal(capabilityFixtureId("profiler.hierarchy"), "profiler_hierarchy_capture");
  assert.equal(capabilityFixtureId("vfx.template.create"), "vfx_template");
  assert.equal(capabilityFixtureId("scene.unload"), "scene_level2_open");
  assert.equal(capabilityFixtureId("scene.move-object"), "scene_gameplay_open");
  assert.equal(capabilityFixtureId("scene.set-active"), "scene_main_open");
  assert.equal(capabilityFixtureId("scene.create"), "scene_create_absent");
});

test("profiler hierarchy fixture records frames before validating the latest frame", async () => {
  const fixture = JSON.parse(await readFile(new URL("../.plan/evidence/final-cleanup/live-fixtures-final-3.json", import.meta.url), "utf8"));
  const steps = fixture.profiler_hierarchy_capture.steps;
  assert.deepEqual(steps.map((step) => step.id), ["start-recording", "capture-frames", "verify-latest-main-thread"]);
  assert.match(steps[1].argv.join(" "), /\"frames\":30/);
  assert.match(steps[2].argv.join(" "), /\"frame\":-1/);
  assert.equal(fixture.vfx_template.steps[0].expect.contains["data.result.templates"], "SimpleParticleSystem");
  assert.match(fixture.scriptable_object_create_absent.steps[0].argv.join(" "), /scriptable-object-create-absent/);
  assert.match(fixture.cinemachine_extension_absent.steps[0].argv.join(" "), /cinemachine-extension-absent/);
});

test("closed project baseline restores persistent state without touching UnityLockfile", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix closed baseline "));
  const project = join(dir, "Project");
  const baseline = join(dir, "Baseline");
  for (const name of ["Assets", "Packages", "ProjectSettings"]) await mkdir(join(project, name), { recursive: true });
  await writeFile(join(project, "Assets", "fixture.txt"), "asset-v1");
  await writeFile(join(project, "Packages", "manifest.json"), "manifest-v1");
  await writeFile(join(project, "ProjectSettings", "ProjectVersion.txt"), "settings-v1");
  await writeFile(join(project, "Temp", "UnityLockfile"), "keep-lock", { flag: "w" }).catch(async () => { await mkdir(join(project, "Temp"), { recursive: true }); await writeFile(join(project, "Temp", "UnityLockfile"), "keep-lock"); });
  await snapshotClosedProjectBaseline(project, baseline);
  await writeFile(join(project, "Packages", "manifest.json"), "mutated");
  await mkdir(join(project, "Builds"), { recursive: true });
  await writeFile(join(project, "Builds", "output.exe"), "generated");
  await restoreClosedProjectBaseline(project, baseline);
  assert.equal(await readFile(join(project, "Packages", "manifest.json"), "utf8"), "manifest-v1");
  assert.equal(await readFile(join(project, "Temp", "UnityLockfile"), "utf8"), "keep-lock");
  await assert.rejects(readFile(join(project, "Builds", "output.exe")));
});

test("closed project baseline fails closed before deleting project state", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix missing baseline "));
  const project = join(dir, "Project");
  const baseline = join(dir, "MissingBaseline");
  for (const name of ["Assets", "Packages", "ProjectSettings"]) await mkdir(join(project, name), { recursive: true });
  await writeFile(join(project, "Assets", "fixture.txt"), "asset-live");
  await writeFile(join(project, "Packages", "manifest.json"), "manifest-live");
  await writeFile(join(project, "ProjectSettings", "ProjectVersion.txt"), "settings-live");
  for (const name of ["Assets", "Packages", "ProjectSettings"]) await mkdir(join(baseline, name), { recursive: true });

  await assert.rejects(restoreClosedProjectBaseline(project, baseline), /baseline-complete/);
  assert.equal(await readFile(join(project, "Assets", "fixture.txt"), "utf8"), "asset-live");
  assert.equal(await readFile(join(project, "Packages", "manifest.json"), "utf8"), "manifest-live");
  assert.equal(await readFile(join(project, "ProjectSettings", "ProjectVersion.txt"), "utf8"), "settings-live");
});

test("fresh resume creates a missing closed baseline but prior closed history rejects it", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix resume baseline "));
  const project = join(dir, "Project");
  const baseline = join(dir, "Baseline");
  for (const name of ["Assets", "Packages", "ProjectSettings"]) {
    await mkdir(join(project, name), { recursive: true });
    await writeFile(join(project, name, "marker.txt"), name);
  }

  assert.equal(await ensureClosedProjectBaseline(project, baseline, true), "created");
  assert.equal(await ensureClosedProjectBaseline(project, baseline, false), "existing");
  await rm(baseline, { recursive: true, force: true });
  await assert.rejects(ensureClosedProjectBaseline(project, baseline, false), /missing or incomplete/);
});

test("closed-project routing rewrites business project fields", () => {
  const primary = join("workspace", "Dev", "UnityProject");
  const closed = join("workspace", "Dev", "ClosedProject");
  assert.deepEqual(routeCapabilityInput({ project: "./Dev/UnityProject", projectPath: primary, path: "Assets/A.asset" }, closed, primary), {
    project: closed,
    projectPath: closed,
    path: "Assets/A.asset"
  });
  assert.deepEqual(routeCapabilityInput({ project: primary }, primary, primary), { project: primary });
  const matrix = { o: { project: primary, closed_job_project: closed } };
  assert.equal(capabilityProject(matrix, "tool", { group: "package", destructiveHint: true, supportStatus: "live-bridge" }), primary);
  assert.equal(capabilityProject(matrix, "tool", { group: "package", destructiveHint: true, supportStatus: "cli-only" }), closed);
});

test("terminal capability jobs are observed without cancellation", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix terminal job "));
  const cli = await makeMock(dir);
  const log = join(dir, "calls.log");
  process.env.MATRIX_LOG = log;
  process.env.MATRIX_TERMINAL_JOB = "1";
  try {
    const project = join(dir, "Project");
    const closed = join(dir, "Closed");
    await prepareClosedProject(closed);
    const result = await runMatrix({ cli: process.execPath, cli_args: [cli], project, allow_destructive_project: project, target: "target", evidence_dir: join(dir, "evidence"), closed_job_project: closed, fixture: fixtures(), timeoutMs: 5000 });
    assert.equal(result.summary.complete, true, JSON.stringify(result.records.filter((record) => record.assertion?.status !== "PASS")));
    const calls = (await readFile(log, "utf8")).trim().split("\n").map(JSON.parse);
    assert.ok(calls.some((argv) => argv[0] === "jobs" && argv[1] === "status" && argv[2] === "job-capability"));
    assert.equal(calls.some((argv) => argv[0] === "jobs" && argv[1] === "cancel" && argv[2] === "job-capability"), false);
  } finally {
    delete process.env.MATRIX_TERMINAL_JOB;
  }
});

test("package embed cleanup resolves and verifies the registry package", async () => {
  const fixture = JSON.parse(await readFile(new URL("../.plan/evidence/final-cleanup/live-fixtures-final-3.json", import.meta.url), "utf8"));
  const steps = fixture.package_unembed.steps;
  assert.ok(steps.some((step) => step.argv.includes("reflection.method.call") && step.argv.some((arg) => arg.includes("UnityEditor.PackageManager.Client"))));
  assert.ok(steps.some((step) => step.id === "verify-probuilder-loaded" && step.expect?.exists?.includes("data.result.methods.0")));
});

test("resume reruns PlayMode session controls instead of reusing stale PASS", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix session controls "));
  const cli = await makeMock(dir);
  process.env.MATRIX_LOG = join(dir, "calls.log");
  const base = { cli: process.execPath, cli_args: [cli], project: join(dir, "P"), allow_destructive_project: join(dir, "P"), evidence_dir: join(dir, "e"), closed_job_project: join(dir, "C"), fixture: fixtures(), capabilities: ["runtime.only"], include_playmode: true, timeoutMs: 5000 };
  const first = await runMatrix(base);
  const resumed = await runMatrix({ ...base, resume: true });
  for (const id of ["control.playmode-initial-restore.verify", "control.play-baseline.initial-editor.editor.stop.ready", "tool.editor.play.transition", "playmode.ready", "tool.editor.stop.transition", "playmode.exit-ready", "control.play-baseline.post-playmode.editor.stop.ready", "control.playmode-fast.verify", "control.playmode-restore.verify"]) {
    assert.notEqual(resumed.records.find((record) => record.id === id)?.at, first.records.find((record) => record.id === id)?.at, `${id} must execute in every runner process`);
  }
});

test("resume rebuilds expected cases and retains full-run checkpoints", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix resume ")); const cli = await makeMock(dir); const base = { cli: process.execPath, cli_args: [cli], project: join(dir, "P"), evidence_dir: join(dir, "e"), closed_job_project: join(dir, "C"), fixture: {}, timeoutMs: 5000 };
  await runMatrix(base);
  const checkpoint = { id: "control.synthetic-checkpoint", project: base.project, target: null, buildStamp: null, assertion: { status: "PASS" } };
  await appendFile(join(base.evidence_dir, "calls.jsonl"), `${JSON.stringify(checkpoint)}\n`, "utf8");
  const resumed = await runMatrix({ ...base, resume: true });
  assert.equal(resumed.summary.complete, false);
  assert.ok(resumed.summary.expectedCaseIds.includes("control.concurrency"));
  assert.ok(resumed.summary.expectedCaseIds.includes(checkpoint.id));
});

test("resume reruns setup controls for a failed terminal case", async () => {
  const dir = await mkdtemp(join(tmpdir(), "matrix resume setup "));
  const cli = await makeMock(dir);
  const evidence = join(dir, "e");
  const log = join(dir, "calls.log");
  process.env.MATRIX_LOG = log;
  const project = join(dir, "P");
  const closed = join(dir, "C");
  await prepareClosedProject(closed);
  const base = { cli: process.execPath, cli_args: [cli], project, allow_destructive_project: project, evidence_dir: evidence, closed_job_project: closed, fixture: fixtures(), timeoutMs: 5000 };
  const first = await runMatrix(base);
  assert.equal(first.summary.complete, true);
  const terminal = first.records.find((record) => record.id === "tool.runtime.only.play.default");
  const jsonlPath = join(evidence, "calls.jsonl");
  const original = await readFile(jsonlPath, "utf8");
  await writeFile(jsonlPath, `${original}${JSON.stringify({ ...terminal, assertion: { status: "FAIL", issue: "synthetic interrupted case" } })}\n`, "utf8");
  await writeFile(process.env.MATRIX_STATE, "false", "utf8");
  const before = (await readFile(log, "utf8")).split("\n").filter((line) => line.includes('"editor.play"')).length;
  const resumed = await runMatrix({ ...base, resume: true });
  assert.equal(resumed.summary.complete, true, JSON.stringify(resumed.records.filter((record) => record.assertion?.status !== "PASS")));
  const after = (await readFile(log, "utf8")).split("\n").filter((line) => line.includes('"editor.play"')).length;
  assert.ok(after > before, "failed case must rebuild its PlayMode setup instead of reusing stale PASS controls");
});
