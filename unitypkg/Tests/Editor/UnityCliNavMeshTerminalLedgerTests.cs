#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;

namespace UnityCli.Tests
{
    /// <summary>
    /// NavMesh 终态账本与共享终态缓存使用同一个不变量：条目只按原调用的重投窗口存活，
    /// 因此账本不是每 session 的烘焙次数预算。测试直接写正式机制文件，先备份并在结束时还原现场。
    /// </summary>
    public sealed class UnityCliNavMeshTerminalLedgerTests
    {
        private const string LedgerScope = "navmesh-bake-terminal-ledger";
        private const string WrittenThisSessionKey = "UnityCli.NavMeshBake.TerminalLedgerWrittenThisSession";
        private const string ProofKeySessionKey = "UnityCli.NavMeshBake.StatusProofKey";
        private static readonly string Digest = new string('a', 64);
        private string ledgerPath;
        private string backup;
        private bool hadBackup;
        private bool hadWrittenFlag;

        [SetUp]
        public void SetUp()
        {
            UnityCliBridge.UnityCliNavMeshBakeCoordinator.Initialize();
            Assert.IsNotEmpty(SessionState.GetString(ProofKeySessionKey, string.Empty),
                "The NavMesh session proof key must exist before the ledger can be written.");
            ledgerPath = Path.Combine(Path.GetDirectoryName(UnityEngine.Application.dataPath), "Library", "UnityCli", "active-" + LedgerScope + ".json");
            hadBackup = File.Exists(ledgerPath);
            backup = hadBackup ? File.ReadAllText(ledgerPath, Encoding.UTF8) : null;
            hadWrittenFlag = SessionState.GetBool(WrittenThisSessionKey, false);
        }

        [TearDown]
        public void TearDown()
        {
            if (hadBackup) File.WriteAllText(ledgerPath, backup, new UTF8Encoding(false));
            else if (File.Exists(ledgerPath)) File.Delete(ledgerPath);
            SessionState.SetBool(WrittenThisSessionKey, hadWrittenFlag);
        }

        /// <summary>窗口已过的条目不再占容量：200 条过期记录也不会阻止新的 bake 准入。</summary>
        [Test]
        public void ExpiredLedgerEntriesDoNotBlockAdmission()
        {
            var expired = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
            for (var index = 0; index < 200; index++) Store(index, expired);

            Assert.DoesNotThrow(() => UnityCliBridge.UnityCliNavMeshBakeCoordinator.EnsureTerminalLedgerCapacity());
            var ledger = UnityCliActiveOperation.Load(LedgerScope);
            Assert.AreEqual(1, ((IList<object>)((Dictionary<string, object>)ledger)["entries"]).Count);
        }

        /// <summary>未到期窗口占满容量时在effect之前用busy拒绝，并给出自动恢复时间。</summary>
        [Test]
        public void UnexpiredLedgerCapacityRefusesBeforeEffectWithBusy()
        {
            var future = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000;
            for (var index = 0; index < 128; index++) Store(index, future);

            var exception = Assert.Throws<UnityCliBridge.BridgeCommandException>(
                () => UnityCliBridge.UnityCliNavMeshBakeCoordinator.EnsureTerminalLedgerCapacity());
            Assert.AreEqual("E_UNITY_BUSY", exception.Code);
            Assert.IsTrue(exception.Transient);
            Assert.Greater(exception.RetryAfterMs, 0);
            Assert.IsNotEmpty(exception.NextActions);
            var details = (Dictionary<string, object>)exception.Details;
            Assert.AreEqual("navmesh-terminal-ledger-capacity", details["reason"]);
            Assert.AreEqual(false, details["ownEffectStarted"]);
        }

        /// <summary>每个条目都带自己的重投窗口，供读取时裁剪使用。</summary>
        [Test]
        public void LedgerEntriesCarryTheirReplayWindow()
        {
            var future = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000;
            Store(0, future);
            Store(1, future);

            var ledger = (Dictionary<string, object>)UnityCliActiveOperation.Load(LedgerScope);
            var entries = (IList<object>)ledger["entries"];
            Assert.AreEqual(2, entries.Count);
            foreach (var value in entries)
                Assert.Greater(Convert.ToInt64(((Dictionary<string, object>)value)["retainUntilUtcMs"]), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        private static void Store(int index, long retainUntilUtcMs)
        {
            var requestId = "navmesh-ledger-test-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var envelope = new Dictionary<string, object> { { "requestId", requestId }, { "ok", true } };
            UnityCliBridge.UnityCliNavMeshBakeCoordinator.StoreTerminalLedgerIdentity(requestId, Digest, envelope, retainUntilUtcMs);
        }
    }
}
#endif
