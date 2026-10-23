using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli.Tests
{
    public sealed class UnityCliContinuationIntegrationTests
    {
        private const string PrefabFolder = "Assets/UnityCliTests";
        private const string PrefabPath = PrefabFolder + "/issue-042-continuation.prefab";

        [TearDown]
        public void TearDown()
        {
            FrameDebuggerReflection.ResetTestOverrides();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && string.Equals(PrefabToolSupport.StageAssetPath(stage), PrefabPath, StringComparison.OrdinalIgnoreCase))
            {
                PrefabToolSupport.SavePrefabStage(stage);
                PrefabToolSupport.ClosePrefabStage();
            }
            AssetDatabase.DeleteAsset(PrefabPath);
            if (AssetDatabase.IsValidFolder(PrefabFolder) && AssetDatabase.FindAssets(string.Empty, new[] { PrefabFolder }).Length == 0)
                AssetDatabase.DeleteAsset(PrefabFolder);
        }

        [Test]
        public void FrameDebuggerPagesEntireControlledCaptureAndRejectsChangedDeletedOrUnknownPositions()
        {
            const int eventCount = 1203;
            var capture = "capture-a";
            var currentCount = eventCount;
            FrameDebuggerReflection.AvailableOverrideForTests = true;
            FrameDebuggerReflection.EnabledOverrideForTests = () => true;
            FrameDebuggerReflection.EventCountOverrideForTests = () => currentCount;
            FrameDebuggerReflection.EventNameOverrideForTests = index => "event-" + index.ToString("D4", CultureInfo.InvariantCulture);
            FrameDebuggerReflection.EventDataOverrideForTests = index => null;
            FrameDebuggerReflection.GenerationOverrideForTests = () => capture;

            var tool = new ProfilerFrameDebuggerEventsTool();
            var indices = new List<long>();
            string cursor = null;
            string firstCursor = null;
            string generation = null;
            var pageCount = 0;
            do
            {
                var page = tool.ExecuteAsync(
                    new ProfilerFrameDebuggerEventsTool.Request { PageSize = 500, Cursor = cursor },
                    Context("issue-042-frame-" + pageCount.ToString(CultureInfo.InvariantCulture))).GetAwaiter().GetResult();
                pageCount++;
                Assert.That(page.TotalEvents, Is.EqualTo(eventCount));
                Assert.That(page.PageInfo.Cursor, Is.EqualTo(cursor));
                Assert.That(page.PageInfo.ReturnedCount, Is.EqualTo(page.Events.Count));
                Assert.That(page.Budget.ScannedRecords, Is.LessThanOrEqualTo(500));
                Assert.That(page.Budget.Nodes, Is.LessThanOrEqualTo(500));
                if (generation == null) generation = page.PageInfo.Generation;
                else Assert.That(page.PageInfo.Generation, Is.EqualTo(generation));
                indices.AddRange(page.Events.Select(item => item.Index));
                cursor = page.PageInfo.NextCursor;
                if (firstCursor == null) firstCursor = cursor;
            } while (cursor != null);

            CollectionAssert.AreEqual(Enumerable.Range(0, eventCount).Select(index => (long)index).ToArray(), indices.ToArray());
            Assert.That(pageCount, Is.EqualTo(3));
            Assert.That(indices.Distinct().Count(), Is.EqualTo(eventCount));

            capture = "capture-b";
            AssertStale(tool, firstCursor, "issue-042-frame-generation");
            capture = "capture-a";
            currentCount--;
            AssertStale(tool, firstCursor, "issue-042-frame-deleted");
            currentCount = eventCount;
            var unknown = UnityCliContinuationToken.Create("profiler.frame-debugger-events", generation, "unknown");
            AssertStale(tool, unknown, "issue-042-frame-unknown");
        }

        [Test]
        public void FrameDebuggerHonorsDeadlineAndCancellationBeforePublishingPartialEvents()
        {
            var clock = new ManualClock();
            FrameDebuggerReflection.AvailableOverrideForTests = true;
            FrameDebuggerReflection.EventCountOverrideForTests = () => 10;
            FrameDebuggerReflection.GenerationOverrideForTests = () => "capture-deadline";
            FrameDebuggerReflection.EventNameOverrideForTests = index =>
            {
                clock.Advance(2);
                return "event-" + index.ToString(CultureInfo.InvariantCulture);
            };
            FrameDebuggerReflection.EventDataOverrideForTests = index => null;
            var budget = new UnityCliRequestBudget(
                new UnityCliReadBudgetInput { MaxMainThreadMilliseconds = 1 },
                30000,
                CancellationToken.None,
                clock);
            var deadline = Assert.Throws<UnityCliRequestBudgetExceededException>(() => ProfilerFrameDebuggerEventsTool.ReadPage(
                new ProfilerFrameDebuggerEventsTool.Request { PageSize = 5 }, budget));
            Assert.That(deadline.Dimension, Is.EqualTo("mainThreadMilliseconds"));

            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => new ProfilerFrameDebuggerEventsTool().ExecuteAsync(
                new ProfilerFrameDebuggerEventsTool.Request { PageSize = 5 },
                Context("issue-042-frame-cancel", cancellation.Token)).GetAwaiter().GetResult());
        }

        [Test]
        public void ConcurrentFramePagesUseIndependentBudgetsAndRetainNoSnapshotState()
        {
            FrameDebuggerReflection.AvailableOverrideForTests = true;
            FrameDebuggerReflection.EventCountOverrideForTests = () => 64;
            FrameDebuggerReflection.EventNameOverrideForTests = index => "event-" + index.ToString(CultureInfo.InvariantCulture);
            FrameDebuggerReflection.EventDataOverrideForTests = index => null;
            FrameDebuggerReflection.GenerationOverrideForTests = () => "capture-concurrent";
            var pages = new ProfilerFrameDebuggerEventsTool.Result[8];

            Parallel.For(0, pages.Length, index =>
            {
                var budget = new UnityCliRequestBudget(null, 30000, CancellationToken.None);
                pages[index] = ProfilerFrameDebuggerEventsTool.ReadPage(
                    new ProfilerFrameDebuggerEventsTool.Request { PageSize = 7 }, budget);
            });

            Assert.That(pages.All(page => page.Events.Count == 7), Is.True);
            Assert.That(pages.All(page => page.Events.Select(item => item.Index).SequenceEqual(Enumerable.Range(0, 7).Select(value => (long)value))), Is.True);
            Assert.That(pages.Select(page => page.PageInfo.NextCursor).Distinct().Count(), Is.EqualTo(1));
            Assert.That(pages.All(page => page.Budget.ScannedRecords == 7 && page.Budget.Nodes == 7), Is.True);
        }

        [Test]
        public void PrefabStagePagesWithoutDuplicatesAndRejectsGenerationOrDeletedBoundary()
        {
            CreatePrefabFixture(17);
            Assert.That(PrefabToolSupport.OpenPrefabStage(PrefabPath), Is.True);
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Assert.That(stage, Is.Not.Null);
            Assert.That(stage.prefabContentsRoot, Is.Not.Null);

            var tool = new GameObjectFindTool();
            var ids = new List<long>();
            string cursor = null;
            string firstCursor = null;
            do
            {
                var page = tool.ExecuteAsync(new GameObjectFindTool.Request
                {
                    SearchTerm = "issue-042-match",
                    SearchMethod = "by_name",
                    IncludeInactive = true,
                    PreferPrefabStage = true,
                    PageSize = 4,
                    Cursor = cursor
                }, Context("issue-042-prefab-page")).GetAwaiter().GetResult();
                Assert.That(page.PrefabStageOpen, Is.True);
                Assert.That(page.PageInfo.Cursor, Is.EqualTo(cursor));
                ids.AddRange(page.Items.Select(item => item.Id));
                cursor = page.PageInfo.NextCursor;
                if (firstCursor == null) firstCursor = cursor;
            } while (cursor != null);

            Assert.That(ids.Count, Is.EqualTo(17));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(17));

            UnityCliReadGeneration.InvalidateHierarchy();
            AssertGoFindStale(tool, firstCursor, "issue-042-prefab-generation");

            var fresh = tool.ExecuteAsync(new GameObjectFindTool.Request
            {
                SearchTerm = "issue-042-match",
                SearchMethod = "by_name",
                IncludeInactive = true,
                PreferPrefabStage = true,
                PageSize = 1
            }, Context("issue-042-prefab-deleted-first")).GetAwaiter().GetResult();
            string position;
            Assert.That(UnityCliContinuationToken.TryParse(
                fresh.PageInfo.NextCursor, "game-object.find", fresh.PageInfo.Generation, out position), Is.True);
            var separator = position.IndexOf(':');
            int instanceId;
            Assert.That(separator, Is.GreaterThan(0));
            Assert.That(int.TryParse(position.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId), Is.True);
            var deletedBoundary = EditorUtility.InstanceIDToObject(instanceId);
            Assert.That(deletedBoundary, Is.Not.Null);
            UnityEngine.Object.DestroyImmediate(deletedBoundary);
            AssertGoFindStale(tool, fresh.PageInfo.NextCursor, "issue-042-prefab-deleted");
        }

        [Test]
        public void ExactIdSelectorDoesNotScanLargeSceneAndUnknownIdsReturnEmpty()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                GameObject target = null;
                for (var index = 0; index < 1000; index++)
                {
                    var value = new GameObject("issue-042-exact-" + index.ToString("D4", CultureInfo.InvariantCulture));
                    SceneManager.MoveGameObjectToScene(value, scene);
                    if (index == 777) target = value;
                }
                var tool = new GameObjectFindTool();
                var found = tool.ExecuteAsync(new GameObjectFindTool.Request
                {
                    SearchTerm = target.GetInstanceID().ToString(CultureInfo.InvariantCulture),
                    SearchMethod = "by_id",
                    IncludeInactive = true,
                    Budget = new UnityCliReadBudgetInput { MaxScannedRecords = 1 }
                }, Context("issue-042-exact-found")).GetAwaiter().GetResult();
                Assert.That(found.ReturnedCount, Is.EqualTo(1));
                Assert.That(found.Items.Single().Id, Is.EqualTo(target.GetInstanceID()));
                Assert.That(found.Budget.ScannedRecords, Is.Zero);

                var missing = tool.ExecuteAsync(new GameObjectFindTool.Request
                {
                    SearchTerm = int.MaxValue.ToString(CultureInfo.InvariantCulture),
                    SearchMethod = "by_id",
                    IncludeInactive = true,
                    Budget = new UnityCliReadBudgetInput { MaxScannedRecords = 1 }
                }, Context("issue-042-exact-missing")).GetAwaiter().GetResult();
                Assert.That(missing.ReturnedCount, Is.Zero);
                Assert.That(missing.PageInfo.Complete, Is.True);
                Assert.That(missing.Budget.ScannedRecords, Is.Zero);
            }
            finally
            {
                Assert.That(EditorSceneManager.ClosePreviewScene(scene), Is.True);
            }
        }

        private static void AssertStale(ProfilerFrameDebuggerEventsTool tool, string cursor, string requestId)
        {
            var exception = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => tool.ExecuteAsync(
                new ProfilerFrameDebuggerEventsTool.Request { PageSize = 500, Cursor = cursor }, Context(requestId)).GetAwaiter().GetResult());
            Assert.That(exception.Code, Is.EqualTo("E_CONTINUATION_STALE"));
        }

        private static void AssertGoFindStale(GameObjectFindTool tool, string cursor, string requestId)
        {
            var exception = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => tool.ExecuteAsync(new GameObjectFindTool.Request
            {
                SearchTerm = "issue-042-match",
                SearchMethod = "by_name",
                IncludeInactive = true,
                PreferPrefabStage = true,
                PageSize = 4,
                Cursor = cursor
            }, Context(requestId)).GetAwaiter().GetResult());
            Assert.That(exception.Code, Is.EqualTo("E_CONTINUATION_STALE"));
        }

        private static UnityCliProjectToolContext Context(string requestId, CancellationToken cancellationToken = default)
        {
            return new UnityCliProjectToolContext { RequestId = requestId, TimeoutMs = 30000, CancellationToken = cancellationToken };
        }

        private static void CreatePrefabFixture(int childCount)
        {
            if (!AssetDatabase.IsValidFolder(PrefabFolder)) AssetDatabase.CreateFolder("Assets", "UnityCliTests");
            AssetDatabase.DeleteAsset(PrefabPath);
            var root = new GameObject("issue-042-prefab-root");
            try
            {
                for (var index = 0; index < childCount; index++)
                {
                    var child = new GameObject("issue-042-match");
                    child.transform.SetParent(root.transform, false);
                    child.SetActive(index % 3 != 0);
                }
                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, PrefabPath), Is.Not.Null);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private sealed class ManualClock : IUnityCliMonotonicClock
        {
            public long ElapsedMilliseconds { get; private set; }
            internal void Advance(long milliseconds) { ElapsedMilliseconds += milliseconds; }
        }
    }
}
