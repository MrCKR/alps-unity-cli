#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// debug 域对象句柄的唯一 owner。
    ///
    /// 规则：
    /// - 句柄绑定 (runtimeId, domainGeneration) 与解析出的类型；换 runtime 或换 generation 后一律为 stale，绝不隐式重绑。
    /// - 默认弱引用：不改变目标存活；目标被回收或销毁后解析为 stale，绝不把"GC 后找不到"当成"字段为 null"。
    /// - 强引用是显式记录的干预：它改变目标存活，必须由调用方声明并在结果与 describe 中说明。
    /// - 句柄数量按 session 与全局双限，先达到者生效；上限判定发生在任何 effect 之前。
    /// - 本类型不注册 update 回调，不读写文件，不执行用户代码。
    /// </summary>
    internal static class DebugHandleRegistry
    {
        private sealed class HandleRecord
        {
            internal string HandleId;
            internal string SessionId;
            internal string RuntimeId;
            internal string DomainGeneration;
            internal string TypeName;
            internal bool Strong;
            internal WeakReference Weak;
            internal object StrongTarget;
            internal string CreatedAtUtc;
            internal double CreatedAtMonotonicMs;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, HandleRecord> Handles = new Dictionary<string, HandleRecord>(StringComparer.Ordinal);
        private static long nextHandleIndex;

        /// <summary>进程内当前持有的句柄总数。</summary>
        internal static int GlobalCount { get { lock (Sync) return Handles.Count; } }

        /// <summary>该 session 当前持有的句柄数。</summary>
        internal static int SessionCount(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return 0;
            lock (Sync)
            {
                var count = 0;
                foreach (var record in Handles.Values)
                {
                    if (string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)) count++;
                }
                return count;
            }
        }

        /// <summary>
        /// 在 effect 之前判定句柄预算；不做任何创建。判定前先清理已不可能再解析的弱句柄。
        /// 返回 null 表示可以创建，否则返回限制名称。
        /// </summary>
        internal static string CanCreate(string sessionId, out int sessionUsed, out int globalUsed)
        {
            lock (Sync)
            {
                PruneCollectedLocked();
                return EvaluateBudgetLocked(sessionId, out sessionUsed, out globalUsed);
            }
        }

        /// <summary>
        /// 在 effect 之前建立句柄。失败时不产生任何句柄，调用方必须放弃本次 effect。
        /// 预算判定前先清理已经不可能再解析的弱句柄，避免已回收目标长期占用句柄表。
        /// </summary>
        internal static string Create(string sessionId, object target, bool strong, string runtimeId, string domainGeneration, out DebugHandleView view, out int sessionUsed, out int globalUsed)
        {
            view = null;
            if (target == null) throw new ArgumentNullException(nameof(target));
            var now = DebugEventClocks.MonotonicMilliseconds;
            lock (Sync)
            {
                PruneCollectedLocked();
                var failure = EvaluateBudgetLocked(sessionId, out sessionUsed, out globalUsed);
                if (failure != null) return failure;
                var record = new HandleRecord
                {
                    HandleId = "h-" + (++nextHandleIndex).ToString(CultureInfo.InvariantCulture),
                    SessionId = sessionId,
                    RuntimeId = runtimeId ?? string.Empty,
                    DomainGeneration = domainGeneration ?? string.Empty,
                    TypeName = TypeName(target),
                    Strong = strong,
                    Weak = strong ? null : new WeakReference(target),
                    StrongTarget = strong ? target : null,
                    CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    CreatedAtMonotonicMs = now
                };
                Handles[record.HandleId] = record;
                sessionUsed = CountForSessionLocked(sessionId);
                globalUsed = Handles.Count;
                view = Project(record, "live", string.Empty);
                return null;
            }
        }

        /// <summary>
        /// 解析句柄当前状态。返回 null 表示句柄不存在；否则返回当前状态并给出可证明的失效原因。
        /// 不可解析时 target 为 null，调用方必须报告 stale 而不是把它当作 null 值。
        /// </summary>
        internal static string Resolve(string handleId, string runtimeId, string domainGeneration, out object target, out DebugHandleView view)
        {
            target = null;
            view = null;
            if (string.IsNullOrEmpty(handleId)) return "missing";
            lock (Sync)
            {
                HandleRecord record;
                if (!Handles.TryGetValue(handleId, out record)) return "missing";
                if (!string.Equals(record.RuntimeId, runtimeId ?? string.Empty, StringComparison.Ordinal))
                {
                    view = Project(record, "stale", "runtime-changed");
                    return "stale";
                }
                if (!string.Equals(record.DomainGeneration, domainGeneration ?? string.Empty, StringComparison.Ordinal))
                {
                    view = Project(record, "stale", "domain-generation-changed");
                    return "stale";
                }
                if (record.Strong)
                {
                    target = record.StrongTarget;
                    view = Project(record, "live", string.Empty);
                    return "live";
                }
                var weakTarget = record.Weak == null ? null : record.Weak.Target;
                if (weakTarget == null)
                {
                    view = Project(record, "stale", "target-collected");
                    return "stale";
                }
                if (IsDestroyedUnityObject(weakTarget))
                {
                    // Unity 已销毁对象：引用仍存在但对象已失效，必须与"字段为 null"区分。
                    view = Project(record, "stale", "unity-object-destroyed");
                    return "stale";
                }
                target = weakTarget;
                view = Project(record, "live", string.Empty);
                return "live";
            }
        }

        /// <summary>释放一个句柄；返回是否确实释放。释放后该身份不再可解析也不复用。</summary>
        internal static bool Release(string handleId)
        {
            if (string.IsNullOrEmpty(handleId)) return false;
            lock (Sync) return Handles.Remove(handleId);
        }

        /// <summary>释放一个 session 的全部句柄并返回释放数量；只触碰本 session 的记录。</summary>
        internal static int ReleaseSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return 0;
            lock (Sync)
            {
                var stale = new List<string>();
                foreach (var pair in Handles)
                {
                    if (string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal)) stale.Add(pair.Key);
                }
                foreach (var key in stale) Handles.Remove(key);
                return stale.Count;
            }
        }

        /// <summary>当前句柄预算占用摘要；与 DebugLimits 同源，不复制数量。</summary>
        internal static DebugValueHandleBudgetView Budget(string sessionId, int sessionUsed, int globalUsed)
        {
            return new DebugValueHandleBudgetView
            {
                SessionId = sessionId ?? string.Empty,
                SessionUsed = sessionUsed,
                SessionRemaining = Math.Max(0, DebugLimits.MaxHandlesPerSession - sessionUsed),
                MaxHandlesPerSession = DebugLimits.MaxHandlesPerSession,
                GlobalUsed = globalUsed,
                GlobalRemaining = Math.Max(0, DebugLimits.MaxHandlesGlobal - globalUsed),
                MaxHandlesGlobal = DebugLimits.MaxHandlesGlobal
            };
        }

        /// <summary>句柄双限的唯一判定规则：先达到者生效；调用方必须已持有 Sync。</summary>
        private static string EvaluateBudgetLocked(string sessionId, out int sessionUsed, out int globalUsed)
        {
            sessionUsed = CountForSessionLocked(sessionId);
            globalUsed = Handles.Count;
            if (sessionUsed >= DebugLimits.MaxHandlesPerSession) return "session-handle-limit";
            if (globalUsed >= DebugLimits.MaxHandlesGlobal) return "global-handle-limit";
            return null;
        }

        /// <summary>清理已经不可能再解析的弱句柄；强句柄与已换 runtime/generation 的记录保留作为证据。</summary>
        private static void PruneCollectedLocked()
        {
            var stale = new List<string>();
            foreach (var pair in Handles)
            {
                var record = pair.Value;
                if (record.Strong || record.Weak == null) continue;
                var target = record.Weak.Target;
                if (target == null)
                {
                    stale.Add(pair.Key);
                    continue;
                }
                if (IsDestroyedUnityObject(target)) stale.Add(pair.Key);
            }
            foreach (var key in stale) Handles.Remove(key);
        }

        /// <summary>
        /// 判定已销毁的 Unity 对象：托管包装仍然存在，但 Unity 重载的 == 把已销毁对象判为 null。
        /// 必须先用 ReferenceEquals 判断包装是否存在；若先写 unityObject != null，该表达式本身就走 Unity 重载的 !=，
        /// 对已销毁对象返回 false，整个判定永不成立。
        /// </summary>
        private static bool IsDestroyedUnityObject(object candidate)
        {
            var unityObject = candidate as UnityEngine.Object;
            return !ReferenceEquals(unityObject, null) && unityObject == null;
        }

        private static int CountForSessionLocked(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return 0;
            var count = 0;
            foreach (var record in Handles.Values)
            {
                if (string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)) count++;
            }
            return count;
        }

        private static DebugHandleView Project(HandleRecord record, string state, string staleReason)
        {
            return new DebugHandleView
            {
                HandleId = record.HandleId,
                SessionId = record.SessionId,
                TypeName = record.TypeName,
                Strong = record.Strong,
                State = state,
                StaleReason = staleReason ?? string.Empty,
                RuntimeId = record.RuntimeId,
                DomainGeneration = record.DomainGeneration,
                CreatedAtUtc = record.CreatedAtUtc,
                CreatedAtMonotonicMs = record.CreatedAtMonotonicMs,
                LifetimeNote = record.Strong
                    ? "Strong handle: it keeps the target alive while the handle exists, so it is a recorded intervention that changes object lifetime."
                    : "Weak handle: it never keeps the target alive; after collection or destruction it reads stale instead of null."
            };
        }

        private static string TypeName(object target)
        {
            var type = target.GetType();
            return type.FullName ?? type.Name;
        }
    }
}
#endif
