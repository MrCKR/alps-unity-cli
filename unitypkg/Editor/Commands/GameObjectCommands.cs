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
            RegisterCommand(registry, "go.duplicate", "go", true, GameObjectDuplicate);
            RegisterCommand(registry, "go.move-relative", "go", true, GameObjectMoveRelative);
            RegisterCommand(registry, "go.look-at", "go", true, GameObjectLookAt);
            RegisterCommand(registry, "go.delete", "go", true, GameObjectDelete);
            RegisterCommand(registry, "go.set-parent", "go", true, GameObjectSetParent);
            RegisterCommand(registry, "component.add", "component", true, ComponentAdd);
            RegisterCommand(registry, "component.remove", "component", true, ComponentRemove);
            RegisterCommand(registry, "component.set", "component", true, ComponentSet);
            // 深读写组件：对齐 IvanMurzak gameobject-component-get/modify/list-all
            RegisterCommand(registry, "component.get", "component", false, ComponentGet);
            RegisterCommand(registry, "component.modify", "component", true, ComponentModify);
            RegisterCommand(registry, "component.list-types", "component", false, ComponentListTypes);
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
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)gameObject.GetInstanceID())
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .EndObject()
                .ToString();
        }


        private static string GameObjectInspect(BridgeRequest request)
        {
            var target = RequiredTarget(request, "GameObject inspect requires a target.");
            var gameObject = ResolveUniqueTarget(target, "GameObject inspect requires a target.");
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", GameObjectDetailJson(gameObject))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
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

        private static string GameObjectDuplicate(BridgeRequest request)
        {
            EnsureEditorNotBusy("go.duplicate");
            var source = ResolveUniqueTarget(RequiredTarget(request, "GameObject duplicate requires a target."), "GameObject duplicate requires a target.");
            var duplicate = UnityEngine.Object.Instantiate(source);
            Undo.RegisterCreatedObjectUndo(duplicate, "unity_cli go.duplicate");
            duplicate.name = request.StringArg("newName") ?? request.StringArg("name") ?? (source.name + " Copy");
            var parent = request.StringArg("parent");
            duplicate.transform.SetParent(string.IsNullOrEmpty(parent) ? source.transform.parent : ResolveUniqueTarget(parent, "Parent GameObject is required.").transform, true);
            duplicate.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            EditorSceneManager.MarkSceneDirty(duplicate.scene);
            return SuccessJson(GameObjectMutationJson("go.duplicate", duplicate));
        }

        private static string GameObjectMoveRelative(BridgeRequest request)
        {
            EnsureEditorNotBusy("go.move-relative");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "GameObject move-relative requires a target."), "GameObject move-relative requires a target.");
            Undo.RecordObject(gameObject.transform, "unity_cli go.move-relative");
            var offset = new Vector3(request.FloatArg("offsetX", 0f), request.FloatArg("offsetY", 0f), request.FloatArg("offsetZ", 0f));
            gameObject.transform.position += offset;
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(GameObjectMutationJson("go.move-relative", gameObject));
        }

        private static string GameObjectLookAt(BridgeRequest request)
        {
            EnsureEditorNotBusy("go.look-at");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "GameObject look-at requires a target."), "GameObject look-at requires a target.");
            var lookAtName = request.StringArg("lookAt") ?? request.StringArg("destination");
            var lookAt = ResolveUniqueTarget(lookAtName, "GameObject look-at requires --look-at <target>.");
            Undo.RecordObject(gameObject.transform, "unity_cli go.look-at");
            gameObject.transform.LookAt(lookAt.transform);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(GameObjectMutationJson("go.look-at", gameObject));
        }

        private static string GameObjectDelete(BridgeRequest request)
        {
            EnsureEditorNotBusy("go.delete");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "GameObject delete requires a target."), "GameObject delete requires a target.");
            var targetJson = GameObjectTargetJson(gameObject);
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "go.delete")
                    .Prop("dryRun", true)
                    .PropRaw("target", targetJson)
                    .PropArray("plannedEffects", arr => arr
                        .RawValue(new UnityCliJsonWriter().BeginObject()
                            .Prop("type", "go.delete")
                            .PropRaw("target", targetJson)
                            .Prop("sideEffects", "none")
                            .EndObject().ToString()))
                    .Prop("logPath", LogPath())
                    .EndObject()
                    .ToString());
            }
            var scene = gameObject.scene;
            Undo.DestroyObjectImmediate(gameObject);
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("deleted", targetJson)
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "go.delete")
                        .PropRaw("target", targetJson)
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
        }

        /// <summary>设置 GameObject 父级：对齐 Ivan gameobject-set-parent。</summary>
        private static string GameObjectSetParent(BridgeRequest request)
        {
            EnsureEditorNotBusy("go.set-parent");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "go.set-parent requires a target."), "go.set-parent requires a target.");
            var parentName = request.StringArg("parent");
            var worldPositionStays = request.BoolArg("worldPositionStays", true);
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "go.set-parent").Prop("dryRun", true)
                    .PropRaw("target", GameObjectTargetJson(gameObject))
                    .Prop("parent", parentName ?? "<root>")
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "go.set-parent").Prop("parent", parentName ?? "<root>").EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            Transform newParent = null;
            if (!string.IsNullOrEmpty(parentName))
            {
                var parentGo = ObjectRefResolver.FindGameObjectByPath(parentName);
                if (parentGo == null)
                {
                    throw new BridgeCommandException("E_PARENT_NOT_FOUND", "Parent GameObject not found: " + parentName, "Pass a valid hierarchy path or name.");
                }
                newParent = parentGo.transform;
            }
            Undo.SetTransformParent(gameObject.transform, newParent, "go.set-parent");
            gameObject.transform.SetParent(newParent, worldPositionStays);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "go.set-parent")
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .Prop("parent", parentName ?? "<root>")
                .Prop("worldPositionStays", worldPositionStays)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "go.set-parent").EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
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
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("component", removed)
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "component.remove")
                        .Prop("target", HierarchyPath(gameObject))
                        .Prop("component", componentName)
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
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

        /// <summary>深读单个 Component：输出 fields/props，支持 paths/viewQuery/maxDepth。对齐 Ivan gameobject-component-get。</summary>
        private static string ComponentGet(BridgeRequest request)
        {
            var component = ObjectRefResolver.ResolveComponent(request);
            var options = new ObjectSerializer.Options
            {
                MaxDepth = request.IntArg("maxDepth", 4),
                IncludeFields = request.BoolArg("includeFields", true),
                IncludeProperties = request.BoolArg("includeProperties", true),
                DeepSerialization = request.BoolArg("deepSerialization", true)
            };
            var pathsRaw = request.RawJsonArg("paths");
            if (!string.IsNullOrEmpty(pathsRaw))
            {
                options.Paths = new HashSet<string>();
                foreach (var p in pathsRaw.Trim('[', ']').Split(','))
                {
                    var clean = p.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(clean)) options.Paths.Add(clean);
                }
            }
            var memberJson = ObjectSerializer.Serialize(component, options);
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "component.get");
            w.Prop("typeName", component.GetType().FullName);
            w.Prop("componentInstanceId", component.GetInstanceID());
            w.PropRaw("gameObject", GameObjectTargetJson(component.gameObject));
            w.PropRaw("data", memberJson);
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
        }

        /// <summary>深改单个 Component：支持 pathPatches 与 jsonPatch (Merge Patch)。对齐 Ivan gameobject-component-modify。</summary>
        private static string ComponentModify(BridgeRequest request)
        {
            EnsureEditorNotBusy("component.modify");
            var component = ObjectRefResolver.ResolveComponent(request);
            var pathPatchesJson = request.RawJsonArg("pathPatches");
            var jsonPatchJson = request.RawJsonArg("jsonPatch");
            var componentDiffJson = request.RawJsonArg("componentDiff");
            if (string.IsNullOrEmpty(pathPatchesJson) && string.IsNullOrEmpty(jsonPatchJson) && string.IsNullOrEmpty(componentDiffJson))
            {
                throw new BridgeCommandException("E_COMPONENT_PATCH_REQUIRED",
                    "component.modify requires pathPatches, jsonPatch or componentDiff.",
                    "Pass --input '{\"component\":{\"type\":\"Transform\"},\"pathPatches\":[{\"path\":\"localPosition\",\"value\":{\"x\":1}}]}'.");
            }
            if (request.DryRun)
            {
                var dry = new UnityCliJsonWriter().BeginObject();
                dry.Prop("command", "component.modify");
                dry.Prop("dryRun", true);
                dry.Prop("componentInstanceId", component.GetInstanceID());
                dry.Prop("typeName", component.GetType().FullName);
                dry.PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "component.modify").Prop("target", component.gameObject.name).EndObject());
                dry.Prop("logPath", LogPath());
                return dry.EndObject().ToString();
            }
            // 执行顺序对齐 IvanMurzak: jsonPatch/componentDiff → pathPatches
            ObjectPatch.PatchResult result = null;
            var effectiveMerge = !string.IsNullOrEmpty(jsonPatchJson) ? jsonPatchJson : componentDiffJson;
            if (!string.IsNullOrEmpty(effectiveMerge))
            {
                result = ObjectPatch.ApplyMergePatch(component, effectiveMerge);
            }
            if (!string.IsNullOrEmpty(pathPatchesJson))
            {
                var pathResult = ObjectPatch.ApplyPathPatches(component, pathPatchesJson);
                if (result == null) result = pathResult;
                else { result.ChangedPaths.AddRange(pathResult.ChangedPaths); result.Logs.AddRange(pathResult.Logs); }
            }
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
            var afterJson = ObjectSerializer.Serialize(component, new ObjectSerializer.Options { MaxDepth = request.IntArg("maxDepth", 2), IncludeFields = true, IncludeProperties = true, DeepSerialization = false });
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "component.modify");
            w.Prop("componentInstanceId", component.GetInstanceID());
            w.Prop("typeName", component.GetType().FullName);
            w.PropArray("changedPaths", arr => { foreach (var p in result.ChangedPaths) arr.Value(p); });
            w.PropRaw("updatedData", afterJson);
            w.PropArray("logs", arr => { foreach (var l in result.Logs) arr.Value(l); });
            w.PropRaw("audit", ObjectPatch.ResultToJson(result));
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
        }

        /// <summary>枚举当前项目可用的 Component 类型。对齐 Ivan gameobject-component-list-all。</summary>
        private static string ComponentListTypes(BridgeRequest request)
        {
            var keyword = request.StringArg("keyword") ?? request.StringArg("search");
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", 100), 500));
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var types = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] candidates;
                try { candidates = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { candidates = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in candidates)
                {
                    if (t == null || !typeof(Component).IsAssignableFrom(t)) continue;
                    if (t.IsAbstract) continue;
                    if (!string.IsNullOrEmpty(keyword) && t.FullName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    types.Add(t);
                }
            }
            types.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
            var total = types.Count;
            var page = types.Skip(cursor).Take(pageSize).ToList();
            var nextCursor = cursor + page.Count < total ? (cursor + page.Count).ToString(CultureInfo.InvariantCulture) : null;
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "component.list-types");
            w.Prop("count", (long)total);
            w.PropArray("types", arr =>
            {
                foreach (var t in page)
                {
                    arr.BeginObject()
                        .Prop("fullName", t.FullName)
                        .Prop("name", t.Name)
                        .Prop("assembly", t.Assembly.GetName().Name)
                        .Prop("namespace", t.Namespace)
                        .EndObject();
                }
            });
            w.PropObject("pageInfo", p => p.Prop("pageSize", (long)pageSize).Prop("cursor", (long)cursor).Prop("nextCursor", nextCursor).Prop("total", (long)total));
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
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
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("query", target)
                .Prop("matchCount", (long)resolution.Matches.Count)
                .PropArray("items", arr => { foreach (var item in items) { arr.RawValue(item); } })
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
        }

        private static string HierarchyItemJson(Transform transform)
        {
            var componentNames = new List<string>();
            foreach (var component in transform.GetComponents<Component>())
            {
                componentNames.Add(component != null ? component.GetType().Name : "MissingScript");
            }
            var gameObject = transform.gameObject;
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)gameObject.GetInstanceID())
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .Prop("active", gameObject.activeSelf)
                .Prop("tag", gameObject.tag)
                .Prop("layer", (long)gameObject.layer)
                .Prop("componentCount", (long)componentNames.Count)
                .PropArray("components", arr => { foreach (var name in componentNames) { arr.Value(name); } })
                .EndObject()
                .ToString();
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
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", GameObjectDetailJson(gameObject))
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", type)
                        .Prop("target", HierarchyPath(gameObject))
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }

        private static string GameObjectDetailJson(GameObject gameObject)
        {
            var children = new List<string>();
            foreach (Transform child in gameObject.transform)
            {
                children.Add(GameObjectTargetJson(child.gameObject));
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)gameObject.GetInstanceID())
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .Prop("active", gameObject.activeSelf)
                .Prop("tag", gameObject.tag)
                .Prop("layer", (long)gameObject.layer)
                .PropRaw("transform", TransformJson(gameObject.transform))
                .PropRaw("components", ComponentsJson(gameObject))
                .PropArray("children", arr => { foreach (var child in children) { arr.RawValue(child); } })
                .EndObject()
                .ToString();
        }

        private static string TransformJson(Transform transform)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("position", Vector3Json(transform.position))
                .PropRaw("rotation", Vector3Json(transform.eulerAngles))
                .PropRaw("scale", Vector3Json(transform.localScale))
                .EndObject()
                .ToString();
        }

        private static string ComponentsJson(GameObject gameObject)
        {
            var writer = new UnityCliJsonWriter().BeginArray();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                writer.RawValue(ComponentSummaryJson(component));
            }
            return writer.EndArray().ToString();
        }

        private static string ComponentSummaryJson(Component component)
        {
            if (component == null)
            {
                return new UnityCliJsonWriter().BeginObject()
                    .Prop("type", "MissingScript")
                    .Prop("enabled", false)
                    .EndObject()
                    .ToString();
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("type", component.GetType().Name)
                .Prop("fullName", component.GetType().FullName)
                .Prop("enabled", ComponentEnabled(component))
                .EndObject()
                .ToString();
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
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("component", ComponentSummaryJson(component))
                .Prop("property", propertyName)
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", type)
                        .Prop("target", HierarchyPath(gameObject))
                        .Prop("component", component == null ? null : component.GetType().Name)
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }

        private sealed class Counter
        {
            public int Value;
        }
    }
}
#endif
