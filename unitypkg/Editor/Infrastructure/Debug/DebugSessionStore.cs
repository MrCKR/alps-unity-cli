#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// 官方 session 文件的一次观察结果。
    ///
    /// 它同时承担两件事：fail-closed 判定所需的字段，以及发布时语义 compare-and-swap 的期望值。
    /// 可解析文件的 CAS 比较 (editorSessionId, domainGeneration, publication)；
    /// 任一侧不可解析时退化为原始内容校验和比较，仍不允许覆盖已变化的文件。
    /// </summary>
    internal sealed class DebugSessionStoreObservation
    {
        /// <summary>官方路径上是否存在文件。</summary>
        internal bool Present;

        /// <summary>文件内容是否为 JSON object；false 表示不可用。</summary>
        internal bool Parsable;

        /// <summary>载荷声明的 Editor Session；不可解析时为空。</summary>
        internal string EditorSessionId = string.Empty;

        /// <summary>载荷声明的 domain generation；不可解析时为空。</summary>
        internal string DomainGeneration = string.Empty;

        /// <summary>载荷声明的 publication；不可解析时为 0。</summary>
        internal long Publication;

        /// <summary>已解析载荷；不可解析时为 null。加载与恢复共用同一次读取。</summary>
        internal IDictionary<string, object> Payload;

        /// <summary>原始内容的稳定校验和；仅用于发布前的 CAS 与诊断。</summary>
        internal string ContentChecksum = string.Empty;

        /// <summary>文件字节数。</summary>
        internal long Bytes;

        /// <summary>CAS：当前观察是否仍然是发布所基于的观察。</summary>
        internal bool Matches(DebugSessionStoreObservation current)
        {
            if (current == null) return false;
            if (Present != current.Present) return false;
            if (!Present) return true;
            if (Parsable && current.Parsable)
            {
                return string.Equals(EditorSessionId, current.EditorSessionId, StringComparison.Ordinal)
                    && string.Equals(DomainGeneration, current.DomainGeneration, StringComparison.Ordinal)
                    && Publication == current.Publication;
            }
            return string.Equals(ContentChecksum, current.ContentChecksum, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// debug 域 session 定义的唯一持久化 owner。
    ///
    /// 布局：整个 debug 域固定一个文件 Library/UnityCli/debug/session.json，绝不一 session 一文件。
    /// 写入：进程内单写者锁内重读官方文件、做语义 CAS、写同目录 .tmp、flush 后原子替换。
    /// 主线程专属的 SessionState 恢复事实与文件分开：off-main 发布只留待写事实，由下一次主线程发布补写。
    /// 读取：无锁，只读官方路径，最多重读一次（有界次数，不随调用次数增长）。
    /// 不可用文件一律 fail closed：受影响定义保持未加载，只写一条有界日志，不阻断其他 Bridge 能力。
    /// </summary>
    internal static class DebugSessionStore
    {
        private const int MaxGapLogBytes = 512;
        private const string GenerationSessionKey = "unitycli.debug.session.generation";
        private const string PublicationSessionKey = "unitycli.debug.session.publication";

        /// <summary>
        /// 被推迟到主线程的恢复事实写在报告里的条目名。
        /// 名称与原因一起构成自足事实：调用方据此知道哪一个写入还没完成，以及为什么。
        /// </summary>
        internal const string RecoveryFactsEntryName = "store-recovery-facts";

        /// <summary>
        /// off-main 发布推迟主线程专属恢复事实的唯一原因文本。
        /// 它是“未完成”的精确理由，不是完成声明：下一次可证明的主线程发布即写入这两个键。
        /// </summary>
        internal const string RecoveryFactsDeferredReason = "deferred: SessionState recovery facts are main-thread-only and are written by the next main-thread publication";

        private static readonly object PublicationGate = new object();

        /// <summary>
        /// 主线程已发布的 SessionState 恢复事实：本次 Editor 启动的 publication 下界与上一代 domain generation。
        ///
        /// SessionState 是 Editor 主线程专属状态，而暂停控制通道会在后台线程上读写 session 状态，
        /// 因此这里与 debug 域其它状态使用同一条规则：只有主线程读写 SessionState 并发布结果，
        /// off-main 服务作用域（<see cref="UnityCliBridge.CatalogOffMainRead"/> 为 true 的那一次服务）
        /// 只读取已发布值、只记下待写事实，绝不直接访问 SessionState。
        /// </summary>
        private static volatile string publishedGeneration = string.Empty;
        private static long publishedFloor;

        /// <summary>
        /// off-main 发布（主线程暂停期间控制通道服务 stop）留下的待写恢复事实。
        /// 它们由下一次可证明的主线程发布补写；重启后自然作废，不跨 Editor 启动保留。
        /// </summary>
        private static string pendingGeneration = string.Empty;
        private static long pendingPublication;
        private static volatile bool recoveryFactsPending;

        /// <summary>debug 域在项目内的临时根，与 hook 快照同级但互不覆盖。</summary>
        internal static string Root()
        {
            return Path.Combine(UnityCliBridge.ProjectRoot(), "Library", "UnityCli", "debug");
        }

        /// <summary>官方 session 文件路径。</summary>
        internal static string FilePath()
        {
            return Path.Combine(Root(), "session.json");
        }

        /// <summary>
        /// 无锁观察官方文件；最多读取两次。
        /// 第一次观察已可解析且 publication 不低于本 Editor Session 的下界时直接采用，否则只重读一次。
        /// </summary>
        internal static DebugSessionStoreObservation Observe()
        {
            var floor = PublicationFloor();
            var observed = ReadOnce();
            // 官方文件不存在等价于空存储：没有任何受影响定义。
            if (observed == null) return new DebugSessionStoreObservation();
            if (observed.Parsable && observed.Publication >= floor) return observed;
            // 有界重读：只多读一次，避免把写入者尚未发布的中间状态当作终态。
            var repeated = ReadOnce();
            return repeated ?? observed;
        }

        /// <summary>
        /// 在单写者锁内重读官方文件并做语义 CAS，然后原子替换。
        /// 失败时不写入任何字节，调用方必须把当前操作视为未提交。
        /// 返回成功写入的 UTF-8 字节数。
        /// </summary>
        internal static long Publish(Dictionary<string, object> payload, DebugSessionStoreObservation expected, string editorSessionId, string domainGeneration, long publication)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            var json = UnityCliSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes > DebugLimits.MaxSessionStoreBytes)
                throw new BridgeCommandException(
                    "E_DEBUG_STORE_FAILED",
                    "The session payload exceeds the declared single-file byte budget.",
                    new Dictionary<string, object> { { "reason", "store-bytes-exceeded" }, { "bytes", (long)bytes }, { "maxBytes", DebugLimits.MaxSessionStoreBytes } },
                    new[] { "Reduce session definitions or their initial state size, then retry." });

            lock (PublicationGate)
            {
                var current = ReadOnce() ?? new DebugSessionStoreObservation();
                if (!expected.Matches(current))
                    throw new BridgeCommandException(
                        "E_DEBUG_STORE_FAILED",
                        "The official session file changed after it was observed, so this publication was refused.",
                        new Dictionary<string, object> { { "reason", "publication-cas-mismatch" }, { "expectedPublication", expected.Publication }, { "observedPublication", current.Publication } },
                        new[] { "Run tools run debug.session.list to read the current store state, then submit a new request." });
                Directory.CreateDirectory(Root());
                // 文件本身是普通磁盘写入，任何线程都可以完成；只有主线程专属的 SessionState 恢复事实需要分流。
                UnityCliBridge.DurableWriteText(FilePath(), json);
                if (UnityCliBridge.CatalogOffMainRead)
                {
                    // 本次发布来自暂停控制通道的后台线程服务。在这里写 SessionState 会直接抛 UnityException，
                    // 使一个已经落盘的发布失去答复；因此只记下待写事实，交给下一次可证明的主线程发布补写。
                    pendingGeneration = domainGeneration ?? string.Empty;
                    pendingPublication = publication;
                    recoveryFactsPending = true;
                    UnityCliBridge.WriteLog("debug_session_recovery_facts_deferred publication=" + publication.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    WriteRecoveryFacts(domainGeneration, publication);
                    ClearPendingRecoveryFacts();
                    // 刚写入的两个键必须立即进入主线程镜像，否则同一域内的 off-main 读取会看到旧事实。
                    PublishRecoveryFacts();
                }
                return bytes;
            }
        }

        /// <summary>
        /// 由可证明的主线程读取两项 SessionState 恢复事实并发布。
        ///
        /// 调用时机：Bridge 启动路径在控制通道发布之前调用一次；Publish 写入后刷新同一份镜像。
        /// 因此 off-main 服务读到的镜像与本 Editor 启动的真实 SessionState 一致，不需要（也不允许）自行读取 SessionState。
        /// </summary>
        internal static void PublishRecoveryFacts()
        {
            // 上一次 off-main 发布留下的待写事实必须先落地；否则镜像会退回旧事实，
            // 而官方文件已经带有更新的 publication，重载后的下界会无谓地变松。
            if (recoveryFactsPending && !UnityCliBridge.CatalogOffMainRead)
            {
                WriteRecoveryFacts(pendingGeneration, pendingPublication);
                ClearPendingRecoveryFacts();
            }
            publishedFloor = ReadFloor();
            publishedGeneration = ReadGeneration();
        }

        /// <summary>本次服务是否已经推迟了主线程专属的恢复事实；调用方据此如实报告未完成的写入。</summary>
        internal static bool RecoveryFactsPending { get { return recoveryFactsPending; } }

        /// <summary>报告里使用的自足事实文本：命名被推迟的写入并给出精确原因。</summary>
        internal static string RecoveryFactsDeferredFact()
        {
            return "pending entry " + RecoveryFactsEntryName + " was not written by this off-main stop: " + RecoveryFactsDeferredReason;
        }

        /// <summary>唯一写入两项 SessionState 恢复事实的成员；调用方必须已证明本次服务不在 off-main 作用域内。</summary>
        private static void WriteRecoveryFacts(string domainGeneration, long publication)
        {
            SessionState.SetString(GenerationSessionKey, domainGeneration ?? string.Empty);
            SessionState.SetString(PublicationSessionKey, publication.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>清除待写事实；调用方必须已经把它补写成更新的值。</summary>
        private static void ClearPendingRecoveryFacts()
        {
            pendingGeneration = string.Empty;
            pendingPublication = 0L;
            recoveryFactsPending = false;
        }

        /// <summary>记录本次 Editor 启动已经观察到的最高 publication；重启后自然归零。</summary>
        internal static long PublicationFloor()
        {
            // off-main 服务不得调用主线程专属 Unity API：只读取主线程已发布值。
            if (UnityCliBridge.CatalogOffMainRead) return publishedFloor;
            publishedFloor = ReadFloor();
            return publishedFloor;
        }

        /// <summary>
        /// 本次 Editor 启动内被接受的前一个 domain generation；
        /// Domain Reload 后允许读取上一个 generation 发布的文件，Editor 重启后该值归零。
        /// </summary>
        internal static string PublishedGeneration()
        {
            if (UnityCliBridge.CatalogOffMainRead) return publishedGeneration;
            publishedGeneration = ReadGeneration();
            return publishedGeneration;
        }

        /// <summary>唯一读取 publication 下界的成员；调用方必须已证明自己在主线程上。</summary>
        private static long ReadFloor()
        {
            var text = SessionState.GetString(PublicationSessionKey, string.Empty);
            long value;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0L ? value : 0L;
        }

        /// <summary>唯一读取上一代 domain generation 的成员；调用方必须已证明自己在主线程上。</summary>
        private static string ReadGeneration()
        {
            return SessionState.GetString(GenerationSessionKey, string.Empty) ?? string.Empty;
        }

        /// <summary>把不可用原因写进有界日志；同一原因只记录一次。</summary>
        internal static void LogUnusable(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;
            UnityCliBridge.WriteLog("debug_session_store_unusable reason=" + UnityCliBridge.BoundedText(reason, MaxGapLogBytes));
        }

        /// <summary>读取一次官方文件；不存在时返回 null。任何读取或解析异常都归约为不可解析观察。</summary>
        private static DebugSessionStoreObservation ReadOnce()
        {
            var path = FilePath();
            try
            {
                if (!File.Exists(path)) return null;
                var length = new FileInfo(path).Length;
                if (length > DebugLimits.MaxSessionStoreBytes)
                    return new DebugSessionStoreObservation { Present = true, Bytes = length };
                var bytes = File.ReadAllBytes(path);
                var observation = new DebugSessionStoreObservation { Present = true, Bytes = bytes.LongLength };
                if (bytes.LongLength > DebugLimits.MaxSessionStoreBytes) return observation;
                var text = Encoding.UTF8.GetString(bytes);
                observation.ContentChecksum = RawChecksum(bytes.LongLength, text);
                if (string.IsNullOrWhiteSpace(text)) return observation;
                var parsed = UnityCliSerializer.Parse(text) as IDictionary<string, object>;
                if (parsed == null) return observation;
                observation.Parsable = true;
                observation.EditorSessionId = Text(parsed, "editorSessionId");
                observation.DomainGeneration = Text(parsed, "domainGeneration");
                observation.Publication = Long(parsed, "publication");
                observation.Payload = parsed;
                return observation;
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("debug_session_store_read_failed " + exception.GetType().Name + ": " + UnityCliBridge.BoundedText(exception.Message, MaxGapLogBytes));
                return new DebugSessionStoreObservation { Present = true };
            }
        }

        /// <summary>原始内容校验和：直接复用统一载荷的 canonical 校验和实现，不引入第二个哈希实现。</summary>
        private static string RawChecksum(long length, string text)
        {
            return UnityCliDebugSessionStorePayload.Checksum(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "rawBytes", length },
                { "rawText", text ?? string.Empty }
            });
        }

        private static string Text(IDictionary<string, object> payload, string key)
        {
            object value;
            if (!payload.TryGetValue(key, out value) || value == null) return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static long Long(IDictionary<string, object> payload, string key)
        {
            object value;
            if (!payload.TryGetValue(key, out value) || value == null) return 0L;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0L; }
        }
    }
}
#endif
