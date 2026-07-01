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
            RegisterCommand(registry, "go.delete", "go", false, GameObjectDelete);
            RegisterCommand(registry, "go.set-parent", "go", false, GameObjectSetParent);
            RegisterCommand(registry, "component.add", "component", true, ComponentAdd);
            RegisterCommand(registry, "component.remove", "component", false, ComponentRemove);
            RegisterCommand(registry, "component.set", "component", true, ComponentSet);
            // 深读写组件：对齐 IvanMurzak gameobject-component-get/modify/list-all
            RegisterCommand(registry, "component.get", "component", false, ComponentGet);
            RegisterCommand(registry, "component.modify", "component", false, ComponentModify);
            RegisterCommand(registry, "component.list-types", "component", false, ComponentListTypes);
        }

        private static TargetResolution ResolveGameObjectTarget(string target)
        {
            var matches = new List<GameObject>();
            if (string.IsNullOrEmpty(target))
            {
                return TargetResolution.FromMatches(matches);
            }

            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
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
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
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
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "Target not found: " + target, "Run unity_cli tools run scene.hierarchy or pass a hierarchy path / instance id.");
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
            var parent = request.StringArg("parent");
            var parentTransform = string.IsNullOrEmpty(parent) ? null : ResolveUniqueTarget(parent, "Parent GameObject is required.").transform;
            var primitive = request.StringArg("primitive");
            var primitiveType = string.IsNullOrEmpty(primitive) ? (PrimitiveType?)null : ParsePrimitiveType(primitive);
            ValidateGameObjectMutationInputs(request);

            GameObject gameObject;
            if (primitiveType.HasValue)
            {
                gameObject = GameObject.CreatePrimitive(primitiveType.Value);
                gameObject.name = name;
            }
            else
            {
                gameObject = new GameObject(name);
            }
            if (parentTransform != null)
            {
                gameObject.transform.SetParent(parentTransform, false);
            }
            ApplyGameObjectMutations(gameObject, request);
            Undo.RegisterCreatedObjectUndo(gameObject, "unity_cli go.create");
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(GameObjectMutationJson("go.create", gameObject));
        }

        private static string GameObjectModify(BridgeRequest request)
        {
            EnsureEditorNotBusy("go.modify");
            ValidateGameObjectMutationInputs(request);
            var targets = ResolveModifyTargets(request);
            if (targets.Count == 1)
            {
                var gameObject = targets[0];
                Undo.RecordObject(gameObject.transform, "unity_cli go.modify");
                Undo.RecordObject(gameObject, "unity_cli go.modify");
                ApplyGameObjectMutations(gameObject, request);
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
                return SuccessJson(GameObjectMutationJson("go.modify", gameObject));
            }

            foreach (var gameObject in targets)
            {
                Undo.RecordObject(gameObject.transform, "unity_cli go.modify batch");
                Undo.RecordObject(gameObject, "unity_cli go.modify batch");
            }
            foreach (var gameObject in targets)
            {
                ApplyGameObjectMutations(gameObject, request);
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
            return SuccessJson(GameObjectBatchMutationJson("go.modify", targets));
        }

        private static string GameObjectDuplicate(BridgeRequest request)
        {
            EnsureEditorNotBusy("go.duplicate");
            var source = ResolveUniqueTarget(RequiredTarget(request, "GameObject duplicate requires a target."), "GameObject duplicate requires a target.");
            var parent = request.StringArg("parent");
            var parentTransform = string.IsNullOrEmpty(parent) ? source.transform.parent : ResolveUniqueTarget(parent, "Parent GameObject is required.").transform;
            var duplicate = UnityEngine.Object.Instantiate(source);
            Undo.RegisterCreatedObjectUndo(duplicate, "unity_cli go.duplicate");
            duplicate.name = request.StringArg("newName") ?? request.StringArg("name") ?? (source.name + " Copy");
            duplicate.transform.SetParent(parentTransform, true);
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
            var lookAt = ResolveUniqueTarget(lookAtName, "GameObject look-at requires a lookAt target. Use unity_cli tools run go.look-at --input '{\"target\":\"CameraRig\",\"lookAt\":\"Player\"}'.");
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
            var newParent = string.IsNullOrEmpty(parentName) ? null : ResolveUniqueTarget(parentName, "Parent GameObject is required.").transform;
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "go.set-parent").Prop("dryRun", true)
                    .PropRaw("target", GameObjectTargetJson(gameObject))
                    .Prop("parent", parentName ?? "<root>")
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "go.set-parent").Prop("parent", parentName ?? "<root>").EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
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

        /// <summary>添加 Component，并可在创建后一次性设置简单公开字段/属性。</summary>
        private static string ComponentAdd(BridgeRequest request)
        {
            EnsureEditorNotBusy("component.add");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "Component add requires a target."), "Component add requires a target.");
            var componentName = RequiredComponentName(request);
            var type = ResolveComponentType(componentName);
            if (!AllowsMultipleComponents(type) && gameObject.GetComponent(type) != null)
            {
                throw new BridgeCommandException("E_COMPONENT_EXISTS", "Component already exists on target: " + componentName, "Use component.set or component.modify to update existing component properties.");
            }
            var propertyChanges = BuildComponentPropertyChanges(type, ComponentPropertiesJson(request));
            var component = Undo.AddComponent(gameObject, type);
            if (component == null || !type.IsInstanceOfType(component))
            {
                if (!object.ReferenceEquals(component, null))
                {
                    Undo.DestroyObjectImmediate(component);
                }
                throw new BridgeCommandException("E_COMPONENT_ADD_FAILED", "Unity did not add component type: " + componentName, "The type may be restricted by Unity on this GameObject. Run component.get to inspect existing components, then choose a different component type or update the existing component.");
            }
            ApplyComponentPropertyChanges(component, propertyChanges);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(ComponentMutationJson("component.add", gameObject, component, null, propertyChanges, null));
        }

        /// <summary>删除目标 GameObject 上的 Component，支持 componentIndex 精确选择重复组件。</summary>
        private static string ComponentRemove(BridgeRequest request)
        {
            EnsureEditorNotBusy("component.remove");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "Component remove requires a target."), "Component remove requires a target.");
            var componentNames = JsonStringArray(request.RawJsonArg("components"));
            if (componentNames.Length > 0)
            {
                var targets = new List<Tuple<string, Component>>();
                foreach (var name in componentNames)
                {
                    var component = ResolveComponent(gameObject, name, null);
                    if (component == null)
                    {
                        throw new BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + name, "Run unity_cli tools run component.get or context read scene.gameobject.components to list components.");
                    }
                    if (component is Transform)
                    {
                        throw new BridgeCommandException("E_COMPONENT_REMOVE_UNSUPPORTED", "Transform cannot be removed from a GameObject.", "Remove a non-Transform component.");
                    }
                    targets.Add(Tuple.Create(name, component));
                }
                var removed = new List<string>();
                foreach (var item in targets)
                {
                    removed.Add(ComponentSummaryJson(item.Item2));
                    Undo.DestroyObjectImmediate(item.Item2);
                }
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("target", GameObjectTargetJson(gameObject))
                    .Prop("removedCount", (long)removed.Count)
                    .PropArray("components", arr => { foreach (var item in removed) arr.RawValue(item); })
                    .PropArray("plannedEffects", arr =>
                    {
                        foreach (var item in targets)
                        {
                            arr.BeginObject().Prop("type", "component.remove").Prop("target", HierarchyPath(gameObject)).Prop("component", item.Item1).EndObject();
                        }
                    })
                    .Prop("logPath", LogPath())
                    .EndObject()
                    .ToString());
            }

            var componentName = RequiredComponentName(request);
            var componentIndex = request.OptionalIntArg("componentIndex");
            var singleComponent = ResolveComponent(gameObject, componentName, componentIndex);
            if (singleComponent == null)
            {
                throw new BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + componentName, "Run unity_cli tools run component.get or context read scene.gameobject.components to list components.");
            }
            if (singleComponent is Transform)
            {
                throw new BridgeCommandException("E_COMPONENT_REMOVE_UNSUPPORTED", "Transform cannot be removed from a GameObject.", "Remove a non-Transform component.");
            }
            var singleRemoved = ComponentSummaryJson(singleComponent);
            Undo.DestroyObjectImmediate(singleComponent);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("component", singleRemoved)
                .Prop("componentIndex", componentIndex.HasValue ? componentIndex.Value.ToString(CultureInfo.InvariantCulture) : null)
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

        /// <summary>设置 Component 的一个或多个简单公开字段/属性。</summary>
        private static string ComponentSet(BridgeRequest request)
        {
            EnsureEditorNotBusy("component.set");
            var gameObject = ResolveUniqueTarget(RequiredTarget(request, "Component set requires a target."), "Component set requires a target.");
            var componentName = RequiredComponentName(request);
            var componentIndex = request.OptionalIntArg("componentIndex");
            var component = ResolveComponent(gameObject, componentName, componentIndex);
            if (component == null)
            {
                throw new BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + componentName, "Run unity_cli tools run component.get or context read scene.gameobject.components to list components.");
            }

            var propertyName = request.StringArg("property");
            var propertiesJson = ComponentPropertiesJson(request);
            if (string.IsNullOrEmpty(propertyName) && string.IsNullOrEmpty(propertiesJson))
            {
                throw new BridgeCommandException("E_COMPONENT_PROPERTY_REQUIRED", "component.set requires property/value or a properties object.", "Pass --input '{\"target\":\"Player\",\"component\":\"Rigidbody\",\"properties\":{\"mass\":5}}'.");
            }

            var propertyChanges = new List<ComponentPropertyChange>();
            if (!string.IsNullOrEmpty(propertyName))
            {
                if (!request.HasArg("value"))
                {
                    throw new BridgeCommandException("E_COMPONENT_VALUE_REQUIRED", "component.set requires a value when property is provided.", "Pass value as --input JSON so booleans/numbers keep their type.");
                }
                propertyChanges.Add(BuildComponentPropertyChange(component.GetType(), propertyName, request.RawJsonArg("value")));
            }
            propertyChanges.AddRange(BuildComponentPropertyChanges(component.GetType(), propertiesJson));

            Undo.RecordObject(component, "unity_cli component.set");
            ApplyComponentPropertyChanges(component, propertyChanges);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return SuccessJson(ComponentMutationJson("component.set", gameObject, component, propertyName, propertyChanges, componentIndex));
        }

        /// <summary>深读单个 Component：输出 fields/props，支持 paths/viewQuery/maxDepth。对齐 Ivan gameobject-component-get。</summary>
        /// <summary>深读单个 Component：支持 paths/viewQuery 互斥的 token-safe 读取。</summary>
        private static string ComponentGet(BridgeRequest request)
        {
            var component = ObjectRefResolver.ResolveComponent(request);
            var options = BuildSerializerOptions(request);
            var memberJson = ObjectSerializer.Serialize(component, options);
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "component.get");
            w.Prop("typeName", component.GetType().FullName);
            w.Prop("componentInstanceId", component.GetInstanceID());
            w.PropRaw("gameObject", GameObjectTargetJson(component.gameObject));
            w.PropRaw("data", memberJson);
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
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
                return SuccessJson(dry.EndObject().ToString());
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
            return SuccessJson(w.EndObject().ToString());
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
            return SuccessJson(w.EndObject().ToString());
        }

        private static string GameObjectFind(BridgeRequest request)
        {
            var searchTerm = request.StringArg("searchTerm") ?? request.StringArg("query") ?? request.StringArg("target") ?? request.StringArg("name") ?? request.StringArg("tag") ?? request.StringArg("layer") ?? request.StringArg("component") ?? request.StringArg("path") ?? request.StringArg("id");
            if (string.IsNullOrEmpty(searchTerm))
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "GameObject find requires a search term.", "Pass --input '{\"searchTerm\":\"Player\",\"searchMethod\":\"by_name\"}'.");
            }
            var searchMethod = request.StringArg("searchMethod") ?? InferGameObjectSearchMethod(request);
            var includeInactive = request.BoolArg("includeInactive", false);
            var preferPrefabStage = request.BoolArg("preferPrefabStage", false);
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", 50), 500));
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            // Prefab Stage 优先：preferPrefabStage=true 且有打开的 Prefab Stage 时，先在 prefab 内容里匹配，
            // 再合并场景匹配，让 Prefab 编辑时的对象解析优先命中 prefab 内容（对齐 IvanMurzak 语义）。
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            var prefabStageMatches = new List<GameObject>();
            var prefabStageOpen = preferPrefabStage && prefabStage != null && prefabStage.prefabContentsRoot != null;
            if (prefabStageOpen)
            {
                foreach (var t in prefabStage.prefabContentsRoot.GetComponentsInChildren<Transform>(includeInactive))
                {
                    if (MatchesGameObject(t.gameObject, searchMethod, searchTerm)) prefabStageMatches.Add(t.gameObject);
                }
            }
            var sceneMatches = SearchGameObjects(searchMethod, searchTerm, includeInactive);
            var matches = prefabStageOpen ? prefabStageMatches.Concat(sceneMatches).ToList() : sceneMatches;
            var total = matches.Count;
            var page = matches.Skip(cursor).Take(pageSize).ToList();
            var nextCursor = cursor + page.Count < total ? (cursor + page.Count).ToString(CultureInfo.InvariantCulture) : null;

            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("query", searchTerm)
                .Prop("searchMethod", searchMethod)
                .Prop("includeInactive", includeInactive)
                .Prop("preferPrefabStage", preferPrefabStage)
                .Prop("prefabStageOpen", prefabStageOpen)
                .Prop("matchCount", (long)total)
                .PropArray("items", arr => { foreach (var gameObject in page) { arr.RawValue(HierarchyItemJson(gameObject.transform)); } })
                .PropObject("pageInfo", p => p.Prop("pageSize", (long)pageSize).Prop("cursor", (long)cursor).Prop("nextCursor", nextCursor).Prop("total", (long)total))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
        }

        private static string InferGameObjectSearchMethod(BridgeRequest request)
        {
            if (!string.IsNullOrEmpty(request.StringArg("tag"))) return "by_tag";
            if (!string.IsNullOrEmpty(request.StringArg("layer"))) return "by_layer";
            if (!string.IsNullOrEmpty(request.StringArg("component"))) return "by_component";
            if (!string.IsNullOrEmpty(request.StringArg("path"))) return "by_path";
            if (!string.IsNullOrEmpty(request.StringArg("id"))) return "by_id";
            return "by_name";
        }

        private static List<GameObject> SearchGameObjects(string searchMethod, string searchTerm, bool includeInactive)
        {
            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>(includeInactive).OrderBy(HierarchyPath, StringComparer.Ordinal).ToList();
            return allObjects.Where(gameObject => MatchesGameObject(gameObject, searchMethod, searchTerm)).ToList();
        }

        // 提取单个 GameObject 的搜索匹配判定，供场景搜索与 Prefab Stage 优先搜索复用。
        private static bool MatchesGameObject(GameObject gameObject, string searchMethod, string searchTerm)
        {
            var normalized = (searchMethod ?? "by_name").Trim().ToLowerInvariant();
            if (normalized == "by_id") return gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == searchTerm;
            if (normalized == "by_path") return string.Equals(HierarchyPath(gameObject), searchTerm, StringComparison.Ordinal);
            if (normalized == "by_tag") return string.Equals(gameObject.tag, searchTerm, StringComparison.Ordinal);
            if (normalized == "by_layer") return gameObject.layer == ParseLayer(searchTerm);
            if (normalized == "by_component") { var type = ResolveComponentType(searchTerm); return type != null && gameObject.GetComponent(type) != null; }
            if (normalized != "by_name") throw new BridgeCommandException("E_SEARCH_METHOD_INVALID", "Unsupported GameObject searchMethod: " + searchMethod, "Use by_name, by_tag, by_layer, by_component, by_path, or by_id.");
            return string.Equals(gameObject.name, searchTerm, StringComparison.Ordinal);
        }

        private static string HierarchyItemJson(Transform transform)
        {
            return HierarchyItemJson(transform, -1, true);
        }

        /// <summary>生成轻量 GameObject 层级项；默认只给组件类型摘要，详细字段交给 component.get。</summary>
        private static string HierarchyItemJson(Transform transform, int depth, bool includeComponents)
        {
            var componentNames = new List<string>();
            foreach (var component in transform.GetComponents<Component>())
            {
                componentNames.Add(component != null ? component.GetType().Name : "MissingScript");
            }
            var gameObject = transform.gameObject;
            var writer = new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)gameObject.GetInstanceID())
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .Prop("active", gameObject.activeSelf)
                .Prop("activeInHierarchy", gameObject.activeInHierarchy)
                .Prop("tag", gameObject.tag)
                .Prop("layer", (long)gameObject.layer)
                .Prop("childCount", (long)transform.childCount)
                .Prop("childrenTruncated", transform.childCount > 0)
                .Prop("componentCount", (long)componentNames.Count)
                .PropArray("componentTypes", arr => { foreach (var name in componentNames) { arr.Value(name); } });
            if (depth >= 0)
            {
                writer.Prop("depth", (long)depth);
            }
            if (includeComponents)
            {
                writer.PropArray("components", arr => { foreach (var name in componentNames) { arr.Value(name); } });
            }
            return writer.EndObject().ToString();
        }

        /// <summary>按深度、名称过滤和 inactive 策略收集层级摘要，分页在调用方统一处理。</summary>
        private static void CollectHierarchy(Transform transform, List<string> items, int depth, int maxDepth, string nameFilter, bool includeInactive, bool includeComponents)
        {
            CollectHierarchy(transform, items, depth, maxDepth, nameFilter, includeInactive, includeComponents, null);
        }

        private static void CollectHierarchy(Transform transform, List<string> items, int depth, int maxDepth, string query, bool includeInactive, bool includeComponents, string componentType)
        {
            var gameObject = transform.gameObject;
            if (includeInactive || gameObject.activeInHierarchy)
            {
                if (LargeOutputTextMatches(query, gameObject.name, HierarchyPath(gameObject)) && LargeOutputComponentMatches(gameObject, componentType))
                {
                    items.Add(HierarchyItemJson(transform, depth, includeComponents));
                }
            }
            if (depth >= maxDepth)
            {
                return;
            }
            foreach (Transform child in transform)
            {
                CollectHierarchy(child, items, depth + 1, maxDepth, query, includeInactive, includeComponents, componentType);
            }
        }

        /// <summary>兼容 Prefab/Context 分页层级读取：统计整棵 Transform 树节点数。</summary>
        private static int CountHierarchy(Transform transform)
        {
            var count = 1;
            foreach (Transform child in transform)
            {
                count += CountHierarchy(child);
            }
            return count;
        }

        /// <summary>兼容 Prefab/Context 分页层级读取：按 DFS 顺序跳过 cursor 后收集 pageSize 个节点。</summary>
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

        // go.modify 支持 targets[] 批量修改；先完整解析，避免前半成功后半失败的部分写入。
        private static List<GameObject> ResolveModifyTargets(BridgeRequest request)
        {
            var rawTargets = request.RawJsonArg("targets");
            var targetNames = JsonStringArray(rawTargets);
            if (targetNames.Length == 0)
            {
                return new List<GameObject> { ResolveUniqueTarget(RequiredTarget(request, "GameObject modify requires target or targets[]."), "GameObject modify requires target or targets[].") };
            }
            var targets = new List<GameObject>();
            var seen = new HashSet<int>();
            foreach (var targetName in targetNames)
            {
                var gameObject = ResolveUniqueTarget(targetName, "GameObject modify targets[] contains an empty target.");
                if (seen.Add(gameObject.GetInstanceID())) targets.Add(gameObject);
            }
            if (targets.Count == 0)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject modify targets[] must contain at least one target.", "Pass target for one object or targets as a non-empty string array.");
            }
            return targets;
        }

        private static void ValidateGameObjectMutationInputs(BridgeRequest request)
        {
            var tagValue = request.StringArg("tagValue");
            if (!string.IsNullOrEmpty(tagValue) && !IsKnownTag(tagValue))
            {
                throw new BridgeCommandException("E_TAG_NOT_FOUND", "Tag not found: " + tagValue, "Create the tag first with project.tag or pass an existing Unity tag.");
            }
            var layerValue = request.StringArg("layerValue");
            if (!string.IsNullOrEmpty(layerValue))
            {
                ParseLayer(layerValue);
            }
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
            if (string.Equals(value, "cube", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Cube;
            if (string.Equals(value, "sphere", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Sphere;
            if (string.Equals(value, "capsule", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Capsule;
            if (string.Equals(value, "cylinder", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Cylinder;
            if (string.Equals(value, "plane", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Plane;
            if (string.Equals(value, "quad", StringComparison.OrdinalIgnoreCase)) return PrimitiveType.Quad;
            throw new BridgeCommandException("E_PRIMITIVE_TYPE_INVALID", "Unsupported primitive type: " + value, "Use cube, sphere, capsule, cylinder, plane, or quad.");
        }

        private static bool IsKnownTag(string value)
        {
            return UnityEditorInternal.InternalEditorUtility.tags.Contains(value);
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

        // 生成批量 go.modify 返回：保留每个目标详情与对应 plannedEffects，便于 AI 审计结果。
        private static string GameObjectBatchMutationJson(string type, List<GameObject> gameObjects)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("modifiedCount", (long)gameObjects.Count)
                .PropArray("gameObjects", arr => { foreach (var gameObject in gameObjects) arr.RawValue(GameObjectDetailJson(gameObject)); })
                .PropArray("plannedEffects", arr =>
                {
                    foreach (var gameObject in gameObjects)
                    {
                        arr.RawValue(new UnityCliJsonWriter().BeginObject()
                            .Prop("type", type)
                            .Prop("target", HierarchyPath(gameObject))
                            .EndObject().ToString());
                    }
                })
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

        /// <summary>判断 Component 类型是否允许在同一 GameObject 上添加多个实例。</summary>
        private static bool AllowsMultipleComponents(Type type)
        {
            return type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length == 0;
        }

        private static Component ResolveComponent(GameObject gameObject, string componentName)
        {
            return ResolveComponent(gameObject, componentName, null);
        }

        private static Component ResolveComponent(GameObject gameObject, string componentName, int? componentIndex)
        {
            var type = ResolveComponentType(componentName);
            if (!componentIndex.HasValue)
            {
                return gameObject.GetComponent(type);
            }
            var components = gameObject.GetComponents(type);
            if (componentIndex.Value < 0 || componentIndex.Value >= components.Length)
            {
                throw new BridgeCommandException("E_COMPONENT_INDEX_OUT_OF_RANGE", "componentIndex " + componentIndex.Value + " is out of range for " + componentName + ".", "Run context read scene.gameobject.components to discover component indices.");
            }
            return components[componentIndex.Value];
        }

        private static string ComponentPropertiesJson(BridgeRequest request)
        {
            var propertiesJson = request.RawJsonArg("properties") ?? request.RawJsonArg("componentProperties");
            if (string.IsNullOrEmpty(propertiesJson)) return null;
            var trimmed = propertiesJson.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == (char)34 && trimmed[trimmed.Length - 1] == (char)34)
            {
                trimmed = JsonUnescape(trimmed.Substring(1, trimmed.Length - 2));
            }
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
            {
                throw new BridgeCommandException("E_COMPONENT_PROPERTIES_INVALID", "component properties must be a JSON object.", "Pass properties as --input '{\"properties\":{\"mass\":5}}'.");
            }
            return trimmed;
        }

        private static List<ComponentPropertyChange> BuildComponentPropertyChanges(Type componentType, string propertiesJson)
        {
            var changes = new List<ComponentPropertyChange>();
            if (string.IsNullOrEmpty(propertiesJson)) return changes;
            foreach (var propertyName in JsonObjectPropertyNames(propertiesJson))
            {
                changes.Add(BuildComponentPropertyChange(componentType, propertyName, JsonRawProperty(propertiesJson, propertyName)));
            }
            return changes;
        }

        private static ComponentPropertyChange BuildComponentPropertyChange(Type componentType, string propertyName, string rawJson)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            var property = componentType.GetProperty(propertyName, flags);
            if (property != null && property.CanWrite)
            {
                return new ComponentPropertyChange(propertyName, ConvertComponentJsonValue(rawJson, property.PropertyType), property.PropertyType, property, null, rawJson);
            }
            var field = componentType.GetField(propertyName, flags);
            if (field != null)
            {
                return new ComponentPropertyChange(propertyName, ConvertComponentJsonValue(rawJson, field.FieldType), field.FieldType, null, field, rawJson);
            }
            throw new BridgeCommandException("E_COMPONENT_PROPERTY_NOT_FOUND", "Writable component property not found: " + propertyName, "Use component.get or type.schema to inspect writable public fields/properties.");
        }

        private static void ApplyComponentPropertyChanges(Component component, List<ComponentPropertyChange> changes)
        {
            foreach (var change in changes)
            {
                if (change.Property != null) change.Property.SetValue(component, change.Value, null);
                else if (change.Field != null) change.Field.SetValue(component, change.Value);
            }
        }

        private static object ConvertComponentJsonValue(string rawJson, Type type)
        {
            var value = (rawJson ?? "null").Trim();
            if (Nullable.GetUnderlyingType(type) != null)
            {
                if (value == "null") return null;
                type = Nullable.GetUnderlyingType(type);
            }
            if (type == typeof(string)) return JsonValueToString(value);
            if (type == typeof(bool)) return JsonValueToBool(value);
            if (type == typeof(int)) return (int)JsonValueToLong(value);
            if (type == typeof(long)) return JsonValueToLong(value);
            if (type == typeof(float)) return JsonValueToFloat(value);
            if (type == typeof(double)) return JsonValueToDouble(value);
            if (type == typeof(Vector2)) return ParseVector2(JsonValueToString(value));
            if (type == typeof(Vector3)) return ParseVector3(JsonValueToString(value));
            if (type.IsEnum) return Enum.Parse(type, JsonValueToString(value) ?? string.Empty, true);
            throw new BridgeCommandException("E_COMPONENT_PROPERTY_TYPE_UNSUPPORTED", "Unsupported component property type: " + type.Name, "Use component.modify for nested/object references, or simple string/bool/int/float/double/enum/Vector2/Vector3 here.");
        }

        private static string JsonValueToString(string value)
        {
            if (value == "null") return null;
            if (value.Length >= 2 && value[0] == (char)34 && value[value.Length - 1] == (char)34)
            {
                return JsonUnescape(value.Substring(1, value.Length - 2));
            }
            return value;
        }

        private static bool JsonValueToBool(string value)
        {
            var text = JsonValueToString(value);
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1") return true;
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0") return false;
            throw new BridgeCommandException("E_COMPONENT_VALUE_INVALID", "Expected boolean component value but got: " + text, "Pass true/false as JSON booleans in --input.");
        }

        private static long JsonValueToLong(string value)
        {
            var text = JsonValueToString(value);
            long parsed;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            throw new BridgeCommandException("E_COMPONENT_VALUE_INVALID", "Expected integer component value but got: " + text, "Pass a JSON number or numeric string.");
        }

        private static float JsonValueToFloat(string value)
        {
            return (float)JsonValueToDouble(value);
        }

        private static double JsonValueToDouble(string value)
        {
            var text = JsonValueToString(value);
            double parsed;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            throw new BridgeCommandException("E_COMPONENT_VALUE_INVALID", "Expected numeric component value but got: " + text, "Pass a JSON number or numeric string.");
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

        private static string ComponentMutationJson(string type, GameObject gameObject, Component component, string propertyName, List<ComponentPropertyChange> changes, int? componentIndex)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("component", ComponentSummaryJson(component))
                .Prop("property", propertyName)
                .Prop("componentIndex", componentIndex.HasValue ? componentIndex.Value.ToString(CultureInfo.InvariantCulture) : null)
                .PropArray("changedProperties", arr =>
                {
                    foreach (var change in changes ?? new List<ComponentPropertyChange>())
                    {
                        arr.BeginObject()
                            .Prop("name", change.Name)
                            .Prop("type", change.ValueType.Name)
                            .PropRaw("value", change.RawJson)
                            .EndObject();
                    }
                })
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

        private sealed class ComponentPropertyChange
        {
            public ComponentPropertyChange(string name, object value, Type valueType, PropertyInfo property, FieldInfo field, string rawJson)
            {
                Name = name;
                Value = value;
                ValueType = valueType;
                Property = property;
                Field = field;
                RawJson = rawJson;
            }

            public string Name { get; private set; }
            public object Value { get; private set; }
            public Type ValueType { get; private set; }
            public PropertyInfo Property { get; private set; }
            public FieldInfo Field { get; private set; }
            public string RawJson { get; private set; }
        }

        private sealed class Counter
        {
            public int Value;
        }
    }
}
#endif
