using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace UnityCli.Tests
{
    /// <summary>
    /// method-hook 的定向逻辑测试：exact 目标解析、有界投影、State 校验与事件 cursor 语义。
    /// 真实 Harmony patch 行为由 Dev 功能矩阵验证，不在本夹具重复。
    /// </summary>
    public sealed class UnityCliMethodHookTests
    {
        private sealed class HookProbeTarget
        {
            public int PublicField = 7;
            private int privateField = 11;

            public int Add(int left, int right) { return left + right; }

            public void Bump(ref int value) { value += 10; }

            private string Secret(int seed) { return "secret-" + seed.ToString(System.Globalization.CultureInfo.InvariantCulture); }

            private static int StaticTag() { return 42; }

            public int AccessPrivateField() { return privateField; }
        }

        private static MethodHookTargetSpec Spec(string methodName, bool isStatic, string returnType, params string[] parameterTypes)
        {
            return new MethodHookTargetSpec
            {
                AssemblyName = typeof(HookProbeTarget).Assembly.GetName().Name,
                TypeName = typeof(HookProbeTarget).FullName,
                MethodName = methodName,
                ParameterTypes = parameterTypes.ToList(),
                ReturnType = returnType,
                IsStatic = isStatic
            };
        }

        [Test]
        public void ExactTargetResolvesPublicPrivateStaticAndByRefSignatures()
        {
            var add = MethodHookTargetResolver.Resolve(Spec("Add", false, "System.Int32", "System.Int32", "System.Int32"));
            Assert.That(add.Method.Name, Is.EqualTo("Add"));
            Assert.That(add.Method.IsStatic, Is.False);

            var secret = MethodHookTargetResolver.Resolve(Spec("Secret", false, "System.String", "System.Int32"));
            Assert.That(secret.Method.IsPrivate, Is.True, "private methods must stay supported targets");

            var staticTag = MethodHookTargetResolver.Resolve(Spec("StaticTag", true, "System.Int32"));
            Assert.That(staticTag.Method.IsStatic, Is.True);

            var bump = MethodHookTargetResolver.Resolve(Spec("Bump", false, "System.Void", "System.Int32&"));
            Assert.That(bump.ParameterSignature, Is.EqualTo("System.Int32&"));
        }

        [Test]
        public void ExactTargetRejectsWrongSignatureAndUnsupportedShapes()
        {
            var notFound = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookTargetResolver.Resolve(Spec("Add", false, "System.Int64", "System.Int32", "System.Int32")));
            Assert.That(notFound.Code, Is.EqualTo("E_HOOK_TARGET_NOT_FOUND"));

            var wrongArity = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookTargetResolver.Resolve(Spec("Add", false, "System.Int32", "System.Int32")));
            Assert.That(wrongArity.Code, Is.EqualTo("E_HOOK_TARGET_NOT_FOUND"));

            var missingType = new MethodHookTargetSpec
            {
                AssemblyName = typeof(HookProbeTarget).Assembly.GetName().Name,
                TypeName = "UnityCli.Tests.DoesNotExist",
                MethodName = "Add",
                ParameterTypes = new List<string>(),
                ReturnType = "System.Void",
                IsStatic = false
            };
            var missing = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookTargetResolver.Resolve(missingType));
            Assert.That(missing.Code, Is.EqualTo("E_HOOK_TARGET_NOT_FOUND"));

            var abstractMethod = typeof(Stream).GetMethod("Read", new[] { typeof(byte[]), typeof(int), typeof(int) });
            Assert.That(abstractMethod, Is.Not.Null);
            Assert.That(MethodHookTargetResolver.UnsupportedReason(abstractMethod), Does.Contain("abstract"));
            var abstractTarget = new MethodHookTargetSpec
            {
                AssemblyName = typeof(Stream).Assembly.GetName().Name,
                TypeName = typeof(Stream).FullName,
                MethodName = "Read",
                ParameterTypes = new List<string> { "System.Byte[]", "System.Int32", "System.Int32" },
                ReturnType = "System.Int32",
                IsStatic = false
            };
            var unsupported = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookTargetResolver.Resolve(abstractTarget));
            Assert.That(unsupported.Code, Is.EqualTo("E_HOOK_METHOD_UNSUPPORTED"));
        }

        [Test]
        public void FieldResolutionWalksBaseTypesAndFailsWithTheFieldName()
        {
            var field = MethodHookTargetResolver.ResolveField(typeof(HookProbeTarget), null, "privateField");
            Assert.That(field.Name, Is.EqualTo("privateField"));
            Assert.That(field.IsPrivate, Is.True);

            var missing = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookTargetResolver.ResolveField(typeof(HookProbeTarget), null, "nope"));
            Assert.That(missing.Message, Does.Contain("nope"));
        }

        [Test]
        public void ProjectionStaysBoundedAndNeverTraversesArbitraryObjects()
        {
            var truncation = new MethodHookProjection.Truncation();
            var huge = new string('x', MethodHookLimits.MaxProjectionStringBytes + 128);
            var projectedString = MethodHookProjection.Project(huge, truncation);
            Assert.That(truncation.Truncated, Is.True);
            Assert.That(projectedString, Is.Not.Null);

            var deep = new Dictionary<string, object>();
            var cursor = deep;
            for (var index = 0; index < MethodHookLimits.MaxProjectionDepth + 4; index++)
            {
                var child = new Dictionary<string, object>();
                cursor["level" + index] = child;
                cursor = child;
            }
            var projection = new MethodHookProjection.Truncation();
            Assert.DoesNotThrow(() => MethodHookProjection.Project(deep, projection));
            Assert.That(projection.Truncated, Is.True);

            var wide = Enumerable.Range(0, MethodHookLimits.MaxProjectionItems + 16).Cast<object>().ToList();
            var wideProjection = new MethodHookProjection.Truncation();
            var projectedWide = MethodHookProjection.Project(wide, wideProjection) as IList<object>;
            Assert.That(projectedWide, Is.Not.Null);
            Assert.That(projectedWide.Count, Is.LessThanOrEqualTo(MethodHookLimits.MaxProjectionItems));
            Assert.That(wideProjection.Truncated, Is.True);

            var delegateValue = new Action(() => { });
            var text = MethodHookProjection.Project(delegateValue, new MethodHookProjection.Truncation()) as string;
            Assert.That(text, Does.StartWith("delegate:"));

            var objectValue = new HookProbeTarget();
            var objectText = MethodHookProjection.Project(objectValue, new MethodHookProjection.Truncation()) as string;
            Assert.That(objectText, Does.StartWith("object:"), "arbitrary objects must be projected by type without running user code");
        }

        [Test]
        public void StateBagAcceptsOnlyBoundedJsonValues()
        {
            var bag = new MethodHookStateBag("hook-test");
            bag["counter"] = 3;
            bag["label"] = "run";
            bag["items"] = new List<object> { 1, "two", true };
            bag["nested"] = new Dictionary<string, object> { { "a", 1 } };
            Assert.That(bag.Count, Is.EqualTo(4));

            var stateFailure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => bag["bad"] = new Action(() => { }));
            Assert.That(stateFailure.Code, Is.EqualTo("E_SCHEMA_INVALID"));

            var typeFailure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => bag["type"] = typeof(HookProbeTarget));
            Assert.That(typeFailure.Code, Is.EqualTo("E_SCHEMA_INVALID"));

            var depth = new Dictionary<string, object>();
            var cursor = depth;
            for (var index = 0; index < MethodHookLimits.MaxStateDepth + 3; index++)
            {
                var child = new Dictionary<string, object>();
                cursor["level" + index] = child;
                cursor = child;
            }
            var depthFailure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => bag["deep"] = depth);
            Assert.That(depthFailure.Code, Is.EqualTo("E_HOOK_LIMIT_EXCEEDED"));

            var byteFailure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => bag["huge"] = new string('y', MethodHookLimits.MaxStateBytes));
            Assert.That(byteFailure.Code, Is.EqualTo("E_HOOK_LIMIT_EXCEEDED"));
            Assert.That(bag.ContainsKey("huge"), Is.False, "a rejected write must not leave a partial entry");
        }

        [Test]
        public void EventBufferAssignsSequencesTrimsOldestAndReportsGaps()
        {
            var buffer = new MethodHookEventBuffer();
            for (var index = 0; index < 5; index++) buffer.Append(Record("hook-a", "prefix", index));
            Assert.That(buffer.LastSequence, Is.EqualTo(5));
            Assert.That(buffer.Count, Is.EqualTo(5));

            var page = buffer.Read(new MethodHookEventFilter(), null, "session-test", 3);
            Assert.That(page.Records.Select(record => record.Sequence), Is.EqualTo(new long[] { 3, 4, 5 }));
            Assert.That(page.HasMore, Is.False);
            Assert.That(page.NextScannedSequence, Is.EqualTo(5));

            var filtered = buffer.Read(new MethodHookEventFilter { Kind = "postfix" }, null, "session-test", 10);
            Assert.That(filtered.Records, Is.Empty);

            buffer.RecordGap(10, 12, "checkpoint-missing");
            Assert.That(buffer.Gaps.Count, Is.EqualTo(1));
            Assert.That(buffer.Gaps[0].FromSequence, Is.EqualTo(10));
        }

        [Test]
        public void EventCursorContinuesAfterTheLastScannedSequenceAndRejectsStaleScopes()
        {
            var buffer = new MethodHookEventBuffer();
            for (var index = 0; index < 6; index++) buffer.Append(Record("hook-a", index % 2 == 0 ? "prefix" : "custom", index));
            var filter = new MethodHookEventFilter { HookId = "hook-a" };
            var start = buffer.BeginCursor(filter, "session-test");

            buffer.Append(Record("hook-a", "prefix", 6));
            var first = buffer.Read(filter, start, "session-test", 2);
            Assert.That(first.Records.Count, Is.EqualTo(1), "only the newly appended record is after the install cursor");
            Assert.That(first.Records[0].Sequence, Is.EqualTo(7));
            Assert.That(first.HasMore, Is.False);

            var next = new MethodHookEventCursor { EditorSessionId = "session-test", ScopeHash = filter.ScopeKey("session-test"), ScannedSequence = first.NextScannedSequence }.Encode();
            var second = buffer.Read(filter, next, "session-test", 2);
            Assert.That(second.Records, Is.Empty, "a cursor that already scanned the tail must not repeat records");

            var otherFilter = new MethodHookEventFilter { Kind = "custom" };
            var changedFilter = Assert.Throws<MethodHookCursorException>(() => buffer.Read(otherFilter, next, "session-test", 2));
            Assert.That(changedFilter.Code, Is.EqualTo("E_CONTINUATION_STALE"));

            var otherSession = Assert.Throws<MethodHookCursorException>(() => buffer.Read(filter, next, "session-other", 2));
            Assert.That(otherSession.Code, Is.EqualTo("E_CONTINUATION_STALE"));
        }

        [Test]
        public void EventCursorOlderThanTheRetainedWindowFailsWithTheReadableStart()
        {
            var seeded = new MethodHookEventBuffer();
            for (var index = 0; index < 6; index++) seeded.Append(Record("hook-a", "prefix", index));
            var retained = seeded.Snapshot().Skip(3).ToList();

            var buffer = new MethodHookEventBuffer();
            buffer.Restore(retained, 6, null);
            Assert.That(buffer.OldestAvailableSequence, Is.EqualTo(3), "the readable start follows the oldest retained record");

            var page = buffer.Read(new MethodHookEventFilter(), null, "session-test", 2);
            Assert.That(page.Records.Select(record => record.Sequence), Is.EqualTo(new long[] { 5, 6 }));

            var expired = new MethodHookEventCursor { EditorSessionId = "session-test", ScopeHash = new MethodHookEventFilter().ScopeKey("session-test"), ScannedSequence = 0 }.Encode();
            var failure = Assert.Throws<MethodHookCursorException>(() => buffer.Read(new MethodHookEventFilter(), expired, "session-test", 2));
            Assert.That(failure.Code, Is.EqualTo("E_HOOK_EVENT_CURSOR_EXPIRED"));
        }

        [Test]
        public void SessionScopedStoreKeysRoundTripWithinOneEditorStart()
        {
            var previousFloor = MethodHookStore.EventFloor();
            var previousSuppression = MethodHookStore.LoadSuppression();
            try
            {
                MethodHookStore.SetEventFloor(4321);
                Assert.That(MethodHookStore.EventFloor(), Is.EqualTo(4321));
                MethodHookStore.SaveSuppression(new Dictionary<string, string> { { "hook-1", "state save failed" } });
                var suppression = MethodHookStore.LoadSuppression();
                Assert.That(suppression.ContainsKey("hook-1"), Is.True);
                Assert.That(suppression["hook-1"], Does.Contain("state save failed"));
            }
            finally
            {
                MethodHookStore.SetEventFloor(previousFloor);
                MethodHookStore.SaveSuppression(previousSuppression);
            }
        }

        [Test]
        public void EventCheckpointIsBoundedAndReportsTruncation()
        {
            var snapshot = new List<Dictionary<string, object>>();
            var payload = new string('x', 2048);
            for (var index = 0; index < 8000; index++)
            {
                snapshot.Add(new Dictionary<string, object>
                {
                    { "sequence", (long)(index + 1) }, { "hookId", "hook-checkpoint" }, { "kind", "prefix" },
                    { "value", payload },
                    { "fields", new Dictionary<string, object> { { "f0", payload } } }
                });
            }

            int dropped;
            var kept = MethodHookHost.BuildBoundedEventCheckpoint(snapshot, out dropped);
            Assert.That(kept.Count, Is.GreaterThan(0), "the newest events must always be representable");
            Assert.That(dropped, Is.EqualTo(snapshot.Count - kept.Count));
            Assert.That(dropped, Is.GreaterThan(0), "an oversized ring must be truncated instead of failing the whole checkpoint");

            long bytes = 0;
            foreach (var entry in kept) bytes += System.Text.Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(entry));
            Assert.That(bytes, Is.LessThanOrEqualTo(MethodHookLimits.MaxCheckpointEventBytes), "the checkpoint must stay inside its byte budget");
            Assert.That((long)kept[kept.Count - 1]["sequence"], Is.EqualTo((long)snapshot[snapshot.Count - 1]["sequence"]), "the newest event must be retained");
            Assert.That((long)kept[0]["sequence"], Is.EqualTo((long)snapshot[snapshot.Count - kept.Count]["sequence"]), "the oldest retained event must be contiguous with the tail");
        }

        [Test]
        public void ReadBudgetMemberCountsMatchThePublicDtos()
        {
            Assert.That(UnityCliSchemaDeriver.SerializableMembers(typeof(MethodHookEventItem)).Count, Is.EqualTo(MethodHookLimits.EventItemMembers),
                "the events read budget is derived from EventItemMembers and must track the DTO");
            Assert.That(UnityCliSchemaDeriver.SerializableMembers(typeof(MethodHookListItem)).Count, Is.EqualTo(MethodHookLimits.ListItemMembers),
                "the list read budget is derived from ListItemMembers and must track the DTO");
            Assert.That(MethodHookLimits.EventPageGetters(MethodHookLimits.EventsMaxLimit), Is.LessThanOrEqualTo(UnityCliRequestBudget.AbsoluteMaxGetters),
                "a full events page must stay inside the absolute read budget");
            Assert.That(MethodHookLimits.ListPageGetters(MethodHookLimits.ListMaxLimit), Is.LessThanOrEqualTo(UnityCliRequestBudget.AbsoluteMaxGetters),
                "a full list page must stay inside the absolute read budget");
        }

        [Test]
        public void LimitsSummaryStaysSingleSourcedAndBounded()
        {
            var summary = MethodHookLimits.Summary();
            Assert.That(summary, Does.Contain(MethodHookLimits.MaxDefinitionsPerSession.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(summary, Does.Contain(MethodHookLimits.MaxCompiledArtifacts.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(summary, Does.Contain(MethodHookLimits.RecoveryBudgetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static MethodHookEventRecord Record(string hookId, string kind, int payload)
        {
            return new MethodHookEventRecord
            {
                HookId = hookId,
                Revision = 1,
                Kind = kind,
                PlaySessionId = "play-1",
                ClockSegment = "play-1:seg-1",
                Frame = payload,
                Name = kind,
                Value = payload
            };
        }
    }
}
