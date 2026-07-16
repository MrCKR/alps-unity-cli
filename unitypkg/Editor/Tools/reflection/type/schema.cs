#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// type.schema — 为 live C# / Unity 类型生成带 $defs 的 JSON Schema。只读。
    /// </summary>
    public class TypeSchemaTool : IUnityCliTool<TypeSchemaTool.Request, TypeSchemaTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string TypeName { get; set; }
            public int MaxDepth { get; set; } = 3;
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string TypeName { get; set; }
            [UnityCliAnyJson] public object Schema { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "type.schema",
            Summary = "Generate a JSON Schema for a live C# type with $defs.",
            Description = "Generate a JSON Schema (with $defs) for a live C# or Unity type. Read-only. Use the schema to understand the structure of objects before calling object.get-data or object.modify.",
            Group = "reflection",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Get schema for Rigidbody", Input = new Request { TypeName = "UnityEngine.Rigidbody" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.TypeName))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TYPE_NAME_REQUIRED",
                    "type.schema requires typeName.",
                    "Pass --input '{\"typeName\":\"UnityEngine.Rigidbody\"}'.");
            }
            var schema = UnityCliBridge.TypeSchemaGenerator.Generate(request.TypeName, request.MaxDepth);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "type.schema",
                TypeName = request.TypeName,
                Schema = schema
            });
        }
    }
}
#endif
