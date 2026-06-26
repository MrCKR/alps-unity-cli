#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterShaderCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "shader.info", "shader", false, ShaderInfo);
            RegisterCommand(registry, "shader.read", "shader", false, ShaderRead);
            RegisterCommand(registry, "shader.create", "shader", true, ShaderCreate);
            RegisterCommand(registry, "shader.update", "shader", true, ShaderUpdate);
            RegisterCommand(registry, "shader.list", "shader", false, ShaderList);
            RegisterCommand(registry, "shader.get-data", "shader", false, ShaderGetData);
        }

        /// <summary>列出项目中所有 Shader 资产。对齐 Ivan shader list。</summary>
        private static string ShaderList(BridgeRequest request)
        {
            var keyword = request.StringArg("keyword") ?? request.StringArg("search");
            var guids = AssetDatabase.FindAssets("t:Shader " + (keyword ?? string.Empty));
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", 100), 500));
            var results = new List<string>();
            foreach (var guid in guids)
            {
                if (results.Count >= pageSize) break;
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var shader = AssetDatabase.LoadMainAssetAtPath(assetPath) as Shader;
                if (shader == null) continue;
                results.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("path", NormalizeAssetPath(assetPath))
                    .Prop("guid", guid)
                    .Prop("name", shader.name)
                    .Prop("propertyCount", (long)shader.GetPropertyCount())
                    .Prop("renderQueue", (long)shader.renderQueue)
                    .EndObject().ToString());
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "shader.list").Prop("count", (long)results.Count)
                .PropArray("shaders", arr => { foreach (var r in results) arr.RawValue(r); })
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>读取 Shader 属性/subshaders/passes/compile messages。对齐 Ivan shader get-data。</summary>
        private static string ShaderGetData(BridgeRequest request)
        {
            var path = request.StringArg("path") ?? request.StringArg("assetPath");
            Shader shader;
            if (!string.IsNullOrEmpty(path))
            {
                shader = AssetDatabase.LoadMainAssetAtPath(path) as Shader;
            }
            else
            {
                var name = request.StringArg("name");
                shader = Shader.Find(name);
            }
            if (shader == null)
            {
                throw new BridgeCommandException("E_SHADER_NOT_FOUND", "Shader not found.", "Run tools run shader.list --input '{\"keyword\":\"<keyword>\"}' to discover shader paths.");
            }
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "shader.get-data");
            w.Prop("name", shader.name);
            w.Prop("renderQueue", (long)shader.renderQueue);
            w.Prop("isSupported", shader.isSupported);
            w.PropArray("properties", arr =>
            {
                for (var i = 0; i < shader.GetPropertyCount(); i++)
                {
                    arr.BeginObject()
                        .Prop("name", shader.GetPropertyName(i))
                        .Prop("description", shader.GetPropertyDescription(i))
                        .Prop("type", shader.GetPropertyType(i).ToString())
                        .Prop("flags", shader.GetPropertyFlags(i).ToString())
                        .EndObject();
                }
            });
            w.Prop("propertyCount", (long)shader.GetPropertyCount());
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        private static string ShaderInfo(BridgeRequest request)
        {
            var path = ResolveShaderPath(request.StringArg("path") ?? request.StringArg("target"));
            var shader = LoadShaderLikeAsset(path) as Shader;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("shader", ShaderAssetJson(path, shader, false))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ShaderRead(BridgeRequest request)
        {
            var path = ResolveShaderPath(request.StringArg("path") ?? request.StringArg("target"));
            LoadShaderLikeAsset(path);
            var fullPath = AssetFullPath(path);
            string content;
            try
            {
                content = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_IO_ERROR", "Failed to read Shader file: " + exception.Message, "Check file encoding and permissions: " + path);
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("shader", ShaderAssetJson(path, AssetDatabase.LoadAssetAtPath<Shader>(path), true))
                .Prop("content", content)
                .Prop("encoding", "utf-8")
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ShaderCreate(BridgeRequest request)
        {
            var path = RequireShaderPath(request.StringArg("path"));
            var overwrite = request.BoolArg("overwrite", false);
            if (AssetExists(path) && !overwrite)
            {
                return ErrorJson("E_SHADER_OUTPUT_EXISTS", "Shader output already exists: " + path, "Pass --overwrite or choose a different Assets/*.shader path.");
            }

            var content = request.StringArg("content");
            if (string.IsNullOrEmpty(content))
            {
                if (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                {
                    return ErrorJson("E_SHADER_CONTENT_REQUIRED", "ShaderGraph creation requires --content or --content-file.", "Pass a ShaderGraph JSON payload via --content-file.");
                }
                content = DefaultShaderContent(request.StringArg("name"), path);
            }

            WriteTextAsset(path, content, overwrite);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            WriteLog("shader_created path=" + path);
            return SuccessJson(ShaderMutationJson("shader.create", path));
        }

        private static string ShaderUpdate(BridgeRequest request)
        {
            var path = ResolveShaderPath(request.StringArg("path") ?? request.StringArg("target"));
            LoadShaderLikeAsset(path);
            var content = request.StringArg("content");
            if (content == null)
            {
                return ErrorJson("E_SHADER_CONTENT_REQUIRED", "Shader update requires --content or --content-file.", "Pass replacement Shader content via --content-file.");
            }

            WriteTextAsset(path, content, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            WriteLog("shader_updated path=" + path);
            return SuccessJson(ShaderMutationJson("shader.update", path));
        }

        private static string RequireShaderPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_SHADER_PATH_REQUIRED", "Pass an Assets/*.shader or Assets/*.shadergraph path.", "Use tools run shader.list --input '{\"keyword\":\"<keyword>\"}' to discover the path, or pass an Assets/*.shader path.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || (!path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)))
            {
                throw new BridgeCommandException("E_SHADER_PATH_INVALID", "Shader path must be under Assets/ and end with .shader or .shadergraph: " + path, "Pass a Unity asset path such as Assets/Shaders/Example.shader.");
            }
            return path;
        }

        private static string ResolveShaderPath(string rawPath)
        {
            var path = RequireShaderPath(rawPath);
            if (!AssetExists(path))
            {
                throw new BridgeCommandException("E_SHADER_NOT_FOUND", "Shader asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:Shader\"}' to discover the path.");
            }
            return path;
        }

        private static UnityEngine.Object LoadShaderLikeAsset(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null)
            {
                throw new BridgeCommandException("E_SHADER_NOT_FOUND", "Shader asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:Shader\"}' to discover the path.");
            }
            return asset;
        }

        private static Shader LoadShaderAsset(string path)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null)
            {
                throw new BridgeCommandException("E_SHADER_NOT_FOUND", "Shader asset not found or not compiled as a Shader: " + path, "Check ShaderGraph imports or Unity Console diagnostics.");
            }
            return shader;
        }

        private static string DefaultShaderContent(string rawName, string path)
        {
            var name = string.IsNullOrEmpty(rawName) ? Path.GetFileNameWithoutExtension(path) : rawName;
            return "Shader \"UnityCli/" + EscapeShaderName(name) + "\" {\n    Properties {\n        _Color (\"Color\", Color) = (1,1,1,1)\n    }\n    SubShader {\n        Tags { \"RenderType\"=\"Opaque\" }\n        Pass {\n            CGPROGRAM\n            #include \"UnityCG.cginc\"\n            #pragma vertex vert\n            #pragma fragment frag\n            struct appdata { float4 vertex : POSITION; };\n            struct v2f { float4 vertex : SV_POSITION; };\n            fixed4 _Color;\n            v2f vert (appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); return o; }\n            fixed4 frag (v2f i) : SV_Target { return _Color; }\n            ENDCG\n        }\n    }\n}\n";
        }

        private static string EscapeShaderName(string name)
        {
            return (name ?? "Shader").Replace("\\", "/").Replace("\"", "");
        }

        private static string ShaderMutationJson(string type, string path)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("shader", ShaderAssetJson(path, shader, false))
                .PropArray("plannedEffects", arr => arr.RawValue(new UnityCliJsonWriter().BeginObject()
                    .Prop("type", type)
                    .Prop("path", path)
                    .Prop("refresh", true)
                    .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject().ToString();
        }

        private static string ShaderAssetJson(string path, Shader shader, bool includeContentMetadata)
        {
            var fullPath = AssetFullPath(path);
            var fileInfo = new FileInfo(fullPath);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("name", shader != null ? shader.name : (asset != null ? asset.name : Path.GetFileNameWithoutExtension(path)))
                .Prop("assetType", asset == null ? null : asset.GetType().Name)
                .Prop("isShaderGraph", path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                .Prop("exists", fileInfo.Exists)
                .Prop("sizeBytes", fileInfo.Exists ? (long)fileInfo.Length : 0L)
                .Prop("encoding", includeContentMetadata ? "utf-8" : null)
                .PropRaw("diagnostics", ShaderDiagnosticsJson(shader))
                .EndObject().ToString();
        }

        private static string ShaderDiagnosticsJson(Shader shader)
        {
            if (shader == null)
            {
                return new UnityCliJsonWriter().BeginObject()
                    .PropNull("supported")
                    .Prop("messageCount", 0L)
                    .PropArray("messages", arr => {})
                    .EndObject().ToString();
            }
            var messages = ShaderMessagesJson(shader);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("supported", shader.isSupported)
                .Prop("messageCount", (long)messages.Count)
                .PropArray("messages", arr => { foreach (var m in messages) arr.RawValue(m); })
                .EndObject().ToString();
        }

        private static List<string> ShaderMessagesJson(Shader shader)
        {
            var items = new List<string>();
            var getMessages = ShaderMessagesMethod();
            if (getMessages == null)
            {
                return items;
            }
            var messages = getMessages.Invoke(null, new object[] { shader }) as Array;
            if (messages == null)
            {
                return items;
            }
            foreach (var message in messages)
            {
                items.Add(ShaderMessageJson(message));
            }
            return items;
        }

        private static MethodInfo ShaderMessagesMethod()
        {
            var shaderUtil = Type.GetType("UnityEditor.ShaderUtil,UnityEditor");
            if (shaderUtil == null)
            {
                return null;
            }
            foreach (var method in shaderUtil.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "GetShaderMessages")
                {
                    continue;
                }
                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Shader))
                {
                    return method;
                }
            }
            return null;
        }

        private static string ShaderMessageJson(object message)
        {
            var type = message.GetType();
            return new UnityCliJsonWriter().BeginObject()
                .Prop("severity", ReflectionValue(type, message, "severity"))
                .Prop("message", ReflectionValue(type, message, "message"))
                .Prop("file", ReflectionValue(type, message, "file"))
                .Prop("line", ReflectionValue(type, message, "line"))
                .Prop("platform", ReflectionValue(type, message, "platform"))
                .EndObject().ToString();
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
    }
}
#endif
