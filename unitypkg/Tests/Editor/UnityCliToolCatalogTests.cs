#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliToolCatalogTests
    {
        private Type bridgeType;
        private Type builderType;
        private Type providerKindType;
        private object publishedTemplate;

        [SetUp]
        public void SetUp()
        {
            bridgeType = Type.GetType("UnityCli.UnityCliBridge, UnityCli.Editor", true);
            builderType = Type.GetType("UnityCli.UnityCliToolCatalogBuilder, UnityCli.Editor", true);
            providerKindType = Type.GetType("UnityCli.UnityCliToolProviderKind, UnityCli.Editor", true);
            publishedTemplate = FirstTool(ReadPublishedSnapshot());
        }

        [Test]
        public void StaticConstructorPublishesOneSnapshotAndLaterReadsDoNoDiscoveryWork()
        {
            var first = ReadPublishedSnapshot();
            var second = ReadPublishedSnapshot();
            Assert.That(second, Is.SameAs(first));
            Assert.That((int)Field("projectToolCatalogConstructionCount").GetValue(null), Is.EqualTo(1));
            Assert.That(Int(first, "DiscoveryPassCount"), Is.EqualTo(1));
            Assert.That(Int(first, "AssetDatabaseOperationCount"), Is.Zero);
            Assert.That(Int(first, "SourceFileReadCount"), Is.Zero);
            Assert.That(Int(first, "PersistedToolValueOperationCount"), Is.Zero);
            Assert.That(Int(first, "PerToolSuccessLogCount"), Is.Zero);
        }

        [Test]
        public void MetadataOnlyBuilderPublishesThreeHundredProjectToolsWithProviderCountParity()
        {
            var builder = NewBuilder(400, 20);
            for (var index = 0; index < 301; index++)
                Add(builder, Definition("fixture.project." + index, "project", "native", "fixture"), "ProjectInterface");
            for (var index = 0; index < 2; index++)
                Add(builder, Definition("fixture.builtin." + index, "built-in", "native", "fixture"), "BuiltIn");
            for (var index = 0; index < 3; index++)
                Add(builder, Definition("fixture.extension." + index, "extension", "native", "fixture"), "Extension");
            for (var index = 0; index < 4; index++)
                Add(builder, Definition("UnityPipeline_fixture_" + index, "project", "official-attribute", "UnityPipeline"), "OfficialAttribute");

            var snapshot = Invoke(builder, "Publish", 12L);
            Assert.That(Int(snapshot, "ProjectInterfaceToolCount"), Is.EqualTo(301));
            Assert.That(Int(snapshot, "BuiltInToolCount"), Is.EqualTo(2));
            Assert.That(Int(snapshot, "ExtensionToolCount"), Is.EqualTo(3));
            Assert.That(Int(snapshot, "OfficialAttributeToolCount"), Is.EqualTo(4));
            Assert.That(Int(snapshot, "ToolCount"), Is.EqualTo(310));
            Assert.That(Int(snapshot, "ToolCount"), Is.EqualTo(
                Int(snapshot, "BuiltInToolCount") + Int(snapshot, "ExtensionToolCount")
                + Int(snapshot, "ProjectInterfaceToolCount") + Int(snapshot, "OfficialAttributeToolCount")));
            Assert.That(Int(snapshot, "AssetDatabaseOperationCount"), Is.Zero);
            Assert.That(Int(snapshot, "SourceFileReadCount"), Is.Zero);
            Assert.That(Int(snapshot, "PersistedToolValueOperationCount"), Is.Zero);
            Assert.That(Int(snapshot, "PerToolSuccessLogCount"), Is.Zero);
        }

        [Test]
        public void DuplicateOrInvalidDescriptorFaultsBuilderWithoutPublishingPartialTable()
        {
            var publishedBefore = ReadPublishedSnapshot();
            var duplicate = NewBuilder(10, 2);
            var definition = Definition("fixture.duplicate", "project", "native", "fixture");
            Add(duplicate, definition, "ProjectInterface");
            Assert.That(() => Add(duplicate, Definition("fixture.duplicate", "project", "native", "fixture"), "ProjectInterface"), Throws.TypeOf<TargetInvocationException>());
            Assert.That(() => Invoke(duplicate, "Publish", 1L), Throws.TypeOf<TargetInvocationException>());

            var invalidDescriptor = NewBuilder(10, 2);
            var invalidDefinition = Definition("fixture.invalid", "project", "native", "fixture");
            Property(invalidDefinition, "Description").SetValue(invalidDefinition, string.Empty);
            Assert.That(() => Add(invalidDescriptor, invalidDefinition, "ProjectInterface"), Throws.TypeOf<TargetInvocationException>());
            Assert.That(() => Invoke(invalidDescriptor, "Publish", 1L), Throws.TypeOf<TargetInvocationException>());

            var invalidSchema = NewBuilder(10, 2);
            var schemaDefinition = Definition("fixture.schema", "project", "native", "fixture");
            Property(schemaDefinition, "InputSchema").SetValue(schemaDefinition, "not-json");
            Assert.That(() => Add(invalidSchema, schemaDefinition, "ProjectInterface"), Throws.TypeOf<TargetInvocationException>());
            Assert.That(() => Invoke(invalidSchema, "Publish", 1L), Throws.TypeOf<TargetInvocationException>());

            var invalidCompletion = NewBuilder(10, 2);
            var completionDefinition = Definition("fixture.completion", "project", "native", "fixture");
            Property(completionDefinition, "CompletionContract").SetValue(completionDefinition, null);
            Assert.That(() => Add(invalidCompletion, completionDefinition, "ProjectInterface"), Throws.TypeOf<TargetInvocationException>());
            Assert.That(() => Invoke(invalidCompletion, "Publish", 1L), Throws.TypeOf<TargetInvocationException>());
            Assert.That(ReadPublishedSnapshot(), Is.SameAs(publishedBefore));
        }

        [Test]
        public void UnsupportedOfficialSignatureOrReturnTypeCannotPublishPendingTable()
        {
            var validate = bridgeType.GetMethod("ValidateOfficialMethodSignature", BindingFlags.NonPublic | BindingFlags.Static);
            var invalidMethod = GetType().GetMethod("InstanceOfficialMethod", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(() => validate.Invoke(null, new object[] { invalidMethod }), Throws.TypeOf<TargetInvocationException>());

            var output = bridgeType.GetMethod("BuildOfficialOutputContract", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(() => output.Invoke(null, new object[] { typeof(ValueTask<int>) }), Throws.TypeOf<TargetInvocationException>());

            var builder = NewBuilder(10, 2);
            Invoke(builder, "Abort");
            Assert.That(() => Invoke(builder, "Publish", 1L), Throws.TypeOf<TargetInvocationException>());
        }

        private void InstanceOfficialMethod() { }
        private static void StaticOfficialMethod() { }

        private object ReadPublishedSnapshot()
        {
            var reader = bridgeType.GetProperty("ProjectToolCatalogSnapshotReader", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var read = reader.GetType().GetMethod("Read", BindingFlags.Public | BindingFlags.Instance);
            return read.Invoke(reader, null);
        }

        private static object FirstTool(object snapshot)
        {
            var tools = snapshot.GetType().GetProperty("Tools").GetValue(snapshot);
            var values = tools.GetType().GetProperty("Values").GetValue(tools) as IEnumerable;
            return values.Cast<object>().First();
        }

        private object NewBuilder(int assemblies, int providers)
        {
            var builder = Activator.CreateInstance(builderType, true);
            Invoke(builder, "BeginDiscovery", assemblies, providers);
            return builder;
        }

        private object Definition(string name, string source, string profile, string group)
        {
            var memberwiseClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);
            var definition = memberwiseClone.Invoke(publishedTemplate, null);
            Property(definition, "Name").SetValue(definition, name);
            Property(definition, "Summary").SetValue(definition, "Fixture summary.");
            Property(definition, "Description").SetValue(definition, "Fixture descriptor for pure catalog builder validation.");
            Property(definition, "Group").SetValue(definition, group);
            Property(definition, "Source").SetValue(definition, source);
            Property(definition, "ContractProfile").SetValue(definition, profile);
            Property(definition, "CompletionContract").SetValue(definition, Completion(name, source));
            if (profile == "official-attribute")
            {
                var bindingType = Type.GetType("UnityCli.OfficialAttributeToolBinding, UnityCli.Editor", true);
                var parameterType = Type.GetType("UnityCli.OfficialAttributeParameterBinding, UnityCli.Editor", true);
                var binding = Activator.CreateInstance(bindingType, true);
                Property(binding, "Method").SetValue(binding, GetType().GetMethod("StaticOfficialMethod", BindingFlags.NonPublic | BindingFlags.Static));
                Property(binding, "DeclaredResultType").SetValue(binding, typeof(void));
                Property(binding, "Parameters").SetValue(binding, Activator.CreateInstance(typeof(List<>).MakeGenericType(parameterType)));
                Property(definition, "OfficialAttributeBinding").SetValue(definition, binding);
            }
            else Property(definition, "OfficialAttributeBinding").SetValue(definition, null);
            return definition;
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
