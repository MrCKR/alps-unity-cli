#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// material.assign — 把 Material 资产分配到 GameObject 的 Renderer(指定 slot)或 UI Graphic。
    /// </summary>
    public class MaterialAssignTool : IUnityCliTool<MaterialAssignTool.Request, MaterialAssignTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Name { get; set; }
            public string Material { get; set; }
            public string Path { get; set; }
            public int? Slot { get; set; }
            public int? MaterialIndex { get; set; }
        }

        public sealed class Result
        {
            public MaterialAssignTarget Target { get; set; }
            public MaterialRef Material { get; set; }
            public string ComponentType { get; set; }
            public long Slot { get; set; }
            public List<MaterialPlannedEffect> PlannedEffects { get; set; }
        }

        public sealed class MaterialRef
        {
            public string Path { get; set; }
            public string Guid { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "material.assign",
            Summary = "Assign an Assets/*.mat to a GameObject's Renderer (by slot) or UI Graphic component.",
            Description = "Assign an Assets/*.mat to a GameObject. Assigns to a Renderer's sharedMaterials slot (default 0) when present; otherwise assigns to a UnityEngine.UI.Graphic component's material property. Target resolves by name, hierarchy path, or instance id.",
            Group = "material",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Assign a material to slot 0", Input = new Request { Target = "Player", Material = "Assets/Materials/Player.mat" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            MaterialToolSupport.EnsureEditorNotBusy("material.assign");
            var target = request.Target ?? request.Name;
            var materialPath = MaterialToolSupport.RequireMaterialPath(request.Material ?? request.Path);
            var material = MaterialToolSupport.LoadMaterialAsset(materialPath);
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(target, "Material assign requires a target GameObject.");

            var renderer = gameObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                var slot = Math.Max(0, request.Slot ?? request.MaterialIndex ?? 0);
                var materials = renderer.sharedMaterials;
                if (slot >= materials.Length)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_SLOT_INVALID", "Renderer material slot is out of range: " + slot.ToString(CultureInfo.InvariantCulture), "Pass --slot between 0 and " + Math.Max(0, materials.Length - 1).ToString(CultureInfo.InvariantCulture) + ".");
                }
                UnityCliSceneMutation.RecordObject(renderer, "Assign material");
                materials[slot] = material;
                renderer.sharedMaterials = materials;
                UnityCliSceneMutation.MarkSceneObjectDirty(renderer);
                UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Target = BuildTarget(gameObject),
                    Material = new MaterialRef { Path = materialPath, Guid = AssetDatabase.AssetPathToGUID(materialPath) },
                    ComponentType = "Renderer",
                    Slot = slot,
                    PlannedEffects = new List<MaterialPlannedEffect> { new MaterialPlannedEffect { Type = "material.assign", Target = GameObjectToolSupport.HierarchyPath(gameObject), Path = materialPath } }
                });
            }

            var graphic = MaterialToolSupport.FindUiGraphic(gameObject);
            if (graphic != null)
            {
                var property = graphic.GetType().GetProperty("material", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || !property.CanWrite)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_ASSIGN_UNSUPPORTED", "UI Graphic material property is not writable on target: " + target, "Assign to a Renderer or UI Graphic component with a writable material property.");
                }
                UnityCliSceneMutation.RecordObject(graphic, "Assign material");
                property.SetValue(graphic, material, null);
                UnityCliSceneMutation.MarkSceneObjectDirty(graphic);
                UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Target = BuildTarget(gameObject),
                    Material = new MaterialRef { Path = materialPath, Guid = AssetDatabase.AssetPathToGUID(materialPath) },
                    ComponentType = "Graphic",
                    Slot = 0,
                    PlannedEffects = new List<MaterialPlannedEffect> { new MaterialPlannedEffect { Type = "material.assign", Target = GameObjectToolSupport.HierarchyPath(gameObject), Path = materialPath } }
                });
            }

            throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_TARGET_UNSUPPORTED", "Target has no Renderer or UI Graphic component: " + target, "Add a Renderer/UI Graphic component or choose another target.");
        }

        private static MaterialAssignTarget BuildTarget(GameObject gameObject)
        {
            return new MaterialAssignTarget { Id = gameObject.GetInstanceID(), Name = gameObject.name, Path = GameObjectToolSupport.HierarchyPath(gameObject) };
        }
    }
}
#endif
