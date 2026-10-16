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
    /// </summary>
    public class ComponentSetTool : IUnityCliTool<ComponentSetTool.Request, ComponentSetTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Component { get; set; }
            public int? ComponentIndex { get; set; }
            public string Property { get; set; }
            [UnityCliAnyJson] public object Value { get; set; }
            [UnityCliAnyJson] public Dictionary<string, object> Properties { get; set; }
        }

        public sealed class Result
        {
            public ComponentSummary Component { get; set; }
            public List<ChangedProperty> ChangedProperties { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "component.set",
            Summary = "Set one simple public field/property, or multiple simple fields/properties via properties, on a Component attached to a GameObject.",
            Description = "Set one simple public field/property, or multiple simple fields/properties via properties, on a Component attached to a GameObject. For nested values, object references, arrays, or SerializedObject-style patches, use component.modify after component.get/debug.type.schema.",
            Group = "component",
            DestructiveHint = false,
            SideEffects = new[] { "component-modify", "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set multiple Rigidbody values", Input = new Request { Target = "Player", Component = "Rigidbody", Properties = new Dictionary<string, object> { { "mass", 5L }, { "useGravity", false } } } },
                new ToolExample { Description = "Set one boolean value", Input = new Request { Target = "Main Camera", Component = "Camera", Property = "enabled", Value = false } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("component.set");
            var gameObject = ComponentAddTool.ResolveTarget(request.Target, "Component set requires a target.");
            var componentName = ComponentAddTool.RequireComponentName(request.Component);
            var component = GameObjectToolSupport.ResolveComponent(gameObject, componentName, request.ComponentIndex);
            if (component == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + componentName, "Run unitycli tools run component.get or tools run scene.gameobject.components to list components.");
            }

            if (string.IsNullOrEmpty(request.Property) && (request.Properties == null || request.Properties.Count == 0))
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_PROPERTY_REQUIRED", "component.set requires property/value or a properties object.", "Pass --input '{\"target\":\"Player\",\"component\":\"Rigidbody\",\"properties\":{\"mass\":5}}'.");
            }

            var propertyChanges = new List<ComponentPropertyChange>();
            if (!string.IsNullOrEmpty(request.Property))
            {
                if (request.Value == null)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_VALUE_REQUIRED", "component.set requires a value when property is provided.", "Pass value as --input JSON so booleans/numbers keep their type.");
                }
                propertyChanges.Add(GameObjectToolSupport.BuildComponentPropertyChange(component.GetType(), request.Property, request.Value));
            }
            propertyChanges.AddRange(GameObjectToolSupport.BuildComponentPropertyChanges(component.GetType(), request.Properties));

            ctx.CommitEffect();
            UnityCliSceneMutation.RecordObject(component, "component.set");
            GameObjectToolSupport.ApplyComponentPropertyChanges(component, propertyChanges);
            UnityCliSceneMutation.MarkSceneObjectDirty(component);
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            var observedComponent = ComponentAddTool.VerifyComponentReadback(gameObject.GetInstanceID(), component.GetInstanceID(), component.GetType(), propertyChanges);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Component = GameObjectToolSupport.BuildComponentSummary(observedComponent),
                ChangedProperties = ComponentAddTool.BuildChangedProperties(propertyChanges),
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "component.set", Target = GameObjectToolSupport.HierarchyPath(gameObject), Component = component.GetType().Name } }
            });
        }
    }
}
#endif
