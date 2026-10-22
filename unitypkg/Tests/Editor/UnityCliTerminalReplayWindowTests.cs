#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace UnityCli.Tests
{
    /// <summary>
    /// 终态重投历史只按原调用的 execution deadline 存活，不能是 session 级预算。
    /// 测试直接写正式缓存路径，因此先备份并在结束时还原现场。
    /// </summary>
    public sealed class UnityCliTerminalReplayWindowTests
    {
        private const string Scope = "terminal-window-test";
        private static readonly string Digest = new string('a', 64);
        private string terminalCachePath;
        private string requestId;
        private string backup;
        private string lastObserved;
        private bool hadBackup;

        [SetUp]
        public void SetUp()
        {
            terminalCachePath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "UnityCli", "terminal-cache.json");
            hadBackup = File.Exists(terminalCachePath);
            backup = hadBackup ? File.ReadAllText(terminalCachePath, Encoding.UTF8) : null;
            lastObserved = backup;
            requestId = "terminal-window-test-" + Guid.NewGuid().ToString("N");
            UnityCliActiveOperation.Clear(Scope);
        }

        [TearDown]
        public void TearDown()
        {
            UnityCliActiveOperation.Clear(Scope);
            // 只回滚本次测试自己的写入；测试期间 Editor 若已写入新终态，保留其状态而不是用旧快照覆盖。
            var current = File.Exists(terminalCachePath) ? File.ReadAllText(terminalCachePath, Encoding.UTF8) : null;
            if (!string.Equals(current, lastObserved, StringComparison.Ordinal)) return;
            if (hadBackup) File.WriteAllText(terminalCachePath, backup, new UTF8Encoding(false));
            else if (File.Exists(terminalCachePath)) File.Delete(terminalCachePath);
        }

        /// <summary>窗口已过的identity不再占用容量，也不阻止新的mutation准入。</summary>
        [Test]
        public void ExpiredReplayWindowsDoNotBlockAdmission()
        {
            WriteCache(RetiredIdentities(4088, UtcNowMs - 60_000));

            Assert.DoesNotThrow(() => UnityCliActiveOperation.EnsureTerminalAdmission());
        }

        /// <summary>仍有未到期窗口时在准入前用无effect的busy拒绝，而不是超时或永久预算。</summary>
        [Test]
        public void UnexpiredReplayWindowsRefuseBeforeEffectWithBusy()
        {
            WriteCache(RetiredIdentities(4088, UtcNowMs + 600_000));

            var exception = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => UnityCliActiveOperation.EnsureTerminalAdmission());
            Assert.AreEqual("E_UNITY_BUSY", exception.Code);
            Assert.IsTrue(exception.Transient);
            Assert.Greater(exception.RetryAfterMs, 0);
            Assert.IsNotEmpty(exception.NextActions);
            var details = (Dictionary<string, object>)exception.Details;
            Assert.AreEqual("terminal-replay-history-capacity", details["reason"]);
            Assert.AreEqual(false, details["ownEffectStarted"]);
        }

        /// <summary>新terminal总是写入重投窗口，已过窗口的旧identity在写入时被裁剪。</summary>
        [Test]
        public void StoredTerminalCarriesItsReplayWindow()
        {
            WriteCache(RetiredIdentities(4, UtcNowMs - 60_000));
            var envelope = new Dictionary<string, object> { { "requestId", requestId }, { "ok", true } };

            UnityCliActiveOperation.StoreTerminalFirst(Scope, requestId, Digest, envelope);

            var storedText = File.ReadAllText(terminalCachePath, Encoding.UTF8);
            lastObserved = storedText;
            var root = UnityCliSerializer.Parse(storedText) as Dictionary<string, object>;
            Assert.AreEqual(3L, root["version"]);
            var entries = (List<object>)root["entries"];
            var entry = (Dictionary<string, object>)entries[entries.Count - 1];
            Assert.Greater((long)entry["retainUntilUtcMs"], UtcNowMs);
            Assert.AreEqual(0, ((List<object>)root["retiredIdentities"]).Count);
            object stored;
            Assert.IsTrue(UnityCliActiveOperation.TryGetTerminal(Scope, requestId, Digest, out stored));
        }

        /// <summary>已被挤出窗口的identity仍按unknown拒绝，绝不复执行effect。</summary>
        [Test]
        public void RetiredTerminalRefusesReplayWithUnknownOutcome()
        {
            WriteCache(new List<object>
            {
                new Dictionary<string, object>
                {
                    { "identity", IdentityHash(Scope, requestId) },
                    { "retainUntilUtcMs", UtcNowMs + 600_000 }
                }
            });

            var exception = Assert.Throws<UnityCliBridge.BridgeCommandException>(
                () => UnityCliActiveOperation.TryGetTerminal(Scope, requestId, Digest, out _));
            Assert.AreEqual("E_EXECUTION_TIMEOUT", exception.Code);
            var details = (Dictionary<string, object>)exception.Details;
            Assert.AreEqual("blocked", details["replay"]);
            Assert.AreEqual("terminal-evicted", details["reason"]);
            Assert.IsNotEmpty(exception.NextActions);
        }

        private static long UtcNowMs { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }

        private static List<object> RetiredIdentities(int count, long retainUntilUtcMs)
        {
            var values = new List<object>(count);
            for (var index = 0; index < count; index++)
                values.Add(new Dictionary<string, object>
                {
                    { "identity", new string('b', 48) + index.ToString("x16", CultureInfo.InvariantCulture) },
                    { "retainUntilUtcMs", retainUntilUtcMs }
                });
            return values;
        }

        private static string IdentityHash(string scope, string scopeRequestId)
        {
            using (var sha = SHA256.Create())
                return string.Concat(Array.ConvertAll(sha.ComputeHash(Encoding.UTF8.GetBytes(scope + "\0" + scopeRequestId)), value => value.ToString("x2")));
        }

        private void WriteCache(List<object> retired)
        {
            var root = new Dictionary<string, object>
            {
                { "version", 3L },
                { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "retiredIdentities", retired },
                { "entries", new List<object>() }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(terminalCachePath));
            var text = UnityCliSerializer.Serialize(root) + "\n";
            File.WriteAllText(terminalCachePath, text, new UTF8Encoding(false));
            lastObserved = text;
        }
    }
}
#endif
