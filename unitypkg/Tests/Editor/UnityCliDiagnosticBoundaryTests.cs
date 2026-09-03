#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnityCli.Tests
{
    [NonParallelizable]
    public sealed class UnityCliDiagnosticBoundaryTests
    {
        private static readonly Type BridgeType = Type.GetType("UnityCli.UnityCliBridge, UnityCli.Editor", true);

        [Test]
        public void DoctorCatalogHandlerReadsThePublishedSnapshotWithoutAnotherConstruction()
        {
            var constructionCounter = Field("projectToolCatalogConstructionCount");
            var reader = BridgeType.GetProperty("ProjectToolCatalogSnapshotReader", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null, null);
            var snapshot = reader.GetType().GetMethod("Read", BindingFlags.Public | BindingFlags.Instance).Invoke(reader, null);
            var expectedToolCount = Convert.ToInt64(snapshot.GetType().GetProperty("ToolCount").GetValue(snapshot, null));
            var registry = Method("CreateCommandRegistry").Invoke(null, null);
            var requestType = Type.GetType("UnityCli.Protocol.BridgeRequest, UnityCli.Editor", true);
            var request = Activator.CreateInstance(requestType);
            requestType.GetProperty("Command").SetValue(request, "internal.doctor.catalog", null);
            requestType.GetProperty("RequestId").SetValue(request, "doctor-catalog-owner", null);
            requestType.GetProperty("Input").SetValue(request, new Dictionary<string, object>(), null);

            var before = Convert.ToInt64(constructionCounter.GetValue(null));
            var pending = (ValueTask<object>)registry.GetType().GetMethod("ExecuteAsync").Invoke(registry, new[] { request, (object)CancellationToken.None });
            var result = (IDictionary<string, object>)pending.AsTask().GetAwaiter().GetResult();
            var after = Convert.ToInt64(constructionCounter.GetValue(null));

            Assert.That(after, Is.EqualTo(before));
            Assert.That(result["identity"], Is.EqualTo("unity-cli-immutable-domain-catalog"));
            Assert.That(Convert.ToInt64(result["toolCount"]), Is.EqualTo(expectedToolCount));
            Assert.That(result["providerCounts"], Is.InstanceOf<IDictionary<string, object>>());
            Assert.That(result["publicationDiagnostics"], Is.InstanceOf<IDictionary<string, object>>());
            var serialized = Type.GetType("UnityCli.UnityCliSerializer, UnityCli.Editor", true)
                .GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { result }) as string;
            Assert.That(Encoding.UTF8.GetByteCount(serialized), Is.LessThanOrEqualTo(64 * 1024));
            Assert.That(serialized, Does.Not.Contain("fingerprint").IgnoreCase);
            Assert.That(serialized, Does.Not.Contain("rePinValue"));
        }

        [Test]
        public void ProjectToolNamesUseProfileAwareGrammarAndOneUtf8Budget()
        {
            var validator = Type.GetType("UnityCli.UnityCliToolName, UnityCli.Editor", true);
            var validate = validator.GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { "scene.data", "native" }));
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { new string('a', 128), "native" }));
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { "UnityPipeline_hotfix_container_inspect", "official-attribute" }));
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { "UnityPipeline_project_reload_data", "official-attribute" }));

            AssertInvalidName(validate, "UnityPipeline_project_reload_data", "native");
            AssertInvalidName(validate, "project_reload_data", "official-attribute");
            AssertInvalidName(validate, "Scene.Data", "native");
            AssertInvalidName(validate, "scene..data", "native");
            AssertInvalidName(validate, "scene.数据", "native");
            AssertInvalidName(validate, new string('a', 129), "native");
            AssertInvalidName(validate, "scene.data\n", "native");
            AssertInvalidName(validate, "scene.data\r\n", "native");
            AssertInvalidName(validate, "scene.data ", "native");
            AssertInvalidName(validate, "UnityPipeline_project_reload_data\n", "official-attribute");
            AssertInvalidName(validate, "UnityPipeline_project_reload_data\r\n", "official-attribute");
            AssertInvalidName(validate, "UnityPipeline_project_reload_data\t", "official-attribute");
        }

        [Test]
        public void CodeExecBridgeEnvelopeUsesTerminalCauseBeyondEightLevels()
        {
            const string terminalMessage = "terminal-actionable-cause";
            Exception exception = new InvalidOperationException(terminalMessage);
            for (var index = 0; index < 12; index++) exception = new TargetInvocationException("wrapper-" + index, exception);

            var result = Method("ExceptionFailure").Invoke(null, new object[]
            {
                "E_CODE_RUNTIME_EXCEPTION", string.Empty, exception, string.Empty, string.Empty,
                "Inspect the bounded runtime exception cause chain and affected business state."
            });
            var envelope = (string)Method("SerializeCodeExecutionResult").Invoke(null, new[] { "code-exec-deep-cause", result });
            var serializer = Type.GetType("UnityCli.UnityCliSerializer, UnityCli.Editor", true);
            var root = (IDictionary<string, object>)serializer.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { envelope });
            var error = (IDictionary<string, object>)root["error"];
            var details = (IDictionary<string, object>)error["details"];
            var causes = (IList)details["causes"];
            var terminal = (IDictionary<string, object>)causes[causes.Count - 1];

            Assert.That(error["code"], Is.EqualTo("E_CODE_RUNTIME_EXCEPTION"));
            Assert.That(error["message"], Is.EqualTo(terminalMessage));
            Assert.That(details["message"], Is.EqualTo(terminalMessage));
            Assert.That(details["exceptionType"], Is.EqualTo(typeof(InvalidOperationException).FullName));
            Assert.That(details["causeChainTruncated"], Is.EqualTo(true));
            Assert.That(causes.Count, Is.EqualTo(8));
            Assert.That(terminal["depth"], Is.EqualTo(12L));
            Assert.That(terminal["message"], Is.EqualTo(terminalMessage));
            Assert.That(Encoding.UTF8.GetByteCount(envelope), Is.LessThanOrEqualTo(1536 * 1024));
        }

        private static void AssertInvalidName(MethodInfo validate, string name, string contractProfile)
        {
            var exception = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, new object[] { name, contractProfile }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(exception.InnerException.Message, Is.EqualTo("Tool name must use the registered Tool syntax and fit 128 UTF-8 bytes."));
        }

        private static FieldInfo Field(string name)
        {
            return BridgeType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingFieldException(name);
        }

        private static MethodInfo Method(string name)
        {
            return BridgeType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException(name);
        }
    }
}
#endif
