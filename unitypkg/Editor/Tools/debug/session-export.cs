#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.session-export — 只读导出当前不可变 recipe snapshot，按字节分页返回。
    /// 不产生 artifact、不写文件、不改变 Unity 或 session 状态。
    /// </summary>
    public class DebugSessionExportTool : IUnityCliTool<DebugSessionExportRequest, DebugSessionExportResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.session-export",
            Summary = "Read the current immutable recipe snapshot of one debug session as bounded pages, without writing any artifact or file.",
            Description = "Read a reusable recipe of one debug session as JSON text pages. Without cursor the call freezes the session's current publication and returns page one; with cursor it returns the next page of the same snapshot, so different reads can never be stitched into a file that mixes publications. The recipe carries the session definition with its selected JSON state and clock kind, every program with its source text, hash, entries and bindings, every direct hook with its exact target, capture configuration, callback source, listener and breakpoint configuration, the declared check definitions and the named logical clocks. It never includes live pause sites, object handles, Thread, Task or delegate objects, invocation records, unfinished commands or a listener's live thread identity. Output: fragment (this page's JSON slice), offset, totalBytes, pageBytes, complete, document when complete, cursor for the next page, and contentSha256 recomputed over the complete document. Reads the in-memory publication and writes no file. Rejections: E_DEBUG_SESSION_NOT_FOUND, E_DEBUG_EXPORT_CURSOR_EXPIRED, E_DEBUG_LIMIT_EXCEEDED, E_SCHEMA_INVALID.", Notes = DebugDomainNotes.SessionManage,
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new string[0],
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            SupportsDryRun = false,
            Examples = new[]
            {
                new ToolExample { Description = "Freeze the current publication and read the first page.", Input = new DebugSessionExportRequest { SessionId = "dbg-1" } },
                new ToolExample { Description = "Read the next page of the same snapshot.", Input = new DebugSessionExportRequest { SessionId = "dbg-1", Cursor = "dbg-1@3/1#524288" } }
            }
        };

        public ValueTask<DebugSessionExportResult> ExecuteAsync(DebugSessionExportRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 只读导出：没有 effect barrier，没有残余需要提交。
            var plan = DebugRecipeHost.PrepareExport(request);
            return new ValueTask<DebugSessionExportResult>(DebugRecipeHost.Export(plan));
        }
    }
}
#endif
