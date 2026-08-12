#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>记录当前 Editor session 内已完成请求的追加式审计日志。</summary>
    internal static class UnityCliTransactionLog
    {
        private const string SessionRotatedKey = "UnityCli.TransactionLog.Rotated";
        private const string LogFileName = "unity-cli-transactions.log";
        private const string OldLogFileName = "unity-cli-transactions.old.log";
        private const long MaxLogFileBytes = 16L * 1024L * 1024L;
        private static readonly object Sync = new object();

        /// <summary>
        /// 请求完成时调用，将原始 request/response JSON 作为嵌入节点追加到 JSONL 文件。
        /// 任意日志失败只能告警，绝不影响请求处理。
        /// </summary>
        internal static void Append(string requestId, string command, string requestJson, string responseJson)
        {
            try
            {
                var entry = new Dictionary<string, object>
                {
                    { "time", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                    { "requestId", requestId },
                    { "command", command },
                    { "request", UnityCliSerializer.Parse(requestJson) },
                    { "response", UnityCliSerializer.Parse(responseJson) }
                };
                var line = UnityCliSerializer.Serialize(entry) + Environment.NewLine;
                var encoding = new UTF8Encoding(false);
                var lineBytes = encoding.GetByteCount(line);
                if (lineBytes > MaxLogFileBytes)
                {
                    Debug.LogWarning("Unity CLI transaction log entry exceeded the bounded segment size and was omitted.");
                    return;
                }
                lock (Sync)
                {
                    var directory = GetTransactionLogDirectory();
                    var path = Path.Combine(directory, LogFileName);
                    var oldPath = Path.Combine(directory, OldLogFileName);
                    Directory.CreateDirectory(directory);
                    EnsureBoundedCapacity(path, oldPath, lineBytes);
                    File.AppendAllText(path, line, encoding);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Unity CLI transaction log write failed: " + exception.Message);
            }
        }

        /// <summary>
        /// Editor 启动路径的主线程调用。SessionState 跨 domain reload 保留，因此同一 session 只轮转一次。
        /// </summary>
        internal static void RotateForNewSession()
        {
            try
            {
                if (SessionState.GetBool(SessionRotatedKey, false)) return;

                lock (Sync)
                {
                    var logsDirectory = GetTransactionLogDirectory();
                    Directory.CreateDirectory(logsDirectory);
                    RotateBounded(
                        Path.Combine(logsDirectory, LogFileName),
                        Path.Combine(logsDirectory, OldLogFileName));
                    SessionState.SetBool(SessionRotatedKey, true);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Unity CLI transaction log rotation failed: " + exception.Message);
            }
        }

        /// <summary>追加前维持current/old两个固定segment，任何历史超大文件都不会继续保留。</summary>
        private static void EnsureBoundedCapacity(string path, string oldPath, long incomingBytes)
        {
            if (File.Exists(oldPath) && new FileInfo(oldPath).Length > MaxLogFileBytes) File.Delete(oldPath);
            if (!File.Exists(path)) return;
            var currentBytes = new FileInfo(path).Length;
            if (currentBytes <= MaxLogFileBytes && currentBytes + incomingBytes <= MaxLogFileBytes) return;
            RotateBounded(path, oldPath);
        }

        /// <summary>正常segment轮转到old；历史超大segment直接删除，避免轮转后仍违反硬上限。</summary>
        private static void RotateBounded(string path, string oldPath)
        {
            if (File.Exists(oldPath)) File.Delete(oldPath);
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length <= MaxLogFileBytes) File.Move(path, oldPath);
            else File.Delete(path);
        }

        /// <summary>transactions 日志目录：项目临时根 Library/UnityCli/。</summary>
        private static string GetTransactionLogDirectory()
        {
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library", "UnityCli");
        }
    }
}
#endif
