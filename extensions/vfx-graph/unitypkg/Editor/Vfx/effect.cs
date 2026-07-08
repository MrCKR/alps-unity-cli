#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// vfx.effect - 管理场景中的 VisualEffect 组件与图资产绑定。
    /// </summary>
    public class VfxEffectTool : IUnityCliTool<VfxEffectTool.Request, VfxEffectTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Target { get; set; }
            public string Graph { get; set; }
            public bool? Add { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public VfxEffectInfo Effect { get; set; }
            public List<VfxPlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.effect",
            Summary = "Add or inspect a VisualEffect component on a target GameObject and optionally assign a VFX Graph asset.",
            Description = "Inspect the VisualEffect component on a target GameObject, optionally add the component when add=true, and optionally assign a VFX Graph asset through the reflected visualEffectAsset property.",
            Group = "vfx",
            Version = "1.0.0",
            DestructiveHint = true,
            RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "scene-object-edit", "component-add", "component-reference-edit" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Inspect an existing effect", Input = new Request { Target = "Effects/Emitter" } },
                new ToolExample { Description = "Add component and assign graph", Input = new Request { Target = "Effects/Emitter", Add = true, Graph = "Assets/VFX/Explosion.vfx" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            VfxToolSupport.EnsureEditorReady("vfx.effect");
            var visualEffectType = VfxToolSupport.RequireVisualEffectType();
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "VFX effect requires target.");
            var component = gameObject.GetComponent(visualEffectType);
            var effects = new List<VfxPlannedEffect>();
            if (component == null && request.Add == true)
            {
                component = gameObject.AddComponent(visualEffectType);
                effects.Add(new VfxPlannedEffect { Type = "vfx.effect.add", Target = GameObjectToolSupport.HierarchyPath(gameObject) });
            }
            if (component == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_VFX_COMPONENT_NOT_FOUND",
                    "VisualEffect component not found on target: " + GameObjectToolSupport.HierarchyPath(gameObject),
                    "Pass add=true to add one or choose a target with VisualEffect.");
            }

            if (!string.IsNullOrWhiteSpace(request.Graph))
            {
                var graphPath = AssetToolSupport.NormalizeAssetPath(request.Graph);
                var graph = AssetDatabase.LoadAssetAtPath(graphPath, VfxToolSupport.RequireVfxAssetType());
                if (graph == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_VFX_GRAPH_NOT_FOUND",
                        "VFX Graph asset not found: " + request.Graph,
                        "Pass an existing Assets/*.vfx path or create one with unity_cli tools run vfx.graph --input '{\"path\":\"Assets/VFX/Effect.vfx\",\"action\":\"create\"}'.");
                }

                var property = visualEffectType.GetProperty("visualEffectAsset", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || !property.CanWrite)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_VFX_ASSIGN_UNSUPPORTED",
                        "VisualEffect asset property is not writable in this Unity version.",
                        "Assign the VFX Graph in Unity or upgrade Visual Effect Graph package.");
                }

                property.SetValue(component, graph, null);
                effects.Add(new VfxPlannedEffect { Type = "vfx.effect.assign", Target = GameObjectToolSupport.HierarchyPath(gameObject), Graph = graphPath });
            }

            EditorUtility.SetDirty(component);
            VfxToolSupport.MarkSceneDirty(gameObject);
            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Effect = VfxToolSupport.BuildEffectInfo(component),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
