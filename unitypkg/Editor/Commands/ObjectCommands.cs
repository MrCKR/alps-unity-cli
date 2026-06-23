#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterObjectCommands(BridgeCommandRegistry registry)
        {
            // 通用 Object 深读写：对齐 IvanMurzak object-get-data/object-modify
            RegisterCommand(registry, "object.get-data", "object", false, ObjectGetData);
            RegisterCommand(registry, "object.modify", "object", true, ObjectModify);
        }

        /// <summary>读取任意 UnityEngine.Object 的深序列化数据。</summary>
        private static string ObjectGetData(BridgeRequest request)
        {
            var target = ObjectRefResolver.ResolveObject(request);
            var options = BuildSerializerOptions(request);
            var memberJson = ObjectSerializer.Serialize(target, options);
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "object.get-data");
            w.Prop("typeName", target.GetType().FullName);
            w.Prop("instanceId", target.GetInstanceID());
            w.PropRaw("data", memberJson);
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
        }

        /// <summary>修改任意 Unity Object：支持 pathPatches 与 jsonPatch (Merge Patch)。</summary>
        private static string ObjectModify(BridgeRequest request)
        {
            EnsureEditorNotBusy("object.modify");
            var target = ObjectRefResolver.ResolveObject(request);

            var pathPatchesJson = request.RawJsonArg("pathPatches");
            var jsonPatchJson = request.RawJsonArg("jsonPatch");
            if (string.IsNullOrEmpty(pathPatchesJson) && string.IsNullOrEmpty(jsonPatchJson))
            {
                throw new BridgeCommandException("E_OBJECT_PATCH_REQUIRED",
                    "object.modify requires pathPatches or jsonPatch.",
                    "Pass --input '{\"instanceId\":123,\"pathPatches\":[{\"path\":\"m_Color\",\"value\":\"#FF0000\"}]}' or jsonPatch merge.");
            }

            if (request.DryRun)
            {
                var dry = new UnityCliJsonWriter().BeginObject();
                dry.Prop("command", "object.modify");
                dry.Prop("dryRun", true);
                dry.Prop("instanceId", target.GetInstanceID());
                dry.Prop("typeName", target.GetType().FullName);
                dry.PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "object.modify")
                    .Prop("target", target.name)
                    .Prop("hasPathPatches", !string.IsNullOrEmpty(pathPatchesJson))
                    .Prop("hasJsonPatch", !string.IsNullOrEmpty(jsonPatchJson))
                    .EndObject());
                dry.Prop("logPath", LogPath());
                return dry.EndObject().ToString();
            }

            // 记录修改前数据用于回读
            var beforeJson = ObjectSerializer.Serialize(target, new ObjectSerializer.Options { MaxDepth = 1, IncludeFields = false, IncludeProperties = false, DeepSerialization = false });

            // 执行顺序对齐 IvanMurzak: jsonPatch → pathPatches → objectDiff
            ObjectPatch.PatchResult result = null;
            if (!string.IsNullOrEmpty(jsonPatchJson))
            {
                result = ObjectPatch.ApplyMergePatch(target, jsonPatchJson);
            }
            if (!string.IsNullOrEmpty(pathPatchesJson))
            {
                var pathResult = ObjectPatch.ApplyPathPatches(target, pathPatchesJson);
                if (result == null) result = pathResult;
                else
                {
                    result.ChangedPaths.AddRange(pathResult.ChangedPaths);
                    result.Logs.AddRange(pathResult.Logs);
                }
            }

            // 标记脏（场景对象）
            if (target is Component c && c.gameObject != null) UnityEditor.EditorUtility.SetDirty(c);
            if (target is GameObject go) UnityEditor.EditorUtility.SetDirty(go);
            if (target is UnityEngine.Object uo) UnityEditor.EditorUtility.SetDirty(uo);

            // 回读更新后数据
            var afterJson = ObjectSerializer.Serialize(target, BuildSerializerOptions(request));

            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "object.modify");
            w.Prop("instanceId", target.GetInstanceID());
            w.Prop("typeName", target.GetType().FullName);
            w.PropArray("changedPaths", arr => { foreach (var p in result.ChangedPaths) arr.Value(p); });
            w.PropRaw("updatedData", afterJson);
            w.PropArray("logs", arr => { foreach (var l in result.Logs) arr.Value(l); });
            w.PropRaw("audit", ObjectPatch.ResultToJson(result));
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
        }

        private static ObjectSerializer.Options BuildSerializerOptions(BridgeRequest request)
        {
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
            var nameFilter = request.StringArg("namePattern");
            if (!string.IsNullOrEmpty(nameFilter))
            {
                options.NameFilters = new HashSet<string>(nameFilter.Split(',').Select(s => s.Trim()));
            }
            return options;
        }
    }
}
#endif
