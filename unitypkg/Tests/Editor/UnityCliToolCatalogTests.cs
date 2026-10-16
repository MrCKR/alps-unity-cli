#if UNITY_EDITOR
using System;
using System.Collections;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public static class UnityCliToolCatalogFixtureProbe
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, int> DescribeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        public static void Reset()
        {
            lock (Sync)
            {
                DescribeCounts.Clear();
            }
        }

        public static void RecordDescribe(string providerName)
        {
            lock (Sync)
            {
                int current;
                DescribeCounts.TryGetValue(providerName, out current);
                DescribeCounts[providerName] = current + 1;
            }
        }

        public static Dictionary<string, int> Snapshot()
        {
            lock (Sync)
            {
                return new Dictionary<string, int>(DescribeCounts, StringComparer.Ordinal);
            }
        }
    }

    public sealed class UnityCliToolCatalogTests
    {
        private static Assembly[] compiledProjectToolProviderAssemblies;
        private Type bridgeType;
        private Type builderType;
        private Type providerKindType;
        private Type registeredToolType;
        private Type ownershipType;
        private FieldInfo publishedField;
        private FieldInfo healthField;
        private object publishedTemplate;

        [SetUp]
        public void SetUp()
        {
            bridgeType = Type.GetType("UnityCli.UnityCliBridge, UnityCli.Editor", true);
            builderType = Type.GetType("UnityCli.UnityCliToolCatalogBuilder, UnityCli.Editor", true);
            providerKindType = Type.GetType("UnityCli.UnityCliToolProviderKind, UnityCli.Editor", true);
            registeredToolType = Type.GetType("UnityCli.RegisteredTool, UnityCli.Editor", true);
            ownershipType = Type.GetType("UnityCli.RegistryToolOwnership, UnityCli.Editor", true);
            publishedField = Field("publishedProjectToolCatalog");
            healthField = Field("projectToolCatalogHealth");
            publishedTemplate = FirstValue(Property(publishedField.GetValue(null), "ExecutableTools").GetValue(publishedField.GetValue(null)));
        }

        [Test]
        public void StaticConstructorPublishesOneImmutableDescriptorSnapshotAndLaterReadsDoNoDiscoveryWork()
        {
            var first = ReadPublishedSnapshot();
            var second = ReadPublishedSnapshot();
            Assert.That(second, Is.SameAs(first));
            Assert.That((int)Field("projectToolCatalogConstructionCount").GetValue(null), Is.EqualTo(1));
            Assert.That(Int(first, "DiscoveryPassCount"), Is.EqualTo(1));
            Assert.That(Int(first, "ProviderMetadataReadCount"), Is.EqualTo(Int(first, "ProviderAssemblyCount")));
            Assert.That(Int(first, "AggregateSuccessLogCount"), Is.EqualTo(1));

            var descriptor = FirstTool(first);
            foreach (var privateExecutionMember in new[] { "InvokeAsync", "RequestType", "ResultType", "OfficialAttributeBinding", "OwnerIdentity" })
                Assert.That(descriptor.GetType().GetProperty(privateExecutionMember, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Null, privateExecutionMember);
            Assert.That(descriptor.GetType().GetProperties().Where(property => property.SetMethod != null), Is.Empty);

            var firstExamples = (IList)Invoke(descriptor, "CloneExamples");
            var firstExample = (IDictionary<string, object>)firstExamples[0];
            firstExample["description"] = "mutated test copy";
            var secondExamples = (IList)Invoke(descriptor, "CloneExamples");
            Assert.That(((IDictionary<string, object>)secondExamples[0])["description"], Is.Not.EqualTo("mutated test copy"));
        }

        /// <summary>
        /// 目录守卫：本模块声明的每个 IUnityCliTool 都必须真的进入已发布目录，且目录里每个 Tool 的两侧
        /// schema 都必须能重新派生。schema 派生失败会让 catalog 在构造期隔离该 Tool，于是它在公开 CLI
        /// 上既不可发现也不可调用（describe 只报 E_TOOL_REGISTRATION_FAILED）。
        /// </summary>
        [Test]
        public void EveryToolInTheEditorAssemblyBuildsItsDescriptorAndBothSchemasInThePublishedCatalog()
        {
            var published = Field("publishedProjectToolCatalog").GetValue(null);
            Assert.That(published, Is.Not.Null, "the Tool catalog must be published in the current domain");
            var executableTools = (IDictionary)Property(published, "ExecutableTools").GetValue(published);
            var publishedNames = new HashSet<string>(executableTools.Keys.Cast<object>().Select(key => (string)key), StringComparer.Ordinal);
            var derive = Type.GetType("UnityCli.UnityCliSchemaDeriver, UnityCli.Editor", true)
                .GetMethod("DeriveSchema", BindingFlags.Public | BindingFlags.Static);

            // ① 被隔离的 Tool 根本不在目录里；每个声明的实现都必须能在已发布名称中找到自己的 descriptor。
            foreach (var type in ToolDeclarations())
            {
                var descriptor = DescriptorOf(type);
                Assert.That(publishedNames, Does.Contain(descriptor.Name), RejectionReason(published, descriptor.Name, type));
            }

            // ② 已发布目录里每个 Tool 的 Request/Result schema 都必须能重新派生，空 schema 视为派生失败。
            foreach (DictionaryEntry entry in executableTools)
            {
                var name = (string)entry.Key;
                foreach (var side in new[] { "RequestType", "ResultType" })
                {
                    var dto = (Type)Property(entry.Value, side).GetValue(entry.Value);
                    Assert.That(dto, Is.Not.Null, name + "." + side + " must be declared");
                    AssertSchemaDerives(derive, name, side, dto);
                }
            }
        }

        [Test]
        public void CatalogFingerprintIsCachedStableAndBindsPublishedDescriptorContent()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                var firstBuilder = NewBuilder(1, 1);
                Add(firstBuilder, Definition("fixture.fingerprint", "project", "native", "fixture"), "ProjectInterface");
                var firstSnapshot = PublishThroughProductionSlot(firstBuilder, 1L);
                var firstFingerprint = (string)Property(firstSnapshot, "ToolCatalogFingerprint").GetValue(firstSnapshot);

                var sameBuilder = NewBuilder(1, 1);
                Add(sameBuilder, Definition("fixture.fingerprint", "project", "native", "fixture"), "ProjectInterface");
                var sameSnapshot = PublishThroughProductionSlot(sameBuilder, 999L);
                var sameFingerprint = (string)Property(sameSnapshot, "ToolCatalogFingerprint").GetValue(sameSnapshot);

                var changedBuilder = NewBuilder(1, 1);
                Add(changedBuilder, Definition("fixture.fingerprint", "project", "native", "fixture", "Changed fixture summary."), "ProjectInterface");
                var changedSnapshot = PublishThroughProductionSlot(changedBuilder, 1L);
                var changedFingerprint = (string)Property(changedSnapshot, "ToolCatalogFingerprint").GetValue(changedSnapshot);

                Assert.That(firstFingerprint, Does.Match("^[0-9a-f]{64}$"));
                Assert.That(Property(firstSnapshot, "ToolCatalogFingerprint").GetValue(firstSnapshot), Is.SameAs(firstFingerprint));
                Assert.That(sameFingerprint, Is.EqualTo(firstFingerprint));
                Assert.That(changedFingerprint, Is.Not.EqualTo(firstFingerprint));
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
            }
        }

        [Test]
        public void ProviderMetadataCountersAndThreeHundredOneEntriesFlowThroughProductionPublication()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                UnityCliToolCatalogFixtureProbe.Reset();
                var fixtureAssemblies = CompileProjectToolProviderAssemblies(301);
                var builder = Activator.CreateInstance(builderType, true);
                var collect = bridgeType.GetMethod("CollectProjectToolCatalog", BindingFlags.NonPublic | BindingFlags.Static);
                collect.Invoke(null, new object[] { AppDomain.CurrentDomain.GetAssemblies(), builder });
                var snapshot = PublishThroughProductionSlot(builder, 12L);
                Assert.That(ReadPublishedSnapshot(), Is.SameAs(snapshot));
                Assert.That(Int(snapshot, "ProviderAssemblyCount"), Is.GreaterThanOrEqualTo(302));
                Assert.That(Int(snapshot, "ProjectInterfaceToolCount"), Is.GreaterThanOrEqualTo(301));
                Assert.That(Int(snapshot, "ToolCount"), Is.GreaterThanOrEqualTo(301));

                var descriptors = ((IEnumerable)Property(snapshot, "Descriptors").GetValue(snapshot)).Cast<object>().ToList();
                var fixtureDescriptorNames = descriptors
                    .Select(item => (string)Property(item, "Name").GetValue(item))
                    .Where(name => name.StartsWith("fixture.generated.", StringComparison.Ordinal))
                    .ToList();
                var describeCounts = UnityCliToolCatalogFixtureProbe.Snapshot();

                Assert.That(fixtureAssemblies.Length, Is.EqualTo(301));
                Assert.That(fixtureAssemblies.All(assembly => assembly.GetTypes().Count(type => !type.IsAbstract && type.Name.StartsWith("GeneratedTool", StringComparison.Ordinal)) == 1), Is.True);
                Assert.That(fixtureDescriptorNames.Count, Is.EqualTo(301));
                Assert.That(fixtureDescriptorNames.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(301));
                Assert.That(describeCounts.Count, Is.EqualTo(301));
                Assert.That(describeCounts.Values.All(count => count == 1), Is.True);
                Assert.That(fixtureDescriptorNames, Is.EquivalentTo(describeCounts.Keys));
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
                UnityCliToolCatalogFixtureProbe.Reset();
            }
        }

        [Test]
        public void AbortedCatalogCannotReplaceThePublishedSlot()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                var candidate = NewBuilder(1, 1);
                Add(candidate, Definition("fixture.published", "project", "native", "fixture"), "ProjectInterface");
                PublishThroughProductionSlot(candidate, 1L);
                var publishedCandidate = publishedField.GetValue(null);

                // 只有共享发现基础设施失败的 abort 才使本次 publication 整体不可用。
                var aborted = NewBuilder(1, 1);
                Add(aborted, Definition("fixture.first", "project", "native", "fixture"), "ProjectInterface");
                Invoke(aborted, "Abort");
                Assert.That(() => PublishThroughProductionSlot(aborted, 1L), Throws.TypeOf<TargetInvocationException>());
                Assert.That(publishedField.GetValue(null), Is.SameAs(publishedCandidate));
                Assert.That(ReadPublishedSnapshot(), Is.SameAs(Property(publishedCandidate, "Snapshot").GetValue(publishedCandidate)));
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
            }
        }

        [Test]
        public void IsolatableRegistrationsAreExcludedWithoutAbortingTheRemainingCatalog()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                // 同名冲突：先到者与后来者均不可执行，但发布本身成功；来源按编译身份去重。
                var duplicate = NewBuilder(1, 1);
                Add(duplicate, Definition("fixture.duplicate", "built-in", "native", "fixture"), "BuiltIn");
                Add(duplicate, Definition("fixture.duplicate", "project", "native", "fixture"), "ProjectInterface");
                var duplicateSnapshot = PublishThroughProductionSlot(duplicate, 1L);
                Assert.That(Int(duplicateSnapshot, "ToolCount"), Is.EqualTo(0));
                Assert.That(Int(duplicateSnapshot, "DiagnosticCount"), Is.EqualTo(1));
                var conflict = ((IEnumerable)Property(duplicateSnapshot, "Diagnostics").GetValue(duplicateSnapshot)).Cast<object>().Single();
                Assert.That((string)Property(conflict, "Kind").GetValue(conflict), Is.EqualTo("conflict"));
                Assert.That((string)Property(conflict, "Name").GetValue(conflict), Is.EqualTo("fixture.duplicate"));
                Assert.That(((IEnumerable)Property(conflict, "Sources").GetValue(conflict)).Cast<string>().Count(), Is.EqualTo(2));

                // 单个非法描述符只禁用那个名称，同轮其他健康候选照常发布。
                var mixed = NewBuilder(1, 1);
                Add(mixed, Definition("fixture.healthy", "project", "native", "fixture"), "ProjectInterface");
                Add(mixed, Definition("fixture." + new string('n', 249), "project", "native", "fixture"), "ProjectInterface");
                var mixedSnapshot = PublishThroughProductionSlot(mixed, 1L);
                Assert.That(Int(mixedSnapshot, "ToolCount"), Is.EqualTo(1));
                Assert.That(Int(mixedSnapshot, "DiagnosticCount"), Is.EqualTo(1));
                var descriptorNames = ((IEnumerable)Property(mixedSnapshot, "Descriptors").GetValue(mixedSnapshot))
                    .Cast<object>().Select(item => (string)Property(item, "Name").GetValue(item)).ToList();
                Assert.That(descriptorNames, Is.EqualTo(new[] { "fixture.healthy" }));
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
            }
        }

        [Test]
        public void UnhealthyHealthSnapshotRemainsReadableWhileCatalogReaderFailsClosed()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                var healthType = Type.GetType("UnityCli.UnityCliToolCatalogHealthSnapshot, UnityCli.Editor", true);
                var health = Activator.CreateInstance(healthType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { false, 0, "bounded failure", "reload domain" }, null);
                publishedField.SetValue(null, null);
                healthField.SetValue(null, health);

                var healthReader = bridgeType.GetProperty("ProjectToolCatalogHealthSnapshotReader", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                var observed = healthReader.GetType().GetMethod("Read", BindingFlags.Public | BindingFlags.Instance).Invoke(healthReader, null);
                Assert.That((bool)Property(observed, "Ready").GetValue(observed), Is.False);
                Assert.That((string)Property(observed, "Failure").GetValue(observed), Is.EqualTo("bounded failure"));
                var exception = Assert.Throws<TargetInvocationException>(() => ReadPublishedSnapshot());
                Assert.That(((UnityCliBridge.BridgeCommandException)exception.InnerException).Code, Is.EqualTo("E_TOOL_REGISTRY_UNHEALTHY"));
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
            }
        }

        [Test]
        public void IsolatedProviderFaultsExcludeItsWholeContribution()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                // provider 枚举失败后，它先前贡献的候选也必须一并撤销，不能保留半份结果。
                var builder = NewBuilder(2, 2);
                Add(builder, Definition("fixture.trusted", "project", "native", "fixture"), "ProjectInterface");
                Add(builder, Definition("fixture.partial", "built-in", "native", "fixture"), "BuiltIn");
                Invoke(builder, "RecordProviderFailure", bridgeType.Assembly.GetName().FullName, "provider could not be enumerated completely");
                var snapshot = PublishThroughProductionSlot(builder, 1L);

                var descriptorNames = ((IEnumerable)Property(snapshot, "Descriptors").GetValue(snapshot))
                    .Cast<object>().Select(item => (string)Property(item, "Name").GetValue(item)).ToList();
                Assert.That(descriptorNames.Contains("fixture.partial"), Is.False);
                Assert.That(descriptorNames.Contains("fixture.trusted"), Is.True);
                Assert.That(Int(snapshot, "DiagnosticCount"), Is.EqualTo(1));
                var provider = ((IEnumerable)Property(snapshot, "Diagnostics").GetValue(snapshot)).Cast<object>().Single();
                Assert.That((string)Property(provider, "Kind").GetValue(provider), Is.EqualTo("provider"));
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
            }
        }

        [Test]
        public void ConflictingToolNameIsRejectedAsNameConflictForDescribeAndRun()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                var builder = NewBuilder(1, 1);
                Add(builder, Definition("fixture.conflict", "project", "native", "fixture"), "ProjectInterface");
                Add(builder, Definition("fixture.conflict", "project", "native", "fixture"), "ProjectInterface");
                PublishThroughProductionSlot(builder, 1L);

                // describe 不得静默退化成 found:false，run 不得回退到另一个同名实现。
                var describeRequest = new UnityCli.Protocol.BridgeRequest
                {
                    Input = new Dictionary<string, object> { { "name", "fixture.conflict" } }
                };
                var describeException = Assert.Throws<TargetInvocationException>(() => bridgeType
                    .GetMethod("ProjectToolCatalogDescribe", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { describeRequest, false }));
                var describeFailure = (UnityCliBridge.BridgeCommandException)describeException.InnerException;
                Assert.That(describeFailure.Code, Is.EqualTo("E_TOOL_NAME_CONFLICT"));
                Assert.That(describeFailure.Transient, Is.False);
                Assert.That(describeFailure.RetryAfterMs, Is.EqualTo(0));

                var runRequest = new UnityCli.Protocol.BridgeRequest
                {
                    Input = new Dictionary<string, object> { { "name", "fixture.conflict" }, { "input", new Dictionary<string, object>() } }
                };
                var runValue = (ValueTask<object>)bridgeType
                    .GetMethod("ProjectToolRun", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { runRequest, CancellationToken.None });
                var runFailure = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => runValue.AsTask().GetAwaiter().GetResult());
                Assert.That(runFailure.Code, Is.EqualTo("E_TOOL_NAME_CONFLICT"));
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
            }
        }

        [Test]
        public void ValidateProviderKindMismatchRejectsOnlyTheOffendingCandidate()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                // official-attribute 声明没有保留前缀/group：只禁用该实现，不影响同轮合法名称。
                var builder = NewBuilder(1, 1);
                Add(builder, Definition("fixture.legit", "project", "native", "fixture"), "ProjectInterface");
                Add(builder, Definition("fixture.spoof", "project", "official-attribute", "fixture"), "OfficialAttribute");
                var snapshot = PublishThroughProductionSlot(builder, 1L);
                var descriptorNames = ((IEnumerable)Property(snapshot, "Descriptors").GetValue(snapshot))
                    .Cast<object>().Select(item => (string)Property(item, "Name").GetValue(item)).ToList();
                Assert.That(descriptorNames, Is.EqualTo(new[] { "fixture.legit" }));
                Assert.That(Int(snapshot, "DiagnosticCount"), Is.EqualTo(1));
                var rejected = ((IEnumerable)Property(snapshot, "Diagnostics").GetValue(snapshot)).Cast<object>().Single();
                Assert.That((string)Property(rejected, "Kind").GetValue(rejected), Is.EqualTo("rejected"));
                Assert.That((string)Property(rejected, "Name").GetValue(rejected), Is.EqualTo("fixture.spoof"));
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
            }
        }

        [Test]
        public void MultibyteDescriptorBoundsAndCompleteCatalogEnvelopesUseUtf8Bytes()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                // 超限描述符只隔离该名称；边界仍按 wire UTF-8 字节计算。
                var accepted = NewBuilder(1, 1);
                Add(accepted, Definition("fixture.utf8.accepted", "project", "native", new string('组', 85), new string('界', 170) + "ab"), "ProjectInterface");
                var acceptedSnapshot = PublishThroughProductionSlot(accepted, 1L);
                Assert.That(Int(acceptedSnapshot, "ToolCount"), Is.EqualTo(1));
                Assert.That(Int(acceptedSnapshot, "DiagnosticCount"), Is.EqualTo(0));

                var rejected = NewBuilder(1, 1);
                Add(rejected, Definition("fixture.utf8.too-long", "project", "native", new string('组', 85), new string('界', 170) + "abc"), "ProjectInterface");
                Assert.That(Int(PublishThroughProductionSlot(rejected, 1L), "ToolCount"), Is.EqualTo(0));
                var rejectedGroup = NewBuilder(1, 1);
                Add(rejectedGroup, Definition("fixture.utf8.group-too-long", "project", "native", new string('组', 86)), "ProjectInterface");
                Assert.That(Int(PublishThroughProductionSlot(rejectedGroup, 1L), "ToolCount"), Is.EqualTo(0));

                var builder = NewBuilder(4096, 1);
                for (var index = 0; index < 4096; index++)
                    Add(builder, Definition("fixture.bound." + index.ToString("D4"), "project", "native", "g" + index.ToString("D4"), new string('界', 170)), "ProjectInterface");
                PublishThroughProductionSlot(builder, 1L);
                var summaryRequest = CursorSummaryRequest(null, null);
                ((IDictionary<string, object>)summaryRequest.Input)["limit"] = 101;
                var summary = bridgeType.GetMethod("ProjectToolCatalogSummary", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { summaryRequest, false });
                Assert.That(Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(summary)), Is.LessThanOrEqualTo(256 * 1024));
                Assert.That(((IList)((IDictionary<string, object>)summary)["tools"]).Count, Is.EqualTo(101));
                Assert.That((bool)((IDictionary<string, object>)summary)["hasMore"], Is.True);

                var groupsRequest = new UnityCli.Protocol.BridgeRequest
                {
                    Input = new Dictionary<string, object> { { "reservedNames", new List<object>() } }
                };
                var groups = bridgeType.GetMethod("ProjectToolCatalogGroups", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { groupsRequest, false });
                Assert.That(Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(groups)), Is.LessThanOrEqualTo(2 * 1024 * 1024));
                Assert.That(((IList)((IDictionary<string, object>)groups)["groups"]).Count, Is.EqualTo(4096));
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
            }
        }

        [Test]
        public void SerializedSummaryBudgetTruncatesWithAnAccurateSignedBoundary()
        {
            var names = Enumerable.Range(0, 101).Select(index => "fixture.bytes." + index.ToString("D3")).ToList();
            var pageNames = names.ToList();
            var tools = names.Select(name => (object)new Dictionary<string, object>
            {
                { "name", name }, { "summary", new string('界', 170) + "ab" }, { "group", "fixture" },
                { "target", "editor" }, { "contractProfile", "native" }, { "commandName", name },
                { "available", false }, { "unavailableReason", new string('界', 682) }
            }).ToList();
            var bounded = (IDictionary<string, object>)bridgeType.GetMethod("BoundSignedCatalogSummaryData", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { new string('g', 64), 101, names, 0, pageNames, tools, new string('s', 64) });
            Assert.That(Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(bounded)), Is.LessThanOrEqualTo(256 * 1024));
            var emittedNames = ((IEnumerable)bounded["pageNames"]).Cast<object>().Select(Convert.ToString).ToList();
            Assert.That(emittedNames.Count, Is.InRange(1, 100));
            Assert.That((bool)bounded["hasMore"], Is.True);
            var tokenFields = Encoding.UTF8.GetString(FromBase64Url(((string)bounded["continuationToken"]).Split('.')[0])).Split('\n');
            Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(tokenFields[2])), Is.EqualTo(emittedNames.Last()));
        }

        [Test]
        public void EditorContinuationRejectsAlteredExistingAndUnknownBoundariesInSameGeneration()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                var builder = NewBuilder(1, 1);
                foreach (var name in new[] { "fixture.cursor.a", "fixture.cursor.b", "fixture.cursor.c" })
                    Add(builder, Definition(name, "project", "native", "fixture"), "ProjectInterface");
                PublishThroughProductionSlot(builder, 1L);
                var summary = bridgeType.GetMethod("ProjectToolCatalogSummary", BindingFlags.NonPublic | BindingFlags.Static);
                var firstRequest = CursorSummaryRequest(null, null);
                var first = (IDictionary<string, object>)summary.Invoke(null, new object[] { firstRequest, false });
                var token = (string)first["continuationToken"];
                var generation = (string)first["catalogGeneration"];
                foreach (var boundary in new[] { "fixture.cursor.c", "fixture.cursor.unknown" })
                {
                    var request = CursorSummaryRequest(generation, AlterCursorBoundary(token, boundary));
                    var exception = Assert.Throws<TargetInvocationException>(() => summary.Invoke(null, new object[] { request, false }));
                    Assert.That(((UnityCliBridge.BridgeCommandException)exception.InnerException).Code, Is.EqualTo("E_DISCOVERY_CURSOR_INVALID"), boundary);
                }
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
            }
        }

        [Test]
        public void EditorDescribeAcceptsOneExactFallbackWhileRuntimeDescribeRejectsIt()
        {
            var describe = bridgeType.GetMethod("ProjectToolCatalogDescribe", BindingFlags.NonPublic | BindingFlags.Static);
            var request = new UnityCli.Protocol.BridgeRequest
            {
                Input = new Dictionary<string, object> { { "name", "fixture.missing" }, { "fallbackName", "fixture.also-missing" } }
            };
            Assert.That(() => describe.Invoke(null, new object[] { request, false }), Throws.Nothing);
            var exception = Assert.Throws<TargetInvocationException>(() => describe.Invoke(null, new object[] { request, true }));
            Assert.That(((UnityCliBridge.BridgeCommandException)exception.InnerException).Code, Is.EqualTo("E_SCHEMA_INVALID"));
        }

        [Test]
        public void PrivateCatalogReadersRejectUnknownInputBeforeSnapshotProjection()
        {
            var request = new UnityCli.Protocol.BridgeRequest
            {
                Input = new Dictionary<string, object> { { "limit", 20L }, { "unknown", true } }
            };
            var summary = bridgeType.GetMethod("ProjectToolCatalogSummary", BindingFlags.NonPublic | BindingFlags.Static);
            var exception = Assert.Throws<TargetInvocationException>(() => summary.Invoke(null, new object[] { request, false }));
            Assert.That(((UnityCliBridge.BridgeCommandException)exception.InnerException).Code, Is.EqualTo("E_SCHEMA_INVALID"));
        }

        [Test]
        public void UnsupportedOfficialSignatureOrReturnTypeCannotPublishPendingTable()
        {
            var validate = bridgeType.GetMethod("ValidateOfficialMethodSignature", BindingFlags.NonPublic | BindingFlags.Static);
            var invalidMethod = GetType().GetMethod("InstanceOfficialMethod", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(() => validate.Invoke(null, new object[] { invalidMethod }), Throws.TypeOf<TargetInvocationException>());

            var output = bridgeType.GetMethod("BuildOfficialOutputContract", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(() => output.Invoke(null, new object[] { typeof(ValueTask<int>) }), Throws.TypeOf<TargetInvocationException>());
        }

        private void InstanceOfficialMethod() { }
        private static UnityCli.Protocol.BridgeRequest CursorSummaryRequest(string generation, string token)
        {
            var input = new Dictionary<string, object>
            {
                { "limit", 1L }, { "reservedNames", new List<object>() }, { "localNames", new List<object>() },
                { "cursorContext", new string('a', 64) }
            };
            if (generation != null) input["expectedCatalogGeneration"] = generation;
            if (token != null) input["cursorToken"] = token;
            return new UnityCli.Protocol.BridgeRequest { Input = input };
        }

        private static string AlterCursorBoundary(string token, string boundary)
        {
            var parts = token.Split('.');
            var payload = Encoding.UTF8.GetString(FromBase64Url(parts[0])).Split('\n');
            payload[2] = Convert.ToBase64String(Encoding.UTF8.GetBytes(boundary));
            return ToBase64Url(Encoding.UTF8.GetBytes(string.Join("\n", payload))) + "." + parts[1];
        }

        private static string ToBase64Url(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] FromBase64Url(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            return Convert.FromBase64String(normalized);
        }

        private static void StaticOfficialMethod() { }

        // UnityCli.Editor 里声明的具体 Tool 实现；抽象基类与接口不是注册候选。
        private static List<Type> ToolDeclarations()
        {
            var toolInterface = typeof(IUnityCliTool<,>);
            return toolInterface.Assembly.GetTypes()
                .Where(type => !type.IsAbstract && !type.IsInterface
                    && type.GetInterfaces().Any(item => item.IsGenericType && item.GetGenericTypeDefinition() == toolInterface))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();
        }

        private static ToolDescriptor DescriptorOf(Type type)
        {
            var describe = type.GetMethod("Describe", Type.EmptyTypes);
            Assert.That(describe, Is.Not.Null, type.FullName + " must expose Describe");
            var descriptor = describe.Invoke(Activator.CreateInstance(type), null) as ToolDescriptor;
            Assert.That(descriptor, Is.Not.Null, type.FullName + ".Describe must return a ToolDescriptor");
            Assert.That(descriptor.Name, Is.Not.Null, type.FullName + " must declare a Tool name");
            Assert.That(descriptor.Name, Is.Not.Empty, type.FullName + " must declare a Tool name");
            return descriptor;
        }

        // 派生失败把真实原因写在断言消息里，而不是变成一个裸的 TargetInvocationException。
        private static void AssertSchemaDerives(MethodInfo derive, string toolName, string side, Type dto)
        {
            string schema;
            try
            {
                schema = (string)derive.Invoke(null, new object[] { dto });
            }
            catch (TargetInvocationException exception)
            {
                Exception root = exception;
                while (root.InnerException != null) root = root.InnerException;
                Assert.Fail(toolName + " " + side + " schema cannot be derived for " + dto.FullName + ": " + root.Message);
                return;
            }
            Assert.That(schema, Is.Not.Null, toolName + " " + side + " schema must derive");
            Assert.That(schema, Is.Not.Empty, toolName + " " + side + " schema must derive");
        }

        // 被隔离时给出该名称/该实现类型的真实拒绝原因与来源，便于直接定位 DTO 或 descriptor 缺陷。
        private static string RejectionReason(object published, string name, Type type)
        {
            var snapshot = Property(published, "Snapshot").GetValue(published);
            var diagnostics = ((IEnumerable)Property(snapshot, "Diagnostics").GetValue(snapshot)).Cast<object>();
            var matches = diagnostics
                .Where(item => string.Equals((string)Property(item, "Name").GetValue(item), name, StringComparison.Ordinal)
                    || string.Equals((string)Property(item, "Name").GetValue(item), type.FullName, StringComparison.Ordinal)
                    || ((IEnumerable)Property(item, "Sources").GetValue(item)).Cast<object>().Any(source => Convert.ToString(source).Contains(type.FullName)))
                .Select(item => (string)Property(item, "Kind").GetValue(item) + " " + (string)Property(item, "Name").GetValue(item) + ": " + (string)Property(item, "Reason").GetValue(item))
                .ToArray();
            return "Tool " + name + " (" + type.FullName + ") is absent from the published catalog"
                + (matches.Length == 0 ? "; the catalog reported no diagnostic for it." : "; catalog diagnostics: " + string.Join(" | ", matches));
        }

        private object ReadPublishedSnapshot()
        {
            var reader = bridgeType.GetProperty("ProjectToolCatalogSnapshotReader", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            return reader.GetType().GetMethod("Read", BindingFlags.Public | BindingFlags.Instance).Invoke(reader, null);
        }

        private static object FirstTool(object snapshot)
        {
            var descriptors = Property(snapshot, "Descriptors").GetValue(snapshot) as IEnumerable;
            return descriptors.Cast<object>().First();
        }

        private static object FirstValue(object dictionary)
        {
            var values = Property(dictionary, "Values").GetValue(dictionary) as IEnumerable;
            return values.Cast<object>().First();
        }

        private object NewBuilder(int assemblies, int providers)
        {
            var builder = Activator.CreateInstance(builderType, true);
            Invoke(builder, "BeginDiscovery", assemblies, providers);
            for (var index = 0; index < providers; index++) Invoke(builder, "RecordProviderMetadata", "fixture.provider." + index);
            return builder;
        }

        private object Definition(string name, string source, string profile, string group, string summary = "Fixture summary.")
        {
            var descriptor = new ToolDescriptor
            {
                Name = name,
                Summary = summary,
                Description = "Fixture descriptor for production catalog publication validation.",
                Group = group,
                DestructiveHint = false,
                SideEffects = new string[0],
                SupportsDryRun = false,
                Permissions = new string[0],
                RequiresPackages = new string[0],
                Examples = new[] { new ToolExample { Description = "Fixture", Input = new Dictionary<string, object>() } },
                Target = "editor",
                PlayModeSupport = profile == "official-attribute" ? "unknown" : "supported",
                DefaultTimeoutMs = 30000,
                MaxTimeoutMs = 60000
            };
            var implementationType = source == "built-in" ? bridgeType : GetType();
            var extensionId = source == "extension" ? "fixture" : null;
            var ownership = Activator.CreateInstance(ownershipType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { source, extensionId, implementationType.Assembly, implementationType }, null);
            object binding = null;
            if (profile == "official-attribute")
            {
                var bindingType = Type.GetType("UnityCli.OfficialAttributeToolBinding, UnityCli.Editor", true);
                var parameterType = Type.GetType("UnityCli.OfficialAttributeParameterBinding, UnityCli.Editor", true);
                binding = Activator.CreateInstance(bindingType, true);
                Property(binding, "Method").SetValue(binding, GetType().GetMethod("StaticOfficialMethod", BindingFlags.NonPublic | BindingFlags.Static));
                Property(binding, "DeclaredResultType").SetValue(binding, typeof(void));
                Property(binding, "Parameters").SetValue(binding, Activator.CreateInstance(typeof(List<>).MakeGenericType(parameterType)));
            }
            var invoke = Property(publishedTemplate, "InvokeAsync").GetValue(publishedTemplate);
            var constructor = registeredToolType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            return constructor.Invoke(new[]
            {
                descriptor, ownership, "{}", "{}", "editorSession", profile,
                typeof(Dictionary<string, object>), typeof(Dictionary<string, object>), Completion(name, source), binding, invoke
            });
        }

        private static Assembly[] CompileProjectToolProviderAssemblies(int count)
        {
            if (compiledProjectToolProviderAssemblies != null && compiledProjectToolProviderAssemblies.Length == count)
                return compiledProjectToolProviderAssemblies;

            var assemblies = new List<Assembly>(count);
            for (var index = 0; index < count; index++)
                assemblies.Add(CompileProjectToolProviderAssembly(index));

            compiledProjectToolProviderAssemblies = assemblies.ToArray();
            return compiledProjectToolProviderAssemblies;
        }

        private static Assembly CompileProjectToolProviderAssembly(int index)
        {
            var suffix = index.ToString("D3");
            var source = new StringBuilder();
            source.AppendLine("using System.Collections.Generic; using System.Threading; using System.Threading.Tasks; using UnityCli;");
            source.AppendLine("namespace UnityCli.GeneratedCatalogFixture" + suffix + " {");
            source.AppendLine("public sealed class FixtureRequest {} public sealed class FixtureResult {}");
            source.AppendLine("public sealed class GeneratedTool" + suffix + " : IUnityCliTool<FixtureRequest, FixtureResult> {");
            source.AppendLine("public ToolDescriptor Describe() { global::UnityCli.Tests.UnityCliToolCatalogFixtureProbe.RecordDescribe(\"fixture.generated." + suffix + "\"); return new ToolDescriptor { Name=\"fixture.generated." + suffix
                + "\", Summary=\"Generated fixture.\", Description=\"Production collector fixture.\", Group=\"fixture\", Examples=new[]{new ToolExample{Description=\"Fixture\", Input=new Dictionary<string, object>()}}, Completion=new ToolCompletionDescriptor{ExecutionClass=\"atomic\", CompletionPredicate=\"typed-business-terminal\", ProofSource=\"typed-result-and-effect-readback\", ReloadPolicy=\"caller-responsibility\", TimeoutError=\"E_EXECUTION_TIMEOUT\", OutcomeUnknownPolicy=\"caller-responsibility\", CompletionDriverId=\"atomic-typed-result-v1\"} }; }");
            source.AppendLine("public ValueTask<FixtureResult> ExecuteAsync(FixtureRequest request, UnityCliProjectToolContext ctx, CancellationToken ct) { return new ValueTask<FixtureResult>(new FixtureResult()); }}");
            source.AppendLine("}");
            using (var provider = new CSharpCodeProvider())
            {
                var parameters = new CompilerParameters
                {
                    GenerateExecutable = false,
                    GenerateInMemory = true,
                    CompilerOptions = "/langversion:latest",
                    OutputAssembly = "UnityCli.GeneratedCatalogFixture" + suffix + ".dll"
                };
                parameters.ReferencedAssemblies.Add("System.dll");
                parameters.ReferencedAssemblies.Add("System.Core.dll");
                parameters.ReferencedAssemblies.Add(typeof(IUnityCliTool<,>).Assembly.Location);
                parameters.ReferencedAssemblies.Add(typeof(UnityCliToolCatalogFixtureProbe).Assembly.Location);
                var result = provider.CompileAssemblyFromSource(parameters, source.ToString());
                var failures = result.Errors.Cast<CompilerError>().Where(error => !error.IsWarning).Select(error => error.ToString()).ToArray();
                Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
                return result.CompiledAssembly;
            }
        }

        private object Completion(string name, string source)
        {
            var type = Type.GetType("UnityCli.UnityCliToolCompletionContract, UnityCli.Editor", true);
            var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var owner = source == "extension" ? "extension:fixture" : source;
            return constructor.Invoke(new object[]
            {
                name, owner, "direct-terminal", "atomic", "typed-result-schema-v1", "typed-handler-result",
                "fail-before-effect", "E_EXECUTION_TIMEOUT", "not-applicable-read-only", "direct-typed-result-v1", "none", false
            });
        }

        private object PublishThroughProductionSlot(object builder, long elapsedMilliseconds)
        {
            var method = bridgeType.GetMethod("PublishProjectToolCatalog", BindingFlags.NonPublic | BindingFlags.Static);
            return method.Invoke(null, new[] { builder, (object)elapsedMilliseconds });
        }

        private void Add(object builder, object definition, string providerKind)
        {
            Invoke(builder, "Add", definition, Enum.Parse(providerKindType, providerKind));
        }

        private static object Invoke(object target, string name, params object[] args)
        {
            var method = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return method.Invoke(target, args);
        }

        private FieldInfo Field(string name)
        {
            return bridgeType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingFieldException(name);
        }

        private static PropertyInfo Property(object target, string name)
        {
            return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingMemberException(name);
        }

        private static int Int(object target, string name)
        {
            return Convert.ToInt32(Property(target, name).GetValue(target));
        }
    }
}
#endif
