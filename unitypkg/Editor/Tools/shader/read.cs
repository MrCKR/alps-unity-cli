#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// shader.read — 分页读取 Shader 源码文本(offset/maxBytes/nextOffset)。
    /// 对齐旧 ShaderRead handler;只读。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ShaderReadTool : IUnityCliTool<ShaderReadTool.Request, ShaderReadTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
            public int? Offset { get; set; }
            public int? MaxBytes { get; set; }
        }

        public sealed class Result
        {
            public ShaderInfo Shader { get; set; }
            public string Content { get; set; }
            public string Encoding { get; set; }
            public long Offset { get; set; }
            public long MaxBytes { get; set; }
            public long SizeBytes { get; set; }
            public long ReturnedBytes { get; set; }
            public bool Truncated { get; set; }
            public string NextOffset { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "shader.read",
            Summary = "Read shader source text in bounded pages with offset/maxBytes and nextOffset continuation.",
            Description = "Read shader source text from an Assets/*.shader file in bounded pages. Use offset to continue from a previous nextOffset and maxBytes to cap the returned chunk. ShaderGraph (.shadergraph) assets are JSON, not HLSL source.",
            Group = "shader",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read the first 64KB", Input = new Request { Path = "Assets/Shaders/Example.shader", MaxBytes = 65536 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var path = ShaderToolSupport.ResolveShaderPath(request.Path ?? request.Target);
            ShaderToolSupport.LoadShaderLikeAsset(path);
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(AssetToolSupport.AssetFullPath(path));
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_IO_ERROR", "Failed to read Shader file: " + exception.Message, "Check file encoding and permissions: " + path);
            }
            var offset = Math.Max(0, request.Offset ?? 0);
            var maxBytes = Math.Max(1, Math.Min(request.MaxBytes ?? 65536, 1024 * 1024));
            if (offset > bytes.Length) offset = bytes.Length;
            var returnedBytes = Math.Min(maxBytes, bytes.Length - offset);
            var content = Encoding.UTF8.GetString(bytes, offset, returnedBytes);
            var nextOffset = offset + returnedBytes < bytes.Length ? (offset + returnedBytes).ToString(CultureInfo.InvariantCulture) : null;
            return new Result
            {
                Shader = ShaderToolSupport.BuildShaderInfo(path, AssetDatabase.LoadAssetAtPath<Shader>(path), true),
                Content = content,
                Encoding = "utf-8",
                Offset = offset,
                MaxBytes = maxBytes,
                SizeBytes = bytes.Length,
                ReturnedBytes = returnedBytes,
                Truncated = nextOffset != null,
                NextOffset = nextOffset
            };
        }
    }
}
#endif
