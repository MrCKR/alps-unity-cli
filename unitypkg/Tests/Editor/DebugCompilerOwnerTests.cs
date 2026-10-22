#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// 统一编译器 owner 的行为测试：四个提交形状共享同一份记账、加载前拒绝、失败候选按真实加载结果计数、
    /// 两路推进互斥、超时清理保留占用，以及暂停期 lane 不依赖中央 scheduler tick 的有界推进。
    /// </summary>
    public sealed class DebugCompilerOwnerTests
    {
        [SetUp]
        public void ResetOwner()
        {
            DebugCompilerOwner.ResetDomain();
        }

        [TearDown]
        public void ResetOwnerAfterTest()
        {
            DebugCompilerOwner.ResetDomain();
        }

        [Test]
        public void TwoShapesShareOneArtifactCounter()
        {
            // code 与 hook 的产物都只经同一个 owner 记账：数量与字节在同一份账上累加。
            DebugCompilerOwner.CommitLoadedArtifact("code-candidate", 4096L, "UnityCli.RuntimeCode");
            DebugCompilerOwner.CommitLoadedArtifact("hook-candidate", 2048L, "UnityCli.HookCallback.0123456789abcdef");

            var budget = DebugCompilerOwner.Budget();
            Assert.That(budget.Count, Is.EqualTo(2), "both shapes must share one artifact counter");
            Assert.That(budget.Bytes, Is.EqualTo(6144L + 2L * DebugLimits.MaxCompiledArtifactOverheadBytes), "both shapes must share one byte counter, each charged the per-assembly allowance");
            Assert.That(DebugCompilerOwner.IsLoaded("code-candidate"), Is.True);
            Assert.That(DebugCompilerOwner.IsLoaded("hook-candidate"), Is.True);
        }

        [Test]
        public void IdenticalCandidateDoesNotDoubleCountWhileADifferentCandidateDoes()
        {
            DebugCompilerOwner.CommitLoadedArtifact("same-candidate", 1024L, "UnityCli.Same");
            DebugCompilerOwner.CommitLoadedArtifact("same-candidate", 1024L, "UnityCli.Same");
            Assert.That(DebugCompilerOwner.Budget().Count, Is.EqualTo(1), "recompiling the identical candidate must not double-count");
            Assert.That(DebugCompilerOwner.Budget().Bytes, Is.EqualTo(1024L + DebugLimits.MaxCompiledArtifactOverheadBytes));

            DebugCompilerOwner.CommitLoadedArtifact("different-candidate", 2048L, "UnityCli.Different");
            Assert.That(DebugCompilerOwner.Budget().Count, Is.EqualTo(2), "a genuinely different artifact must consume a new slot");
            Assert.That(DebugCompilerOwner.Budget().Bytes, Is.EqualTo(3072L + 2L * DebugLimits.MaxCompiledArtifactOverheadBytes));
        }

        [Test]
        public void RefusalReportsOccupancyAndNeverReloadsAutomatically()
        {
            for (var index = 0; index < DebugLimits.MaxCompiledArtifacts; index++)
                DebugCompilerOwner.CommitLoadedArtifact("candidate-" + index.ToString(CultureInfo.InvariantCulture), 1L, "UnityCli.Generated" + index.ToString(CultureInfo.InvariantCulture));

            var admission = DebugCompilerOwner.TryAdmitCandidate("candidate-new", 0L);
            var chargedPerArtifact = 1L + DebugLimits.MaxCompiledArtifactOverheadBytes;
            Assert.That(admission.Admitted, Is.False, "the count budget must refuse before an unrecoverable load");
            Assert.That(admission.Reason, Is.EqualTo("artifact-count"));
            Assert.That(admission.Report.Count, Is.EqualTo(DebugLimits.MaxCompiledArtifacts));
            Assert.That(admission.Report.RemainingCount, Is.EqualTo(0));
            Assert.That(admission.Report.Bytes, Is.EqualTo((long)DebugLimits.MaxCompiledArtifacts * chargedPerArtifact));
            var details = admission.Report.ToData();
            Assert.That(details["compiledArtifacts"], Is.EqualTo(DebugLimits.MaxCompiledArtifacts));
            Assert.That(details["compiledArtifactBytes"], Is.EqualTo((long)DebugLimits.MaxCompiledArtifacts * chargedPerArtifact));
            Assert.That(details["remainingArtifacts"], Is.EqualTo(0));
            // 数量上限由字节预算与固定开销推导，两者在同一尺度上同时到达；超出部分按 0 剩余上报。
            Assert.That(details["compiledArtifactBytes"], Is.GreaterThanOrEqualTo(DebugLimits.MaxCompiledArtifactBytes));
            Assert.That(details["remainingBytes"], Is.EqualTo(0));
            Assert.That(details["maxSingleArtifactBytes"], Is.EqualTo(DebugLimits.MaxSingleArtifactBytes));
            // 判定发生在加载之前：拒绝不改变状态，也不自动 reload 或扣减已加载占用。
            Assert.That(DebugCompilerOwner.Budget().Count, Is.EqualTo(DebugLimits.MaxCompiledArtifacts));
            Assert.That(DebugCompilerOwner.ReferenceFingerprint(), Is.Not.Empty);
            Assert.That(DebugCompilerOwner.ReloadSuggestion, Does.Contain("Reload the Unity domain"));

            DebugCompilerOwner.ResetDomain();
            DebugCompilerOwner.CommitLoadedArtifact("filled-byte-candidate", DebugLimits.MaxCompiledArtifactBytes - DebugLimits.MaxCompiledArtifactOverheadBytes - 1L, "UnityCli.Filled");
            var byteRefusal = DebugCompilerOwner.TryAdmitCandidate("byte-candidate", 2L);
            Assert.That(byteRefusal.Admitted, Is.False, "the cumulative byte budget must refuse before an unrecoverable load");
            Assert.That(byteRefusal.Reason, Is.EqualTo("artifact-bytes"));

            var singleRefusal = DebugCompilerOwner.TryAdmitCandidate("single-candidate", DebugLimits.MaxSingleArtifactBytes + 1L);
            Assert.That(singleRefusal.Admitted, Is.False, "the single artifact budget must refuse before an unrecoverable load");
            Assert.That(singleRefusal.Reason, Is.EqualTo("single-artifact-bytes"));
        }

        [Test]
        public void LoadedCandidateAccountingFollowsTheRealLoadResult()
        {
            // 编译失败的候选没有加载任何程序集，因此不占数量，也不占字节。
            Assert.That(DebugCompilerOwner.TryBeginOwnership(DebugCompilerLane.MainThreadTick), Is.True);
            DynamicCodeCompileRun run = null;
            try
            {
                run = DynamicCodeCompiler.StartCompilation(Request("this is not valid C# at all"), AllReferences(), () => true, CancellationToken.None);
                DriveToTerminal(run, DebugCompilerLane.MainThreadTick);
                var failed = DynamicCodeCompiler.CompleteCompilation(run);
                Assert.That(failed.Success, Is.False, "invalid source must not compile");
                Assert.That(DebugCompilerOwner.Budget().Count, Is.EqualTo(0), "a candidate that never loaded must not consume artifact count");
                Assert.That(DebugCompilerOwner.Budget().Bytes, Is.EqualTo(0L));
            }
            finally
            {
                DebugCompilerOwner.EndOwnership(DebugCompilerLane.MainThreadTick);
            }

            // 已经加载但 ABI 校验失败的候选仍然计入：字节已经进入 Mono，无法逐个卸载。
            DebugCompilerOwner.CommitLoadedArtifact("abi-failed-candidate", 512L, "UnityCli.RuntimeCode");
            Assert.That(DebugCompilerOwner.Budget().Count, Is.EqualTo(1));
            Assert.That(DebugCompilerOwner.Budget().Bytes, Is.EqualTo(512L + DebugLimits.MaxCompiledArtifactOverheadBytes));
        }

        [Test]
        public void BoundedAdvanceCompletesACompileWithoutMainThreadTicks()
        {
            var run = DynamicCodeCompiler.StartCompilation(
                Request("public static class Probe { public static int Value() { return 7; } }"),
                AllReferences(),
                () => true,
                CancellationToken.None);
            Assert.That(run.Terminal, Is.False);

            var started = false;
            var worker = new Thread(() =>
            {
                if (!DebugCompilerOwner.TryBeginOwnershipWithin(DebugCompilerLane.PauseService, TimeSpan.FromSeconds(5))) return;
                started = true;
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(120);
                    while (!run.Terminal && DateTime.UtcNow < deadline)
                    {
                        // 暂停期服务循环只调用有界推进入口，不经过 UnityCliWaitScheduler.Tick()。
                        DebugCompilerOwner.AdvanceOwnedCompilation(run, DebugCompilerLane.PauseService);
                        Thread.Sleep(10);
                    }
                }
                finally
                {
                    DebugCompilerOwner.EndOwnership(DebugCompilerLane.PauseService);
                }
            });
            worker.Start();
            Assert.That(worker.Join(TimeSpan.FromSeconds(180)), Is.True, "the pause service lane must converge within its own budget");
            Assert.That(started, Is.True, "the pause lane must acquire the compile budget without waiting for a main-thread continuation");

            var result = DynamicCodeCompiler.CompleteCompilation(run);
            Assert.That(result.Success, Is.True, "the bounded advance entry point must drive a compile to success without scheduler ticks");
            Assert.That(DebugCompilerOwner.IsOwned, Is.False, "the pause lane must release ownership when it finishes");
        }

        [Test]
        public void TwoWayBoundedAdvanceIsMutuallyExclusive()
        {
            Assert.That(DebugCompilerOwner.TryBeginOwnership(DebugCompilerLane.MainThreadTick), Is.True);
            Assert.That(DebugCompilerOwner.TryBeginOwnership(DebugCompilerLane.PauseService), Is.False, "the pause lane must not take ownership while the main-thread lane holds it");
            Assert.That(DebugCompilerOwner.HoldsOwnership(DebugCompilerLane.MainThreadTick), Is.True);
            Assert.That(DebugCompilerOwner.HoldsOwnership(DebugCompilerLane.PauseService), Is.False);

            var stopwatch = Stopwatch.StartNew();
            var acquired = DebugCompilerOwner.TryBeginOwnershipWithin(DebugCompilerLane.PauseService, TimeSpan.FromMilliseconds(150));
            stopwatch.Stop();
            Assert.That(acquired, Is.False, "a bounded acquisition must fail while another lane owns the compiler");
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(5000), "a bounded acquisition must return within its budget instead of waiting for an old continuation");

            Assert.That(DebugCompilerOwner.AdvanceOwnedCompilation(null, DebugCompilerLane.PauseService), Is.False, "a non-owner lane must not advance the compile");
            Assert.That(DebugCompilerOwner.AdvanceOwnedCompilation(null, DebugCompilerLane.MainThreadTick), Is.True, "the owning lane drives the same bounded advance entry point");
            Assert.That(DebugCompilerOwner.EndOwnership(DebugCompilerLane.PauseService), Is.False, "a non-owner lane must not release ownership");

            Assert.That(DebugCompilerOwner.EndOwnership(DebugCompilerLane.MainThreadTick), Is.True);
            Assert.That(DebugCompilerOwner.IsOwned, Is.False);
            Assert.That(DebugCompilerOwner.TryBeginOwnershipWithin(DebugCompilerLane.PauseService, TimeSpan.FromSeconds(1)), Is.True, "the pause lane may take over after the owner released");
            Assert.That(DebugCompilerOwner.EndOwnership(DebugCompilerLane.PauseService), Is.True);
        }

        [Test]
        public void TimeoutOrCancelCleanupKeepsArtifactAccounting()
        {
            DebugCompilerOwner.CommitLoadedArtifact("already-loaded", 777L, "UnityCli.RuntimeCode");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.That(DebugCompilerOwner.TryBeginOwnership(DebugCompilerLane.MainThreadTick), Is.True);
                try
                {
                    var run = DynamicCodeCompiler.StartCompilation(Request("public static class Probe { }"), AllReferences(), () => true, cancellation.Token);
                    Assert.That(run.Terminal, Is.False);
                    Assert.That(DebugCompilerOwner.AdvanceOwnedCompilation(run, DebugCompilerLane.MainThreadTick), Is.True, "an already cancelled compile must reach its terminal state in one bounded step");
                    var result = DynamicCodeCompiler.CompleteCompilation(run);
                    Assert.That(result.TimedOut, Is.True);
                    foreach (var path in new[] { run.SourceFilePath, run.ResponseFilePath, run.AssemblyFilePath, run.PdbFilePath })
                        Assert.That(File.Exists(path), Is.False, "timeout cleanup must delete " + path);
                }
                finally
                {
                    DebugCompilerOwner.EndOwnership(DebugCompilerLane.MainThreadTick);
                }
            }

            Assert.That(DebugCompilerOwner.Budget().Count, Is.EqualTo(1), "cancel must not credit back an already loaded artifact");
            Assert.That(DebugCompilerOwner.Budget().Bytes, Is.EqualTo(777L + DebugLimits.MaxCompiledArtifactOverheadBytes));
        }

        [Test]
        public void RemoveAndStopHaveNoAccountingCreditBackPath()
        {
            // 记账只能增加，或在 Domain Reload 边界整体归零：不得存在 unload/release 单个产物的入口。
            foreach (var method in typeof(DebugCompilerOwner).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var forbidden in new[] { "Unload", "Release", "Credit", "Remove", "Delete", "Stop" })
                    Assert.That(method.Name.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase), Is.LessThan(0),
                        method.Name + " must not exist: only the Domain Reload boundary reclaims loaded artifacts");

            DebugCompilerOwner.CommitLoadedArtifact("kept", 2048L, "UnityCli.Kept");
            Assert.That(DebugCompilerOwner.Budget().Count, Is.EqualTo(1));
            DebugCompilerOwner.ResetDomain();
            Assert.That(DebugCompilerOwner.Budget().Count, Is.EqualTo(0), "only the Domain Reload boundary may zero the accounting");
            Assert.That(DebugCompilerOwner.Budget().Bytes, Is.EqualTo(0L));
        }

        [Test]
        public void PerRequestReferencesDoNotMutateTheGenerationSnapshot()
        {
            var baseline = AllReferences();
            var withExtra = DebugCompilerOwner.References("Z:\\unitycli-extra-reference.dll", null, () => true);
            Assert.That(withExtra, Does.Contain("Z:\\unitycli-extra-reference.dll"), "a per-request reference must reach its own request");
            var afterRequest = AllReferences();
            Assert.That(afterRequest, Does.Not.Contain("Z:\\unitycli-extra-reference.dll"), "a per-request reference must not leak into the shared generation snapshot");
            Assert.That(afterRequest.Count, Is.EqualTo(baseline.Count));
        }

        [Test]
        public void PublishedReferenceSnapshotDoesNotChangeWithinAGeneration()
        {
            var fingerprint = DebugCompilerOwner.ReferenceFingerprint();
            var excludedBefore = ExcludedAssemblies(DebugCompilerOwner.ReferenceDiagnostics());
            DebugCompilerOwner.CommitLoadedArtifact("generated-artifact", 16L, typeof(DebugCompilerOwner).Assembly.GetName().Name);

            Assert.That(DebugCompilerOwner.ReferenceFingerprint(), Is.EqualTo(fingerprint), "later loads must not change the published generation snapshot");
            Assert.That(ExcludedAssemblies(DebugCompilerOwner.ReferenceDiagnostics()), Is.EqualTo(excludedBefore + 1), "this feature's own artifacts are outputs, not reference inputs");
        }

        private static DynamicCodeCompileRequest Request(string source)
        {
            return new DynamicCodeCompileRequest
            {
                SourceText = source,
                AssemblySimpleName = "UnityCli.DebugCompilerOwnerTests"
            };
        }

        private static List<string> AllReferences()
        {
            return DebugCompilerOwner.References(null, null, () => true);
        }

        private static void DriveToTerminal(DynamicCodeCompileRun run, DebugCompilerLane lane)
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!run.Terminal && DateTime.UtcNow < deadline)
            {
                DebugCompilerOwner.AdvanceOwnedCompilation(run, lane);
                Thread.Sleep(10);
            }
        }

        private static int ExcludedAssemblies(string diagnostics)
        {
            const string marker = "excluded=";
            var start = diagnostics.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            return int.Parse(diagnostics.Substring(start), CultureInfo.InvariantCulture);
        }
    }
}
#endif
