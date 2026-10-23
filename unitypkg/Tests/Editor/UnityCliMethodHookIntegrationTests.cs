#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// method-hook 的真实 patch 集成测试：在 EditMode 域内安装定义、用反射触发被拦截方法、
    /// 校验改参/ref 回写/结果替换/跳过原方法/静态与私有目标/第三方 owner 保留/启停恢复。
    ///
    /// 只用反射调用目标方法，因此不受“调用点编译早于补丁则看不到补丁”的限制；
    /// 回调源码编译由 Dev 功能矩阵覆盖，本夹具不启动 compiler。
    /// </summary>
    public sealed class UnityCliMethodHookIntegrationTests
    {
        private const string ForeignOwnerId = "unitycli.test.foreign";

        private static object foreignHarmony;
        private static Type foreignHarmonyType;
        private static Type foreignHarmonyMethodType;

        /// <summary>
        /// 第三方 owner 用反射安装，避免测试程序集依赖 HarmonyLib；这与 Dev 探针的形态一致，
        /// 也保证测试只验证“本 CLI 不碰别人的 patch”这一契约。
        /// </summary>
        private static void EnsureForeignHarmony()
        {
            if (foreignHarmony != null) return;
            var assembly = FindLoadedAssembly("0Harmony");
            if (assembly == null) Assert.Ignore("0Harmony is not loaded in this domain; the foreign-owner contract needs the packaged dependency.");
            foreignHarmonyType = assembly.GetType("HarmonyLib.Harmony", true);
            foreignHarmonyMethodType = assembly.GetType("HarmonyLib.HarmonyMethod", true);
            foreignHarmony = Activator.CreateInstance(foreignHarmonyType, new object[] { ForeignOwnerId });
        }

        private static Assembly FindLoadedAssembly(string simpleName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.Ordinal)) return assembly;
            return null;
        }

        private static void ForeignPatch(MethodBase original, MethodInfo postfix)
        {
            EnsureForeignHarmony();
            var patchMethod = foreignHarmonyType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => string.Equals(method.Name, "Patch", StringComparison.Ordinal) && method.GetParameters().Length == 5);
            Assert.That(patchMethod, Is.Not.Null, "Harmony.Patch(MethodBase, HarmonyMethod, HarmonyMethod, HarmonyMethod, HarmonyMethod) is required");
            var harmonyMethod = Activator.CreateInstance(foreignHarmonyMethodType, new object[] { postfix });
            patchMethod.Invoke(foreignHarmony, new object[] { original, null, harmonyMethod, null, null });
        }

        private static void ForeignUnpatchAll()
        {
            if (foreignHarmony == null) return;
            var unpatchAll = foreignHarmonyType.GetMethod("UnpatchAll", new[] { typeof(string) });
            if (unpatchAll != null) unpatchAll.Invoke(foreignHarmony, new object[] { ForeignOwnerId });
            foreignHarmony = null;
        }

        public sealed class HookFixture
        {
            private int hits;
            private string hidden = "hidden";

            public int Hits { get { return hits; } }

            public int Add(int left, int right)
            {
                hits++;
                return left + right;
            }

            public void Bump(ref int value)
            {
                hits++;
                value += 10;
            }

            public string Describe(string input)
            {
                hits++;
                return "orig:" + input;
            }

            private int Secret(int seed)
            {
                hits++;
                return seed * 3;
            }

            public int CallSecret(int seed) { return Secret(seed); }

            public static string StaticTag(string input) { return "static:" + input; }

            private static string ForeignTagged(string input) { return "foreign-original:" + input; }

            public static string CallForeignTagged(string input) { return ForeignTagged(input); }

            public string ReadHidden() { return hidden; }

            /// <summary>业务递归：每次调用都是真实调用，必须各自建立帧与事件。</summary>
            public string Recurse(string input, int depth)
            {
                hits++;
                if (depth <= 0) return "leaf:" + input;
                return Recurse(input, depth - 1) + "|" + input;
            }

            public string Boom(string input)
            {
                hits++;
                throw new InvalidOperationException("original-boom");
            }
        }

        /// <summary>
        /// pause 断点是命中行为，不是有效行为：没有捕获字段也没有回调代码时 install 预检必须拒绝，
        /// 只有与至少一个捕获行为一起声明时才越过该要求。
        /// 预检在 effect barrier 之前判定，因此这里只验证判定顺序与错误码，不安装真实 patch。
        /// </summary>
        [Test]
        public void PauseBreakpointAloneIsRefusedByTheInstallPreflight()
        {
            var request = new MethodHookInstallRequest
            {
                Label = "pause-only",
                Activation = "editor-and-play",
                Target = new MethodHookTargetSpec
                {
                    AssemblyName = typeof(HookFixture).Assembly.GetName().Name,
                    TypeName = typeof(HookFixture).FullName,
                    MethodName = "Add",
                    ParameterTypes = new List<string> { "System.Int32", "System.Int32" },
                    ReturnType = "System.Int32",
                    IsStatic = false
                },
                Breakpoint = new MethodHookBreakpointSpec
                {
                    Kind = DebugPauseBreakpointKinds.Pause,
                    Position = DebugPausePositions.Prefix,
                    PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = DebugPauseTimeoutKinds.Bounded, DurationMs = 120000L }
                }
            };
            var refused = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareInstall(request));
            Assert.That(refused.Code, Is.EqualTo("E_SCHEMA_INVALID"));
            Assert.That(refused.Message, Does.Contain("at least one effective capture or callback behaviour"));

            // 同一声明加上一个捕获行为后，行为要求不再拒绝它；后续失败（控制通道或 backend）不属于该要求。
            request.Capture = new MethodHookCaptureSpec { Arguments = true };
            try
            {
                var plan = MethodHookHost.PrepareInstall(request);
                DebugPauseHost.ReleaseUncommittedPlan(plan.BreakpointPlan);
            }
            catch (UnityCliBridge.BridgeCommandException error)
            {
                Assert.That(error.Code, Is.Not.EqualTo("E_SCHEMA_INVALID"),
                    "one capture behaviour together with the pause breakpoint must satisfy the behaviour requirement");
            }
        }

        [SetUp]
        public void SetUp()
        {
            RemoveAllOwnDefinitions();
        }

        [TearDown]
        public void TearDown()
        {
            RemoveAllOwnDefinitions();
        }

        private static void RemoveAllOwnDefinitions()
        {
            var list = MethodHookHost.List(new MethodHookListRequest { Limit = DebugLimits.ListMaxLimit });
            foreach (var item in list.Items)
                MethodHookHost.ApplyRemove(new MethodHookMutationPlan { Action = "remove", Definition = FindDefinition(item.HookId) });
        }

        /// <summary>测试通过 host 内部表取回定义，避免依赖 host 暴露额外查找入口。</summary>
        private static MethodHookDefinition FindDefinition(string hookId)
        {
            var field = typeof(MethodHookHost).GetField("Definitions", BindingFlags.Static | BindingFlags.NonPublic);
            var map = (Dictionary<string, MethodHookDefinition>)field.GetValue(null);
            return map[hookId];
        }

        private static MethodHookInstallResult Install(string label, string methodName, bool isStatic, string returnType, string[] parameterTypes, string prefixCode, string postfixCode, MethodHookCaptureSpec capture = null)
        {
            var request = new MethodHookInstallRequest
            {
                Label = label,
                Activation = "editor-and-play",
                Target = new MethodHookTargetSpec
                {
                    AssemblyName = typeof(HookFixture).Assembly.GetName().Name,
                    TypeName = typeof(HookFixture).FullName,
                    MethodName = methodName,
                    ParameterTypes = new List<string>(parameterTypes),
                    ReturnType = returnType,
                    IsStatic = isStatic
                },
                Capture = capture,
                Callbacks = prefixCode == null && postfixCode == null ? null : new MethodHookCallbackSpec { PrefixCode = prefixCode, PostfixCode = postfixCode }
            };
            var plan = MethodHookHost.PrepareInstall(request);
            var prepared = Compile(plan.Callbacks);
            return MethodHookHost.ApplyInstall(plan, prepared);
        }

        /// <summary>EditMode 测试在主线程上同步运行，因此必须用同步轮询 waiter，不能等中央 scheduler 推进。</summary>
        private static MethodHookPreparedCallbacks Compile(MethodHookCallbackSpec callbacks)
        {
            if (callbacks == null || (string.IsNullOrWhiteSpace(callbacks.PrefixCode) && string.IsNullOrWhiteSpace(callbacks.PostfixCode))) return null;
            return MethodHookHost.CompileCallbacksAsync(callbacks, new TestWaiter(), TimeSpan.FromSeconds(120), System.Threading.CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }

        /// <summary>
        /// 同步 waiter：直接在调用线程上轮询谓词，直到成立或超时。
        /// 它只能用于测试；产品路径必须使用中央 scheduler 的 waiter，以免阻塞 Editor 主线程。
        /// </summary>
        private sealed class TestWaiter : IUnityCliWaiter
        {
            private const int PollMilliseconds = 20;

            public UnityCliMainThreadAwaitable<bool> NextFrame(int frames = 1, System.Threading.CancellationToken ct = default)
            {
                return Ready(true);
            }

            public UnityCliMainThreadAwaitable<bool> Delay(TimeSpan duration, System.Threading.CancellationToken ct = default)
            {
                return Poll(() => true, duration, ct);
            }

            public UnityCliMainThreadAwaitable<bool> Until(Func<bool> predicate, TimeSpan timeout, System.Threading.CancellationToken ct = default)
            {
                return Poll(predicate, timeout, ct);
            }

            public UnityCliMainThreadAwaitable<bool> UntilStable(Func<bool> predicate, TimeSpan timeout, int minimumFrames = 2, TimeSpan minimumDuration = default, System.Threading.CancellationToken ct = default)
            {
                return Poll(predicate, timeout, ct);
            }

            public UnityCliMainThreadAwaitable<bool> UntilEditorReady(TimeSpan timeout, System.Threading.CancellationToken ct = default)
            {
                return Poll(() => !UnityEditor.EditorApplication.isCompiling && !UnityEditor.EditorApplication.isUpdating, timeout, ct);
            }

            public UnityCliMainThreadAwaitable<bool> UntilPlayModeState(UnityEditor.PlayModeStateChange state, TimeSpan timeout, System.Threading.CancellationToken ct = default)
            {
                return Poll(() => UnityEditor.EditorApplication.isPlaying, timeout, ct);
            }

            public UnityCliMainThreadAwaitable<T> ForCallback<T>(Action<Func<Func<T>, bool>, Func<Exception, bool>> start, TimeSpan timeout, System.Threading.CancellationToken ct = default, Action onAbandoned = null)
            {
                throw new NotSupportedException("The test waiter does not support callback waits.");
            }

            private static UnityCliMainThreadAwaitable<bool> Ready(bool value)
            {
                return new UnityCliMainThreadAwaitable<bool>(new CompletedSource<bool>(value));
            }

            private UnityCliMainThreadAwaitable<bool> Poll(Func<bool> predicate, TimeSpan timeout, System.Threading.CancellationToken ct)
            {
                var deadline = DateTime.UtcNow.Add(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
                while (true)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (predicate()) return Ready(true);
                    if (DateTime.UtcNow >= deadline) return Ready(false);
                    System.Threading.Thread.Sleep(PollMilliseconds);
                }
            }

            private sealed class CompletedSource<T> : IMainThreadAwaitableSource<T>
            {
                private readonly T value;
                internal CompletedSource(T value) { this.value = value; }
                public bool IsCompleted { get { return true; } }
                public void RegisterContinuation(Action continuation) { }
                public T GetResult() { return value; }
            }
        }


        private static object InvokeStatic(string methodName, params object[] arguments)
        {
            var method = typeof(HookFixture).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            return method.Invoke(null, arguments);
        }

        [Test]
        public void PrefixMutatesArgumentsAndSkipOriginalKeepsHits()
        {
            var install = Install("it-mutate", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" },
                "ctx.SetArgument(0, (int)ctx.Arguments[0] + 100);", null);
            Assert.That(install.State, Is.EqualTo("armed"));

            var instance = new HookFixture();
            var add = typeof(HookFixture).GetMethod("Add");
            var result = (int)add.Invoke(instance, new object[] { 2, 3 });
            Assert.That(result, Is.EqualTo(105), "the prefix must change the argument the original method receives");
            Assert.That(instance.Hits, Is.EqualTo(1));

            Install("it-skip", "Describe", false, "System.String", new[] { "System.String" }, "ctx.SkipOriginal(\"hooked\");", null);
            var describe = typeof(HookFixture).GetMethod("Describe");
            var described = (string)describe.Invoke(instance, new object[] { "x" });
            Assert.That(described, Is.EqualTo("hooked"));
            Assert.That(instance.Hits, Is.EqualTo(1), "skipping the original must not increase its hit counter");
        }

        [Test]
        public void PostfixReplacesResultForValueReferenceStaticAndPrivateTargets()
        {
            Install("it-result", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" }, null, "ctx.SetResult((int)ctx.Result + 1000);");
            var instance = new HookFixture();
            Assert.That((int)typeof(HookFixture).GetMethod("Add").Invoke(instance, new object[] { 2, 3 }), Is.EqualTo(1005));

            Install("it-static", "StaticTag", true, "System.String", new[] { "System.String" }, null, "ctx.SetResult(\"patched:\" + ctx.Arguments[0]);");
            Assert.That(InvokeStatic("StaticTag", "q"), Is.EqualTo("patched:q"), "static postfix must replace the result");

            Install("it-private", "Secret", false, "System.Int32", new[] { "System.Int32" }, null, "ctx.SetResult((int)ctx.Result * 10);");
            var secret = typeof(HookFixture).GetMethod("Secret", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That((int)secret.Invoke(instance, new object[] { 4 }), Is.EqualTo(120), "private targets must be interceptable");
        }

        [Test]
        public void ByRefParameterRoundTripsThroughPrefixAndOriginal()
        {
            Install("it-byref", "Bump", false, "System.Void", new[] { "System.Int32&" }, "ctx.SetArgument(0, (int)ctx.Arguments[0] + 5);", null);
            var instance = new HookFixture();
            var arguments = new object[] { 1 };
            typeof(HookFixture).GetMethod("Bump").Invoke(instance, arguments);
            Assert.That((int)arguments[0], Is.EqualTo(16), "prefix +5 then original +10 must both be visible to the caller");
        }

        [Test]
        public void CaptureEventsRecordHitsAndPrivateFieldsWithoutCallbackSource()
        {
            var instance = new HookFixture();
            var install = Install("it-capture", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" }, null, null,
                new MethodHookCaptureSpec
                {
                    Arguments = true,
                    InstanceFields = new List<MethodHookCaptureFieldSpec> { new MethodHookCaptureFieldSpec { FieldName = "hits" } }
                });
            Assert.That(install.Callbacks, Is.Not.Null);
            var sequenceBefore = MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId }).EventSummary.LatestSequence;

            typeof(HookFixture).GetMethod("Add").Invoke(instance, new object[] { 4, 5 });
            var page = DebugEventStore.ReadEvents(new DebugEventsRequest { HookId = install.HookId, Limit = 16 });
            var prefix = page.Events.FindAll(item => item.Kind == "prefix");
            Assert.That(prefix.Count, Is.GreaterThan(0));
            Assert.That(prefix[prefix.Count - 1].Fields, Is.Not.Null, "captured private field must be readable");
            Assert.That(MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId }).EventSummary.LatestSequence, Is.GreaterThan(sequenceBefore), "capture must append bounded business events");
        }

        [Test]
        public void ForeignOwnerPatchSurvivesOwnInstallAndRemoval()
        {
            EnsureForeignHarmony();
            var foreignPostfix = typeof(ForeignPatches).GetMethod("ForeignPostfix", BindingFlags.Public | BindingFlags.Static);
            var method = typeof(HookFixture).GetMethod("CallForeignTagged", BindingFlags.Public | BindingFlags.Static);
            ForeignPatch(method, foreignPostfix);
            try
            {
                Assert.That(InvokeStatic("CallForeignTagged", "a"), Is.EqualTo("foreign-patched:foreign-original:a"));
                var install = Install("it-foreign", "CallForeignTagged", true, "System.String", new[] { "System.String" }, null, "ctx.Emit(\"own\", 1);");
                var info = MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId });
                Assert.That(info.ForeignOwners, Does.Contain(ForeignOwnerId), "coexisting foreign owners must be reported");

                MethodHookHost.ApplyRemove(new MethodHookMutationPlan { Action = "remove", Definition = FindDefinition(install.HookId) });
                Assert.That(InvokeStatic("CallForeignTagged", "b"), Is.EqualTo("foreign-patched:foreign-original:b"), "removing own patch must not remove foreign owners");
            }
            finally
            {
                ForeignUnpatchAll();
            }
            Assert.That(InvokeStatic("CallForeignTagged", "c"), Is.EqualTo("foreign-original:c"));
        }

        [Test]
        public void DisableAndRemoveRestoreOriginalBehaviourAndRevisionSemantics()
        {
            var install = Install("it-lifecycle", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" }, "ctx.SetArgument(0, 100);", null);
            var instance = new HookFixture();
            var add = typeof(HookFixture).GetMethod("Add");
            Assert.That((int)add.Invoke(instance, new object[] { 1, 1 }), Is.EqualTo(101));

            var info = MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId });
            var plan = MethodHookHost.PrepareDisable(new MethodHookDisableRequest { HookId = install.HookId, EditorSessionId = info.EditorSessionId, ExpectedRevision = info.Revision });
            var disabled = MethodHookHost.ApplyDisable(plan);
            Assert.That(disabled.Changed, Is.True);
            Assert.That(disabled.State, Is.EqualTo("disabled"));
            Assert.That((int)add.Invoke(instance, new object[] { 1, 1 }), Is.EqualTo(2), "disabling must restore the original behaviour");

            var disableAgain = MethodHookHost.ApplyDisable(MethodHookHost.PrepareDisable(new MethodHookDisableRequest { HookId = install.HookId, EditorSessionId = info.EditorSessionId, ExpectedRevision = disabled.Revision }));
            Assert.That(disableAgain.Changed, Is.False, "disabling an already disabled definition is a no-op");
            Assert.That(disableAgain.Revision, Is.EqualTo(disabled.Revision));

            var enablePlan = MethodHookHost.PrepareEnable(new MethodHookEnableRequest { HookId = install.HookId, EditorSessionId = info.EditorSessionId, ExpectedRevision = disabled.Revision });
            var enablePrepared = Compile(new MethodHookCallbackSpec { PrefixCode = enablePlan.Definition.PrefixCode, PostfixCode = enablePlan.Definition.PostfixCode });
            var enabled = MethodHookHost.ApplyEnable(enablePlan, enablePrepared);
            Assert.That(enabled.State, Is.EqualTo("armed"));
            Assert.That((int)add.Invoke(instance, new object[] { 1, 1 }), Is.EqualTo(101), "enabling must reactivate the definition");

            var removed = MethodHookHost.ApplyRemove(new MethodHookMutationPlan { Action = "remove", Definition = FindDefinition(install.HookId) });
            Assert.That(removed.Removed, Is.True);
            Assert.That(removed.AdapterReleased, Is.True, "the last definition on the target releases the owned adapter");
            Assert.That((int)add.Invoke(instance, new object[] { 1, 1 }), Is.EqualTo(2));

            var missing = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareRemove(new MethodHookRemoveRequest { HookId = install.HookId, EditorSessionId = info.EditorSessionId, ExpectedRevision = 1 }));
            Assert.That(missing.Code, Is.EqualTo("E_HOOK_NOT_FOUND"));
        }

        [Test]
        public void StaleRevisionAndSessionFailBeforeAnyEffect()
        {
            var install = Install("it-stale", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" }, null, null,
                new MethodHookCaptureSpec { Arguments = true });
            var info = MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId });

            var revision = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareDisable(new MethodHookDisableRequest { HookId = install.HookId, EditorSessionId = info.EditorSessionId, ExpectedRevision = info.Revision + 5 }));
            Assert.That(revision.Code, Is.EqualTo("E_HOOK_REVISION_STALE"));

            var session = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareDisable(new MethodHookDisableRequest { HookId = install.HookId, EditorSessionId = "session-deadbeef", ExpectedRevision = info.Revision }));
            Assert.That(session.Code, Is.EqualTo("E_HOOK_SESSION_STALE"));

            var after = MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId });
            Assert.That(after.State, Is.EqualTo("armed"), "a rejected mutation must not change the definition");
            Assert.That(after.Revision, Is.EqualTo(info.Revision));
        }

        [Test]
        public void PackagedPluginImporterProjectionIsVerifiedAtRuntime()
        {
            var packageRoot = UnityCliBridge.BridgePackageAssetRoot();
            Assert.That(packageRoot, Is.Not.Empty, "the Bridge package asset root must be resolvable in a real project");
            Assert.DoesNotThrow(() => MethodHookHost.VerifyPluginImporterSettings(packageRoot + "/Editor/Plugins/0Harmony.dll"),
                "the packaged plugin must import as an Editor-only, non-preloaded managed plugin");

            var missing = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.VerifyPluginImporterSettings(packageRoot + "/Editor/Plugins/DoesNotExist.dll"));
            Assert.That(missing.Code, Is.EqualTo("E_HOOK_BACKEND_UNAVAILABLE"));
            var empty = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.VerifyPluginImporterSettings(string.Empty));
            Assert.That(empty.Code, Is.EqualTo("E_HOOK_BACKEND_UNAVAILABLE"));
        }

        [Test]
        public void CallbackPendingFlagMarksCallsDuringTheRestoringWindow()
        {
            // 模拟“定义已 armed 但回调尚未在本域编译完成”的重挂窗口：不传 prepared，使 Callback 仍为 null。
            var request = new MethodHookInstallRequest
            {
                Label = "it-restoring",
                Activation = "editor-and-play",
                Target = new MethodHookTargetSpec
                {
                    AssemblyName = typeof(HookFixture).Assembly.GetName().Name,
                    TypeName = typeof(HookFixture).FullName,
                    MethodName = "Add",
                    ParameterTypes = new List<string> { "System.Int32", "System.Int32" },
                    ReturnType = "System.Int32",
                    IsStatic = false
                },
                Callbacks = new MethodHookCallbackSpec { PrefixCode = "ctx.Emit(\"hit\", 1);" }
            };
            var plan = MethodHookHost.PrepareInstall(request);
            var install = MethodHookHost.ApplyInstall(plan, null);
            var definition = FindDefinition(install.HookId);
            Assert.That(definition.Callback, Is.Null, "the callback must still be pending in this constructed window");
            definition.State = MethodHookState.Armed;

            var instance = new HookFixture();
            Assert.That((int)typeof(HookFixture).GetMethod("Add").Invoke(instance, new object[] { 1, 2 }), Is.EqualTo(3), "the original call must be unaffected");

            var page = DebugEventStore.ReadEvents(new DebugEventsRequest { HookId = install.HookId, Limit = DebugLimits.EventsMaxLimit });
            Assert.That(page.Events.FindAll(item => item.Kind == "prefix" && item.CallbackPending).Count, Is.EqualTo(1), "a call in the restoring window must be readable as callbackPending");
            Assert.That(page.Events.FindAll(item => item.Kind == "custom").Count, Is.EqualTo(0), "no callback may run while it is pending");
            definition.State = MethodHookState.Dormant;
        }

        [Test]
        public void OriginalExceptionIsRecordedByTheFinalizerWithoutFabricatingPostfix()
        {
            var install = Install("it-finalizer", "Boom", false, "System.String", new[] { "System.String" }, null, "ctx.SetResult(\"postfix-ran\");");
            var instance = new HookFixture();
            var boom = typeof(HookFixture).GetMethod("Boom");
            var failure = Assert.Throws<TargetInvocationException>(() => boom.Invoke(instance, new object[] { "x" }));
            Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>(), "the original exception must propagate unchanged");
            Assert.That(failure.InnerException.Message, Is.EqualTo("original-boom"));

            var page = DebugEventStore.ReadEvents(new DebugEventsRequest { HookId = install.HookId, Limit = DebugLimits.EventsMaxLimit });
            Assert.That(page.Events.FindAll(item => item.Kind == "postfix").Count, Is.EqualTo(0), "a throwing original must not produce a successful postfix event");
            var ended = page.Events.FindAll(item => item.Kind == "lifecycle" && item.Name == "original-exception");
            Assert.That(ended.Count, Is.EqualTo(1), "the finalizer must record exactly one original-exception event");
            Assert.That(MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId }).State, Is.EqualTo("armed"), "an original exception is not a callback fault");
        }

        [Test]
        public void FaultedEnableReResolvesAndReportsFailureHonestly()
        {
            var install = Install("it-fault-enable", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" }, "throw new System.InvalidOperationException(\"boom\");", null);
            var add = typeof(HookFixture).GetMethod("Add");
            var instance = new HookFixture();
            add.Invoke(instance, new object[] { 1, 1 });
            var faulted = MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId });
            Assert.That(faulted.State, Is.EqualTo("faulted"));

            // 负例：目标不存在时 enable 必须明确失败，且不伪装成已激活。
            var definition = FindDefinition(install.HookId);
            var originalTypeName = definition.TargetSpec.TypeName;
            definition.TargetSpec.TypeName = "UnityCli.Tests.DoesNotExist";
            var failure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareEnable(new MethodHookEnableRequest { HookId = install.HookId, EditorSessionId = faulted.EditorSessionId, ExpectedRevision = faulted.Revision }));
            Assert.That(failure.Code, Is.EqualTo("E_HOOK_TARGET_NOT_FOUND"));
            Assert.That(MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId }).State, Is.EqualTo("faulted"), "a failed enable must not masquerade as success");
            definition.TargetSpec.TypeName = originalTypeName;

            var current = MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId });
            var plan = MethodHookHost.PrepareEnable(new MethodHookEnableRequest { HookId = install.HookId, EditorSessionId = current.EditorSessionId, ExpectedRevision = current.Revision });
            var prepared = Compile(new MethodHookCallbackSpec { PrefixCode = plan.Definition.PrefixCode, PostfixCode = plan.Definition.PostfixCode });
            var enabled = MethodHookHost.ApplyEnable(plan, prepared);
            Assert.That(enabled.State, Is.EqualTo("armed"));
            Assert.That(MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId }).LastFault, Is.Null, "a successful enable clears the recorded fault");
        }

        [Test]
        public void CallbackSelfCallBypassesReentryAndKeepsOriginalSemantics()
        {
            var install = Install("it-reentry", "Describe", false, "System.String", new[] { "System.String" },
                "if ((string)ctx.Arguments[0] == \"outer\") { ctx.Instance.GetType().GetMethod(\"Describe\").Invoke(ctx.Instance, new object[] { \"inner\" }); }", null);
            var instance = new HookFixture();
            var describe = typeof(HookFixture).GetMethod("Describe");

            Assert.That((string)describe.Invoke(instance, new object[] { "outer" }), Is.EqualTo("orig:outer"), "the original method must still run");
            var info = MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId });
            Assert.That(info.BypassCount, Is.GreaterThan(0), "a callback self-call must be bypassed by the thread-local guard");
            Assert.That(info.State, Is.EqualTo("armed"), "a bypassed self-call is not a fault");

            var page = DebugEventStore.ReadEvents(new DebugEventsRequest { HookId = install.HookId, Limit = DebugLimits.EventsMaxLimit });
            Assert.That(page.Events.FindAll(item => item.Kind == "prefix").Count, Is.EqualTo(1), "the bypassed inner call must not emit another prefix event");
        }

        [Test]
        public void WorkerThreadHitDispatchesCallbacksOnTheCallingThread()
        {
            var install = Install("it-worker-dispatch", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" },
                "ctx.SetArgument(0, (int)ctx.Arguments[0] + 100);", "ctx.SetResult((int)ctx.Result * 2);");
            var instance = new HookFixture();
            var add = typeof(HookFixture).GetMethod("Add");
            object result = null;
            Exception error = null;
            var workerThreadId = 0;
            var thread = new System.Threading.Thread(() =>
            {
                workerThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                try { result = add.Invoke(instance, new object[] { 1, 2 }); }
                catch (Exception exception) { error = exception; }
            });
            thread.Start();
            Assert.That(thread.Join(TimeSpan.FromSeconds(30)), Is.True, "a worker-thread hit must not deadlock the caller");
            Assert.That(error, Is.Null, "a worker-thread hit must not surface a framework exception to the game call");
            Assert.That((int)result, Is.EqualTo(206), "prefix argument mutation and postfix result mutation must both apply on a worker thread");

            var info = MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId });
            Assert.That(info.State, Is.EqualTo("armed"), "a worker hit is not a fault");
            Assert.That(info.LastFault, Is.Null);

            var page = DebugEventStore.ReadEvents(new DebugEventsRequest { HookId = install.HookId, Limit = DebugLimits.EventsMaxLimit });
            var prefix = page.Events.FindAll(item => item.Kind == "prefix");
            var postfix = page.Events.FindAll(item => item.Kind == "postfix");
            Assert.That(prefix.Count, Is.EqualTo(1));
            Assert.That(postfix.Count, Is.EqualTo(1));
            Assert.That(prefix[0].MainThread, Is.False, "a worker hit must be recorded as off-main");
            Assert.That(prefix[0].ThreadId, Is.EqualTo(workerThreadId), "the recorded thread must be the thread that made the original call");
            Assert.That(prefix[0].Frame, Is.EqualTo(0L), "an off-main event must not claim a Unity frame number");
            Assert.That(postfix[0].InvocationId, Is.EqualTo(prefix[0].InvocationId), "prefix and postfix must share one pairing key");
        }

        [Test]
        public void WorkerThreadCallbackFaultClosesOnlyThatDefinitionUntilTheMainThreadTick()
        {
            var failing = Install("it-worker-fault", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" }, "throw new System.InvalidOperationException(\"worker-boom\");", null);
            var healthy = Install("it-worker-healthy", "Describe", false, "System.String", new[] { "System.String" }, null, "ctx.SetResult(\"healthy\");");
            var instance = new HookFixture();
            var add = typeof(HookFixture).GetMethod("Add");

            object result = null;
            var first = new System.Threading.Thread(() => { result = add.Invoke(instance, new object[] { 1, 2 }); });
            first.Start();
            Assert.That(first.Join(TimeSpan.FromSeconds(30)), Is.True);
            Assert.That((int)result, Is.EqualTo(3), "a worker callback fault must not change the original call");

            // worker 只关闭派发；状态、持久化与 detach 由主线程在中央 tick 上提交。
            MethodHookHost.Tick();
            var info = MethodHookHost.Info(new MethodHookInfoRequest { HookId = failing.HookId });
            Assert.That(info.State, Is.EqualTo("faulted"));
            Assert.That(info.LastFault.Code, Is.EqualTo("E_HOOK_CALLBACK_FAILED"));
            Assert.That(info.LastFault.Message, Does.Contain("worker-boom"));
            Assert.That(info.LastFault.Revision, Is.EqualTo(info.Revision), "the fault must carry the revision it happened under");

            object second = null;
            var again = new System.Threading.Thread(() => { second = add.Invoke(instance, new object[] { 2, 3 }); });
            again.Start();
            Assert.That(again.Join(TimeSpan.FromSeconds(30)), Is.True);
            Assert.That((int)second, Is.EqualTo(5), "a faulted hook must leave later original calls unchanged");

            Assert.That((string)typeof(HookFixture).GetMethod("Describe").Invoke(instance, new object[] { "x" }), Is.EqualTo("healthy"), "an unrelated definition must keep working");
            Assert.That(MethodHookHost.Info(new MethodHookInfoRequest { HookId = healthy.HookId }).State, Is.EqualTo("armed"));
        }

        [Test]
        public void RecursiveBusinessCallsGetTheirOwnFramesAndEvents()
        {
            var install = Install("it-recursion", "Recurse", false, "System.String", new[] { "System.String", "System.Int32" }, "ctx.Emit(\"enter\", ctx.Arguments[1]);", null);
            var instance = new HookFixture();
            var recurse = typeof(HookFixture).GetMethod("Recurse");
            Assert.That((string)recurse.Invoke(instance, new object[] { "a", 2 }), Is.EqualTo("leaf:a|a|a"), "the original recursion must run unchanged");

            var info = MethodHookHost.Info(new MethodHookInfoRequest { HookId = install.HookId });
            Assert.That(info.HitCount, Is.EqualTo(3L), "every recursive business call is a real hit");
            Assert.That(info.BypassCount, Is.EqualTo(0L), "a business recursion is not a suppressed callback self-call");

            var page = DebugEventStore.ReadEvents(new DebugEventsRequest { HookId = install.HookId, Limit = DebugLimits.EventsMaxLimit });
            var prefix = page.Events.FindAll(item => item.Kind == "prefix");
            Assert.That(prefix.Count, Is.EqualTo(3), "every recursive business call must produce its own hit event");
            Assert.That(new HashSet<long>(prefix.ConvertAll(item => item.InvocationId)).Count, Is.EqualTo(3), "each frame must carry its own invocation id");
        }

        [Test]
        public void SecondInterventionIsRejectedBeforeEffectAndStoppingOneOwnerKeepsTheOtherObservation()
        {
            var owner = Install("it-owner", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" }, "ctx.Emit(\"owner\", 1);", null);
            var instance = new HookFixture();
            var add = typeof(HookFixture).GetMethod("Add");
            var definitionsBefore = MethodHookHost.List(new MethodHookListRequest { Limit = DebugLimits.ListMaxLimit }).Total;

            var conflicting = new MethodHookInstallRequest
            {
                Label = "it-owner-two",
                Activation = "editor-and-play",
                Target = new MethodHookTargetSpec
                {
                    AssemblyName = typeof(HookFixture).Assembly.GetName().Name,
                    TypeName = typeof(HookFixture).FullName,
                    MethodName = "Add",
                    ParameterTypes = new List<string> { "System.Int32", "System.Int32" },
                    ReturnType = "System.Int32",
                    IsStatic = false
                },
                Callbacks = new MethodHookCallbackSpec { PrefixCode = "ctx.Emit(\"second\", 2);" }
            };
            var conflict = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareInstall(conflicting));
            Assert.That(conflict.Code, Is.EqualTo("E_DEBUG_RESOURCE_CONFLICT"));
            Assert.That(MethodHookHost.List(new MethodHookListRequest { Limit = DebugLimits.ListMaxLimit }).Total, Is.EqualTo(definitionsBefore), "a rejected install must not create a definition");
            Assert.That((int)add.Invoke(instance, new object[] { 1, 1 }), Is.EqualTo(2), "the rejected request must not change behaviour");

            // 纯结构化观察不写目标状态，可以与干预共存。
            var observation = Install("it-observe", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" }, null, null, new MethodHookCaptureSpec { Arguments = true });
            Assert.That((int)add.Invoke(instance, new object[] { 4, 4 }), Is.EqualTo(8));
            var observed = MethodHookHost.Info(new MethodHookInfoRequest { HookId = observation.HookId });
            Assert.That(observed.HitCount, Is.EqualTo(1L), "the observation must receive its own hit while the intervention is active");

            // 停用一个 owner 不得移除另一个 owner 对同一 target 的观察。
            var ownerInfo = MethodHookHost.Info(new MethodHookInfoRequest { HookId = owner.HookId });
            MethodHookHost.ApplyDisable(MethodHookHost.PrepareDisable(new MethodHookDisableRequest { HookId = owner.HookId, EditorSessionId = ownerInfo.EditorSessionId, ExpectedRevision = ownerInfo.Revision }));
            Assert.That((int)add.Invoke(instance, new object[] { 5, 5 }), Is.EqualTo(10));
            Assert.That(MethodHookHost.Info(new MethodHookInfoRequest { HookId = observation.HookId }).HitCount, Is.EqualTo(2L), "the surviving observation must keep dispatching on the shared target");
        }

        [Test]
        public void CallbackFaultStopsOnlyTheAffectedDefinition()
        {
            var faulty = Install("it-fault", "Add", false, "System.Int32", new[] { "System.Int32", "System.Int32" }, "throw new System.InvalidOperationException(\"boom\");", null);
            var healthy = Install("it-healthy", "Describe", false, "System.String", new[] { "System.String" }, null, "ctx.SetResult(\"healthy\");");
            var instance = new HookFixture();

            var add = typeof(HookFixture).GetMethod("Add");
            Assert.That((int)add.Invoke(instance, new object[] { 1, 1 }), Is.EqualTo(2), "a faulty callback must not change the original call");
            var faultyInfo = MethodHookHost.Info(new MethodHookInfoRequest { HookId = faulty.HookId });
            Assert.That(faultyInfo.State, Is.EqualTo("faulted"));
            Assert.That(faultyInfo.LastFault.Code, Is.EqualTo("E_HOOK_CALLBACK_FAILED"));
            Assert.That(faultyInfo.LastFault.Message, Does.Contain("boom"));

            var described = (string)typeof(HookFixture).GetMethod("Describe").Invoke(instance, new object[] { "x" });
            Assert.That(described, Is.EqualTo("healthy"), "an unrelated definition must keep working");
            Assert.That(MethodHookHost.Info(new MethodHookInfoRequest { HookId = healthy.HookId }).State, Is.EqualTo("armed"));
        }

        /// <summary>
        /// 回调失败语义必须在 install 的公开描述里自足：否则调用方会把“hook 已经停止派发、原方法照常执行”
        /// 误认为“逻辑已经被改写”。本断言与上面的行为断言配套，防止描述再次漂移。
        /// </summary>
        [Test]
        public void InstallDescriptorDisclosesCallbackFaultSemantics()
        {
            var description = new MethodHookInstallTool().Describe().Description;
            Assert.That(description, Does.Contain("Callback failure:"));
            Assert.That(description, Does.Contain("E_HOOK_CALLBACK_FAILED"));
            Assert.That(description, Does.Contain("the definition becomes faulted"));
            Assert.That(description, Does.Contain("stops dispatching"));
            Assert.That(description, Does.Contain("keeps its original behaviour"));
            Assert.That(description, Does.Contain("debug.hook-info lastFault"));
            Assert.That(description, Does.Contain("kind=fault"));
            Assert.That(description, Does.Contain("debug.hook-enable"));
        }

        public static class ForeignPatches
        {
            public static void ForeignPostfix(ref string __result)
            {
                __result = "foreign-patched:" + __result;
            }
        }
    }
}
#endif

