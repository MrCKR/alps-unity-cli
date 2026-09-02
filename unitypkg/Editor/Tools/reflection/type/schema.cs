#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>type.schema — live 类型 schema 的有界成员/definition 分页。</summary>
    public class TypeSchemaTool : IUnityCliTool<TypeSchemaTool.Request, TypeSchemaTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string TypeName { get; set; }
            [UnityCliRange(0L, 16L)] public int? MaxDepth { get; set; }
            [UnityCliRange(1L, 512L)] public int? MaxMembers { get; set; }
            [UnityCliRange(1L, 256L)] public int? MaxDefinitions { get; set; }
            [UnityCliRange(1L, 256L)] public int? PageSize { get; set; }
            public string Cursor { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            [UnityCliAnyJson] public object Schema { get; set; }
            public bool Truncated { get; set; }
            public string NextCursor { get; set; }
            public string Generation { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "type.schema",
            Summary = "Page a live C# type schema under global depth, member, definition, reflection and byte ceilings.",
            Description = "Generate a JSON Schema page for one live C# type. maxDepth is 0..16 (default 3), maxMembers 1..512 (default 256), maxDefinitions 1..256 (default 128), and pageSize 1..256 (default 64). Root members are stable-ordinal paged; nextCursor binds assembly generation and all structural limits. Recursive definitions share the same member/node/reflection/monotonic-time/UTF-8 budget (defaults 4096 nodes, 4096 reflection calls, 250 ms and 1 MiB; maxima 20000/20000/2000 ms/8 MiB). Assembly reload or compilation invalidates the type cache and old cursors. Read-only in Edit and Play Mode.",
            Group = "reflection", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read the first Rigidbody schema page", Input = new Request { TypeName = "UnityEngine.Rigidbody", PageSize = 64 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (request == null || string.IsNullOrEmpty(request.TypeName))
                throw new UnityCliBridge.BridgeCommandException("E_TYPE_NAME_REQUIRED", "type.schema requires typeName.", "Pass a full live C# type name.");
            var budget = ctx.UseReadBudget(request.Budget);
            var page = UnityCliBridge.TypeSchemaGenerator.Generate(request.TypeName, request.MaxDepth ?? 3, request.MaxMembers ?? 256,
                request.MaxDefinitions ?? 128, request.PageSize ?? 64, request.Cursor, budget);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Schema = page.Data, Truncated = page.Truncated, NextCursor = page.NextCursor,
                Generation = page.Generation, Budget = budget.Usage()
            });
        }
    }
}
#endif
