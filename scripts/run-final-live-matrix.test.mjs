import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import {
  acquireProjectSourceLock,
  acquireRunnerLock,
  appendEvidenceLine,
  assertError,
  assertNoSymbolicAncestor,
  assertPositiveSuccess,
  assertSuccess,
  capabilityFixtureId,
  capabilityProject,
  callRecoveryEffect,
  concurrencyOverlapMs,
  concurrencySerialProof,
  journalLifecycleIssue,
  PLAYMODE_RESOLVE_BLOCKED_TOOLS,
  PLAYMODE_UNAVAILABLE_CODE_OVERRIDES,
  DIRECT_LIVE_CASES,
  directInvariantIssue,
  coverExternalStateDependentTool,
  EXTERNAL_STATE_DEPENDENT_TOOL_NAMES,
  MATRIX_OWNED_DIALOG,
  directPhaseIds,
  extensionAvailabilityIssues,
  fixtureError,
  fixtureExpectationIssue,
  fixtureStepRetriesReadyAssertion,
  FROZEN_DIRECT_TOOL_NAMES,
  hasReusableDirectCase,
  isReadyEditorState,
  loadInternalResultSchemas,
  loadRuntimeInventoryBaseline,
  markOwnedProjectSnapshotClean,
  markOwnedProjectSnapshotReconciled,
  ownedEmbeddedPackageCleanupTarget,
  ownedSceneBakeCleanupPlan,
  packageResolveReconciliationIssue,
  parseOptions,
  parsePublicExample,
  prepareOwnedProjectSnapshot,
  publicDescriptorIssue,
  publicMatrixArgv,
  readDirectCapture,
  resolveCaseTemplate,
  restoreOwnedProjectSnapshot,
  routeCapabilityFixtureInput,
  routeCapabilityInput,
  runtimeInventoryIssues,
  sceneBakeArtifactDirectory,
  runCli,
  runDirectCasePhase,
  runFixtureSequence,
  unavailableDescriptorErrorCode,
  verifyOwnedProjectSnapshotClosure
} from "./run-final-live-matrix.mjs";

/** 为 filesystem-only snapshot 测试建立最小授权 Dev project。 */
async function createOwnedProject(root) {
  await Promise.all([
    mkdir(join(root, "Assets", "Scenes"), { recursive: true }),
    mkdir(join(root, "ProjectSettings"), { recursive: true }),
    mkdir(join(root, "Packages", "com.alpsckr.unitycli", "Editor"), { recursive: true })
  ]);
  await Promise.all([
    writeFile(join(root, "Assets", "Scenes", "Main.unity"), "baseline-scene\n"),
    writeFile(join(root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.61f1\n"),
    writeFile(join(root, "Packages", "com.alpsckr.unitycli", "Editor", "Bridge.cs"), "baseline-package\n"),
    writeFile(join(root, "Packages", "manifest.json"), "{\"dependencies\":{}}\n"),
    writeFile(join(root, "Packages", "packages-lock.json"), "{\"dependencies\":{}}\n")
  ]);
}

test("parseOptions accepts direct Live Editor matrix options", () => {
  const options = parseOptions([
    "--cli", process.execPath,
    "--cli-arg", "dist/cli.js",
    "--project", "D:/Dev/UnityProject",
    "--offline-project", "D:/evidence/offline-project",
    "--evidence-dir", "D:/evidence",
    "--capability", "editor.refresh",
    "--timeout-ms", "5000",
    "--fixture-file", "D:/evidence/live-fixtures.json"
  ]);
  assert.equal(options.cli, process.execPath);
  assert.deepEqual(options.cli_args, ["dist/cli.js"]);
  assert.deepEqual(options.capabilities, ["editor.refresh"]);
  assert.equal(options.offline_project, "D:/evidence/offline-project");
  assert.equal(options.timeoutMs, 5000);
  assert.equal(options.fixture_file, "D:/evidence/live-fixtures.json");
  assert.deepEqual(options.fixture, {});
  assert.throws(() => parseOptions([
    "--cli", process.execPath,
    "--project", "D:/Dev/UnityProject",
    "--evidence-dir", "D:/evidence",
    "--fixture-setup", '{"steps":[]}'
  ]), /Inline fixture arguments are forbidden/);
  assert.throws(() => parseOptions([
    "--cli", process.execPath,
    "--project", "D:/Dev/UnityProject",
    "--evidence-dir", "D:/evidence",
    "--resume"
  ]), /resume is forbidden/);
});

test("parseOptions controls-only slice is explicit and mutually exclusive with capability slices", () => {
  const options = parseOptions([
    "--cli", process.execPath,
    "--project", "D:/Dev/UnityProject",
    "--evidence-dir", "D:/evidence-controls",
    "--controls-only",
    "--fixture-file", "D:/evidence/live-fixtures.json"
  ]);
  assert.equal(options.controls_only, true);
  assert.equal(options.include_playmode, false);
  assert.deepEqual(options.capabilities, []);
  const full = parseOptions(["--cli", process.execPath, "--project", "P", "--evidence-dir", "E"]);
  assert.equal(full.controls_only, false);
  // 两套切片语义互斥：controls-only进程不得混入capability过滤。
  assert.throws(() => parseOptions([
    "--cli", process.execPath,
    "--project", "P",
    "--evidence-dir", "E",
    "--controls-only",
    "--capability", "go.create"
  ]), /--controls-only forbids --capability/);
  assert.throws(() => parseOptions([
    "--cli", process.execPath,
    "--project", "P",
    "--evidence-dir", "E",
    `--allow-${"destructive-project"}`, "P"
  ]), /Unexpected argument/);
});

test("fixture retries execution timeout when the caller explicitly requests it", async () => {
  const calls = [];
  const matrix = {
    o: { project: "D:/Project" },
    failed: async () => ({ assertion: { status: "FAIL" } }),
    call: async (id, argv, assertion) => {
      calls.push({ id, argv });
      const envelope = calls.length === 1
        ? { ok: false, requestId: "req_timeout", error: { code: "E_EXECUTION_TIMEOUT", message: "timeout" } }
        : { ok: true, requestId: "req_retry", data: {} };
      const result = { exitCode: calls.length === 1 ? 1 : 0 };
      const issue = assertion(envelope, result);
      return { argv, envelope, exitCode: result.exitCode, result, assertion: { status: issue ? "FAIL" : "PASS", ...(issue ? { issue } : {}) } };
    }
  };
  const fixture = { assert: { ready: true }, steps: [{ id: "effect", argv: ["tools", "run", "profiler.stop", "--input", "{}"], expect: {}, retry: { attempts: 2, errorCodes: ["E_EXECUTION_TIMEOUT"] } }] };
  assert.equal(await runFixtureSequence(matrix, "reset", fixture), true);
  assert.equal(calls.length, 2);
});

test("reconciliation invokes the caller-selected retry after timeout", async () => {
  let spawned = false;
  const record = await callRecoveryEffect({
    call: async () => { spawned = true; return { assertion: { status: "PASS" } }; }
  }, "control.source-restore.package-resolve", ["tools", "run", "package.resolve", "--input", "{}"], () => null, { cleanup: true });
  assert.equal(spawned, true);
  assert.equal(record.assertion.status, "PASS");
});

test("package.resolve reconciliation requires a complete resolved package snapshot", () => {
  const hash = "a".repeat(64);
  const resolved = { ok: true, requestId: "req_resolve", data: { status: "resolved", manifestHash: hash, lockHash: hash, packageSnapshotHash: hash, packageCount: 1, packages: [{ name: "com.example", version: "1.0.0", source: "registry" }] } };
  assert.equal(packageResolveReconciliationIssue(resolved, { exitCode: 0 }, { manifestHash: hash, lockHash: hash }), null);
  assert.match(packageResolveReconciliationIssue({ ...resolved, data: { ...resolved.data, status: "pending" } }, { exitCode: 0 }), /resolved terminal state/);
  assert.match(packageResolveReconciliationIssue(resolved, { exitCode: 0 }, { manifestHash: "b".repeat(64), lockHash: hash }), /differs from the restored snapshot/);
  assert.match(packageResolveReconciliationIssue({ ...resolved, data: { ...resolved.data, lockHash: "bad" } }, { exitCode: 0 }), /invalid lockHash/);
  assert.match(packageResolveReconciliationIssue({ ...resolved, data: { ...resolved.data, packageCount: 2 } }, { exitCode: 0 }), /snapshot is incomplete/);
  assert.match(packageResolveReconciliationIssue(resolved, { exitCode: 1 }), /exited 1/);
});

test("current JavaScript harness uses only the production public CLI entrypoint", async () => {
  const [source, packageJson] = await Promise.all([
    readFile(new URL("./run-final-live-matrix.mjs", import.meta.url), "utf8"),
    readFile(new URL("../package.json", import.meta.url), "utf8").then(JSON.parse)
  ]);
  assert.equal(packageJson.scripts["matrix:live"], "node scripts/run-final-live-matrix.mjs");
  assert.equal(packageJson.scripts["matrix:live:dist"], "node scripts/run-final-live-matrix.mjs --cli node --cli-arg dist/cli.js");
  assert.match(source, /spawn\(cli, argv, \{ shell: false, windowsHide: true/);
  assert.match(source, /Required --fixture-file is missing/);
  assert.match(source, /const lockPath = resolve\(evidenceDir, "\.runner\.lock"\)/);
  assert.match(source, /releaseLock = await acquireRunnerLock\(options\.evidence_dir\)/);
  assert.match(source, /if \(record\?\.assertion\?\.status === "FAIL"\) throw new MatrixTerminalFailure\(record\)/);
  assert.match(source, /await rm\(target\.embeddedPath, \{ recursive: true, force: false \}\)[\s\S]*cleanup\.package\.resolve[\s\S]*cleanup\.ready[\s\S]*cleanup\.package\.info/);
  assert.match(source, /prepareOwnedBakeStartSnapshot[\s\S]*cleanupOwnedBakeStartCase[\s\S]*cleanup\.graphics\.bake-status[\s\S]*cleanup\.asset\.delete[\s\S]*cleanup\.asset\.refresh[\s\S]*Final scene bake artifact path\/digest closure/);
  assert.match(source, /recoverAllPendingOwnedBakeSnapshots\(m\)[\s\S]*setCasePlayMode\(m, "initial-editor"/);
  assert.match(source, /"prepared", "effect-terminal", "clean"/);
  assert.match(source, /prepared graphics\.bake-start snapshot has no proven terminal success; inspect the scene before deciding whether to retry/);
  const invokeSource = source.slice(source.indexOf("async function invoke"));
  assert.ok(invokeSource.indexOf('writeSceneBakeSnapshotStatus(bakeSnapshot, "effect-terminal"') < invokeSource.indexOf("cleanupOwnedBakeStartCase(m, caseId"));
  assert.match(source, /Persisted baseline digest changed[\s\S]*asset\.refresh/);
  assert.match(source, /prepareOwnedProjectSnapshot\(this\.o\.project[\s\S]*setCasePlayMode\(m, "initial-editor"/);
  assert.match(source, /semantic-reset-complete[\s\S]*restoreOwnedProjectSnapshot[\s\S]*package\.resolve[\s\S]*editor\.refresh[\s\S]*scene\.load[\s\S]*markOwnedProjectSnapshotClean/);
  assert.match(source, /blockMissingRequiredFixture\(m, "control\.setup"[\s\S]*runRequiredFixtureSequence\(m, "setup"[\s\S]*recoverFinalMatrixState\(m\)[\s\S]*recordRunnerFailure\(m, "control\.source-restore\.exception"[\s\S]*await report\(m\)/);
  assert.match(source, /ACTIVE_OPERATION_SCOPES[\s\S]*active-\$\{scope\}\.json[\s\S]*Request-level journal must not be produced/);
  assert.doesNotMatch(source, /E_REQUEST_OUTCOME_UNKNOWN|matchingUncertainCall|exact outcome-unknown/i);
  assert.match(source, /const playReady = await m\.call\("playmode\.ready"[\s\S]*stopAfterTerminalFailure\(playReady\)/);
  assert.match(source, /return blockTerminal\(m, "playmode\.transition"[\s\S]*stopAfterTerminalFailure\(await m\.failed\("control\.post-playmode-edit"/);
  assert.match(source, /blockMissingRequiredFixture\(m, "control\.concurrency"[\s\S]*concurrencyRecord\.assertion\?\.status !== "PASS"/);
  assert.match(source, /callRecoveryEffect[\s\S]*source-restore\.package-resolve[\s\S]*packageResolveReconciliationIssue[\s\S]*source-restore\.editor-refresh[\s\S]*source-restore\.scene-load/);
  assert.match(source, /const expectedPublicPath = `Packages\/\$\{packageId\}`/);
  assert.doesNotMatch(source, /\.plan[\\/]evidence|public-output-modal|run-live-matrix\.ps1|\.ps1["'`]|powershell|pwsh/i);
  assert.doesNotMatch(source, /spawn\(["'`]Unity(?:\.exe)?["'`]/i);
});

test("evidence append retries sharing violations without replaying the CLI", async () => {
  const attempts = [];
  const delays = [];
  await appendEvidenceLine("calls.jsonl", "record\n", {
    maxAttempts: 3,
    wait: async (delayMs) => { delays.push(delayMs); },
    append: async (path, line) => {
      attempts.push({ path, line });
      if (attempts.length < 3) throw Object.assign(new Error("locked"), { code: "EBUSY" });
    }
  });
  assert.equal(attempts.length, 3);
  assert.deepEqual(delays, [250, 250]);
  assert.deepEqual(attempts.at(-1), { path: "calls.jsonl", line: "record\n" });

  let nonSharingAttempts = 0;
  await assert.rejects(
    () => appendEvidenceLine("calls.jsonl", "record\n", {
      append: async () => {
        nonSharingAttempts += 1;
        throw Object.assign(new Error("disk full"), { code: "ENOSPC" });
      }
    }),
    (error) => error?.code === "ENOSPC"
  );
  assert.equal(nonSharingAttempts, 1);
});

test("component.add positive example is absent from the shared ComponentTarget baseline", async () => {
  const [descriptorSource, setupSource] = await Promise.all([
    readFile(new URL("../unitypkg/Editor/Tools/component/add.cs", import.meta.url), "utf8"),
    readFile(new URL("./live-matrix/fixtures/live-matrix-setup.cs", import.meta.url), "utf8")
  ]);
  assert.match(descriptorSource, /Component = "AudioSource"/);
  assert.match(setupSource, /EnsureComponent<Rigidbody>\(componentTarget\)/);
  assert.doesNotMatch(setupSource, /EnsureComponent<AudioSource>\(componentTarget\)/);
});

test("parseOptions rejects missing direct-call prerequisites", () => {
  assert.throws(() => parseOptions(["--cli", "node"]), /Required --project is missing/);
  assert.throws(() => parseOptions(["--cli", "node", "--project", "P", "--evidence-dir", "E", "--timeout-ms", "0"]), /must be positive/);
});

test("parsePublicExample maps structured input and execution without argv", () => {
  assert.deepEqual(
    parsePublicExample({ description: "Echo.", input: { message: "hello" } }, "tool", "sample.echo"),
    { input: { message: "hello" }, flags: [] }
  );
  assert.deepEqual(
    parsePublicExample({ description: "Preview.", input: {}, execution: { dryRun: true, timeoutMs: 2500 } }, "tool", "sample.info"),
    { input: {}, flags: ["--dry-run", "--timeout-ms", "2500"] }
  );
  assert.throws(
    () => parsePublicExample({ input: {}, argv: ["tools", "run", "sample.echo"] }, "tool", "sample.echo"),
    /unsupported fields: argv/
  );
  assert.throws(
    () => parsePublicExample({ input: { dryRun: true } }, "tool", "sample.echo"),
    /execution-only dryRun/
  );
  assert.throws(
    () => parsePublicExample({ input: {}, execution: { timeoutMs: 0 } }, "tool", "sample.echo"),
    /positive integer/
  );
});

test("public descriptor audit rejects removed fields and default availability", () => {
  const descriptor = {
    name: "sample.echo", description: "Echo one value.", inputSchema: { type: "object" },
    destructiveHint: false, playModeSupport: "supported", supportsDryRun: false, target: "editor",
    examples: [{ description: "Echo.", input: { value: "hello" } }],
  };
  assert.equal(publicDescriptorIssue(descriptor), null);
  assert.match(publicDescriptorIssue({ ...descriptor, outputSchema: {} }), /outputSchema/);
  assert.match(publicDescriptorIssue({ ...descriptor, available: true }), /omit default available:true/);
  assert.match(publicDescriptorIssue({ ...descriptor, available: false }), /unavailableReason/);
});

test("Live harness loads every package result schema from current compiled metadata", async () => {
  const schemas = await loadInternalResultSchemas(resolve("dist", "cli.js"));
  assert.ok(schemas.size >= 400);
  assert.equal(schemas.get("code.exec")?.oneOf instanceof Array, true);
  assert.equal(schemas.get("scene.gameobject")?.type, "object");
});

test("external-state-dependent Tools do not use descriptor placeholders as positive matrix inputs", async () => {
  assert.deepEqual(EXTERNAL_STATE_DEPENDENT_TOOL_NAMES, ["editor.dialog.click"]);
  const source = await readFile(new URL("./run-final-live-matrix.mjs", import.meta.url), "utf8");
  assert.match(source, /fresh editor\.dialogs result with a real dialogId and enabled button/);
  assert.match(source, /tool\.\$\{name\}\.external-state\.dialogs/);
  assert.match(source, /MATRIX_OWNED_DIALOG/);
  const removedToolName = `editor.launch${"-dialog.dismiss"}`;
  assert.doesNotMatch(source, new RegExp(removedToolName.replaceAll(".", "\\.")));
});

test("test.cancel coverage freezes and cancels one coordinator-owned run identity", async () => {
  const source = await readFile(new URL("./run-final-live-matrix.mjs", import.meta.url), "utf8");
  assert.match(source, /external-state\.save-scene[\s\S]*scene\.save[\s\S]*Pre-cancellation scene\.save did not prove a clean MatrixScene/);
  assert.match(source, /WaitForExplicitCancellation/);
  assert.match(source, /E_TEST_CANCEL_IDENTITY_UNPROVEN/);
  assert.match(source, /test\.cancel actual did not prove the planned GUID inactive/);
  assert.match(source, /E_TEST_RUN_CANCELLED/);
  assert.match(source, /tool\.test\.cancel\.external-state\.post-idle/);
});

test("matrix dialog coverage never runs an unknown modal and uses the fresh owned dialog", async () => {
  const descriptor = { resultSchema: {} };
  const ownedDialog = (dialogId = "fresh-owned-dialog") => ({
    dialogId,
    title: MATRIX_OWNED_DIALOG.title,
    message: `Fixture ${MATRIX_OWNED_DIALOG.messageMarker}`,
    buttons: ["OK", MATRIX_OWNED_DIALOG.button]
  });
  const runCoverage = async (dialogs) => {
    const calls = [];
    const records = [];
    const matrix = {
      descriptors: new Map(),
      notApplicable: async (id, reason, meta) => { records.push({ status: "N/A", id, reason, meta }); },
      blocked: async (id, reason, meta) => { records.push({ status: "BLOCKED", id, reason, meta }); },
      call: async (id, argv, assertion) => {
        calls.push({ id, argv });
        const envelope = id.endsWith(".describe")
          ? { ok: true, requestId: "describe", data: { name: "editor.dialogs", description: "Read dialogs.", inputSchema: { type: "object" }, destructiveHint: false, playModeSupport: "supported", supportsDryRun: false, target: "editor" } }
          : { ok: true, requestId: "fresh-observation", data: { observedAt: "2026-01-01T00:00:00.000Z", dialogs } };
        const result = { exitCode: 0 };
        const issue = assertion(envelope, result);
        return { envelope, result, assertion: { status: issue ? "FAIL" : "PASS" } };
      }
    };
    const toolRuns = [];
    await coverExternalStateDependentTool(matrix, "editor.dialog.click", descriptor, "editor", "D:/Project", async (...args) => { toolRuns.push(args); });
    return { calls, records, toolRuns };
  };

  const empty = await runCoverage([]);
  assert.equal(empty.toolRuns.length, 0);
  assert.deepEqual(empty.records.map((record) => record.status), ["N/A", "N/A", "N/A"]);
  assert.match(empty.records.at(-1).reason, /No matrix-owned dialog fixture was injected/);

  const unknown = await runCoverage([{ dialogId: "external-dialog", title: "External modal", message: "Do not touch", buttons: ["Delete"] }]);
  assert.equal(unknown.toolRuns.length, 0);
  assert.deepEqual(unknown.records.map((record) => record.status), ["N/A", "N/A", "BLOCKED"]);
  assert.match(unknown.records.at(-1).reason, /Unknown external modal/);
  assert.equal(unknown.calls.some((call) => call.argv[0] === "tools" && call.argv[1] === "run" && call.argv[2] === "editor.dialog.click"), false);

  const multiple = await runCoverage([ownedDialog("owned-a"), ownedDialog("owned-b")]);
  assert.equal(multiple.toolRuns.length, 0);
  assert.equal(multiple.records.at(-1).status, "BLOCKED");
  assert.match(multiple.records.at(-1).reason, /Multiple matrix-owned/);

  const owned = await runCoverage([{
    dialogId: "external-first",
    title: "External modal",
    message: "Do not touch",
    buttons: ["Delete"]
  }, ownedDialog("fresh-owned-dialog")]);
  assert.equal(owned.records.length, 2);
  assert.equal(owned.toolRuns.length, 1);
  assert.deepEqual(owned.toolRuns[0].slice(1, 6), ["tool", "editor.dialog.click", "external-state.click", descriptor, { dialogId: "fresh-owned-dialog", button: "Cancel" }]);
});

test("matrix readiness uses the current immediate editor.state shape", () => {
  const state = (value, status = "ready") => ({ ok: true, data: { status, state: { playMode: { known: true, value } } } });
  assert.equal(isReadyEditorState(state("edit"), false), true);
  assert.equal(isReadyEditorState(state("play"), true), true);
  assert.equal(isReadyEditorState(state("edit"), true), false);
  assert.equal(isReadyEditorState(state("edit", "transitioning"), false), false);
  assert.equal(isReadyEditorState({ ok: true, data: { status: "ready", state: { playMode: { known: false } } } }, false), false);
});

test("fixture assertion retry is limited to explicit editor ready convergence", () => {
  const readyStep = { argv: ["tools", "run", "editor.state", "--input", "{}"], expect: { equals: { "data.status": "ready" } } };
  assert.equal(fixtureStepRetriesReadyAssertion(readyStep), true);
  assert.equal(fixtureStepRetriesReadyAssertion({ ...readyStep, argv: ["tools", "run", "editor.state"] }), false);
  assert.equal(fixtureStepRetriesReadyAssertion({ ...readyStep, expect: { equals: { "data.status": "transitioning" } } }), false);
  assert.equal(fixtureStepRetriesReadyAssertion({ ...readyStep, expect: { equals: { "data.state.playMode.value": "edit" } } }), false);
});

test("unavailable descriptor probes preserve the stable availability error", () => {
  assert.equal(unavailableDescriptorErrorCode({ unavailableReason: "E_TOOL_UNAVAILABLE: not registered" }), "E_TOOL_UNAVAILABLE");
  assert.equal(unavailableDescriptorErrorCode({ unavailableReason: "E_EXTENSION_DEPENDENCY_MISSING: package absent" }), "E_EXTENSION_DEPENDENCY_MISSING");
  assert.equal(unavailableDescriptorErrorCode({}), "E_TOOL_UNAVAILABLE");
});

test("package.embed cleanup accepts only its exact owned package directory", async () => {
  const project = await mkdtemp(join(tmpdir(), "unity-cli-matrix-中文项目-"));
  try {
    const expected = resolve(project, "Packages", "com.unity.probuilder");
    assert.deepEqual(
      ownedEmbeddedPackageCleanupTarget(project, { packageId: "com.unity.probuilder", embeddedPath: "Packages/com.unity.probuilder" }),
      { packageId: "com.unity.probuilder", embeddedPath: expected, publicPath: "Packages/com.unity.probuilder" }
    );
    assert.throws(
      () => ownedEmbeddedPackageCleanupTarget(project, { packageId: "com.unity.probuilder", embeddedPath: expected }),
      /not its exact project-relative package directory/
    );
    assert.throws(
      () => ownedEmbeddedPackageCleanupTarget(project, { packageId: "com.unity.probuilder", embeddedPath: "Packages/other" }),
      /not its exact project-relative package directory/
    );
    assert.throws(
      () => ownedEmbeddedPackageCleanupTarget(project, { packageId: "..", embeddedPath: resolve(project, "Packages") }),
      /invalid package id/
    );
    assert.throws(
      () => ownedEmbeddedPackageCleanupTarget(project, { packageId: "com.unity.probuilder" }),
      /no embedded path/
    );
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("graphics.bake-start cleanup is confined to the active scene lighting directory", async () => {
  const project = await mkdtemp(join(tmpdir(), "unity-cli-bake-cleanup-中文项目-"));
  try {
    const location = sceneBakeArtifactDirectory(project, "Assets/UnityCLIValidation/MatrixScene.unity");
    assert.equal(location.projectRoot, resolve(project));
    assert.equal(location.absoluteDirectory, resolve(project, "Assets/UnityCLIValidation/MatrixScene"));
    assert.equal(location.publicDirectory, "Assets/UnityCLIValidation/MatrixScene");
    assert.throws(() => sceneBakeArtifactDirectory(project, "Assets/../Outside.unity"), /outside Assets|escapes/);
    assert.throws(() => sceneBakeArtifactDirectory(project, "Packages/Scene.unity"), /Assets/);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("graphics.bake-start cleanup rejects junction ownership escapes", async () => {
  const owner = await mkdtemp(join(tmpdir(), "unity-cli-bake-owner-"));
  const outside = await mkdtemp(join(tmpdir(), "unity-cli-bake-outside-"));
  const junction = join(owner, "linked");
  try {
    await mkdir(outside, { recursive: true });
    await symlink(outside, junction, process.platform === "win32" ? "junction" : "dir");
    await assert.rejects(() => assertNoSymbolicAncestor(join(junction, "artifact.bin"), owner, "test path"), /symbolic link or junction/);
    await assert.rejects(() => assertNoSymbolicAncestor(outside, owner, "test path"), /escapes its owner root/);
  } finally {
    await rm(owner, { recursive: true, force: true });
    await rm(outside, { recursive: true, force: true });
  }
});

test("graphics.bake-start cleanup restores baseline bytes and deletes only paired additions", () => {
  const lighting = "Assets/UnityCLIValidation/MatrixScene/LightingData.asset";
  const reflection = "Assets/UnityCLIValidation/MatrixScene/ReflectionProbe-0.exr";
  const before = [
    { path: lighting, sha256: "before-lighting" },
    { path: `${lighting}.meta`, sha256: "lighting-meta" }
  ];
  const after = [
    { path: lighting, sha256: "after-lighting" },
    { path: `${lighting}.meta`, sha256: "lighting-meta" },
    { path: reflection, sha256: "reflection" },
    { path: `${reflection}.meta`, sha256: "reflection-meta" }
  ];
  assert.deepEqual(ownedSceneBakeCleanupPlan(before, after), {
    deleteAssets: [reflection],
    restoreFiles: [lighting],
    refreshAssets: [lighting]
  });
  assert.throws(
    () => ownedSceneBakeCleanupPlan(before, [...before, { path: `${reflection}.meta`, sha256: "orphan" }]),
    /cannot attribute orphan meta/
  );
  assert.throws(
    () => ownedSceneBakeCleanupPlan(before, [...before, { path: reflection, sha256: "asset-without-meta" }]),
    /added asset without meta/
  );
  assert.throws(
    () => ownedSceneBakeCleanupPlan([{ path: `${lighting}.meta`, sha256: "orphan" }], []),
    /baseline contains orphan meta/
  );
});

test("local matrix cases bind deterministic caches and package projects", () => {
  assert.deepEqual(routeCapabilityFixtureInput({ api: "UnityEngine.Camera", docsDir: ".cache/unity-docs" }, "docs.get", { docs_cache_dir: "D:/evidence/docs-cache" }), { api: "UnityEngine.Camera", docsDir: "D:/evidence/docs-cache" });
  assert.deepEqual(routeCapabilityFixtureInput({ query: "Camera" }, "docs.search", { docs_cache_dir: "D:/evidence/docs-cache" }), { query: "Camera", docsDir: "D:/evidence/docs-cache" });
  assert.deepEqual(routeCapabilityFixtureInput({ member: "UnityEngine.Camera.Render", reflectDir: ".cache/unity-reflect" }, "reflect.member", { reflect_cache_dir: "D:/evidence/reflect-cache" }), { member: "UnityEngine.Camera.Render", reflectDir: "D:/evidence/reflect-cache" });
  assert.deepEqual(routeCapabilityFixtureInput({ query: "Camera" }, "reflect.search", { reflect_cache_dir: "D:/evidence/reflect-cache" }), { query: "Camera", reflectDir: "D:/evidence/reflect-cache" });
  assert.deepEqual(routeCapabilityFixtureInput({ type: "UnityEngine.Camera" }, "reflect.type", { reflect_cache_dir: "D:/evidence/reflect-cache" }), { type: "UnityEngine.Camera", reflectDir: "D:/evidence/reflect-cache" });
  assert.deepEqual(routeCapabilityFixtureInput({}, "package.registry.clear", { project: "D:/Dev/UnityProject" }), { project: "D:/Dev/UnityProject" });
  assert.deepEqual(routeCapabilityFixtureInput({ project: { invalid: true } }, "package.registry.clear", { project: "D:/Dev/UnityProject" }), { project: { invalid: true } });
  assert.deepEqual(routeCapabilityFixtureInput({ name: "Acme" }, "package.scoped-registry.remove", { project: "D:/Dev/UnityProject" }), { name: "Acme", project: "D:/Dev/UnityProject" });
  assert.deepEqual(routeCapabilityFixtureInput({ target: "Camera" }, "camera.info", { docs_cache_dir: "D:/evidence/docs-cache", reflect_cache_dir: "D:/evidence/reflect-cache" }), { target: "Camera" });
});

test("matrix assertions validate the exact public envelope and business schema", () => {
  const requestId = "req_matrix_contract";
  const schema = {
    type: "object",
    required: ["status", "count"],
    properties: { status: { type: "string" }, count: { type: "integer" } },
    additionalProperties: false
  };
  assert.equal(assertPositiveSuccess({ ok: true, requestId, data: { status: "completed", count: 1 }, warnings: [] }, schema), null);
  assert.match(assertPositiveSuccess({ ok: true, requestId, data: { status: "failed" } }), /terminal status failed/);
  assert.match(assertPositiveSuccess({ ok: true, requestId, result: {} }), /business data/);
  assert.match(assertPositiveSuccess({ ok: true, requestId, data: { status: "completed", count: 1 }, audit: {} }, schema), /unexpected fields: audit/);
  assert.match(assertSuccess({ ok: true, requestId, data: { status: "completed", count: 1, jobId: "legacy" } }, schema), /unexpected fields: jobId/);
  const nullableSchema = {
    type: "object",
    properties: {
      ageMs: { type: ["integer", "null"] },
      nested: { type: ["object", "null"], properties: { value: { type: "string" } }, additionalProperties: false }
    },
    additionalProperties: false
  };
  assert.equal(assertSuccess({ ok: true, requestId, data: { ageMs: 4, nested: null } }, nullableSchema), null);
  assert.equal(assertSuccess({ ok: true, requestId, data: { ageMs: null, nested: { value: "ok" } } }, nullableSchema), null);
  assert.match(assertSuccess({ ok: true, requestId, data: { ageMs: 4.5, nested: null } }, nullableSchema), /ageMs must be integer,null/);
  assert.equal(assertError({ ok: false, requestId, error: {
    code: "E_SCHEMA_INVALID", message: "bad input", transient: false, retryAfterMs: 0,
    nextActions: ["Fix input."], details: { path: "$.input", expected: "object", actual: "string" }
  } }), null);
  assert.match(assertError({ ok: false, requestId, logs: [], error: {
    code: "E_SCHEMA_INVALID", message: "bad input", transient: false, retryAfterMs: 0,
    nextActions: ["Fix input."], details: { path: "$.input", expected: "object", actual: "string" }
  } }), /unexpected fields: logs/);
});

test("matrix routes capabilities by instance and reserves project for control commands", () => {
  assert.deepEqual(publicMatrixArgv(["tools", "run", "sample.echo", "--input", "{}"], "D:/Main", "D:/Main", "unity-main"), ["tools", "run", "sample.echo", "--input", "{}", "--instance", "unity-main", "--raw"]);
  assert.deepEqual(publicMatrixArgv(["tools", "run", "editor.state", "--input", "{}"], "D:/Main", "D:/Main", "unity-main"), ["tools", "run", "editor.state", "--input", "{}", "--instance", "unity-main", "--raw"]);
  assert.deepEqual(publicMatrixArgv(["tools", "run", "editor.state", "--input", "{}", "--instance", "unity-stale"], "D:/Main", "D:/Main", "unity-main"), ["tools", "run", "editor.state", "--input", "{}", "--instance", "unity-stale", "--raw"]);
  assert.deepEqual(publicMatrixArgv(["extensions", "doctor"], "D:/Main", "D:/Main", "unity-main"), ["extensions", "doctor", "--project", "D:/Main", "--raw"]);
  assert.doesNotMatch(publicMatrixArgv(["tools", "run", "sample.echo"], "D:/Main", "D:/Main", "unity-main").join(" "), /--project/);
});

test("matrix routes isolated project fields without changing the primary project", () => {
  const input = { project: "D:/Main", projectPath: "D:/Main", name: "sample" };
  assert.deepEqual(routeCapabilityInput(input, "D:/Clone", "D:/Main"), {
    project: "D:/Clone", projectPath: "D:/Clone", name: "sample"
  });
  assert.equal(routeCapabilityInput(input, "D:/Main", "D:/Main"), input);
  const matrix = { o: { project: "D:/Main", offline_project: "D:/Closed" } };
  assert.equal(capabilityProject(matrix, "tool", { name: "package.bridge-remove" }), "D:/Closed");
  assert.equal(capabilityProject(matrix, "tool", { name: "package.resolve" }), "D:/Main");
});

test("extension doctor validation consumes the current checks envelope", () => {
  const listData = { extensions: [{ id: "probuilder", toolCount: 37, providedTools: ["probuilder.create-shape"] }] };
  const healthy = { checks: [{ id: "probuilder", availability: "available", installed: true, missingPackageIds: [], missingFiles: [], changedFiles: [], unknownFiles: [] }] };
  assert.deepEqual(extensionAvailabilityIssues(listData, healthy), []);
  assert.deepEqual(extensionAvailabilityIssues(listData, { extensions: healthy.checks }).map((issue) => issue.id), ["probuilder"]);
  assert.deepEqual(extensionAvailabilityIssues(listData, { checks: [{ ...healthy.checks[0], installed: false }] }).map((issue) => issue.id), ["probuilder"]);
  assert.deepEqual(extensionAvailabilityIssues({ extensions: [{ id: "empty", toolCount: 0 }] }, { checks: [] }), []);
});

test("runtime inventory is exact for product capabilities and requires Dev fixtures", async () => {
  const baseline = await loadRuntimeInventoryBaseline();
  assert.equal(baseline.tools.length, 418);
  assert.equal(baseline.tools.includes("docs.list") || baseline.tools.includes("docs.topic"), false);
  const projectTools = [
    "test", "validation.echo", "validation.ui-cache", "validation.wait", "validation.wait-status",
    "UnityPipeline_test", "UnityPipeline_validation.official.any-output", "UnityPipeline_validation.official.dictionary",
    "UnityPipeline_validation.official.jobject", "UnityPipeline_validation.official.result-getter-throw",
    "UnityPipeline_validation.official.runtime", "UnityPipeline_validation.official.structured",
    "UnityPipeline_validation.official.sync", "UnityPipeline_validation.official.task",
    "UnityPipeline_validation.official.throw", "UnityPipeline_validation.official.timeout",
    "UnityPipeline_validation.official.timeout-status"
  ];
  const discovered = { tools: [...baseline.tools, ...projectTools] };
  assert.deepEqual(runtimeInventoryIssues(discovered, baseline), []);
  discovered.tools.push("project.unexpected");
  assert.match(runtimeInventoryIssues(discovered, baseline).join(" "), /Unexpected non-project Tools: project\.unexpected/);
  discovered.tools = discovered.tools.filter((name) => name !== "project.unexpected" && name !== "validation.echo" && name !== "probuilder.create-shape");
  const missingIssues = runtimeInventoryIssues(discovered, baseline).join(" ");
  assert.match(missingIssues, /Missing current public Tools: probuilder\.create-shape/);
  assert.match(missingIssues, /Missing required project fixture Tools: validation\.echo/);
  discovered.tools.push("validation.echo", "probuilder.create-shape", "build.status");
  assert.match(runtimeInventoryIssues(discovered, baseline).join(" "), /Removed public Tools are discoverable: build\.status/);
});

test("frozen direct cases are a complete request-specific closed set", async () => {
  assert.deepEqual(Object.keys(DIRECT_LIVE_CASES).sort(), [...FROZEN_DIRECT_TOOL_NAMES].sort());
  assert.equal(FROZEN_DIRECT_TOOL_NAMES.length, 30);
  assert.equal(FROZEN_DIRECT_TOOL_NAMES.filter((name) => name.startsWith("probuilder.")).length, 15);
  assert.equal(capabilityFixtureId("probuilder.create-shape"), null);
  assert.equal(capabilityFixtureId("probuilder.center-pivot"), "probuilder_generic");
  assert.equal(capabilityFixtureId("probuilder.convert-to-probuilder"), "probuilder_generic");
  assert.equal(capabilityFixtureId("prefab.create"), "prefab_create");
  assert.equal(capabilityFixtureId("package.remove"), "package_remove");
  assert.equal(capabilityFixtureId("package.restore"), "package_restore");
  assert.equal(capabilityFixtureId("ui.create-panel-settings"), "ui_panel_settings_absent");
  assert.equal(capabilityFixtureId("vfx.parameter"), "vfx_parameter");
  assert.equal(capabilityFixtureId("editor.refresh"), null);

  for (const name of FROZEN_DIRECT_TOOL_NAMES) {
    const recipe = DIRECT_LIVE_CASES[name];
    assert.equal(recipe.invocation.name, name, `${name} must invoke itself`);
    assert.ok(Array.isArray(recipe.before) && recipe.before.length > 0, `${name} must capture a before state`);
    assert.ok(Array.isArray(recipe.after) && recipe.after.length > 0, `${name} must capture a fresh after state`);
    assert.ok(Array.isArray(recipe.cleanup) && recipe.cleanup.length > 0, `${name} must declare cleanup`);
    const serialized = JSON.stringify(recipe);
    assert.doesNotMatch(serialized, /probuilder_reset|data\.result|waitReady|--channel|--wait(?:-timeout-ms)?|--project|--unity/);
    const steps = [...(recipe.setup ?? []), ...(recipe.before ?? []), ...(recipe.preflight ?? []), recipe.invocation, ...(recipe.after ?? []), ...(recipe.cleanup ?? [])];
    for (const candidate of steps) {
      const probe = candidate?.input?.completionProbe;
      if (!probe) continue;
      assert.equal(typeof probe.readTool, "string", `${name} completionProbe must name readTool`);
      assert.equal(Object.hasOwn(probe, "context"), false, `${name} completionProbe must not use removed context`);
    }
    for (const setup of [...(recipe.setup ?? []), ...(recipe.before ?? []), ...(recipe.after ?? []), ...(recipe.cleanup ?? [])]) {
      assert.notEqual(setup.name, "code.exec", `${name} must not use code.exec outside its own invocation`);
    }
  }
  const source = await readFile(new URL("./run-final-live-matrix.mjs", import.meta.url), "utf8");
  assert.match(source, /suppressFrozenPlayPositive/);
  assert.match(source, /cannot use a descriptor example as PlayMode success evidence/);
  assert.match(source, /directPhaseIds\(sourceCase, recipe\)\.every/);
  assert.match(source, /recoverInterruptedDirectCase/);
  assert.match(source, /direct-recovery\.json/);
  // direct Tool 不可重放；仅最终只读 ready 汇总允许收敛重试。
  assert.match(source, /cleanupAttempted: true \}, retryAssertion: businessSucceeded && cleanupSucceeded \}, project\)/);
  assert.match(source, /const retryReadiness = meta\.retryAssertion === true[\s\S]*base\[0\] === "tools"[\s\S]*base\[1\] === "run"[\s\S]*base\[2\] === "editor\.state"/);
  assert.match(source, /if \(!issue \|\| !retryReadiness \|\| attempt === TRANSIENT_MAX_ATTEMPTS\) break/);
  assert.doesNotMatch(source, /envelope\?\.error\?\.transient !== true/);
});

test("NavMeshSurface direct case owns one saved fixture, fresh asset proof, and exact cleanup", () => {
  const recipe = DIRECT_LIVE_CASES["navmesh.surface.bake"];
  assert.deepEqual(recipe.setup.map((item) => item.name), ["go.create", "component.add", "go.create", "scene.save"]);
  assert.equal(recipe.setup[1].input.component, "Unity.AI.Navigation.NavMeshSurface");
  assert.deepEqual(recipe.before[0].flags, ["--dry-run"]);
  assert.equal(recipe.before[0].expect.equals["/data/targetCount"], 1);
  assert.equal(recipe.invocation.capture.dataPath, "/data/surfaces/0/dataPath");
  assert.deepEqual(recipe.after.map((item) => item.name), ["navmesh.bake-status", "asset.info"]);
  assert.deepEqual(recipe.cleanup.map((item) => item.name), ["go.delete", "go.delete", "asset.delete", "scene.save"]);
  assert.deepEqual(recipe.cleanup[2].when, ["dataPath"]);
  assert.ok(recipe.invariants.some((item) => item.left === "dataGuid" && item.right === "assetGuid"));
});

test("Prefab and Timeline direct cases own unique assets, fresh readback, and cleanup", () => {
  const prefabNames = ["prefab.overrides.apply", "prefab.overrides.revert", "prefab.variant.create"];
  const timelineNames = ["timeline.create", "timeline.info", "timeline.track.add", "timeline.clip.add"];
  for (const name of [...prefabNames, ...timelineNames]) {
    const recipe = DIRECT_LIVE_CASES[name];
    assert.equal(recipe.invocation.name, name);
    const assetCleanup = recipe.cleanup.find((item) => item.name === "asset.delete");
    assert.ok(assetCleanup, `${name} must delete its owned asset`);
    assert.ok(assetCleanup.acceptErrorCodes.includes("E_ASSET_NOT_FOUND"));
  }
  const apply = DIRECT_LIVE_CASES["prefab.overrides.apply"];
  assert.deepEqual(apply.setup.map((item) => item.name), ["asset.duplicate", "prefab.instantiate", "go.create"]);
  assert.deepEqual(apply.after.map((item) => item.name), ["prefab.instantiate", "scene.gameobject"]);
  assert.ok(apply.cleanup[1].acceptErrorCodes.includes("E_TARGET_NOT_FOUND"));
  assert.equal(apply.before[0].expect.equals["/data/changed"], true);
  assert.ok(apply.invariants.some((item) => item.operator === "notEquals"));
  const revert = DIRECT_LIVE_CASES["prefab.overrides.revert"];
  assert.equal(revert.after[0].expectedErrorCode, "E_TARGET_NOT_FOUND");
  const variant = DIRECT_LIVE_CASES["prefab.variant.create"];
  assert.equal(variant.before[0].expectedErrorCode, "E_ASSET_NOT_FOUND");
  assert.equal(DIRECT_LIVE_CASES["timeline.create"].before[0].expectedErrorCode, "E_ASSET_NOT_FOUND");
  const clip = DIRECT_LIVE_CASES["timeline.clip.add"];
  assert.deepEqual(clip.invocation.input.trackLocalId, { $var: "trackLocalId" });
  assert.equal(clip.after[0].name, "timeline.info");
});

test("each ProBuilder direct case owns a distinct geometry recipe", () => {
  const names = FROZEN_DIRECT_TOOL_NAMES.filter((name) => name.startsWith("probuilder."));
  for (const name of names) {
    const recipe = DIRECT_LIVE_CASES[name];
    const setupTools = (recipe.setup ?? []).map((item) => item.name);
    if (name !== "probuilder.create-shape") assert.ok(setupTools.some((tool) => tool === "probuilder.create-poly-shape" || tool === "probuilder.create-shape"), `${name} must construct its own geometry`);
    assert.ok(recipe.after.some((item) => item.name === "probuilder.mesh-info"), `${name} must include fresh public mesh readback`);
  }
  assert.equal(DIRECT_LIVE_CASES["probuilder.append-vertices-to-edge"].setup[0].name, "probuilder.create-shape");
  assert.equal(DIRECT_LIVE_CASES["probuilder.append-vertices-to-edge"].setup[0].input.shapeType, "Cube");
  assert.deepEqual(DIRECT_LIVE_CASES["probuilder.append-vertices-to-edge"].invocation.input.edgeIndices, [0]);
  assert.deepEqual(DIRECT_LIVE_CASES["probuilder.bevel-edges"].invocation.input.edges, [{ a: 1, b: 2 }]);
  assert.deepEqual(DIRECT_LIVE_CASES["probuilder.bridge-edges"].invocation.input.edgeA, { a: 0, b: 1 });
  assert.deepEqual(DIRECT_LIVE_CASES["probuilder.insert-vertex"].invocation.input.edge, { a: 1, b: 2 });
  assert.equal(DIRECT_LIVE_CASES["probuilder.insert-vertex"].invocation.input.x, 0.25);
  assert.equal(DIRECT_LIVE_CASES["probuilder.insert-vertex"].invocation.input.z, 0.25);
  assert.equal(DIRECT_LIVE_CASES["probuilder.create-shape"].invocation.input.shapeType, "CurvedStair");
  assert.deepEqual(DIRECT_LIVE_CASES["probuilder.merge-vertices"].invocation.input.vertexIndices, [0, 1]);
  assert.deepEqual(DIRECT_LIVE_CASES["probuilder.weld-vertices"].invocation.input.vertexIndices, [0, 1]);
  assert.equal(DIRECT_LIVE_CASES["probuilder.create-shape"].before[0].expectedErrorCode, "E_TARGET_NOT_FOUND");
  assert.equal(DIRECT_LIVE_CASES["probuilder.merge-objects"].after.find((item) => item.id === "source-removed").expectedErrorCode, "E_TARGET_NOT_FOUND");
});

test("active fixtures use only current public execution semantics", async () => {
  const fixtureUrl = new URL("./live-matrix/fixtures/live-fixtures.json", import.meta.url);
  const fixtures = JSON.parse(await readFile(fixtureUrl, "utf8"));
  const serialized = JSON.stringify(fixtures);
  const capabilitySetup = await readFile(new URL("./live-matrix/fixtures/live-capability-setup.cs", import.meta.url), "utf8");
  assert.match(capabilitySetup, /profiler-snapshots/);
  assert.match(capabilitySetup, /WriteAllBytes\(System\.IO\.Path\.Combine\(projectRoot, "snap1\.snap"\)/);
  assert.match(capabilitySetup, /WriteAllBytes\(System\.IO\.Path\.Combine\(projectRoot, "snap2\.snap"\)/);
  assert.doesNotMatch(serialized, /\.plan[\\/]evidence|public-output-modal|final-cleanup|data\.result|waitReady|--(?:channel|unity|wait|wait-timeout-ms|bridge-endpoint)|\"closed_job\"|package-unembed/);
  for (const name of [
    "live-matrix-setup.cs",
    "live-capability-setup.cs",
    "live-pipeline-setup.cs",
    "live-playmode-options.cs",
    "live-domain-reload-setup.cs"
  ]) assert.match(serialized, new RegExp(`scripts/live-matrix/fixtures/${name.replace(".", "\\.")}`));
  assert.equal(fixtures.setup.steps[0].argv[2], "profiler.stop");
  assert.equal(fixtures.reset.steps[0].argv[2], "profiler.stop");
  assert.deepEqual(fixtures.concurrency.assert, { minOverlapMs: 1 });
  assert.equal(fixtures.concurrency.calls.every((item) => item.argv[2] === "validation.wait"), true);
  const setupSceneLoad = fixtures.setup.steps.find((step) => step.id === "load-baseline-scene");
  const resetSceneLoad = fixtures.reset.steps.find((step) => step.id === "load-baseline-scene");
  assert.equal(setupSceneLoad.argv[2], "scene.load");
  assert.equal(resetSceneLoad.argv[2], "scene.load");
  assert.match(setupSceneLoad.argv[4], /MatrixScene\.unity/);
  assert.deepEqual(fixtures.profiler_hierarchy_capture.steps.map((step) => step.id), ["start", "verify"]);
  assert.equal(fixtures.profiler_hierarchy_capture.steps.some((step) => step.argv[2] === "profiler.capture"), false);
  assert.deepEqual(fixtures.profiler_snapshots.steps.map((step) => step.id), ["write-inputs", "restore-proof"]);
  assert.equal(fixtures.profiler_snapshots.steps.some((step) => step.argv[2] === "profiler.memory-snapshot.take"), false);
  for (const name of [
    "cinemachine.aim", "cinemachine.extension.add", "scene.create", "scene.move-object", "scene.set-active", "scene.unload",
    "prefab.create", "prefab.unpack", "prefab.stage-close", "profiler.load", "profiler.hierarchy", "profiler.frame-debugger.disable", "profiler.memory-snapshot.compare",
    "package.remove", "package.restore", "package.scoped-registry.remove", "ui.document.create", "ui.detach-document", "ui.modify-visual-element",
    "scriptable-object.create", "scriptable-object.modify", "vfx.line-create", "vfx.template.create"
  ]) {
    const fixtureId = capabilityFixtureId(name);
    assert.ok(fixtureId && fixtures[fixtureId], `${name} requires active fixture ${fixtureId}`);
  }
  for (const fixture of Object.values(fixtures)) {
    const calls = [...(fixture.steps ?? []), ...(fixture.calls ?? [])];
    for (const call of calls) {
      const argv = call.argv;
      const inputIndex = argv.indexOf("--input");
      const input = inputIndex < 0 ? {} : JSON.parse(argv[inputIndex + 1].replace(/\$\{[A-Za-z][A-Za-z0-9_]*\}/g, "0"));
      if (argv[0] === "tools" && argv[2] === "code.exec") {
        assert.ok(input.completionProbe, `${call.id ?? argv[2]} code.exec must declare completionProbe`);
        assert.equal(input.completionProbe.readTool, "scene.gameobject");
      }
      if (argv[0] === "tools" && argv[1] === "run" && argv[2] === "editor.state") assert.deepEqual(input, {});
    }
  }
  for (const path of [
    "./live-matrix/fixtures/live-matrix-setup.cs",
    "./live-matrix/fixtures/live-capability-setup.cs",
    "./live-matrix/fixtures/live-pipeline-setup.cs",
    "./live-matrix/fixtures/live-playmode-options.cs",
    "./live-matrix/fixtures/live-domain-reload-setup.cs"
  ]) assert.doesNotMatch(await readFile(new URL(path, import.meta.url), "utf8"), /ctx\.Request/);
  const resetSource = await readFile(new URL("./live-matrix/fixtures/live-matrix-setup.cs", import.meta.url), "utf8");
  assert.doesNotMatch(resetSource, /NewSceneMode\.Single/);
  assert.doesNotMatch(resetSource, /AssetDatabase\.Refresh/);
  assert.match(resetSource, /requires the fixed MatrixScene baseline/);
});

test("validation caches are deterministic current source fixtures", async () => {
  const [docs, reflect] = await Promise.all([
    readFile(new URL("./live-matrix/fixtures/validation-cache/docs-index.json", import.meta.url), "utf8").then(JSON.parse),
    readFile(new URL("./live-matrix/fixtures/validation-cache/reflect-index.json", import.meta.url), "utf8").then(JSON.parse)
  ]);
  assert.equal(docs.unityVersion, "2022.3");
  assert.deepEqual(docs.entries.map((entry) => entry.name), ["UnityEngine.Camera", "UnityEngine.Camera.Render"]);
  assert.equal(reflect.unityVersion, "2022.3");
  assert.equal(reflect.entries.length, 1);
  assert.equal(reflect.entries[0].fullName, "UnityEngine.Camera");
  assert.deepEqual(reflect.entries[0].members.map((member) => member.name), ["Render"]);
  assert.deepEqual(reflect.assemblies, [{ name: "UnityEngine.CoreModule", status: "loaded" }]);
});

test("global matrix setup and final recovery are process-local", async () => {
  const source = await readFile(new URL("./run-final-live-matrix.mjs", import.meta.url), "utf8");
  const editMode = source.indexOf('setCasePlayMode(m, "initial-editor", false)');
  const setup = source.indexOf('runRequiredFixtureSequence(m, "setup"');
  const initialRestore = source.indexOf('runRequiredFixtureSequence(m, "playmode-initial-restore"');
  assert.ok(editMode >= 0 && editMode < setup && setup < initialRestore);
  assert.match(source, /control\.play-baseline\.\$\{caseId\}\.\$\{playing \? "enter-play" : "enter-edit"\}\.current/);
  assert.doesNotMatch(source, /control\.play-baseline\.\$\{caseId\}\.current`/);
  assert.match(source, /current\.envelope\.data\.state\.playMode\.value === \(playing \? "play" : "edit"\)/);
  assert.match(source, /control\.final-reset\./);
  assert.match(source, /pipeline-final-restore/);
  assert.doesNotMatch(source, /package-final-unembed/);
});

test("undo redo and animator recipes avoid reload-prone or externally driven targets", () => {
  const undo = DIRECT_LIVE_CASES["editor.undo"];
  const redo = DIRECT_LIVE_CASES["editor.redo"];
  const animator = DIRECT_LIVE_CASES["animator.crossfade"];
  assert.deepEqual(undo.setup.map((item) => item.name), ["go.create", "go.modify"]);
  assert.equal(undo.setup[0].input.name, "${target}");
  assert.equal(undo.setup[1].input.target, "${target}");
  assert.deepEqual(redo.setup.map((item) => item.name), ["go.create", "go.modify", "editor.undo"]);
  assert.equal(redo.setup[0].input.name, "${target}");
  assert.equal(redo.setup[1].input.target, "${target}");
  assert.equal(redo.setup[2].input.completionProbe.input.target, "${target}");
  assert.deepEqual(undo.cleanup.map((item) => item.name), ["go.delete"]);
  assert.deepEqual(redo.cleanup.map((item) => item.name), ["go.delete"]);
  assert.deepEqual(animator.before.map((item) => item.name), ["scene.gameobject", "animation.state"]);
  assert.equal(animator.before[0].capture.beforeTargetId, "/data/gameObject/id");
  assert.equal(animator.before[1].capture.beforeCurrentStateHash, "/data/animator/currentState/fullPathHash");
  assert.equal(animator.invocation.capture.invocationCurrentStateHash, "/data/state/currentStateHash");
  assert.deepEqual(animator.after.map((item) => item.name), ["scene.gameobject", "animation.state"]);
  assert.equal(animator.after[1].capture.afterCurrentStateHash, "/data/animator/currentState/fullPathHash");
  assert.deepEqual(animator.cleanup.map((item) => item.name), ["go.delete"]);
  assert.ok(!animator.cleanup.some((item) => item.name === "asset.delete"));
});

test("Package matrix cases declare terminal-only exact cleanup", async () => {
  const source = await readFile(new URL("./run-final-live-matrix.mjs", import.meta.url), "utf8");
  assert.match(source, /record\.assertion\?\.status !== "PASS" \|\| record\.envelope\?\.ok !== true \|\| flags\.includes\("--dry-run"\)/);
  assert.match(source, /if \(name === "package\.embed"\)[\s\S]*ownedEmbeddedPackageCleanupTarget[\s\S]*package\.resolve[\s\S]*package\.info/);
  for (const pair of [["package.deploy", "package.restore"], ["package.install", "package.remove"], ["package.registry.set", "package.registry.clear"], ["package.scoped-registry.add", "package.scoped-registry.remove"]]) {
    assert.match(source, new RegExp(`"${pair[0].replaceAll(".", "\\.")}"[\\s\\S]*name: "${pair[1].replaceAll(".", "\\.")}"`));
  }
});

test("pipeline fixture materializes scene-session Volume profiles", async () => {
  const source = await readFile(new URL("./live-matrix/fixtures/live-pipeline-setup.cs", import.meta.url), "utf8");
  assert.match(source, /GetProperty\("profile", System\.Reflection\.BindingFlags\.Public \| System\.Reflection\.BindingFlags\.Instance\)/);
  assert.match(source, /GetField\("m_InternalProfile", System\.Reflection\.BindingFlags\.NonPublic \| System\.Reflection\.BindingFlags\.Instance\)/);
  assert.match(source, /ReferenceEquals\(internalProfileField\.GetValue\(volume\), sessionProfile\)/);
});

test("physics direct case fixes and restores every global prerequisite", () => {
  const recipe = DIRECT_LIVE_CASES["physics.simulation"];
  const readSettings = recipe.setup.find((item) => item.id === "read-settings");
  const manualSettings = recipe.setup.find((item) => item.id === "manual-settings");
  const restoreSettings = recipe.cleanup.find((item) => item.id === "restore-settings");
  assert.equal(readSettings.flags, undefined);
  assert.equal(readSettings.capture.priorGravity, "/data/physics/gravity");
  assert.deepEqual(manualSettings.input.gravity, { x: 0, y: -9.81, z: 0 });
  assert.equal(manualSettings.input.simulationMode2D, "Script");
  assert.equal(recipe.setup.find((item) => item.id === "create-target").input.y, 5);
  assert.equal(recipe.invocation.input.completionProbe.assertions[0].before, 5);
  assert.equal(recipe.invocation.input.completionProbe.assertions[0].after, 4.996076);
  assert.deepEqual(restoreSettings.input.gravity, { $var: "priorGravity" });
  assert.ok(restoreSettings.when.includes("priorGravity"));
});

test("direct case templates and captures preserve JSON types", () => {
  const variables = { count: 3, enabled: false, payload: { x: 1 }, name: "Target" };
  assert.deepEqual(resolveCaseTemplate({ count: { $var: "count" }, enabled: "${enabled}", payload: { $var: "payload" }, label: "id-${name}" }, variables), {
    count: 3, enabled: false, payload: { x: 1 }, label: "id-Target"
  });
  const envelope = { ok: true, data: { count: 3, nested: { enabled: false } } };
  assert.equal(readDirectCapture(envelope, "/data/count"), 3);
  assert.equal(readDirectCapture(envelope, "/data/nested/enabled"), false);
  assert.throws(() => readDirectCapture(envelope, "/data/result/count"), /Legacy \/data\/result capture is forbidden/);
  assert.throws(() => readDirectCapture(envelope, "data.count"), /absolute \/data JSON Pointer/);
});

test("direct invariant comparisons cover identity, topology and finite numerics", () => {
  const variables = { before: 3, after: 4, left: 1, right: 2, actual: 0.996076, objectA: { x: 1 }, objectB: { x: 1 }, hashA: "a", hashB: "b" };
  assert.equal(directInvariantIssue([
    { left: "after", operator: "deltaEquals", right: "before", value: 1 },
    { left: "after", operator: "sumEquals", operands: ["left", "right", "left"] },
    { left: "actual", operator: "approx", value: 0.996, tolerance: 0.001 },
    { left: "hashA", operator: "notEquals", right: "hashB" },
    { left: "objectA", operator: "deepEquals", right: "objectB" }
  ], variables), null);
  assert.match(directInvariantIssue([{ left: "before", operator: "greaterThan", right: "after" }], variables), /Direct invariant failed/);
});

test("terminal capability failure stops later coverage after owned cleanup", async () => {
  const source = await readFile(new URL("./run-final-live-matrix.mjs", import.meta.url), "utf8");
  assert.match(source, /baselineFailure = await m\.failed\([\s\S]*stopAfterTerminalFailure\(baselineFailure\)/);
  assert.match(source, /const editorPlayCleanup = await cleanupEditorPlay\([\s\S]*if \(editorPlayCleanup\) throw new MatrixTerminalFailure\(editorPlayCleanup\)[\s\S]*stopAfterTerminalFailure\(record\)/);
  assert.match(source, /finally \{[\s\S]*runDirectCasePhase\(m, sourceCase, "cleanup"[\s\S]*stopAfterTerminalFailure\(record\)/);
  assert.match(source, /catch \(error\) \{[\s\S]*error instanceof MatrixTerminalFailure[\s\S]*control\.runner\.exception/);
  assert.match(source, /error instanceof MatrixTerminalFailure[\s\S]*await recoverFinalMatrixState\(m\)[\s\S]*await report\(m\)/);
});

test("cleanup phase continues after an earlier cleanup step fails", async () => {
  const calls = [];
  const matrix = {
    call: async (id) => {
      calls.push(id);
      return { assertion: { status: id.endsWith("delete-target") ? "FAIL" : "PASS" }, envelope: { ok: true, data: {} } };
    },
    directRecovery: new Map(),
    failed: async () => ({ assertion: { status: "FAIL" } })
  };
  const definitions = [
    { id: "delete-target", kind: "tool", name: "go.delete", input: { target: "Target" } },
    { id: "restore-settings", kind: "tool", name: "physics.settings", input: { autoSimulation: true } }
  ];

  assert.equal(await runDirectCasePhase(matrix, "tool.physics.simulation.direct", "cleanup", definitions, {}, "D:/Project"), false);
  assert.deepEqual(calls, [
    "tool.physics.simulation.direct.cleanup.delete-target",
    "tool.physics.simulation.direct.cleanup.restore-settings"
  ]);
});

test("resume rejects a parent pass without complete phase and cleanup evidence", () => {
  const sourceCase = "tool.sample.direct";
  const recipe = {
    setup: [{ id: "setup" }],
    before: [{ id: "before" }],
    invocation: { id: "invoke" },
    after: [{ id: "after" }],
    cleanup: [{ id: "cleanup" }]
  };
  const recipeHash = "recipe-hash";
  const passed = new Set([sourceCase, ...directPhaseIds(sourceCase, recipe).filter((id) => !id.endsWith(".cleanup.cleanup"))]);
  const matrix = {
    prior: new Map([[sourceCase, { directCase: { recipeHash } }]]),
    directRecovery: new Map([[sourceCase, { status: "complete", recipeHash }]]),
    hasReusablePass: (id) => passed.has(id)
  };

  assert.equal(hasReusableDirectCase(matrix, sourceCase, recipe, recipeHash, "D:/Project"), false);
  passed.add(`${sourceCase}.cleanup.cleanup`);
  assert.equal(hasReusableDirectCase(matrix, sourceCase, recipe, recipeHash, "D:/Project"), true);
});

test("concurrency overlap requires measured child process intervals", () => {
  const left = { process: { startedAtMs: 100, endedAtMs: 300 } };
  const right = { process: { startedAtMs: 200, endedAtMs: 400 } };
  assert.equal(concurrencyOverlapMs(left, right, 9999), 100);
  assert.equal(concurrencyOverlapMs(left, { process: {} }, 9999), 0);
  assert.equal(concurrencyOverlapMs(left, { process: { startedAtMs: 400, endedAtMs: 500 } }, 9999), 0);
});

test("fixture cleanup accepts only declared stable error codes", () => {
  const missing = { ok: false, error: { code: "E_ASSET_NOT_FOUND" } };
  assert.equal(fixtureExpectationIssue(missing, { exitCode: 1 }, { acceptErrorCodes: ["E_ASSET_NOT_FOUND"] }), null);
  assert.match(fixtureExpectationIssue(missing, { exitCode: 1 }, { acceptErrorCodes: ["E_TARGET_NOT_FOUND"] }), /Fixture exited/);
  assert.equal(fixtureExpectationIssue({ ok: true, requestId: "req_fixture", data: {} }, { exitCode: 0 }, { acceptErrorCodes: ["E_ASSET_NOT_FOUND"] }), null);
});

test("concurrency fixture selects exactly one proof mode", () => {
  const calls = [{ argv: [], expect: {} }, { argv: [], expect: {} }];
  assert.equal(fixtureError({ assert: { serial: true }, calls }, "concurrency"), null);
  assert.equal(fixtureError({ assert: { minOverlapMs: 1 }, calls }, "concurrency"), null);
  assert.match(fixtureError({ assert: {}, calls }, "concurrency"), /exactly one/);
  assert.match(fixtureError({ assert: { serial: true, minOverlapMs: 1 }, calls }, "concurrency"), /exactly one/);
  assert.match(fixtureError({ assert: { minOverlapMs: 0 }, calls }, "concurrency"), /exactly one/);
});

test("concurrency serial proof accepts only disjoint execution intervals", () => {
  const sequential = concurrencySerialProof(
    { process: { startedAtMs: 100, endedAtMs: 300 } },
    { process: { startedAtMs: 300, endedAtMs: 500 } }
  );
  assert.equal(sequential.serial, true);
  const reversed = concurrencySerialProof(
    { process: { startedAtMs: 400, endedAtMs: 500 } },
    { process: { startedAtMs: 100, endedAtMs: 400 } }
  );
  assert.equal(reversed.serial, true);
  const overlapping = concurrencySerialProof(
    { process: { startedAtMs: 100, endedAtMs: 300 } },
    { process: { startedAtMs: 200, endedAtMs: 400 } }
  );
  assert.equal(overlapping.serial, false);
  const missing = concurrencySerialProof({ process: {} }, { process: { startedAtMs: 1, endedAtMs: 2 } });
  assert.equal(missing.serial, false);
});

test("final recovery proves ready EditMode before EditMode-only recovery fixtures", async () => {
  const source = await readFile(new URL("./run-final-live-matrix.mjs", import.meta.url), "utf8");
  // 终态失败可能发生在 PlayMode case 之后；pipeline/reset/scene.load fixture 只在 EditMode 有效。
  const recovery = source.indexOf("async function recoverFinalMatrixState");
  const enterEdit = source.indexOf('setCasePlayMode(m, "recovery-enter-edit", false)', recovery);
  const pipeline = source.indexOf('runFixtureSequence(m, "pipeline-final-restore"', recovery);
  assert.ok(recovery >= 0 && enterEdit > recovery && pipeline > enterEdit, "recovery must re-enter EditMode before any recovery fixture");
});

test("playmode unavailable code overrides cover exactly editor.compile's contracted code", async () => {
  assert.equal(PLAYMODE_UNAVAILABLE_CODE_OVERRIDES.size, 1);
  assert.equal(PLAYMODE_UNAVAILABLE_CODE_OVERRIDES.get("editor.compile"), "E_UNITY_COMPILE_REQUIRES_EDIT_MODE");
  const source = await readFile(new URL("./run-final-live-matrix.mjs", import.meta.url), "utf8");
  assert.match(source, /phase === "play"[\s\S]*"E_RUNTIME_NOT_RUNNING"/);
  assert.doesNotMatch(source, /E_RUNTIME_TOOL_TARGET_MISMATCH/);
});

test("playmode resolve blocked tools exactly cover package file mutations", () => {
  assert.deepEqual([...PLAYMODE_RESOLVE_BLOCKED_TOOLS].sort(), [
    "package.deploy", "package.embed", "package.install", "package.remove", "package.resolve", "package.restore",
    "package.scoped-registry.add", "package.scoped-registry.remove"
  ]);
  // 只读 package 能力不得被误封：list/info/search 在 PlayMode 下仍可调用。
  for (const name of ["package.list", "package.info", "package.search", "package.search-remote", "package.bridge-remove"]) {
    assert.equal(PLAYMODE_RESOLVE_BLOCKED_TOOLS.has(name), false, name);
  }
});

test("journal lifecycle forbids request archives and clears terminal active state", async () => {
  const project = await mkdtemp(join(tmpdir(), "ucli-active-lifecycle-"));
  try {
    assert.equal(await journalLifecycleIssue(project, ["tools", "run", "go.create"], { requestId: "req_clean" }), null);
    await mkdir(join(project, "Library", "UnityCli", "requests"), { recursive: true });
    await writeFile(join(project, "Library", "UnityCli", "requests", "req_archived.json"), "{}");
    assert.match(await journalLifecycleIssue(project, ["tools", "run", "go.create"], { requestId: "req_archived" }), /Request-level journal/);
    await mkdir(join(project, "Library", "UnityCli"), { recursive: true });
    await writeFile(join(project, "Library", "UnityCli", "active-compile.json"), "{}");
    assert.match(await journalLifecycleIssue(project, ["tools", "run", "editor.compile"], { requestId: "req_compile" }), /active operation was not cleared/);
    await rm(join(project, "Library", "UnityCli", "active-compile.json"));
    assert.equal(await journalLifecycleIssue(project, ["tools", "run", "editor.compile"], { requestId: "req_compile" }), null);
  } finally { await rm(project, { recursive: true, force: true }); }
});

test("runCli records process times and terminates a timed-out direct call", async () => {
  const success = await runCli(process.execPath, ["-e", "process.stdout.write(JSON.stringify({ok:true,data:{value:1}}))"], 5000);
  assert.equal(success.exitCode, 0);
  assert.equal(success.timedOut, false);
  assert.ok(success.endedAtMs >= success.startedAtMs);
  assert.equal(success.durationMs, success.endedAtMs - success.startedAtMs);

  const timeout = await runCli(process.execPath, ["-e", "setTimeout(() => {}, 3000)"], 50);
  assert.equal(timeout.timedOut, true);
  assert.ok(timeout.endedAtMs >= timeout.startedAtMs);
});

test("runner lock rejects concurrent ownership and can be reacquired", async () => {
  const directory = await mkdtemp(join(tmpdir(), "unity-cli-matrix-lock-"));
  try {
    const release = await acquireRunnerLock(directory);
    await assert.rejects(() => acquireRunnerLock(directory), /E_MATRIX_RUNNER_ACTIVE/);
    await release();
    const releaseAgain = await acquireRunnerLock(directory);
    await releaseAgain();
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("project source lock rejects a second matrix owner", async () => {
  const owner = await mkdtemp(join(tmpdir(), "unity-cli-project-lock-"));
  const project = join(owner, "project");
  const evidence = join(owner, "evidence");
  try {
    await createOwnedProject(project);
    const release = await acquireProjectSourceLock(project, evidence);
    await assert.rejects(() => acquireProjectSourceLock(project, evidence), /E_MATRIX_PROJECT_ACTIVE/);
    await release();
    const releaseAgain = await acquireProjectSourceLock(project, evidence);
    await releaseAgain();
  } finally {
    await rm(owner, { recursive: true, force: true });
  }
});

test("owned project snapshot restores exact bytes and absence closure", async () => {
  const owner = await mkdtemp(join(tmpdir(), "unity-cli-project-snapshot-"));
  const project = join(owner, "project");
  const evidence = join(owner, "evidence");
  try {
    await createOwnedProject(project);
    const baselineScene = await readFile(join(project, "Assets", "Scenes", "Main.unity"));
    let snapshot = await prepareOwnedProjectSnapshot(project, evidence, "stamp-a");
    assert.equal(snapshot.document.status, "prepared");
    assert.ok(snapshot.document.fileCount >= 5);

    await writeFile(join(project, "Assets", "Scenes", "Main.unity"), "changed-scene\n");
    await rm(join(project, "ProjectSettings", "ProjectVersion.txt"));
    await mkdir(join(project, "Assets", "Generated"), { recursive: true });
    await writeFile(join(project, "Assets", "Generated", "owned.txt"), "created\n");
    await writeFile(join(project, "Packages", "manifest.json"), "{\"changed\":true}\n");
    await writeFile(join(project, "Packages", "unitycli.install.json"), "{\"unexpected\":true}\n");

    snapshot = await restoreOwnedProjectSnapshot(snapshot);
    assert.equal(snapshot.document.status, "restored");
    assert.equal(snapshot.document.packageStateChanged, true);
    assert.deepEqual(await readFile(join(project, "Assets", "Scenes", "Main.unity")), baselineScene);
    await assert.rejects(() => readFile(join(project, "Assets", "Generated", "owned.txt")), (error) => error?.code === "ENOENT");
    await assert.rejects(() => readFile(join(project, "Packages", "unitycli.install.json")), (error) => error?.code === "ENOENT");
    assert.equal((await verifyOwnedProjectSnapshotClosure(snapshot)).ok, true);

    snapshot = await markOwnedProjectSnapshotReconciled(snapshot, { packageResolve: "PASS", editorRefresh: "PASS" });
    snapshot = await markOwnedProjectSnapshotClean(snapshot);
    assert.equal(snapshot.document.status, "clean");
    assert.equal(JSON.parse(await readFile(join(evidence, "owned-project-snapshot", "manifest.json"), "utf8")).status, "clean");
    await assert.rejects(() => prepareOwnedProjectSnapshot(project, evidence, "stamp-a", { allowExisting: true }), /must never be resumed or reused/);
  } finally {
    await rm(owner, { recursive: true, force: true });
  }
});

test("owned project snapshot verifies every backup before mutation", async () => {
  const owner = await mkdtemp(join(tmpdir(), "unity-cli-project-backup-"));
  const project = join(owner, "project");
  const evidence = join(owner, "evidence");
  try {
    await createOwnedProject(project);
    const snapshot = await prepareOwnedProjectSnapshot(project, evidence, "stamp-a");
    const scene = snapshot.document.files.find((file) => file.path === "Assets/Scenes/Main.unity");
    await writeFile(join(snapshot.snapshotRoot, ...scene.backupPath.split("/")), "tampered\n");
    await writeFile(join(project, "Assets", "Scenes", "Main.unity"), "user-current\n");
    await assert.rejects(() => restoreOwnedProjectSnapshot(snapshot), /backup digest changed/);
    assert.equal(await readFile(join(project, "Assets", "Scenes", "Main.unity"), "utf8"), "user-current\n");
  } finally {
    await rm(owner, { recursive: true, force: true });
  }
});

test("owned project snapshot rejects a tampered manifest before deletion", async () => {
  const owner = await mkdtemp(join(tmpdir(), "unity-cli-project-manifest-"));
  const project = join(owner, "project");
  const evidence = join(owner, "evidence");
  const outside = join(owner, "outside.txt");
  try {
    await createOwnedProject(project);
    const snapshot = await prepareOwnedProjectSnapshot(project, evidence, "stamp-a");
    const manifestPath = join(evidence, "owned-project-snapshot", "manifest.json");
    const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
    manifest.files[0].path = "../outside.txt";
    manifest.files[0].backupPath = "files/../outside.txt";
    await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    await writeFile(outside, "preserve\n");
    await assert.rejects(() => restoreOwnedProjectSnapshot(snapshot), /metadata is invalid|path is invalid/);
    assert.equal(await readFile(outside, "utf8"), "preserve\n");
  } finally {
    await rm(owner, { recursive: true, force: true });
  }
});

test("owned project snapshot rejects reparse paths", async (context) => {
  if (process.platform !== "win32") context.skip("Junction semantics are Windows-specific.");
  const owner = await mkdtemp(join(tmpdir(), "unity-cli-project-reparse-"));
  const project = join(owner, "project");
  const evidence = join(owner, "evidence");
  const outside = join(owner, "outside");
  try {
    await createOwnedProject(project);
    await mkdir(outside, { recursive: true });
    await symlink(outside, join(project, "Assets", "Linked"), "junction");
    await assert.rejects(() => prepareOwnedProjectSnapshot(project, evidence, "stamp-a"), /symbolic link or junction/);
  } finally {
    await rm(owner, { recursive: true, force: true });
  }
});
