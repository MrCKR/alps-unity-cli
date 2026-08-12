#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Reflection;
using UnityEditor;

namespace UnityCli.Tools.Editor
{
    /// <summary>
    /// editor.tool-states 只读 Tool：读取 Editor transform 工具和 snap/grid 状态。
    /// </summary>
    public class EditorToolStatesTool : IUnityCliTool<EditorToolStatesTool.Request, EditorToolStatesTool.Result>
    {
        public sealed class Request { }

        public sealed class TransformState
        {
            public string ActiveTool { get; set; }
            public string PivotMode { get; set; }
            public string PivotRotation { get; set; }
        }

        public sealed class SnapState
        {
            public bool MoveSnapEnabled { get; set; }
            public bool RotationSnapEnabled { get; set; }
            public bool ScaleSnapEnabled { get; set; }
            public float MoveSnap { get; set; }
            public float RotationSnap { get; set; }
            public float ScaleSnap { get; set; }
        }

        public sealed class Result
        {
            public TransformState Transform { get; set; }
            public SnapState Snap { get; set; }
            public bool GridAvailable { get; set; }
        }

        // ── desc:只声明人工字段,source=built-in 表示进入只读 Tool 表 ──
        public ToolDescriptor Describe() => new()
        {
            Name = "editor.tool-states",
            Summary = "Read Editor transform, pivot, snap, and grid tool state.",
            Description = "Read Unity Editor transform tool settings, pivot mode/rotation, snap settings, and grid availability. This is not MCP tool discovery; it observes editor tool configuration only.",
            DestructiveHint = false,
            Group = "editor",
            Target = "editor",
            PlayModeSupport = "supported",
            Examples = new[] { new ToolExample { Description = "Read transform tool and snap settings", Input = new Request { } } }
        };

        /// <summary>
        /// 读取 transform 工具与 snap 设置；只观察状态，不切换工具、不修改网格或吸附设置。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var snapSettingsType = FindType("UnityEditor.EditorSnapSettings");
            var moveSnapEnabled = snapSettingsType != null ? BoolMemberValue(snapSettingsType, null, "snapMoveEnabled") : null;
            var rotationSnapEnabled = snapSettingsType != null ? BoolMemberValue(snapSettingsType, null, "snapRotationEnabled") : null;
            var scaleSnapEnabled = snapSettingsType != null ? BoolMemberValue(snapSettingsType, null, "snapScaleEnabled") : null;
            var moveSnap = snapSettingsType != null ? FloatMemberValue(snapSettingsType, null, "moveSnap") : null;
            var rotationSnap = snapSettingsType != null ? FloatMemberValue(snapSettingsType, null, "rotationSnap") : null;
            var scaleSnap = snapSettingsType != null ? FloatMemberValue(snapSettingsType, null, "scaleSnap") : null;

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Transform = new TransformState
                {
                    ActiveTool = UnityEditor.Tools.current.ToString(),
                    PivotMode = UnityEditor.Tools.pivotMode.ToString(),
                    PivotRotation = UnityEditor.Tools.pivotRotation.ToString()
                },
                Snap = new SnapState
                {
                    MoveSnapEnabled = moveSnapEnabled ?? false,
                    RotationSnapEnabled = rotationSnapEnabled ?? false,
                    ScaleSnapEnabled = scaleSnapEnabled ?? false,
                    MoveSnap = moveSnapEnabled.HasValue && moveSnapEnabled.Value && moveSnap.HasValue ? moveSnap.Value : 0f,
                    RotationSnap = rotationSnapEnabled.HasValue && rotationSnapEnabled.Value && rotationSnap.HasValue ? rotationSnap.Value : 0f,
                    ScaleSnap = scaleSnapEnabled.HasValue && scaleSnapEnabled.Value && scaleSnap.HasValue ? scaleSnap.Value : 0f
                },
                GridAvailable = snapSettingsType != null
            });
        }

        // 在当前 AppDomain 中查找 Unity 内部类型；找不到时返回 null，让输出降级为 gridAvailable=false。
        private static Type FindType(string fullName)
        {
            var type = Type.GetType(fullName);
            if (type != null) return type;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch
                {
                    // 忽略无法反射的编辑器程序集，继续查找其它程序集。
                }
            }
            return null;
        }

        // 读取 bool 类型的 public/private static/instance 成员；反射失败时返回 null。
        private static bool? BoolMemberValue(Type type, object instance, string memberName)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                try { return Convert.ToBoolean(prop.GetValue(instance, null), CultureInfo.InvariantCulture); } catch { return null; }
            }
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { return Convert.ToBoolean(field.GetValue(instance), CultureInfo.InvariantCulture); } catch { return null; }
            }
            return null;
        }

        // 读取 float 类型的 public/private static/instance 成员；反射失败时返回 null。
        private static float? FloatMemberValue(Type type, object instance, string memberName)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                try { return Convert.ToSingle(prop.GetValue(instance, null), CultureInfo.InvariantCulture); } catch { return null; }
            }
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { return Convert.ToSingle(field.GetValue(instance), CultureInfo.InvariantCulture); } catch { return null; }
            }
            return null;
        }
    }
}
#endif
