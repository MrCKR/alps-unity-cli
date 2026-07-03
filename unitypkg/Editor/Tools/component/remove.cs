#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// component.remove — 从 GameObject 删除一个或多个非 Transform Component,支持 componentIndex 精确选择。
    /// 对齐旧 ComponentRemove handler;session-affecting。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ComponentRemoveTool : IUnityCliTool<ComponentRemoveTool.Request, ComponentRemoveTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Component { get; set; }
            public List<string> Components { get; set; }
            public int? ComponentIndex { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public ComponentSummary Component { get; set; }
            public List<ComponentSummary> Components { get; set; }
            public long RemovedCount { get; set; }
            public string ComponentIndex { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "component.remove",
            Summary = "Remove one or more non-Transform Components from a GameObject.",
            Description = "Remove one or more non-Transform Components from a GameObject in the current scene/session. Use component for one type and componentIndex when multiple components of the same type exist; use components as a string array to remove multiple different component types in one call.",
            Group = "component",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "component-remove", "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Remove first BoxCollider", Input = new Request { Target = "Player", Component = "BoxCollider", ComponentIndex = 0 } },
                new ToolExample { Description = "Remove multiple component types", Input = new Request { Target = "Player", Components = new List<string> { "BoxCollider", "Rigidbody" } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("component.remove");
            var gameObject = ComponentAddTool.ResolveTarget(request.Target, "Component remove requires a target.");
            var targetRef = GameObjectToolSupport.BuildGameObjectRef(gameObject);

            // components[] 批量删除模式
            if (request.Components != null && request.Components.Count > 0)
            {
                var targets = new List<Component>();
                foreach (var name in request.Components)
                {
                    var component = GameObjectToolSupport.ResolveComponent(gameObject, name, null);
                    if (component == null)
                    {
                        throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + name, "Run unity_cli tools run component.get or context read scene.gameobject.components to list components.");
                    }
                    EnsureNotTransform(component, name);
                    targets.Add(component);
                }
                var removed = new List<ComponentSummary>();
                foreach (var component in targets)
                {
                    removed.Add(GameObjectToolSupport.BuildComponentSummary(component));
                    Undo.DestroyObjectImmediate(component);
                }
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
                var effects = new List<GameObjectPlannedEffect>();
                foreach (var name in request.Components)
                {
                    effects.Add(new GameObjectPlannedEffect { Type = "component.remove", Target = GameObjectToolSupport.HierarchyPath(gameObject), Component = name });
                }
                return new Result { Target = targetRef, Components = removed, RemovedCount = removed.Count, PlannedEffects = effects };
            }

            // 单组件删除模式
            var componentName = ComponentAddTool.RequireComponentName(request.Component);
            var singleComponent = GameObjectToolSupport.ResolveComponent(gameObject, componentName, request.ComponentIndex);
            if (singleComponent == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + componentName, "Run unity_cli tools run component.get or context read scene.gameobject.components to list components.");
            }
            EnsureNotTransform(singleComponent, componentName);
            var singleRemoved = GameObjectToolSupport.BuildComponentSummary(singleComponent);
            Undo.DestroyObjectImmediate(singleComponent);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return new Result
            {
                Target = targetRef,
                Component = singleRemoved,
                ComponentIndex = request.ComponentIndex.HasValue ? request.ComponentIndex.Value.ToString(CultureInfo.InvariantCulture) : null,
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "component.remove", Target = GameObjectToolSupport.HierarchyPath(gameObject), Component = componentName } }
            };
        }

        private static void EnsureNotTransform(Component component, string name)
        {
            if (component is Transform)
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_REMOVE_UNSUPPORTED", "Transform cannot be removed from a GameObject.", "Remove a non-Transform component.");
            }
        }
    }
}
#endif
