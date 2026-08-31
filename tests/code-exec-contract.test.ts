import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import codeExec from "../src/tools/code/exec.js";

test("code.exec publishes direct examples with and without a completion probe", () => {
  const plainExample = codeExec.examples?.[0];
  const probeExample = codeExec.examples?.[1];
  assert.ok(plainExample);
  assert.ok(probeExample);
  assert.deepEqual(plainExample.argv?.slice(0, 3), ["tools", "run", "code.exec"]);
  const plainInputIndex = plainExample.argv!.indexOf("--input");
  const plainInput = JSON.parse(plainExample.argv![plainInputIndex + 1]!) as Record<string, unknown>;
  assert.equal(typeof plainInput.code, "string");
  assert.equal("completionProbe" in plainInput, false);
  const probeInputIndex = probeExample.argv!.indexOf("--input");
  const probeInput = JSON.parse(probeExample.argv![probeInputIndex + 1]!) as Record<string, unknown>;
  assert.ok(probeInput.completionProbe && typeof probeInput.completionProbe === "object");
  assert.equal(plainExample.argv!.includes("--dry-run"), false);
  assert.equal(probeExample.argv!.includes("--dry-run"), false);
  assert.equal(codeExec.timeoutMs, 60_000);
  assert.doesNotMatch(JSON.stringify(codeExec.examples), /\{\.\.\.\}|--channel|batchmode|--job/);
});

test("code.exec clearly preserves necessary hazardous code and discloses the non-preemptible main-thread boundary", () => {
  assert.match(codeExec.summary, /cannot be force-stopped by timeout/i);
  assert.match(codeExec.description, /does not reject infinite loops, long sleeps, blocking waits, lock contention/);
  assert.match(codeExec.description, /Prefer a bounded alternative when possible/);
  assert.match(codeExec.description, /necessary code executes unchanged/);
  assert.match(codeExec.description, /cannot interrupt Run/);
  assert.match(codeExec.description, /require terminating the Editor/);
  assert.match(codeExec.description, /outcome-unknown, not proof execution stopped/);
  assert.match(codeExec.description, /wait for this CLI process to exit and allow at least 5000ms beyond --timeout-ms/);
  assert.match(codeExec.description, /Dry-run reports source hash/);
  assert.doesNotMatch(codeExec.description, /reject(?:s|ed)? (?:dangerous|hazardous) code/i);
});

test("raw code.exec dry-run cannot compile source and normal execution rechecks the exact lease", async () => {
  const [support, coordinator, runtimeCode, toolSource, dispatcher] = await Promise.all([
    readFile("unitypkg/Editor/Infrastructure/CodeExecSupport.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs", "utf8"),
    readFile("src/runtime-code.ts", "utf8"),
    readFile("src/tools/code/exec.ts", "utf8"),
    readFile("src/tools/dispatcher.ts", "utf8"),
  ]);
  const entry = support.slice(support.indexOf("private static string CodeExec"), support.indexOf("private static string WrapCodeSource"));
  assert.ok(entry.indexOf("request.Execution.DryRun == true") < entry.indexOf("request.StringArg(\"sourceText\")"));
  assert.match(entry, /E_DRY_RUN_UNSUPPORTED/);
  assert.doesNotMatch(entry, /File\.ReadAllText|sourcePath/);
  assert.match(entry, /DryRun = request\.Execution != null && request\.Execution\.DryRun == true/);
  assert.match(entry, /BridgeCommandException effectBarrierException = null/);
  assert.match(entry, /BridgeCommandException completionProbeException = null/);
  assert.match(entry, /CommitEffectAction = \(\) =>[\s\S]*BeginCodeEffect\(request, cancellationToken\)[\s\S]*effectBarrierException = exception/);
  assert.match(entry, /ExecuteCompletionProbe\(executionContext, completionProbe, invoke\)[\s\S]*catch \(BridgeCommandException exception\)[\s\S]*completionProbeException = exception/);
  assert.match(entry, /returnValue = NormalizeReturnValue\(method\.Invoke\([\s\S]*ExecuteCompletionProbe\(executionContext, completionProbe, invoke\)/);
  assert.match(support, /NormalizeReturnValue[\s\S]*float\.IsNaN\(number\) \|\| float\.IsInfinity\(number\)[\s\S]*double\.IsNaN\(number\) \|\| double\.IsInfinity\(number\)/);
  assert.doesNotMatch(entry, /CodeSuccessData\([^;]+NormalizeReturnValue/);
  assert.match(entry, /catch \(Exception exception\)[\s\S]*ReferenceEquals\(exception, effectBarrierException\)[\s\S]*ReferenceEquals\(exception, completionProbeException\)[\s\S]*throw;[\s\S]*E_CODE_RUNTIME_EXCEPTION/);
  assert.match(entry, /BeginCodeEffect[\s\S]*cancellationToken\.IsCancellationRequested[\s\S]*TryDeadlineRemainingMs[\s\S]*BeginDirectEffect\(request\.RequestId\)/);
  assert.match(support, /RunUnityCompiler[\s\S]*cancellationToken[\s\S]*TryDeadlineRemainingMs[\s\S]*WaitForExit\(Math\.Max[\s\S]*TerminateCompiler/);
  assert.match(support, /CompileWithUnityCompiler[\s\S]*Library", "UnityCli", "code-exec-temp"[\s\S]*finally[\s\S]*DeleteTemporaryCodeFile/);
  assert.doesNotMatch(support, /Microsoft\.CodeAnalysis|CompileInProcess|CodeExecUseInProcessRoslyn|CompileWithCscFallback/);
  assert.match(support, /DefaultCodeReferences\(request\.StringArg\("refs"\), request, cancellationToken\)[\s\S]*EnsureCodePreEffectDeadline/);
  assert.match(support, /RoslynReferenceFailure[\s\S]*E_REFERENCE_RESOLUTION_FAILED/);
  const referenceDiscovery = support.slice(support.indexOf("private static IEnumerable<string> DefaultCodeReferences"), support.indexOf("private static IEnumerable<string> SplitCsv"));
  assert.doesNotMatch(referenceDiscovery, /File\.Exists|File\.Open|Directory\.GetFiles/);
  assert.doesNotMatch(support, /ValidateCodeReferences|WaitForExit\(60000\)|E_CODE_RUNTIME_FAILED/);
  assert.match(support, /E_CODE_RUNTIME_EXCEPTION/);
  assert.match(coordinator, /BeginDirectEffect\(string requestId\)[\s\S]*DirectMutationBindingMatches\(requestId, requestDigest\)[\s\S]*IsOwnedBy\(requestId, requestDigest\)[\s\S]*E_UNITY_BUSY/);
  assert.match(toolSource, /absoluteDeadlineMs: context\.absoluteDeadlineMs[\s\S]*timeoutMs: 60_000/);
  assert.match(runtimeCode, /maxCodeSourceBytes = 768 \* 1024[\s\S]*maxBridgePayloadBytes = 900 \* 1024/);
  assert.match(runtimeCode, /readBoundedCodeFile[\s\S]*beforeRuntimeCodeDeadline[\s\S]*handle\.read/);
  assert.match(runtimeCode, /readAllStdin\(absoluteDeadlineMs\)[\s\S]*E_EXECUTION_TIMEOUT/);
  assert.match(runtimeCode, /refs\.length > 64[\s\S]*accessReferenceBeforeDeadline/);
  assert.match(runtimeCode, /executeBridgePayload[\s\S]*absoluteDeadlineMs \}\);/);
  assert.match(runtimeCode, /function normalizeTimeout[\s\S]*return Math\.trunc\(value\)/);
  assert.doesNotMatch(runtimeCode, /Math\.min\(Math\.trunc\(value\), 600_000\)/);
  assert.match(dispatcher, /codeInputDeadlineMs[\s\S]*name === "code\.exec" \? 1024 \* 1024/);
  assert.match(dispatcher, /readBoundedInputFile[\s\S]*beforeInputDeadline[\s\S]*handle\.read/);
  assert.match(dispatcher, /readStdin\(maxBytes, absoluteDeadlineMs\)[\s\S]*InputDeadlineExceeded/);
  assert.match(dispatcher, /E_CODE_INPUT_TOO_LARGE/);
});
