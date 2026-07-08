#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// vfx.parameter - 设置 VisualEffect 暴露参数。
    /// </summary>
    public class VfxParameterTool : IUnityCliTool<VfxParameterTool.Request, VfxParameterTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Target { get; set; }
            [UnityCliRequired]
            public string Name { get; set; }
            public string Type { get; set; }
            public double? NumberValue { get; set; }
            public int? IntValue { get; set; }
            public bool? BoolValue { get; set; }
            public string AssetPath { get; set; }
            public double? X { get; set; }
            public double? Y { get; set; }
            public double? Z { get; set; }
            public double? W { get; set; }
            public double? R { get; set; }
            public double? G { get; set; }
            public double? B { get; set; }
            public double? A { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public string ParameterName { get; set; }
            public string ParameterType { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.parameter",
            Summary = "Set an exposed VisualEffect parameter by explicit scalar, vector, color, texture, or mesh input.",
            Description = "Set an exposed VisualEffect parameter on a target component using an explicit type. Use numberValue, intValue, boolValue, assetPath, or the vector/color axis fields that match the requested type. This tool reflects the package API at runtime and does not require compile-time VFX references.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "component-state-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set float parameter", Input = new Request { Target = "Effects/Emitter", Name = "SpawnRate", Type = "float", NumberValue = 12.5 } },
                new ToolExample { Description = "Set color parameter", Input = new Request { Target = "Effects/Emitter", Name = "Tint", Type = "color", R = 1, G = 0.5, B = 0.2, A = 1 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.parameter");
            var visualEffectType = VfxToolSupport.RequireVisualEffectType();
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "VFX parameter requires target.");
            var component = gameObject.GetComponent(visualEffectType);
            if (component == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_COMPONENT_NOT_FOUND",
                    "VisualEffect component not found on target: " + GameObjectToolSupport.HierarchyPath(gameObject),
                    "Pass add=true to vfx.effect or choose a target with VisualEffect.");
            }

            var parameterType = (request.Type ?? "float").Trim().ToLowerInvariant();
            var effects = new List<VfxPlannedEffect>();
            try
            {
                switch (parameterType)
                {
                    case "float":
                        Invoke(visualEffectType, component, "SetFloat", new[] { typeof(string), typeof(float) }, request.Name, (float)(request.NumberValue ?? 0d));
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, VfxToolSupport.Number(request.NumberValue ?? 0d)));
                        break;
                    case "int":
                        Invoke(visualEffectType, component, "SetInt", new[] { typeof(string), typeof(int) }, request.Name, request.IntValue ?? 0);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, (request.IntValue ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                    case "bool":
                        Invoke(visualEffectType, component, "SetBool", new[] { typeof(string), typeof(bool) }, request.Name, request.BoolValue ?? false);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, VfxToolSupport.Bool(request.BoolValue ?? false)));
                        break;
                    case "vector2":
                        Invoke(visualEffectType, component, "SetVector2", new[] { typeof(string), typeof(Vector2) }, request.Name, new Vector2((float)(request.X ?? 0d), (float)(request.Y ?? 0d)));
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, "vector2"));
                        break;
                    case "vector3":
                        Invoke(visualEffectType, component, "SetVector3", new[] { typeof(string), typeof(Vector3) }, request.Name, new Vector3((float)(request.X ?? 0d), (float)(request.Y ?? 0d), (float)(request.Z ?? 0d)));
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, "vector3"));
                        break;
                    case "vector4":
                        Invoke(visualEffectType, component, "SetVector4", new[] { typeof(string), typeof(Vector4) }, request.Name, new Vector4((float)(request.X ?? 0d), (float)(request.Y ?? 0d), (float)(request.Z ?? 0d), (float)(request.W ?? 0d)));
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, "vector4"));
                        break;
                    case "color":
                        Invoke(visualEffectType, component, "SetVector4", new[] { typeof(string), typeof(Vector4) }, request.Name, (Vector4)new Color((float)(request.R ?? 0d), (float)(request.G ?? 0d), (float)(request.B ?? 0d), (float)(request.A ?? 1d)));
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, "color"));
                        break;
                    case "texture":
                        var texture = string.IsNullOrWhiteSpace(request.AssetPath) ? null : AssetDatabase.LoadAssetAtPath<Texture>(AssetToolSupport.NormalizeAssetPath(request.AssetPath));
                        if (texture == null && !string.IsNullOrWhiteSpace(request.AssetPath))
                        {
                            throw new UnityCliBridge.BridgeCommandException("E_VFX_TEXTURE_NOT_FOUND", "Texture not found: " + request.AssetPath, "Pass a valid Assets/ texture path.");
                        }
                        Invoke(visualEffectType, component, "SetTexture", new[] { typeof(string), typeof(Texture) }, request.Name, texture);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, request.AssetPath ?? "null"));
                        break;
                    case "mesh":
                        var mesh = string.IsNullOrWhiteSpace(request.AssetPath) ? null : AssetDatabase.LoadAssetAtPath<Mesh>(AssetToolSupport.NormalizeAssetPath(request.AssetPath));
                        if (mesh == null && !string.IsNullOrWhiteSpace(request.AssetPath))
                        {
                            throw new UnityCliBridge.BridgeCommandException("E_VFX_MESH_NOT_FOUND", "Mesh not found: " + request.AssetPath, "Pass a valid Assets/ mesh path.");
                        }
                        Invoke(visualEffectType, component, "SetMesh", new[] { typeof(string), typeof(Mesh) }, request.Name, mesh);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, request.AssetPath ?? "null"));
                        break;
                    default:
                        throw new UnityCliBridge.BridgeCommandException(
                            "E_VFX_PARAMETER_TYPE_UNSUPPORTED",
                            "Unsupported VFX parameter type: " + parameterType,
                            "Use float, int, bool, vector2, vector3, vector4, color, texture, or mesh.");
                }
            }
            catch (TargetInvocationException exception)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_PARAMETER_SET_FAILED",
                    "Failed to set VFX parameter: " + (exception.InnerException == null ? exception.Message : exception.InnerException.Message),
                    "Check the parameter name and type match the VFX Graph exposed parameters.");
            }

            EditorUtility.SetDirty(component);
            VfxToolSupport.MarkSceneDirty(gameObject);
            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                ParameterName = request.Name,
                ParameterType = parameterType,
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }

        private static void Invoke(Type owner, Component component, string methodName, System.Type[] signature, params object[] args)
        {
            var method = owner.GetMethod(methodName, signature);
            if (method != null)
            {
                method.Invoke(component, args);
            }
        }
    }
}
#endif
