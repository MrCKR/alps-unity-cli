#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>跨 domain reload 续接的单 scope 进行中操作状态。</summary>
    internal static class UnityCliActiveOperation
    {
        internal static Dictionary<string, object> Load(string scope)
        {
            var path = ActiveOperationPath(scope);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Active operation state exceeds its fixed size limit.");
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
                throw new InvalidDataException("Active operation state must not contain a UTF-8 BOM.");
            var state = UnityCliSerializer.Parse(new UTF8Encoding(false, true).GetString(bytes)) as Dictionary<string, object>;
            if (state == null) throw new InvalidDataException("Active operation state root must be an object.");
            return state;
        }

        internal static void Save(string scope, Dictionary<string, object> state)
        {
            if (state == null) throw new ArgumentNullException("state");
            var serialized = UnityCliSerializer.Serialize(state) + "\n";
            if (Encoding.UTF8.GetByteCount(serialized) > 8 * 1024 * 1024) throw new InvalidDataException("Active operation state exceeds its fixed size limit.");
            DurableWriteText(ActiveOperationPath(scope), serialized);
        }

        internal static void Clear(string scope)
        {
            try { File.Delete(ActiveOperationPath(scope)); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        internal static bool Exists(string scope)
        {
            try { File.GetAttributes(ActiveOperationPath(scope)); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        /// <summary>terminal已durable后以三段式transfer取得TerminalCleanup owner，再清root与lease。</summary>
        internal static void BeginExactTerminalCleanup(string scope, string requestId, string requestDigest, bool completeAdmission = true, Action<string> cleanupBoundary = null)
        {
            BeginExactTerminalCleanup(scope, requestId, requestDigest, null, completeAdmission, cleanupBoundary);
        }

        internal static void BeginExactTerminalCleanup(string scope, string requestId, string requestDigest, BridgeQueueState state, bool completeAdmission = true, Action<string> cleanupBoundary = null)
        {
            ValidateTerminalIdentity(scope, requestId, requestDigest);
            var cleanupScope = scope + "-terminal-cleanup";
            var existing = Load(cleanupScope);
            if (existing != null && (!string.Equals(Text(existing, "requestId"), requestId, StringComparison.Ordinal)
                || !string.Equals(Text(existing, "requestDigest"), requestDigest, StringComparison.Ordinal)))
            {
                if (RetryExactTerminalCleanup(scope, cleanupBoundary)) return;
                existing = null;
            }
            if (existing == null)
            {
                existing = new Dictionary<string, object>
                {
                    { "requestId", requestId }, { "requestDigest", requestDigest },
                    { "editorSessionId", UnityCliBridge.EditorSessionId() }, { "completeAdmission", completeAdmission }
                };
                if (completeAdmission)
                {
                    var owner = state == null ? UnityCliBridge.CaptureAdmissionOwner(requestId, requestDigest) : UnityCliBridge.CaptureAdmissionOwner(state, requestId, requestDigest);
                    if (state == null) UnityCliBridge.PersistAdmissionOwnerTransfer(cleanupScope, existing, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
                    else UnityCliBridge.PersistAdmissionOwnerTransfer(state, cleanupScope, existing, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
                }
                else Save(cleanupScope, existing);
            }
            else if (completeAdmission)
            {
                object current;
                if (!existing.TryGetValue("completeAdmission", out current) || !(current is bool)) return;
                var owner = state == null ? UnityCliBridge.CaptureAdmissionOwner(requestId, requestDigest) : UnityCliBridge.CaptureAdmissionOwner(state, requestId, requestDigest);
                existing["completeAdmission"] = true;
                if (owner.OwnerState != UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup)
                {
                    if (state == null) UnityCliBridge.PersistAdmissionOwnerTransfer(cleanupScope, existing, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
                    else UnityCliBridge.PersistAdmissionOwnerTransfer(state, cleanupScope, existing, owner, UnityCliAdmissionPool.AdmissionOwnerState.TerminalCleanup);
                }
                else
                {
                    existing["admissionTransferCommitted"] = true;
                    Save(cleanupScope, existing);
                }
            }
            RetryExactTerminalCleanup(scope, state, cleanupBoundary);
        }

        /// <summary>只有确认exact owner已不存在才删除cleanup marker；读取不确定时保持原状。</summary>
        internal static bool RetryExactTerminalCleanup(string scope, Action<string> cleanupBoundary = null)
        {
            return RetryExactTerminalCleanup(scope, null, cleanupBoundary);
        }

        internal static bool RetryExactTerminalCleanup(string scope, BridgeQueueState state, Action<string> cleanupBoundary = null)
        {
            var cleanupScope = scope + "-terminal-cleanup";
            var marker = Load(cleanupScope);
            if (marker == null) return false;
            var requestId = Text(marker, "requestId");
            var requestDigest = Text(marker, "requestDigest");
            var editorSessionId = Text(marker, "editorSessionId");
            object completeAdmissionValue;
            if (!marker.TryGetValue("completeAdmission", out completeAdmissionValue) || !(completeAdmissionValue is bool)) return true;
            var completeAdmission = (bool)completeAdmissionValue;
            ValidateTerminalIdentity(scope, requestId, requestDigest);
            if (!string.Equals(editorSessionId, UnityCliBridge.EditorSessionId(), StringComparison.Ordinal))
            {
                ClearForTerminalCleanup(cleanupScope, cleanupBoundary);
                return false;
            }
            UnityCliBridge.AdmissionOwnerIdentity admissionOwner = null;
            if (completeAdmission)
            {
                try { admissionOwner = state == null ? UnityCliBridge.ReconcileAdmissionTransferForLiveMarker(cleanupScope, marker, requestId, requestDigest) : UnityCliBridge.ReconcileAdmissionTransferForLiveMarker(state, cleanupScope, marker, requestId, requestDigest); }
                catch { return true; }
                if (admissionOwner == null) return false;
            }
            Dictionary<string, object> active;
            try { active = Load(scope); }
            catch
            {
                bool markerOwnsLease;
                if (!UnityCliBridge.UnityCliSessionMutationLease.TryIsOwnedBy(requestId, requestDigest, out markerOwnsLease)) return true;
                if (!markerOwnsLease)
                {
                    if (completeAdmission && !(state == null ? UnityCliBridge.CompleteAdmissionRoot(admissionOwner) : UnityCliBridge.CompleteAdmissionRoot(state, admissionOwner))) return true;
                    ClearForTerminalCleanup(cleanupScope, cleanupBoundary);
                    return false;
                }
                try { ClearForTerminalCleanup(scope, cleanupBoundary); active = null; }
                catch { return true; }
            }
            if (active != null && string.Equals(Text(active, "requestId"), requestId, StringComparison.Ordinal)
                && string.Equals(Text(active, "requestDigest"), requestDigest, StringComparison.Ordinal))
            {
                try { ClearForTerminalCleanup(scope, cleanupBoundary); }
                catch { return true; }
            }
            UnityCliBridge.UnityCliSessionMutationLease.Release(requestId, requestDigest);
            bool isOwned;
            if (!UnityCliBridge.UnityCliSessionMutationLease.TryIsOwnedBy(requestId, requestDigest, out isOwned) || isOwned) return true;
            if (completeAdmission && !(state == null ? UnityCliBridge.CompleteAdmissionRoot(admissionOwner) : UnityCliBridge.CompleteAdmissionRoot(state, admissionOwner))) return true;
            ClearForTerminalCleanup(cleanupScope, cleanupBoundary);
            return false;
        }

        private static void ClearForTerminalCleanup(string scope, Action<string> cleanupBoundary)
        {
            if (cleanupBoundary != null) cleanupBoundary(scope);
            Clear(scope);
        }

        private const int MaxTerminalCacheBytes = 8 * 1024 * 1024;
        private const int MaxTerminalEntryBytes = 2 * 1024 * 1024;
        private const int MaxRetiredTerminalIdentities = 4096;
        private const int TerminalAdmissionIdentityReserve = 8;

        private sealed class TerminalCacheState
        {
            internal List<Dictionary<string, object>> Entries = new List<Dictionary<string, object>>();
            internal HashSet<string> RetiredIdentities = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>exact retired history满载后在新lease发布前关闭admission，已有effect仍可写terminal。</summary>
        internal static void EnsureTerminalAdmission()
        {
            var cache = LoadTerminalCache();
            if (cache.Entries.Count + cache.RetiredIdentities.Count + TerminalAdmissionIdentityReserve >= MaxRetiredTerminalIdentities)
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal history reached its fixed exact-identity limit; restart the Editor session before submitting another mutation.");
        }

        /// <summary>读取当前Editor session内的exact terminal；同requestId不同digest稳定拒绝。</summary>
        internal static bool TryGetTerminal(string scope, string requestId, string requestDigest, out object envelope)
        {
            envelope = null;
            ValidateTerminalIdentity(scope, requestId, requestDigest);
            var cache = LoadTerminalCache();
            foreach (var entry in cache.Entries)
            {
                if (!string.Equals(Text(entry, "scope"), scope, StringComparison.Ordinal)
                    || !string.Equals(Text(entry, "requestId"), requestId, StringComparison.Ordinal)) continue;
                if (!string.Equals(Text(entry, "requestDigest"), requestDigest, StringComparison.Ordinal))
                    throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Terminal requestId was reused with different input.");
                object stored;
                if (!entry.TryGetValue("envelope", out stored) || stored == null)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal cache entry is incomplete; outcome is unknown.");
                envelope = stored;
                return true;
            }
            RejectRetiredTerminal(cache.RetiredIdentities, scope, requestId);
            return false;
        }

        /// <summary>按业务selector读取terminal，仅用于start响应丢失且调用方尚未取得root digest的恢复。</summary>
        internal static bool TryGetTerminalBySelector(string scope, string requestId, string selectorDigest, out object envelope)
        {
            envelope = null;
            ValidateTerminalIdentity(scope, requestId, selectorDigest);
            var cache = LoadTerminalCache();
            foreach (var entry in cache.Entries)
            {
                if (!string.Equals(Text(entry, "scope"), scope, StringComparison.Ordinal)
                    || !string.Equals(Text(entry, "requestId"), requestId, StringComparison.Ordinal)) continue;
                if (!string.Equals(Text(entry, "selectorDigest"), selectorDigest, StringComparison.Ordinal))
                    throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Terminal requestId was reused with different input.");
                object stored;
                if (!entry.TryGetValue("envelope", out stored) || stored == null)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal cache entry is incomplete; outcome is unknown.");
                envelope = stored;
                return true;
            }
            RejectRetiredTerminal(cache.RetiredIdentities, scope, requestId);
            return false;
        }

        internal static object StoreTerminalFirst(string scope, string requestId, string requestDigest, object envelope)
        {
            return StoreTerminalFirst(scope, requestId, requestDigest, null, envelope);
        }

        /// <summary>first-terminal-wins；固定cache按100条与8MiB双重上限保留当前session最新identity。</summary>
        internal static object StoreTerminalFirst(string scope, string requestId, string requestDigest, string selectorDigest, object envelope)
        {
            ValidateTerminalIdentity(scope, requestId, requestDigest);
            if (!string.IsNullOrEmpty(selectorDigest)) ValidateTerminalIdentity(scope, requestId, selectorDigest);
            if (envelope == null) throw new ArgumentNullException("envelope");
            var cache = LoadTerminalCache();
            foreach (var entry in cache.Entries)
            {
                if (!string.Equals(Text(entry, "scope"), scope, StringComparison.Ordinal)
                    || !string.Equals(Text(entry, "requestId"), requestId, StringComparison.Ordinal)) continue;
                if (!string.Equals(Text(entry, "requestDigest"), requestDigest, StringComparison.Ordinal)
                    || (!string.IsNullOrEmpty(selectorDigest) && !string.Equals(Text(entry, "selectorDigest"), selectorDigest, StringComparison.Ordinal)))
                    throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Terminal requestId was reused with different input.");
                object stored;
                if (!entry.TryGetValue("envelope", out stored) || stored == null)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal cache entry is incomplete; outcome is unknown.");
                return stored;
            }
            RejectRetiredTerminal(cache.RetiredIdentities, scope, requestId);
            if (cache.Entries.Count + cache.RetiredIdentities.Count >= MaxRetiredTerminalIdentities)
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal exact history has no reserved identity slot; outcome is unknown.");
            var added = new Dictionary<string, object>
            {
                { "scope", scope }, { "requestId", requestId }, { "requestDigest", requestDigest },
                { "storedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, { "envelope", envelope }
            };
            if (!string.IsNullOrEmpty(selectorDigest)) added["selectorDigest"] = selectorDigest;
            if (Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(added)) > MaxTerminalEntryBytes)
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal envelope exceeds its fixed size limit; outcome is unknown.");
            cache.Entries.Add(added);
            var serialized = SerializeTerminalCache(cache);
            while (cache.Entries.Count > 100 || Encoding.UTF8.GetByteCount(serialized) > MaxTerminalCacheBytes)
            {
                if (cache.Entries.Count <= 1)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal cache cannot retain the new terminal within its fixed size limit; outcome is unknown.");
                var retired = cache.Entries[0];
                if (cache.RetiredIdentities.Count >= MaxRetiredTerminalIdentities)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal exact history cannot retire another identity; outcome is unknown.");
                cache.RetiredIdentities.Add(TerminalIdentityHash(Text(retired, "scope"), Text(retired, "requestId")));
                cache.Entries.RemoveAt(0);
                serialized = SerializeTerminalCache(cache);
            }
            try { DurableWriteText(TerminalCachePath(), serialized); }
            catch (Exception)
            {
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal cache could not be written; outcome is unknown.");
            }
            return envelope;
        }

        private static TerminalCacheState LoadTerminalCache()
        {
            var path = TerminalCachePath();
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return new TerminalCacheState(); }
            catch (DirectoryNotFoundException) { return new TerminalCacheState(); }
            catch (Exception)
            {
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal cache cannot be read; outcome is unknown.");
            }
            if (bytes.Length > 8 * 1024 * 1024)
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal cache exceeds its fixed size limit; outcome is unknown.");
            try
            {
                var root = UnityCliSerializer.Parse(new UTF8Encoding(false, true).GetString(bytes)) as Dictionary<string, object>;
                object sessionValue;
                object entriesValue;
                object retiredValue;
                object versionValue;
                if (root == null || !root.TryGetValue("version", out versionValue) || !(versionValue is long) || (long)versionValue != 2
                    || !root.TryGetValue("editorSessionId", out sessionValue)
                    || !root.TryGetValue("entries", out entriesValue) || !(entriesValue is List<object>)
                    || !root.TryGetValue("retiredIdentities", out retiredValue) || !(retiredValue is List<object>))
                    throw new InvalidDataException("Durable terminal cache root is incomplete.");
                if (!string.Equals(sessionValue as string, UnityCliBridge.EditorSessionId(), StringComparison.Ordinal))
                    return new TerminalCacheState();
                var result = new TerminalCacheState();
                foreach (var value in (List<object>)retiredValue)
                {
                    var identity = value as string;
                    if (identity == null || identity.Length != 64 || !identity.All(Uri.IsHexDigit) || !result.RetiredIdentities.Add(identity))
                        throw new InvalidDataException("Durable terminal retired identity is invalid.");
                }
                if (result.RetiredIdentities.Count > MaxRetiredTerminalIdentities) throw new InvalidDataException("Durable terminal retired identity limit was exceeded.");
                var identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in (List<object>)entriesValue)
                {
                    var entry = value as Dictionary<string, object>;
                    if (entry == null || Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(entry)) > MaxTerminalEntryBytes)
                        throw new InvalidDataException("Durable terminal cache entry is invalid or oversized.");
                    var scope = Text(entry, "scope");
                    var requestId = Text(entry, "requestId");
                    ValidateTerminalIdentity(scope, requestId, Text(entry, "requestDigest"));
                    if (!identities.Add(scope + "\0" + requestId) || result.RetiredIdentities.Contains(TerminalIdentityHash(scope, requestId)))
                        throw new InvalidDataException("Durable terminal cache contains a duplicate or retired request identity.");
                    object selectorValue;
                    if (entry.TryGetValue("selectorDigest", out selectorValue)) ValidateTerminalIdentity(scope, requestId, selectorValue as string);
                    object envelopeValue;
                    var envelope = entry.TryGetValue("envelope", out envelopeValue) ? envelopeValue as Dictionary<string, object> : null;
                    object envelopeRequestId;
                    object envelopeOk;
                    if (envelope == null || !envelope.TryGetValue("requestId", out envelopeRequestId)
                        || !string.Equals(envelopeRequestId as string, requestId, StringComparison.Ordinal)
                        || !envelope.TryGetValue("ok", out envelopeOk) || !(envelopeOk is bool))
                        throw new InvalidDataException("Durable terminal cache envelope is invalid.");
                    DateTimeOffset storedAt;
                    if (!DateTimeOffset.TryParse(Text(entry, "storedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out storedAt))
                        throw new InvalidDataException("Durable terminal cache timestamp is invalid.");
                    result.Entries.Add(entry);
                }
                if (result.Entries.Count > 100) throw new InvalidDataException("Durable terminal cache entry limit was exceeded.");
                return result;
            }
            catch (BridgeCommandException) { throw; }
            catch (Exception)
            {
                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Durable terminal cache is corrupt; outcome is unknown.");
            }
        }

        private static string SerializeTerminalCache(TerminalCacheState cache)
        {
            return UnityCliSerializer.Serialize(new Dictionary<string, object>
            {
                { "version", 2L }, { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "retiredIdentities", SortedRetiredIdentities(cache.RetiredIdentities) },
                { "entries", cache.Entries.ConvertAll<object>(entry => entry) }
            }) + "\n";
        }

        private static void RejectRetiredTerminal(HashSet<string> retired, string scope, string requestId)
        {
            if (!retired.Contains(TerminalIdentityHash(scope, requestId))) return;
            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "The request terminal was retired from the bounded exact cache; replay is forbidden.");
        }

        private static List<object> SortedRetiredIdentities(HashSet<string> retired)
        {
            var values = new List<string>(retired);
            values.Sort(StringComparer.Ordinal);
            return values.ConvertAll<object>(value => value);
        }

        private static string TerminalIdentityHash(string scope, string requestId)
        {
            using (var sha = SHA256.Create())
                return string.Concat(Array.ConvertAll(sha.ComputeHash(Encoding.UTF8.GetBytes(scope + "\0" + requestId)), value => value.ToString("x2")));
        }

        private static void ValidateTerminalIdentity(string scope, string requestId, string requestDigest)
        {
            ActiveOperationPath(scope);
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200 || string.IsNullOrWhiteSpace(requestDigest) || requestDigest.Length != 64)
                throw new BridgeCommandException("E_EXECUTION_FAILED", "Durable terminal identity is invalid.");
            foreach (var character in requestDigest)
                if (!Uri.IsHexDigit(character)) throw new BridgeCommandException("E_EXECUTION_FAILED", "Durable terminal identity is invalid.");
        }

        private static string Text(Dictionary<string, object> value, string key)
        {
            object item;
            return value != null && value.TryGetValue(key, out item) ? item as string : null;
        }

        private static string TerminalCachePath()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrWhiteSpace(projectRoot)) throw new InvalidOperationException("Unity project root is unavailable.");
            return Path.Combine(projectRoot, "Library", "UnityCli", "terminal-cache.json");
        }

        private static string ActiveOperationPath(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope) || scope.Length > 80)
                throw new ArgumentException("Active operation scope is required.", "scope");
            foreach (var character in scope)
                if (!(char.IsLetterOrDigit(character) || character == '-'))
                    throw new ArgumentException("Active operation scope contains invalid characters.", "scope");
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrWhiteSpace(projectRoot)) throw new InvalidOperationException("Unity project root is unavailable.");
            return Path.Combine(projectRoot, "Library", "UnityCli", "active-" + scope + ".json");
        }

        private const uint MoveFileReplaceExisting = 0x1;
        private const uint MoveFileWriteThrough = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

        /// <summary>临时文件落盘后使用 write-through 替换，旧状态始终保持完整。</summary>
        private static void DurableWriteText(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(content); writer.Flush(); stream.Flush(true); }
                if (!MoveFileExW(temporary, path, MoveFileReplaceExisting | MoveFileWriteThrough))
                    throw new IOException("MoveFileExW failed: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
#endif
