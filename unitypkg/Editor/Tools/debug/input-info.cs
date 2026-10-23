#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.input-info — 只读读取可选设备后端的能力、通用输入路径、候选 managed wrapper 的现场验证结果
    /// 与本 owner 实际拥有的虚拟设备与输入记录。它不建立设备、不提交事件、不安装任何包。
    /// </summary>
    public class DebugInputInfoTool : IUnityCliTool<DebugInputInfoRequest, DebugInputInfoResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.input-info",
            Summary = "Read the optional device backend status, the general input paths, the managed wrapper verification for a candidate input, and the devices and input records this owner actually holds.",
            Description = "Read the input picture of the current Editor session without changing anything. Inputs: optional sessionId to limit the device and record list to one session, and optional wrapper (the exact target shape: assemblyName, typeName, methodName, parameterTypes, returnType, isStatic) to ask whether one concrete managed input wrapper is really drivable here. Output: backend with name, status (available or unavailable), reason, detail, apiIdentity, missingApiMembers, layouts and installedByThisOwner, which is always false because this owner never installs a package; genericRoutes, each naming the real Tool, the real thread and the relation to a logical frame for the general input paths that do not need any device package; legacy with supported, reason, statement, methodIdentity, resolutionError, the verified route (hookId, sessionId, state, hitCount, detourVerified, invocationVerified) and driveWindows; devices with deviceId, kind, name, state, backend, apiIdentity, resourceId, playSegmentId, creation time and thread plus removalReason; holds with dispatchId, deviceId, deviceKind, action, controls, endBoundary, holdMs, expiresAtMonotonicMs, dispatchedAtMonotonicMs, dispatchedThreadId, state (held, pending or released), releaseReason, releasedAtMonotonicMs, releaseConfirmed, the resolved pointer coordinate space with the observed width and height, confirmedControls read live from the device, and confirmReason; budget with the declared device, record, boundary, planned-input and pending-release limits; and ownershipStatement. Availability honesty: the device backend requires the optional Input System API to be loaded AND every API member this owner needs to be present, so a missing package reports status unavailable with reason backend-not-loaded, and a package whose shape does not match reports api-member-missing plus the bounded missingApiMembers list. Nothing is ever installed and no existing device is ever listed as owned. General input is broader than a keyboard: an input can be a project method call, a message enqueue, protocol data, parameter replacement, result replacement or a device event, so the general paths are debug.method-invoke for a business entry, debug.hook-install for parameter, result or skip intervention on an interceptable managed wrapper, debug.code-execute for explicit code, and debug.program-run for a bounded program entry or a cross-thread delivery to a thread-safe point. None of them is tied to a single logical frame. Wrapper verification honesty: a managed wrapper is reported supported only when a real detour is armed on that exact target in this domain AND at least one real invocation of it was observed; otherwise supported is false with reason no-wrapper-declared, wrapper-unresolved (plus resolutionError), no-live-detour or no-observed-invocation, and the statement says explicitly that this input cannot be driven. When the candidate cannot be resolved, no custom project shortcut is used to pretend that it works. A device event submitted by this owner never proves that the business read the input: acceptance must be proven by a hook capture, a program check, a value read or a sealed debug.run window. Budget: " + DebugLimits.InputSummary() + " Rejections: E_DEBUG_SESSION_NOT_FOUND for an unknown sessionId and E_SCHEMA_INVALID for a malformed wrapper. Reliability: this is a pure read, it is safe to call repeatedly, it never writes the session store, and a backend status of unavailable does not fail the call: it is the answer.",
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
                    Description = "Read the backend status and the input records of one session.",
                    Input = new DebugInputInfoRequest { SessionId = "dbg-1" }
                },
                new ToolExample
                {
                    Description = "Ask whether one concrete managed wrapper input is really drivable; replace every identity field with the exact live signature.",
                    Input = new DebugInputInfoRequest
                    {
                        Wrapper = new MethodHookTargetSpec
                        {
                            AssemblyName = "Assembly-CSharp",
                            TypeName = "Game.PlayerInput",
                            MethodName = "ReadMoveAxis",
                            ParameterTypes = new System.Collections.Generic.List<string>(),
                            ReturnType = "System.Single",
                            IsStatic = true
                        }
                    }
                }
            }
        };

        public ValueTask<DebugInputInfoResult> ExecuteAsync(DebugInputInfoRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            return new ValueTask<DebugInputInfoResult>(DebugInputHost.Info(request));
        }
    }
}
#endif
