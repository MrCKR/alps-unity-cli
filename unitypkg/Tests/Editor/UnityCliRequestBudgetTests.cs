using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli.Tests
{
    public sealed class UnityCliRequestBudgetTests
    {
        [Test]
        public void SparseHundredThousandConsoleShapeStopsBeforeMaterialization()
        {
            var clock = new ManualClock();
            var budget = Budget(clock, scans: 1000);
            var matches = 0;
            for (var index = 99999; index >= 0; index--)
            {
                if (!budget.TryConsumeScannedRecord()) break;
                if (index % 10000 == 0) matches++;
            }
            Assert.That(budget.Usage().ScannedRecords, Is.EqualTo(1000));
            Assert.That(matches, Is.EqualTo(0));
            Assert.That(budget.ExhaustedDimension, Is.EqualTo("scannedRecords"));
        }

        [Test]
        public void ZeroNegativeAndOverMaximumBudgetValuesFailBeforeReads()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new UnityCliRequestBudget(new UnityCliReadBudgetInput { MaxScannedRecords = 0 }, 1000, CancellationToken.None, new ManualClock()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UnityCliRequestBudget(new UnityCliReadBudgetInput { MaxNodes = -1 }, 1000, CancellationToken.None, new ManualClock()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UnityCliRequestBudget(new UnityCliReadBudgetInput { MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters + 1 }, 1000, CancellationToken.None, new ManualClock()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UnityCliRequestBudget(new UnityCliReadBudgetInput { MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls + 1 }, 1000, CancellationToken.None, new ManualClock()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UnityCliRequestBudget(new UnityCliReadBudgetInput { MaxMainThreadMilliseconds = UnityCliRequestBudget.AbsoluteMaxMainThreadMilliseconds + 1 }, 1000, CancellationToken.None, new ManualClock()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UnityCliRequestBudget(new UnityCliReadBudgetInput { MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes + 1 }, 1000, CancellationToken.None, new ManualClock()));
        }

        [Test]
        public void SerializedUtf8BudgetCountsEncodedBytesAndStructureReservations()
        {
            var budget = new UnityCliRequestBudget(new UnityCliReadBudgetInput { MaxSerializedUtf8Bytes = 8 }, 1000, CancellationToken.None, new ManualClock());
            Assert.That(budget.TryReserveSerializedUtf8("é"), Is.True);
            Assert.That(budget.TryReserveSerializedUtf8Bytes(6), Is.True);
            Assert.That(budget.TryReserveSerializedUtf8("x"), Is.False);
            Assert.That(budget.ExhaustedDimension, Is.EqualTo("serializedUtf8Bytes"));
            Assert.That(budget.Usage().EstimatedSerializedUtf8Bytes, Is.EqualTo(8));
            Assert.That(budget.Usage().ActualSerializedUtf8Bytes, Is.Zero);
        }

        [Test]
        public void ManualMonotonicClockAndCancellationShareEveryCounter()
        {
            var clock = new ManualClock();
            var cancellation = new CancellationTokenSource();
            var budget = new UnityCliRequestBudget(new UnityCliReadBudgetInput { MaxMainThreadMilliseconds = 5 }, 1000, cancellation.Token, clock);
            Assert.That(budget.TryConsumeNode(), Is.True);
            clock.Advance(5);
            Assert.That(budget.TryConsumeGetter(), Is.False);
            Assert.That(budget.ExhaustedDimension, Is.EqualTo("mainThreadMilliseconds"));

            var cancelled = new UnityCliRequestBudget(null, 1000, cancellation.Token, clock);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => cancelled.TryConsumeReflectionCall());
        }

        [Test]
        public void CyclicDeepWideEnumerableAndMultiplePathsUseOneSerializerBudget()
        {
            var cycle = new Node { Name = "root" };
            cycle.Next = cycle;
            var cycleJson = UnityCliSerializer.Serialize(UnityCliBridge.ObjectSerializer.Serialize(cycle,
                new UnityCliBridge.ObjectSerializer.Options { MaxDepth = 16, MaxArrayItems = 1000 }, Budget(new ManualClock(), nodes: 32)));
            StringAssert.Contains("cycle-detected", cycleJson);

            var enumerable = new CountingEnumerable(100000);
            var enumerableBudget = Budget(new ManualClock(), scans: 17, nodes: 64);
            var enumerableJson = UnityCliSerializer.Serialize(UnityCliBridge.ObjectSerializer.Serialize(enumerable,
                new UnityCliBridge.ObjectSerializer.Options { MaxDepth = 4, MaxArrayItems = 1000 }, enumerableBudget));
            Assert.That(enumerable.MoveNextCount, Is.EqualTo(17));
            StringAssert.Contains("non-resumable-enumerable", enumerableJson);
            StringAssert.DoesNotContain("nextPath", enumerableJson);

            var multi = new GetterFixture();
            var pathBudget = Budget(new ManualClock(), getters: 1, reflections: 32, nodes: 32);
            var pathJson = UnityCliSerializer.Serialize(UnityCliBridge.ObjectSerializer.Serialize(multi,
                new UnityCliBridge.ObjectSerializer.Options { Paths = new List<string> { "First", "Second" }, MaxDepth = 2 }, pathBudget));
            Assert.That(multi.GetterCount, Is.EqualTo(1));
            StringAssert.Contains("request-budget-exhausted", pathJson);
        }

        [Test]
        public void OversizedAndRepeatedCapturePlansFailBeforeTextureAllocation()
        {
            var requestBudget = Budget(new ManualClock());
            Assert.Throws<UnityCliBridge.BridgeCommandException>(() => ScreenshotToolSupport.PrepareCaptureBudget(8192, 8192, 1, false, null, requestBudget));
            var captures = ScreenshotToolSupport.PrepareCaptureBudget(512, 512, 2, false, null, Budget(new ManualClock()));
            captures.BeginCapture(512, 512);
            captures.BeginCapture(512, 512);
            Assert.Throws<UnityCliBridge.BridgeCommandException>(() => captures.BeginCapture(512, 512));
            Assert.That(captures.Plan.PeakTemporaryBytes, Is.LessThanOrEqualTo(ScreenshotToolSupport.DefaultMaxTemporaryBytes));
        }

        [Test]
        public void HighBranchProfilerAndLargeSceneOrPrefabShapesStopAtGlobalNodeBudget()
        {
            var profiler = SyntheticTree.Wide(depth: 2, width: 100);
            var profilerBudget = Budget(new ManualClock(), scans: 250, nodes: 200);
            Assert.That(Traverse(profiler, profilerBudget), Is.EqualTo(200));
            Assert.That(profilerBudget.ExhaustedDimension, Is.EqualTo("nodes"));

            var scene = SyntheticTree.Wide(depth: 4, width: 12);
            var sceneBudget = Budget(new ManualClock(), scans: 101, nodes: 101);
            Assert.That(Traverse(scene, sceneBudget), Is.EqualTo(101));
            Assert.That(sceneBudget.Usage().ScannedRecords, Is.EqualTo(101));
        }

        [Test]
        public void ProfilerHierarchyTraversesUnity2022FlatPreorderWithoutChildAt()
        {
            var budget = Budget(new ManualClock(), scans: 64, nodes: 64, getters: 64, reflections: 128);
            using (var accessor = ProfilerHierarchyTool.RawHierarchyAccessor.InitializeForTests(new Unity2022RawFrameDataViewProbe(), budget))
            {
                var stack = new List<ProfilerHierarchyTool.TraversalFrame>
                {
                    new ProfilerHierarchyTool.TraversalFrame { ParentId = 0, NextSample = 1, Depth = 0 }
                };
                var items = new List<ProfilerHierarchyTool.HierarchyItem>();
                ProfilerHierarchyTool.HierarchyItem item;
                while (ProfilerHierarchyTool.TryNext(accessor, stack, 8, budget, out item)) items.Add(item);

                CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, items.Select(value => value.Id).ToArray());
                CollectionAssert.AreEqual(new long[] { 0, 1, 0, 3 }, items.Select(value => value.ParentId).ToArray());
                CollectionAssert.AreEqual(new long[] { 0, 1, 0, 1 }, items.Select(value => value.Depth).ToArray());
                CollectionAssert.AreEqual(new long[] { 1, 0, 1, 0 }, items.Select(value => value.ChildCount).ToArray());
                Assert.That(accessor.SubtreeEndExclusive(0, budget), Is.EqualTo(5));
                Assert.That(stack, Is.Empty);
            }
        }

        [Test]
        public void ContinuationBindsScopeGenerationAndRawPosition()
        {
            var token = UnityCliContinuationToken.Create("console.read", "generation-a", "99999:99000:sentinel");
            string position;
            Assert.That(UnityCliContinuationToken.TryParse(token, "console.read", "generation-a", out position), Is.True);
            Assert.That(position, Is.EqualTo("99999:99000:sentinel"));
            Assert.That(UnityCliContinuationToken.TryParse(token, "console.read", "generation-b", out position), Is.False);
            Assert.That(UnityCliContinuationToken.TryParse(token, "scene.hierarchy", "generation-a", out position), Is.False);
        }

        [Test]
        public void SnapshotRetentionKeepsLatestHundredAndHonorsBytePressure()
        {
            var sizes = Enumerable.Repeat(10L, 101).ToArray();
            var byCount = UnityCliRetentionPlanner.RetainedIndices(sizes, 100, 10000);
            Assert.That(byCount.Count, Is.EqualTo(100));
            Assert.That(byCount.Contains(100), Is.False);
            var byBytes = UnityCliRetentionPlanner.RetainedIndices(sizes, 100, 35);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, byBytes.OrderBy(value => value).ToArray());
            var contiguousNewest = UnityCliRetentionPlanner.RetainedIndices(new[] { 20L, 40L, 1L }, 100, 35);
            CollectionAssert.AreEqual(new[] { 0 }, contiguousNewest.OrderBy(value => value).ToArray());
        }

        [Test]
        public void BroadReflectionReturnsStableContinuationWithinCallBudget()
        {
            var budget = Budget(new ManualClock(), scans: 8, nodes: 64, reflections: 128);
            var page = UnityCliBridge.ReflectionInvoker.FindMethods("System.String", null,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, 100, null, budget);
            Assert.That(page.Methods.Count, Is.LessThanOrEqualTo(8));
            Assert.That(page.Truncated, Is.True);
            Assert.That(page.NextCursor, Is.Not.Null.And.Not.Empty);
            string position;
            Assert.That(UnityCliContinuationToken.TryParse(page.NextCursor, "runtime-reflection.method.find", page.Generation, out position), Is.True);
        }

        [Test]
        public void CallbackPublicationOwnsFactoryOnceAndRejectsLateCallbacks()
        {
            var abandoned = new UnityCliCallbackPublication<string>();
            var lateFactoryCalls = 0;
            Assert.That(abandoned.TryAbandon(), Is.True);
            Assert.That(abandoned.TryComplete(() => { lateFactoryCalls++; return "late"; }), Is.False);
            Assert.That(lateFactoryCalls, Is.Zero);

            var completed = new UnityCliCallbackPublication<string>();
            Assert.That(completed.TryComplete(() => "published"), Is.True);
            Assert.That(completed.TryFail(new InvalidOperationException("late")), Is.False);
            bool isCompleted;
            bool isFailed;
            string value;
            Exception error;
            completed.Read(out isCompleted, out isFailed, out value, out error);
            Assert.That(isCompleted, Is.True);
            Assert.That(isFailed, Is.False);
            Assert.That(value, Is.EqualTo("published"));
            Assert.That(error, Is.Null);

            var faulted = new UnityCliCallbackPublication<string>();
            Assert.That(faulted.TryComplete(() => { throw new IOException("write fault"); }), Is.True);
            faulted.Read(out isCompleted, out isFailed, out value, out error);
            Assert.That(isCompleted, Is.False);
            Assert.That(isFailed, Is.True);
            Assert.That(error, Is.TypeOf<IOException>());
        }

        [Test]
        public void RealScenePagerAndComponentGetterFailClosedBeforeBatchMaterialization()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var first = NewRoot(scene, "issue-042-first");
                var second = NewRoot(scene, "issue-042-second");
                var third = NewRoot(scene, "issue-042-third");
                first.AddComponent<BoxCollider>();
                first.AddComponent<SphereCollider>();

                var rootBudget = Budget(new ManualClock(), scans: 2);
                Assert.Throws<UnityCliRequestBudgetExceededException>(() => UnityCliBridge.ObjectRefResolver.BoundedSceneRoots(scene, rootBudget));
                Assert.That(rootBudget.Usage().ScannedRecords, Is.Zero);

                var componentBudget = Budget(new ManualClock(), scans: 2);
                Assert.Throws<UnityCliRequestBudgetExceededException>(() => UnityCliBridge.ObjectRefResolver.BoundedComponents(first, componentBudget));
                Assert.That(componentBudget.Usage().ScannedRecords, Is.Zero);

                var pagerBudget = Budget(new ManualClock(), scans: 32, nodes: 32, getters: 128);
                var traversal = new UnityCliHierarchyPager.Traversal(scene, null, 8, pagerBudget);
                Assert.That(traversal.First(pagerBudget), Is.SameAs(first));
                Assert.That(traversal.Next(first, pagerBudget), Is.SameAs(second));
                var generation = UnityCliReadGeneration.Hierarchy(scene);
                var cursor = UnityCliHierarchyPager.Cursor("issue-042", generation, "scene", third, pagerBudget);
                string phase;
                Assert.That(UnityCliHierarchyPager.ResolveCursor(cursor, "issue-042", generation, pagerBudget, out phase), Is.SameAs(third));
                Assert.That(phase, Is.EqualTo("scene"));

                var handler = new UnityCliBridge.SceneGameObjectComponentsTool();
                Assert.Throws<UnityCliRequestBudgetExceededException>(() => handler.ExecuteAsync(
                    new UnityCliBridge.SceneGameObjectComponentsTool.Request
                    {
                        Id = first.GetInstanceID(),
                        IncludeProperties = false,
                        Budget = new UnityCliReadBudgetInput { MaxScannedRecords = 2 }
                    },
                    new UnityCliProjectToolContext { RequestId = "issue-042-over-budget", TimeoutMs = 30000, CancellationToken = CancellationToken.None }).GetAwaiter().GetResult());
                var handlerResult = handler.ExecuteAsync(
                    new UnityCliBridge.SceneGameObjectComponentsTool.Request { Id = first.GetInstanceID(), IncludeProperties = false },
                    new UnityCliProjectToolContext { RequestId = "issue-042", TimeoutMs = 30000, CancellationToken = CancellationToken.None }).GetAwaiter().GetResult();
                Assert.That(handlerResult.Count, Is.EqualTo(3));
                Assert.That(handlerResult.Components.Count, Is.EqualTo(3));
            }
            finally
            {
                Assert.That(EditorSceneManager.ClosePreviewScene(scene), Is.True);
                Assert.That(scene.IsValid(), Is.False);
            }
        }

        [Test]
        public void DictionaryAndEnumTruncationAreExplicitlyNonResumable()
        {
            var dictionary = new Dictionary<string, int> { { "alpha", 1 }, { "beta", 2 } };
            var dictionaryJson = UnityCliSerializer.Serialize(UnityCliBridge.ObjectSerializer.Serialize(
                dictionary,
                new UnityCliBridge.ObjectSerializer.Options { MaxDepth = 4, MaxArrayItems = 1 },
                Budget(new ManualClock(), scans: 32, nodes: 32, getters: 32, reflections: 32)));
            StringAssert.Contains("non-resumable-dictionary-truncation", dictionaryJson);
            StringAssert.DoesNotContain("nextPath", dictionaryJson);

            var enumPage = UnityCliBridge.TypeSchemaGenerator.Generate(
                typeof(Issue042Enum).FullName,
                4,
                1,
                16,
                16,
                null,
                Budget(new ManualClock(), scans: 4096, nodes: 64, getters: 64, reflections: 4096));
            var schema = (Dictionary<string, object>)enumPage.Data["schema"];
            Assert.That(schema["continuationSupported"], Is.False);
            Assert.That(schema["truncationReason"], Is.EqualTo("non-resumable-enum-truncation"));
            Assert.That(enumPage.NextCursor, Is.Null);
        }

        [Test]
        public void ConsoleCoreFailsOnZeroProgressWithoutRescanningUnboundedInput()
        {
            var adapter = new EmptyConsoleSnapshot(100000);
            var budget = Budget(new ManualClock(), scans: 3, nodes: 32, getters: 32, reflections: 32);
            Assert.Throws<UnityCliRequestBudgetExceededException>(() => EditorToolSupport.ReadConsolePage("error", null, 10, budget, adapter));
            Assert.That(adapter.ReadCount, Is.EqualTo(3));
            Assert.That(budget.ExhaustedDimension, Is.EqualTo("scannedRecords"));
        }

        [Test]
        public void WideReflectionSnapshotsAndInvocationPrebudgetFailBeforeEffect()
        {
            Assert.Throws<UnityCliRequestBudgetExceededException>(() => UnityCliReflectionSnapshot.Methods(
                typeof(string),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static,
                Budget(new ManualClock(), scans: 64, nodes: 64, getters: 64, reflections: 64),
                1));
            Assert.Throws<UnityCliRequestBudgetExceededException>(() => UnityCliReflectionSnapshot.Interfaces(
                typeof(Dictionary<string, string>),
                Budget(new ManualClock(), scans: 64, nodes: 64, getters: 64, reflections: 64),
                1));
            Assert.Throws<UnityCliRequestBudgetExceededException>(() => UnityCliReflectionSnapshot.GenericArguments(
                typeof(Dictionary<string, List<Dictionary<int, string>>>),
                Budget(new ManualClock(), scans: 64, nodes: 64, getters: 64, reflections: 64),
                1));
            Assert.Throws<UnityCliRequestBudgetExceededException>(() => UnityCliBridge.TypeSchemaGenerator.Generate(
                typeof(Dictionary<string, List<Dictionary<int, string>>>).AssemblyQualifiedName,
                8,
                64,
                64,
                64,
                null,
                Budget(new ManualClock(), scans: 128, nodes: 128, getters: 128, reflections: 128, serialized: 64)));

            Issue042InvocationFixture.InvocationCount = 0;
            var callBudget = Budget(new ManualClock(), scans: 128, nodes: 128, getters: 128, reflections: 128, serialized: 1024);
            Assert.Throws<UnityCliRequestBudgetExceededException>(() => UnityCliBridge.ReflectionInvoker.PrepareCall(
                typeof(Issue042InvocationFixture).FullName,
                "Invoke",
                new List<object> { 1L, 2L, 3L },
                null,
                null,
                BindingFlags.Public | BindingFlags.Static,
                callBudget));
            Assert.That(Issue042InvocationFixture.InvocationCount, Is.Zero);
        }

        [Test]
        public void TerminalSerializationReportsExactUtf8AndUsesManualDeadline()
        {
            var clock = new ManualClock();
            var budget = Budget(clock);
            var data = new Dictionary<string, object>
            {
                { "value", "é" },
                { "budget", budget.Usage() }
            };
            var json = UnityCliBridge.SerializeTerminalSuccess("issue-042", data, budget, CancellationToken.None);
            Assert.That(budget.Usage().ActualSerializedUtf8Bytes, Is.EqualTo(Encoding.UTF8.GetByteCount(json)));
            StringAssert.Contains("actualSerializedUtf8Bytes", json);

            var deadlineClock = new ManualClock();
            var deadlineBudget = Budget(deadlineClock, milliseconds: 5);
            deadlineClock.Advance(5);
            Assert.Throws<UnityCliRequestBudgetExceededException>(() => UnityCliBridge.SerializeTerminalSuccess(
                "issue-042-deadline", new { Value = "late" }, deadlineBudget, CancellationToken.None));
        }

        [Test]
        public void MemoryRetentionLimitsAndOrphanCleanupOperateOnRealFiles()
        {
            var root = CreateTestRoot("memory");
            try
            {
                ProfilerMemorySnapshotSupport.ResetTestOverrides();
                ProfilerMemorySnapshotSupport.RootOverrideForTests = root;
                string newest = null;
                for (var index = 0; index < 101; index++)
                {
                    var path = Path.Combine(root, "snapshot-" + index.ToString("D3") + ".snap");
                    File.WriteAllBytes(path, new[] { (byte)index });
                    File.SetLastWriteTimeUtc(path, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index));
                    newest = path;
                }
                ProfilerMemorySnapshotSupport.ApplyRetention(newest, Budget(new ManualClock(), scans: 10000, nodes: 4096, getters: 4096));
                Assert.That(Directory.GetFiles(root, "*.snap", SearchOption.TopDirectoryOnly).Length, Is.EqualTo(ProfilerMemorySnapshotSupport.RetainedSnapshotCount));
                Assert.That(File.Exists(newest), Is.True);
                Assert.That(File.Exists(Path.Combine(root, "snapshot-000.snap")), Is.False);

                CollectionAssert.AreEqual(
                    new[] { 0, 1 },
                    UnityCliRetentionPlanner.RetainedIndices(
                        new[] { ProfilerMemorySnapshotSupport.AbsoluteSnapshotBytes, ProfilerMemorySnapshotSupport.AbsoluteSnapshotBytes, 1L },
                        ProfilerMemorySnapshotSupport.RetainedSnapshotCount,
                        ProfilerMemorySnapshotSupport.RetainedSnapshotBytes).OrderBy(value => value).ToArray());
                Assert.DoesNotThrow(() => ProfilerMemorySnapshotSupport.ValidateSnapshotLength(
                    ProfilerMemorySnapshotSupport.AbsoluteSnapshotBytes,
                    ProfilerMemorySnapshotSupport.AbsoluteSnapshotBytes));
                Assert.Throws<UnityCliBridge.BridgeCommandException>(() => ProfilerMemorySnapshotSupport.ValidateSnapshotLength(
                    ProfilerMemorySnapshotSupport.AbsoluteSnapshotBytes + 1L,
                    ProfilerMemorySnapshotSupport.AbsoluteSnapshotBytes));

                for (var index = 0; index < 10; index++)
                    File.WriteAllBytes(Path.Combine(root, ".pending-orphan-" + index.ToString("D2") + ".snap"), new byte[] { 1 });
                Assert.That(ProfilerMemorySnapshotSupport.CleanupOrphanPass(), Is.True);
                Assert.That(Directory.GetFiles(root, ".pending-*.snap", SearchOption.TopDirectoryOnly), Is.Empty);
            }
            finally
            {
                ProfilerMemorySnapshotSupport.ResetTestOverrides();
                DeleteTestRoot(root);
            }
        }

        [Test]
        public void ScreenshotPendingPublicationIsUniqueAtomicAndFaultOwned()
        {
            var root = CreateTestRoot("screen");
            try
            {
                var output = Path.Combine(root, "capture.png");
                using (var first = new ScreenshotScreenTool.PendingCapturePublication(output))
                using (var second = new ScreenshotScreenTool.PendingCapturePublication(output))
                {
                    Assert.That(first.PendingPath, Is.Not.EqualTo(second.PendingPath));
                    File.WriteAllText(first.PendingPath, "first");
                    File.WriteAllText(second.PendingPath, "second");
                    first.Publish();
                    Assert.That(File.ReadAllText(output), Is.EqualTo("first"));
                    Assert.That(first.TryFault(), Is.False);
                    Assert.That(second.TryFault(), Is.True);
                    Assert.That(File.Exists(second.PendingPath), Is.False);
                    Assert.Throws<InvalidOperationException>(() => second.Publish());
                    Assert.That(File.ReadAllText(output), Is.EqualTo("first"));
                }
                string latePath;
                using (var late = new ScreenshotScreenTool.PendingCapturePublication(output))
                {
                    latePath = late.PendingPath;
                    late.BeginWrite();
                    Assert.That(late.TryFault(), Is.True);
                }
                File.WriteAllText(latePath, "late");
                using (var sweep = new ScreenshotScreenTool.PendingCapturePublication(output))
                {
                    Assert.That(File.Exists(latePath), Is.False);
                    Assert.That(sweep.TryFault(), Is.True);
                }
                Assert.That(Directory.GetFiles(root, "*.unitycli.pending", SearchOption.TopDirectoryOnly), Is.Empty);
            }
            finally
            {
                DeleteTestRoot(root);
            }
        }

        [Test]
        public void TextureAndRawViewOwnershipReleaseInitializationFailures()
        {
            Texture2D texture = null;
            var destroyed = false;
            Assert.Throws<InvalidOperationException>(() => ScreenshotToolSupport.CreateTextureWithOwnership(
                () => texture = new Texture2D(2, 2, TextureFormat.RGB24, false),
                value => { throw new InvalidOperationException("populate failed"); },
                value => { destroyed = true; UnityEngine.Object.DestroyImmediate(value); }));
            Assert.That(destroyed, Is.True);
            Assert.That(texture == null, Is.True);

            var rawView = new DisposableProbe();
            Assert.Throws<InvalidOperationException>(() => ProfilerHierarchyTool.RawHierarchyAccessor.InitializeOwnedView<object>(
                rawView,
                value => { throw new InvalidOperationException("binding failed"); }));
            Assert.That(rawView.Disposed, Is.True);
        }

        private static GameObject NewRoot(Scene scene, string name)
        {
            var gameObject = new GameObject(name);
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            return gameObject;
        }

        private static string CreateTestRoot(string suffix)
        {
            var parent = Path.GetFullPath(Path.Combine("Library", "UnityCli", "tests"));
            var root = Path.Combine(parent, "issue-042-" + suffix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Assert.That(Directory.Exists(root), Is.True);
            return root;
        }

        private static void DeleteTestRoot(string root)
        {
            var parent = Path.GetFullPath(Path.Combine("Library", "UnityCli", "tests")) + Path.DirectorySeparatorChar;
            var canonical = Path.GetFullPath(root);
            Assert.That(canonical.StartsWith(parent, StringComparison.OrdinalIgnoreCase), Is.True);
            if (Directory.Exists(canonical)) Directory.Delete(canonical, true);
            Assert.That(Directory.Exists(canonical), Is.False);
        }

        private static int Traverse(SyntheticTree root, UnityCliRequestBudget budget)
        {
            var count = 0;
            var stack = new Stack<SyntheticTree>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeNode()) break;
                var current = stack.Pop();
                count++;
                for (var index = current.Children.Count - 1; index >= 0; index--) stack.Push(current.Children[index]);
            }
            return count;
        }

        private static UnityCliRequestBudget Budget(ManualClock clock, long scans = 10000, long nodes = 4096, long getters = 2048, long reflections = 4096, long serialized = 8 * 1024 * 1024, long milliseconds = 2000)
        {
            return new UnityCliRequestBudget(new UnityCliReadBudgetInput
            {
                MaxScannedRecords = scans,
                MaxNodes = nodes,
                MaxGetters = getters,
                MaxReflectionCalls = reflections,
                MaxMainThreadMilliseconds = milliseconds,
                MaxSerializedUtf8Bytes = serialized
            }, 30000, CancellationToken.None, clock);
        }

        private sealed class ManualClock : IUnityCliMonotonicClock
        {
            public long ElapsedMilliseconds { get; private set; }
            public void Advance(long milliseconds) { ElapsedMilliseconds += milliseconds; }
        }

        private sealed class Node { public string Name { get; set; } public Node Next { get; set; } }

        private sealed class GetterFixture
        {
            public int GetterCount;
            public string First { get { GetterCount++; return "first"; } }
            public string Second { get { GetterCount++; return "second"; } }
        }

        private sealed class DisposableProbe : IDisposable
        {
            internal bool Disposed { get; private set; }
            public void Dispose() { Disposed = true; }
        }

        private sealed class Unity2022RawFrameDataViewProbe
        {
            private static readonly int[] DirectChildren = { 2, 1, 0, 1, 0 };
            private static readonly int[] RecursiveChildren = { 4, 1, 0, 1, 0 };

            public bool valid { get { return true; } }
            public int sampleCount { get { return DirectChildren.Length; } }
            public int frameIndex { get { return 17; } }
            public int threadIndex { get { return 0; } }
            public double frameStartTimeMs { get { return 100.0; } }
            public float frameTimeMs { get { return 16.0f; } }
            public int GetSampleChildrenCount(int sampleIndex) { return DirectChildren[sampleIndex]; }
            public int GetSampleChildrenCountRecursive(int sampleIndex) { return RecursiveChildren[sampleIndex]; }
            public string GetSampleName(int sampleIndex) { return "sample-" + sampleIndex.ToString(); }
            public float GetSampleTimeMs(int sampleIndex) { return sampleIndex; }
        }

        private sealed class EmptyConsoleSnapshot : EditorToolSupport.IConsoleSnapshotAdapter
        {
            internal EmptyConsoleSnapshot(int count) { Count = count; }
            public int Count { get; private set; }
            internal int ReadCount { get; private set; }
            public bool TryRead(int rawIndex, UnityCliRequestBudget budget, out ConsoleEntry result)
            {
                result = null;
                if (!budget.TryConsumeScannedRecord()) return false;
                ReadCount++;
                result = new ConsoleEntry { Type = "log", Message = string.Empty, File = string.Empty };
                return true;
            }
        }

        private enum Issue042Enum { First, Second, Third }

        private static class Issue042InvocationFixture
        {
            internal static int InvocationCount;
            public static int Invoke(int first, int second, int third)
            {
                InvocationCount++;
                return first + second + third;
            }
        }

        private sealed class CountingEnumerable : IEnumerable
        {
            private readonly int count;
            internal CountingEnumerable(int count) { this.count = count; }
            internal int MoveNextCount { get; private set; }
            public IEnumerator GetEnumerator() { return new Counter(this); }

            private sealed class Counter : IEnumerator
            {
                private readonly CountingEnumerable owner;
                private int index = -1;
                internal Counter(CountingEnumerable owner) { this.owner = owner; }
                public object Current { get { return index; } }
                public bool MoveNext() { owner.MoveNextCount++; index++; return index < owner.count; }
                public void Reset() { index = -1; }
            }
        }

        private sealed class SyntheticTree
        {
            internal readonly List<SyntheticTree> Children = new List<SyntheticTree>();
            internal static SyntheticTree Wide(int depth, int width)
            {
                var root = new SyntheticTree();
                if (depth == 0) return root;
                for (var index = 0; index < width; index++) root.Children.Add(Wide(depth - 1, width));
                return root;
            }
        }
    }
}
