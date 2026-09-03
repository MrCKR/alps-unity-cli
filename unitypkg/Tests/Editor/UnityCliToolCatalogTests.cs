#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliToolCatalogTests
    {
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
            Assert.That(Int(first, "AssetDatabaseOperationCount"), Is.Zero);
            Assert.That(Int(first, "SourceFileReadCount"), Is.Zero);
            Assert.That(Int(first, "PersistedToolValueOperationCount"), Is.Zero);
            Assert.That(Int(first, "PerToolSuccessLogCount"), Is.Zero);

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
        public void ProviderMetadataCountersAndThreeHundredOneEntriesFlowThroughProductionPublication()
        {
            var previousCatalog = publishedField.GetValue(null);
            var previousHealth = healthField.GetValue(null);
            try
            {
                var builder = NewBuilder(400, 301);
                for (var index = 0; index < 301; index++)
                    Add(builder, Definition("fixture.project." + index, "project", "native", "fixture"), "ProjectInterface");
                var snapshot = PublishThroughProductionSlot(builder, 12L);
                Assert.That(ReadPublishedSnapshot(), Is.SameAs(snapshot));
                Assert.That(Int(snapshot, "ProviderAssemblyCount"), Is.EqualTo(301));
                Assert.That(Int(snapshot, "ProjectInterfaceToolCount"), Is.EqualTo(301));
                Assert.That(Int(snapshot, "ToolCount"), Is.EqualTo(301));
                Assert.That(Int(snapshot, "DiscoveryPassCount"), Is.EqualTo(1));
                Assert.That(Int(snapshot, "ProviderMetadataReadCount"), Is.EqualTo(301));
                Assert.That(Int(snapshot, "AggregateSuccessLogCount"), Is.EqualTo(1));
                Assert.That(Int(snapshot, "AssetDatabaseOperationCount"), Is.Zero);
                Assert.That(Int(snapshot, "SourceFileReadCount"), Is.Zero);
                Assert.That(Int(snapshot, "PersistedToolValueOperationCount"), Is.Zero);
                Assert.That(Int(snapshot, "PerToolSuccessLogCount"), Is.Zero);
            }
            finally
            {
                publishedField.SetValue(null, previousCatalog);
                healthField.SetValue(null, previousHealth);
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

        private object Definition(string name, string source, string profile, string group)
        {
            var descriptor = new ToolDescriptor
            {
                Name = name,
                Summary = "Fixture summary.",
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
