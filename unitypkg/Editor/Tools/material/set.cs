#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// material.set — 设置 Material 资产属性(color/float/vector/texture)。session-affecting(asset-write)。
    /// </summary>
    public class MaterialSetTool : IUnityCliTool<MaterialSetTool.Request, MaterialSetTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Path { get; set; }
            [UnityCliRequired] public string Property { get; set; }
            [UnityCliRequired] public string Value { get; set; }
            public string Type { get; set; }
        }

        public sealed class Result
        {
            public MaterialInfo Material { get; set; }
            public string Property { get; set; }
            public string PropertyType { get; set; }
            [UnityCliAnyJson] public object AppliedValue { get; set; }
            public List<MaterialPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "material.set",
            Summary = "Set a Material asset property (color, float, vector or texture) by shader property name.",
            Description = "Set a Material asset property by shader property name. Pass the property value as value; the value type is inferred from the shader property when possible or overridden with type. Inspect properties with material.info first.",
            Group = "material",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set the base color", Input = new Request { Path = "Assets/Materials/Probe.mat", Property = "_Color", Value = "#ff0000ff", Type = "color" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            MaterialToolSupport.EnsureEditorNotBusy("material.set");
            var path = MaterialToolSupport.RequireMaterialPath(request.Path);
            var propertyName = request.Property;
            var value = request.Value;
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
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Material = MaterialToolSupport.BuildMaterialInfo(path, material, null),
                Property = propertyName,
                PropertyType = propertyType,
                AppliedValue = appliedValue,
                PlannedEffects = new List<MaterialPlannedEffect> { new MaterialPlannedEffect { Type = "material.set", Path = path, Property = propertyName } }
            });
        }
    }
}
#endif
