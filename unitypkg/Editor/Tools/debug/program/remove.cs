#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.program.remove — 删除一个已停止、没有活动运行的程序定义及其可释放路由。
    /// 仍然加载且无法卸载的程序集必须在结果里如实报告。
    /// </summary>
    public class DebugProgramRemoveTool : IUnityCliTool<DebugProgramRemoveRequest, DebugProgramRemoveResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.program.remove",
            Summary = "Delete a stopped debug program definition and its revocable routes, reporting assemblies that stay loaded.",
            Description = "Delete one program definition from the current domain. Input: programId (required). Preconditions: the program must not own an active run, a callback that is still in flight or an attached detour that was not prepared by this Tool; otherwise the call is refused with the current state, the active run count and the attached binding count instead of deleting a live owner. Effect: every binding of the definition is detached through the single hook host, the run resource slot is released, and the definition with its run records, resolved entries and prepared mount plans is deleted. Output: removed flag, how many bindings were detached, how many revocable routes were released, and the honest unload report. Unloading rule: assemblies already loaded in this domain cannot be unloaded by remove, so loadedNonUnloadableAssemblies and loadedNonUnloadableBytes report what stays resident and keeps counting against the shared compile budget; only a Domain Reload reclaims those bytes, and unloadable stays false until that happens. This Tool never touches another program, another session or the session owner itself. Rejections: E_DEBUG_PROGRAM_NOT_FOUND and E_DEBUG_PROGRAM_RUN_ACTIVE with the blocking facts. " + DebugLimits.ProgramSummary(),
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new[] { "debug-session" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Delete a stopped program definition after its run finished.",
                    Input = new DebugProgramRemoveRequest { ProgramId = "prog-1" }
                }
            }
        };

        public ValueTask<DebugProgramRemoveResult> ExecuteAsync(DebugProgramRemoveRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 前置判定在 effect barrier 之前完成；detach 与删除在 barrier 之后。
            var controller = DebugProgramHost.PrepareRemove(request);
            ctx.CommitEffect();
            return new ValueTask<DebugProgramRemoveResult>(DebugProgramHost.ApplyRemove(controller));
        }
    }
}
#endif
