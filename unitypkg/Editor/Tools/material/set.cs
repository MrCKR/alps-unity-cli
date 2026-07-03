#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// material.set — 设置 Material 资产属性(color/float/vector/texture)。session-affecting(asset-write)。
    /// 对齐旧 MaterialSet handler。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class MaterialSetTool : IUnityCliTool<MaterialSetTool.Request, MaterialSetTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Material { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
            public string Color { get; set; }
            public string FloatValue { get; set; }
            public string Vector { get; set; }
            public string Texture { get; set; }
            public string Type { get; set; }
        }

        public sealed class Result
        {
            public MaterialInfo Material { get; set; }
            public string Property { get; set; }
            public string PropertyType { get; set; }
            public object AppliedValue { get; set; }
            public List<MaterialPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "material.set",
            Summary = "Set a Material asset property (color, float, vector or texture) by shader property name.",
            Description = "Set a Material asset property by shader property name. The value type is inferred from the shader property when possible, or overridden with type. Use the color/floatValue/vector/texture aliases for typed inputs. Inspect properties with material.info first.",
            Group = "material",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set the base color", Input = new Request { Path = "Assets/Materials/Probe.mat", Property = "_Color", Color = "#ff0000ff" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            MaterialToolSupport.EnsureEditorNotBusy("material.set");
            var path = MaterialToolSupport.RequireMaterialPath(request.Path ?? request.Material);
            var propertyName = request.Property;
            // value/color/floatValue/vector/texture 别名回退到统一 value
            var value = request.Value ?? request.Color ?? request.FloatValue ?? request.Vector ?? request.Texture;
            var requestedType = request.Type;
            var material = MaterialToolSupport.LoadMaterialAsset(path);
            if (string.IsNullOrEmpty(propertyName))
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PROPERTY_REQUIRED", "Material set requires a property name.", "Inspect shader properties with tools run material.info --input '{\"path\":\"<path>\"}'.");
            }
            if (!material.HasProperty(propertyName))
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PROPERTY_NOT_FOUND", "Material property not found: " + propertyName, "Inspect shader properties with tools run material.info --input '{\"path\":\"<path>\"}'.");
            }

            var propertyType = MaterialToolSupport.MaterialPropertyType(material, propertyName);
            var normalizedType = MaterialToolSupport.NormalizeMaterialValueType(string.IsNullOrEmpty(requestedType) ? propertyType : requestedType);
            if (string.IsNullOrEmpty(normalizedType))
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PROPERTY_TYPE_UNSUPPORTED", "Unsupported Material property type for " + propertyName + ": " + propertyType, "Pass --type color|float|vector|texture when the shader property cannot be inferred.");
            }
            if (!MaterialToolSupport.TypesCompatible(propertyType, normalizedType))
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PROPERTY_TYPE_MISMATCH", "Material property " + propertyName + " is " + propertyType + ", not " + requestedType + ".", "Use the property type reported by tools run material.info --input '{\"path\":\"<path>\"}'.");
            }

            var appliedValue = MaterialToolSupport.ApplyMaterialValue(material, propertyName, normalizedType, value);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return new Result
            {
                Material = MaterialToolSupport.BuildMaterialInfo(path, material, null),
                Property = propertyName,
                PropertyType = propertyType,
                AppliedValue = appliedValue,
                PlannedEffects = new List<MaterialPlannedEffect> { new MaterialPlannedEffect { Type = "material.set", Path = path, Property = propertyName } }
            };
        }
    }
}
#endif
