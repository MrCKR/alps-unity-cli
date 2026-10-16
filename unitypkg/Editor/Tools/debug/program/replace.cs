#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.program.replace — 原子替换一个程序的源码、entry ABI、bindings 与 revision 状态。
    /// 顺序固定：revision/预算检查 → 编译候选 → ABI 与全部 binding 校验 → 新目标 attachment 准备
    /// → CAS 发布不可变 revision → 旧目标与引用在在途调用结束后释放。
    /// </summary>
    public class DebugProgramReplaceTool : IUnityCliTool<DebugProgramReplaceRequest, DebugProgramReplaceResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.program.replace",
            Summary = "Atomically replace one debug program revision, keeping the previous working configuration on any failure.",
            Description = "Publish a new immutable revision for an existing program. Inputs: programId, a required expectedRevision, an optional label (the stored label is kept when omitted), exactly one of source or file, entries, optional bindings and the optional candidate revision state with its schema. The fixed order is: expectedRevision and budget check, candidate compilation, ABI and full binding validation, preparation of the new target attachments, a compare-and-swap publication of the immutable revision, and only then the release of old targets and references after the calls that were already in flight have ended. Any compile, ABI, reference, signature, budget or CAS failure keeps the previous working configuration and only records the refusal as a fact, so a partially replaced handler set can never be observed. State handover: invocation temporaries, the program revision state and shared session JSON are different things. A candidate revision state is published only as a JSON snapshot that passed its schema and byte checks, the state epoch is verified before publication, and an epoch conflict keeps the old configuration. Calls already in flight keep using the old revision and its state, and their writes are never auto-merged into the new state. Closures, delegates, Thread, Task and object references are never moved between revisions; long-lived values belong in the explicit session state instead. Output: old and new revision, the publication status, the new state epoch and the number of calls that were still in flight when the new revision was published. Rejections: E_DEBUG_PROGRAM_NOT_FOUND, E_DEBUG_PROGRAM_REVISION_CONFLICT when expectedRevision no longer matches, E_DEBUG_PROGRAM_STATE_CONFLICT when the state epoch moved, E_DEBUG_PROGRAM_WRONG_STATE for a stopped program, E_DEBUG_PROGRAM_ABI_INVALID, E_DEBUG_PROGRAM_BINDING_INVALID, E_HOOK_COMPILE_BUDGET_EXHAUSTED, E_DEBUG_LIMIT_EXCEEDED, E_CODE_COMPILE_FAILED and E_DEBUG_PROGRAM_SOURCE_UNREADABLE. Live handle identities from an unchanged runtime may keep being owned by the session; a revision never stores its own strong copy. Entry ABI: " + UnityCliDebugProgramAbi.SignatureTable() + ". " + UnityCliDebugProgramAbi.StepContextAbi() + " A method callback entry may change arguments and skip the original on prefix, change the result on postfix, and never suppresses an exception: the original exception keeps propagating. A direct debug.hook.* call must not change a program-owned revision; change program-owned bindings through this Tool or through debug.program.stop. " + DebugLimits.ProgramSummary(),
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
