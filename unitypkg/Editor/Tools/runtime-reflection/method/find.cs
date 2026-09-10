#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;

namespace UnityCli
{
    /// <summary>runtime-reflection.method.find — stable raw method ordinal pagination under one reflection budget。</summary>
    public class ReflectionMethodFindTool : IUnityCliTool<ReflectionMethodFindTool.Request, ReflectionMethodFindTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string TypeName { get; set; }
            public string MethodName { get; set; }
            public bool IncludePrivate { get; set; }
            public bool IncludeStatic { get; set; } = true;
            public bool IncludeInstance { get; set; } = true;
            [UnityCliRange(1L, 500L)] public int? PageSize { get; set; }
            public string Cursor { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public long Total { get; set; }
            public long ReturnedCount { get; set; }
            public List<UnityCliBridge.ReflectionMethodDescriptor> Methods { get; set; }
            public string NextCursor { get; set; }
            public bool Truncated { get; set; }
            public string Generation { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "runtime-reflection.method.find",
            Summary = "Page live C# method signatures by exact type/name and visibility/static scope under one reflection budget.",
            Description = "Find methods on one exact live type. includePrivate defaults false; includeStatic/includeInstance default true. pageSize is 1..500 (default 100). Filtering advances over raw MethodInfo ordinals, so sparse searches remain bounded and total is the raw method count rather than a fabricated filtered total. Each method and parameter descriptor consumes the shared scan/node/reflection/monotonic-time/UTF-8 budget; defaults are 10000/4096/4096/250 ms/1 MiB and maxima are 100000/20000/20000/2000 ms/8 MiB. A parameter-heavy method may return parametersTruncated with parameterCount. nextCursor binds type, flags, name filter and listener/assembly generation. Read-only in Edit and Play Mode.",
            Group = "runtime-reflection", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Find Translate overloads", Input = new Request { TypeName = "UnityEngine.Transform", MethodName = "Translate", PageSize = 100 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (request == null || string.IsNullOrEmpty(request.TypeName))
                throw new UnityCliBridge.BridgeCommandException("E_TYPE_NAME_REQUIRED", "runtime-reflection.method.find requires typeName.", "Pass a full live C# type name.");
            var bindings = (request.IncludePrivate ? BindingFlags.Public | BindingFlags.NonPublic : BindingFlags.Public)
                | (request.IncludeStatic ? BindingFlags.Static : BindingFlags.Default)
                | (request.IncludeInstance ? BindingFlags.Instance : BindingFlags.Default);
            if (!request.IncludeStatic && !request.IncludeInstance)
                throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_SCOPE_INVALID", "At least one of includeStatic/includeInstance must be true.", "Enable static methods, instance methods, or both.");
            var budget = ctx.UseReadBudget(request.Budget);
            var page = UnityCliBridge.ReflectionInvoker.FindMethods(request.TypeName, request.MethodName, bindings, request.PageSize ?? 100, request.Cursor, budget);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Total = page.Total, ReturnedCount = page.Methods.Count, Methods = page.Methods,
                NextCursor = page.NextCursor, Truncated = page.Truncated, Generation = page.Generation, Budget = budget.Usage()
            });
        }
    }
}
#endif
