#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliDurableStatusTests
    {
        private const string Scope = "durable-status-test-v2";
        private const string RunningRequestId = "durable-status-running-root-01";
        private const string TerminalRequestId = "durable-status-terminal-root-01";
        private static readonly string Digest = new string('a', 64);

        [SetUp]
        public void SetUp()
        {
            UnityCliActiveOperation.Clear(Scope);
        }

        [TearDown]
        public void TearDown()
        {
            UnityCliActiveOperation.Clear(Scope);
        }

        [Test]
        public void ReadScope_ReturnsRunningPhaseWithoutDigest()
        {
            UnityCliActiveOperation.Save(Scope, new Dictionary<string, object>
            {
                { "requestId", RunningRequestId }, { "requestDigest", Digest }, { "phase", "effect_started" }
            });

            var result = (Dictionary<string, object>)UnityCliBridge.UnityCliDurableStatusCoordinator.ReadScope(Scope, RunningRequestId);

            Assert.That(result["requestId"], Is.EqualTo(RunningRequestId));
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
                { "ok", true }, { "requestId", TerminalRequestId }, { "data", new Dictionary<string, object> { { "value", 1L } } }
            };
            var first = UnityCliActiveOperation.StoreTerminalFirst(Scope, TerminalRequestId, Digest, null, "test.run", terminal);

            var result = (Dictionary<string, object>)UnityCliBridge.UnityCliDurableStatusCoordinator.ReadScope(Scope, TerminalRequestId);
            Assert.That(UnityCliActiveOperation.TryGetTerminalByRequestId(Scope, TerminalRequestId, out var internalDigest, out var statusDomain, out var selected), Is.True);

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
                "internal.package.remove.public-status", "internal.package.update.public-status",
                "internal.test.run.public-status"
            };
            foreach (var command in commands)
                Assert.That(UnityCliBridgeQueuePolicy.IsSafeStatusCommand(command), Is.True, command);
            Assert.That(UnityCliBridgeQueuePolicy.IsSafeStatusCommand("internal.operation.public-status"), Is.False);
        }
    }
}
#endif
