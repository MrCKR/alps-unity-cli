#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;

namespace UnityCli
{
    /// <summary>
    /// <summary>通过反射调用任意 C# 方法。</summary>
    /// </summary>
    public class ReflectionMethodCallTool : IUnityCliTool<ReflectionMethodCallTool.Request, ReflectionMethodCallTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string TypeName { get; set; }
            [UnityCliRequired]
            public string MethodName { get; set; }
            [UnityCliAnyJson] public object Args { get; set; }
            public List<string> ParameterTypes { get; set; }
            [UnityCliAnyJson] public Dictionary<string, object> InstanceRef { get; set; }
            [UnityCliRequired] public UnityCliCompletionProbe CompletionProbe { get; set; }
        }

        public sealed class Result
        {
            [UnityCliAnyJson] public object Data { get; set; }
            public List<PlannedEffect> PlannedEffects { get; set; }
            public UnityCliCompletionProbeReadback Completion { get; set; }
        }

        public sealed class PlannedEffect
        {
            public string Type { get; set; }
            public string Target { get; set; }
            public string SideEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "reflection.method.call",
            Summary = "Call one synchronous C# overload and prove its state change through a read Tool.",
            Description = "Invoke one non-generic synchronous overload in Edit or Play mode. Pass parameterTypes with full type names in declaration order for exact selection, or omit it only when the supplied argument count selects one overload; use an empty array for an exact zero-parameter signature. args must supply every non-optional parameter; unknown or extra arguments fail before the effect. instanceRef is required for instance methods. completionProbe is mandatory and proves declared before/after state through an eligible read Tool in the same Editor turn. Awaitable, iterator, yield, AsyncOperation, ref, and out methods are rejected; return data is never completion proof.",
            Group = "reflection",
            DestructiveHint = true,
            SideEffects = new[] { "arbitrary-method-invocation" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Preview a synchronous overload without invoking it",
                    Input = new Request
                    {
                        TypeName = "UnityEngine.Mathf", MethodName = "Abs", Args = new object[] { -2.5f }, ParameterTypes = new List<string> { "System.Single" },
                        CompletionProbe = new UnityCliCompletionProbe
                        {
                            ReadTool = "scene.gameobject", Input = new Dictionary<string, object> { { "target", "Main Camera" } },
                            Assertions = new List<UnityCliCompletionAssertion>
                            {
                                new UnityCliCompletionAssertion { Path = "/gameObject/transform/position/x", Operator = "approx", Before = 0.0, After = 1.0, Tolerance = 0.0001 }
                            }
                        }
                    },
                    Execution = new ToolExampleExecution { DryRun = true }
                }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.TypeName) || string.IsNullOrEmpty(request.MethodName))
            {
                throw new UnityCliBridge.BridgeCommandException("E_METHOD_TARGET_REQUIRED",
                    "reflection.method.call requires typeName and methodName.",
                    "Run tools run reflection.method.find to discover methods, then retry with the required target fields.");
            }

            var bindings = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            // overload、参数与 instance 都是可预见校验，必须在 effect barrier 前完成。
            var prepared = UnityCliBridge.ReflectionInvoker.PrepareCall(
                request.TypeName, request.MethodName,
                request.Args, request.ParameterTypes, request.InstanceRef, bindings);

            // dry-run 必须在任何反射调用前返回，任意方法即使看似只读也不能在预览阶段执行。
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    PlannedEffects = new List<PlannedEffect>
                    {
                        new PlannedEffect { Type = "reflection.method.call", Target = request.TypeName + "." + request.MethodName, SideEffects = "arbitrary-method-invocation" }
                    }
                });
            }

            Dictionary<string, object> data = null;
            var completion = ctx.RunCompletionProbe(request.CompletionProbe, () => data = UnityCliBridge.ReflectionInvoker.InvokePrepared(prepared));
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Data = data,
                Completion = completion,
                PlannedEffects = new List<PlannedEffect>
                {
                    new PlannedEffect { Type = "reflection.method.call", Target = request.TypeName + "." + request.MethodName, SideEffects = "arbitrary-method-invocation" }
                }
            });
        }
    }
}
#endif
