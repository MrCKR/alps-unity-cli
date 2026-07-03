#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// component.add — 向 GameObject 添加 Component,可在创建后一次性设置简单公开字段/属性。
    /// 对齐旧 ComponentAdd handler;session-affecting。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ComponentAddTool : IUnityCliTool<ComponentAddTool.Request, ComponentAddTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Component { get; set; }
            public string Properties { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public ComponentSummary Component { get; set; }
            public List<ChangedProperty> ChangedProperties { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "component.add",
            Summary = "Add a Unity Component to a GameObject, optionally setting simple public fields/properties from a properties object after the component is created.",
            Description = "Add a Unity Component to a GameObject, optionally setting simple public fields/properties from a properties object after the component is created. Use component.list-types first when the exact type name is unclear.",
            Group = "component",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "component-add", "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Add Rigidbody with initial properties", Input = new Request { Target = "Player", Component = "Rigidbody", Properties = "{\"mass\":2,\"useGravity\":false}" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("component.add");
            var gameObject = ResolveTarget(request.Target, "Component add requires a target.");
            var componentName = RequireComponentName(request.Component);
            var type = GameObjectToolSupport.ResolveComponentType(componentName);
            if (!GameObjectToolSupport.AllowsMultipleComponents(type) && gameObject.GetComponent(type) != null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_EXISTS", "Component already exists on target: " + componentName, "Use component.set or component.modify to update existing component properties.");
            }
            var propertyChanges = GameObjectToolSupport.BuildComponentPropertyChanges(type, GameObjectToolSupport.ComponentPropertiesJson(request.Properties));
            var component = Undo.AddComponent(gameObject, type);
            if (component == null || !type.IsInstanceOfType(component))
            {
                if (!object.ReferenceEquals(component, null)) Undo.DestroyObjectImmediate(component);
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_ADD_FAILED", "Unity did not add component type: " + componentName, "The type may be restricted by Unity on this GameObject. Run component.get to inspect existing components, then choose a different component type or update the existing component.");
            }
            GameObjectToolSupport.ApplyComponentPropertyChanges(component, propertyChanges);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Component = GameObjectToolSupport.BuildComponentSummary(component),
                ChangedProperties = BuildChangedProperties(propertyChanges),
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "component.add", Target = GameObjectToolSupport.HierarchyPath(gameObject), Component = component.GetType().Name } }
            };
        }

        internal static GameObject ResolveTarget(string target, string message)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", message, "Pass a GameObject name, hierarchy path or instance id.");
            }
            return GameObjectToolSupport.ResolveUniqueTarget(target, message);
        }

        internal static string RequireComponentName(string component)
        {
            if (string.IsNullOrEmpty(component))
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_TYPE_REQUIRED", "Component command requires --component <type>.", "Pass a Unity component type name, such as BoxCollider or UnityEngine.Rigidbody.");
            }
            return component;
        }

        internal static List<ChangedProperty> BuildChangedProperties(List<ComponentPropertyChange> changes)
        {
            var result = new List<ChangedProperty>();
            foreach (var change in changes ?? new List<ComponentPropertyChange>())
            {
                result.Add(new ChangedProperty { Name = change.Name, Type = change.ValueType.Name, Value = GameObjectToolSupport.ParseJsonToObject(change.RawJson) });
            }
            return result;
        }
    }
}
#endif
