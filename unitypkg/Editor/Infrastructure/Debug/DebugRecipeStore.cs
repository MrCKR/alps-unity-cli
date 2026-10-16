#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>recipe 校验的稳定失败原因；调用方据此映射错误码与 nextActions。</summary>
    internal static class DebugRecipeFailures
    {
        internal const string ContentInvalid = "content-invalid";
        internal const string FormatUnsupported = "format-unsupported";
        internal const string ChecksumMissing = "checksum-missing";
        internal const string ChecksumMismatch = "checksum-mismatch";
        internal const string PathMismatch = "path-mismatch";
        internal const string OverBudget = "over-budget";
        internal const string ExcludedContent = "excluded-content";
        internal const string CursorExpired = "cursor-expired";
        /// <summary>
        /// 该 session 没有保留的导出快照（尚未导出，或 Domain Reload 已结束上一个 generation）。
        /// </summary>
        internal const string SnapshotMissing = "snapshot-missing";

        internal const string SessionMissing = "session-missing";
    }

    /// <summary>一次 recipe 校验的结果；失败时 success 为 false 且给出稳定原因与自足说明。</summary>
    internal sealed class DebugRecipeValidation
    {
        internal bool Success;
        internal string Failure = string.Empty;
        internal string Message = string.Empty;
        internal long Bytes;
        internal Dictionary<string, object> Document;
    }

    /// <summary>一次 recipe 分页读取的结果；fragment 始终存在，complete 时它是完整文档文本。</summary>
    internal sealed class DebugRecipePageResult
    {
        internal string SessionId = string.Empty;
        internal string SnapshotId = string.Empty;
        internal long Publication;
        internal long Offset;
        internal long TotalBytes;
        internal int PageBytes;
        internal bool Complete;
        internal bool HasMore;
        internal string Cursor = string.Empty;
        internal string Fragment = string.Empty;
        internal Dictionary<string, object> Document;
    }

    /// <summary>
    /// recipe 文档的冻结编解码、校验与分页 cursor owner，以及每 session 唯一的有界导出快照表。
    ///
    /// 约定：
    /// - 文档只包含可复用的配置事实：session 定义、program 源码/ABI/bindings、Hook 配置、检查定义与时钟。
    ///   活动 pause 现场、对象句柄、Thread/Task/delegate 对象与未完成命令一律不得出现；出现即 fail closed。
    /// - 快照只存在于内存：导出不产生 artifact、不写文件。每个 session 同时最多一个快照，
    ///   再次导出会替换它并使旧 cursor 失效；分页只读同一个快照，不可能把多次读取拼成一个文件。
    /// - cursor 形状固定为 snapshotId + byteOffset，字节切片始终落在 UTF-8 字符边界上。
    /// </summary>
    internal static class DebugRecipeStore
    {
        /// <summary>冻结格式号；字段集变化必须同步升号，旧格式一律不可用。</summary>
        internal const string Format = "unitycli.debug.recipe/1";
        internal const string FormatField = "format";
        internal const string ChecksumField = "checksum";
        internal const string ProjectPathField = "projectPath";
        internal const string PublicationField = "publication";
        internal const string SessionField = "session";
        internal const string SessionIdField = "sessionId";

        private const char CursorSeparator = '#';

        /// <summary>
        /// 绝不允许进入 recipe 的键。它们是活动机制状态或托管对象身份：
        /// pause 现场、句柄、Thread/Task/delegate 以及未完成命令都不可能被复用为配置。
        /// </summary>
        private static readonly HashSet<string> ExcludedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "pause", "pauseId", "pauseSites", "pauses", "breakpointPauses",
            "handles", "handleId", "handleIds", "handleTable",
            "threads", "thread", "threadId", "threadIds", "managedTask", "task", "tasks", "taskId",
            "delegate", "delegates", "callback", "callbackInstance",
            "programRuns", "programRunId", "invocations", "invocationId", "invocation",
            "unfinishedCommands", "pendingCommands", "pendingInjections", "requestId", "continuationToken",
            "weakReferences", "unityObject", "objectId", "instanceId"
        };

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Snapshot> Snapshots = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        private static long snapshotSequence;

        /// <summary>一个不可变导出快照；只在建立时计算一次字节与 id。</summary>
        private sealed class Snapshot
        {
            internal string SessionId = string.Empty;
            internal string SnapshotId = string.Empty;
            internal long Publication;
            internal string DomainGeneration = string.Empty;
            internal byte[] Bytes;
            internal string CreatedAtUtc = string.Empty;
        }

        /// <summary>对不含 checksum 的文档计算跨键序稳定的 SHA-256 小写 hex；复用统一载荷校验和实现。</summary>
        internal static string Checksum(IDictionary<string, object> document)
        {
            return UnityCliDebugSessionStorePayload.Checksum(document);
        }

        /// <summary>把文档文本解析为 recipe object；失败返回 null。</summary>
        internal static Dictionary<string, object> Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return UnityCliSerializer.Parse(text) as Dictionary<string, object>; }
            catch { return null; }
        }

        /// <summary>
        /// 校验完整内容、格式、checksum、路径与预算。任一规则命中即 fail closed 并给出稳定原因。
        /// 路径是辅助信息，但与本机 canonical 项目不一致时仍然失败：recipe 不能被悄悄套用到别的项目。
        /// </summary>
        internal static DebugRecipeValidation Validate(Dictionary<string, object> document, string expectedProjectPath)
        {
            var result = new DebugRecipeValidation { Document = document };
            if (document == null)
            {
                result.Failure = DebugRecipeFailures.ContentInvalid;
                result.Message = "The recipe text is not a JSON object, so it cannot be validated or imported.";
                return result;
            }
            var format = Text(document, FormatField);
            if (!string.Equals(format, Format, StringComparison.Ordinal))
            {
                result.Failure = DebugRecipeFailures.FormatUnsupported;
                result.Message = "The recipe declares format '" + format + "'; this build accepts only '" + Format + "'.";
                return result;
            }
            var declared = Text(document, ChecksumField);
            if (declared.Length == 0)
            {
                result.Failure = DebugRecipeFailures.ChecksumMissing;
                result.Message = "The recipe carries no checksum field, so its content cannot be trusted.";
                return result;
            }
            var unsigned = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in document)
            {
                if (string.Equals(pair.Key, ChecksumField, StringComparison.Ordinal)) continue;
                unsigned[pair.Key] = pair.Value;
            }
            string actual;
            try { actual = Checksum(unsigned); }
            catch (Exception exception)
            {
                result.Failure = DebugRecipeFailures.ContentInvalid;
                result.Message = "The recipe contains a value that is not a JSON value: " + exception.Message;
                return result;
            }
            if (!string.Equals(declared, actual, StringComparison.Ordinal))
            {
                result.Failure = DebugRecipeFailures.ChecksumMismatch;
                result.Message = "The recipe checksum does not match its content, so the file was modified or truncated after it was written.";
                return result;
            }
            string text;
            try { text = UnityCliSerializer.Serialize(document); }
            catch (Exception exception)
            {
                result.Failure = DebugRecipeFailures.ContentInvalid;
                result.Message = "The recipe could not be serialized as JSON: " + exception.Message;
                return result;
            }
            result.Bytes = Encoding.UTF8.GetByteCount(text);
            if (result.Bytes > DebugLimits.MaxRecipeBytes)
            {
                result.Failure = DebugRecipeFailures.OverBudget;
                result.Message = "The recipe is " + result.Bytes.ToString(CultureInfo.InvariantCulture) + " bytes, above the "
                    + DebugLimits.MaxRecipeBytes.ToString(CultureInfo.InvariantCulture) + " byte single-recipe budget.";
                return result;
            }
            var forbidden = FindExcludedKey(document, 0);
            if (forbidden != null)
            {
                result.Failure = DebugRecipeFailures.ExcludedContent;
                result.Message = "The recipe carries excluded key '" + forbidden + "': live pause sites, object handles, Thread/Task/delegate objects and unfinished commands are mechanism state and can never be reused as configuration.";
                return result;
            }
            var declaredPath = Text(document, ProjectPathField);
            if (declaredPath.Length == 0 || !string.Equals(NormalizePath(declaredPath), NormalizePath(expectedProjectPath), StringComparison.OrdinalIgnoreCase))
            {
                result.Failure = DebugRecipeFailures.PathMismatch;
                result.Message = "The recipe was written for project '" + declaredPath + "', which is not this canonical project path; import refuses to apply a recipe to a different project.";
                return result;
            }
            var session = Value(document, SessionField) as IDictionary<string, object>;
            if (session == null || Text(session, SessionIdField).Length == 0)
            {
                result.Failure = DebugRecipeFailures.ContentInvalid;
                result.Message = "The recipe has no session section with a sessionId, so no inactive session definition can be established.";
                return result;
            }
            result.Success = true;
            return result;
        }

        /// <summary>递归查找被排除的键；有界深度，避免异常结构造成无界遍历。</summary>
        private static string FindExcludedKey(object value, int depth)
        {
            if (depth > 8) return null;
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                foreach (var pair in dictionary)
                {
                    if (ExcludedKeys.Contains(pair.Key)) return pair.Key;
                    var nested = FindExcludedKey(pair.Value, depth + 1);
                    if (nested != null) return nested;
                }
                return null;
            }
            var list = value as IList;
            if (list != null)
            {
                foreach (var item in list)
                {
                    var nested = FindExcludedKey(item, depth + 1);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.TrimEnd('\\', '/'); }
        }

        internal static string Text(IDictionary<string, object> data, string key)
        {
            if (data == null) return string.Empty;
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        internal static object Value(IDictionary<string, object> data, string key)
        {
            if (data == null) return null;
            object value;
            return data.TryGetValue(key, out value) ? value : null;
        }

        internal static long Long(IDictionary<string, object> data, string key)
        {
            if (data == null) return 0L;
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return 0L;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0L; }
        }

        // ------------------------------------------------------------------
        // 每 session 一个的有界快照
        // ------------------------------------------------------------------

        /// <summary>
        /// 建立或替换该 session 的导出快照；超过单快照字节预算时在建立前失败，不产生任何快照。
        /// 第一页调用本方法冻结 publication，之后的分页只读同一个快照。
        /// </summary>
        internal static DebugRecipePageResult Publish(string sessionId, long publication, string domainGeneration, Dictionary<string, object> document, int pageBytes)
        {
            var text = UnityCliSerializer.Serialize(document);
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.LongLength > DebugLimits.MaxRecipeBytes)
                throw new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "The recipe is " + bytes.LongLength.ToString(CultureInfo.InvariantCulture) + " bytes, above the per-recipe budget of "
                    + DebugLimits.MaxRecipeBytes.ToString(CultureInfo.InvariantCulture) + " bytes.",
                    new Dictionary<string, object> { { "bytes", (long)bytes.LongLength }, { "maxBytes", DebugLimits.MaxRecipeBytes }, { "sessionId", sessionId } },
                    new[] { "Reduce the session's program sources, hook definitions or check declarations before exporting again." });
            lock (Sync)
            {
                snapshotSequence++;
                var snapshot = new Snapshot
                {
                    SessionId = sessionId,
                    SnapshotId = sessionId + "@" + publication.ToString(CultureInfo.InvariantCulture) + "/" + snapshotSequence.ToString(CultureInfo.InvariantCulture),
                    Publication = publication,
                    DomainGeneration = domainGeneration ?? string.Empty,
                    Bytes = bytes,
                    CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
                // 每 session 最多一个快照：替换即失效旧 cursor，不保留第二个可读的旧 publication。
                Snapshots[sessionId] = snapshot;
                return PageOf(snapshot, 0L, pageBytes);
            }
        }

        /// <summary>按 cursor 读取同一快照的下一页；快照被替换、session 不同或 cursor 越界一律返回过期。</summary>
        internal static DebugRecipePageResult ReadPage(string sessionId, string cursor, int pageBytes, out string failure, out string message)
        {
            failure = null;
            message = string.Empty;
            string snapshotId;
            long offset;
            if (!TryDecodeCursor(cursor, out snapshotId, out offset))
            {
                failure = DebugRecipeFailures.CursorExpired;
                message = "The cursor is not a valid '<snapshotId>#<byteOffset>' value, so it cannot address a retained snapshot.";
                return null;
            }
            lock (Sync)
            {
                Snapshot snapshot;
                if (!Snapshots.TryGetValue(sessionId ?? string.Empty, out snapshot))
                {
                    failure = DebugRecipeFailures.SnapshotMissing;
                    message = "This session has no retained export snapshot in the current domain generation.";
                    return null;
                }
                if (!string.Equals(snapshot.SnapshotId, snapshotId, StringComparison.Ordinal))
                {
                    failure = DebugRecipeFailures.CursorExpired;
                    message = "The snapshot this cursor addresses was replaced by a newer export of the same session.";
                    return null;
                }
                if (offset < 0L || offset > snapshot.Bytes.LongLength)
                {
                    failure = DebugRecipeFailures.CursorExpired;
                    message = "The cursor offset is outside the retained snapshot.";
                    return null;
                }
                return PageOf(snapshot, offset, pageBytes);
            }
        }

        /// <summary>当前 session 是否已有快照。</summary>
        internal static bool HasSnapshot(string sessionId)
        {
            lock (Sync) return sessionId != null && Snapshots.ContainsKey(sessionId);
        }

        /// <summary>使一个 session 的快照失效；cursor 之后返回过期。</summary>
        internal static bool Invalidate(string sessionId)
        {
            lock (Sync) return sessionId != null && Snapshots.Remove(sessionId);
        }

        /// <summary>
        /// 使全部快照失效。Domain Reload 后旧域的内存快照不复存在，Play 退出与 Editor 退出同样失效；
        /// 调用方不需要（也不可能）继续读取旧 publication。
        /// </summary>
        internal static void InvalidateAll()
        {
            lock (Sync) Snapshots.Clear();
        }

        private static DebugRecipePageResult PageOf(Snapshot snapshot, long offset, int pageBytes)
        {
            var budget = pageBytes <= 0 ? DebugLimits.RecipePageDefaultBytes : Math.Min(pageBytes, DebugLimits.RecipePageMaxBytes);
            var bytes = snapshot.Bytes;
            var total = bytes.LongLength;
            var remaining = total - offset;
            var count = remaining > budget ? budget : (int)remaining;
            // 页边界必须落在 UTF-8 字符边界：不切断多字节序列，也不产生替换字符。
            while (count > 0 && (bytes[offset + count - 1] & 0x80) != 0) count--;
            // 保证前进：极端情况下整页都落在一个多字节序列内部时，至少完整送出该字符的第一个序列。
            if (count == 0) count = Math.Min(FirstCharacterLength(bytes, offset), (int)Math.Min(remaining, int.MaxValue));
            var fragment = count == 0 ? string.Empty : Encoding.UTF8.GetString(bytes, (int)offset, count);
            var next = offset + count;
            var complete = next >= total;
            var result = new DebugRecipePageResult
            {
                SessionId = snapshot.SessionId,
                SnapshotId = snapshot.SnapshotId,
                Publication = snapshot.Publication,
                Offset = offset,
                TotalBytes = total,
                PageBytes = count,
                Complete = complete,
                HasMore = !complete,
                Fragment = fragment,
                Cursor = complete ? string.Empty : EncodeCursor(snapshot.SnapshotId, next)
            };
            if (complete)
            {
                try { result.Document = UnityCliSerializer.Parse(fragment) as Dictionary<string, object>; }
                catch { result.Document = null; }
            }
            return result;
        }

        /// <summary>offset 处第一个完整 UTF-8 字符的字节长度（1..4）；越界返回 1。</summary>
        private static int FirstCharacterLength(byte[] bytes, long offset)
        {
            if (offset < 0L || offset >= bytes.LongLength) return 1;
            var lead = bytes[offset];
            if ((lead & 0x80) == 0) return 1;
            var expected = (lead & 0xE0) == 0xC0 ? 2 : (lead & 0xF0) == 0xE0 ? 3 : (lead & 0xF8) == 0xF0 ? 4 : 1;
            return expected > bytes.LongLength - offset ? 1 : expected;
        }

        internal static string EncodeCursor(string snapshotId, long offset)
        {
            return (snapshotId ?? string.Empty) + CursorSeparator + offset.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool TryDecodeCursor(string cursor, out string snapshotId, out long offset)
        {
            snapshotId = string.Empty;
            offset = 0L;
            if (string.IsNullOrEmpty(cursor)) return false;
            var separator = cursor.LastIndexOf(CursorSeparator);
            if (separator <= 0 || separator == cursor.Length - 1) return false;
            snapshotId = cursor.Substring(0, separator);
            return long.TryParse(cursor.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) && offset >= 0L;
        }
    }
}
#endif
