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
            private static bool recoveryBlocked;

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
                recoveryBlocked = false;
                if (!RecoverStaleOwner()) { recoveryBlocked = true; return; }
                try
                {
                    var directOutcome = ReconcileAcceptedDirectMutationBeforeLeaseRestore(ProductionQueueState, null);
                    if (directOutcome == DirectMutationReloadOutcome.IdentityBlocked)
                    {
                        recoveryBlocked = true;
                        return;
                    }
                    TryFinalizeOwnedPlayModeRoot();
                    if (!RestoreValidatedActiveRootLease(ProductionQueueState)) recoveryBlocked = true;
                }
                catch { recoveryBlocked = true; }
            }

            /// <summary>测试只通过隔离state执行与production相同的direct reconciliation→generic restore顺序。</summary>
            internal static string InitializeRecoveryOrderForTests(BridgeQueueState state, bool cleanupPending = false)
            {
                if (state == null || state.IsolatedExecution == null)
                    throw new ArgumentException("Lease recovery order tests require isolated state.", "state");
                var directOutcome = ReconcileAcceptedDirectMutationBeforeLeaseRestore(state, (requestId, requestDigest) => cleanupPending);
                return directOutcome == DirectMutationReloadOutcome.IdentityBlocked || !RestoreValidatedActiveRootLease(state)
                    ? DirectMutationReloadOutcome.IdentityBlocked.ToString()
                    : directOutcome.ToString();
            }

            /// <summary>accepted direct root必须先冻结effect-before终态；显式依赖默认使用production cleanup identity。</summary>
            private static DirectMutationReloadOutcome ReconcileAcceptedDirectMutationBeforeLeaseRestore(BridgeQueueState state, Func<string, string, bool> cleanupPending)
            {
                var root = state.LoadOperation("direct-mutation");
                if (root == null || !string.Equals(root.ContainsKey("phase") ? root["phase"] as string : null, "accepted_before_effect", StringComparison.Ordinal))
                    return DirectMutationReloadOutcome.NotApplicable;
                if (!string.Equals(root.ContainsKey("editorSessionId") ? root["editorSessionId"] as string : null, EditorSessionId(), StringComparison.Ordinal))
                {
                    state.ClearOperation("direct-mutation");
                    return DirectMutationReloadOutcome.NotApplicable;
                }
                var requestId = root.ContainsKey("requestId") ? root["requestId"] as string : null;
                var requestDigest = root.ContainsKey("requestDigest") ? root["requestDigest"] as string : null;
                var releaseExactOwner = cleanupPending == null
                    ? DirectMutationCleanupPendingForIdentity(requestId, requestDigest)
                    : cleanupPending(requestId, requestDigest);
                return ReconcileAcceptedDirectMutationAfterReload(state, requestId, requestDigest, releaseExactOwner);
            }

            /// <summary>固定scope恢复只接受当前session唯一root；direct reconciliation已经在本方法之前完成。</summary>
            private static bool RestoreValidatedActiveRootLease(BridgeQueueState state)
            {
                string currentRequestId;
                string currentRequestDigest;
                if (!state.TryGetCurrentLeaseOwner(out currentRequestId, out currentRequestDigest)) return false;
                Owner recoverable = null;
                var currentSession = EditorSessionId();
                foreach (var scope in new[] { "compile", "package-resolve", "refresh", "asset-refresh", "package-embed", "test-run", "build", "navmesh-bake", "occlusion-bake", "direct-mutation" })
                {
                    if (!state.OperationExists(scope)) continue;
                    Dictionary<string, object> root;
                    try { root = state.LoadOperation(scope); }
                    catch { return false; }
                    object requestId;
                    object requestDigest;
                    object stateSession;
                    if (root == null || !root.TryGetValue("requestId", out requestId) || !root.TryGetValue("requestDigest", out requestDigest)
                        || !root.TryGetValue("editorSessionId", out stateSession)) return false;
                    if (!string.Equals(stateSession as string, currentSession, StringComparison.Ordinal))
                    {
                        state.ClearOperation(scope);
                        continue;
                    }
                    var candidate = new Owner { RequestId = requestId as string, RequestDigest = requestDigest as string };
                    if (!IsSafeId(candidate.RequestId) || !IsSha256(candidate.RequestDigest) || recoverable != null) return false;
                    recoverable = candidate;
                }
                if (recoverable == null) return true;
                if (!string.IsNullOrEmpty(currentRequestId) || !string.IsNullOrEmpty(currentRequestDigest))
                    return string.Equals(currentRequestId, recoverable.RequestId, StringComparison.Ordinal)
                        && string.Equals(currentRequestDigest, recoverable.RequestDigest, StringComparison.Ordinal);
                bool recoveredLease;
                return state.TryRecoverLease(recoverable.RequestId, recoverable.RequestDigest, out recoveredLease);
            }

            /// <summary>
            /// 已通过业务 coordinator 语义校验的同 session active root 可恢复 exact lease。
            /// 同进程同 request 的损坏 digest 先按原始字节隔离；其他有效 owner 保持不变。
            /// </summary>
            internal static bool TryRecoverExact(string requestId, string requestDigest, out bool newlyAcquired)
            {
                newlyAcquired = false;
                if (recoveryBlocked && !TryClearRecoveryBlock()) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Session mutation recovery is blocked by unreadable active state; outcome is unknown.");
                if (!IsSafeId(requestId) || !IsSha256(requestDigest)) return false;
                var current = ReadOwner();
                if (current != null && OwnsCurrentProcess(current)
                    && string.Equals(current.RequestId, requestId, StringComparison.Ordinal))
                {
                    if (string.Equals(current.RequestDigest, requestDigest, StringComparison.Ordinal)) return true;
                    // 合法的不同 digest 是另一个有效 identity；只有缺失/非法 digest 才属于可隔离损坏 owner。
                    if (IsSha256(current.RequestDigest) || !QuarantineObservedOwner(current)) return false;
                }
                else if (current != null && !OwnerInvalid(current)) return false;
                return TryAcquireCore(requestId, requestDigest, true, out newlyAcquired);
            }

            /// <summary>主线程以当前 lease owner 重观察 PlayMode root；不匹配时保持 lease 与 journal 原状。</summary>
            internal static bool TryFinalizeOwnedPlayModeRoot()
            {
                Owner owner;
                byte[] ignored;
                return TryReadOwnerSnapshot(out owner, out ignored)
                    && owner != null && OwnsCurrentProcess(owner)
                    && TryFinalizeRecoveredPlayModeRoot(owner.RequestId, owner.RequestDigest);
            }

            /// <summary>原子取得 lease；首次owner要求当前不存在任何primary active root。</summary>
            internal static bool TryAcquire(string requestId, string requestDigest, out bool newlyAcquired)
            {
                return TryAcquireCore(requestId, requestDigest, false, out newlyAcquired);
            }

            private static bool TryAcquireCore(string requestId, string requestDigest, bool allowValidatedActiveRoot, out bool newlyAcquired)
            {
                newlyAcquired = false;
                if (recoveryBlocked && !TryClearRecoveryBlock()) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Session mutation recovery is blocked by unreadable active state; outcome is unknown.");
                if (!IsSafeId(requestId) || !IsSha256(requestDigest)) throw new BridgeCommandException("E_EXECUTION_FAILED", "Session mutation lease requires a safe requestId and request digest.");
                var current = ReadOwner();
                if (current != null && OwnsCurrentProcess(current) && string.Equals(current.RequestId, requestId, StringComparison.Ordinal)) return string.Equals(current.RequestDigest, requestDigest, StringComparison.Ordinal);
                if (current != null)
                {
                    if (!OwnerInvalid(current)) return false;
                    if (!QuarantineObservedOwner(current)) return false;
                }
                if (!allowValidatedActiveRoot && HasAnyPrimaryActiveRoot()) return false;
                if (!allowValidatedActiveRoot) UnityCliActiveOperation.EnsureTerminalAdmission();

                var path = LeasePath();
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)); }
                catch (Exception)
                {
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Session mutation lease storage cannot be prepared; ownership is unknown.", "Wait for lease storage to become writable before submitting another mutation.");
                }
                var token = Guid.NewGuid().ToString("N");
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
                var ownerText = UnityCliSerializer.Serialize(owner) + "\n";
                try
                {
                    // CreateNew + Flush(true)成功即是唯一发布事实；发布后再次读取只会制造I/O假失败。
                    WriteDurableNew(path, ownerText);
                    newlyAcquired = true;
                    return true;
                }
                catch (Exception)
                {
                    // 写入异常时只尝试隔离与预期完整bytes完全相同的本次owner；partial/后继owner保持不变。
                    var raw = new UTF8Encoding(false).GetBytes(ownerText);
                    var expected = new Owner
                    {
                        ProcessId = CurrentProcessId(), ProcessStartIdentity = CurrentProcessStartIdentity(),
                        RequestId = requestId, RequestDigest = requestDigest, Token = token, EditorSessionId = EditorSessionId()
                    };
                    QuarantineObservedOwner(expected, raw);
                    Owner observed;
                    byte[] ignored;
                    if (TryReadOwnerSnapshot(out observed, out ignored) && observed != null
                        && !OwnerInvalid(observed) && !SameOwner(expected, observed)) return false;
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Session mutation lease publication cannot be confirmed; ownership is unknown.", "Inspect the selected instance before submitting another mutation.");
                }
            }

            /// <summary>cleanup必须同时匹配requestId与digest，旧request不得释放同名新owner。</summary>
            internal static void Release(string requestId, string requestDigest)
            {
                ReleaseMatchingOwner(delegate(Owner owner)
                {
                    return string.Equals(owner.RequestId, requestId, StringComparison.Ordinal)
                        && string.Equals(owner.RequestDigest, requestDigest, StringComparison.Ordinal);
                });
            }

            /// <summary>cleanup查询显式返回读取是否确定；I/O不确定时不得解释为owner不存在。</summary>
            internal static bool TryIsOwnedBy(string requestId, string requestDigest, out bool isOwned)
            {
                Owner owner;
                byte[] ignored;
                if (!TryReadOwnerSnapshot(out owner, out ignored))
                {
                    isOwned = false;
                    return false;
                }
                isOwned = OwnsCurrentProcess(owner)
                    && string.Equals(owner.RequestId, requestId, StringComparison.Ordinal)
                    && string.Equals(owner.RequestDigest, requestDigest, StringComparison.Ordinal);
                return true;
            }

            /// <summary>effect ownership必须绑定完整identity；读取不确定时以outcome unknown fail closed。</summary>
            internal static bool IsOwnedBy(string requestId, string requestDigest)
            {
                bool isOwned;
                if (!TryIsOwnedBy(requestId, requestDigest, out isOwned))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Session mutation lease ownership cannot be read; outcome is unknown.", "Wait for lease storage to become readable before deciding whether to submit another mutation.");
                return isOwned;
            }

            /// <summary>仅当目标是当前唯一primary root时，才把global owner归因给该scope。</summary>
            internal static bool TryGetOnlyActiveScopeOwner(string expectedScope, out string requestId, out string requestDigest)
            {
                requestId = null;
                requestDigest = null;
                Owner owner;
                byte[] raw;
                if (!TryReadOwnerSnapshot(out owner, out raw) || owner == null || !OwnsCurrentProcess(owner)) return false;
                string onlyScope = null;
                foreach (var scope in new[] { "compile", "package-resolve", "refresh", "asset-refresh", "package-embed", "test-run", "build", "navmesh-bake", "occlusion-bake", "direct-mutation" })
                {
                    if (!UnityCliActiveOperation.Exists(scope)) continue;
                    if (onlyScope != null) return false;
                    onlyScope = scope;
                }
                if (!string.Equals(onlyScope, expectedScope, StringComparison.Ordinal)) return false;
                requestId = owner.RequestId;
                requestDigest = owner.RequestDigest;
                return true;
            }

            /// <summary>原子读取当前Editor进程owner完整身份；I/O不确定时返回false且不提供半个identity。</summary>
            internal static bool TryGetCurrentOwner(out string requestId, out string requestDigest)
            {
                requestId = null;
                requestDigest = null;
                Owner owner;
                byte[] ignored;
                if (!TryReadOwnerSnapshot(out owner, out ignored)) return false;
                if (!OwnsCurrentProcess(owner)) return true;
                requestId = owner.RequestId;
                requestDigest = owner.RequestDigest;
                return true;
            }

            /// <summary>从首次读取起绑定原始bytes；读取不确定时重试，bytes变化时停止，避免释放后继owner。</summary>
            private static void ReleaseMatchingOwner(Func<Owner, bool> matches)
            {
                Owner observed = null;
                byte[] observedRaw = null;
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    if (!TryReadOwnerSnapshot(out observed, out observedRaw))
                    {
                        System.Threading.Thread.Sleep(10);
                        continue;
                    }
                    if (observed == null || !OwnsCurrentProcess(observed) || !matches(observed)) return;
                    break;
                }
                if (observed == null || observedRaw == null) return;

                for (var attempt = 0; attempt < 8; attempt++)
                {
                    if (QuarantineObservedOwner(observed, observedRaw)) return;
                    Owner current;
                    byte[] currentRaw;
                    if (TryReadOwnerSnapshot(out current, out currentRaw)
                        && (current == null || currentRaw == null || !observedRaw.SequenceEqual(currentRaw))) return;
                    System.Threading.Thread.Sleep(10);
                }
            }

            private static bool TryClearRecoveryBlock()
            {
                try
                {
                    Owner owner;
                    byte[] raw;
                    var readable = TryReadOwnerSnapshot(out owner, out raw);
                    if (!readable)
                    {
                        if (raw == null || HasAnyPrimaryActiveRoot() || !QuarantineCorruptOwner(raw)) return false;
                        if (!TryReadOwnerSnapshot(out owner, out raw)) return false;
                    }
                    if (owner != null || HasAnyPrimaryActiveRoot()) return false;
                    recoveryBlocked = false;
                    return true;
                }
                catch { return false; }
            }

            private static bool HasAnyPrimaryActiveRoot()
            {
                foreach (var scope in new[] { "compile", "package-resolve", "refresh", "asset-refresh", "package-embed", "test-run", "build", "navmesh-bake", "occlusion-bake", "direct-mutation" })
                    if (UnityCliActiveOperation.Exists(scope)) return true;
                return false;
            }

            private static bool RecoverStaleOwner()
            {
                Owner owner;
                byte[] ignored;
                if (!TryReadOwnerSnapshot(out owner, out ignored)) return false;
                if (owner != null && OwnerInvalid(owner)) QuarantineObservedOwner(owner);
                return true;
            }

            private static Owner ReadOwner()
            {
                Owner owner;
                byte[] ignored;
                if (!TryReadOwnerSnapshot(out owner, out ignored))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Session mutation lease ownership cannot be read; outcome is unknown.", "Wait for lease storage to become readable before submitting another mutation.");
                return owner;
            }

            /// <summary>返回稳定读取结果；false表示瞬时I/O不确定，null owner只表示路径确定不存在。</summary>
            private static bool TryReadOwnerSnapshot(out Owner owner, out byte[] raw)
            {
                owner = null;
                raw = null;
                try { raw = File.ReadAllBytes(LeasePath()); }
                catch (FileNotFoundException) { return true; }
                catch (DirectoryNotFoundException) { return true; }
                catch { return false; }
                owner = ParseOwner(raw);
                return owner != null;
            }

            private static Owner ParseOwner(byte[] raw)
            {
                try
                {
                    var text = new UTF8Encoding(false, true).GetString(raw);
                    var value = UnityCliSerializer.Parse(text) as IDictionary<string, object>;
                    object pidValue;
                    object startValue;
                    object requestValue;
                    object tokenValue;
                    object digestValue;
                    object sessionValue;
                    if (value == null || !value.TryGetValue("pid", out pidValue)
                        || !value.TryGetValue("requestId", out requestValue) || !value.TryGetValue("token", out tokenValue)
                        || !value.TryGetValue("processStartIdentity", out startValue)
                        || !value.TryGetValue("requestDigest", out digestValue)
                        || !value.TryGetValue("editorSessionId", out sessionValue)) return null;
                    long pid;
                    var requestId = requestValue as string;
                    var requestDigest = digestValue as string;
                    var token = tokenValue as string;
                    var processStartIdentity = startValue as string;
                    var editorSessionId = sessionValue as string;
                    if (!long.TryParse(Convert.ToString(pidValue, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out pid)
                        || pid <= 0 || !IsSafeId(requestId) || !IsSha256(requestDigest)
                        || string.IsNullOrWhiteSpace(token) || token.Length > 200
                        || string.IsNullOrWhiteSpace(processStartIdentity) || processStartIdentity.Length > 200
                        || string.IsNullOrWhiteSpace(editorSessionId) || editorSessionId.Length > 200) return null;
                    return new Owner { ProcessId = pid, ProcessStartIdentity = processStartIdentity, RequestId = requestId, RequestDigest = requestDigest, Token = token, EditorSessionId = editorSessionId };
                }
                catch { return null; }
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
                return owner == null || ProcessExited(owner.ProcessId, owner.ProcessStartIdentity);
            }

            private static bool ProcessExited(long processId, string processStartIdentity)
            {
                if (processId <= 0) return true;
                try
                {
                    using (var process = Process.GetProcessById(checked((int)processId)))
                    {
                        if (string.IsNullOrWhiteSpace(processStartIdentity)) return false;
                        var identity = "win32:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                        return !string.Equals(identity, processStartIdentity, StringComparison.Ordinal);
                    }
                }
                catch (ArgumentException) { return true; }
                catch { return false; }
            }

            /// <summary>仅在无active业务root时隔离稳定可读但不可解析的owner原始bytes。</summary>
            private static bool QuarantineCorruptOwner(byte[] expectedRaw)
            {
                if (expectedRaw == null || expectedRaw.Length == 0) return false;
                var path = LeasePath();
                var fingerprint = OwnerFingerprint(expectedRaw);
                var claim = path + ".recovery";
                FileStream claimHandle;
                if (!TryAcquireRecoveryClaim(claim, fingerprint, out claimHandle)) return false;
                try
                {
                    byte[] verify;
                    try { verify = File.ReadAllBytes(path); }
                    catch (FileNotFoundException) { return true; }
                    catch { return false; }
                    if (!expectedRaw.SequenceEqual(verify)) return false;
                    var quarantine = path + ".quarantine";
                    if (File.Exists(quarantine) && !TryDeleteObservedFile(quarantine)) return false;
                    try { File.Move(path, quarantine); }
                    catch (FileNotFoundException) { return true; }
                    catch (IOException) { return false; }
                    return TryDeleteExactFile(quarantine, expectedRaw);
                }
                finally
                {
                    claimHandle.Dispose();
                    try { File.Delete(claim); } catch { }
                }
            }

            /// <summary>跨进程recovery竞争固定claim；claim owner重读相同原始bytes后才可隔离。</summary>
            private static bool QuarantineObservedOwner(Owner observed)
            {
                Owner current;
                byte[] raw;
                if (!TryReadOwnerSnapshot(out current, out raw)) return false;
                if (current == null) return true;
                if (!SameOwner(observed, current)) return false;
                return QuarantineObservedOwner(observed, raw);
            }

            /// <summary>expectedRaw是调用者首次观测快照；任何bytes变化都使本次隔离失效。</summary>
            private static bool QuarantineObservedOwner(Owner observed, byte[] expectedRaw)
            {
                var path = LeasePath();
                Owner current;
                byte[] before;
                if (!TryReadOwnerSnapshot(out current, out before)) return false;
                if (current == null) return true;
                if (before == null || !expectedRaw.SequenceEqual(before) || !SameOwner(observed, current)) return false;

                var fingerprint = OwnerFingerprint(before);
                var claim = path + ".recovery";
                FileStream claimHandle;
                if (!TryAcquireRecoveryClaim(claim, fingerprint, out claimHandle)) return false;
                try
                {
                    byte[] verify;
                    try { verify = File.ReadAllBytes(path); }
                    catch (FileNotFoundException) { return true; }
                    catch { return false; }
                    if (!before.SequenceEqual(verify)) return false;

                    // 固定quarantine路径使崩溃残留数量有界；只有相同原始bytes的惰性副本可清理。
                    var quarantine = path + ".quarantine";
                    if (File.Exists(quarantine) && !TryDeleteObservedFile(quarantine)) return false;
                    try { File.Move(path, quarantine); }
                    catch (FileNotFoundException) { return true; }
                    catch (IOException) { return false; }
                    if (!TryDeleteExactFile(quarantine, before)) return false;
                    // 锁文件移除后顺手删除空的实例目录，保持 .temp/requests/ 下只存在进行中的 mutation。
                    try { var dir = Path.GetDirectoryName(path); if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
                    return true;
                }
                finally
                {
                    claimHandle.Dispose();
                    // 若后继恢复者已独占打开，Windows拒绝删除；因此不会删除后继claim。
                    try { File.Delete(claim); } catch { }
                }
            }

            /// <summary>固定claim用OS独占句柄提供互斥；进程退出自动释放，残留文件不会阻塞下一恢复者。</summary>
            private static bool TryAcquireRecoveryClaim(string claim, string fingerprint, out FileStream claimHandle)
            {
                claimHandle = null;
                try
                {
                    claimHandle = new FileStream(claim, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    var value = UnityCliSerializer.Serialize(new Dictionary<string, object>
                    {
                        { "pid", CurrentProcessId() },
                        { "processStartIdentity", CurrentProcessStartIdentity() },
                        { "editorSessionId", EditorSessionId() },
                        { "token", Guid.NewGuid().ToString("N") },
                        { "observedFingerprint", fingerprint },
                        { "createdAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
                    }) + "\n";
                    var raw = new UTF8Encoding(false).GetBytes(value);
                    claimHandle.SetLength(0);
                    claimHandle.Write(raw, 0, raw.Length);
                    claimHandle.Flush(true);
                    return true;
                }
                catch
                {
                    if (claimHandle != null) claimHandle.Dispose();
                    claimHandle = null;
                    return false;
                }
            }

            private static bool TryDeleteObservedFile(string path)
            {
                byte[] observed;
                try { observed = File.ReadAllBytes(path); }
                catch (FileNotFoundException) { return true; }
                catch { return false; }
                return TryDeleteExactFile(path, observed);
            }

            private static bool TryDeleteExactFile(string path, byte[] expected)
            {
                byte[] current;
                try { current = File.ReadAllBytes(path); }
                catch (FileNotFoundException) { return true; }
                catch { return false; }
                if (!expected.SequenceEqual(current)) return false;
                try { File.Delete(path); return !File.Exists(path); }
                catch { return false; }
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
                    && string.Equals(left.Token, right.Token, StringComparison.Ordinal)
                    && string.Equals(left.EditorSessionId, right.EditorSessionId, StringComparison.Ordinal);
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
