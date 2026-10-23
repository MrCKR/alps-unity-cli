#if UNITY_EDITOR
using System;
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
            public string ParameterName { get; set; }
            public string ParameterType { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.parameter",
            Summary = "Set an exposed VisualEffect parameter by explicit scalar, vector, color, texture, or mesh input.",
            Description = "Set an exposed VisualEffect parameter on a target component using an explicit type, then require matching Has*/Get* APIs to verify its typed readback. Use numberValue, intValue, boolValue, assetPath, or the vector/color axis fields that match the requested type. This tool reflects the package API at runtime and does not require compile-time VFX references.",
            Group = "vfx",
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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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
                    "Use vfx.effect-add or choose a target with VisualEffect.");
            }

            if (string.IsNullOrWhiteSpace(request.Name)) throw new UnityCliBridge.BridgeCommandException("E_VFX_PARAMETER_NAME_REQUIRED", "VFX parameter name is required.", "Pass a non-empty exposed parameter name.");
            var parameterType = (request.Type ?? "float").Trim().ToLowerInvariant();
            if (parameterType != "float" && parameterType != "int" && parameterType != "bool" && parameterType != "vector2" && parameterType != "vector3" && parameterType != "vector4" && parameterType != "color" && parameterType != "texture" && parameterType != "mesh") throw new UnityCliBridge.BridgeCommandException("E_VFX_PARAMETER_TYPE_UNSUPPORTED", "Unsupported VFX parameter type: " + parameterType, "Use float, int, bool, vector2, vector3, vector4, color, texture, or mesh.");
            VfxToolSupport.RequireFinite(request.NumberValue, "numberValue"); VfxToolSupport.RequireFinite(request.X, "x"); VfxToolSupport.RequireFinite(request.Y, "y"); VfxToolSupport.RequireFinite(request.Z, "z"); VfxToolSupport.RequireFinite(request.W, "w"); VfxToolSupport.RequireFinite(request.R, "r", 0, 1); VfxToolSupport.RequireFinite(request.G, "g", 0, 1); VfxToolSupport.RequireFinite(request.B, "b", 0, 1); VfxToolSupport.RequireFinite(request.A, "a", 0, 1);
            PrepareParameterApi(visualEffectType, component, parameterType, request.Name);
            if ((parameterType == "texture" || parameterType == "mesh") && !string.IsNullOrWhiteSpace(request.AssetPath) && AssetDatabase.LoadAssetAtPath(AssetToolSupport.NormalizeAssetPath(request.AssetPath), parameterType == "texture" ? typeof(Texture) : typeof(Mesh)) == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_PARAMETER_ASSET_NOT_FOUND", "Parameter asset not found: " + request.AssetPath, "Pass an existing compatible asset path.");
            var gameObjectId = gameObject.GetInstanceID();
            var effects = new List<VfxPlannedEffect>();
            try
            {
                ctx.CommitEffect();
                var observedObject = VfxToolSupport.FindGameObjectByTarget(gameObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                component = observedObject == null ? null : observedObject.GetComponent(visualEffectType);
                if (component == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_OBSERVED_TARGET_MISSING", "vfx.parameter target/component disappeared after commit.", "Retry after checking the target still exists.");
                switch (parameterType)
                {
                    case "float":
                        var floatValue = (float)(request.NumberValue ?? 0d);
                        Invoke(visualEffectType, component, "SetFloat", new[] { typeof(string), typeof(float) }, request.Name, floatValue);
                        Verify(visualEffectType, component, "Float", request.Name, floatValue);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, VfxToolSupport.Number(request.NumberValue ?? 0d)));
                        break;
                    case "int":
                        var intValue = request.IntValue ?? 0;
                        Invoke(visualEffectType, component, "SetInt", new[] { typeof(string), typeof(int) }, request.Name, intValue);
                        Verify(visualEffectType, component, "Int", request.Name, intValue);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, (request.IntValue ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                    case "bool":
                        var boolValue = request.BoolValue ?? false;
                        Invoke(visualEffectType, component, "SetBool", new[] { typeof(string), typeof(bool) }, request.Name, boolValue);
                        Verify(visualEffectType, component, "Bool", request.Name, boolValue);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, VfxToolSupport.Bool(request.BoolValue ?? false)));
                        break;
                    case "vector2":
                        var vector2Value = new Vector2((float)(request.X ?? 0d), (float)(request.Y ?? 0d));
                        Invoke(visualEffectType, component, "SetVector2", new[] { typeof(string), typeof(Vector2) }, request.Name, vector2Value);
                        Verify(visualEffectType, component, "Vector2", request.Name, vector2Value);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, "vector2"));
                        break;
                    case "vector3":
                        var vector3Value = new Vector3((float)(request.X ?? 0d), (float)(request.Y ?? 0d), (float)(request.Z ?? 0d));
                        Invoke(visualEffectType, component, "SetVector3", new[] { typeof(string), typeof(Vector3) }, request.Name, vector3Value);
                        Verify(visualEffectType, component, "Vector3", request.Name, vector3Value);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, "vector3"));
                        break;
                    case "vector4":
                        var vector4Value = new Vector4((float)(request.X ?? 0d), (float)(request.Y ?? 0d), (float)(request.Z ?? 0d), (float)(request.W ?? 0d));
                        Invoke(visualEffectType, component, "SetVector4", new[] { typeof(string), typeof(Vector4) }, request.Name, vector4Value);
                        Verify(visualEffectType, component, "Vector4", request.Name, vector4Value);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, "vector4"));
                        break;
                    case "color":
                        var colorValue = (Vector4)new Color((float)(request.R ?? 0d), (float)(request.G ?? 0d), (float)(request.B ?? 0d), (float)(request.A ?? 1d));
                        Invoke(visualEffectType, component, "SetVector4", new[] { typeof(string), typeof(Vector4) }, request.Name, colorValue);
                        Verify(visualEffectType, component, "Vector4", request.Name, colorValue);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, "color"));
                        break;
                    case "texture":
                        var texture = string.IsNullOrWhiteSpace(request.AssetPath) ? null : AssetDatabase.LoadAssetAtPath<Texture>(AssetToolSupport.NormalizeAssetPath(request.AssetPath));
                        if (texture == null && !string.IsNullOrWhiteSpace(request.AssetPath))
                        {
                            throw new UnityCliBridge.BridgeCommandException("E_VFX_TEXTURE_NOT_FOUND", "Texture not found: " + request.AssetPath, "Pass a valid Assets/ texture path.");
                        }
                        Invoke(visualEffectType, component, "SetTexture", new[] { typeof(string), typeof(Texture) }, request.Name, texture);
                        Verify(visualEffectType, component, "Texture", request.Name, texture);
                        effects.Add(VfxToolSupport.Effect("vfx.parameter", request.Name, request.AssetPath ?? "null"));
                        break;
                    case "mesh":
                        var mesh = string.IsNullOrWhiteSpace(request.AssetPath) ? null : AssetDatabase.LoadAssetAtPath<Mesh>(AssetToolSupport.NormalizeAssetPath(request.AssetPath));
                        if (mesh == null && !string.IsNullOrWhiteSpace(request.AssetPath))
                        {
                            throw new UnityCliBridge.BridgeCommandException("E_VFX_MESH_NOT_FOUND", "Mesh not found: " + request.AssetPath, "Pass a valid Assets/ mesh path.");
                        }
                        Invoke(visualEffectType, component, "SetMesh", new[] { typeof(string), typeof(Mesh) }, request.Name, mesh);
                        Verify(visualEffectType, component, "Mesh", request.Name, mesh);
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

            UnityCliSceneMutation.MarkSceneObjectDirty(component);
            VfxToolSupport.MarkSceneDirty(component.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                ParameterName = request.Name,
                ParameterType = parameterType,
                PlannedEffects = effects
            });
        }

        private static void PrepareParameterApi(Type owner, Component component, string parameterType, string name)
        {
            var suffix = parameterType == "color" ? "Vector4" : char.ToUpperInvariant(parameterType[0]) + parameterType.Substring(1);
            var valueType = parameterType == "float" ? typeof(float) : parameterType == "int" ? typeof(int) : parameterType == "bool" ? typeof(bool) : parameterType == "vector2" ? typeof(Vector2) : parameterType == "vector3" ? typeof(Vector3) : typeof(Vector4);
            if (parameterType == "texture") valueType = typeof(Texture);
            if (parameterType == "mesh") valueType = typeof(Mesh);
            var set = owner.GetMethod("Set" + suffix, new[] { typeof(string), valueType });
            var has = owner.GetMethod("Has" + suffix, new[] { typeof(string) });
            var get = owner.GetMethod("Get" + suffix, new[] { typeof(string) });
            if (set == null || has == null || get == null || get.ReturnType != valueType || has.ReturnType != typeof(bool)) throw new UnityCliBridge.BridgeCommandException("E_VFX_PARAMETER_API_UNSUPPORTED", "VisualEffect typed parameter API is unavailable for " + parameterType + ".", "Check the installed Visual Effect Graph package version.");
            if (!(bool)has.Invoke(component, new object[] { name })) throw new UnityCliBridge.BridgeCommandException("E_VFX_PARAMETER_UNAVAILABLE", "VisualEffect parameter is not exposed: " + name, "Expose a parameter with the requested name and type in the VFX Graph.");
        }

        private static void Invoke(Type owner, Component component, string methodName, System.Type[] signature, params object[] args)
        {
            var method = owner.GetMethod(methodName, signature);
            if (method == null)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_PARAMETER_API_UNSUPPORTED", "VisualEffect method is unavailable: " + methodName, "Check the installed Visual Effect Graph package version.");
            method.Invoke(component, args);
        }

        // 每种参数都必须存在对应 Has*/Get* API，并且读回值与写入的强类型值一致。
        private static void Verify(Type owner, Component component, string typeName, string name, object expected)
        {
            var has = owner.GetMethod("Has" + typeName, new[] { typeof(string) });
            var get = owner.GetMethod("Get" + typeName, new[] { typeof(string) });
            if (has == null || get == null)
                throw new UnityCliBridge.BridgeCommandException("E_VFX_PARAMETER_API_UNSUPPORTED", "VisualEffect readback API is unavailable for " + typeName + ".", "Check the installed Visual Effect Graph package version.");
            if (!(bool)has.Invoke(component, new object[] { name }))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_PARAMETER_UNAVAILABLE", "VisualEffect parameter is not exposed: " + name, "Expose a parameter with the requested name and type in the VFX Graph.");
            var actual = get.Invoke(component, new object[] { name });
            if (!Equals(actual, expected))
                throw new UnityCliBridge.BridgeCommandException("E_VFX_PARAMETER_VERIFY_FAILED", "VisualEffect parameter did not retain the requested value: " + name, "Check the parameter type and value in the VFX Graph.");
        }
    }
}
#endif
