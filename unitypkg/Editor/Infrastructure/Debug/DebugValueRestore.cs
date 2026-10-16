#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityCli
{
    /// <summary>
    /// debug 域条件恢复记录的唯一 owner。
    ///
    /// 职责：
    /// - 保存受管理字段写入前的原值、目标身份与写入时刻；
    /// - 在 session 清理管线中以条件恢复语义写回：只有目标仍有效且当前值仍等于本次写入值时才恢复；
    ///   其它写入存在时报告冲突，绝不覆盖业务新值。
    ///
    /// 规则：
    /// - 记录按 session 归属；一个 session 的恢复绝不触碰另一个 session 的目标。
    /// - 保存目标身份需要保留解析出的托管引用，这本身是 debugger 拥有的、在 session 生命周期内有效的记录。
    /// - 恢复写入与比较都在同一主线程调用内完成，不声称跨线程原子性；无法保证时报告需要显式恢复。
    /// </summary>
    internal static class DebugValueRestore
    {
        /// <summary>
        /// 本设施清理条目的 owner 标签。
        /// 冻结的设施 owner 列表只枚举 hook/program/input/pause/run/observation 五类报告类别；
        /// 值写入的句柄释放与条件恢复属于 value 设施，因此用自己的 owner 标签登记清理条目。
        /// </summary>
        internal const string CleanupOwnerLabel = "value";

        /// <summary>条件恢复条目的步骤名；与释放句柄条目区分。</summary>
        internal const string RestoreStepName = "debug-value-restore";

        /// <summary>释放句柄条目的步骤名。</summary>
        internal const string HandleStepName = "debug-value-handles";

        private sealed class RestoreRecord
        {
            internal string RestoreId;
            internal string SessionId;
            internal DebugValuePlan Plan;
            internal object OriginalValue;
            internal object WrittenValue;
            internal string WrittenAtUtc;
            internal double WrittenAtMonotonicMs;
            internal string Path;
            internal string MemberTypeName;
        }

        private static readonly object Sync = new object();
        private static readonly List<RestoreRecord> Records = new List<RestoreRecord>();
        private static readonly HashSet<string> RegisteredSessions = new HashSet<string>(StringComparer.Ordinal);
        private static long nextRestoreIndex;

        /// <summary>
        /// 登记一条条件恢复记录并返回公开投影。
        /// 首次为一个 session 登记时同时把恢复与句柄释放接入该 session 的清理管线。
        /// </summary>
        internal static DebugValueRestoreView Register(DebugValuePlan plan, string sessionId, object originalValue, object writtenValue, string pathText, string memberTypeName)
        {
            var record = new RestoreRecord
            {
                RestoreId = "restore-" + (++nextRestoreIndex).ToString(CultureInfo.InvariantCulture),
                SessionId = sessionId,
                Plan = plan,
                OriginalValue = originalValue,
                WrittenValue = writtenValue,
                WrittenAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                WrittenAtMonotonicMs = DebugEventClocks.MonotonicMilliseconds,
                Path = pathText ?? string.Empty,
                MemberTypeName = memberTypeName ?? string.Empty
            };
            lock (Sync) Records.Add(record);
            RegisterCleanupSteps(sessionId);
            return new DebugValueRestoreView
            {
                Registered = true,
                RestoreId = record.RestoreId,
                SessionId = sessionId,
                TargetIdentity = plan.RootIdentity,
                Path = record.Path,
                MemberTypeName = record.MemberTypeName,
                OriginalValue = DebugValueProjection.Project(record.OriginalValue, out _, out _),
                WrittenValue = DebugValueProjection.Project(record.WrittenValue, out _, out _),
                WrittenAtUtc = record.WrittenAtUtc,
                WrittenAtMonotonicMs = record.WrittenAtMonotonicMs,
                Condition = "Restored only while the target is still valid and the current value still equals the value this session wrote.",
                Note = "The record keeps the resolved target identity for the lifetime of the session, so a session shift or a later writer can make the restore report a conflict instead of overwriting."
            };
        }

        /// <summary>该 session 当前登记的条件恢复记录数量。</summary>
        internal static int CountFor(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return 0;
            lock (Sync)
            {
                var count = 0;
                foreach (var record in Records)
                {
                    if (string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)) count++;
                }
                return count;
            }
        }

        /// <summary>
        /// session 清理管线的条件恢复条目：逐条比较并写回，把三种终态分别记入报告。
        /// 目标仍有效且当前值仍等于本次写入值时写回；目标已失效记入目标失效；当前值已被其他写入
        /// 改变（或写回被拒）记入冲突。冲突与目标失效都绝不覆盖业务新值，因此它们不能再被重试变得可完成，
        /// 也不阻止 remove：报告把它们列为不可恢复的结构化效果。
        /// </summary>
        internal static bool RestoreSession(string sessionId, UnityCliDebugCleanupReport report)
        {
            List<RestoreRecord> pending;
            lock (Sync)
            {
                pending = new List<RestoreRecord>();
                foreach (var record in Records)
                {
                    if (string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)) pending.Add(record);
                }
            }
            foreach (var record in pending)
            {
                string conflict;
                bool stale;
                if (RestoreOne(record, out conflict, out stale))
                {
                    report.RestoredFields.Add(record.Path + " (" + record.Plan.RootIdentity + ")");
                    lock (Sync) Records.Remove(record);
                    continue;
                }
                if (stale)
                {
                    report.StaleTargets.Add(record.Path + " (" + record.Plan.RootIdentity + "): " + conflict);
                    report.UnrecoverableEffects.Add("field write at " + record.Path + " (" + record.Plan.RootIdentity + ") cannot be restored: " + conflict);
                }
                else
                {
                    report.ConflictingFields.Add(record.Path + " (" + record.Plan.RootIdentity + "): " + conflict);
                    report.UnrecoverableEffects.Add("field write at " + record.Path + " (" + record.Plan.RootIdentity + ") was not restored: " + conflict + "; the business value is kept");
                }
                // 两种终态都不可能因重试变得可完成：保留记录只会让 remove 被永久拒绝，因此在这里终结它。
                lock (Sync) Records.Remove(record);
            }
            return true;
        }

        /// <summary>
        /// 写回一条记录；返回 false 时给出可证明的原因与它属于哪一类终态。
        /// 先建立目标有效性，再做值比较：解析链（含根）为 null 时原目标已不存在，当前值没有可比对象，
        /// 因此那属于目标失效，不能因为“读到的不是本次写值”就报成与另一个写者的冲突。
        /// 比较与写回发生在同一个主线程调用内（清理条目声明 RequiresMainThread），不声称跨线程原子性。
        /// </summary>
        private static bool RestoreOne(RestoreRecord record, out string conflict, out bool stale)
        {
            conflict = string.Empty;
            stale = false;
            if (record.Plan == null)
            {
                stale = true;
                conflict = "target identity is no longer available";
                return false;
            }
            object current;
            string failure;
            string nullAtPath;
            if (!DebugValueSupport.TryReadPlanValue(record.Plan, out current, out failure, out nullAtPath) || !string.IsNullOrEmpty(nullAtPath))
            {
                stale = true;
                conflict = string.IsNullOrEmpty(nullAtPath)
                    ? "target is no longer readable: " + failure
                    : (string.Equals(nullAtPath, DebugValueSupport.RootNullMarker, StringComparison.Ordinal)
                        ? "the path no longer resolves (the root itself is null)"
                        : "the path no longer resolves (null at " + nullAtPath + ")");
                return false;
            }
            if (DebugValueSupport.Canonical(current) != DebugValueSupport.Canonical(record.WrittenValue))
            {
                conflict = "the current value does not equal the value this session wrote";
                return false;
            }
            string writeFailure;
            if (!DebugValueSupport.TryWritePlanValue(record.Plan, record.OriginalValue, out writeFailure))
            {
                conflict = "restore write failed: " + writeFailure;
                return false;
            }
            return true;
        }

        /// <summary>把条件恢复与句柄释放条目接入 session 清理管线；每个 (order, name) 每个 session 只登记一次。</summary>
        private static void RegisterCleanupSteps(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            lock (Sync)
            {
                if (!RegisteredSessions.Add(sessionId)) return;
            }
            var releaseOrder = UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.ReleaseWaits);
            var restoreOrder = UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.ExplicitCleanup);
            DebugSessionHost.RegisterCleanupStep(sessionId, restoreOrder, RestoreStepName, CleanupOwnerLabel, report => RestoreSession(sessionId, report),
                // 条件恢复写回业务字段，必须在主线程的同步边界上完成；主线程暂停期间由控制通道服务的 stop 推迟它。
                true);
            DebugSessionHost.RegisterCleanupStep(sessionId, releaseOrder, HandleStepName, CleanupOwnerLabel, report =>
            {
                var released = DebugHandleRegistry.ReleaseSession(sessionId);
                report.ReleasedHandles += released;
                return true;
            });
        }
    }
}
#endif
