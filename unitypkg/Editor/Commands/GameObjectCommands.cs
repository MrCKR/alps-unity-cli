#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterGameObjectCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "go.find", "go", false, GameObjectFind);
            RegisterCommand(registry, "go.inspect", "go", false, GameObjectInspect);
            RegisterCommand(registry, "go.create", "go", true, GameObjectCreate);
            RegisterCommand(registry, "go.modify", "go", true, GameObjectModify);
            RegisterCommand(registry, "go.delete", "go", true, GameObjectDelete);
            RegisterCommand(registry, "component.add", "component", true, ComponentAdd);
            RegisterCommand(registry, "component.remove", "component", true, ComponentRemove);
            RegisterCommand(registry, "component.set", "component", true, ComponentSet);
        }

        private static TargetResolution ResolveGameObjectTarget(string target)
        {
            var matches = new List<GameObject>();
            if (string.IsNullOrEmpty(target))
            {
                return TargetResolution.FromMatches(matches);
            }

            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            return TargetResolution.FromMatches(matches);
        }

        private static GameObject ResolveUniqueTarget(string target, string requiredMessage)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", requiredMessage, "Pass a GameObject name, hierarchy path or instance id.");
            }
            var matches = new List<GameObject>();
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            if (matches.Count > 1)
            {
                throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched: " + target, "Pass a hierarchy path or instance id.");
            }
            if (matches.Count == 0)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "Target not found: " + target, "Run unity_cli scene hierarchy or pass a hierarchy path / instance id.");
            }
            return matches[0];
        }

        private static string GameObjectTargetJson(GameObject gameObject)
        {
            return "{\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Quote(gameObject.name) + ",\"path\":" + Quote(HierarchyPath(gameObject)) + "}";
        }


        private static string GameObjectInspect(BridgeRequest request)
        {
            var target = RequiredTarget(request, "GameObject inspect requires a target.");
            var gameObject = ResolveUniqueTarget(target, "GameObject inspect requires a target.");
            return SuccessJson("{\"gameObject\":" + GameObjectDetailJson(gameObject) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string GameObjectCreate(BridgeRequest request)
        {
            EnsureEditorNotBusy("go.create");
            var name = request.StringArg("name") ?? request.StringArg("target");
            if (string.IsNullOrEmpty(name))
            {
                name = "GameObject";
            }
            GameObject gameObject;
            var primitive = request.StringArg("primitive");
            if (!string.IsNullOrEmpty(primitive))
            {
                gameObject = GameObject.CreatePrimitive(ParsePrimitiveType(primitive));
                gameObject.name = name;
            }
            else
            {
                gameObject = new GameObject(name);
            }
            var parent = request.StringArg("parent");
            if (!string.IsNullOrEmpty(parent))
            {
                gameObject.transform.SetParent(ResolveUniqueTarget(parent, "Parent GameObject is required.").transform, false);
            }
            ApplyGameObjectMutations(gameObject, request);
            Undo.RegisterCreatedObjectUndo(gameObject, "unity_cli go.create");
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(GameObjectMutationJson("go.create", gameObject));
        }

        private static string GameObjectModify(BridgeRequest request)
        {
            EnsureEditorNotBusy("go.modify");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "GameObject modify requires a target."), "GameObject modify requires a target.");
            Undo.RecordObject(gameObject.transform, "unity_cli go.modify");
            Undo.RecordObject(gameObject, "unity_cli go.modify");
            ApplyGameObjectMutations(gameObject, request);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(GameObjectMutationJson("go.modify", gameObject));
        }

        private static string GameObjectDelete(BridgeRequest request)
        {
            EnsureEditorNotBusy("go.delete");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "GameObject delete requires a target."), "GameObject delete requires a target.");
            var targetJson = GameObjectTargetJson(gameObject);
            if (request.DryRun)
            {
                return SuccessJson("{\"command\":\"go.delete\",\"dryRun\":true,\"target\":" + targetJson + ",\"plannedEffects\":[{\"type\":\"go.delete\",\"target\":" + targetJson + ",\"sideEffects\":\"none\"}],\"logPath\":" + Quote(LogPath()) + "}");
            }
            var scene = gameObject.scene;
            Undo.DestroyObjectImmediate(gameObject);
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
            return SuccessJson("{\"deleted\":" + targetJson + ",\"plannedEffects\":[{\"type\":\"go.delete\",\"target\":" + targetJson + "}],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ComponentAdd(BridgeRequest request)
        {
            EnsureEditorNotBusy("component.add");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "Component add requires a target."), "Component add requires a target.");
            var componentName = RequiredComponentName(request);
            if (ResolveComponent(gameObject, componentName) != null)
            {
                throw new BridgeCommandException("E_COMPONENT_EXISTS", "Component already exists on target: " + componentName, "Use component set to modify existing component properties.");
            }
            var type = ResolveComponentType(componentName);
            var component = Undo.AddComponent(gameObject, type);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(ComponentMutationJson("component.add", gameObject, component, null));
        }

        private static string ComponentRemove(BridgeRequest request)
        {
            EnsureEditorNotBusy("component.remove");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "Component remove requires a target."), "Component remove requires a target.");
            var componentName = RequiredComponentName(request);
            var component = ResolveComponent(gameObject, componentName);
            if (component == null)
            {
                throw new BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + componentName, "Run unity_cli go inspect <target> to list components.");
            }
            if (component is Transform)
            {
                throw new BridgeCommandException("E_COMPONENT_REMOVE_UNSUPPORTED", "Transform cannot be removed from a GameObject.", "Remove a non-Transform component.");
            }
            var removed = ComponentSummaryJson(component);
            Undo.DestroyObjectImmediate(component);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson("{\"target\":" + GameObjectTargetJson(gameObject) + ",\"component\":" + removed + ",\"plannedEffects\":[{\"type\":\"component.remove\",\"target\":" + Quote(HierarchyPath(gameObject)) + ",\"component\":" + Quote(componentName) + "}],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ComponentSet(BridgeRequest request)
        {
            EnsureEditorNotBusy("component.set");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "Component set requires a target."), "Component set requires a target.");
            var componentName = RequiredComponentName(request);
            var propertyName = request.StringArg("property");
            if (string.IsNullOrEmpty(propertyName))
            {
                throw new BridgeCommandException("E_COMPONENT_PROPERTY_REQUIRED", "component.set requires --property <name>.", "Pass a writable component property name.");
            }
            var component = ResolveComponent(gameObject, componentName);
            if (component == null)
            {
                throw new BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + componentName, "Run unity_cli go inspect <target> to list components.");
            }
            SetComponentProperty(component, propertyName, request.StringArg("value"));
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(ComponentMutationJson("component.set", gameObject, component, propertyName));
        }

        private static string GameObjectFind(BridgeRequest request)
        {
            var target = request.StringArg("target");
            if (string.IsNullOrEmpty(target))
            {
                target = request.StringArg("name");
            }
            if (string.IsNullOrEmpty(target))
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "GameObject find requires --name or a target.", "Pass unity_cli go find --name <name>, a hierarchy path, or an instance id.");
            }
            var resolution = ResolveGameObjectTarget(target);
            var items = new List<string>();
            foreach (var gameObject in resolution.Matches)
            {
                items.Add(HierarchyItemJson(gameObject.transform));
            }
            return SuccessJson("{\"query\":" + Quote(target) + ",\"matchCount\":" + resolution.Matches.Count.ToString(CultureInfo.InvariantCulture) + ",\"items\":[" + string.Join(",", items.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string HierarchyItemJson(Transform transform)
        {
            var componentNames = new List<string>();
            foreach (var component in transform.GetComponents<Component>())
            {
                componentNames.Add(Quote(component != null ? component.GetType().Name : "MissingScript"));
            }
            var gameObject = transform.gameObject;
            return "{"
                + "\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ","
                + "\"name\":" + Quote(gameObject.name) + ","
                + "\"path\":" + Quote(HierarchyPath(gameObject)) + ","
                + "\"active\":" + Bool(gameObject.activeSelf) + ","
                + "\"tag\":" + Quote(gameObject.tag) + ","
                + "\"layer\":" + gameObject.layer.ToString(CultureInfo.InvariantCulture) + ","
                + "\"componentCount\":" + componentNames.Count.ToString(CultureInfo.InvariantCulture) + ","
                + "\"components\":[" + string.Join(",", componentNames.ToArray()) + "]"
                + "}";
        }

        private static int CountHierarchy(Transform transform)
        {
            var count = 1;
            foreach (Transform child in transform)
            {
                count += CountHierarchy(child);
            }
            return count;
        }

        private static void CollectHierarchy(Transform transform, List<string> items, int cursor, int pageSize, Counter counter)
        {
            if (counter.Value >= cursor && items.Count < pageSize)
            {
                items.Add(HierarchyItemJson(transform));
            }
            counter.Value++;
            foreach (Transform child in transform)
            {
                if (items.Count >= pageSize)
                {
                    return;
                }
                CollectHierarchy(child, items, cursor, pageSize, counter);
            }
        }

        private static bool ComponentEnabled(Component component)
        {
            var property = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            return property == null || (bool)property.GetValue(component, null);
        }

        private static string RequiredTarget(BridgeRequest request, string message)
        {
            var target = request.StringArg("target") ?? request.StringArg("name") ?? request.StringArg("path") ?? request.StringArg("id");
            if (string.IsNullOrEmpty(target))
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", message, "Pass a GameObject name, hierarchy path or instance id.");
            }
            return target;
        }

        private static void ApplyGameObjectMutations(GameObject gameObject, BridgeRequest request)
        {
            var newName = request.StringArg("newName");
            if (!string.IsNullOrEmpty(newName))
            {
                gameObject.name = newName;
            }
            var active = request.OptionalBoolArg("active");
            if (active.HasValue)
            {
                gameObject.SetActive(active.Value);
            }
            var tagValue = request.StringArg("tagValue");
            if (!string.IsNullOrEmpty(tagValue))
            {
                gameObject.tag = tagValue;
            }
            var layerValue = request.StringArg("layerValue");
            if (!string.IsNullOrEmpty(layerValue))
            {
                gameObject.layer = ParseLayer(layerValue);
            }
            gameObject.transform.position = new Vector3(request.FloatArg("x", gameObject.transform.position.x), request.FloatArg("y", gameObject.transform.position.y), request.FloatArg("z", gameObject.transform.position.z));
            gameObject.transform.eulerAngles = new Vector3(request.FloatArg("rotationX", gameObject.transform.eulerAngles.x), request.FloatArg("rotationY", gameObject.transform.eulerAngles.y), request.FloatArg("rotationZ", gameObject.transform.eulerAngles.z));
            gameObject.transform.localScale = new Vector3(request.FloatArg("scaleX", gameObject.transform.localScale.x), request.FloatArg("scaleY", gameObject.transform.localScale.y), request.FloatArg("scaleZ", gameObject.transform.localScale.z));
        }

        private static PrimitiveType ParsePrimitiveType(string value)
        {
            if (string.Equals(value, "sphere", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Sphere;
            if (string.Equals(value, "capsule", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Capsule;
            if (string.Equals(value, "cylinder", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Cylinder;
            if (string.Equals(value, "plane", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Plane;
            if (string.Equals(value, "quad", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Quad;
            return PrimitiveType.Cube;
        }

        private static int ParseLayer(string value)
        {
            int parsed;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            var layer = LayerMask.NameToLayer(value);
            if (layer < 0)
            {
                throw new BridgeCommandException("E_LAYER_NOT_FOUND", "Layer not found: " + value, "Pass a numeric layer index or a configured Unity layer name.");
            }
            return layer;
        }

        private static string GameObjectMutationJson(string type, GameObject gameObject)
        {
            return "{\"gameObject\":" + GameObjectDetailJson(gameObject) + ",\"plannedEffects\":[{\"type\":" + Quote(type) + ",\"target\":" + Quote(HierarchyPath(gameObject)) + "}],\"logPath\":" + Quote(LogPath()) + "}";
        }

        private static string GameObjectDetailJson(GameObject gameObject)
        {
            var children = new List<string>();
            foreach (Transform child in gameObject.transform)
            {
                children.Add(GameObjectTargetJson(child.gameObject));
            }
            return "{"
                + "\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ","
                + "\"name\":" + Quote(gameObject.name) + ","
                + "\"path\":" + Quote(HierarchyPath(gameObject)) + ","
                + "\"active\":" + Bool(gameObject.activeSelf) + ","
                + "\"tag\":" + Quote(gameObject.tag) + ","
                + "\"layer\":" + gameObject.layer.ToString(CultureInfo.InvariantCulture) + ","
                + "\"transform\":" + TransformJson(gameObject.transform) + ","
                + "\"components\":" + ComponentsJson(gameObject) + ","
                + "\"children\":[" + string.Join(",", children.ToArray()) + "]"
                + "}";
        }

        private static string TransformJson(Transform transform)
        {
            return "{\"position\":" + Vector3Json(transform.position) + ",\"rotation\":" + Vector3Json(transform.eulerAngles) + ",\"scale\":" + Vector3Json(transform.localScale) + "}";
        }

        private static string ComponentsJson(GameObject gameObject)
        {
            var items = new List<string>();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                items.Add(ComponentSummaryJson(component));
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string ComponentSummaryJson(Component component)
        {
            if (component == null)
            {
                return "{\"type\":\"MissingScript\",\"enabled\":false}";
            }
            return "{\"type\":" + Quote(component.GetType().Name) + ",\"fullName\":" + Quote(component.GetType().FullName) + ",\"enabled\":" + Bool(ComponentEnabled(component)) + "}";
        }

        private static string RequiredComponentName(BridgeRequest request)
        {
            var componentName = request.StringArg("component");
            if (string.IsNullOrEmpty(componentName))
            {
                throw new BridgeCommandException("E_COMPONENT_TYPE_REQUIRED", "Component command requires --component <type>.", "Pass a Unity component type name, such as BoxCollider or UnityEngine.Rigidbody.");
            }
            return componentName;
        }

        private static Type ResolveComponentType(string componentName)
        {
            var type = FindType(componentName) ?? FindType("UnityEngine." + componentName) ?? FindType("UnityEngine.UI." + componentName);
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] candidates;
                    try
                    {
                        candidates = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException exception)
                    {
                        candidates = exception.Types.Where(candidate => candidate != null).ToArray();
                    }
                    foreach (var candidate in candidates)
                    {
                        if (string.Equals(candidate.Name, componentName, StringComparison.Ordinal))
                        {
                            type = candidate;
                            break;
                        }
                    }
                    if (type != null)
                    {
                        break;
                    }
                }
            }
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                throw new BridgeCommandException("E_COMPONENT_TYPE_NOT_FOUND", "Component type not found: " + componentName, "Pass a full component type name or a UnityEngine component name.");
            }
            return type;
        }

        private static Component ResolveComponent(GameObject gameObject, string componentName)
        {
            var type = ResolveComponentType(componentName);
            return gameObject.GetComponent(type);
        }

        private static void SetComponentProperty(Component component, string propertyName, string value)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            var property = component.GetType().GetProperty(propertyName, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(component, ConvertComponentValue(value, property.PropertyType), null);
                return;
            }
            var field = component.GetType().GetField(propertyName, flags);
            if (field != null)
            {
                field.SetValue(component, ConvertComponentValue(value, field.FieldType));
                return;
            }
            throw new BridgeCommandException("E_COMPONENT_PROPERTY_NOT_FOUND", "Writable component property not found: " + propertyName, "Use a public writable field or property name.");
        }

        private static object ConvertComponentValue(string value, Type type)
        {
            if (type == typeof(string)) return value ?? string.Empty;
            if (type == typeof(bool)) return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
            if (type == typeof(int)) return int.Parse(value ?? "0", CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(value ?? "0", CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(value ?? "0", CultureInfo.InvariantCulture);
            if (type == typeof(Vector2)) return ParseVector2(value);
            if (type == typeof(Vector3)) return ParseVector3(value);
            if (type.IsEnum) return Enum.Parse(type, value ?? string.Empty, true);
            throw new BridgeCommandException("E_COMPONENT_PROPERTY_TYPE_UNSUPPORTED", "Unsupported component property type: " + type.Name, "Supported property types: string, bool, int, float, double, enum, Vector2, Vector3.");
        }

        private static Vector2 ParseVector2(string value)
        {
            var parts = (value ?? string.Empty).Split(',');
            return new Vector2(parts.Length > 0 ? ParseFloat(parts[0], "x") : 0f, parts.Length > 1 ? ParseFloat(parts[1], "y") : 0f);
        }

        private static Vector3 ParseVector3(string value)
        {
            var parts = (value ?? string.Empty).Split(',');
            return new Vector3(parts.Length > 0 ? ParseFloat(parts[0], "x") : 0f, parts.Length > 1 ? ParseFloat(parts[1], "y") : 0f, parts.Length > 2 ? ParseFloat(parts[2], "z") : 0f);
        }

        private static string ComponentMutationJson(string type, GameObject gameObject, Component component, string propertyName)
        {
            return "{\"target\":" + GameObjectTargetJson(gameObject) + ",\"component\":" + ComponentSummaryJson(component) + ",\"property\":" + Quote(propertyName) + ",\"plannedEffects\":[{\"type\":" + Quote(type) + ",\"target\":" + Quote(HierarchyPath(gameObject)) + ",\"component\":" + Quote(component == null ? null : component.GetType().Name) + "}],\"logPath\":" + Quote(LogPath()) + "}";
        }

        private sealed class Counter
        {
            public int Value;
        }
    }
}
#endif
