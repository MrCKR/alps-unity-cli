#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliInstanceCleanupTests
    {
        private Type bridgeType;
        private MethodInfo cleanup;
        private MethodInfo boundedLogLine;
        private MethodInfo rollLog;
        private string originalDataRoot;
        private string root;
        private string instances;

        [SetUp]
        public void SetUp()
        {
            bridgeType = Type.GetType("UnityCli.UnityCliBridge, UnityCli.Editor", true);
            cleanup = bridgeType.GetMethod("RunInstanceCleanup", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException("RunInstanceCleanup");
            boundedLogLine = bridgeType.GetMethod("BoundedBridgeLogLine", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException("BoundedBridgeLogLine");
            rollLog = bridgeType.GetMethod("RollBridgeLogIfNeeded", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException("RollBridgeLogIfNeeded");
            originalDataRoot = Environment.GetEnvironmentVariable("UNITYCLI_DATA_DIR");
            root = Path.Combine(Directory.GetCurrentDirectory(), "Library", "UnityCli", "instance-cleanup-tests");
            instances = Path.Combine(root, "instances");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(instances);
            Environment.SetEnvironmentVariable("UNITYCLI_DATA_DIR", root);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("UNITYCLI_DATA_DIR", originalDataRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        [Test]
        public void TwoBoundedPassesReachOfflineAndDamagedRetentionCaps()
        {
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 115; index++)
                WriteOffline("offline", index, index < 5 ? now.AddDays(-40 - index) : now.AddHours(-index - 1));
            for (var index = 0; index < 30; index++)
                WriteDamaged(index, index < 5 ? now.AddDays(-45 - index) : now.AddHours(-index - 1));
            WriteClaim("active", 0, Process.GetCurrentProcess().Id, Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), "editor");
            WriteClaim("unverifiable", 0, 0, string.Empty, "editor");
            WriteMixedClaims("mixed", 0);

            var first = cleanup.Invoke(null, null);
            Assert.That(Value(first, "Scanned"), Is.EqualTo(148));
            Assert.That(Value(first, "Offline"), Is.EqualTo(115));
            Assert.That(Value(first, "Damaged"), Is.EqualTo(30));
            Assert.That(Value(first, "Preserved"), Is.EqualTo(3));
            Assert.That(Value(first, "Deleted"), Is.EqualTo(20));
            Assert.That(Value(first, "Deferred"), Is.EqualTo(5));
            Assert.That((bool)Field(first, "Truncated"), Is.False);
            Assert.That(File.Exists(PathFor("damaged", 4)), Is.False, "oldest damaged record must be deleted first");
            Assert.That(File.Exists(PathFor("offline", 4)), Is.False, "oldest offline record must be deleted first");
            Assert.That(File.Exists(PathFor("offline", 114)), Is.False, "oldest over-cap recent offline record must be deleted");
            Assert.That(File.Exists(PathFor("offline", 5)), Is.True, "newer retained offline record must remain");
            Assert.That(File.Exists(PathFor("damaged", 29)), Is.True, "delete budget must defer remaining planned records");

            var second = cleanup.Invoke(null, null);
            Assert.That(Value(second, "Deleted"), Is.EqualTo(5));
            Assert.That(Value(second, "Deferred"), Is.EqualTo(0));
            Assert.That(CountOffline(), Is.EqualTo(100));
            Assert.That(CountDamaged(), Is.EqualTo(20));
            Assert.That(File.Exists(PathFor("active", 0)), Is.True);
            Assert.That(File.Exists(PathFor("unverifiable", 0)), Is.True);
            Assert.That(File.Exists(PathFor("mixed", 0)), Is.True);
            Assert.That(Directory.GetFiles(instances, "*.tmp").Length, Is.EqualTo(0));
        }

        [Test]
        public void ScanLimitSetsTruncatedAndStillCapsDeletion()
        {
            var now = DateTimeOffset.UtcNow.AddDays(-40);
            for (var index = 0; index < 2001; index++) WriteOffline("scan", index, now.AddMinutes(-index));
            var result = cleanup.Invoke(null, null);
            Assert.That(Value(result, "Scanned"), Is.EqualTo(2000));
            Assert.That((bool)Field(result, "Truncated"), Is.True);
            Assert.That(Value(result, "Deleted"), Is.EqualTo(20));
            Assert.That(Directory.GetFiles(instances, "*.json").Length, Is.EqualTo(1981));
            Assert.That(Directory.GetFiles(instances, "*.tmp").Length, Is.EqualTo(0));
        }

        [Test]
        public void RepeatedLogEntriesKeepTwoBoundedSegments()
        {
            var logPath = Path.Combine(root, "bounded.log");
            var oversized = new string('x', 96 * 1024);
            for (var index = 0; index < 400; index++)
            {
                var line = (string)boundedLogLine.Invoke(null, new object[] { oversized, 64 * 1024 });
                var entry = line + Environment.NewLine;
                rollLog.Invoke(null, new object[] { logPath, Encoding.UTF8.GetByteCount(entry) });
                File.AppendAllText(logPath, entry, new UTF8Encoding(false));
            }
            var segments = Directory.GetFiles(root, "bounded.log*");
            Assert.That(segments.Length, Is.EqualTo(2));
            foreach (var segment in segments) Assert.That(new FileInfo(segment).Length, Is.LessThanOrEqualTo(10L * 1024 * 1024));
            Assert.That(Encoding.UTF8.GetByteCount((string)boundedLogLine.Invoke(null, new object[] { oversized, 64 * 1024 })), Is.LessThanOrEqualTo(64 * 1024));
        }

        [Test]
        public void PerInstanceLockDefersThenAllowsTheSameOldRecord()
        {
            var candidate = WriteOffline("locked", 0, DateTimeOffset.UtcNow.AddDays(-40));
            var instanceId = Path.GetFileNameWithoutExtension(candidate);
            using (var held = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                var thread = new Thread(delegate()
                {
                    using (var mutex = new Mutex(false, "Local\\UnityCli.Registry." + instanceId))
                    {
                        mutex.WaitOne();
                        held.Set();
                        release.Wait();
                        mutex.ReleaseMutex();
                    }
                });
                thread.IsBackground = true;
                thread.Start();
                Assert.That(held.Wait(5000), Is.True);
                var deferred = cleanup.Invoke(null, null);
                Assert.That(Value(deferred, "Deleted"), Is.EqualTo(0));
                Assert.That(Value(deferred, "Deferred"), Is.EqualTo(1));
                Assert.That(File.Exists(candidate), Is.True);
                release.Set();
                Assert.That(thread.Join(5000), Is.True);
            }

            var deleted = cleanup.Invoke(null, null);
            Assert.That(Value(deleted, "Deleted"), Is.EqualTo(1));
            Assert.That(File.Exists(candidate), Is.False);
            Assert.That(Directory.GetFiles(instances, "*.tmp").Length, Is.EqualTo(0));
        }

        private string WriteOffline(string kind, int index, DateTimeOffset publishedAt)
        {
            var identity = Identity(kind, index);
            var path = Path.Combine(instances, identity.Item1 + ".json");
            var json = "{\"version\":3,\"revision\":1,\"canonicalizationVersion\":1,\"identity\":{\"instanceId\":\"" + identity.Item1 + "\",\"canonicalProjectPath\":\"" + Json(identity.Item2) + "\",\"displayProjectPath\":\"" + Json(identity.Item3) + "\"},\"sessions\":[],\"publishedAt\":\"" + publishedAt.ToString("O", CultureInfo.InvariantCulture) + "\"}\n";
            File.WriteAllText(path, json, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, publishedAt.UtcDateTime);
            return path;
        }

        private void WriteDamaged(int index, DateTimeOffset writtenAt)
        {
            var path = PathFor("damaged", index);
            File.WriteAllText(path, "{\"version\":3,", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime);
        }

        private void WriteClaim(string kind, int index, int processId, string processStartedAt, string target)
        {
            var identity = Identity(kind, index);
            var path = Path.Combine(instances, identity.Item1 + ".json");
            var sessionId = "session-" + new string(kind == "active" ? 'a' : 'b', 64);
            var json = "{\"version\":3,\"revision\":1,\"canonicalizationVersion\":1,\"identity\":{\"instanceId\":\"" + identity.Item1 + "\",\"canonicalProjectPath\":\"" + Json(identity.Item2) + "\",\"displayProjectPath\":\"" + Json(identity.Item3) + "\"},\"sessions\":[{\"target\":\"" + target + "\",\"sessionId\":\"" + sessionId + "\",\"processId\":" + processId.ToString(CultureInfo.InvariantCulture) + ",\"processStartedAt\":\"" + processStartedAt + "\"}],\"publishedAt\":\"" + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\"}\n";
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        private void WriteMixedClaims(string kind, int index)
        {
            var identity = Identity(kind, index);
            var process = Process.GetCurrentProcess();
            var started = process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            var path = Path.Combine(instances, identity.Item1 + ".json");
            var json = "{\"version\":3,\"revision\":1,\"canonicalizationVersion\":1,\"identity\":{\"instanceId\":\"" + identity.Item1 + "\",\"canonicalProjectPath\":\"" + Json(identity.Item2) + "\",\"displayProjectPath\":\"" + Json(identity.Item3) + "\"},\"sessions\":[{\"target\":\"editor\",\"processId\":" + process.Id.ToString(CultureInfo.InvariantCulture) + ",\"processStartedAt\":\"" + started + "\"},{\"target\":\"runtime\",\"processId\":" + process.Id.ToString(CultureInfo.InvariantCulture) + ",\"processStartedAt\":\"" + started + "\"}],\"publishedAt\":\"" + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\"}\n";
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        private int CountOffline()
        {
            var count = 0;
            foreach (var path in Directory.GetFiles(instances, "*.json"))
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (text.Contains("\"sessions\":[]")) count++;
            }
            return count;
        }

        private int CountDamaged()
        {
            var count = 0;
            foreach (var path in Directory.GetFiles(instances, "*.json"))
                if (File.ReadAllText(path, Encoding.UTF8) == "{\"version\":3,") count++;
            return count;
        }

        private string PathFor(string kind, int index)
        {
            return Path.Combine(instances, Identity(kind, index).Item1 + ".json");
        }

        private Tuple<string, string, string> Identity(string kind, int index)
        {
            var canonical = "d:\\unitycli-cleanup-tests\\" + kind + "-" + index.ToString("D3", CultureInfo.InvariantCulture);
            return Tuple.Create("unity-" + Sha256(canonical), canonical, "D:\\UnityCli-Cleanup-Tests\\" + kind + "-" + index.ToString("D3", CultureInfo.InvariantCulture));
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var result = new StringBuilder(64);
                foreach (var item in bytes) result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static string Json(string value) { return value.Replace("\\", "\\\\").Replace("\"", "\\\""); }
        private static int Value(object owner, string name) { return Convert.ToInt32(Field(owner, name), CultureInfo.InvariantCulture); }
        private static object Field(object owner, string name) { return owner.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner); }
    }
}
#endif
