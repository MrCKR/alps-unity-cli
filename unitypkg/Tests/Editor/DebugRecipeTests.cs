#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli.Tests
{
    /// <summary>
    /// recipe 导出、分页、校验、导入与生命周期边界的端到端行为测试。
    ///
    /// 测试必须走真实链路：recipe 由 host 从当前 publication 构造，导入走真实 program 编译与 session 发布路径。
    /// 纯校验与分页规则用构造文档直接验证，因为它们不依赖 Unity 状态，且必须能证明 fail closed。
    /// </summary>
    public sealed class DebugRecipeTests
    {
        private const string RecipeFileName = "debug-recipe-tests.json";

        /// <summary>被测 recipe 自己的 session identity；导入必须把它重建为新 session，不得沿用。</summary>
        private const string RecipeSessionId = "dbg-source";

        private static string RecipeDirectory()
        {
            return Path.Combine(UnityCliBridge.ProjectRoot(), "Library", "UnityCli", "tests");
        }

        private static string RecipePath()
        {
            return Path.Combine(RecipeDirectory(), RecipeFileName);
        }

        // ------------------------------------------------------------------
        // 校验与分页
        // ------------------------------------------------------------------

        [Test]
        public void RecipeValidationFailsClosedOnEveryDeclaredRule()
        {
            var project = UnityCliBridge.ProjectRoot();
            Assert.That(DebugRecipeStore.Validate(null, project).Failure, Is.EqualTo(DebugRecipeFailures.ContentInvalid));

            var wrongFormat = Sealed(new Dictionary<string, object> { { "format", "unitycli.debug.recipe/0" } });
            Assert.That(DebugRecipeStore.Validate(wrongFormat, project).Failure, Is.EqualTo(DebugRecipeFailures.FormatUnsupported));

            var noChecksum = new Dictionary<string, object> { { "format", DebugRecipeStore.Format } };
            Assert.That(DebugRecipeStore.Validate(noChecksum, project).Failure, Is.EqualTo(DebugRecipeFailures.ChecksumMissing));

            var tampered = Sealed(MinimalDocument(project));
            tampered["createdAtUtc"] = "tampered-after-sealing";
            Assert.That(DebugRecipeStore.Validate(tampered, project).Failure, Is.EqualTo(DebugRecipeFailures.ChecksumMismatch));

            var otherProject = Sealed(MinimalDocument("D:/another-project"));
            Assert.That(DebugRecipeStore.Validate(otherProject, project).Failure, Is.EqualTo(DebugRecipeFailures.PathMismatch));

            var oversized = MinimalDocument(project);
            oversized["padding"] = new string('x', DebugLimits.MaxRecipeBytes);
            Assert.That(DebugRecipeStore.Validate(Sealed(oversized), project).Failure, Is.EqualTo(DebugRecipeFailures.OverBudget));

            foreach (var excluded in new[] { "pauseSites", "handleId", "threadId", "delegate", "programRuns", "unfinishedCommands" })
            {
                var document = MinimalDocument(project);
                document[excluded] = new List<object> { "live-mechanism-state" };
                var validation = DebugRecipeStore.Validate(Sealed(document), project);
                Assert.That(validation.Failure, Is.EqualTo(DebugRecipeFailures.ExcludedContent), "excluded key " + excluded + " must fail closed");
            }

            var valid = Sealed(MinimalDocument(project));
            var accepted = DebugRecipeStore.Validate(valid, project);
            Assert.That(accepted.Success, Is.True, accepted.Message);
            Assert.That(accepted.Bytes, Is.GreaterThan(0L));
        }

        [Test]
        public void RecipePagesReassembleToTheSameSnapshotAndReplacementExpiresTheCursor()
        {
            var sessionId = "dbg-recipe-page";
            var document = MinimalDocument(UnityCliBridge.ProjectRoot());
            // 多字节内容必须被完整分页，不得产生替换字符或断裂编码。
            document["clocks"] = new List<object> { new Dictionary<string, object> { { "name", "业务时钟-每秒一击" }, { "value", 3.5d } } };
            document = Sealed(document);

            var first = DebugRecipeStore.Publish(sessionId, 7L, "generation-a", document, 64);
            Assert.That(first.Complete, Is.False, "a 64 byte page cannot hold the whole recipe");
            var text = new StringBuilder(first.Fragment);
            var cursor = first.Cursor;
            var guard = 0;
            while (!string.IsNullOrEmpty(cursor))
            {
                string failure;
                string message;
                var page = DebugRecipeStore.ReadPage(sessionId, cursor, 64, out failure, out message);
                Assert.That(page, Is.Not.Null, failure + ": " + message);
                Assert.That(page.SnapshotId, Is.EqualTo(first.SnapshotId), "later pages must read the same snapshot");
                Assert.That(page.Publication, Is.EqualTo(first.Publication));
                text.Append(page.Fragment);
                cursor = page.Cursor;
                guard++;
                Assert.That(guard, Is.LessThan(10000), "paging must terminate");
            }
            Assert.That(text.ToString(), Is.EqualTo(UnityCliSerializer.Serialize(document)), "paging must reproduce the frozen bytes exactly");
            Assert.That(UnityCliSerializer.Parse(text.ToString()), Is.Not.Null, "the reassembled text must be valid JSON");

            // 每个 session 最多一个快照：再次导出替换它，旧 cursor 必须过期。
            var replacement = DebugRecipeStore.Publish(sessionId, 8L, "generation-a", document, 64);
            Assert.That(replacement.SnapshotId, Is.Not.EqualTo(first.SnapshotId));
            string expiredFailure;
            string expiredMessage;
            var expired = DebugRecipeStore.ReadPage(sessionId, first.Cursor, 64, out expiredFailure, out expiredMessage);
            Assert.That(expired, Is.Null);
            Assert.That(expiredFailure, Is.EqualTo(DebugRecipeFailures.CursorExpired));

            DebugRecipeStore.InvalidateAll();
            string missingFailure;
            string missingMessage;
            Assert.That(DebugRecipeStore.ReadPage(sessionId, replacement.Cursor, 64, out missingFailure, out missingMessage), Is.Null);
            Assert.That(missingFailure, Is.EqualTo(DebugRecipeFailures.SnapshotMissing));
        }

        [Test]
        public void RecipeCursorCodecRejectsMalformedValues()
        {
            string snapshotId;
            long offset;
            Assert.That(DebugRecipeStore.TryDecodeCursor("dbg-1@3/1#1024", out snapshotId, out offset), Is.True);
            Assert.That(snapshotId, Is.EqualTo("dbg-1@3/1"));
            Assert.That(offset, Is.EqualTo(1024L));
            foreach (var malformed in new[] { null, string.Empty, "#12", "snapshot#", "snapshot#abc", "snapshot#-4" })
                Assert.That(DebugRecipeStore.TryDecodeCursor(malformed, out snapshotId, out offset), Is.False, "cursor " + malformed + " must be rejected");
        }

        [Test]
        public void DebugStoreObservationsFailClosedOnForeignSessionAndBrokenPayload()
        {
            string reason;
            Assert.That(DebugStore.TryAccept(new DebugStoreObservation(), DebugStore.DefinitionsFormat, out reason), Is.True, "an absent file is an empty store");
            Assert.That(DebugStore.TryAccept(new DebugStoreObservation { Present = true }, DebugStore.DefinitionsFormat, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("file-empty"));
            Assert.That(DebugStore.TryAccept(new DebugStoreObservation { Present = true, Bytes = 10L, Format = "unitycli.debug.definitions/0" }, DebugStore.DefinitionsFormat, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("format-unsupported"));
            Assert.That(DebugStore.TryAccept(new DebugStoreObservation { Present = true, Bytes = 10L, Format = DebugStore.DefinitionsFormat, EditorSessionId = "another-editor-session" }, DebugStore.DefinitionsFormat, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("editor-session-mismatch"));
            Assert.That(DebugStore.TryAccept(new DebugStoreObservation { Present = true, Bytes = 10L, Format = DebugStore.DefinitionsFormat, EditorSessionId = UnityCliBridge.EditorSessionId() }, DebugStore.DefinitionsFormat, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("file-unparsable"));
        }

        [Test]
        public void DebugStorePublicationAcceptsAnAbsentFileAndStillRefusesAForeignChange()
        {
            var file = Path.Combine(RecipeDirectory(), "store-publication-cas.json");
            DeleteRecipe(file);
            var content = new Dictionary<string, object>(StringComparer.Ordinal) { { "publicationSequence", 1L } };
            try
            {
                // 缺文件等价于空存储：观察结果是“没有文件”时，自己的首次发布不得被自己的这个观察拒绝。
                var published = DebugStore.Publish(file, DebugStore.DefinitionsFormat, content, new DebugStoreObservation());
                Assert.That(published.Publication, Is.EqualTo(1L));
                Assert.That(File.Exists(file), Is.True, "a successful publication must write the store file");

                // 第三方已经发布过：基于旧 publication 的期望值必须被拒绝，且不写入任何字节。
                var stale = new DebugStoreObservation
                {
                    Present = true,
                    Parsable = true,
                    EditorSessionId = published.EditorSessionId,
                    DomainGeneration = published.DomainGeneration,
                    Publication = published.Publication - 1L
                };
                var refused = Assert.Throws<BridgeCommandException>(() => DebugStore.Publish(file, DebugStore.DefinitionsFormat, content, stale));
                Assert.That(refused.Code, Is.EqualTo("E_DEBUG_STORE_FAILED"));
                var details = refused.Details as IDictionary<string, object>;
                Assert.That(details, Is.Not.Null);
                Assert.That(Convert.ToString(details["reason"], CultureInfo.InvariantCulture), Is.EqualTo("publication-cas-mismatch"));

                // 第三方删除了文件：基于旧观察的发布同样必须拒绝，而不是把旧内容当成空存储重写。
                DeleteRecipe(file);
                var deleted = Assert.Throws<BridgeCommandException>(() => DebugStore.Publish(file, DebugStore.DefinitionsFormat, content, published));
                Assert.That(deleted.Code, Is.EqualTo("E_DEBUG_STORE_FAILED"));
            }
            finally
            {
                DeleteRecipe(file);
            }
        }

        // ------------------------------------------------------------------
        // 导入
        // ------------------------------------------------------------------

        [Test]
        public void ImportEstablishesANewInactiveSessionWithoutRunningOrReplayingAnything()
        {
            var before = DebugSessionHost.List(new DebugSessionListRequest()).Items.Count;
            var document = MinimalDocument(UnityCliBridge.ProjectRoot());
            document["programs"] = new List<object> { ProgramSection("imported-probe", StepSource) };
            document["hooks"] = new List<object> { HookSection("imported-hook") };
            var recipe = Sealed(document);
            var file = WriteRecipe(recipe);
            DebugSessionImportResult result = null;
            try
            {
                var plan = DebugRecipeHost.PrepareImport(new DebugSessionImportRequest { File = file, Label = "imported-recipe-session" });
                result = DebugRecipeHost.ApplyImportAsync(plan, new TestWaiter(), TimeSpan.FromSeconds(60), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That(result.RestoredWithoutActivation, Is.True, "a recipe import always establishes an inactive definition");
                Assert.That(result.ActiveResourceCount, Is.EqualTo(0L));
                Assert.That(result.ProgramDefinitions, Is.EqualTo(1L));
                Assert.That(result.HookDefinitions, Is.EqualTo(1L));
                Assert.That(result.ReplayedOperations, Is.Empty, "import has no replay path");
                Assert.That(DebugSessionHost.List(new DebugSessionListRequest()).Items.Count, Is.EqualTo(before + 1));

                var info = DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = result.SessionId });
                Assert.That(info.Session.State, Is.EqualTo("open"));
                Assert.That(info.Session.ActiveResources, Is.Empty, "an imported session owns no active resource");

                // 导入的 program 是 dormant：没有任何 listener 被激活，也没有任何运行被建立。
                var program = DebugProgramHost.Info(new DebugProgramInfoRequest { ProgramId = result.ProgramIds[0] });
                Assert.That(program.Program.State, Is.EqualTo("installed"));
                Assert.That(program.ActiveRuns, Is.Empty, "import must not replay a run from the exported session");
                Assert.That(string.IsNullOrEmpty(program.Program.SourceSha256), Is.False, "the actual source text must survive export and import");

                // 导入的 Hook 也是 dormant：没有 detour、没有命中，且已把 recipe 声明的 session 重建为新建 session。
                var hooks = MethodHookHost.ExportDefinitions(result.SessionId);
                Assert.That(hooks.Count, Is.EqualTo(1));
                Assert.That(Convert.ToString(hooks[0]["state"], CultureInfo.InvariantCulture), Is.EqualTo("disabled"));
                var importedListener = hooks[0]["listener"] as IDictionary<string, object>;
                Assert.That(importedListener, Is.Not.Null, "the imported definition keeps the listener the recipe declared");
                Assert.That(
                    Convert.ToString(importedListener["sessionId"], CultureInfo.InvariantCulture),
                    Is.EqualTo(result.SessionId),
                    "the recipe's own session reference must be re-established as the new session, never kept from the exported one");

                // 也没有被任何 program binding 拥有：直接 Hook 入口仍可精确控制它。
                var removal = MethodHookHost.PrepareRemove(new MethodHookRemoveRequest
                {
                    HookId = result.HookIds[0],
                    EditorSessionId = UnityCliBridge.EditorSessionId(),
                    ExpectedRevision = 1L
                });
                Assert.That(removal.Definition.Owner, Is.Empty, "an imported definition is never owned by a program binding");
            }
            finally
            {
                CleanSession(result);
                DeleteRecipe(file);
            }
        }

        [Test]
        public void FailedImportReportsLoadedCandidateAssembliesAndLeavesNoHalfActivatableSession()
        {
            var before = DebugSessionHost.List(new DebugSessionListRequest()).Items.Count;
            var document = MinimalDocument(UnityCliBridge.ProjectRoot());
            document["programs"] = new List<object> { ProgramSection("imported-probe-failure", StepSource) };
            // 坏 Hook 段：无法读成 Hook 定义，因此整个 import 必须失败并回滚。
            document["hooks"] = new List<object> { new Dictionary<string, object> { { "label", "broken" } } };
            var file = WriteRecipe(Sealed(document));
            BridgeCommandException failure = null;
            try
            {
                var plan = DebugRecipeHost.PrepareImport(new DebugSessionImportRequest { File = file, Label = "imported-recipe-failure" });
                try
                {
                    DebugRecipeHost.ApplyImportAsync(plan, new TestWaiter(), TimeSpan.FromSeconds(60), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
                catch (BridgeCommandException exception)
                {
                    failure = exception;
                }
                Assert.That(failure, Is.Not.Null, "a broken hook section must fail the import");
                Assert.That(failure.Code, Is.EqualTo("E_DEBUG_RECIPE_INVALID"));
                var details = failure.Details as IDictionary<string, object>;
                Assert.That(details, Is.Not.Null);
                Assert.That(details.ContainsKey("loadedCandidateAssemblyCount"), Is.True, "a failed import must report already loaded non-unloadable candidate assemblies");
                Assert.That(Convert.ToInt64(details["loadedCandidateAssemblyCount"], CultureInfo.InvariantCulture), Is.GreaterThanOrEqualTo(1L));
                Assert.That(DebugSessionHost.List(new DebugSessionListRequest()).Items.Count, Is.EqualTo(before), "a failed import must not leave a half activatable session");
            }
            finally
            {
                DeleteRecipe(file);
            }
        }

        [Test]
        public void CorruptRecipeFileFailsClosedWithoutTouchingTheStore()
        {
            var file = RecipePath();
            Directory.CreateDirectory(RecipeDirectory());
            File.WriteAllText(file, "{ this is not json", new UTF8Encoding(false));
            try
            {
                BridgeCommandException failure = null;
                try { DebugRecipeHost.PrepareImport(new DebugSessionImportRequest { File = file }); }
                catch (BridgeCommandException exception) { failure = exception; }
                Assert.That(failure, Is.Not.Null);
                Assert.That(failure.Code, Is.EqualTo("E_DEBUG_RECIPE_INVALID"));
                var details = failure.Details as IDictionary<string, object>;
                Assert.That(details, Is.Not.Null);
                Assert.That(Convert.ToString(details["reason"], CultureInfo.InvariantCulture), Is.EqualTo(DebugRecipeFailures.ContentInvalid));
            }
            finally
            {
                DeleteRecipe(file);
            }
        }

        // ------------------------------------------------------------------
        // recipe 往返：现场身份与声明事实的分界
        // ------------------------------------------------------------------

        [Test]
        public void AListenerBearingHookExportsAndReImportsThroughTheSameValidator()
        {
            MethodHookHost.InitializeAfterReload();
            var sessionId = CreateSession("recipe-listener-source");
            var label = "recipe-listener-hook-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var installed = MethodHookHost.ApplyInstall(MethodHookHost.PrepareInstall(new MethodHookInstallRequest
            {
                Label = label,
                Target = ProbeSpec(),
                Activation = "editor-and-play",
                Capture = new MethodHookCaptureSpec { Arguments = true },
                // 真实的现场线程过滤器：它只对本次 Editor 启动成立，因此绝不能进入 recipe。
                Listener = new MethodHookListenerSpec
                {
                    SessionId = sessionId,
                    Trigger = DebugTriggerSources.MethodHit,
                    MaxHits = 4L,
                    ThreadId = Thread.CurrentThread.ManagedThreadId
                }
            }), null);
            DebugSessionImportResult imported = null;
            try
            {
                var text = ExportRecipeText(sessionId);
                var document = DebugRecipeStore.Parse(text);
                Assert.That(document, Is.Not.Null, "a complete export page is a valid recipe document");

                // 导出内容必须能被自己的校验接受：只带现场线程身份的导出以前在这里得到 excluded-content。
                var validation = DebugRecipeStore.Validate(document, UnityCliBridge.ProjectRoot());
                Assert.That(validation.Success, Is.True, validation.Failure + ": " + validation.Message);

                var exported = ExportedListener(document);
                Assert.That(exported.ContainsKey("threadId"), Is.False, "one Editor startup's thread identity must never enter a reusable recipe");
                Assert.That(Convert.ToString(exported["trigger"], CultureInfo.InvariantCulture), Is.EqualTo(DebugTriggerSources.MethodHit), "the declared trigger stays in the recipe");
                Assert.That(Convert.ToInt64(exported["maxHits"], CultureInfo.InvariantCulture), Is.EqualTo(4L), "the declared hit budget stays in the recipe");
                Assert.That(Convert.ToString(exported["sessionId"], CultureInfo.InvariantCulture), Is.EqualTo(sessionId), "the exported listener still names its own session");

                var file = WriteRecipeText(text);
                var importLabel = "imported-listener-recipe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var plan = DebugRecipeHost.PrepareImport(new DebugSessionImportRequest { File = file, Label = importLabel });
                imported = DebugRecipeHost.ApplyImportAsync(plan, new TestWaiter(), TimeSpan.FromSeconds(60), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That(imported.HookDefinitions, Is.EqualTo(1L), "the listener-bearing hook must survive its own round trip");
                Assert.That(imported.ReplayedOperations, Is.Empty, "import has no replay path");

                var info = MethodHookHost.Info(new MethodHookInfoRequest { HookId = imported.HookIds[0] });
                Assert.That(info.Listener, Is.Not.Null, "the imported definition keeps the listener the recipe declared");
                Assert.That(info.Listener.SessionId, Is.EqualTo(imported.SessionId), "the imported listener belongs to the new session, never to the exported one");
                Assert.That(info.Listener.Trigger, Is.EqualTo(DebugTriggerSources.MethodHit));
                Assert.That(info.Listener.MaxHits, Is.EqualTo(4L));
                Assert.That(info.Listener.Hits, Is.EqualTo(0L), "hit counters belong to one Editor startup and are never reused from a recipe");
                Assert.That(info.Listener.ThreadId, Is.EqualTo(0), "the live thread filter is not carried into the new Editor session: the imported listener matches any managed thread until a new filter is declared");

                // 导入的定义再次导出时仍然可导入：声明投影对同一条定义永远只写声明。
                var reExported = ExportedListener(DebugRecipeStore.Parse(ExportRecipeText(imported.SessionId)));
                Assert.That(reExported.ContainsKey("threadId"), Is.False);
                Assert.That(Convert.ToString(reExported["sessionId"], CultureInfo.InvariantCulture), Is.EqualTo(imported.SessionId));
            }
            finally
            {
                CleanSession(imported);
                DeleteRecipe(RecipePath());
                ReleaseHook(installed);
                StopSession(sessionId);
            }
        }

        [Test]
        public void AHandWrittenRecipeCannotReintroduceLiveIdentityOrHandles()
        {
            var before = DebugSessionHost.List(new DebugSessionListRequest()).Items.Count;
            // 排除键集合保持原样：调用方自己手写它们时仍然 fail closed，不因为导出不再写它们而放宽。
            foreach (var mutation in new[] { "threadId", "handleId" })
            {
                var document = MinimalDocument(UnityCliBridge.ProjectRoot());
                var hook = HookSection("hand-written-" + mutation);
                if (string.Equals(mutation, "threadId", StringComparison.Ordinal))
                {
                    var listener = hook["listener"] as IDictionary<string, object>;
                    Assert.That(listener, Is.Not.Null, "the fixture hook declares a listener section");
                    listener["threadId"] = 9L;
                }
                else
                {
                    hook[mutation] = "handle-9";
                }
                document["hooks"] = new List<object> { hook };
                var file = WriteRecipe(Sealed(document));
                try
                {
                    BridgeCommandException failure = null;
                    try { DebugRecipeHost.PrepareImport(new DebugSessionImportRequest { File = file }); }
                    catch (BridgeCommandException exception) { failure = exception; }
                    Assert.That(failure, Is.Not.Null, "a hand-written recipe carrying " + mutation + " must be refused");
                    Assert.That(failure.Code, Is.EqualTo("E_DEBUG_RECIPE_INVALID"));
                    var details = failure.Details as IDictionary<string, object>;
                    Assert.That(details, Is.Not.Null);
                    Assert.That(Convert.ToString(details["reason"], CultureInfo.InvariantCulture), Is.EqualTo(DebugRecipeFailures.ExcludedContent), "the exclusion list stays fail closed on " + mutation);
                }
                finally
                {
                    DeleteRecipe(file);
                }
            }
            Assert.That(DebugSessionHost.List(new DebugSessionListRequest()).Items.Count, Is.EqualTo(before), "a refused recipe must not establish a session");
        }

        // ------------------------------------------------------------------
        // 夹具
        // ------------------------------------------------------------------

        /// <summary>往返用例的真实 detour 目标；只属于测试程序集，不在业务代码里。</summary>
        private sealed class RecipeProbe
        {
            public int PublicField = 7;

            public void Fire(int amount, string name) { }
        }

        private const string StepSource =
            "using UnityCli;\n" +
            "public static class ImportedProbe\n" +
            "{\n" +
            "    public static UnityCliDebugStepResult Step(UnityCliDebugStepContext context) { return UnityCliDebugStepResult.Finish(); }\n" +
            "}\n";

        private static Dictionary<string, object> MinimalDocument(string projectPath)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { DebugRecipeStore.FormatField, DebugRecipeStore.Format },
                { "createdAtUtc", "2026-10-15T00:00:00Z" },
                { DebugRecipeStore.ProjectPathField, projectPath },
                { DebugRecipeStore.SessionField, new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { DebugRecipeStore.SessionIdField, RecipeSessionId },
                        { "label", "source-session" },
                        { "activationScope", "editor-and-play" },
                        { "clockKind", "monotonic" },
                        { "revision", 3L },
                        { "initialState", new Dictionary<string, object> { { "counter", 1 } } }
                    } },
                { "programs", new List<object>() },
                { "hooks", new List<object>() },
                { "checks", new List<object>() },
                { "clocks", new List<object>() }
            };
        }

        private static Dictionary<string, object> ProgramSection(string label, string source)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "label", label },
                { "entryType", "code" },
                { "source", source },
                { "sourceSha256", UnityCliSerializer.Sha256Canonical(new Dictionary<string, object> { { "source", source } }) },
                { "entries", new List<object> { new Dictionary<string, object> { { "name", "Step" }, { "category", "step" }, { "declaringType", "ImportedProbe" } } } },
                { "bindings", new List<object>() }
            };
        }

        private static Dictionary<string, object> HookSection(string label)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "hookId", "hook-from-recipe" },
                { "revision", 4L },
                { "label", label },
                { "priority", 0L },
                { "activation", "editor-and-play" },
                { "targetKey", string.Empty },
                { "target", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "assemblyName", "Assembly-CSharp" },
                        { "typeName", "UnityCliDebugFixture.DebugCounterModel" },
                        { "methodName", "IncrementBy" },
                        { "parameterTypes", new List<object> { "System.Int32" } },
                        { "returnType", "System.Void" },
                        { "isStatic", false }
                    } },
                { "capture", new Dictionary<string, object>(StringComparer.Ordinal) },
                { "listener", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "sessionId", RecipeSessionId },
                        { "trigger", "method-hit" },
                        { "phase", string.Empty },
                        { "maxHits", 0L },
                        { "instanceIdentity", string.Empty },
                        { "conditions", new List<object>() }
                    } },
                { "prefixCode", "UnityCli.HookCallbacks.RecipeProbe.PrefixA(instance, arguments);" },
                { "postfixCode", string.Empty },
                { "emitHitEvents", true }
            };
        }

        private static Dictionary<string, object> Sealed(Dictionary<string, object> document)
        {
            document[DebugRecipeStore.ChecksumField] = DebugRecipeStore.Checksum(document);
            return document;
        }

        private static string WriteRecipe(Dictionary<string, object> document)
        {
            Directory.CreateDirectory(RecipeDirectory());
            var file = RecipePath();
            File.WriteAllText(file, UnityCliSerializer.Serialize(document), new UTF8Encoding(false));
            return file;
        }

        /// <summary>按原样保存一份导出文本；导出内容必须能被导入端逐字节接受。</summary>
        private static string WriteRecipeText(string text)
        {
            Directory.CreateDirectory(RecipeDirectory());
            var file = RecipePath();
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return file;
        }

        /// <summary>建立一个 open session，作为 listener 声明的计划注入 owner。</summary>
        private static string CreateSession(string prefix)
        {
            var plan = DebugSessionHost.PrepareCreate(new DebugSessionCreateRequest
            {
                Label = prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                ActivationScope = UnityCliDebugActivationScopes.EditorAndPlay,
                ClockKind = UnityCliDebugClocks.Monotonic
            });
            return DebugSessionHost.ApplyCreate(plan).SessionId;
        }

        /// <summary>关闭用例建立的 session；清理失败不掩盖用例断言。</summary>
        private static void StopSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            try { DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId })); }
            catch (BridgeCommandException) { }
            try { DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId })); }
            catch (BridgeCommandException) { }
        }

        /// <summary>释放用例直接安装的 hook；清理失败不掩盖用例断言。</summary>
        private static void ReleaseHook(MethodHookInstallResult installed)
        {
            if (installed == null) return;
            try
            {
                MethodHookHost.ApplyRemove(MethodHookHost.PrepareRemove(new MethodHookRemoveRequest
                {
                    HookId = installed.HookId,
                    EditorSessionId = installed.EditorSessionId,
                    ExpectedRevision = installed.Revision
                }));
            }
            catch (Exception) { }
        }

        private static MethodHookTargetSpec ProbeSpec()
        {
            return new MethodHookTargetSpec
            {
                AssemblyName = typeof(RecipeProbe).Assembly.GetName().Name,
                TypeName = typeof(RecipeProbe).FullName,
                MethodName = "Fire",
                ParameterTypes = new List<string> { "System.Int32", "System.String" },
                ReturnType = "System.Void",
                IsStatic = false
            };
        }

        /// <summary>走真实 export 路径读回完整 recipe 文本；夹具文档必须能落在一页内。</summary>
        private static string ExportRecipeText(string sessionId)
        {
            var page = DebugRecipeHost.Export(DebugRecipeHost.PrepareExport(new DebugSessionExportRequest { SessionId = sessionId }));
            Assert.That(page.Complete, Is.True, "the fixture recipe must fit one default page");
            Assert.That(page.Document, Is.Not.Null, "a complete page carries the parsed recipe");
            return page.Fragment;
        }

        /// <summary>取 recipe 里第一条 hook 的 listener 段；缺失即断言失败，而不是返回 null。</summary>
        private static IDictionary<string, object> ExportedListener(Dictionary<string, object> document)
        {
            Assert.That(document, Is.Not.Null, "the exported text must parse as a recipe document");
            var hooks = DebugRecipeStore.Value(document, "hooks") as IEnumerable<object>;
            Assert.That(hooks, Is.Not.Null, "the recipe must carry a hooks section");
            var first = hooks.Cast<object>().FirstOrDefault() as IDictionary<string, object>;
            Assert.That(first, Is.Not.Null, "the listener-bearing hook must appear in the exported hooks section");
            var listener = first["listener"] as IDictionary<string, object>;
            Assert.That(listener, Is.Not.Null, "the exported hook must keep its declared listener");
            return listener;
        }

        private static void DeleteRecipe(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { /* 测试清理失败不影响断言结论 */ }
        }

        /// <summary>测试收尾只删除本次导入建立的定义：先移除 program，再 stop/remove session。</summary>
        private static void CleanSession(DebugSessionImportResult result)
        {
            if (result == null) return;
            foreach (var programId in result.ProgramIds ?? new List<string>())
            {
                try
                {
                    DebugProgramHost.ApplyRemove(DebugProgramHost.PrepareRemove(new DebugProgramRemoveRequest { ProgramId = programId }));
                }
                catch (BridgeCommandException) { }
            }
            foreach (var hookId in result.HookIds ?? new List<string>())
            {
                try { MethodHookHost.RemoveImportedDormant(hookId); }
                catch (BridgeCommandException) { }
            }
            try
            {
                DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = result.SessionId }));
                DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = result.SessionId }));
            }
            catch (BridgeCommandException) { }
        }

        /// <summary>同步 waiter：EditMode 测试在主线程上同步运行，产品路径只用中央 scheduler 的 waiter。</summary>
        private sealed class TestWaiter : IUnityCliWaiter
        {
            private const int PollMilliseconds = 20;

            public UnityCliMainThreadAwaitable<bool> NextFrame(int frames = 1, CancellationToken ct = default)
            {
                return Completed(true);
            }

            public UnityCliMainThreadAwaitable<bool> Delay(TimeSpan duration, CancellationToken ct = default)
            {
                return Poll(() => true, duration, ct);
            }

            public UnityCliMainThreadAwaitable<bool> Until(Func<bool> predicate, TimeSpan timeout, CancellationToken ct = default)
            {
                return Poll(predicate, timeout, ct);
            }

            public UnityCliMainThreadAwaitable<bool> UntilStable(Func<bool> predicate, TimeSpan timeout, int minimumFrames = 2, TimeSpan minimumDuration = default, CancellationToken ct = default)
            {
                return Poll(predicate, timeout, ct);
            }

            public UnityCliMainThreadAwaitable<bool> UntilEditorReady(TimeSpan timeout, CancellationToken ct = default)
            {
                return Poll(() => !UnityEditor.EditorApplication.isCompiling && !UnityEditor.EditorApplication.isUpdating, timeout, ct);
            }

            public UnityCliMainThreadAwaitable<bool> UntilPlayModeState(UnityEditor.PlayModeStateChange state, TimeSpan timeout, CancellationToken ct = default)
            {
                return Poll(() => UnityEditor.EditorApplication.isPlaying == (state == UnityEditor.PlayModeStateChange.EnteredPlayMode || state == UnityEditor.PlayModeStateChange.ExitingPlayMode), timeout, ct);
            }

            public UnityCliMainThreadAwaitable<T> ForCallback<T>(Action<Func<Func<T>, bool>, Func<Exception, bool>> start, TimeSpan timeout, CancellationToken ct = default, Action onAbandoned = null)
            {
                Func<T> complete = null;
                start((factory) => { complete = factory; return true; }, _ => false);
                return Completed(complete == null ? default : complete());
            }

            private static UnityCliMainThreadAwaitable<T> Completed<T>(T value)
            {
                return new UnityCliMainThreadAwaitable<T>(new ImmediateSource<T>(value));
            }

            private static UnityCliMainThreadAwaitable<bool> Poll(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    if (predicate()) return Completed(true);
                    Thread.Sleep(PollMilliseconds);
                }
                return Completed(predicate());
            }

            private sealed class ImmediateSource<T> : IMainThreadAwaitableSource<T>
            {
                private readonly T value;

                internal ImmediateSource(T value) { this.value = value; }

                public bool IsCompleted { get { return true; } }

                public void RegisterContinuation(Action continuation) { if (continuation != null) continuation(); }

                public T GetResult() { return value; }
            }
        }
    }
}
#endif
