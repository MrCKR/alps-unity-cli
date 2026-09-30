#if UNITY_EDITOR
using System;
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
    /// hook 快照存储 owner。
    ///
    /// 只使用当前 Editor Session 的固定文件（Library/UnityCli/method-hooks 下两个文件），
    /// 单写者串行、write-through 后原子替换发布；reader 只读正式路径。
    /// 会话不匹配、格式不符或内容损坏时一律 fail closed，不提供跨启动恢复。
    /// fault/disabled 保存失败时的恢复抑制与事件 sequence 下界保存在本次 Editor 启动的 SessionState 键中。
    /// </summary>
    internal static class MethodHookStore
    {
        internal const string DefinitionsFormat = "unitycli.method-hook.definitions/1";
        internal const string EventsFormat = "unitycli.method-hook.events/1";

        private const string SuppressionSessionKey = "unitycli.method-hook.recovery-suppression";
        private const string EventFloorSessionKey = "unitycli.method-hook.event-floor";
        private const int MaxSuppressionBytes = 32 * 1024;

        internal static string Root()
        {
            return Path.Combine(UnityCliBridge.ProjectRoot(), "Library", "UnityCli", "method-hooks");
        }

        internal static string DefinitionsPath() { return Path.Combine(Root(), "definitions.json"); }

        internal static string EventsPath() { return Path.Combine(Root(), "events.json"); }

        /// <summary>读取定义快照；缺失、损坏、异格式或非本 session 时返回 null。</summary>
        internal static IDictionary<string, object> LoadDefinitions(string editorSessionId)
        {
            return Load(DefinitionsPath(), DefinitionsFormat, editorSessionId);
        }

        /// <summary>读取事件 checkpoint；判定规则与定义快照一致。</summary>
        internal static IDictionary<string, object> LoadEvents(string editorSessionId)
        {
            return Load(EventsPath(), EventsFormat, editorSessionId);
        }

        private static IDictionary<string, object> Load(string path, string format, string editorSessionId)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return null;
                if (text.Length > MethodHookLimits.MaxSnapshotBytes) return null;
                var parsed = UnityCliSerializer.Parse(text) as IDictionary<string, object>;
                if (parsed == null) return null;
                object value;
                if (!parsed.TryGetValue("format", out value) || !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), format, StringComparison.Ordinal)) return null;
                if (!parsed.TryGetValue("editorSessionId", out value) || !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), editorSessionId, StringComparison.Ordinal)) return null;
                return parsed;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>原子发布定义快照；超过快照上限时在写入前失败。</summary>
        internal static void PublishDefinitions(IDictionary<string, object> payload)
        {
            Publish(DefinitionsPath(), payload);
        }

        /// <summary>原子发布事件 checkpoint。</summary>
        internal static void PublishEvents(IDictionary<string, object> payload)
        {
            Publish(EventsPath(), payload);
        }

        private static void Publish(string path, IDictionary<string, object> payload)
        {
            var json = UnityCliSerializer.Serialize(payload);
            if (Encoding.UTF8.GetByteCount(json) > MethodHookLimits.MaxSnapshotBytes)
                throw new BridgeCommandException(
                    "E_HOOK_LIMIT_EXCEEDED",
                    "The method hook snapshot exceeds its declared byte budget.", null,
                    new[] { "Reduce hook definitions, callback source size or captured State before retrying." });
            Directory.CreateDirectory(Root());
            UnityCliBridge.DurableWriteText(path, json);
        }

        /// <summary>只删除本 owner 的固定文件；不触碰其他项目或目录。</summary>
        internal static void DeleteOwnedFiles()
        {
            DeleteIfExists(DefinitionsPath());
            DeleteIfExists(EventsPath());
        }

        private static void DeleteIfExists(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* 退出路径的删除失败不影响会话收尾 */ }
        }

        /// <summary>当前会话的事件 sequence 下界；新域不重复使用更小的 sequence。</summary>
        internal static long EventFloor()
        {
            return SessionState.GetInt(EventFloorSessionKey, 0);
        }

        internal static void SetEventFloor(long value)
        {
            var bounded = value < 0 ? 0 : value > int.MaxValue ? int.MaxValue : (int)value;
            SessionState.SetInt(EventFloorSessionKey, bounded);
        }

        /// <summary>读取本次启动的恢复抑制集合；跨 Domain Reload 保留，退出即作废。</summary>
        internal static Dictionary<string, string> LoadSuppression()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var text = SessionState.GetString(SuppressionSessionKey, string.Empty);
                if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > MaxSuppressionBytes) return result;
                var parsed = UnityCliSerializer.Parse(text) as IDictionary<string, object>;
                if (parsed == null) return result;
                foreach (var pair in parsed)
                {
                    if (result.Count >= MethodHookLimits.MaxSuppressionEntries) break;
                    result[pair.Key] = pair.Value == null ? string.Empty : Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // 抑制集合不可读时按空集合处理，但调用方仍会因其他失败保持 fail closed。
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
                    if (count >= MethodHookLimits.MaxSuppressionEntries) break;
                    payload[pair.Key] = UnityCliBridge.BoundedText(pair.Value ?? string.Empty, 512);
                    count++;
                }
            }
            SessionState.SetString(SuppressionSessionKey, UnityCliSerializer.Serialize(payload));
        }

        internal static void ClearSuppression()
        {
            SessionState.SetString(SuppressionSessionKey, string.Empty);
            SessionState.SetInt(EventFloorSessionKey, 0);
        }
    }
}
#endif
