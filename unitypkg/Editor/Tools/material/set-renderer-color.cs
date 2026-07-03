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
    /// 对齐旧 MaterialSetRendererColor handler;session-affecting(scene-modify)。支持 dryRun。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
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
            public bool? DryRun { get; set; }
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
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
            if (request.DryRun == true)
            {
                return new Result
                {
                    Command = "material.set-renderer-color",
                    DryRun = true,
                    Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                    Property = propertyName,
                    Color = new MaterialColor { R = color.r, G = color.g, B = color.b, A = color.a },
                    PlannedEffects = new List<MaterialPlannedEffect> { new MaterialPlannedEffect { Type = "material.set-renderer-color", Target = GameObjectToolSupport.HierarchyPath(gameObject), Property = propertyName } }
                };
            }
            var block = new MaterialPropertyBlock();
            if (slot.HasValue) renderer.GetPropertyBlock(block, slot.Value); else renderer.GetPropertyBlock(block);
            block.SetColor(propertyName, color);
            if (slot.HasValue) renderer.SetPropertyBlock(block, slot.Value); else renderer.SetPropertyBlock(block);
            EditorUtility.SetDirty(renderer);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return new Result
            {
                Command = "material.set-renderer-color",
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Property = propertyName,
                Slot = slot,
                Color = new MaterialColor { R = color.r, G = color.g, B = color.b, A = color.a },
                PlannedEffects = new List<MaterialPlannedEffect> { new MaterialPlannedEffect { Type = "material.set-renderer-color", Target = GameObjectToolSupport.HierarchyPath(gameObject), Property = propertyName } }
            };
        }
    }
}
#endif
