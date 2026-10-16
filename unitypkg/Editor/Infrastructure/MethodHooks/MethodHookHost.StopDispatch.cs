#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace UnityCli
{
    /// <summary>
    /// hook 设施在 session 停止中的两个职责：
    ///
    /// 1. 关闭条目（stop-dispatch）：关闭该 session 上每个定义的未来入口，取消尚未到达执行点的投递，
    ///    并把这件事实计入报告。
    /// 2. 命中路径的 session 闸门：session 被 stop 置为关闭后，任何线程上的命中都不再进入用户代码。
    ///
    /// 规则：
    /// - 关闭入口不删除定义、不改变 revision：同一方法上的其他 session 定义不受影响。
    /// - 在途帧按自己的帧完成；闸门只挡住新的进入点。
    /// - 每个 session 只注册一个 stop-dispatch 条目，因此清理条目预算与 hook 数量无关。
    /// </summary>
    internal static partial class MethodHookHost
    {
        /// <summary>已经注册过 session 级 stop-dispatch 条目的 session。</summary>
        private static readonly HashSet<string> DispatchClosedSessions = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>该定义所属的 debug session；未声明 owner 时为空。</summary>
        internal static string SessionIdOf(MethodHookDefinition definition)
        {
            if (definition == null) return string.Empty;
            if (definition.Listener != null && !string.IsNullOrEmpty(definition.Listener.SessionId)) return definition.Listener.SessionId;
            if (definition.Breakpoint != null && !string.IsNullOrEmpty(definition.Breakpoint.SessionId)) return definition.Breakpoint.SessionId;
            return string.Empty;
        }

        /// <summary>
        /// 该定义当前是否允许新的派发。
        /// 逐定义关闭用于故障隔离（同 target 的其他定义不受影响）；session 闸门用于 session stop，
        /// 两者都必须放行：任何一个关闭都表示新入口已关闭。
        /// </summary>
        internal static bool DispatchAllowed(MethodHookDefinition definition)
        {
            if (definition == null) return false;
            if (Volatile.Read(ref definition.DispatchClosed) != 0) return false;
            var sessionId = SessionIdOf(definition);
            if (string.IsNullOrEmpty(sessionId)) return true;
            return DebugSessionHost.IsDispatchOpen(sessionId);
        }

        /// <summary>
        /// install 成功后登记该 session 的关闭条目；同一 session 只登记一次。
        /// 注册失败只记录有界日志：报告会把缺少 owner 的步骤显式报为未注册，绝不静默省略该类别。
        /// </summary>
        internal static void RegisterSessionStopDispatch(MethodHookDefinition definition)
        {
            var sessionId = SessionIdOf(definition);
            if (string.IsNullOrEmpty(sessionId)) return;
            lock (Sync)
            {
                if (!DispatchClosedSessions.Add(sessionId)) return;
            }
            var failure = DebugSessionHost.RegisterCleanupStep(
                sessionId,
                UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.StopDispatch),
                "hook-dispatch:" + sessionId,
                UnityCliDebugFacilityOwners.Hook,
                report => CloseSessionDispatch(sessionId, report));
            if (failure != null)
            {
                lock (Sync) DispatchClosedSessions.Remove(sessionId);
                UnityCliBridge.WriteLog("debug_hook_dispatch_step_reused session=" + sessionId + " reason=" + failure);
            }
        }

        /// <summary>
        /// 关闭条目的唯一实现：逐个关闭该 session 定义的未来入口、取消其 pending 投递并计数。
        /// 它不撤除 detour（那是 detach-hooks 步骤），也不删除定义。
        /// </summary>
        private static bool CloseSessionDispatch(string sessionId, UnityCliDebugCleanupReport report)
        {
            List<MethodHookDefinition> owned;
            lock (Sync)
            {
                owned = new List<MethodHookDefinition>();
                foreach (var definition in Definitions.Values)
                {
                    if (string.Equals(SessionIdOf(definition), sessionId, StringComparison.Ordinal)) owned.Add(definition);
                }
            }
            foreach (var definition in owned)
            {
                // 逐定义关闭按当前 revision 置位，绝不影响其他 session 的同 target 定义。
                Volatile.Write(ref definition.DispatchClosed, (int)Volatile.Read(ref definition.Revision));
                if (report == null) continue;
                report.StoppedFutureEntries++;
                // 未到达执行点的注入必须永不执行；关闭数量是“已取消未开始操作”的直接证据。
                report.CancelledPendingOperations += DebugScheduler.DropPendingForHook(definition.HookId);
                // 回调与断点意味着本 session 执行过任意代码：效果不可回滚，风险必须如实声明。
                var arbitrary = !string.IsNullOrEmpty(definition.PrefixCode) || !string.IsNullOrEmpty(definition.PostfixCode) || definition.Breakpoint != null;
                if (!arbitrary) continue;
                report.UntrackedEffectsPossible = true;
                report.DeclaredEffects.Add("hook " + definition.HookId + " (revision " + definition.Revision.ToString(CultureInfo.InvariantCulture) + ") executed user code on the same thread as its target method for session " + sessionId);
            }
            return true;
        }
    }
}
#endif
