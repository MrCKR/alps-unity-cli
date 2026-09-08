#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliDurableStatusTests
    {
        private string _scope;
        private string _runningRequestId;
        private string _terminalRequestId;
        private static readonly string Digest = new string('a', 64);

        [SetUp]
        public void SetUp()
        {
            // terminal identity在Editor session内有意持久化，增量重复执行必须使用新identity。
            var runId = Guid.NewGuid().ToString("N");
            _scope = "durable-status-test-" + runId;
            _runningRequestId = "durable-status-running-root-" + runId;
            _terminalRequestId = "durable-status-terminal-root-" + runId;
            UnityCliActiveOperation.Clear(_scope);
        }

        [TearDown]
        public void TearDown()
        {
            UnityCliActiveOperation.Clear(_scope);
        }

        [Test]
        public void ReadScope_ReturnsRunningPhaseWithoutDigest()
        {
            UnityCliActiveOperation.Save(_scope, new Dictionary<string, object>
            {
                { "requestId", _runningRequestId }, { "requestDigest", Digest }, { "phase", "effect_started" }
            });

            var result = (Dictionary<string, object>)UnityCliBridge.UnityCliDurableStatusCoordinator.ReadScope(_scope, _runningRequestId);

            Assert.That(result["requestId"], Is.EqualTo(_runningRequestId));
            Assert.That(result["state"], Is.EqualTo("running"));
            Assert.That(result["phase"], Is.EqualTo("effect_started"));
            Assert.That(result.ContainsKey("requestDigest"), Is.False);
            Assert.That(result.ContainsKey("terminalEnvelope"), Is.False);
        }

        [Test]
        public void ReadScope_ReturnsFirstTerminalWithoutReplayingEffect()
        {
            var terminal = new Dictionary<string, object>
            {
                { "ok", true }, { "requestId", _terminalRequestId }, { "data", new Dictionary<string, object> { { "value", 1L } } }
            };
            var first = UnityCliActiveOperation.StoreTerminalFirst(_scope, _terminalRequestId, Digest, null, "test.run", terminal);

            var result = (Dictionary<string, object>)UnityCliBridge.UnityCliDurableStatusCoordinator.ReadScope(_scope, _terminalRequestId);
            Assert.That(UnityCliActiveOperation.TryGetTerminalByRequestId(_scope, _terminalRequestId, out var internalDigest, out var statusDomain, out var selected), Is.True);

            Assert.That(result["state"], Is.EqualTo("completed"));
            Assert.That(result["terminalEnvelope"], Is.EqualTo(first));
            Assert.That(internalDigest, Is.EqualTo(Digest));
            Assert.That(statusDomain, Is.EqualTo("test.run"));
            Assert.That(selected, Is.EqualTo(first));
            Assert.That(result.ContainsKey("requestDigest"), Is.False);
        }

        [Test]
        public void SafeStatusAllowlist_ContainsEveryDomainCommand()
        {
            var commands = new[]
            {
                "internal.asset.refresh.public-status", "internal.build.player.public-status",
                "internal.editor.play-mode.public-status", "internal.package.install.public-status",
                "internal.package.remove.public-status", "internal.test.run.public-status"
            };
            foreach (var command in commands)
                Assert.That(UnityCliBridgeQueuePolicy.IsSafeStatusCommand(command), Is.True, command);
            Assert.That(UnityCliBridgeQueuePolicy.IsSafeStatusCommand("internal.operation.public-status"), Is.False);
        }
    }
}
#endif
