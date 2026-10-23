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
            Description = "Compare two evidence windows of the same domain generation and answer whether their retained traces are equal, diverged or indeterminate for one declared selection. Nothing is assumed for you: the event source, the alignment key, the compared value path and the applicable clock are all required inputs, and the tool never falls back to global sequence numbers, object addresses or wall time. Inputs: leftRunId and rightRunId (required, both must exist in the current domain generation; a run compared with itself is allowed and answers equal); source (required: program, hook, log, trigger or pause, the real sourceId in the unified event store); position (required: the exact executionPoint that must be hit inside that source, or \"*\" for any position; read real values with debug.events-read); optional kind to restrict the source's event kind; alignment (required) with kind ordinal or key, where ordinal pairs the n-th selected event of the left side with the n-th of the right side inside this source, and key additionally requires keyPath, the event-field path whose value identifies the same business call on both sides and is reported as canonical JSON text (a string key therefore appears quoted); valuePath (required) is the event-field path whose value is compared; clock (required: monotonic, editor-tick, unity-frame or project-logical) selects which events are comparable, and events of this source that carry another clock are excluded and counted instead of being silently mixed; optional context (0.." + DebugLimits.MaxRunCompareContext + ", default " + DebugLimits.RunCompareDefaultContext + ") bounds the context reported around the difference. Field paths are dot-separated segments of letters, digits, underscore or hyphen over the published event fields (sequence, sourceId, kind, executionPoint, revision, invocationId, threadId, mainThread, clockKind, clockValue, frame, lastObservedMainFrame, hookId, playSessionId, clockSegment, elapsedMilliseconds, sampled, sampleEvery, callbackPending, instance, methodIdentity, arguments, result, fields, name, value, faultCode, faultMessage, truncated, projectionTruncated), at most " + DebugLimits.MaxRunComparePathSegments + " segments; a numeric segment indexes an array, and a nested difference is reported at the exact leaf path where it first appears. Result semantics: equal means both windows are terminal and complete for the declared selection and alignment and every aligned compared value is identical, which includes the honest case of an empty selection on both sides; diverged means a difference is proven by a real retained event on each side; indeterminate means the retained evidence cannot prove either, and absence of an event on one side is never used as evidence of divergence. What counts as a proven first difference: a compared value that differs between two events that are both retained at the same aligned position; both values are read from the event's published projection, and, for an alignment by call ordinal, both windows' intervals must be complete for the declared selection. An event present on one side and absent on the other, and any value on an event whose projection was truncated or whose clock was excluded, is reported as unprovenDifference instead, never as a difference. Two terminal windows whose selected sets are not fully readable (coverage gap, event not recorded, retained events evicted from the event ring, sampled stream, truncated projection, partial clock coverage, missing or duplicated alignment keys) can only answer indeterminate. A window that is still open cannot answer equal or diverged: the comparison is indeterminate with the window-open reason, and any difference seen so far is reported as unprovenDifference. Output: result (equal, diverged or indeterminate), reasons with stable codes, the echoed source, position, kind, valuePath, clock, alignmentKind and alignmentKeyPath, the applied context, alignedPairCount plus leftOnlyCount and rightOnlyCount, firstDifference with the difference kind (value, value-missing, event-missing-left, event-missing-right or count), the alignment index, the compared value path where the difference appeared and both sides' values with their presence flags plus their canonical JSON text, unprovenDifference with the same shape for a difference that cannot be concluded, contexts with the difference and up to context events before and after it on each side (the side without the difference event gets the same alignment position as an insertion point and an explicit differencePresent false instead of a borrowed event), conditionDifferences describing the two runs' declared definition hash, configuration fingerprint, environment fields, clock coverage, coverage facts and window state, and both side views with state, terminal, verdict, terminationReason, startSequence, endSequence, evidenceState (live for an open window, frozen for a sealed or interrupted window whose bounded evidence was retained at closing, evicted after the payload was released by budget), detailAvailable, sourceMatchCount, selectedCount, excludedByClockCount, intervalReadable, sampled, truncated, alignmentKeyMissingCount, alignmentAmbiguous, the declared clock range, complete, coverage gaps and a coverage text. This tool reads live evidence only: it does not compare external files and it reports the coverage it really achieved instead of a claim. Budget: " + DebugLimits.RunCompareSummary() + " " + DebugLimits.RunSummary() + " Rejections: E_DEBUG_RUN_NOT_FOUND when a runId is unknown or belongs to a previous domain generation, and E_SCHEMA_INVALID for a missing runId, source, position, alignment, valuePath or clock, an unknown source, alignment kind or clock, a keyPath given for ordinal alignment or missing for key alignment, a malformed or too long field path, or a context outside the declared range. Reliability: the comparison is deterministic for retained evidence, does not stop or seal anything, and is safe to call while programs still run outside both compared windows.",
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
