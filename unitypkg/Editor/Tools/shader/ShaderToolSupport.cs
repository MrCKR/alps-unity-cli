#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// shader 域 IUnityCliTool 共享的基础设施 helper:Shader 路径校验、Shader 资产加载、
    /// 诊断/属性/SubShader 反射读取、默认 Shader 内容生成。仅供 shader 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// 设计依据:.plan/工具统一形态.md(自包含工具共享只读 helper,handler 逻辑各自内聚)。
    /// </summary>
    public static class ShaderToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求,避免脏状态写入。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            SceneToolSupport.EnsureEditorNotBusy(command);
        }

        // 校验并返回 Assets/*.shader 或 *.shadergraph 路径;空或格式不符抛 BridgeCommandException。
        public static string RequireShaderPath(string rawPath)
        {
            var path = AssetToolSupport.NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_PATH_REQUIRED", "Pass an Assets/*.shader or Assets/*.shadergraph path.", "Use tools run shader.list --input '{\"keyword\":\"<keyword>\"}' to discover the path, or pass an Assets/*.shader path.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || (!path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_PATH_INVALID", "Shader path must be under Assets/ and end with .shader or .shadergraph: " + path, "Pass a Unity asset path such as Assets/Shaders/Example.shader.");
            }
            return path;
        }

        /// <summary>校验 Shader 路径并要求资产已存在。</summary>
        public static string ResolveShaderPath(string rawPath)
        {
            var path = RequireShaderPath(rawPath);
            if (!AssetToolSupport.AssetExists(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_NOT_FOUND", "Shader asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:Shader\"}' to discover the path.");
            }
            return path;
        }

        /// <summary>加载 Shader 资产(任意类型);不存在抛 BridgeCommandException。</summary>
        public static UnityEngine.Object LoadShaderLikeAsset(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_NOT_FOUND", "Shader asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:Shader\"}' to discover the path.");
            }
            return asset;
        }

        /// <summary>构造 Shader 资产摘要(含诊断)。</summary>
        public static ShaderInfo BuildShaderInfo(string path, Shader shader, bool includeContentMetadata)
        {
            var fullPath = AssetToolSupport.AssetFullPath(path);
            var fileInfo = new FileInfo(fullPath);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            return new ShaderInfo
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                Name = shader != null ? shader.name : (asset != null ? asset.name : Path.GetFileNameWithoutExtension(path)),
                AssetType = asset == null ? null : asset.GetType().Name,
                IsShaderGraph = path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase),
                Exists = fileInfo.Exists,
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0L,
                Encoding = includeContentMetadata ? "utf-8" : null,
                Diagnostics = BuildDiagnostics(shader)
            };
        }

        /// <summary>构造 Shader 编译诊断(含消息明细)。</summary>
        public static ShaderDiagnostics BuildDiagnostics(Shader shader)
        {
            if (shader == null)
            {
                return new ShaderDiagnostics { Supported = null, MessageCount = 0, Messages = new List<ShaderMessage>() };
            }
            var messages = ReadShaderMessages(shader);
            return new ShaderDiagnostics
            {
                Supported = shader.isSupported,
                MessageCount = messages.Count,
                Messages = messages
            };
        }

        /// <summary>构造诊断摘要(不含消息明细)。</summary>
        public static ShaderDiagnostics BuildDiagnosticsSummary(Shader shader)
        {
            if (shader == null)
            {
                return new ShaderDiagnostics { Supported = null, MessageCount = 0, Messages = new List<ShaderMessage>() };
            }
            var count = ReadShaderMessages(shader).Count;
            return new ShaderDiagnostics { Supported = shader.isSupported, MessageCount = count, Messages = new List<ShaderMessage>() };
        }

        // ─── Shader 反射读取(SubShader/Pass 结构;Unity 版本不支持时返回空) ──

        /// <summary>读取 SubShader/Pass 结构 DTO 列表。</summary>
        public static List<ShaderSubshader> BuildSubshaders(Shader shader, bool includeSourceCode, int maxSubshaders, int maxPasses)
        {
            var result = new List<ShaderSubshader>();
            var shaderData = ShaderDataObject(shader);
            if (shaderData == null) return result;
            var subshaderCount = Math.Min(ReflectionInt(shaderData, "SubshaderCount"), maxSubshaders);
            for (var s = 0; s < subshaderCount; s++)
            {
                var subshader = ReflectionInvoke(shaderData, "GetSubshader", s);
                if (subshader == null) continue;
                var passCountTotal = ReflectionInt(subshader, "PassCount");
                var passCount = Math.Min(passCountTotal, maxPasses);
                var passes = new List<ShaderPass>();
                for (var p = 0; p < passCount; p++)
                {
                    var pass = ReflectionInvoke(subshader, "GetPass", p);
                    var passInfo = new ShaderPass { Index = p };
                    if (pass != null)
                    {
                        passInfo.Name = ReflectionValue(pass.GetType(), pass, "Name");
                        if (includeSourceCode) passInfo.SourceCode = ReflectionValue(pass.GetType(), pass, "SourceCode");
                    }
                    passes.Add(passInfo);
                }
                result.Add(new ShaderSubshader { Index = s, PassCount = passCountTotal, Passes = passes });
            }
            return result;
        }

        /// <summary>判断 ShaderData 反射对象是否可用。</summary>
        public static bool SubshaderDataAvailable(Shader shader)
        {
            return ShaderDataObject(shader) != null;
        }

        private static List<ShaderMessage> ReadShaderMessages(Shader shader)
        {
            var items = new List<ShaderMessage>();
            var getMessages = ShaderMessagesMethod();
            if (getMessages == null) return items;
            var messages = getMessages.Invoke(null, new object[] { shader }) as Array;
            if (messages == null) return items;
            foreach (var message in messages)
            {
                var type = message.GetType();
                items.Add(new ShaderMessage
                {
                    Severity = ReflectionValue(type, message, "severity"),
                    Message = ReflectionValue(type, message, "message"),
                    File = ReflectionValue(type, message, "file"),
                    Line = ReflectionValue(type, message, "line"),
                    Platform = ReflectionValue(type, message, "platform")
                });
            }
            return items;
        }

        private static object ShaderDataObject(Shader shader)
        {
            var shaderUtil = Type.GetType("UnityEditor.ShaderUtil,UnityEditor");
            if (shaderUtil == null) return null;
            foreach (var method in shaderUtil.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "GetShaderData") continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Shader))
                {
                    return method.Invoke(null, new object[] { shader });
                }
            }
            return null;
        }

        private static MethodInfo ShaderMessagesMethod()
        {
            var shaderUtil = Type.GetType("UnityEditor.ShaderUtil,UnityEditor");
            if (shaderUtil == null) return null;
            foreach (var method in shaderUtil.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "GetShaderMessages") continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Shader)) return method;
            }
            return null;
        }

        private static object ReflectionInvoke(object target, string name, int index)
        {
            if (target == null) return null;
            var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return method == null ? null : method.Invoke(target, new object[] { index });
        }

        private static int ReflectionInt(object target, string name)
        {
            if (target == null) return 0;
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                var value = property.GetValue(target, null);
                if (value is int) return (int)value;
            }
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                var value = field.GetValue(target);
                if (value is int) return (int)value;
            }
            return 0;
        }

        private static string ReflectionValue(Type type, object target, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                var value = field.GetValue(target);
                return value == null ? null : value.ToString();
            }
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                var value = property.GetValue(target, null);
                return value == null ? null : value.ToString();
            }
            return null;
        }

        /// <summary>生成默认的简单 unlit Shader 内容。</summary>
        public static string DefaultShaderContent(string rawName, string path)
        {
            var name = string.IsNullOrEmpty(rawName) ? Path.GetFileNameWithoutExtension(path) : rawName;
            return "Shader \"UnityCli/" + EscapeShaderName(name) + "\" {\n    Properties {\n        _Color (\"Color\", Color) = (1,1,1,1)\n    }\n    SubShader {\n        Tags { \"RenderType\"=\"Opaque\" }\n        Pass {\n            CGPROGRAM\n            #include \"UnityCG.cginc\"\n            #pragma vertex vert\n            #pragma fragment frag\n            struct appdata { float4 vertex : POSITION; };\n            struct v2f { float4 vertex : SV_POSITION; };\n            fixed4 _Color;\n            v2f vert (appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); return o; }\n            fixed4 frag (v2f i) : SV_Target { return _Color; }\n            ENDCG\n        }\n    }\n}\n";
        }

        private static string EscapeShaderName(string name)
        {
            return (name ?? "Shader").Replace("\\", "/").Replace("\"", "");
        }
    }

    // ─── shader 域共享 DTO ────────────────────────────────────────────────

    public sealed class ShaderInfo
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public string AssetType { get; set; }
        public bool IsShaderGraph { get; set; }
        public bool Exists { get; set; }
        public long SizeBytes { get; set; }
        public string Encoding { get; set; }
        public ShaderDiagnostics Diagnostics { get; set; }
    }

    public sealed class ShaderDiagnostics
    {
        public bool? Supported { get; set; }
        public long MessageCount { get; set; }
        public List<ShaderMessage> Messages { get; set; }
    }

    public sealed class ShaderMessage
    {
        public string Severity { get; set; }
        public string Message { get; set; }
        public string File { get; set; }
        public string Line { get; set; }
        public string Platform { get; set; }
    }

    public sealed class ShaderProperty
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public string Flags { get; set; }
        public long NameId { get; set; }
        public ShaderPropertyRange Range { get; set; }
        public string DefaultTextureName { get; set; }
        public List<string> Attributes { get; set; }
    }

    public sealed class ShaderPropertyRange
    {
        public double Min { get; set; }
        public double Max { get; set; }
    }

    public sealed class ShaderSubshader
    {
        public long Index { get; set; }
        public long PassCount { get; set; }
        public List<ShaderPass> Passes { get; set; }
    }

    public sealed class ShaderPass
    {
        public long Index { get; set; }
        public string Name { get; set; }
        public string SourceCode { get; set; }
    }

    /// <summary>计划副作用项,shader 域 mutation 工具 Result 共享。</summary>
    public sealed class ShaderPlannedEffect
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public bool Refresh { get; set; }
    }
}
#endif
