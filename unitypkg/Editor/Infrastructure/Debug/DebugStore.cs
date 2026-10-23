#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// 一个 debug 存储文件的一次观察结果；同时承担 fail-closed 判定与发布前语义 CAS 的期望值。
    /// </summary>
    internal sealed class DebugStoreObservation
    {
        internal bool Present;
        internal bool Parsable;
        internal string Format = string.Empty;
        internal string EditorSessionId = string.Empty;
        internal string DomainGeneration = string.Empty;
        internal long Publication;
        internal IDictionary<string, object> Payload;
        internal string ContentChecksum = string.Empty;
        internal long Bytes;

        /// <summary>CAS：当前文件是否仍是这次观察所基于的内容。</summary>
        internal bool Matches(DebugStoreObservation current)
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
    /// debug 域定义的唯一 store owner。
    ///
    /// 固定路径（项目内临时根之下，一类一个固定文件，绝不一请求一文件）：
    /// - `Library/UnityCli/debug/definitions.json`：地址定义（Hook 定义与 program 定义真值）快照。
    /// - `Library/UnityCli/debug/events.json`：统一调试事件 checkpoint。
    ///
    /// 协议：
    /// - 顶层携带 format、editorSessionId、domainGeneration、publication 与 canonical payload 的 SHA-256。
    /// - 单写者锁内重读正式文件做语义 CAS，写入固定同目录 `.tmp` 后 write-through 原子替换。
    /// - 读取只读正式路径并有界重读；任一 fail-closed 规则命中即视为该文件不可用，受影响定义保持未加载，
    ///   不阻断其他 Bridge 能力，不阻断其他 owner。
    /// - 跨 Editor 启动的复用由 editorSessionId 门禁阻止；domainGeneration 字段用于诊断与重绑定，
    ///   Domain Reload 后同一次启动内仍然接受上一代写入的文件（重载后必须精确重绑，定义不因重载丢失）。
    /// - 首次发布时一次性删除旧 Hook store 的两个固定文件：旧真值与旧恢复路径不得与新 store 并存。
    /// - 机制状态（事件 sequence 下界、恢复抑制集合）保存在本次 Editor 启动的 SessionState 键中并携带写它的
    ///   editorSessionId；Editor 重启后旧值一律作废，不以进程存活猜测代替。
    /// </summary>
    internal static class DebugStore
    {
        /// <summary>定义文件冻结格式号；字段集变化必须同步升号，旧格式一律不可用。</summary>
        internal const string DefinitionsFormat = "unitycli.debug.definitions/1";

        /// <summary>事件 checkpoint 冻结格式号；条目字段集变化必须同步升号，旧格式一律不可用。</summary>
        internal const string EventsFormat = "unitycli.debug.events/1";

        private const string MechanismOwnerSessionKey = "unitycli.debug.mechanism.editor-session";
        private const string EventFloorKey = "unitycli.debug.mechanism.event-floor";
        private const string SuppressionKey = "unitycli.debug.mechanism.recovery-suppression";
        private const int MaxMechanismBytes = 32 * 1024;
        private const int MaxGapLogBytes = 512;

        private static readonly object PublicationGate = new object();
        private static readonly Dictionary<string, DebugStoreObservation> Observed = new Dictionary<string, DebugStoreObservation>(StringComparer.Ordinal);
        private static bool legacyRemoved;

        /// <summary>debug 域在项目内的临时根。</summary>
        internal static string Root()
        {
            return Path.Combine(UnityCliBridge.ProjectRoot(), "Library", "UnityCli", "debug");
        }

        internal static string DefinitionsPath() { return Path.Combine(Root(), "definitions.json"); }

        internal static string EventsPath() { return Path.Combine(Root(), "events.json"); }

        /// <summary>旧 Hook store 的固定目录；只用于一次性删除，绝不作为读取来源。</summary>
        private static string LegacyRoot()
        {
            return Path.Combine(UnityCliBridge.ProjectRoot(), "Library", "UnityCli", "method-hooks");
        }

        // ------------------------------------------------------------------
        // 观察与发布
        // ------------------------------------------------------------------

        /// <summary>观察定义文件；最多重读一次。</summary>
        internal static DebugStoreObservation ObserveDefinitions()
        {
            return Observe(DefinitionsPath(), DefinitionsFormat);
        }

        /// <summary>观察事件 checkpoint 文件；最多重读一次。</summary>
        internal static DebugStoreObservation ObserveEvents()
        {
            return Observe(EventsPath(), EventsFormat);
        }

        /// <summary>
        /// 发布一个 debug 存储文件并返回新 publication。
        /// 载荷在锁内组装（format、editorSessionId、domainGeneration、publication、checksum），
        /// CAS 不一致或超预算时抛稳定错误且不写入任何字节。
        /// </summary>
        internal static DebugStoreObservation Publish(string path, string format, IDictionary<string, object> content, DebugStoreObservation expected)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            if (string.IsNullOrEmpty(format)) throw new ArgumentNullException("format");
            lock (PublicationGate)
            {
                var sessionId = UnityCliBridge.EditorSessionId();
                var generation = UnityCliBridge.ReadGenerationIdentity();
                // 不存在的文件等价于空存储：缺文件时的首次发布必须能完成，
                // 只有文件真的出现、消失或内容变化才拒绝（与 DebugSessionStore.Publish 同一规则）。
                var current = ReadOnce(path, format) ?? new DebugStoreObservation();
                if (expected != null && !expected.Matches(current))
                    throw new BridgeCommandException(
                        "E_DEBUG_STORE_FAILED",
                        "The debug store file changed after it was observed, so this publication was refused.",
                        new Dictionary<string, object>
                        {
                            { "reason", "publication-cas-mismatch" },
                            { "path", Relative(path) },
                            { "expectedPublication", expected.Publication },
                            { "observedPublication", current.Publication }
                        },
                        new[] { "Run tools run debug.session-info to read the current state, then submit a new request." });
                var publication = Math.Max(expected == null ? 0L : expected.Publication, current.Publication) + 1L;
                var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "format", format },
                    { "editorSessionId", sessionId },
                    { "domainGeneration", generation },
                    { "publication", publication }
                };
                if (content != null)
                {
                    foreach (var pair in content)
                    {
                        if (payload.ContainsKey(pair.Key)) continue;
                        payload[pair.Key] = pair.Value;
                    }
                }
                payload["checksum"] = UnityCliDebugSessionStorePayload.Checksum(payload);
                var json = UnityCliSerializer.Serialize(payload);
                var bytes = Encoding.UTF8.GetByteCount(json);
                if (bytes > DebugLimits.MaxSnapshotBytes)
                    throw new BridgeCommandException(
                        "E_DEBUG_STORE_FAILED",
                        "The debug store payload exceeds the declared single-file byte budget.",
                        new Dictionary<string, object> { { "reason", "store-bytes-exceeded" }, { "path", Relative(path) }, { "bytes", (long)bytes }, { "maxBytes", DebugLimits.MaxSnapshotBytes } },
                        new[] { "Reduce stored debug definitions or their retained evidence, then retry." });
                UnityCliBridge.DurableWriteText(path, json);
                var published = new DebugStoreObservation
                {
                    Present = true,
                    Parsable = true,
                    Format = format,
                    EditorSessionId = sessionId,
                    DomainGeneration = generation,
                    Publication = publication,
                    Payload = payload,
                    Bytes = bytes
                };
                Observed[path] = published;
                DeleteLegacyHookStoreOnce();
                return published;
            }
        }

        /// <summary>
        /// 一次读取；不存在的文件返回 null。解析、格式、Editor 身份、generation、checksum 任一不符即不可用。
        /// 不可用原因由调用方记录缺口：这里只给出观察结果，不抛异常。
        /// </summary>
        private static DebugStoreObservation ReadOnce(string path, string format)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var length = new FileInfo(path).Length;
                var observation = new DebugStoreObservation { Present = true, Bytes = length };
                if (length > DebugLimits.MaxSnapshotBytes) return observation;
                var text = File.ReadAllText(path, Encoding.UTF8);
                observation.ContentChecksum = UnityCliDebugSessionStorePayload.Checksum(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "rawBytes", length },
                    { "rawText", text ?? string.Empty }
                });
                if (string.IsNullOrWhiteSpace(text)) return observation;
                var parsed = UnityCliSerializer.Parse(text) as IDictionary<string, object>;
                if (parsed == null) return observation;
                observation.Format = Text(parsed, "format");
                if (!string.Equals(observation.Format, format, StringComparison.Ordinal)) return observation;
                observation.EditorSessionId = Text(parsed, "editorSessionId");
                observation.DomainGeneration = Text(parsed, "domainGeneration");
                observation.Publication = Long(parsed, "publication");
                var declared = Text(parsed, "checksum");
                var unsigned = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var pair in parsed)
                {
                    if (string.Equals(pair.Key, "checksum", StringComparison.Ordinal)) continue;
                    unsigned[pair.Key] = pair.Value;
                }
                if (declared.Length == 0 || !string.Equals(declared, UnityCliDebugSessionStorePayload.Checksum(unsigned), StringComparison.Ordinal))
                    return observation;
                observation.Parsable = true;
                observation.Payload = parsed;
                return observation;
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("debug_store_read_failed path=" + Relative(path) + " " + exception.GetType().Name + ": " + UnityCliBridge.BoundedText(exception.Message, MaxGapLogBytes));
                return new DebugStoreObservation { Present = true };
            }
        }

        private static DebugStoreObservation Observe(string path, string format)
        {
            var observed = ReadOnce(path, format);
            if (observed == null) return new DebugStoreObservation();
            // 有界重读：只多读一次，避免把写入者尚未发布的中间状态当作终态。
            if (observed.Parsable) return observed;
            var repeated = ReadOnce(path, format);
            return repeated ?? observed;
        }

        /// <summary>
        /// 判定一次观察是否可用；不可用时给出稳定原因，调用方据此记录缺口并保持受影响定义为未加载。
        /// </summary>
        internal static bool TryAccept(DebugStoreObservation observation, string format, out string reason)
        {
            reason = null;
            if (observation == null) { reason = "file-unparsable"; return false; }
            if (!observation.Present) return true;
            if (observation.Bytes == 0L) { reason = "file-empty"; return false; }
            if (observation.Bytes > DebugLimits.MaxSnapshotBytes) { reason = "file-over-budget"; return false; }
            if (!string.Equals(observation.Format, format, StringComparison.Ordinal)) { reason = "format-unsupported"; return false; }
            if (!string.Equals(observation.EditorSessionId, UnityCliBridge.EditorSessionId(), StringComparison.Ordinal)) { reason = "editor-session-mismatch"; return false; }
            if (!observation.Parsable) { reason = "file-unparsable"; return false; }
            return true;
        }

        /// <summary>
        /// 删除本 owner 的两个固定文件；只在 Editor 正常退出时调用。
        /// 本 Editor 启动的定义与事件 checkpoint 随启动失效，不跨启动保留，也不删除其他文件。
        /// </summary>
        internal static void DeleteOwnedFiles()
        {
            TryDelete(DefinitionsPath());
            TryDelete(EventsPath());
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("debug_store_delete_failed path=" + Relative(path) + " " + exception.GetType().Name);
            }
        }

        /// <summary>把不可用原因写进有界日志；不做无界累积。</summary>
        internal static void LogUnusable(string path, string reason)
        {
            UnityCliBridge.WriteLog("debug_store_unusable path=" + Relative(path) + " reason=" + UnityCliBridge.BoundedText(reason ?? string.Empty, MaxGapLogBytes));
        }

        /// <summary>
        /// 一次性删除旧 Hook store 的两个固定文件；只在 debug store 首次成功发布后执行一次，
        /// 不扫描目录、不删除其他项目或用户数据。
        /// </summary>
        private static void DeleteLegacyHookStoreOnce()
        {
            if (legacyRemoved) return;
            legacyRemoved = true;
            var root = LegacyRoot();
            var definitions = Path.Combine(root, "definitions.json");
            var events = Path.Combine(root, "events.json");
            try
            {
                if (File.Exists(definitions)) File.Delete(definitions);
                if (File.Exists(events)) File.Delete(events);
                if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0) Directory.Delete(root);
                UnityCliBridge.WriteLog("debug_store_legacy_hook_store_removed");
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("debug_store_legacy_hook_store_removal_failed " + exception.GetType().Name + ": " + UnityCliBridge.BoundedText(exception.Message, MaxGapLogBytes));
            }
        }

        // ------------------------------------------------------------------
        // 机制状态（SessionState，重启即作废）
        // ------------------------------------------------------------------

        /// <summary>本次 Editor 启动的事件 sequence 下界；新域不重复使用更小的 sequence。</summary>
        internal static long EventFloor()
        {
            return ReadMechanismLong(EventFloorKey);
        }

        internal static void SetEventFloor(long value)
        {
            WriteMechanism(EventFloorKey, Math.Max(0L, value).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>读取本次启动的恢复抑制集合；跨 Domain Reload 保留，Editor 重启即作废。</summary>
        internal static Dictionary<string, string> LoadSuppression()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var text = ReadMechanism(SuppressionKey);
            if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > MaxMechanismBytes) return result;
            var parsed = UnityCliSerializer.Parse(text) as IDictionary<string, object>;
            if (parsed == null) return result;
            foreach (var pair in parsed)
            {
                if (result.Count >= DebugLimits.MaxSuppressionEntries) break;
                result[pair.Key] = pair.Value == null ? string.Empty : Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
            }
            return result;
        }

        /// <summary>保存恢复抑制集合；失败会使调用方关闭 gate 并保持故障定义不可自动复活。</summary>
        internal static void SaveSuppression(IDictionary<string, string> values)
        {
            var payload = new Dictionary<string, object>(StringComparer.Ordinal);
            var count = 0;
            if (values != null)
            {
                foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (count >= DebugLimits.MaxSuppressionEntries) break;
                    payload[pair.Key] = UnityCliBridge.BoundedText(pair.Value ?? string.Empty, 512);
                    count++;
                }
            }
            WriteMechanism(SuppressionKey, UnityCliSerializer.Serialize(payload));
        }

        internal static void ClearSuppression()
        {
            SessionState.SetString(SuppressionKey, string.Empty);
            SessionState.SetString(EventFloorKey, string.Empty);
            SessionState.SetString(MechanismOwnerSessionKey, UnityCliBridge.EditorSessionId());
        }

        /// <summary>唯一读取机制状态文本的成员：记录的 editorSessionId 与当前不一致时按无值处理并清除。</summary>
        private static string ReadMechanism(string key)
        {
            var owner = SessionState.GetString(MechanismOwnerSessionKey, string.Empty);
            if (!string.Equals(owner, UnityCliBridge.EditorSessionId(), StringComparison.Ordinal)) return string.Empty;
            return SessionState.GetString(key, string.Empty);
        }

        private static long ReadMechanismLong(string key)
        {
            var text = ReadMechanism(key);
            long value;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0L ? value : 0L;
        }

        private static void WriteMechanism(string key, string value)
        {
            // 机制状态必须能证明它属于本次 Editor 启动：写入前先记录 owner 身份。
            SessionState.SetString(MechanismOwnerSessionKey, UnityCliBridge.EditorSessionId());
            SessionState.SetString(key, value ?? string.Empty);
        }

        private static string Relative(string path)
        {
            var root = UnityCliBridge.ProjectRoot();
            if (!string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return path.Substring(root.Length).TrimStart('\\', '/');
            return Path.GetFileName(path);
        }

        private static string Text(IDictionary<string, object> data, string key)
        {
            if (data == null) return string.Empty;
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static long Long(IDictionary<string, object> data, string key)
        {
            if (data == null) return 0L;
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return 0L;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0L; }
        }
    }
}
#endif
