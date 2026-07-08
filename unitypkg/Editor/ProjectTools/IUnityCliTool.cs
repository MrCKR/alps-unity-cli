#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// 自定义工具声明形态(泛型接口)。实现本接口即声明一个工具,旧 attribute 形态已废除。
    ///
    /// 设计依据:.plan/自定义工具定义规范.md、.plan/工具统一形态.md
    /// - 协议类型(Request/Result)嵌套工具类内,工具自包含
    /// - desc 用 Describe() 方法(只声明人写字段,schema 由框架从 TReq/TRes 派生,Describe 无法染指)
    /// - handler 用 Execute(TReq): TRes,结构体进结构体出,机制消灭手拼
    /// - 报错抛 BridgeCommandException(阶段0 止血契约),框架 catch 转 ToolError
    /// </summary>
    public interface IUnityCliTool<TReq, TRes>
    {
        /// <summary>人写字段 desc(name/summary/description/group/examples/风险)。不含 schema。框架额外从 TReq/TRes 派生 inputSchema/outputSchema。</summary>
        ToolDescriptor Describe();

        /// <summary>handler。结构体进结构体出。成功返回 Result,报错抛 BridgeCommandException。
        /// ctx 提供 requestId/projectPath/artifactDir/logPath(与旧 UnityCliProjectToolContext 对齐)。</summary>
        TRes Execute(TReq request, UnityCliProjectToolContext ctx);
    }

    /// <summary>
    /// 可选子接口:跨帧异步工具实现此接口。不实现的走同步 Execute。
    /// TryDefer 返回 true 时框架挂起请求(EditorApplication.update 轮询 IsComplete),
    /// 条件满足后调 OnComplete 得最终 Result;超时则返回结构化错误。
    /// 设计依据:.plan/重构设计总文档.md "异步工具:deferred 子接口" 节(废除特殊处理,统一 deferred)。
    /// </summary>
    public interface IUnityCliDeferredTool<TReq, TRes> : IUnityCliTool<TReq, TRes>
    {
        /// <summary>
        /// 尝试挂起为跨帧异步执行。返回 true 时框架用 completion 轮询;返回 false 走同步 Execute。
        /// ctx 与 Execute 共享;completion 的 IsComplete/OnComplete 必须非空,否则框架抛 E_SCHEMA_INVALID。
        /// </summary>
        bool TryDefer(TReq request, UnityCliProjectToolContext ctx, out DeferredCompletion<TRes> completion);
    }

    /// <summary>
    /// deferred 工具的完成句柄。TryDefer 返回 true 时填充,框架据此轮询并完成请求。
    /// C# 9.0(Unity 2022.3)无 required 关键字,IsComplete/OnComplete 非空由框架校验。
    /// </summary>
    public sealed class DeferredCompletion<TRes>
    {
        /// <summary>每帧轮询,返回 true 则完成。可携带每帧副作用(如 BeginSample/EndSample)。必须非空。</summary>
        public Func<bool> IsComplete;

        /// <summary>轮询超时(ms),超时返回 TimeoutErrorCode 错误。</summary>
        public int TimeoutMs = 30000;

        /// <summary>条件满足时调,返回最终 Result。必须非空。抛 BridgeCommandException 会被框架捕获转结构化错误。</summary>
        public Func<TRes> OnComplete;

        public string TimeoutErrorCode = "E_EXECUTION_TIMEOUT";
        public string TimeoutErrorMessage = "Deferred tool timed out.";
        public string TimeoutNextAction = "Retry or reduce scope.";
    }

    /// <summary>
    /// 自定义 Context 声明形态(泛型接口)。只读观察,无 Execute,用 Read。
    /// 设计依据:.plan/Context形态.md(Context = Tool 减法:Read 代 Execute,去写入字段,加 refreshBehavior)
    /// </summary>
    public interface IUnityCliContext<TReq, TRes>
    {
        ContextDescriptor Describe();

        /// <summary>只读 handler。成功返回 Result,报错抛 BridgeCommandException。</summary>
        TRes Read(TReq request, UnityCliProjectToolContext ctx);
    }

    /// <summary>
    /// 内置 Context 的窄 raw 读取扩展。仅 Bridge 内置注册路径使用，用于保留 deferred marker 等旧 Bridge 控制流。
    /// 项目自定义 Context 不走此接口，仍只能通过 IUnityCliContext&lt;TReq,TRes&gt; 返回只读 DTO。
    /// </summary>
    internal interface IUnityCliBuiltInRawBridgeContext
    {
        string ReadRaw(UnityCliBridge.BridgeRequest request, UnityCliProjectToolContext ctx);
    }

    /// <summary>
    /// 工具 desc 的人写字段(不含 schema,schema 由框架从 TReq/TRes 派生)。
    /// 字段集对齐 AGENTS.md descriptor 质量门槛与 TS ToolMetadata。
    /// </summary>
    public sealed class ToolDescriptor
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Group { get; set; }
        public string Version { get; set; }
        public bool DestructiveHint { get; set; }
        public string[] SideEffects { get; set; }
        public string Target { get; set; }              // editor/runtime/both
        public string PlayModeSupport { get; set; }     // supported/runtime-only/session-affecting/editor-only/unknown
        public string CapabilityType { get; set; }      // project-write 等
        public bool SupportsDryRun { get; set; }
        public int DefaultTimeoutMs { get; set; }
        public int MaxTimeoutMs { get; set; }
        public bool SupportsJob { get; set; }
        public string[] ArtifactKinds { get; set; }
        public string[] Permissions { get; set; }
        public string[] RequiresPackages { get; set; }
        public ToolExample[] Examples { get; set; }
        public string Fingerprint { get; set; }         // 可选固定指纹,篡改检测
    }

    /// <summary>
    /// Context desc 的人写字段。比 ToolDescriptor 去掉写入相关(DestructiveHint/SideEffects/SupportsDryRun),加 RefreshBehavior。
    /// </summary>
    public sealed class ContextDescriptor
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Version { get; set; }
        public string Source { get; set; }                // built-in/project;空值按 project 处理
        public string Target { get; set; }
        public string AvailabilityScope { get; set; }
        public string PlayModeSupport { get; set; }
        public string RefreshBehavior { get; set; }      // static/on-demand/live
        public string[] RequiresPackages { get; set; }
        public string Fingerprint { get; set; }
    }

    /// <summary>调用示例。Input 是工具的 Request DTO 实例(强类型,框架序列化展示)。</summary>
    public sealed class ToolExample
    {
        public string Description { get; set; }
        public object Input { get; set; }   // 运行时为 TReq 实例;框架用 UnityCliSerializer.Serialize 展示
    }

    /// <summary>
    /// 标记 Request/Result 属性为 schema 必填。
    /// Unity 2022.3 默认 C# 9.0,不支持 C# 11 `required` 关键字,改用本特性显式标记。
    /// 设计依据:.plan/自定义工具定义规范.md(required→schema 必填)。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class UnityCliRequiredAttribute : Attribute { }
}
#endif
