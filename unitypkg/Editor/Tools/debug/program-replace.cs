#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.program-replace — 原子替换一个程序的源码、entry ABI、bindings 与 revision 状态。
    /// 顺序固定：revision/预算检查 → 编译候选 → ABI 与全部 binding 校验 → 新目标 attachment 准备
    /// → CAS 发布不可变 revision → 旧目标与引用在在途调用结束后释放。
    /// </summary>
    public class DebugProgramReplaceTool : IUnityCliTool<DebugProgramReplaceRequest, DebugProgramReplaceResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.program-replace",
            Summary = "Atomically replace one debug program revision, keeping the previous working configuration on any failure.",
            Description = "Publish a new immutable revision for an existing program. Inputs: programId, required expectedRevision, optional label, exactly one of source or file, entries, optional bindings, and the optional candidate revision state with its schema. Fixed order: expectedRevision and budget check, candidate compilation, ABI and full binding validation, preparation of new target attachments, compare-and-swap publication, and only then release of old targets and references after in-flight calls ended. Any compile, ABI, reference, signature, budget or CAS failure keeps the previous working configuration and records the refusal as a fact, so a partially replaced handler set is never observable. Calls already in flight keep the old revision and its state, and their writes are never auto-merged. Closures, delegates, Thread, Task and object references are never moved between revisions. Output: old and new revision, publication status, new state epoch and how many calls were still in flight. Rejections: E_DEBUG_PROGRAM_REVISION_CONFLICT, E_DEBUG_PROGRAM_STATE_CONFLICT, E_DEBUG_PROGRAM_ABI_INVALID or E_CODE_COMPILE_FAILED.", Notes = DebugDomainNotes.ProgramManage,
            Group = "debug",
            DestructiveHint = true,
            SideEffects = new[] { "debug-session", "artifact-write" },
            Permissions = new[] { "execute-user-code" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 120000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Replace the source and state of an installed program at its current revision.",
                    Input = new DebugProgramReplaceRequest
                    {
                        ProgramId = "prog-1",
                        ExpectedRevision = 1,
                        Source = "using UnityCli;\npublic static class Probe\n{\n    public static UnityCliDebugStepResult Step(UnityCliDebugStepContext context) { return UnityCliDebugStepResult.Finish(); }\n}",
                        Entries = new System.Collections.Generic.List<DebugProgramEntrySpec>
                        {
                            new DebugProgramEntrySpec { Name = "Step", Category = "step", DeclaringType = "Probe" }
                        },
                        State = new System.Collections.Generic.Dictionary<string, object> { { "attempt", 1 } }
                    }
                }
            }
        };

        public async ValueTask<DebugProgramReplaceResult> ExecuteAsync(DebugProgramReplaceRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 候选编译、ABI 与 binding 准备全部在 effect barrier 之前完成；失败时旧配置未被触碰。
            var plan = await DebugProgramHost.PrepareReplaceAsync(request, ctx.Wait, TimeSpan.FromMilliseconds(ctx.TimeoutMs), ct);
            ctx.CommitEffect();
            return DebugProgramHost.ApplyReplace(plan);
        }
    }
}
#endif
