#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// material.set-renderer-color — 通过 MaterialPropertyBlock 设置 Renderer 实例颜色,不改共享 Material。
    /// </summary>
    public class MaterialSetRendererColorTool : IUnityCliTool<MaterialSetRendererColorTool.Request, MaterialSetRendererColorTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Name { get; set; }
            public string Property { get; set; }
            public string Color { get; set; }
            public string Value { get; set; }
            public int? Slot { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool? DryRun { get; set; }
            public GameObjectRef Target { get; set; }
            public string Property { get; set; }
            public long? Slot { get; set; }
            public MaterialColor Color { get; set; }
            public List<MaterialPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "material.set-renderer-color",
            Summary = "Set a Renderer instance color via MaterialPropertyBlock without changing the shared material; supports --dry-run.",
            Description = "Set a Renderer instance color through a MaterialPropertyBlock, leaving the shared Material asset untouched. Target must have a Renderer. property defaults to _Color; slot optionally targets a sub-material. Supports --dry-run to preview the planned color.",
            Group = "material",
            Version = "1.0.0",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "scene-modify", "renderer-property-block" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Tint a renderer red", Input = new Request { Target = "Player", Color = "#ff0000ff" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            MaterialToolSupport.EnsureEditorNotBusy("material.set-renderer-color");
            var target = request.Target ?? request.Name;
            var propertyName = request.Property ?? "_Color";
            var colorValue = request.Color ?? request.Value;
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(target, "Material renderer color requires a target GameObject.");
            var renderer = gameObject.GetComponent<Renderer>();
            if (renderer == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_RENDERER_NOT_FOUND", "Target has no Renderer component: " + target, "Choose a GameObject with a Renderer or use material.assign for UI Graphic materials.");
            }
            var color = MaterialToolSupport.ParseColor(colorValue);
            var slot = request.Slot;
            if (string.IsNullOrEmpty(propertyName) || float.IsNaN(color.r) || float.IsNaN(color.g) || float.IsNaN(color.b) || float.IsNaN(color.a) || float.IsInfinity(color.r) || float.IsInfinity(color.g) || float.IsInfinity(color.b) || float.IsInfinity(color.a))
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_COLOR_INVALID", "Property and color must be finite valid values.", "Pass a non-empty property and finite RGBA color.");
            var materials = renderer.sharedMaterials;
            if (slot.HasValue && (slot.Value < 0 || slot.Value >= materials.Length))
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_SLOT_INVALID", "Renderer material slot is out of range: " + slot.Value, "Pass a slot from 0 through " + (materials.Length - 1) + ".");
            var materialIndex = slot ?? 0;
            if (materials.Length == 0 || materials[materialIndex] == null || !materials[materialIndex].HasProperty(propertyName))
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PROPERTY_NOT_FOUND", "Renderer material does not expose color property: " + propertyName, "Pass a color property supported by the selected material slot.");
            var propertyId = Shader.PropertyToID(propertyName);
            var rendererId = renderer.GetInstanceID();
            var targetId = gameObject.GetInstanceID();
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "material.set-renderer-color",
                    DryRun = true,
                    Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                    Property = propertyName,
                    Color = new MaterialColor { R = color.r, G = color.g, B = color.b, A = color.a },
                    PlannedEffects = new List<MaterialPlannedEffect> { new MaterialPlannedEffect { Type = "material.set-renderer-color", Target = GameObjectToolSupport.HierarchyPath(gameObject), Property = propertyName } }
                });
            }
            var block = new MaterialPropertyBlock();
            if (slot.HasValue) renderer.GetPropertyBlock(block, slot.Value); else renderer.GetPropertyBlock(block);
            ctx.CommitEffect();
            block.SetColor(propertyId, color);
            if (slot.HasValue) renderer.SetPropertyBlock(block, slot.Value); else renderer.SetPropertyBlock(block);
            UnityCliSceneMutation.MarkSceneObjectDirty(renderer);
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            var observedTarget = EditorUtility.InstanceIDToObject(targetId) as GameObject;
            var observedRenderer = EditorUtility.InstanceIDToObject(rendererId) as Renderer;
            var observedBlock = new MaterialPropertyBlock();
            if (observedRenderer == null || observedTarget == null) throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_RENDERER_COLOR_STATE_MISMATCH", "Renderer disappeared before color readback.", "Retry after the Editor is idle.");
            if (slot.HasValue) observedRenderer.GetPropertyBlock(observedBlock, slot.Value); else observedRenderer.GetPropertyBlock(observedBlock);
            var observedColor = observedBlock.GetColor(propertyId);
            if (!Mathf.Approximately(observedColor.r, color.r) || !Mathf.Approximately(observedColor.g, color.g) || !Mathf.Approximately(observedColor.b, color.b) || !Mathf.Approximately(observedColor.a, color.a))
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_RENDERER_COLOR_STATE_MISMATCH", "Renderer color did not match the requested property value.", "Inspect the Renderer property block and retry.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "material.set-renderer-color",
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Property = propertyName,
                Slot = slot,
                Color = new MaterialColor { R = color.r, G = color.g, B = color.b, A = color.a },
                PlannedEffects = new List<MaterialPlannedEffect> { new MaterialPlannedEffect { Type = "material.set-renderer-color", Target = GameObjectToolSupport.HierarchyPath(gameObject), Property = propertyName } }
            });
        }
    }
}
#endif
