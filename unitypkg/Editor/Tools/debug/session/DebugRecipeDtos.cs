#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    // ---- session.export ----

    /// <summary>导出请求；省略 cursor 表示冻结当前 publication 的第一页。</summary>
    public sealed class DebugSessionExportRequest
    {
        [UnityCliRequired] public string SessionId { get; set; }

        /// <summary>同一快照的后续页 cursor；省略时建立（并替换）该 session 的导出快照。</summary>
        public string Cursor { get; set; }

        /// <summary>一页的字节数；省略时使用已声明的默认值。</summary>
        [UnityCliRange(1, DebugLimits.RecipePageMaxBytes)] public int? PageBytes { get; set; }
    }

    /// <summary>导出结果；fragment 始终存在，complete 为 true 时它是完整 recipe 文本。</summary>
    public sealed class DebugSessionExportResult
    {
        public string SessionId { get; set; }
        public string SnapshotId { get; set; }
        public long Publication { get; set; }
        public long Offset { get; set; }
        public long TotalBytes { get; set; }
        public int PageBytes { get; set; }
        public bool Complete { get; set; }
        public bool HasMore { get; set; }

        /// <summary>下一页 cursor；complete 时为空。</summary>
        public string Cursor { get; set; }

        /// <summary>本页的 recipe 文本片段；按 offset 顺序拼接后的完整文本即 recipe 文档。</summary>
        public string Fragment { get; set; }

        /// <summary>
        /// complete 时为已解析的 recipe 文档；分页未完成时为空。
        /// 文档形状属于 recipe 格式本身，且 fragment 才是权威文本（contentSha256 也对文档重算），
        /// 因此这里有意承载任意 JSON：把它类型化成固定 DTO 会在协议里出现第二份 recipe 形状 owner。
        /// </summary>
        [UnityCliAnyJson] public Dictionary<string, object> Document { get; set; }

        /// <summary>完整导出时对文档重新计算的 checksum，供调用方核对保存的文件未被改写。</summary>
        public string ContentSha256 { get; set; }

        /// <summary>recipe 收录的配置分节。</summary>
        public string IncludedSections { get; set; }

        /// <summary>recipe 明确排除的活动事实。</summary>
        public string ExcludedSections { get; set; }

        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public string RuntimeId { get; set; }
        public long MaxRecipeBytes { get; set; }
        public string Note { get; set; }
    }

    // ---- session.import ----

    /// <summary>导入请求；label 省略时使用 recipe 里的 label（冲突时明确失败，不静默改名）。</summary>
    public sealed class DebugSessionImportRequest
    {
        [UnityCliRequired] public string File { get; set; }

        public string Label { get; set; }
        [UnityCliStringEnum("editor", "play", "editor-and-play")] public string ActivationScope { get; set; }
    }

    /// <summary>导入结果；它只证明建立了未激活定义，不证明任何业务行为发生。</summary>
    public sealed class DebugSessionImportResult
    {
        public string SessionId { get; set; }
        public string Label { get; set; }
        public string ActivationScope { get; set; }
        public string ClockKind { get; set; }
        public string State { get; set; }

        /// <summary>该定义由 recipe 建立且当前没有任何活动资源。</summary>
        public bool RestoredWithoutActivation { get; set; }
        public long ActiveResourceCount { get; set; }
        public long ProgramDefinitions { get; set; }
        public long HookDefinitions { get; set; }
        public long CheckDeclarations { get; set; }
        public List<string> ClockNames { get; set; }
        public List<string> ProgramIds { get; set; }
        public List<string> HookIds { get; set; }
        public string RecipeFile { get; set; }
        public string RecipeChecksum { get; set; }
        public long RecipeBytes { get; set; }

        /// <summary>import 没有重放路径，因此该列表恒为空；它如实表达“没有操作被重放”。</summary>
        public List<string> ReplayedOperations { get; set; }

        /// <summary>本次导入已经加载而不可回收的候选程序集数量与字节；失败与成功都报告。</summary>
        public long LoadedCandidateAssemblyCount { get; set; }
        public long LoadedCandidateAssemblyBytes { get; set; }

        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public string RuntimeId { get; set; }
        public long StorePublication { get; set; }
        public string Note { get; set; }
    }
}
#endif
