#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// component.set — 设置 Component 的一个或多个简单公开字段/属性。
    /// 对齐旧 ComponentSet handler;session-affecting。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ComponentSetTool : IUnityCliTool<ComponentSetTool.Request, ComponentSetTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Component { get; set; }
            public int? ComponentIndex { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
            public string Properties { get; set; }
        }

        public sealed class Result
        {
            public GameObjectRef Target { get; set; }
            public ComponentSummary Component { get; set; }
            public string Property { get; set; }
            public string ComponentIndex { get; set; }
            public List<ChangedProperty> ChangedProperties { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "component.set",
            Summary = "Set one simple public field/property, or multiple simple fields/properties via properties, on a Component attached to a GameObject.",
            Description = "Set one simple public field/property, or multiple simple fields/properties via properties, on a Component attached to a GameObject. For nested values, object references, arrays, or SerializedObject-style patches, use component.modify after component.get/type.schema.",
            Group = "component",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "component-modify", "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set multiple Rigidbody values", Input = new Request { Target = "Player", Component = "Rigidbody", Properties = "{\"mass\":5,\"useGravity\":false}" } },
                new ToolExample { Description = "Set one boolean value", Input = new Request { Target = "Main Camera", Component = "Camera", Property = "enabled", Value = "false" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("component.set");
            var gameObject = ComponentAddTool.ResolveTarget(request.Target, "Component set requires a target.");
            var componentName = ComponentAddTool.RequireComponentName(request.Component);
            var component = GameObjectToolSupport.ResolveComponent(gameObject, componentName, request.ComponentIndex);
            if (component == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + componentName, "Run unity_cli tools run component.get or context read scene.gameobject.components to list components.");
            }

            if (string.IsNullOrEmpty(request.Property) && string.IsNullOrEmpty(request.Properties))
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_PROPERTY_REQUIRED", "component.set requires property/value or a properties object.", "Pass --input '{\"target\":\"Player\",\"component\":\"Rigidbody\",\"properties\":{\"mass\":5}}'.");
            }

            var propertyChanges = new List<ComponentPropertyChange>();
            if (!string.IsNullOrEmpty(request.Property))
            {
                if (string.IsNullOrEmpty(request.Value))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_VALUE_REQUIRED", "component.set requires a value when property is provided.", "Pass value as --input JSON so booleans/numbers keep their type.");
                }
                propertyChanges.Add(GameObjectToolSupport.BuildComponentPropertyChange(component.GetType(), request.Property, request.Value));
            }
            propertyChanges.AddRange(GameObjectToolSupport.BuildComponentPropertyChanges(component.GetType(), GameObjectToolSupport.ComponentPropertiesJson(request.Properties)));

            Undo.RecordObject(component, "unity_cli component.set");
            GameObjectToolSupport.ApplyComponentPropertyChanges(component, propertyChanges);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Component = GameObjectToolSupport.BuildComponentSummary(component),
                Property = request.Property,
                ComponentIndex = request.ComponentIndex.HasValue ? request.ComponentIndex.Value.ToString(CultureInfo.InvariantCulture) : null,
                ChangedProperties = ComponentAddTool.BuildChangedProperties(propertyChanges),
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "component.set", Target = GameObjectToolSupport.HierarchyPath(gameObject), Component = component.GetType().Name } }
            };
        }
    }
}
#endif
