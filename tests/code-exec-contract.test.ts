import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import codeExecute from "../src/tools/code/execute.js";

test("code.execute publishes direct examples with and without a completion probe", () => {
  const plainExample = codeExecute.examples?.[0];
  const probeExample = codeExecute.examples?.[1];
  assert.ok(plainExample);
  assert.ok(probeExample);
  assert.deepEqual(plainExample.argv?.slice(0, 3), ["tools", "run", "code.execute"]);
  const plainInputIndex = plainExample.argv!.indexOf("--input");
  const plainInput = JSON.parse(plainExample.argv![plainInputIndex + 1]!) as Record<string, unknown>;
  assert.equal(typeof plainInput.code, "string");
  assert.equal("completionProbe" in plainInput, false);
  const probeInputIndex = probeExample.argv!.indexOf("--input");
  const probeInput = JSON.parse(probeExample.argv![probeInputIndex + 1]!) as Record<string, unknown>;
  assert.ok(probeInput.completionProbe && typeof probeInput.completionProbe === "object");
  assert.equal(plainExample.argv!.includes("--dry-run"), false);
  assert.equal(probeExample.argv!.includes("--dry-run"), false);
  assert.equal(codeExecute.timeoutMs, 60_000);
  assert.doesNotMatch(JSON.stringify(codeExecute.examples), /\{\.\.\.\}|--channel|batchmode|--job/);
});

test("code.execute clearly preserves necessary hazardous code and discloses the non-preemptible main-thread boundary", () => {
  assert.match(codeExecute.summary, /cannot be force-stopped by timeout/i);
  assert.match(codeExecute.description, /does not reject infinite loops, long sleeps, blocking waits, lock contention/);
  assert.match(codeExecute.description, /Prefer a bounded alternative when possible/);
  assert.match(codeExecute.description, /necessary code executes unchanged/);
  assert.match(codeExecute.description, /cannot interrupt Run/);
  assert.match(codeExecute.description, /require terminating the Editor/);
  assert.match(codeExecute.description, /outcome-unknown, not proof execution stopped/);
  assert.match(codeExecute.description, /wait for this CLI process to exit and allow at least 5000ms beyond --timeout-ms/);
  assert.match(codeExecute.description, /Dry-run reports source hash/);
  assert.doesNotMatch(codeExecute.description, /reject(?:s|ed)? (?:dangerous|hazardous) code/i);
});

test("Shared exception projection grants exact Editor internals access without an asmdef cycle", async () => {
  const [assemblyInfo, sharedAssembly, editorAssembly] = await Promise.all([
    readFile("unitypkg/Shared/AssemblyInfo.cs", "utf8"),
    readFile("unitypkg/Shared/UnityCli.Shared.asmdef", "utf8"),
    readFile("unitypkg/UnityCli.Editor.asmdef", "utf8"),
  ]);
  assert.match(assemblyInfo, /InternalsVisibleTo\("UnityCli\.Editor"\)/);
  assert.match(assemblyInfo, /InternalsVisibleTo\("UnityCli\.Editor\.Tests"\)/);
  assert.deepEqual((JSON.parse(sharedAssembly) as { references: string[] }).references, []);
  assert.ok((JSON.parse(editorAssembly) as { references: string[] }).references.includes("UnityCli.Shared"));
});

test("raw code.execute dry-run cannot compile source and normal execution rechecks the exact lease", async () => {
  const [support, compiler, coordinator, runtimeCode, toolSource, dispatcher] = await Promise.all([
    readFile("unitypkg/Editor/Infrastructure/CodeExecSupport.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/DynamicCodeCompiler.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs", "utf8"),
    readFile("src/runtime-code.ts", "utf8"),
    readFile("src/tools/code/execute.ts", "utf8"),
    readFile("src/tools/dispatcher.ts", "utf8"),
  ]);
  const entry = support.slice(support.indexOf("ValueTask<string> CodeExec"), support.indexOf("private static string WrapCodeSource"));
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
  // 编译子进程与固定临时目录的唯一 owner 已抽取到共享编译器；同一不变式改在新 owner 上校验。
  assert.match(compiler, /RunUnityCompilerAsync[\s\S]*deadlineOk[\s\S]*waiter\.Until[\s\S]*TerminateCompiler/);
  assert.match(compiler, /MaxCompileSeconds[\s\S]*Task\.WaitAll[\s\S]*TerminateCompiler/);
  assert.match(compiler, /TemporaryRoot\(\)[\s\S]*"Library", "UnityCli", "code-exec-temp"[\s\S]*finally[\s\S]*DeleteTemporaryCodeFile/);
  assert.doesNotMatch(support, /Microsoft\.CodeAnalysis|CompileInProcess|CodeExecUseInProcessRoslyn|CompileWithCscFallback/);
  assert.doesNotMatch(compiler, /Microsoft\.CodeAnalysis|CompileInProcess|CodeExecUseInProcessRoslyn|CompileWithCscFallback/);
  assert.match(compiler, /CollectReferences[\s\S]*deadlineOk\(\)[\s\S]*OperationCanceledException/);
  assert.match(support, /RoslynReferenceFailure[\s\S]*E_REFERENCE_RESOLUTION_FAILED/);
  const referenceDiscovery = compiler.slice(compiler.indexOf("internal static List<string> CollectReferences"), compiler.indexOf("private static void AddReference"));
  assert.doesNotMatch(referenceDiscovery, /File\.Exists|File\.Open|Directory\.GetFiles/);
  assert.doesNotMatch(support, /ValidateCodeReferences|WaitForExit\(60000\)|E_CODE_RUNTIME_FAILED/);
  assert.match(support, /UnityCliExceptionProjection\.Project\(exception\)[\s\S]*projection\.PublicMessage/);
  assert.match(support, /"causes", projection\.Causes[\s\S]*"causeChainTruncated", projection\.Truncated/);
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
  assert.match(dispatcher, /parseTypedInput\(flags, requestId, name === "code\.execute" \? 1024 \* 1024 : undefined, [^)]+\)/);
  assert.match(dispatcher, /readBoundedInputFile[\s\S]*beforeInputDeadline[\s\S]*handle\.read/);
  assert.match(dispatcher, /readStdin\(maxBytes, absoluteDeadlineMs\)[\s\S]*InputDeadlineExceeded/);
  assert.match(dispatcher, /E_CODE_INPUT_TOO_LARGE/);
});
