#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using NUnit.Framework;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli.Tests
{
    /// <summary>
    /// debug.thread.list、debug.value.read 与 debug.value.write 的引擎行为测试。
    ///
    /// 覆盖：安全投影、字段路径解析与拒绝、值类型副本链写回、句柄弱引用与失效、句柄预算、
    /// 命名逻辑时钟、四种时钟溯源与线程观察范围声明。
    /// tool 层需要 open session 的路径属于 Live 验收范围，本文件只验证不依赖 session 定义的引擎语义。
    /// </summary>
    public sealed class DebugThreadValueTests
    {
        /// <summary>静态构造函数的可观察副作用；它属于本测试类，因此读取它不会触发被测类型的类型初始化。</summary>
        private static bool probeTypeInitialized;

        /// <summary>
        /// 恢复进程级全局现场：命名逻辑时钟表是同一 domain 内共享的，测试发布的时钟
        /// 会继续出现在真实的 debug.value.read 结果里并占用有限名额，因此每个用例结束后必须清空。
        /// </summary>
        [TearDown]
        public void ReleaseGlobalClockState()
        {
            DebugLogicalClocks.Clear();
        }

        private static UnityCliRequestBudget Budget()
        {
            return new UnityCliRequestBudget(null, 30000, CancellationToken.None);
        }

        private static BridgeCommandException Reject(Func<DebugValuePlan> action)
        {
            return Assert.Throws<BridgeCommandException>(() => action());
        }

        // ------------------------------------------------------------------
        // 安全投影
        // ------------------------------------------------------------------

        [Test]
        public void ProjectionReportsArbitraryReferencesByIdentityAndBoundsStrings()
        {
            string category;
            bool truncated;
            var identity = DebugValueProjection.Project(new NoInitializerProbe(), out category, out truncated);
            Assert.That(category, Is.EqualTo(DebugValueProjection.IdentityOnly), "an arbitrary reference must not be serialised");
            var marker = identity as Dictionary<string, object>;
            Assert.That(marker, Is.Not.Null);
            Assert.That(marker["suppressed"], Is.EqualTo("no-user-code-projection"));
            Assert.That(truncated, Is.False);

            var longText = new string('x', DebugLimits.MaxProjectionStringBytes * 2);
            var projected = DebugValueProjection.Project(longText, out category, out truncated) as string;
            Assert.That(category, Is.EqualTo(DebugValueProjection.Bounded));
            Assert.That(truncated, Is.True, "a string beyond the declared projection budget must be marked truncated");
            Assert.That(projected.Length, Is.EqualTo(DebugLimits.MaxProjectionStringBytes));
        }

        [Test]
        public void UserEnumerableIsNotEnumeratedByTheProjection()
        {
            string category;
            bool truncated;
            var projected = DebugValueProjection.Project(new CountingEnumerable(), out category, out truncated);
            Assert.That(category, Is.EqualTo(DebugValueProjection.IdentityOnly), "a user IEnumerable must not be walked");
            Assert.That(CountingEnumerable.Enumerations, Is.EqualTo(0), "the projection must never run a user enumerator");
            Assert.That(projected, Is.InstanceOf<Dictionary<string, object>>());
        }

        [Test]
        public void FrameworkContainerAndArrayAreProjectedWithTheDeclaredBounds()
        {
            string category;
            bool truncated;
            var list = new List<int>();
            for (var index = 0; index < DebugLimits.MaxProjectionItems + 5; index++) list.Add(index);
            var projected = DebugValueProjection.Project(list, out category, out truncated) as List<object>;
            Assert.That(category, Is.EqualTo(DebugValueProjection.Bounded));
            Assert.That(projected, Is.Not.Null);
            Assert.That(projected.Count, Is.EqualTo(DebugLimits.MaxProjectionItems));
            Assert.That(truncated, Is.True);
        }

        // ------------------------------------------------------------------
        // 根与路径解析
        // ------------------------------------------------------------------

        [Test]
        public void AFieldPathLongerThanTheDeclaredBudgetIsRejectedBeforeAnyRead()
        {
            var path = new List<string>();
            for (var index = 0; index <= DebugLimits.MaxValuePathLength; index++) path.Add("value");
            var failure = Reject(() => ResolveStatic(rootKind: "static-field", type: typeof(NoInitializerProbe), field: "counter", path: path));
            Assert.That(failure.Code, Is.EqualTo("E_DEBUG_LIMIT_EXCEEDED"));
            var details = failure.Details as Dictionary<string, object>;
            Assert.That(details["limit"], Is.EqualTo("value-path-segments"));
            Assert.That((long)details["max"], Is.EqualTo((long)DebugLimits.MaxValuePathLength));
        }

        [Test]
        public void APropertyInThePathIsRefusedBecauseAPureReadMustNotRunAGetter()
        {
            // 路径分段在上一分段声明的类型上解析：根必须是声明了属性成员的实例引用，分段才会落在真正的属性上。
            NoInitializerProbe.Singleton = new NoInitializerProbe();
            var failure = Reject(() => ResolveStatic(rootKind: "static-field", type: typeof(NoInitializerProbe), field: "Singleton", path: new List<string> { "GetterOnly" }));
            Assert.That(failure.Code, Is.EqualTo("E_DEBUG_SCOPE_UNSUPPORTED"));
            Assert.That((failure.Details as Dictionary<string, object>)["rule"], Is.EqualTo("property-getter-not-executed"));
            Assert.That(string.Join(" ", failure.NextActions), Does.Contain("debug.code.execute"), "the refusal must route to explicit code execution");
            Assert.That(NoInitializerProbe.GetterCalls, Is.EqualTo(0), "the getter must never run");
        }

        [Test]
        public void AMissingMemberIsRefusedWithTheExactFieldRule()
        {
            var failure = Reject(() => ResolveStatic(rootKind: "static-field", type: typeof(NoInitializerProbe), field: "counter", path: new List<string> { "missingMember" }));
            Assert.That(failure.Code, Is.EqualTo("E_DEBUG_SCOPE_UNSUPPORTED"));
            Assert.That((failure.Details as Dictionary<string, object>)["rule"], Is.EqualTo("member-not-a-field"));
        }

        [Test]
        public void AStaticFieldOnATypeWithAStaticConstructorIsRefusedInsteadOfTriggeringTypeInitialisation()
        {
            Assert.That(typeof(StaticConstructorProbe).TypeInitializer, Is.Not.Null, "this fixture must declare a static constructor for the rule to apply");
            var failure = Reject(() => ResolveStatic(rootKind: "static-field", type: typeof(StaticConstructorProbe), field: "initialized", path: null));
            Assert.That(failure.Code, Is.EqualTo("E_DEBUG_SCOPE_UNSUPPORTED"));
            Assert.That((failure.Details as Dictionary<string, object>)["rule"], Is.EqualTo("static-type-not-initialized"));
            Assert.That(probeTypeInitialized, Is.False, "the refusal must not have run the type initialiser");
        }

        [Test]
        public void AnArraySegmentMustBeAnIndexAndElementReadDoesNotRunUserCode()
        {
            NoInitializerProbe.samples = new int[3] { 1, 2, 3 };
            var plan = ResolveStatic(rootKind: "static-field", type: typeof(NoInitializerProbe), field: "samples", path: new List<string> { "1" });
            object value;
            string failure;
            string nullAtPath;
            Assert.That(DebugValueSupport.TryReadPlanValue(plan, out value, out failure, out nullAtPath), Is.True, failure);
            Assert.That(value, Is.EqualTo(2));
            Assert.That(plan.LeafType, Is.EqualTo(typeof(int)));

            var refused = Reject(() => ResolveStatic(rootKind: "static-field", type: typeof(NoInitializerProbe), field: "samples", path: new List<string> { "Length" }));
            Assert.That((refused.Details as Dictionary<string, object>)["rule"], Is.EqualTo("array-index-required"));
        }

        [Test]
        public void AnIntermediateNullIsReportedAsANullValueRatherThanAStaleTarget()
        {
            NoInitializerProbe.Singleton = new NoInitializerProbe { Nullable = null };
            var plan = ResolveStatic(rootKind: "static-field", type: typeof(NoInitializerProbe), field: "Singleton", path: new List<string> { "Nullable", "value" });
            object value;
            string failure;
            string nullAtPath;
            Assert.That(DebugValueSupport.TryReadPlanValue(plan, out value, out failure, out nullAtPath), Is.True, failure);
            Assert.That(value, Is.Null);
            Assert.That(nullAtPath, Is.EqualTo("Nullable"), "the path where the chain became null must be reported");
        }

        // ------------------------------------------------------------------
        // 值类型副本链写入
        // ------------------------------------------------------------------

        [Test]
        public void WritingThroughAValueTypePathIsWrittenBackAndNeverSilentlyLost()
        {
            var probe = new NoInitializerProbe();
            NoInitializerProbe.Singleton = probe;
            var plan = ResolveStatic(rootKind: "static-field", type: typeof(NoInitializerProbe), field: "Singleton", path: new List<string> { "outer", "middle", "depth" });
            Assert.That(plan.StructWriteBack, Is.True, "an intermediate value type must be reported as an explicit write-back chain");

            string failure;
            Assert.That(DebugValueSupport.TryWritePlanValue(plan, 42, out failure), Is.True, failure);
            Assert.That(probe.outer.middle.depth, Is.EqualTo(42), "the boxed copy chain must reach the real field");

            object value;
            string readFailure;
            string nullAtPath;
            Assert.That(DebugValueSupport.TryReadPlanValue(plan, out value, out readFailure, out nullAtPath), Is.True, readFailure);
            Assert.That(value, Is.EqualTo(42));
        }

        [Test]
        public void AWriteRejectsAValueThatDoesNotMatchTheDeclaredFieldType()
        {
            var probe = new NoInitializerProbe();
            NoInitializerProbe.Singleton = probe;
            var plan = ResolveStatic(rootKind: "static-field", type: typeof(NoInitializerProbe), field: "Singleton", path: new List<string> { "outer", "middle", "depth" });
            string failure;
            Assert.That(DebugValueSupport.TryWritePlanValue(plan, "not-an-int", out failure), Is.False);
            Assert.That(failure, Does.Contain("does not match"));
            Assert.That(probe.outer.middle.depth, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------
        // 写入的 effect 前判定
        // ------------------------------------------------------------------

        /// <summary>
        /// const 字段不可写。它必须在 effect barrier 之前被拒绝：越过 barrier 后才发现写入不可能，
        /// 会让调用方误以为状态可能已变化。ctx 记录了 barrier 的提交动作，断言它从未被触发。
        /// </summary>
        [Test]
        public void AConstantFieldIsRefusedBeforeTheEffectBarrier()
        {
            var commits = 0;
            var ctx = new UnityCliProjectToolContext { TimeoutMs = 30000, CommitEffectAction = () => commits++ };
            var request = WriteRequest(typeof(NoInitializerProbe).FullName, "MaxRecursionDepth", null, 5);

            var failure = Assert.Throws<BridgeCommandException>(() => DebugValueSupport.ValidateWrite(request, ctx.UseReadBudget()));
            Assert.That(failure.Code, Is.EqualTo("E_DEBUG_FIELD_NOT_WRITABLE"));
            Assert.That((failure.Details as Dictionary<string, object>)["rule"], Is.EqualTo("field-is-literal"));
            Assert.That(ctx.EffectCommitted, Is.False, "a refused write must not have crossed the effect barrier");
            Assert.That(commits, Is.EqualTo(0), "the commit action must never run for a refused write");
            Assert.That(NoInitializerProbe.MaxRecursionDepth, Is.EqualTo(8), "the constant must be untouched");
            Assert.That(string.Join(" ", failure.NextActions), Does.Not.Contain("debug.code.execute"), "explicit code execution cannot write a constant either");
        }

        /// <summary>init-only（readonly）字段同样不是可变状态，必须在 effect 之前拒绝。</summary>
        [Test]
        public void AnInitOnlyFieldIsRefusedBeforeTheEffectBarrier()
        {
            var probe = new NoInitializerProbe();
            NoInitializerProbe.Singleton = probe;
            var commits = 0;
            var ctx = new UnityCliProjectToolContext { TimeoutMs = 30000, CommitEffectAction = () => commits++ };
            var request = WriteRequest(typeof(NoInitializerProbe).FullName, "Singleton", new List<string> { "ReadOnlyValue" }, 9);

            var failure = Assert.Throws<BridgeCommandException>(() => DebugValueSupport.ValidateWrite(request, ctx.UseReadBudget()));
            Assert.That(failure.Code, Is.EqualTo("E_DEBUG_FIELD_NOT_WRITABLE"));
            Assert.That((failure.Details as Dictionary<string, object>)["rule"], Is.EqualTo("field-is-init-only"));
            Assert.That(ctx.EffectCommitted, Is.False);
            Assert.That(commits, Is.EqualTo(0));
            Assert.That(probe.ReadOnlyValue, Is.EqualTo(3), "the init-only field must be untouched");
        }

        /// <summary>路径中间成员为 null 时写入必然不可能，必须在 effect 之前以其自身规则拒绝。</summary>
        [Test]
        public void ANullIntermediateMemberIsRefusedBeforeTheEffectBarrier()
        {
            NoInitializerProbe.Singleton = new NoInitializerProbe { Nullable = null };
            var ctx = new UnityCliProjectToolContext { TimeoutMs = 30000 };
            var request = WriteRequest(typeof(NoInitializerProbe).FullName, "Singleton", new List<string> { "Nullable", "value" }, 7);

            var failure = Assert.Throws<BridgeCommandException>(() => DebugValueSupport.ValidateWrite(request, ctx.UseReadBudget()));
            Assert.That(failure.Code, Is.EqualTo("E_DEBUG_SCOPE_UNSUPPORTED"));
            Assert.That((failure.Details as Dictionary<string, object>)["rule"], Is.EqualTo("path-member-null"));
            Assert.That(ctx.EffectCommitted, Is.False);
        }

        /// <summary>
        /// expectedValue 前置条件属于 effect 前阶段：判定必须在 barrier 之前完成，结果既不是超时也不是部分写入。
        /// </summary>
        [Test]
        public void AGuardMismatchIsDecidedBeforeTheEffectBarrierAndWritesNothing()
        {
            var probe = new NoInitializerProbe();
            NoInitializerProbe.Singleton = probe;
            var ctx = new UnityCliProjectToolContext { TimeoutMs = 30000 };
            var request = WriteRequest(typeof(NoInitializerProbe).FullName, "Singleton", new List<string> { "value" }, 99);
            request.GuardByExpectedValue = true;
            request.ExpectedValue = 4242;

            var plan = DebugValueSupport.ValidateWrite(request, ctx.UseReadBudget());
            Assert.That(plan.GuardEvaluated, Is.True, "the guard must be evaluated during the pre-effect phase");
            Assert.That(plan.GuardSatisfied, Is.False, "a mismatch must be known before the effect barrier");
            Assert.That(ctx.EffectCommitted, Is.False, "the guard verdict needs no effect barrier");

            var result = DebugValueSupport.ApplyWrite(plan, request);
            Assert.That(result.Applied, Is.False);
            Assert.That(result.Outcome, Is.EqualTo("guard-mismatch"));
            Assert.That(result.Restore, Is.Null, "a guard mismatch registers no restore record");
            Assert.That(result.PreviousValue, Is.EqualTo(1), "the result reports the effect-time-before value");
            Assert.That(ReadBack(plan.Plan), Is.EqualTo(1), "a guard mismatch must not write the field");
        }

        /// <summary>
        /// 根静态字段的无路径写入必须给出可证明的终态（回读），并同时证明写入结果的序列化留在默认请求预算内。
        /// </summary>
        [Test]
        public void ARootStaticFieldWriteIsProvenAndStaysInsideTheDefaultRequestBudget()
        {
            NoInitializerProbe.writableCounter = 0;
            var ctx = new UnityCliProjectToolContext { TimeoutMs = 30000 };
            var budget = ctx.UseReadBudget();
            var request = WriteRequest(typeof(NoInitializerProbe).FullName, "writableCounter", null, 5);

            var plan = DebugValueSupport.ValidateWrite(request, budget);
            var result = DebugValueSupport.ApplyWrite(plan, request);
            Assert.That(result.Applied, Is.True);
            Assert.That(result.Outcome, Is.EqualTo("applied"));
            Assert.That(result.WrittenValue, Is.EqualTo(5));
            Assert.That(ReadBack(plan.Plan), Is.EqualTo(5), "the write must be provable by a readback");
            Assert.That(NoInitializerProbe.writableCounter, Is.EqualTo(5));

            Assert.That(UnityCliSchemaDeriver.SerializableMembers(typeof(DebugValueWriteResult)).Count, Is.EqualTo(DebugLimits.ValueWriteResultMembers));
            Assert.That(UnityCliSchemaDeriver.SerializableMembers(typeof(DebugValueRestoreView)).Count, Is.EqualTo(DebugLimits.ValueWriteRestoreMembers));
            Assert.That(DebugLimits.ValueWriteGetters(), Is.GreaterThanOrEqualTo((long)DebugLimits.ValueWriteSerializations * DebugLimits.ValueWriteFixedMembers));
            Assert.That(DebugLimits.ValueWriteGetters(), Is.LessThanOrEqualTo(UnityCliRequestBudget.DefaultMaxGetters), "the declared write bound must stay inside the shared default request budget");

            for (var pass = 0; pass < DebugLimits.ValueWriteSerializations; pass++)
                UnityCliSerializer.Serialize(result, budget, CancellationToken.None);
            Assert.That(budget.IsExhausted, Is.False, budget.ExhaustedDimension);
            Assert.That(budget.Usage().Getters, Is.LessThanOrEqualTo(DebugLimits.ValueWriteGetters()), "one write plus the framework serialization passes must stay inside the derived bound");
            Assert.That(budget.Usage().Getters, Is.LessThanOrEqualTo(UnityCliRequestBudget.DefaultMaxGetters));
            NoInitializerProbe.writableCounter = 0;
        }

        /// <summary>写入请求夹具；session 与恢复登记由各用例自行决定。</summary>
        private static DebugValueWriteRequest WriteRequest(string typeName, string fieldName, List<string> path, object value)
        {
            return new DebugValueWriteRequest
            {
                RootKind = "static-field",
                TypeName = typeName,
                FieldName = fieldName,
                FieldPath = path,
                Value = value,
                RegisterRestore = false,
                SessionId = "dbg-write-probe"
            };
        }

        /// <summary>通过写入路径自己的解析计划回读当前值，不绕过产品读取实现。</summary>
        private static object ReadBack(DebugValuePlan plan)
        {
            object value;
            string failure;
            string nullAtPath;
            Assert.That(DebugValueSupport.TryReadPlanValue(plan, out value, out failure, out nullAtPath), Is.True, failure);
            return value;
        }

        [Test]
        public void ADestroyedUnityObjectHandleReportsStaleInsteadOfANullField()
        {
            var gameObject = new UnityEngine.GameObject("unitycli-debug-value-probe");
            DebugHandleView view;
            int sessionUsed;
            int globalUsed;
            try
            {
                Assert.That(DebugHandleRegistry.Create("dbg-unity", gameObject, false, "runtime-1", "gen-1", out view, out sessionUsed, out globalUsed), Is.Null);
                UnityEngine.Object.DestroyImmediate(gameObject);

                object resolved;
                DebugHandleView staleView;
                var status = DebugHandleRegistry.Resolve(view.HandleId, "runtime-1", "gen-1", out resolved, out staleView);
                Assert.That(status, Is.EqualTo("stale"));
                Assert.That(resolved, Is.Null);
                Assert.That(staleView.StaleReason, Is.EqualTo("unity-object-destroyed").Or.EqualTo("target-collected"), "a destroyed target is never presented as a null field value");
            }
            finally
            {
                DebugHandleRegistry.ReleaseSession("dbg-unity");
            }
        }

        // ------------------------------------------------------------------
        // 句柄
        // ------------------------------------------------------------------

        [Test]
        public void AWeakHandleReadsStaleAfterTheTargetIsCollectedInsteadOfReportingNull()
        {
            var handleId = CreateWeakHandleForCollectedTarget();
            object target;
            DebugHandleView view;
            var status = DebugHandleRegistry.Resolve(handleId, "runtime-1", "gen-1", out target, out view);
            Assert.That(status, Is.EqualTo("stale"));
            Assert.That(target, Is.Null);
            Assert.That(view.State, Is.EqualTo("stale"));
            Assert.That(view.StaleReason, Is.EqualTo("target-collected"));
            Assert.That(view.Strong, Is.False);
        }

        [Test]
        public void AHandleFromAnotherRuntimeOrGenerationNeverRebinds()
        {
            var target = new NoInitializerProbe();
            DebugHandleView created;
            int sessionUsed;
            int globalUsed;
            var failure = DebugHandleRegistry.Create("dbg-handle", target, false, "runtime-a", "gen-a", out created, out sessionUsed, out globalUsed);
            Assert.That(failure, Is.Null);

            object resolved;
            DebugHandleView view;
            Assert.That(DebugHandleRegistry.Resolve(created.HandleId, "runtime-b", "gen-a", out resolved, out view), Is.EqualTo("stale"));
            Assert.That(view.StaleReason, Is.EqualTo("runtime-changed"));
            Assert.That(DebugHandleRegistry.Resolve(created.HandleId, "runtime-a", "gen-b", out resolved, out view), Is.EqualTo("stale"));
            Assert.That(view.StaleReason, Is.EqualTo("domain-generation-changed"));
            Assert.That(DebugHandleRegistry.Resolve(created.HandleId, "runtime-a", "gen-a", out resolved, out view), Is.EqualTo("live"));
            Assert.That(resolved, Is.SameAs(target));

            Assert.That(DebugHandleRegistry.ReleaseSession("dbg-handle"), Is.EqualTo(1));
            Assert.That(DebugHandleRegistry.Resolve(created.HandleId, "runtime-a", "gen-a", out resolved, out view), Is.EqualTo("missing"));
        }

        [Test]
        public void TheHandleBudgetIsEnforcedPerSessionBeforeAnyEffect()
        {
            var target = new NoInitializerProbe();
            var created = new List<string>();
            for (var index = 0; index < DebugLimits.MaxHandlesPerSession; index++)
            {
                DebugHandleView view;
                int sessionUsed;
                int globalUsed;
                var failure = DebugHandleRegistry.Create("dbg-budget", target, false, "runtime-1", "gen-1", out view, out sessionUsed, out globalUsed);
                Assert.That(failure, Is.Null, failure);
                created.Add(view.HandleId);
            }

            DebugHandleView rejected;
            int rejectedSessionUsed;
            int rejectedGlobalUsed;
            Assert.That(
                DebugHandleRegistry.Create("dbg-budget", target, false, "runtime-1", "gen-1", out rejected, out rejectedSessionUsed, out rejectedGlobalUsed),
                Is.EqualTo("session-handle-limit"));
            Assert.That(rejected, Is.Null, "a rejected handle must not exist");
            Assert.That(rejectedSessionUsed, Is.EqualTo(DebugLimits.MaxHandlesPerSession));

            int checkedSessionUsed;
            int checkedGlobalUsed;
            Assert.That(DebugHandleRegistry.CanCreate("dbg-budget", out checkedSessionUsed, out checkedGlobalUsed), Is.EqualTo("session-handle-limit"), "the pre-effect budget check must report the same limit");
            Assert.That(checkedSessionUsed, Is.EqualTo(DebugLimits.MaxHandlesPerSession));
            Assert.That(DebugHandleRegistry.SessionCount("dbg-budget"), Is.EqualTo(DebugLimits.MaxHandlesPerSession), "the budget check must not create anything");
            Assert.That(DebugHandleRegistry.SessionCount("dbg-budget"), Is.EqualTo(DebugLimits.MaxHandlesPerSession));
            Assert.That(DebugHandleRegistry.ReleaseSession("dbg-budget"), Is.EqualTo(DebugLimits.MaxHandlesPerSession));
            Assert.That(DebugHandleRegistry.SessionCount("dbg-budget"), Is.EqualTo(0));
        }

        [Test]
        public void AStrongHandleIsReportedAsALifetimeIntervention()
        {
            var target = new NoInitializerProbe();
            DebugHandleView view;
            int sessionUsed;
            int globalUsed;
            Assert.That(DebugHandleRegistry.Create("dbg-strong", target, true, "runtime-1", "gen-1", out view, out sessionUsed, out globalUsed), Is.Null);
            Assert.That(view.Strong, Is.True);
            Assert.That(view.LifetimeNote, Does.Contain("keeps the target alive"));
            DebugHandleRegistry.ReleaseSession("dbg-strong");
        }

        // ------------------------------------------------------------------
        // 时钟与线程
        // ------------------------------------------------------------------

        [Test]
        public void NamedLogicalClockIsBoundedAndNeverClaimsToBeTheUnityFrame()
        {
            var published = DebugLogicalClocks.Publish("test.clock", 1234d, "1234", "field:test", "gen-test");
            Assert.That(published.IsUnityFrameCount, Is.False, "a project logical clock must never be presented as Time.frameCount");
            Assert.That(published.CapturedSameSite, Is.True);
            Assert.That(published.ValidityNote, Does.Contain("never the Unity frame count"));
            Assert.That(published.ValidFromMonotonicMs, Is.EqualTo(published.ValidToMonotonicMs), "a single observation instant must not claim continuous validity");

            for (var index = 0; index < DebugLimits.MaxNamedLogicalClocks + 3; index++)
                DebugLogicalClocks.Publish("test.clock-" + index, index, index.ToString(), "field:test", "gen-test");
            Assert.That(DebugLogicalClocks.Snapshot().Count, Is.LessThanOrEqualTo(DebugLimits.MaxNamedLogicalClocks));
            Assert.That(Assert.Throws<BridgeCommandException>(() => DebugLogicalClocks.Publish(string.Empty, 1d, "1", "field:test", "gen-test")).Code, Is.EqualTo("E_SCHEMA_INVALID"));

            var clocks = DebugClockProvenance.CaptureReadClocks(published);
            Assert.That(clocks.Count, Is.EqualTo(4), "monotonic, editor tick, unity frame and the published logical clock must all be present");
            var kinds = new List<string>();
            foreach (var clock in clocks) kinds.Add(clock.Kind);
            Assert.That(kinds, Does.Contain(UnityCliDebugClocks.Monotonic));
            Assert.That(kinds, Does.Contain(UnityCliDebugClocks.EditorTick));
            Assert.That(kinds, Does.Contain(UnityCliDebugClocks.UnityFrame));
            Assert.That(kinds, Does.Contain(UnityCliDebugClocks.ProjectLogical));
            foreach (var clock in clocks)
            {
                Assert.That(clock.Source, Is.Not.Empty, clock.Kind + " must name its source");
                Assert.That(clock.MainThread, Is.EqualTo(DebugEventClocks.IsCurrentThreadMainThread()));
            }
            var frame = clocks.Find(entry => entry.Kind == UnityCliDebugClocks.UnityFrame);
            if (DebugEventClocks.IsCurrentThreadMainThread())
            {
                Assert.That(frame.IsUnityFrameCount, Is.True);
                Assert.That(frame.Source, Does.Contain("main-thread"));
            }
            else
            {
                Assert.That(frame.IsUnityFrameCount, Is.False);
                Assert.That(frame.Frame, Is.EqualTo(0L), "off-main code must never read Time.frameCount");
                Assert.That(frame.Source, Does.Contain("cached"));
            }
        }

        [Test]
        public void ThreadObservationDeclaresItsScopeAndNeverClaimsAllManagedThreads()
        {
            var result = DebugThreads.List(new DebugThreadListRequest { Limit = 1 }, "session-test");
            Assert.That(result.EnumeratesAllManagedThreads, Is.False);
            Assert.That(result.ObservationScope, Does.Contain("not an enumeration of every managed thread"));
            Assert.That(result.Bounds.ThreadRecordsGlobalMax, Is.EqualTo(DebugLimits.MaxThreadRecords));
            Assert.That(result.Bounds.FramesPerThreadMax, Is.EqualTo(DebugLimits.MaxFramesPerThread));
            Assert.That(result.Bounds.EventsPerSessionMax, Is.EqualTo(DebugLimits.MaxEventsPerSession));
            Assert.That(result.Bounds.PageDefaultLimit, Is.EqualTo(DebugLimits.ThreadListDefaultLimit));
            Assert.That(result.Bounds.PageMaxLimit, Is.EqualTo(DebugLimits.ThreadListMaxLimit));
            Assert.That(result.Threads.Count, Is.LessThanOrEqualTo(1));
            foreach (var thread in result.Threads)
            {
                Assert.That(thread.ObservedFrom, Is.Not.Empty, "an observed thread must name the source that produced it");
                Assert.That(thread.MainThreadEvidence, Is.Not.Empty);
            }
        }

        // ------------------------------------------------------------------
        // 请求预算
        // ------------------------------------------------------------------

        /// <summary>
        /// 值读取的结果 DTO 会随每次读取返回整个命名逻辑时钟注册表，而框架会在同一请求内
        /// 有预算地序列化该结果 ValueReadSerializations 次：注册表被填满时，一次标量读取
        /// 必须仍然落在默认请求预算内，而不是以 E_REQUEST_BUDGET_LIMIT_EXCEEDED 结束。
        /// </summary>
        [Test]
        public void AValueReadStaysInsideTheDefaultRequestBudgetWithAFullLogicalClockRegistry()
        {
            for (var index = 0; index < DebugLimits.MaxNamedLogicalClocks + 3; index++)
                DebugLogicalClocks.Publish("budget.clock-" + index.ToString(CultureInfo.InvariantCulture), index, index.ToString(CultureInfo.InvariantCulture), "field:test", "gen-test");
            var retained = DebugLogicalClocks.Snapshot().Count;
            Assert.That(retained, Is.EqualTo(DebugLimits.MaxNamedLogicalClocks), "the registry must be saturated for this case to cover the worst shape");

            var budget = new UnityCliRequestBudget(new UnityCliReadBudgetInput { MaxGetters = DebugLimits.ValueReadGetters() }, 30000, CancellationToken.None);
            var result = DebugValueSupport.Read(
                new DebugValueReadRequest { RootKind = "static-field", TypeName = typeof(NoInitializerProbe).FullName, FieldName = "counter" },
                budget);
            Assert.That(result.Value, Is.EqualTo(0));
            for (var pass = 0; pass < DebugLimits.ValueReadSerializations; pass++)
                UnityCliSerializer.Serialize(result, budget, CancellationToken.None);

            // 一次 getter 对应一个真正被反射读取的 member，且框架的每一次有预算序列化都各自计费一次。
            var expected = (long)DebugLimits.ValueReadSerializations
                * (DebugLimits.ValueReadFixedMembers + (long)retained * DebugLimits.ValueReadLogicalClockMembers);
            Assert.That(budget.Usage().Getters, Is.EqualTo(expected), "exactly one getter is charged per reflected member per serialization pass");
            Assert.That(budget.IsExhausted, Is.False, budget.ExhaustedDimension);
            Assert.That(budget.Usage().Getters, Is.LessThanOrEqualTo(UnityCliRequestBudget.DefaultMaxGetters), "a single scalar read must stay well inside the default request budget");
        }

        /// <summary>推导出的值读取预算必须与实际公开 DTO 一致，且不高于默认请求预算。</summary>
        [Test]
        public void TheDerivedValueReadBudgetTracksThePublicDtosAndTheDefaultRequestBudget()
        {
            Assert.That(UnityCliSchemaDeriver.SerializableMembers(typeof(DebugValueReadResult)).Count, Is.EqualTo(DebugLimits.ValueReadResultMembers));
            Assert.That(UnityCliSchemaDeriver.SerializableMembers(typeof(DebugClockView)).Count, Is.EqualTo(DebugLimits.ValueReadClockMembers));
            Assert.That(UnityCliSchemaDeriver.SerializableMembers(typeof(DebugHandleView)).Count, Is.EqualTo(DebugLimits.ValueReadHandleMembers));
            Assert.That(UnityCliSchemaDeriver.SerializableMembers(typeof(DebugValueHandleBudgetView)).Count, Is.EqualTo(DebugLimits.ValueReadHandleBudgetMembers));
            Assert.That(UnityCliSchemaDeriver.SerializableMembers(typeof(DebugLogicalClockView)).Count, Is.EqualTo(DebugLimits.ValueReadLogicalClockMembers));
            Assert.That(DebugLimits.MaxNamedLogicalClocks, Is.GreaterThan(0));
            Assert.That(DebugLimits.ValueReadGetters(), Is.GreaterThanOrEqualTo((long)DebugLimits.ValueReadSerializations * DebugLimits.ValueReadFixedMembers));
            Assert.That(DebugLimits.ValueReadGetters(), Is.LessThanOrEqualTo(UnityCliRequestBudget.DefaultMaxGetters));
            Assert.That(DebugLimits.ValueReadGetters(), Is.LessThanOrEqualTo(DebugLimits.ValueReadBudgetShareGetters));
        }

        // ------------------------------------------------------------------
        // 夹具
        // ------------------------------------------------------------------

        /// <summary>
        /// 建立一个只被弱句柄引用的目标并强制完整回收。
        /// 目标必须在独立的不内联帧里创建：Debug 构建不会提前释放局部变量，同一帧内的局部在 GC 时仍是根，
        /// 那样无论调用多少次 GC.Collect 都回收不掉，测试会把"未回收"误报成产品缺陷。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string CreateWeakHandleForCollectedTarget()
        {
            var handleId = RegisterWeakHandleForFreshTarget();
            ForceFullCollection();
            return handleId;
        }

        /// <summary>在独立帧内创建目标，只把句柄 id 交给调用方，使目标在帧退出后不再可达。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string RegisterWeakHandleForFreshTarget()
        {
            var target = new NoInitializerProbe();
            DebugHandleView view;
            int sessionUsed;
            int globalUsed;
            var failure = DebugHandleRegistry.Create("dbg-weak", target, false, "runtime-1", "gen-1", out view, out sessionUsed, out globalUsed);
            Assert.That(failure, Is.Null, failure);
            return view.HandleId;
        }

        /// <summary>在目标已经不可达的帧里强制完整回收并等待终结器。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ForceFullCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static DebugValuePlan ResolveStatic(string rootKind, Type type, string field, List<string> path)
        {
            DebugHandleView handle;
            return DebugValueSupport.ResolvePlan(rootKind, type.FullName, field, null, null, path, Budget(), out handle);
        }

        private sealed class NoInitializerProbe
        {
            // 无初始化器的静态字段：本类型因此没有编译器生成的静态构造函数，静态访问可以被证明安全。
            private static int counter;
            private static int getterCalls;

            public static NoInitializerProbe Singleton;

            // 由测试自行赋值的一维数组；同样没有初始化器，因此本类型仍不产生静态构造函数。
            public static int[] samples;

            // 根静态字段无路径写入的夹具字段；同样没有初始化器。
            internal static int writableCounter;

            // 不可写目标：literal 与 init-only 都必须被写入路径在 effect 之前识别。
            public const int MaxRecursionDepth = 8;
            public readonly int ReadOnlyValue = 3;

            public OuterState outer = default(OuterState);
            public NoInitializerProbe Nullable;

            public static int GetterCalls { get { return getterCalls; } }

            private int value = 1;

            private int GetterOnly { get { getterCalls++; return value; } }
        }

        private struct OuterState
        {
            public MiddleState middle;

            public OuterState(MiddleState middle)
            {
                this.middle = middle;
            }
        }

        private struct MiddleState
        {
            public int depth;

            public MiddleState(int depth)
            {
                this.depth = depth;
            }
        }

        private sealed class StaticConstructorProbe
        {
            private static int initialized = 7;

            static StaticConstructorProbe()
            {
                probeTypeInitialized = true;
            }

            internal static int InitializedValue { get { return initialized; } }
        }

        /// <summary>用户实现的 IEnumerable；任何枚举都说明投影执行了用户代码。</summary>
        private sealed class CountingEnumerable : System.Collections.IEnumerable
        {
            internal static int Enumerations;

            public System.Collections.IEnumerator GetEnumerator()
            {
                Enumerations++;
                yield break;
            }
        }
    }
}
#endif
