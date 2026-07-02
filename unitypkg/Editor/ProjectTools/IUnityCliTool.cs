#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// 自定义工具声明形态(泛型接口)。实现本接口即声明一个工具,废除 [UnityCliTool] attribute。
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
        public string Description { get; set; }
        public string Category { get; set; }
        public string Version { get; set; }
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
}
#endif
