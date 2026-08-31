#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliInstancePublicationRetryTests
    {
        private Type bridgeType;
        private MethodInfo register;
        private MethodInfo tick;
        private MethodInfo cancel;
        private MethodInfo instanceId;
        private FieldInfo started;
        private FieldInfo exhausted;
        private FieldInfo attemptCount;
        private string originalDataRoot;
        private bool originalStarted;
        private string testRoot;

        [SetUp]
        public void SetUp()
        {
            bridgeType = Type.GetType("UnityCli.UnityCliBridge, UnityCli.Editor", true);
            register = Method("RegisterInstance");
            tick = Method("TickInstancePublicationRetry");
            cancel = Method("CancelInstancePublicationRetry");
            instanceId = Method("InstanceId");
            started = Field("started");
            exhausted = Field("registryPublicationRetryExhausted");
            attemptCount = Field("registryPublicationAttemptCount");
            originalDataRoot = Environment.GetEnvironmentVariable("UNITYCLI_DATA_DIR");
            originalStarted = (bool)started.GetValue(null);
            testRoot = Path.Combine(Directory.GetCurrentDirectory(), "Library", "UnityCli", "instance-publication-retry-tests");
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
            Directory.CreateDirectory(Path.Combine(testRoot, "instances"));
            Environment.SetEnvironmentVariable("UNITYCLI_DATA_DIR", testRoot);
            started.SetValue(null, true);
            cancel.Invoke(null, null);
        }

        [TearDown]
        public void TearDown()
        {
            cancel.Invoke(null, null);
            started.SetValue(null, originalStarted);
            Environment.SetEnvironmentVariable("UNITYCLI_DATA_DIR", originalDataRoot);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }

        [Test]
        public void TransientWriteFailureUsesLatestStateAndSucceedsOnOneRetry()
        {
            var paths = Paths();
            using (new FileStream(paths.Temporary, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That((bool)register.Invoke(null, new object[] { "transitioning" }), Is.False);
                Assert.That((bool)register.Invoke(null, new object[] { "ready" }), Is.False, "a second request must update the pending state without another write attempt");
                Assert.That((int)attemptCount.GetValue(null), Is.EqualTo(1));
            }

            Thread.Sleep(300);
            tick.Invoke(null, null);
            Assert.That(File.Exists(paths.Registry), Is.True);
            Assert.That(File.Exists(paths.Temporary), Is.False);
            Assert.That((bool)exhausted.GetValue(null), Is.False);
            Assert.That((int)attemptCount.GetValue(null), Is.EqualTo(0));
            var record = ParseRecord(paths.Registry);
            var sessions = (List<object>)record["sessions"];
            var session = (Dictionary<string, object>)sessions[0];
            Assert.That(session["lifecycle"], Is.EqualTo("ready"));
        }

        [Test]
        public void PersistentWriteFailureStopsAtFourAndDoesNotStartAnotherChain()
        {
            var paths = Paths();
            using (new FileStream(paths.Temporary, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That((bool)register.Invoke(null, new object[] { "ready" }), Is.False);
                Thread.Sleep(300); tick.Invoke(null, null);
                Thread.Sleep(550); tick.Invoke(null, null);
                Thread.Sleep(1050); tick.Invoke(null, null);
                Assert.That((int)attemptCount.GetValue(null), Is.EqualTo(4));
                Assert.That((bool)exhausted.GetValue(null), Is.True);
            }

            File.Delete(paths.Temporary);
            Thread.Sleep(1100);
            tick.Invoke(null, null);
            Assert.That((bool)register.Invoke(null, new object[] { "ready" }), Is.False);
            Assert.That((int)attemptCount.GetValue(null), Is.EqualTo(4));
            Assert.That(File.Exists(paths.Registry), Is.False);
            Assert.That(File.Exists(paths.Temporary), Is.False);
        }

        [Test]
        public void CancelClearsExhaustionForTheNextDomainOwner()
        {
            var paths = Paths();
            using (new FileStream(paths.Temporary, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That((bool)register.Invoke(null, new object[] { "ready" }), Is.False);
            }
            cancel.Invoke(null, null);
            Assert.That((bool)exhausted.GetValue(null), Is.False);
            Assert.That((int)attemptCount.GetValue(null), Is.EqualTo(0));
            Assert.That((bool)register.Invoke(null, new object[] { "ready" }), Is.True);
            Assert.That(File.Exists(paths.Registry), Is.True);
            Assert.That(File.Exists(paths.Temporary), Is.False);
        }

        private (string Registry, string Temporary) Paths()
        {
            var id = (string)instanceId.Invoke(null, null);
            var registry = Path.Combine(testRoot, "instances", id + ".json");
            return (registry, registry + ".tmp");
        }

        private Dictionary<string, object> ParseRecord(string path)
        {
            var serializer = Type.GetType("UnityCli.UnityCliSerializer, UnityCli.Editor", true);
            var parse = serializer.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static);
            return (Dictionary<string, object>)parse.Invoke(null, new object[] { File.ReadAllText(path, Encoding.UTF8) });
        }

        private MethodInfo Method(string name)
        {
            return bridgeType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(name);
        }

        private FieldInfo Field(string name)
        {
            return bridgeType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingFieldException(name);
        }
    }
}
#endif
