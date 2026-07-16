#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCli
{
    /// <summary>editor.selection.set — 设置 Editor 选择。</summary>
    public class EditorSelectionSetTool : IUnityCliTool<EditorSelectionSetTool.Request, EditorSelectionSetTool.Result>
    {
        public sealed class Request { public long? InstanceId { get; set; } public string Target { get; set; } public string Path { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public string Selected { get; set; } public string TypeName { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.selection.set", Summary = "Set the active selection in the Unity Editor.", Description = "Set Selection.activeObject by instanceId or target path. dryRun=true previews.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Select by path", Input = new Request { Target = "Player" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var dryRun = ctx.DryRun;
            var target = r.Target ?? r.Path;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "editor.selection.set", DryRun = true, Selected = target ?? "<instanceId>" });
            UnityEngine.Object obj = null;
            if (r.InstanceId.HasValue && r.InstanceId.Value != 0) obj = EditorUtility.InstanceIDToObject((int)r.InstanceId.Value);
            else if (!string.IsNullOrEmpty(target)) obj = UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(target);
            if (obj == null) throw new UnityCliBridge.BridgeCommandException("E_SELECTION_TARGET_NOT_FOUND", "Selection target not found.", "Pass a valid instanceId or path.");
            Selection.activeObject = obj;
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "editor.selection.set", DryRun = false, Selected = obj.name, TypeName = obj.GetType().Name });
        }
    }
}
#endif
