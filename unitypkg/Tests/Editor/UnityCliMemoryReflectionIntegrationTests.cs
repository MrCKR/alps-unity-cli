#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;

namespace UnityCli.Tests
{
    public sealed class UnityCliMemoryReflectionIntegrationTests
    {
        [SetUp]
        public void SetUp()
        {
            FakeMemoryProfiler.Reset();
            ProfilerMemorySnapshotSupport.ResetTestOverrides();
        }

        [TearDown]
        public void TearDown()
        {
            FakeMemoryProfiler.Reset();
            ProfilerMemorySnapshotSupport.ResetTestOverrides();
        }

        [Test]
        public void MemorySnapshotTakeCoversAvailableUnavailableUnsupportedAndWriteFailure()
        {
            var root = CreateTestRoot("memory-take");
            try
            {
                ProfilerMemorySnapshotSupport.RootOverrideForTests = root;
                var tool = new ProfilerMemorySnapshotTakeTool();

                ProfilerToolSupport.MemoryProfilerTypeOverrideForTests = _ => null;
                var missing = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => ExecuteTake(tool, "missing", "missing.snap", null));
                Assert.That(missing.Code, Is.EqualTo("E_PROFILER_PACKAGE_MISSING"));
                AssertInventory(root, 0, 0L);

                ProfilerToolSupport.MemoryProfilerTypeOverrideForTests = _ => typeof(UnsupportedMemoryProfiler);
                var unsupported = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => ExecuteTake(tool, "unsupported", "unsupported.snap", null));
                Assert.That(unsupported.Code, Is.EqualTo("E_PROFILER_API_UNSUPPORTED"));
                AssertInventory(root, 0, 0L);

                ProfilerToolSupport.MemoryProfilerTypeOverrideForTests = _ => typeof(FakeMemoryProfiler);
                FakeMemoryProfiler.OnTake = (path, callback) =>
                {
                    File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
                    callback(path, true);
                };
                var successEffects = 0;
                var result = ExecuteTake(tool, "available", "available.snap", null, () => successEffects++);
                Assert.That(successEffects, Is.EqualTo(1));
                Assert.That(result.SizeBytes, Is.EqualTo(4L));
                Assert.That(result.RetainedCountLimit, Is.EqualTo(100L));
                Assert.That(result.RetainedBytesLimit, Is.EqualTo(8L * 1024L * 1024L * 1024L));
                AssertInventory(root, 1, 4L);
                File.Delete(result.Path);

                FakeMemoryProfiler.OnTake = (path, callback) => callback(path, true);
                var failureEffects = 0;
                var writeFailure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
                    ExecuteTake(tool, "write-failure", "write-failure.snap", null, () => failureEffects++));
                Assert.That(writeFailure.Code, Is.EqualTo("E_PROFILER_SNAPSHOT_ARTIFACT_MISSING"));
                Assert.That(failureEffects, Is.EqualTo(1));
                AssertInventory(root, 0, 0L);
            }
            finally
            {
                DeleteTestRoot(root);
            }
        }

        [TestCase("timeout")]
        [TestCase("cancel")]
        [TestCase("reload")]
        public void MemorySnapshotAbandonmentHasOneTerminalAndLateCallbackCannotPublish(string outcome)
        {
            var root = CreateTestRoot("memory-" + outcome);
            try
            {
                ProfilerMemorySnapshotSupport.RootOverrideForTests = root;
                ProfilerToolSupport.MemoryProfilerTypeOverrideForTests = _ => typeof(FakeMemoryProfiler);
                FakeMemoryProfiler.OnTake = (path, callback) => { };
                var terminal = OutcomeException(outcome);
                var effects = 0;
                var tool = new ProfilerMemorySnapshotTakeTool();

                if (outcome == "cancel")
                {
                    Assert.Throws<OperationCanceledException>(() =>
                        ExecuteTake(tool, outcome, outcome + ".snap", terminal, () => effects++));
                }
                else
                {
                    var failure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
                        ExecuteTake(tool, outcome, outcome + ".snap", terminal, () => effects++));
                    Assert.That(failure.Code, Is.EqualTo(outcome == "timeout" ? "E_EXECUTION_TIMEOUT" : "E_EDITOR_RELOADING"));
                }

                Assert.That(effects, Is.EqualTo(1));
                Assert.That(FakeMemoryProfiler.Callback, Is.Not.Null);
                AssertInventory(root, 0, 0L);

                File.WriteAllBytes(FakeMemoryProfiler.PendingPath, new byte[] { 7, 8, 9 });
                FakeMemoryProfiler.Callback(FakeMemoryProfiler.PendingPath, true);
                AssertInventory(root, 0, 0L);
            }
            finally
            {
                DeleteTestRoot(root);
            }
        }

        [Test]
        public void MemorySnapshotListPagesLatestHundredAndRejectsChangedGeneration()
        {
            var root = CreateTestRoot("memory-list");
            try
            {
                ProfilerMemorySnapshotSupport.RootOverrideForTests = root;
                string newest = null;
                for (var index = 0; index < 105; index++)
                {
                    var path = Path.Combine(root, "snapshot-" + index.ToString("D3") + ".snap");
                    File.WriteAllBytes(path, new[] { (byte)index });
                    File.SetLastWriteTimeUtc(path, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index));
                    newest = path;
                }
                ProfilerMemorySnapshotSupport.ApplyRetention(newest, Budget());
                AssertInventory(root, 100, 100L);

                var tool = new ProfilerMemorySnapshotListTool();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string cursor = null;
                string firstCursor = null;
                var pages = 0;
                do
                {
                    var page = tool.ExecuteAsync(
                        new ProfilerMemorySnapshotListTool.Request { PageSize = 17, Cursor = cursor },
                        Context("memory-list-" + pages.ToString(), null, null)).GetAwaiter().GetResult();
                    Assert.That(page.Root, Is.EqualTo(Path.GetFullPath(root)));
                    Assert.That(page.Total, Is.EqualTo(100L));
                    Assert.That(page.Snapshots.Count, Is.LessThanOrEqualTo(17));
                    foreach (var entry in page.Snapshots)
                    {
                        Assert.That(Path.GetFullPath(entry.Path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
                        Assert.That(names.Add(entry.Path), Is.True);
                    }
                    if (pages == 0) firstCursor = page.NextCursor;
                    cursor = page.NextCursor;
                    pages++;
                } while (cursor != null && pages < 10);

                Assert.That(cursor, Is.Null);
                Assert.That(pages, Is.EqualTo(6));
                Assert.That(names.Count, Is.EqualTo(100));

                File.Delete(names.First());
                var stale = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => tool.ExecuteAsync(
                    new ProfilerMemorySnapshotListTool.Request { PageSize = 17, Cursor = firstCursor },
                    Context("memory-list-stale", null, null)).GetAwaiter().GetResult());
                Assert.That(stale.Code, Is.EqualTo("E_CONTINUATION_STALE"));
                AssertInventory(root, 99, 99L);
            }
            finally
            {
                DeleteTestRoot(root);
            }
        }

        [Test]
        public void RecursiveTypeSchemaPagesAndBoundsDepthMembersDefinitionsBytesTimeAndGeneration()
        {
            var typeName = typeof(RecursiveSchemaFixture).AssemblyQualifiedName;
            var names = new HashSet<string>(StringComparer.Ordinal);
            string cursor = null;
            string firstCursor = null;
            var pages = 0;
            do
            {
                var page = UnityCliBridge.TypeSchemaGenerator.Generate(typeName, 4, 128, 16, 2, cursor, Budget());
                var schema = (Dictionary<string, object>)page.Data["schema"];
                var properties = (Dictionary<string, object>)schema["properties"];
                foreach (var name in properties.Keys) Assert.That(names.Add(name), Is.True);
                if (pages == 0) firstCursor = page.NextCursor;
                cursor = page.NextCursor;
                pages++;
            } while (cursor != null && pages < 20);

            Assert.That(cursor, Is.Null);
            Assert.That(names.Count, Is.GreaterThanOrEqualTo(5));
            Assert.That(pages, Is.GreaterThan(1));

            var limited = UnityCliBridge.TypeSchemaGenerator.Generate(typeName, 1, 2, 1, 100, null, Budget());
            Assert.That(limited.Truncated, Is.True);
            Assert.That((long)limited.Data["returnedMemberCount"], Is.LessThanOrEqualTo(2L));
            Assert.That((long)limited.Data["definitionCount"], Is.LessThanOrEqualTo(1L));
            Assert.That(limited.NextCursor, Is.Not.Null.And.Not.Empty);

            var byteFailure = Assert.Throws<UnityCliRequestBudgetExceededException>(() =>
                UnityCliBridge.TypeSchemaGenerator.Generate(typeName, 4, 128, 16, 100, null, Budget(serialized: 1)));
            Assert.That(byteFailure.Dimension, Is.EqualTo("serializedUtf8Bytes"));
            var timeFailure = Assert.Throws<UnityCliRequestBudgetExceededException>(() =>
                UnityCliBridge.TypeSchemaGenerator.Generate(typeName, 4, 128, 16, 100, null, Budget(milliseconds: 3, clock: new AdvancingClock())));
            Assert.That(timeFailure.Dimension, Is.EqualTo("mainThreadMilliseconds"));

            UnityCliReadGeneration.InvalidateReflection();
            var stale = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
                UnityCliBridge.TypeSchemaGenerator.Generate(typeName, 4, 128, 16, 2, firstCursor, Budget()));
            Assert.That(stale.Code, Is.EqualTo("E_CONTINUATION_STALE"));
        }

        [Test]
        public void ReflectionMethodFindPagesWithoutDuplicatesAndFailsExplicitlyOnZeroProgress()
        {
            var bindings = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            var typeName = typeof(string).AssemblyQualifiedName;
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            string cursor = null;
            string firstCursor = null;
            long total = -1;
            var pages = 0;
            do
            {
                var page = UnityCliBridge.ReflectionInvoker.FindMethods(typeName, null, bindings, 7, cursor, Budget(scans: 10000, nodes: 10000, reflections: 10000));
                if (total < 0L) total = page.Total;
                Assert.That(page.Total, Is.EqualTo(total));
                foreach (var method in page.Methods)
                {
                    var signature = method.DeclaringType + "::" + method.Name + "(" + string.Join(",", method.Parameters.Select(item => item.ParameterType).ToArray()) + ")";
                    Assert.That(signatures.Add(signature), Is.True, signature);
                }
                if (pages == 0) firstCursor = page.NextCursor;
                cursor = page.NextCursor;
                pages++;
            } while (cursor != null && pages < 200);

            Assert.That(cursor, Is.Null);
            Assert.That(signatures.Count, Is.EqualTo(total));
            Assert.That(pages, Is.GreaterThan(1));

            var byteFailure = Assert.Throws<UnityCliRequestBudgetExceededException>(() =>
                UnityCliBridge.ReflectionInvoker.FindMethods(typeName, null, bindings, 7, null, Budget(serialized: 1)));
            Assert.That(byteFailure.Dimension, Is.EqualTo("serializedUtf8Bytes"));
            var timeFailure = Assert.Throws<UnityCliRequestBudgetExceededException>(() =>
                UnityCliBridge.ReflectionInvoker.FindMethods(typeName, null, bindings, 7, null, Budget(milliseconds: 3, clock: new AdvancingClock())));
            Assert.That(timeFailure.Dimension, Is.EqualTo("mainThreadMilliseconds"));

            UnityCliReadGeneration.InvalidateReflection();
            var stale = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
                UnityCliBridge.ReflectionInvoker.FindMethods(typeName, null, bindings, 7, firstCursor, Budget(scans: 10000, nodes: 10000, reflections: 10000)));
            Assert.That(stale.Code, Is.EqualTo("E_CONTINUATION_STALE"));
        }

        private static ProfilerMemorySnapshotTakeTool.Result ExecuteTake(
            ProfilerMemorySnapshotTakeTool tool,
            string requestId,
            string path,
            Exception terminalException,
            Action commitEffect = null)
        {
            var waiter = new ImmediateCallbackWaiter(terminalException);
            return tool.ExecuteAsync(
                new ProfilerMemorySnapshotTakeTool.Request { Path = path },
                Context(requestId, waiter, commitEffect ?? (() => { }))).GetAwaiter().GetResult();
        }

        private static UnityCliProjectToolContext Context(string requestId, IUnityCliWaiter waiter, Action commitEffect)
        {
            return new UnityCliProjectToolContext
            {
                RequestId = requestId,
                TimeoutMs = 30000,
                CancellationToken = CancellationToken.None,
                Wait = waiter,
                CommitEffectAction = commitEffect
            };
        }

        private static Exception OutcomeException(string outcome)
        {
            if (outcome == "cancel") return new OperationCanceledException("Canceled for test.");
            if (outcome == "reload") return new UnityCliBridge.BridgeCommandException("E_EDITOR_RELOADING", "Domain reload canceled the callback.", "Retry after reload.");
            return new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Callback wait timed out.", "Retry or reduce scope.");
        }

        private static UnityCliRequestBudget Budget(
            long scans = 10000,
            long nodes = 4096,
            long getters = 4096,
            long reflections = 4096,
            long serialized = 8 * 1024 * 1024,
            long milliseconds = 2000,
            IUnityCliMonotonicClock clock = null)
        {
            return new UnityCliRequestBudget(new UnityCliReadBudgetInput
            {
                MaxScannedRecords = scans,
                MaxNodes = nodes,
                MaxGetters = getters,
                MaxReflectionCalls = reflections,
                MaxSerializedUtf8Bytes = serialized,
                MaxMainThreadMilliseconds = milliseconds
            }, 30000, CancellationToken.None, clock ?? new FixedClock());
        }

        private static string CreateTestRoot(string suffix)
        {
            var parent = Path.GetFullPath(Path.Combine("Library", "UnityCli", "tests"));
            var root = Path.Combine(parent, "issue-042-" + suffix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
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

        private static void AssertInventory(string root, int expectedFiles, long expectedBytes)
        {
            var canonical = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            var files = Directory.Exists(root)
                ? Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
            Assert.That(files.All(path => Path.GetFullPath(path).StartsWith(canonical, StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(files.Length, Is.EqualTo(expectedFiles));
            Assert.That(files.Sum(path => new FileInfo(path).Length), Is.EqualTo(expectedBytes));
        }

        private sealed class ImmediateCallbackWaiter : IUnityCliWaiter
        {
            private readonly Exception terminalException;
            internal ImmediateCallbackWaiter(Exception terminalException) { this.terminalException = terminalException; }

            public UnityCliMainThreadAwaitable<T> ForCallback<T>(Action<Func<Func<T>, bool>, Func<Exception, bool>> start, TimeSpan timeout, CancellationToken ct = default, Action onAbandoned = null)
            {
                var publication = new UnityCliCallbackPublication<T>();
                start(create => publication.TryComplete(create), error => publication.TryFail(error));
                if (terminalException != null && publication.TryAbandon()) onAbandoned?.Invoke();
                bool completed;
                bool failed;
                T value;
                Exception error;
                publication.Read(out completed, out failed, out value, out error);
                if (terminalException != null) error = terminalException;
                else if (!completed && !failed) error = new InvalidOperationException("The fake callback produced no terminal outcome.");
                return new UnityCliMainThreadAwaitable<T>(new ImmediateSource<T>(value, error));
            }

            public UnityCliMainThreadAwaitable<bool> NextFrame(int frames = 1, CancellationToken ct = default) { throw new NotSupportedException(); }
            public UnityCliMainThreadAwaitable<bool> Delay(TimeSpan duration, CancellationToken ct = default) { throw new NotSupportedException(); }
            public UnityCliMainThreadAwaitable<bool> Until(Func<bool> predicate, TimeSpan timeout, CancellationToken ct = default) { throw new NotSupportedException(); }
            public UnityCliMainThreadAwaitable<bool> UntilStable(Func<bool> predicate, TimeSpan timeout, int minimumFrames = 2, TimeSpan minimumDuration = default, CancellationToken ct = default) { throw new NotSupportedException(); }
            public UnityCliMainThreadAwaitable<bool> UntilEditorReady(TimeSpan timeout, CancellationToken ct = default) { throw new NotSupportedException(); }
            public UnityCliMainThreadAwaitable<bool> UntilPlayModeState(PlayModeStateChange state, TimeSpan timeout, CancellationToken ct = default) { throw new NotSupportedException(); }
        }

        private sealed class ImmediateSource<T> : IMainThreadAwaitableSource<T>
        {
            private readonly T value;
            private readonly Exception error;
            internal ImmediateSource(T value, Exception error) { this.value = value; this.error = error; }
            public bool IsCompleted { get { return true; } }
            public void RegisterContinuation(Action continuation) { continuation?.Invoke(); }
            public T GetResult()
            {
                if (error != null) throw error;
                return value;
            }
        }

        private static class FakeMemoryProfiler
        {
            internal static Action<string, Action<string, bool>> OnTake;
            internal static Action<string, bool> Callback;
            internal static string PendingPath;

            public static void TakeSnapshot(string path, Action<string, bool> callback)
            {
                PendingPath = path;
                Callback = callback;
                OnTake?.Invoke(path, callback);
            }

            internal static void Reset()
            {
                OnTake = null;
                Callback = null;
                PendingPath = null;
            }
        }

        private static class UnsupportedMemoryProfiler { }

        private sealed class RecursiveSchemaFixture
        {
            public string Alpha { get; set; }
            public int Beta { get; set; }
            public RecursiveSchemaFixture Next { get; set; }
            public RecursiveSchemaLeaf Leaf { get; set; }
            public Dictionary<string, List<RecursiveSchemaLeaf>> Items { get; set; }
            public RecursiveSchemaLeaf[] Leaves { get; set; }
        }

        private sealed class RecursiveSchemaLeaf
        {
            public string Value { get; set; }
            public RecursiveSchemaFixture Parent { get; set; }
        }

        private sealed class FixedClock : IUnityCliMonotonicClock
        {
            public long ElapsedMilliseconds { get { return 0L; } }
        }

        private sealed class AdvancingClock : IUnityCliMonotonicClock
        {
            private long elapsed;
            public long ElapsedMilliseconds { get { return Interlocked.Increment(ref elapsed); } }
        }
    }
}
#endif
