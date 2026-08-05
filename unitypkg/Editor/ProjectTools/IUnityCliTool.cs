#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// 工具统一为语言原生异步接口。实现本接口即声明一个工具。
    /// desc 只声明人写字段；schema 由框架从 TReq/TRes 派生。
    /// </summary>
    public interface IUnityCliTool<TReq, TRes>
    {
        /// <summary>人写字段 desc(name/summary/description/group/examples/风险)。不含 schema。</summary>
        ToolDescriptor Describe();

        /// <summary>
        /// 异步执行入口。大多数工具可同步返回 `new ValueTask&lt;TRes&gt;(result)`；
        /// 跨帧/回调工具通过 await ctx.Wait.* 完成。
        /// </summary>
        ValueTask<TRes> ExecuteAsync(TReq request, UnityCliProjectToolContext ctx, CancellationToken ct = default);
    }

    /// <summary>
    /// Context 统一为语言原生异步接口。只读观察能力使用 ReadAsync。
    /// </summary>
    public interface IUnityCliContext<TReq, TRes>
    {
        ContextDescriptor Describe();

        /// <summary>只读异步入口。成功返回 Result，报错抛 BridgeCommandException。</summary>
        ValueTask<TRes> ReadAsync(TReq request, UnityCliProjectToolContext ctx, CancellationToken ct = default);
    }

    /// <summary>只有显式实现本接口的纯只读 Context 才能承担 completionProbe 资源归因。</summary>
    public interface IUnityCliProofObserver<TReq>
    {
        UnityCliProofResourceClaim ResolveProofResource(TReq request);
    }

    /// <summary>proof observer 的内部 canonical resource claim；不会投影到公开成功响应。</summary>
    public sealed class UnityCliProofResourceClaim
    {
        public string ResourceKey { get; set; }
        public string Identity { get; set; }
        public long InstanceId { get; set; }
        public int SceneHandle { get; set; }
    }

    /// <summary>高风险同步动作的声明式完成条件。</summary>
    public sealed class UnityCliCompletionProbe
    {
        [UnityCliRequired]
        [UnityCliStringEnum("scene.gameobject", "scene.gameobject.component")]
        public string Context { get; set; }
        [UnityCliRequired, UnityCliAnyJson]
        public object Input { get; set; }
        [UnityCliRequired]
        public List<UnityCliCompletionAssertion> Assertions { get; set; }
    }

    /// <summary>单条 JSON Pointer 断言；before/after 都按 JSON 类型严格比较。</summary>
    public sealed class UnityCliCompletionAssertion
    {
        [UnityCliRequired]
        public string Path { get; set; }
        [UnityCliRequired]
        [UnityCliStringEnum("equals", "notEquals", "approx", "exists", "lengthEquals", "contains")]
        public string Operator { get; set; }
        [UnityCliPresenceRequired, UnityCliAnyJson]
        public object Before { get; set; }
        [UnityCliPresenceRequired, UnityCliAnyJson]
        public object After { get; set; }
        public double? Tolerance { get; set; }
    }

    /// <summary>成功时只公开调用方声明路径的 fresh after 值。</summary>
    public sealed class UnityCliCompletionProbeReadback
    {
        public List<UnityCliCompletionAssertionReadback> Assertions { get; set; }
    }

    public sealed class UnityCliCompletionAssertionReadback
    {
        public string Path { get; set; }
        [UnityCliAnyJson]
        public object Value { get; set; }
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
        public bool DestructiveHint { get; set; }
        public string[] SideEffects { get; set; }
        public string Target { get; set; }
        public string PlayModeSupport { get; set; }
        public bool SupportsDryRun { get; set; }
        public int DefaultTimeoutMs { get; set; }
        public int MaxTimeoutMs { get; set; }
        public string[] Permissions { get; set; }
        public string[] RequiresPackages { get; set; }
        public ToolExample[] Examples { get; set; }
        public string Fingerprint { get; set; }
        public ToolCompletionDescriptor Completion { get; set; }
    }

    /// <summary>
    /// project/runtime 自定义 Tool 必须显式声明完成语义；built-in 与 Extension 由生成 registry 提供同一契约。
    /// 这里只接受机器可分支字段，不以自由文本替代 completion driver。
    /// </summary>
    public sealed class ToolCompletionDescriptor
    {
        public string ExecutionClass { get; set; }
        public string CompletionPredicate { get; set; }
        public string ProofSource { get; set; }
        public string ReloadPolicy { get; set; }
        public string TimeoutError { get; set; }
        public string OutcomeUnknownPolicy { get; set; }
        public string CompletionDriverId { get; set; }
    }

    /// <summary>
    /// Context descriptor 的人写字段；只保留公开只读契约和内部指纹。
    /// </summary>
    public sealed class ContextDescriptor
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public string AvailabilityScope { get; set; }
        public string PlayModeSupport { get; set; }
        public string[] RequiresPackages { get; set; }
        public ContextExample[] Examples { get; set; }
        public string Fingerprint { get; set; }
    }

    /// <summary>调用示例的内部 typed input，公开投影只保留 argv。</summary>
    public sealed class ToolExample
    {
        public string Description { get; set; }
        public object Input { get; set; }
        public ToolExampleExecution Execution { get; set; }
    }

    public sealed class ToolExampleExecution
    {
        public bool? DryRun { get; set; }
        public int? TimeoutMs { get; set; }
    }

    /// <summary>Context 示例内部保留 typed input，公开投影只保留 argv。</summary>
    public sealed class ContextExample
    {
        public string Description { get; set; }
        public object Input { get; set; }
    }

    /// <summary>
    /// 标记 Request/Result 属性为 schema 必填。
    /// Unity 2022.3 默认 C# 9.0，不支持 C# 11 required 关键字，因此继续使用显式特性。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class UnityCliRequiredAttribute : Attribute { }

    /// <summary>字段必须出现，但允许 JSON null；用于把 null 作为业务断言值。</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class UnityCliPresenceRequiredAttribute : Attribute { }

    /// <summary>
    /// 显式声明字段承载任意 JSON。未标记的 object 或动态 object 集合会让 schema 派生失败。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class UnityCliAnyJsonAttribute : Attribute { }

    /// <summary>为 string 协议字段声明闭集 enum，serializer 仍按普通 string 读写。</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class UnityCliStringEnumAttribute : Attribute
    {
        public UnityCliStringEnumAttribute(params string[] values) { Values = values ?? Array.Empty<string>(); }
        public string[] Values { get; private set; }
    }
}
#endif
