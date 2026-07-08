#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // 解析 scene.gameobject* Context 的目标 GameObject；只读，不修改层级或 Selection。
        private static GameObject ResolveSceneContextGameObject(BridgeRequest request)
        {
            var inputJson = request.RawJsonArg("input");
            var target = request.StringArg("target") ?? SceneContextInputStringArg(inputJson, "name");
            var id = request.OptionalIntArg("id");
            var path = request.StringArg("path");
            return ResolveSceneContextGameObject(target, id, path);
        }

        // context.read 顶层 name 保留给 Context 名；业务 name alias 只能从 input 对象读取。
        private static string SceneContextInputStringArg(string inputJson, string name)
        {
            return JsonStringProperty(inputJson, name);
        }

        // 供 DTO fallback 路径复用的字段级解析；id > path > target，与 legacy handler 保持一致。
        private static GameObject ResolveSceneContextGameObject(string target, int? id, string path)
        {
            GameObject gameObject = null;
            if (id.HasValue) gameObject = EditorUtility.InstanceIDToObject(id.Value) as GameObject;
            else if (!string.IsNullOrEmpty(path)) gameObject = GameObject.Find(path);
            else if (!string.IsNullOrEmpty(target))
            {
                var resolution = ResolveGameObjectTarget(target);
                if (resolution.Matches.Count == 1)
                {
                    gameObject = resolution.Target;
                }
                else if (resolution.Matches.Count > 1)
                {
                    throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched context target: " + target, "Pass a hierarchy path or instance id for disambiguation.");
                }
            }
            if (gameObject == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject context target not found.", "Pass input target, path, or id. Use a scene context such as scene.cameras to discover reusable scene object references.");
            }
            return gameObject;
        }

        // 生成 scene.gameobject.components 的详细组件数组，包含可读属性预览。
        private static string SceneContextComponentDetailsJson(GameObject gameObject)
        {
            var writer = new UnityCliJsonWriter().BeginArray();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                writer.RawValue(SceneContextComponentDetailJson(component));
            }
            return writer.EndArray().ToString();
        }

        // 生成单个组件详情；MissingScript 复用组件摘要 shape。
        private static string SceneContextComponentDetailJson(Component component)
        {
            if (component == null) return ComponentSummaryJson(component);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("type", component.GetType().Name)
                .Prop("fullName", component.GetType().FullName)
                .Prop("enabled", ComponentEnabled(component))
                .PropRaw("properties", SceneContextComponentReadablePropertiesJson(component))
                .EndObject()
                .ToString();
        }

        // 读取组件 public 可读属性为字符串预览；单个属性失败不影响整个 Context。
        private static string SceneContextComponentReadablePropertiesJson(Component component)
        {
            var writer = new UnityCliJsonWriter().BeginObject();
            foreach (var property in component.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                try
                {
                    var value = property.GetValue(component, null);
                    writer.Prop(property.Name, value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
                }
                catch
                {
                    // 某些 Unity 属性 getter 依赖内部状态，失败时跳过该属性，保持 Context 可恢复。
                }
            }
            return writer.EndObject().ToString();
        }
    }
}
#endif
