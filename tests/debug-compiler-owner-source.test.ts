import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const repoRoot = process.cwd();

const OWNER = "unitypkg/Editor/Infrastructure/Compiler/DebugCompilerOwner.cs";
const COMPILER = "unitypkg/Editor/Infrastructure/DynamicCodeCompiler.cs";
const CODE_EXEC = "unitypkg/Editor/Infrastructure/CodeExecSupport.cs";
const HOOK_HOST = "unitypkg/Editor/Infrastructure/MethodHooks/MethodHookHost.cs";
const HOOK_OPERATIONS = "unitypkg/Editor/Infrastructure/MethodHooks/MethodHookHost.Operations.cs";
const OWNER_TESTS = "unitypkg/Tests/Editor/DebugCompilerOwnerTests.cs";

const read = (path: string) => readFile(join(repoRoot, path), "utf8");

/** 统计文本中出现次数，用于冻结“只有一个入口”一类断言。 */
function occurrences(text: string, pattern: RegExp): number {
  return [...text.matchAll(new RegExp(pattern.source, pattern.flags.includes("g") ? pattern.flags : `${pattern.flags}g`))].length;
}

test("the compiler owner is the only compile-ownership entry point", async () => {
  const [owner, compiler, codeExec, hookHost] = await Promise.all([read(OWNER), read(COMPILER), read(CODE_EXEC), read(HOOK_HOST)]);

  assert.match(owner, /internal static bool TryBeginOwnership\(DebugCompilerLane lane\)/);
  assert.match(owner, /internal static bool TryBeginOwnershipWithin\(DebugCompilerLane lane, TimeSpan budget\)/);
  assert.match(owner, /internal static bool EndOwnership\(DebugCompilerLane lane\)/);
  assert.match(owner, /internal enum DebugCompilerLane[\s\S]*MainThreadTick[\s\S]*PauseService[\s\S]*\}/, "both advance lanes must be declared once");
  assert.equal(occurrences(`${owner}${compiler}${codeExec}${hookHost}`, /internal static bool TryBeginOwnership\(/), 1, "only one ownership entry point may exist");

  for (const text of [compiler, codeExec, hookHost]) assert.doesNotMatch(text, /CompileGate|TryBeginCompilation|EndCompilation/, "the compile gate may only live in the owner");
  assert.doesNotMatch(compiler, /private static bool compiling|internal static bool IsCompiling/);
  assert.doesNotMatch(`${codeExec}${hookHost}`, /DynamicCodeCompiler\.(TryBeginCompilation|EndCompilation|CollectReferences|IsCompiling)/, "shapes must not reach the compile gate or the reference snapshot directly");
  assert.doesNotMatch(hookHost, /LoadedCallbackKeys|compiledArtifactBytes|domainReferenceFingerprint/);
  assert.doesNotMatch(hookHost + (await read(HOOK_OPERATIONS)), /EnsureCompileBudget/);
});

test("all four submit shapes share the owner for admission and accounting", async () => {
  const [owner, codeExec, hookHost, hookOperations] = await Promise.all([read(OWNER), read(CODE_EXEC), read(HOOK_HOST), read(HOOK_OPERATIONS)]);

  for (const shape of [codeExec, hookHost]) {
    assert.match(shape, /DebugCompilerOwner\.TryBeginOwnership\(DebugCompilerLane\.MainThreadTick\)/);
    assert.match(shape, /DebugCompilerOwner\.TryAdmitCandidate\(/);
    assert.match(shape, /DebugCompilerOwner\.CommitLoadedArtifact\(/);
    assert.match(shape, /DebugCompilerOwner\.References\(/);
    assert.match(shape, /DebugCompilerOwner\.ReloadSuggestion/);
  }
  // hook 的 effect 前检查也复用同一准入判定，不另写一份预算规则。
  assert.match(hookOperations, /RequireCompileAdmission\(CompileKey\(/);
  assert.match(hookHost, /private static void RequireCompileAdmission\(string identity, long artifactBytes\)/);
  assert.match(owner, /internal static DebugCompilerAdmission TryAdmitCandidate\(string identity, long artifactBytes\)/);
  assert.match(owner, /internal static void CommitLoadedArtifact\(string identity, long artifactBytes, string assemblySimpleName\)/);
  assert.match(owner, /internal static DebugCompilerBudgetReport Budget\(\)/);
  // code 与 hook 的产物 identity 都来自同一 owner 推导。
  assert.match(owner, /internal static string CandidateIdentity\(string shape, string sourceText, string defines\)/);
  assert.match(codeExec, /DebugCompilerOwner\.CandidateIdentity\("debug\.code-execute"/);
  assert.match(hookHost, /var key = CompileKey\(prefix, postfix\);/);
});

test("the reference snapshot and its fingerprint are declared once", async () => {
  const [owner, compiler, codeExec, hookHost] = await Promise.all([read(OWNER), read(COMPILER), read(CODE_EXEC), read(HOOK_HOST)]);
  const snapshotLoop = /foreach \(var assembly in AppDomain\.CurrentDomain\.GetAssemblies\(\)\)/;

  assert.match(owner, /internal static List<string> References\(string refsCsv, List<string> extraReferences, Func<bool> deadlineOk\)/);
  assert.match(owner, /internal static string ReferenceFingerprint\(\)/);
  assert.match(owner, /internal static string ReferenceDiagnostics\(\)/);
  assert.equal(occurrences(owner, snapshotLoop), 2, "the owner owns the reference snapshot enumeration and its bounded diagnostic");
  for (const text of [compiler, codeExec, hookHost]) assert.equal(occurrences(text, snapshotLoop), 0, "no second reference snapshot may exist");
  assert.doesNotMatch(hookHost, /ReferenceFingerprint\(void\)|private static string ReferenceFingerprint\(/);
  assert.doesNotMatch(hookHost, /ModuleVersionId/);
  // 快照按 generation 只发布一次。
  assert.match(owner, /if \(referenceSnapshot != null\) return referenceSnapshot;/);
  assert.match(owner, /referenceFingerprint = null;/);
});

test("the artifact budget is declared only in DebugLimits", async () => {
  const [owner, compiler, hookHost, limits] = await Promise.all([read(OWNER), read(COMPILER), read(HOOK_HOST), read("unitypkg/Editor/Infrastructure/Debug/DebugLimits.cs")]);

  for (const declaration of [/const int MaxCompiledArtifacts/, /const long MaxCompiledArtifactBytes/, /const long MaxSingleArtifactBytes/, /const long MaxCompiledArtifactOverheadBytes/]) {
    for (const text of [owner, compiler, hookHost]) assert.doesNotMatch(text, declaration, "compiler budget values must only be declared in DebugLimits");
  }
  // 三个声明的数值只在 DebugLimits 出现，owner 侧只引用常量名；数量上限由字节预算与单产物固定开销推导。
  assert.match(limits, /MaxCompiledArtifacts = \(int\)\(MaxCompiledArtifactBytes \/ MaxCompiledArtifactOverheadBytes\);/);
  assert.match(limits, /MaxCompiledArtifactBytes = 128L \* 1024 \* 1024;/);
  assert.match(limits, /MaxSingleArtifactBytes = 8L \* 1024 \* 1024;/);
  assert.match(limits, /MaxCompiledArtifactOverheadBytes = 256L \* 1024;/);
  for (const text of [owner, compiler, hookHost]) {
    for (const restated of [/MaxCompiledArtifacts\s*=\s*\d/, /MaxCompiledArtifactBytes\s*=\s*128/, /MaxSingleArtifactBytes\s*=\s*8/, /MaxCompiledArtifactOverheadBytes\s*=\s*\d/]) {
      assert.doesNotMatch(text, restated, "the owner set must reference the declared limits instead of restating them");
    }
  }
  assert.match(owner, /DebugLimits\.MaxCompiledArtifacts/);
  assert.match(owner, /DebugLimits\.MaxCompiledArtifactBytes/);
  assert.match(owner, /DebugLimits\.MaxSingleArtifactBytes/);
  // 准入与记账必须用同一口径把每个程序集的固定开销算进字节预算。
  assert.match(owner, /report\.Bytes \+ ChargedArtifactBytes\(artifactBytes\) > DebugLimits\.MaxCompiledArtifactBytes/);
  assert.match(owner, /loadedArtifactBytes \+= ChargedArtifactBytes\(artifactBytes\)/);
  assert.match(owner, /\+ DebugLimits\.MaxCompiledArtifactOverheadBytes/);
});

test("refusal reports occupancy and never reloads the domain automatically", async () => {
  const owner = await read(OWNER);

  assert.match(owner, /internal const string BudgetExhaustedCode = "E_COMPILER_BUDGET_EXHAUSTED";/);
  assert.match(owner, /internal const string ReloadSuggestion = "[^"]*Reload the Unity domain/);
  // describe 与实现共用同一上限来源：不仅公布数量/字节，还公布单产物固定开销与可回收、可复用的事实。
  assert.match(owner, /BudgetExhaustedCode/);
  assert.match(owner, /\{ "compiledArtifacts", Count \}/);
  assert.match(owner, /\{ "compiledArtifactBytes", Bytes \}/);
  assert.match(owner, /\{ "remainingArtifacts", RemainingCount \}/);
  assert.match(owner, /\{ "remainingBytes", RemainingBytes \}/);
  // 拒绝不得改变状态，也不得自动 reload：owner 不接触 EditorApplication，也不调用任何 reload API。
  assert.doesNotMatch(owner, /EditorApplication|DomainReload|ReloadDomain|AssetDatabase\.Refresh/);
  // remove/stop 没有扣减占用的入口；只有 Domain Reset 可以归零。
  assert.doesNotMatch(owner, /internal static (void|bool|long|int) (Unload|Release|Credit|Remove|Stop|Delete)/);
  assert.match(owner, /internal static void ResetDomain\(\)/);
});

test("the bounded advance entry point exists and both lanes are mutually exclusive", async () => {
  const [owner, compiler] = await Promise.all([read(OWNER), read(COMPILER)]);

  assert.match(owner, /internal static bool AdvanceOwnedCompilation\(DynamicCodeCompileRun run, DebugCompilerLane lane\)/);
  assert.match(owner, /if \(!HoldsOwnership\(lane\)\) return false;[\s\S]*return DynamicCodeCompiler\.AdvanceCompilation\(run\);/);
  assert.match(compiler, /internal static bool AdvanceCompilation\(DynamicCodeCompileRun run\)/);
  assert.match(compiler, /internal static bool AdvanceCompilation\(DynamicCodeCompileRun run\)[\s\S]*run\.CancellationToken\.IsCancellationRequested \|\| !run\.DeadlineOk\(\)[\s\S]*HasExited\(run\.Process\)[\s\S]*OutputDrained\(run\)/,
    "one bounded step must poll exit, drain bounded output and apply cancellation and deadline");
  // 主线程路径也只调用同一个有界推进入口，不再直接等待子进程。
  assert.match(compiler, /waiter\.Until\(\s*\(\) => DebugCompilerOwner\.AdvanceOwnedCompilation\(run, DebugCompilerLane\.MainThreadTick\),/);
  assert.equal(occurrences(`${owner}${compiler}`, /AdvanceCompilation\(run\)/), 1, "there must be exactly one bounded advance implementation and one caller of it");
  assert.match(compiler, /internal static DynamicCodeCompileRun StartCompilation\(/);
  assert.match(compiler, /internal static DynamicCodeCompileResult CompleteCompilation\(DynamicCodeCompileRun run\)/);
  assert.match(compiler, /internal static void AbortCompilation\(DynamicCodeCompileRun run\)/);
});

test("the wait path never busy-polls and keeps the fixed temporary file set", async () => {
  const [owner, compiler] = await Promise.all([read(OWNER), read(COMPILER)]);

  for (const text of [owner, compiler]) {
    assert.doesNotMatch(text, /Thread\.Sleep|SpinWait|Thread\.Spin/, "the compile wait path must not busy-poll");
  }
  assert.equal(occurrences(`${owner}${compiler}`, /code-exec-temp/), 1, "there must be exactly one fixed compile temporary root");
  assert.match(compiler, /Path\.Combine\(UnityCliBridge\.ProjectRoot\(\), "Library", "UnityCli", "code-exec-temp"\)/);
  for (const fileName of ["source.cs", "references.rsp", "assembly.dll", "assembly.pdb"]) {
    assert.ok(compiler.includes(`"${fileName}"`), `the fixed temporary file set must keep ${fileName}`);
  }
  assert.match(compiler, /DeleteTemporaryCodeFileWithRetry\(run\.SourceFilePath\)[\s\S]*DeleteTemporaryCodeFileWithRetry\(run\.ResponseFilePath\)[\s\S]*DeleteTemporaryCodeFileWithRetry\(run\.AssemblyFilePath\)[\s\S]*DeleteTemporaryCodeFileWithRetry\(run\.PdbFilePath\)/,
    "timeout and cancel cleanup must delete the whole fixed file set");
});

test("the frozen behavior coverage is present as NUnit tests", async () => {
  const tests = await read(OWNER_TESTS);
  for (const name of [
    "TwoShapesShareOneArtifactCounter",
    "IdenticalCandidateDoesNotDoubleCountWhileADifferentCandidateDoes",
    "RefusalReportsOccupancyAndNeverReloadsAutomatically",
    "LoadedCandidateAccountingFollowsTheRealLoadResult",
    "BoundedAdvanceCompletesACompileWithoutMainThreadTicks",
    "TwoWayBoundedAdvanceIsMutuallyExclusive",
    "TimeoutOrCancelCleanupKeepsArtifactAccounting",
    "RemoveAndStopHaveNoAccountingCreditBackPath",
  ]) {
    assert.match(tests, new RegExp(`public void ${name}\\(\\)`), `${name} must exist`);
  }
});
