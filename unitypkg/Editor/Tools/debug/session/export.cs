#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.session.export — 只读导出当前不可变 recipe snapshot，按字节分页返回。
    /// 不产生 artifact、不写文件、不改变 Unity 或 session 状态。
    /// </summary>
    public class DebugSessionExportTool : IUnityCliTool<DebugSessionExportRequest, DebugSessionExportResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.session.export",
            Summary = "Read the current immutable recipe snapshot of one debug session as bounded pages, without writing any artifact or file.",
            Description = "Read a reusable recipe of one debug session. Inputs: sessionId (required), an optional cursor from a previous page, and an optional pageBytes. Without cursor the call freezes the session's current publication and returns page one; with cursor it returns the next page of the SAME snapshot, so different reads can never be stitched into a file that mixes several publications. Recipe content: the session definition with its selected JSON state and clock kind, every program with its source text, source hash, entries and bindings, every direct hook definition of the session with its exact target, capture configuration, callback source, listener and breakpoint configuration, the declared check definitions of that session and the named logical clocks. Excluded content: live pause sites, object handles, Thread/Task/delegate objects, invocation records and unfinished commands. Export never emits those and refuses a stored recipe that carries them, because they are mechanism state and cannot be reused as configuration. A listener's live thread identity is the same class of mechanism state: export leaves it out instead of writing a thread id that the importing Editor startup could never mean, so an imported listener keeps its declared trigger, conditions and hit budget and matches any managed thread until the caller declares a new thread filter. The source paths of programs are auxiliary only: the actual source text is always included, so a recipe can be reused without the original files. Output: fragment is the JSON text slice of this page, offset/totalBytes/pageBytes describe the slice, complete is true when the fragment is the whole recipe document (then document also carries the parsed recipe), cursor is the next page cursor, and contentSha256 is recomputed over the complete document so the caller can verify a saved file later. Limits: " + DebugLimits.RecipeSummary() + " Side effects: none. This tool reads the in-memory publication, writes no file and creates no artifact; the caller owns any file it saves. Error codes: E_DEBUG_SESSION_NOT_FOUND when the session does not exist in this Editor session, E_DEBUG_EXPORT_CURSOR_EXPIRED when the cursor belongs to a replaced snapshot or is out of range, E_DEBUG_LIMIT_EXCEEDED when the recipe exceeds the single-recipe byte budget, and E_SCHEMA_INVALID for an out-of-range pageBytes. Lifecycle: reading is pure observation, so it needs no recovery. A new export of the same session replaces its snapshot and invalidates older cursors; a Domain Reload ends the previous generation, so the retained snapshot is gone and old cursors report expired; exiting Play invalidates Play-scoped live state but not a recipe already read out; an Editor restart makes all old mechanism state invalid, and the exported text is the only thing the caller keeps.",
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
