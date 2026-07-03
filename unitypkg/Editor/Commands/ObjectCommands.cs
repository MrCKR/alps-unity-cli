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
        // object.* 已迁移为 IUnityCliTool(unitypkg/Editor/Tools/object/*.cs),
        // 不再用 RegisterCommand。RegisterObjectCommands 已移除。

        private static string ObjectGetData_Removed(BridgeRequest request)
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

        private static string ObjectModify_Removed(BridgeRequest request)
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
                return SuccessJson(dry.EndObject().ToString());
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
            return SuccessJson(w.EndObject().ToString());
        }

        private static ObjectSerializer.Options BuildSerializerOptions(BridgeRequest request)
        {
            var options = new ObjectSerializer.Options
            {
                MaxDepth = request.IntArg("maxDepth", 4),
                MaxArrayItems = Math.Max(0, Math.Min(request.IntArg("maxArrayItems", 50), 1000)),
                IncludeFields = request.BoolArg("includeFields", true),
                IncludeProperties = request.BoolArg("includeProperties", true),
                DeepSerialization = request.BoolArg("deepSerialization", true)
            };
            var pathsRaw = request.RawJsonArg("paths");
            var viewQueryRaw = request.RawJsonArg("viewQuery");
            if (!string.IsNullOrEmpty(pathsRaw) && !string.IsNullOrEmpty(viewQueryRaw))
            {
                throw new BridgeCommandException("E_OBJECT_READ_SCOPE_CONFLICT", "paths and viewQuery are mutually exclusive.", "Use paths for exact member reads, or viewQuery for one filtered subtree.");
            }
            if (!string.IsNullOrEmpty(pathsRaw))
            {
                options.Paths = new HashSet<string>();
                foreach (var p in JsonStringArray(pathsRaw))
                {
                    var clean = p == null ? null : p.Trim();
                    if (!string.IsNullOrEmpty(clean)) options.Paths.Add(clean);
                }
            }
            if (!string.IsNullOrEmpty(viewQueryRaw))
            {
                options.ViewPath = JsonStringProperty(viewQueryRaw, "path") ?? JsonStringProperty(viewQueryRaw, "Path");
                options.NamePattern = JsonStringProperty(viewQueryRaw, "namePattern") ?? JsonStringProperty(viewQueryRaw, "NamePattern");
                options.TypeFilter = JsonStringProperty(viewQueryRaw, "typeFilter") ?? JsonStringProperty(viewQueryRaw, "TypeFilter");
                var maxDepthRaw = JsonRawProperty(viewQueryRaw, "maxDepth") ?? JsonRawProperty(viewQueryRaw, "MaxDepth");
                if (!string.IsNullOrEmpty(maxDepthRaw))
                {
                    int maxDepth;
                    if (int.TryParse(maxDepthRaw.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out maxDepth))
                    {
                        options.MaxDepth = Math.Max(0, maxDepth);
                    }
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
