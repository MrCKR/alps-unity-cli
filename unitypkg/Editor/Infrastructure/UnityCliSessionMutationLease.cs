#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// Live Editor 与 TS 文件 mutation 共用的唯一 instance lease。
        /// 文件 owner 绑定 PID/start identity/requestId/token/editorSessionId；domain reload 只恢复同一 root。
        /// 铁律：owner 的 editorSessionId 与当前进程启动标识不符时一律作废隔离（机制状态重启作废），
        /// 不再以进程存活猜测替代；同 session 内跨进程 owner（TS worker）才回退进程死活判定。
        /// </summary>
        internal static class UnityCliSessionMutationLease
        {
            private sealed class Owner
            {
                internal long ProcessId;
                internal string ProcessStartIdentity;
                internal string RequestId;
                internal string RequestDigest;
                internal string Token;
                internal string EditorSessionId;
            }

            internal static void Initialize()
            {
                RecoverStaleOwner();
                TryFinalizeOwnedPlayModeRoot();
                if (ReadOwner() != null) return;

                // 仅交叉读取每个 coordinator 的固定 active 文件，不扫描任何请求目录。
                // 铁律：active 文件属上次启动（editorSessionId 不符）时不恢复，机制状态重启作废。
                Owner recoverable = null;
                var currentSession = EditorSessionId();
                foreach (var scope in new[] { "compile", "package-resolve", "refresh", "test-run", "build" })
                {
                    if (!UnityCliActiveOperation.Exists(scope)) continue;
                    try
                    {
                        var state = UnityCliActiveOperation.Load(scope);
                        object requestId;
                        object requestDigest;
                        object stateSession;
                        if (state == null || !state.TryGetValue("requestId", out requestId) || !state.TryGetValue("requestDigest", out requestDigest)) continue;
                        if (!state.TryGetValue("editorSessionId", out stateSession) || !string.Equals(stateSession as string, currentSession, StringComparison.Ordinal)) continue;
                        var candidate = new Owner { RequestId = requestId as string, RequestDigest = requestDigest as string };
                        if (!IsSafeId(candidate.RequestId) || !IsSha256(candidate.RequestDigest)) continue;
                        if (recoverable == null || string.CompareOrdinal(candidate.RequestId, recoverable.RequestId) < 0) recoverable = candidate;
                    }
                    catch { }
                }
                if (recoverable != null) { bool ignored; TryAcquire(recoverable.RequestId, recoverable.RequestDigest, out ignored); }
            }

            /// <summary>主线程以当前 lease owner 重观察 PlayMode root；不匹配时保持 lease 与 journal 原状。</summary>
            internal static bool TryFinalizeOwnedPlayModeRoot()
            {
                var owner = ReadOwner();
                return owner != null
                    && OwnsCurrentProcess(owner)
                    && TryFinalizeRecoveredPlayModeRoot(owner.RequestId, owner.RequestDigest);
            }

            /// <summary>原子取得 lease；同进程同 root 在 domain reload 后视为续持。</summary>
            internal static bool TryAcquire(string requestId, string requestDigest, out bool newlyAcquired)
            {
                newlyAcquired = false;
                if (!IsSafeId(requestId) || !IsSha256(requestDigest)) throw new BridgeCommandException("E_EXECUTION_FAILED", "Session mutation lease requires a safe requestId and request digest.");
                var current = ReadOwner();
                if (current != null && OwnsCurrentProcess(current) && string.Equals(current.RequestId, requestId, StringComparison.Ordinal)) return string.Equals(current.RequestDigest, requestDigest, StringComparison.Ordinal);
                if (current != null)
                {
                    if (!OwnerInvalid(current)) return false;
                    if (!QuarantineObservedOwner(current)) return false;
                }

                var path = LeasePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var token = Guid.NewGuid().ToString("N");
                var temp = path + ".create-" + CurrentProcessId().ToString(CultureInfo.InvariantCulture) + "-" + token;
                try
                {
                    var owner = new Dictionary<string, object>
                    {
                        { "pid", CurrentProcessId() },
                        { "token", token },
                        { "processStartIdentity", CurrentProcessStartIdentity() },
                        { "requestId", requestId },
                        { "requestDigest", requestDigest },
                        { "editorSessionId", EditorSessionId() },
                        { "createdAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
                    };
                    WriteDurableNew(temp, UnityCliSerializer.Serialize(owner) + "\n");
                    try { File.Move(temp, path); }
                    catch (IOException) { return false; }
                    var published = ReadOwner();
                    var ownsPublished = published != null && OwnsCurrentProcess(published)
                        && string.Equals(published.RequestId, requestId, StringComparison.Ordinal)
                        && string.Equals(published.RequestDigest, requestDigest, StringComparison.Ordinal)
                        && string.Equals(published.Token, token, StringComparison.Ordinal);
                    newlyAcquired = ownsPublished;
                    return ownsPublished;
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }

            /// <summary>只有当前 Editor 进程持有的同 root owner 可以释放 lease。</summary>
            internal static void Release(string requestId)
            {
                var owner = ReadOwner();
                if (owner == null || !OwnsCurrentProcess(owner) || !string.Equals(owner.RequestId, requestId, StringComparison.Ordinal)) return;
                QuarantineObservedOwner(owner);
            }

            internal static bool IsOwnedBy(string requestId)
            {
                var owner = ReadOwner();
                return OwnsCurrentProcess(owner) && string.Equals(owner.RequestId, requestId, StringComparison.Ordinal);
            }

            internal static string CurrentOwnerRequestId()
            {
                var owner = ReadOwner();
                return OwnsCurrentProcess(owner) ? owner.RequestId : null;
            }

            private static void RecoverStaleOwner()
            {
                var owner = ReadOwner();
                if (owner != null && OwnerInvalid(owner)) QuarantineObservedOwner(owner);
            }

            private static Owner ReadOwner()
            {
                var path = LeasePath();
                if (!File.Exists(path)) return null;
                try
                {
                    var value = UnityCliSerializer.Parse(File.ReadAllText(path, new UTF8Encoding(false, true))) as IDictionary<string, object>;
                    object pidValue;
                    object startValue;
                    object requestValue;
                    object tokenValue;
                    if (value == null || !value.TryGetValue("pid", out pidValue)
                        || !value.TryGetValue("requestId", out requestValue) || !value.TryGetValue("token", out tokenValue)) return new Owner();
                    value.TryGetValue("processStartIdentity", out startValue);
                    long pid;
                    if (!long.TryParse(Convert.ToString(pidValue, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0) return new Owner();
                    object digestValue;
                    object sessionValue;
                    value.TryGetValue("requestDigest", out digestValue);
                    value.TryGetValue("editorSessionId", out sessionValue);
                    return new Owner { ProcessId = pid, ProcessStartIdentity = startValue as string, RequestId = requestValue as string, RequestDigest = digestValue as string, Token = tokenValue as string, EditorSessionId = sessionValue as string };
                }
                catch { return new Owner(); }
            }

            private static bool OwnsCurrentProcess(Owner owner)
            {
                return owner != null && owner.ProcessId == CurrentProcessId()
                    && string.Equals(owner.ProcessStartIdentity, CurrentProcessStartIdentity(), StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(owner.Token);
            }

            /// <summary>owner 是否已失效：非本次启动（机制状态重启作废，含缺 session 字段的旧文件）或同 session 内进程已死。</summary>
            private static bool OwnerInvalid(Owner owner)
            {
                if (owner == null) return true;
                if (!string.Equals(owner.EditorSessionId, EditorSessionId(), StringComparison.Ordinal)) return true;
                return OwnerExited(owner);
            }

            /// <summary>身份缺失的不可解析 owner 来自完整原子文件，可按 fail-closed recovery claim 隔离。</summary>
            private static bool OwnerExited(Owner owner)
            {
                if (owner == null || owner.ProcessId <= 0) return true;
                try
                {
                    using (var process = Process.GetProcessById(checked((int)owner.ProcessId)))
                    {
                        if (string.IsNullOrWhiteSpace(owner.ProcessStartIdentity)) return false;
                        var identity = "win32:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                        return !string.Equals(identity, owner.ProcessStartIdentity, StringComparison.Ordinal);
                    }
                }
                catch (ArgumentException) { return true; }
                catch { return false; }
            }

            /// <summary>跨语言 recovery 先竞争同一 fingerprint claim；claim owner 重读相同原始字节后才可隔离。</summary>
            private static bool QuarantineObservedOwner(Owner observed)
            {
                var path = LeasePath();
                byte[] before;
                try { before = File.ReadAllBytes(path); }
                catch (FileNotFoundException) { return true; }
                if (!SameOwner(observed, ReadOwner())) return false;
                var fingerprint = OwnerFingerprint(before);
                var claim = path + ".recovery-" + fingerprint;
                try { WriteDurableNew(claim, UnityCliSerializer.Serialize(new Dictionary<string, object> { { "pid", CurrentProcessId() }, { "observedFingerprint", fingerprint }, { "createdAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) } }) + "\n"); }
                catch (IOException) { return false; }
                try
                {
                    byte[] verify;
                    try { verify = File.ReadAllBytes(path); }
                    catch (FileNotFoundException) { return true; }
                    if (!before.SequenceEqual(verify)) return false;
                    var quarantine = path + ".quarantine-" + fingerprint + "-" + Guid.NewGuid().ToString("N");
                    try { File.Move(path, quarantine); }
                    catch (FileNotFoundException) { return true; }
                    catch (IOException) { return false; }
                    try { File.Delete(quarantine); } catch { }
                    // 锁文件移除后顺手删除空的实例目录，保持 .temp/requests/ 下只存在进行中的 mutation。
                    try { var dir = Path.GetDirectoryName(path); if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
                    return true;
                }
                finally { try { File.Delete(claim); } catch { } }
            }

            private static string OwnerFingerprint(byte[] raw)
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(Convert.ToBase64String(raw)));
                    var builder = new StringBuilder(digest.Length * 2);
                    foreach (var value in digest) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                    return builder.ToString();
                }
            }

            private static bool SameOwner(Owner left, Owner right)
            {
                return left != null && right != null && left.ProcessId == right.ProcessId
                    && string.Equals(left.ProcessStartIdentity, right.ProcessStartIdentity, StringComparison.Ordinal)
                    && string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal)
                    && string.Equals(left.RequestDigest, right.RequestDigest, StringComparison.Ordinal)
                    && string.Equals(left.Token, right.Token, StringComparison.Ordinal);
            }

            private static void WriteDurableNew(string path, string text)
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }

            // 钥匙是机制临时状态，位于用户级临时区 .temp/requests/<实例>/ 下。
            private static string LeasePath() { return Path.Combine(DataDir(), ".temp", "requests", InstanceId(), ".instance-resource.lock"); }
            private static long CurrentProcessId() { return Process.GetCurrentProcess().Id; }
            private static string CurrentProcessStartIdentity() { using (var process = Process.GetCurrentProcess()) return "win32:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture); }
        }
    }
}
#endif
