#if UNITY_EDITOR
using System;
using System.Globalization;

namespace UnityCli
{
    /// <summary>
    /// method-hook 能力唯一的资源上限与预算声明点。
    ///
    /// 调整 hook 行为时只改这里：实现、schema 范围、describe 文本与定向测试都从本类读取，
    /// 不允许在 Tool、host、store 或后端里再写一份数量常量。
    /// 单位为字节、条数、毫秒或秒，均由字段名说明；全部为编译期常量，避免运行时配置漂移。
    /// </summary>
    internal static class MethodHookLimits
    {
        // ---- 定义与目标 ----

        /// <summary>同一 Editor Session 内允许保留的 hook 定义总数。</summary>
        internal const int MaxDefinitionsPerSession = 128;

        /// <summary>同一 exact target 上允许的逻辑定义数（共用一套 Adapter）。</summary>
        internal const int MaxDefinitionsPerTarget = 16;

        /// <summary>单个定义的 Prefix 或 Postfix 源码上限（UTF-8 字节）。</summary>
        internal const int MaxCallbackSourceBytes = 128 * 1024;

        /// <summary>单个定义允许捕获的 exact 字段数（实例字段与静态字段合计）。</summary>
        internal const int MaxCaptureFields = 64;

        /// <summary>采样步长允许的最大值，避免用一个巨大的步长表达"永不采样"。</summary>
        internal const int MaxSampleEvery = 1000000;

        // ---- 动态回调编译产物 ----

        /// <summary>当前域曾加载过的独特回调编译产物数量上限。</summary>
        internal const int MaxCompiledArtifacts = 64;

        /// <summary>当前域累计加载的回调程序集字节上限。</summary>
        internal const long MaxCompiledArtifactBytes = 128L * 1024 * 1024;

        /// <summary>单个回调编译产物字节上限。</summary>
        internal const long MaxSingleArtifactBytes = 8L * 1024 * 1024;

        /// <summary>当前域允许建立的独特 Adapter 返回类型身份上限。</summary>
        internal const int MaxAdapterIdentities = 256;

        // ---- 回调 State ----

        /// <summary>单个 hook 的回调 State JSON 字节上限；跨 Play 清空。</summary>
        internal const int MaxStateBytes = 32 * 1024;

        /// <summary>State 值投影允许的最大深度。</summary>
        internal const int MaxStateDepth = 16;

        /// <summary>State 单个容器允许的最大条目数。</summary>
        internal const int MaxStateContainerItems = 128;

        // ---- 事件环 ----

        /// <summary>事件环允许保留的最大事件条数。</summary>
        internal const int MaxEventEntries = 16384;

        /// <summary>事件环允许保留的最大估算字节数。</summary>
        internal const long MaxEventRingBytes = 16L * 1024 * 1024;

        /// <summary>单个事件序列化后的字节上限，超出即按边界截断。</summary>
        internal const int MaxEventBytes = 32 * 1024;

        /// <summary>单次 Emit 业务 payload 的字节上限。</summary>
        internal const int MaxEmitBytes = 8 * 1024;

        /// <summary>事件历史缺口记录条数上限；缺口只描述不可读区间，不声称调用未发生。</summary>
        internal const int MaxHistoryGaps = 32;

        // ---- 投影边界 ----

        /// <summary>参数、结果、字段与 Emit 值投影允许的最大深度。</summary>
        internal const int MaxProjectionDepth = 8;

        /// <summary>投影中单个集合允许的最大条目数。</summary>
        internal const int MaxProjectionItems = 128;

        /// <summary>投影中单个字符串允许的最大 UTF-8 字节数。</summary>
        internal const int MaxProjectionStringBytes = 4 * 1024;

        // ---- 公开分页与快照 ----

        /// <summary>method-hook.list 默认页大小。</summary>
        internal const int ListDefaultLimit = 32;

        /// <summary>method-hook.list 允许的最大页大小。</summary>
        internal const int ListMaxLimit = 128;

        /// <summary>method-hook.events 默认页大小（冻结值，Dev 实测在框架 read budget 内可完整返回）。</summary>
        internal const int EventsDefaultLimit = 100;

        /// <summary>
        /// method-hook.events 允许的最大页大小。
        /// 由 Dev 实测的框架 read budget 绝对上限（getters=10000）决定：同一 terminal 在预算内被序列化 3 次，
        /// 单个事件的 22 个 member 约计 66 getters，实测 150 条可返回、200 条被 getters 预算拒绝，
        /// 因此取 128 保留安全余量。项目所有者已就该实测边界裁定冻结为默认 100 / 上限 128，
        /// 计划第 14 节、describe 与 schema 范围同步使用本类常量。
        /// </summary>
        internal const int EventsMaxLimit = 128;

        /// <summary>
        /// MethodHookEventItem 的可序列化 member 数量。
        /// 事件页声明的 read budget 由它推导，因此 UnityCliMethodHookTests 会用反射验证该常量与实际 DTO 一致。
        /// </summary>
        internal const int EventItemMembers = 22;

        /// <summary>method-hook.list 单个条目的可序列化 member 数量。</summary>
        internal const int ListItemMembers = 9;

        /// <summary>method-hook.info 单个回调源码分页的字节上限。</summary>
        internal const int MaxInfoSourcePageBytes = 32 * 1024;

        /// <summary>定义快照文件允许的最大字节数。</summary>
        internal const int MaxSnapshotBytes = 8 * 1024 * 1024;

        /// <summary>
        /// 事件 checkpoint 允许的字节预算。事件环上限（16 MiB）高于总快照上限（8 MiB），
        /// 因此落盘时必须从最旧端有界截断，而不是让整个 checkpoint 失败；截断区间作为 history gap 记录。
        /// </summary>
        internal const long MaxCheckpointEventBytes = 6L * 1024 * 1024;

        // ---- 生命周期 ----

        /// <summary>每个 domain generation 总计的恢复预算，不按 hook 续期。</summary>
        internal const int RecoveryBudgetSeconds = 120;

        /// <summary>回调正常返回但超过该毫秒数时记录 overrun 并使定义 faulted（事后保护，非抢占式超时）。</summary>
        internal const int CallbackOverrunMs = 10;

        /// <summary>跨 Domain Reload 的恢复抑制集合条目上限。</summary>
        internal const int MaxSuppressionEntries = 128;

        /// <summary>info 返回的 foreign patch owner 摘要条目上限。</summary>
        internal const int MaxForeignOwnerEntries = 32;

        // ---- 读取型调用的单次读取预算 ----

        /// <summary>
        /// 一次事件页可能需要的最小 read budget。
        /// 同一 terminal 至少被有预算地序列化两次，因此按 2 倍 member 计费再加固定余量。
        /// </summary>
        internal static long EventPageGetters(int events)
        {
            return 2L * EventItemMembers * Math.Max(1, events) + 256L;
        }

        /// <summary>一次定义页可能需要的最小 read budget。</summary>
        internal static long ListPageGetters(int items)
        {
            return 2L * ListItemMembers * Math.Max(1, items) + 256L;
        }

        /// <summary>供 describe 复用的稳定上限摘要，公开文本与实现共用同一来源。</summary>
        internal static string Summary()
        {
            return "Limits: " + MaxDefinitionsPerSession.ToString(CultureInfo.InvariantCulture) + " definitions ("
                + MaxDefinitionsPerTarget.ToString(CultureInfo.InvariantCulture) + " per target), "
                + (MaxCallbackSourceBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB per callback source, "
                + MaxCompiledArtifacts.ToString(CultureInfo.InvariantCulture) + " compiled callbacks and "
                + (MaxCompiledArtifactBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB per domain, "
                + MaxEventEntries.ToString(CultureInfo.InvariantCulture) + " events / "
                + (MaxEventRingBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB, "
                + (MaxStateBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB state per hook, "
                + RecoveryBudgetSeconds.ToString(CultureInfo.InvariantCulture) + " s restore budget per domain generation.";
        }
    }
}
#endif
