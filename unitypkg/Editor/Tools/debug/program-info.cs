#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.program-info — 读取一个程序定义的完整事实：源码 hash、revision、entry ABI、bindings、
    /// 活动运行、step driver 与占用。它不编译、不激活、不恢复任何东西。
    /// </summary>
    public class DebugProgramInfoTool : IUnityCliTool<DebugProgramInfoRequest, DebugProgramInfoResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.program-info",
            Summary = "Read one debug program definition: source hash, revision, entry ABI, bindings, active runs and occupancy without compiling.",
            Description = "Read the complete facts of one programId from the live owner. Input: programId (required). The result reports the source SHA-256 and byte size, the current revision and its state epoch, the declared state size and schema size, the resolved entry list with each entry's real signature, every binding with whether its detour is currently attached, the state of the definition (installed, active or stopped), all non-terminal runs with their step, check and delivery counts, the continuous step driver facts, the shared compile artifact occupancy and the program budget. This is a pure read: it does not compile, does not attach a detour, does not activate a listener, does not run cleanup, does not restore state and does not write any persistent file, so it is safe to call at any time, including while a run is in flight. In-flight facts: inFlightCalls counts the callbacks that are currently running, and a step driver that is waiting on a signal or condition reports what it is waiting for instead of pretending progress. Rejections: E_DEBUG_PROGRAM_NOT_FOUND when the current domain has no such definition; a definition created before the last Domain Reload is gone and must be re-installed. " + DebugLimits.ProgramSummary(),
            Group = "debug",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Read the current facts of one program.",
                    Input = new DebugProgramInfoRequest { ProgramId = "prog-1" }
                }
            }
        };

        public ValueTask<DebugProgramInfoResult> ExecuteAsync(DebugProgramInfoRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 纯读取：不跨效果屏障，不编译，不激活。
            return new ValueTask<DebugProgramInfoResult>(DebugProgramHost.Info(request));
        }
    }
}
#endif
