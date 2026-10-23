import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import codeExecute from "../src/tools/debug/code-execute.js";
import codeFlowRun from "../src/tools/debug/code-flow-run.js";
import codeFlowSubmit from "../src/tools/debug/code-flow-submit.js";

test("debug.code-execute publishes direct examples with and without a completion probe", () => {
  const plainExample = codeExecute.examples?.[0];
  const probeExample = codeExecute.examples?.[1];
  assert.ok(plainExample);
  assert.ok(probeExample);
  assert.deepEqual(plainExample.argv?.slice(0, 3), ["tools", "run", "debug.code-execute"]);
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

test("debug.code-execute clearly preserves necessary hazardous code and discloses the non-preemptible main-thread boundary", () => {
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

test("every Tool that compiles user code discloses the default reference set and its CS0433 consequence", () => {
  // 引用集来自运行期 AppDomain 快照且只能追加；不披露时调用方只有编译失败后才知道类型名歧义。
  for (const tool of [codeExecute, codeFlowRun, codeFlowSubmit]) {
    assert.match(tool.description, /every locatable assembly already loaded in the Editor AppDomain is referenced/, tool.name);
    assert.match(tool.description, /refs only appends more; nothing can be removed/, tool.name);
    assert.match(tool.description, /CS0433/, tool.name);
    assert.match(tool.description, /System\.Tuple exists in both mscorlib and ExCSS\.Unity/, tool.name);
    assert.match(tool.description, /use object\[\] or a type you declare/, tool.name);
  }
});

test("debug.code-execute publishes the exact wrapper declaration and never cuts a returned value silently", async () => {
  const [support, toolSource, dispatcher, evaluation, pauseDtos, pauseTool] = await Promise.all([
    readFile("unitypkg/Editor/Infrastructure/CodeExecSupport.cs", "utf8"),
    readFile("src/tools/debug/code-execute.ts", "utf8"),
    readFile("src/tools/dispatcher.ts", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/Debug/DebugPauseEvaluation.cs", "utf8"),
    readFile("unitypkg/Editor/Tools/debug/pause/DebugPauseDtos.cs", "utf8"),
    readFile("unitypkg/Editor/Tools/debug/pause-evaluate.cs", "utf8"),
  ]);
  // 包裹声明必须与 WrapCodeSource 的真实插入点逐字一致，否则调用方无法预防 CS0136 一类失败。
  assert.match(support, /namespace UnityCli\.RuntimeCode \{ public static class Script \{ public static object Run\(string argsJson\) \{ " \+ source \+ " \} \} \}/);
  assert.match(support, /namespace UnityCli\.RuntimeCode \{ public static class Script \{ " \+ source \+ " \} \}/);
  assert.match(toolSource, /the body of exactly `public static object Run\(string argsJson\)` inside `namespace UnityCli\.RuntimeCode \{ public static class Script \{ \} \}`/);
  assert.match(toolSource, /the wrapper declares no locals, so argsJson is the only pre-declared identifier/);
  // 返回值裁剪事实必须随结果公开：C# 投影、runtime 字段与 CLI completion proof 三处一致。
  assert.match(support, /NormalizeReturnValue\(object value, out bool truncated\)/);
  assert.match(support, /truncated = bounded\.Length != \(text == null \? 0 : text\.Length\)/);
  assert.match(support, /"returnValueTruncated", returnValueTruncated/);
  assert.match(support, /"returnValueByteBudget", \(long\)MaxCodeOutputBytes/);
  assert.match(toolSource, /returnValueTruncated: boolean; returnValueByteBudget: number/);
  assert.match(dispatcher, /typeof runtime\.returnValueTruncated !== "boolean"/);
  assert.match(dispatcher, /runtime\.returnValueByteBudget <= 0/);
  // 同一条共享 Run 返回值路径上的暂停现场求值也必须带出裁剪事实。
  assert.match(evaluation, /ProjectReturnValue\(object value, bool mainThread, out bool truncated\)/);
  assert.match(evaluation, /truncated = truncation\.Truncated/);
  assert.match(evaluation, /ReturnValueTruncated = returnValueTruncated/);
  assert.match(pauseDtos, /public bool ReturnValueTruncated \{ get; set; \}/);
  assert.match(pauseTool, /returnValueTruncated reports whether the returned value was cut/);
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

test("raw debug.code-execute dry-run cannot compile source and normal execution rechecks the exact lease", async () => {
  const [support, compiler, compilerOwner, coordinator, runtimeCode, toolSource, dispatcher] = await Promise.all([
    readFile("unitypkg/Editor/Infrastructure/CodeExecSupport.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/DynamicCodeCompiler.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/Compiler/DebugCompilerOwner.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs", "utf8"),
    readFile("src/runtime-code.ts", "utf8"),
    readFile("src/tools/debug/code-execute.ts", "utf8"),
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
  // 引用发现、快照指纹与加载产物预算的唯一 owner 现在是 DebugCompilerOwner；同一不变式改在新 owner 上校验。
  assert.match(compilerOwner, /internal static List<string> References\(string refsCsv, List<string> extraReferences, Func<bool> deadlineOk\)[\s\S]*OperationCanceledException/);
  assert.match(compilerOwner, /internal static string ReferenceFingerprint\(\)/);
  assert.match(support, /RoslynReferenceFailure[\s\S]*E_REFERENCE_RESOLUTION_FAILED/);
  const referenceDiscovery = compilerOwner.slice(compilerOwner.indexOf("internal static List<string> References("), compilerOwner.indexOf("private static void AddReference"));
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
  assert.match(dispatcher, /parseTypedInput\(flags, requestId, name === "debug\.code-execute" \? 1024 \* 1024 : undefined, [^)]+\)/);
  assert.match(dispatcher, /readBoundedInputFile[\s\S]*beforeInputDeadline[\s\S]*handle\.read/);
  assert.match(dispatcher, /readStdin\(maxBytes, absoluteDeadlineMs\)[\s\S]*InputDeadlineExceeded/);
  assert.match(dispatcher, /E_CODE_INPUT_TOO_LARGE/);
});
