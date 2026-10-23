#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.run-compare — 按调用方声明的来源、对齐键、值路径与时钟比较两个保留窗口，
    /// 只在两侧真实证据能证明时报告首个差异，缺证据时返回 indeterminate。
    /// 它是纯读取：不封闭窗口、不改变判定、不写文件、不注册回调。
    /// </summary>
    public class DebugRunCompareTool : IUnityCliTool<DebugRunCompareRequest, DebugRunCompareResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.run-compare",
            Summary = "Compare two retained evidence windows on a caller-declared source, alignment key, compared value path and clock, and report the first provable difference or why it cannot be proven.",
            Description = "Compare two evidence windows of the same domain generation and answer whether their retained traces are equal, diverged or indeterminate for one declared selection. Nothing is assumed: source (program, hook, log, trigger or pause), position (an exact executionPoint or \"*\"), alignment (ordinal, or key with keyPath), valuePath and clock (monotonic, editor-tick, unity-frame or project-logical) are all required. equal means both windows are terminal and complete for the selection and every aligned value is identical; diverged means a real retained event on each side proves a difference; indeterminate means the retained evidence proves neither, and an event present on one side only is reported as unprovenDifference rather than a difference. An open window answers indeterminate with the window-open reason. Output: result, reasons with stable codes, the echoed selection, firstDifference with kind and both values, unprovenDifference, contexts around the difference, conditionDifferences, and both side views with evidenceState. Reads live evidence only and reports achieved coverage. Rejections: E_DEBUG_RUN_NOT_FOUND, E_SCHEMA_INVALID.", Notes = DebugDomainNotes.RunRead,
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
                    Description = "Compare two sealed windows by business key on the program check value, using the monotonic clock, and read up to two events of context per side.",
                    Input = new DebugRunCompareRequest
                    {
                        LeftRunId = "run-1",
                        RightRunId = "run-2",
                        Source = "program",
                        Position = "*",
                        Alignment = new DebugRunCompareAlignmentSpec { Kind = "ordinal" },
                        ValuePath = "value.actual",
                        Clock = "monotonic",
                        Context = 2
                    }
                },
                new ToolExample
                {
                    Description = "Align two windows by the name of each program check and compare the passed flag they published.",
                    Input = new DebugRunCompareRequest
                    {
                        LeftRunId = "run-3",
                        RightRunId = "run-4",
                        Source = "program",
                        Position = "editor-tick",
                        Kind = "check",
                        Alignment = new DebugRunCompareAlignmentSpec { Kind = "key", KeyPath = "name" },
                        ValuePath = "value.passed",
                        Clock = "monotonic"
                    }
                }
            }
        };

        public ValueTask<DebugRunCompareResult> ExecuteAsync(DebugRunCompareRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            return new ValueTask<DebugRunCompareResult>(DebugRunHost.Compare(request));
        }
    }
}
#endif
