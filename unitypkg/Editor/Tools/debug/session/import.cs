#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.session.import — 校验 recipe 后原子建立一个新的未激活 session。
    /// 只编译候选源码并登记未激活定义；不执行 startup/cleanup 代码，不重放操作。
    /// </summary>
    public class DebugSessionImportTool : IUnityCliTool<DebugSessionImportRequest, DebugSessionImportResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.session.import",
            Summary = "Validate a stored recipe and atomically establish a new inactive debug session without running startup or cleanup code.",
            Description = "Read a recipe file the caller saved from debug.session.export and establish a new inactive debug session from it. Inputs: file (required, an absolute path to a caller-managed recipe file outside business assets), an optional label that overrides the recipe label, and an optional activationScope. Validation happens before any effect and fails closed: the text must parse as a JSON object, format must be " + DebugRecipeStore.Format + ", the checksum must match the content, the declared projectPath must be this canonical project, the byte size must stay inside the single-recipe budget, and no excluded key (pause sites, object handles, Thread/Task/delegate objects, invocation records, unfinished commands) may appear. After validation the call compiles each declared program source through the single dynamic compiler owner, resolves each declared entry ABI, prepares each declared binding exactly like debug.program.install, registers each direct hook definition as a dormant definition whose declared session reference is re-established as the new session, so no reference to the exported session survives, then publishes the new session definition. A listener's live thread identity is never part of a recipe, so an imported listener keeps its declared trigger, conditions and hit budget and matches any managed thread until the caller declares a new thread filter. A dormant hook is not attached and its listener is not armed; a prepared program is not run. Nothing from the recipe is replayed: no run is restarted, no operation from the exported session is re-issued, no startup or cleanup entry of any program is called, and no target is resolved for a dormant hook. Output: the new sessionId, the label and activationScope actually used, the clock kind, the number of registered program and hook definitions, the declared check count, the clock names, the recipe checksum and byte size, and the count and bytes of candidate assemblies already loaded in this domain. ReplayedOperations is always empty because this call has no replay path. Limits: " + DebugLimits.RecipeSummary() + " Side effects: writes the debug session store file and loads candidate assemblies. Import cannot unload those assemblies; they stay counted for this domain and are never reclaimed by stopping the session. Error codes: E_DEBUG_RECIPE_INVALID with reason content-invalid, format-unsupported, checksum-missing, checksum-mismatch, path-mismatch, over-budget, excluded-content, file-unreadable or binding-content-invalid, and its details always report the already loaded candidate assembly count and bytes; E_DEBUG_SESSION_LABEL_CONFLICT when the label is already used (the label is never silently rewritten, pass another one); E_DEBUG_SESSION_SCOPE_CONFLICT when the activation scope cannot exist in the current editor mode; E_DEBUG_LIMIT_EXCEEDED for session, program or source budgets; E_DEBUG_PROGRAM_ABI_INVALID, E_DEBUG_PROGRAM_BINDING_INVALID, E_CODE_COMPILE_FAILED and E_HOOK_COMPILE_BUDGET_EXHAUSTED from the reused install path. A failure after the new definition was established rolls that definition, its new program definitions and its new dormant hook definitions back, so a failed import never leaves a half-activatable session. Lifecycle: same Editor start with a Domain Reload keeps definitions, source, selected JSON, in-budget evidence and terminal summaries; the reload interrupts active runs, releases input, cancels waits, releases pauses and invalidates delegates and handles, then rebinds precisely, and session-owned execution resources stay inactive until the next explicit run or enable. Import itself is the only path that establishes a session after an Editor restart, because all old mechanism state is invalid then. Exiting Play invalidates the Play run, Play handles, input and pause sites and keeps the investigation definitions, source, session JSON and sealed evidence. This call never executes business code, so what it proves is exactly that the definitions exist and the sources compiled; it does not prove that any business goal is reachable.",
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new[] { "debug-session", "artifact-write" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 120000,
            MaxTimeoutMs = 600000,
            SupportsDryRun = false,
            Examples = new[]
            {
                new ToolExample { Description = "Import a saved recipe into a new inactive session.", Input = new DebugSessionImportRequest { File = "C:/work/recipes/attack-loop.json" } },
                new ToolExample { Description = "Import the same recipe again under a distinct label.", Input = new DebugSessionImportRequest { File = "C:/work/recipes/attack-loop.json", Label = "attack-loop-second", ActivationScope = "editor-and-play" } }
            }
        };

        public async ValueTask<DebugSessionImportResult> ExecuteAsync(DebugSessionImportRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 内容/格式/checksum/路径/预算校验与 session 预算判定全部在 effect 之前完成；
            // 跨过 effect barrier 之后才开始编译候选源码与登记未激活定义。
            var plan = DebugRecipeHost.PrepareImport(request);
            ctx.CommitEffect();
            return await DebugRecipeHost.ApplyImportAsync(plan, ctx.Wait, TimeSpan.FromMilliseconds(ctx.TimeoutMs), ct);
        }
    }
}
#endif
