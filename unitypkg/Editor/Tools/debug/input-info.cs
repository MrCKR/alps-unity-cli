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
            Description = "Read the input picture of the current Editor session without changing anything. Optional sessionId limits the device and record list to one session, and optional wrapper (assemblyName, typeName, methodName, parameterTypes, returnType, isStatic) asks whether one concrete managed input wrapper is really drivable here. Output covers backend status with missingApiMembers and installedByThisOwner, genericRoutes naming the real Tool, thread and frame relation for paths that need no device package, legacy route verification, devices, holds with live confirmedControls and resolved pointer space, the declared budget, and ownershipStatement. A wrapper is reported supported only when a real detour is armed on that exact target in this domain and at least one real invocation was observed; otherwise supported is false with reason no-wrapper-declared, wrapper-unresolved, no-live-detour or no-observed-invocation. Pure read: safe to repeat, never writes the session store, and an unavailable backend is the answer rather than a failure. Rejections: E_DEBUG_SESSION_NOT_FOUND, E_SCHEMA_INVALID.", Notes = DebugDomainNotes.InputRead,
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
