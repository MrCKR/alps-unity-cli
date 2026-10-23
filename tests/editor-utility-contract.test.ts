import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path: string): string => readFileSync(path, "utf8");
const scheduler = read("unitypkg/Editor/Infrastructure/UnityCliWaiter.cs");
const autotick = read("unitypkg/Editor/Tools/editor/autotick-set.cs");
const focus = read("unitypkg/Editor/Tools/editor/focus.cs");
const focusSupport = read("unitypkg/Editor/Tools/editor/EditorWindowFocusToolSupport.cs");
const search = read("unitypkg/Editor/Tools/editor/search.cs");
const searchSupport = read("unitypkg/Editor/Tools/editor/EditorSearchToolSupport.cs");
const capture = read("unitypkg/Editor/Tools/screenshot/editor-element.cs");
const captureSupport = read("unitypkg/Editor/Tools/screenshot/EditorElementCaptureToolSupport.cs");
const registry = read("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs");
const cancel = read("src/tools/test/cancel.ts");
const testCoordinator = read("unitypkg/Editor/Infrastructure/UnityCliTestRunCoordinator.cs");
const completion = JSON.parse(read("contracts/tool-completion-contract.json")) as { tools: Array<{ toolName: string }> };

test("editor.autotick-set只通过中央scheduler持有session状态", () => {
  assert.match(autotick, /Name = "editor\.autotick-set"/);
  assert.match(autotick, /SupportsDryRun = true/);
  assert.match(autotick, /SideEffects = new\[\] \{ "editor-tick-rate" \}/);
  assert.equal((autotick.match(/ctx\.CommitEffect\(\);/g) ?? []).length, 1);
  assert.match(autotick, /UnityCliWaitScheduler\.PrepareAutoTick/);
  assert.match(autotick, /UnityCliWaitScheduler\.ConfigureAutoTick/);
  assert.match(autotick, /UnityCliWaitScheduler\.ReadAutoTickState/);
  assert.doesNotMatch(autotick, /EditorApplication\.update|File\.|EditorPrefs|SessionState/);
  assert.equal(completion.tools.filter((entry) => entry.toolName === "editor.autotick-set").length, 1);
});

test("autotick由唯一Bridge主循环推进并在reload时清空", () => {
  assert.match(scheduler, /TickCoordinator\("editor\.autotick", PumpAutoTick\)/);
  assert.equal((scheduler.match(/PumpAutoTick\);/g) ?? []).length, 1);
  assert.match(scheduler, /AutoTickSignal\(\);[\s\S]*AutoTickForcedCount \+= 1L/);
  assert.match(scheduler, /SignalTick/);
  assert.match(scheduler, /ResetAutoTick\(\);/);
  assert.match(scheduler, /AutoTickForcedCount = 0L/);
  assert.doesNotMatch(scheduler, /EditorPrefs|SessionState|File\.Write|active-autotick/);
});

test("autotick输入范围、no-op和dry-run都在effect前闭合", () => {
  assert.match(autotick, /interval < 0 \|\| interval > 60000/);
  assert.match(autotick, /E_EDITOR_AUTOTICK_INTERVAL_INVALID/);
  assert.ok(autotick.indexOf("PrepareAutoTick(enable)") < autotick.indexOf("if (ctx.DryRun || !changed)"));
  assert.ok(autotick.indexOf("if (ctx.DryRun || !changed)") < autotick.indexOf("ctx.CommitEffect();"));
  assert.match(autotick, /EditorSessionId/);
  assert.match(autotick, /ForcedTickCount/);
});

test("editor.focus冻结selected process HWND并以native foreground证明", () => {
  assert.match(focus, /Name = "editor\.focus"/);
  assert.equal((focus.match(/ctx\.CommitEffect\(\);/g) ?? []).length, 1);
  assert.ok(focus.indexOf("Prepare()") < focus.indexOf("ctx.CommitEffect();"));
  assert.ok(focus.indexOf("ctx.CommitEffect();") < focus.indexOf("Apply(plan)"));
  assert.match(focus, /ctx\.Wait\.UntilStable/);
  assert.match(focusSupport, /Process\.GetCurrentProcess\(\)\.Id/);
  assert.match(focusSupport, /GetWindowThreadProcessId/);
  assert.match(focusSupport, /GetForegroundWindow\(\) == plan\.Window/);
  assert.match(focusSupport, /SetForegroundWindow\(plan\.Window\)/);
  assert.match(focusSupport, /AttachThreadInput\(currentThread, foregroundThread, true\)/);
  assert.match(focusSupport, /if \(attached\) AttachThreadInput\(currentThread, foregroundThread, false\)/);
  assert.match(focusSupport, /SetFocus\(plan\.Window\)/);
  assert.doesNotMatch(focusSupport, /FindWindow\(|Process\.GetProcesses/);
});

test("editor.search只使用Unity Search provider并保持scan/page有界", () => {
  assert.match(search, /Name = "editor\.search"/);
  assert.match(searchSupport, /SearchService\.GetActiveProviders/);
  assert.match(searchSupport, /SearchService\.CreateContext\(requested, request\.Query, SearchFlags\.Synchronous\)/);
  assert.match(searchSupport, /SearchService\.Request\(context, SearchFlags\.Synchronous\)/);
  assert.match(searchSupport, /MaxProviders = 16/);
  assert.match(searchSupport, /MaxOffset = 10000/);
  assert.match(searchSupport, /MaxLimit = 200/);
  assert.match(searchSupport, /results\.Count >= limit/);
  assert.doesNotMatch(searchSupport, /asset\.search|go\.find|CommitEffect/);
});

test("screenshot.editor-element由Unity 6000.7 availability和artifact ceiling双重约束", () => {
  assert.match(capture, /Name = "screenshot\.editor-element"/);
  assert.equal((capture.match(/ctx\.CommitEffect\(\);/g) ?? []).length, 1);
  assert.ok(capture.indexOf("Prepare(request)") < capture.indexOf("ctx.CommitEffect();"));
  assert.match(captureSupport, /#if UNITY_6000_7_OR_NEWER/);
  assert.match(captureSupport, /CaptureToRenderTexture/);
  assert.match(captureSupport, /MaxDimension = 4096/);
  assert.match(captureSupport, /MaxArtifactBytes = 32L \* 1024L \* 1024L/);
  assert.match(captureSupport, /Path\.Combine\(ctx\.ArtifactDir, "editor-element\.png"\)/);
  assert.match(registry, /EditorElementCaptureApiAvailable/);
  assert.match(registry, /E_EDITOR_ELEMENT_CAPTURE_UNAVAILABLE/);
  assert.doesNotMatch(capture, /OutputPath|Base64|Inline/);
});

test("test.cancel只取消coordinator拥有的exact TestJobData GUID", () => {
  assert.match(cancel, /name: "test\.cancel"/);
  assert.match(cancel, /internal\.test\.run\.cancel\.plan/);
  assert.match(cancel, /internal\.test\.run\.cancel\.status/);
  assert.match(cancel, /active\.includes\(runId\)/);
  assert.match(testCoordinator, /UnityCliSessionMutationLease\.IsOwnedBy\(root\.RequestId, root\.RequestDigest\)/);
  assert.match(testCoordinator, /ids\.Length != 1 \|\| !Same\(ids\[0\], runId\)/);
  assert.match(testCoordinator, /GetField\("editModeRunner"/);
  assert.match(testCoordinator, /GetMethod\("OnRunCancel"/);
  assert.match(testCoordinator, /FinishCancelled/);
  assert.doesNotMatch(testCoordinator, /Escape|ExitPlaymode|ExitPlayMode/);
});

test("五个Editor Utility Tool在唯一completion contract各出现一次", () => {
  for (const name of ["editor.autotick-set", "editor.focus", "editor.search", "screenshot.editor-element", "test.cancel"])
    assert.equal(completion.tools.filter((entry) => entry.toolName === name).length, 1, name);
});
