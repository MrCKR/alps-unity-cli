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

        [Test]
        public void CatalogFingerprintIsCachedStableAndBindsPublishedDescriptorContent()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                var firstBuilder = NewBuilder(1, 1);
                Add(firstBuilder, Definition("fixture.fingerprint", "project", "native", "fixture"), "ProjectInterface");
                var first = PublishThroughProductionSlot(firstBuilder, 1L);
                var firstSnapshot = Property(first, "Snapshot").GetValue(first);
                var firstFingerprint = (string)Property(firstSnapshot, "ToolCatalogFingerprint").GetValue(firstSnapshot);

                var sameBuilder = NewBuilder(1, 1);
                Add(sameBuilder, Definition("fixture.fingerprint", "project", "native", "fixture"), "ProjectInterface");
                var same = PublishThroughProductionSlot(sameBuilder, 999L);
                var sameFingerprint = (string)Property(Property(same, "Snapshot").GetValue(same), "ToolCatalogFingerprint").GetValue(Property(same, "Snapshot").GetValue(same));

                var changedBuilder = NewBuilder(1, 1);
                Add(changedBuilder, Definition("fixture.fingerprint", "project", "native", "fixture", "Changed fixture summary."), "ProjectInterface");
                var changed = PublishThroughProductionSlot(changedBuilder, 1L);
                var changedFingerprint = (string)Property(Property(changed, "Snapshot").GetValue(changed), "ToolCatalogFingerprint").GetValue(Property(changed, "Snapshot").GetValue(changed));

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
        public void NthEntryFailureCannotReplaceThePublishedSlot()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                var candidate = NewBuilder(1, 1);
                Add(candidate, Definition("fixture.published", "project", "native", "fixture"), "ProjectInterface");
                PublishThroughProductionSlot(candidate, 1L);
                var publishedCandidate = publishedField.GetValue(null);

                var failed = NewBuilder(1, 1);
                Add(failed, Definition("fixture.first", "project", "native", "fixture"), "ProjectInterface");
                Assert.That(() => Add(failed, Definition("fixture.first", "project", "native", "fixture"), "ProjectInterface"), Throws.TypeOf<TargetInvocationException>());
                Assert.That(() => PublishThroughProductionSlot(failed, 1L), Throws.TypeOf<TargetInvocationException>());
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
        public void DuplicateInvalidOrOversizedDescriptorFaultsBuilderWithoutPublication()
        {
            var duplicate = NewBuilder(1, 1);
            Add(duplicate, Definition("fixture.duplicate", "project", "native", "fixture"), "ProjectInterface");
            Assert.That(() => Add(duplicate, Definition("fixture.duplicate", "project", "native", "fixture"), "ProjectInterface"), Throws.TypeOf<TargetInvocationException>());
            Assert.That(() => PublishThroughProductionSlot(duplicate, 1L), Throws.TypeOf<TargetInvocationException>());

            var oversized = NewBuilder(1, 1);
            Assert.That(() => Add(oversized, Definition("fixture." + new string('n', 249), "project", "native", "fixture"), "ProjectInterface"), Throws.TypeOf<TargetInvocationException>());
            Assert.That(() => PublishThroughProductionSlot(oversized, 1L), Throws.TypeOf<TargetInvocationException>());
        }

        [Test]
        public void MultibyteDescriptorBoundsAndCompleteCatalogEnvelopesUseUtf8Bytes()
        {
            var accepted = NewBuilder(1, 1);
            Assert.That(() => Add(accepted, Definition("fixture.utf8.accepted", "project", "native", new string('组', 85), new string('界', 170) + "ab"), "ProjectInterface"), Throws.Nothing);

            var rejected = NewBuilder(1, 1);
            Assert.That(() => Add(rejected, Definition("fixture.utf8.too-long", "project", "native", new string('组', 85), new string('界', 170) + "abc"), "ProjectInterface"),
                Throws.TypeOf<TargetInvocationException>());
            var rejectedGroup = NewBuilder(1, 1);
            Assert.That(() => Add(rejectedGroup, Definition("fixture.utf8.group-too-long", "project", "native", new string('组', 86)), "ProjectInterface"),
                Throws.TypeOf<TargetInvocationException>());

            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
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
