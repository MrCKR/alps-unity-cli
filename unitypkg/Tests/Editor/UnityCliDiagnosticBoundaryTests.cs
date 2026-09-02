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
        public void FirstDoctorCatalogHandlerDoesNotInvokeDiscoveryWhenSnapshotIsMissing()
        {
            var snapshot = Field("projectToolCatalogDiagnostics");
            var operationCounter = Field("projectToolDiscoveryInvocationCount");
            var originalSnapshot = snapshot.GetValue(null);
            var registry = Method("CreateCommandRegistry").Invoke(null, null);
            var requestType = Type.GetType("UnityCli.Protocol.BridgeRequest, UnityCli.Editor", true);
            var request = Activator.CreateInstance(requestType);
            requestType.GetProperty("Command").SetValue(request, "internal.doctor.catalog", null);
            requestType.GetProperty("RequestId").SetValue(request, "doctor-first-handler", null);
            requestType.GetProperty("Input").SetValue(request, new Dictionary<string, object>(), null);

            try
            {
                snapshot.SetValue(null, null);
                var before = Convert.ToInt64(operationCounter.GetValue(null));
                var pending = (ValueTask<object>)registry.GetType().GetMethod("ExecuteAsync").Invoke(registry, new[] { request, (object)CancellationToken.None });
                Exception failure = null;
                try { pending.AsTask().GetAwaiter().GetResult(); }
                catch (Exception exception) { failure = exception; }

                Assert.That(failure, Is.Not.Null);
                Assert.That(failure.GetType().GetProperty("Code").GetValue(failure, null), Is.EqualTo("E_TOOL_REGISTRY_UNHEALTHY"));
                Assert.That(failure.Message, Is.EqualTo("Tool catalog snapshot is unavailable before registry publication."));
                var operationCount = Convert.ToInt64(operationCounter.GetValue(null)) - before;
                Assert.That(operationCount, Is.Zero);

                var envelope = (string)Method("SerializeBridgeExceptionEnvelope").Invoke(null, new object[] { "doctor-first-handler", failure });
                Assert.That(Encoding.UTF8.GetByteCount(envelope), Is.LessThanOrEqualTo(4096));
                Assert.That(envelope, Does.Contain("\"code\":\"E_TOOL_REGISTRY_UNHEALTHY\""));
            }
            finally
            {
                snapshot.SetValue(null, originalSnapshot);
            }
        }

        [Test]
        public void AllProjectToolOwnersShareOneNameGrammarAndUtf8Budget()
        {
            var validate = Method("ValidateProjectToolName");
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { "scene.data" }));
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { "UnityPipeline_validation.official-result" }));
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { new string('a', 128) }));

            AssertInvalidName(validate, "Scene.Data");
            AssertInvalidName(validate, "scene..data");
            AssertInvalidName(validate, "scene.数据");
            AssertInvalidName(validate, new string('a', 129));
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

        private static void AssertInvalidName(MethodInfo validate, string name)
        {
            var exception = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, new object[] { name }));
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
