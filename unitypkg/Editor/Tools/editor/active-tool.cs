#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityCli.Tools.Editor
{
    /// <summary>
    /// editor.active-tool 只读 Tool：读取当前 Unity Editor 工具手柄状态。
    /// </summary>
    public class EditorActiveToolTool : IUnityCliTool<EditorActiveToolTool.Request, EditorActiveToolTool.Result>
    {
        public sealed class Request { }

        public sealed class Vector3Info
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
        }

        public sealed class QuaternionInfo
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public float W { get; set; }
            public Vector3Info EulerAngles { get; set; }
        }

        public sealed class Result
        {
            public string ActiveTool { get; set; }
            public bool IsCustom { get; set; }
            public string PivotMode { get; set; }
            public string PivotRotation { get; set; }
            public bool HandleAvailable { get; set; }
            public Vector3Info HandlePosition { get; set; }
            public QuaternionInfo HandleRotation { get; set; }
        }

        // ── desc:只声明人工字段,source=built-in 表示进入只读 Tool 表 ──
        public ToolDescriptor Describe() => new()
        {
            Name = "editor.active-tool", Summary = "Read the currently active Unity Editor transform/tool handle plus pivot mode, pivot rotation, handle position, and handle rotation.",
            Description = "Read the currently active Unity Editor transform/tool handle plus pivot mode, pivot rotation, handle position, and handle rotation. Does not change the active tool.",
            DestructiveHint = false,
            Group = "editor",
            Target = "editor",
            PlayModeSupport = "supported",
            Examples = new[] { new ToolExample { Description = "Read the active Editor tool", Input = new Request { } } }
        };

        /// <summary>
        /// 读取当前 Editor 工具和手柄位置/旋转；只观察状态，不切换工具或修改 pivot 设置。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var current = UnityEditor.Tools.current;
            var handlePosition = UnityEditor.Tools.handlePosition;
            var handleRotation = UnityEditor.Tools.handleRotation;
            var handleAvailable = IsFinite(handlePosition) && IsFinite(handleRotation);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                ActiveTool = current.ToString(),
                IsCustom = current != Tool.Move && current != Tool.Rotate && current != Tool.Scale && current != Tool.Rect && current != Tool.None,
                PivotMode = UnityEditor.Tools.pivotMode.ToString(),
                PivotRotation = UnityEditor.Tools.pivotRotation.ToString(),
                HandleAvailable = handleAvailable,
                HandlePosition = ToVector3Info(handleAvailable ? handlePosition : Vector3.zero),
                HandleRotation = ToQuaternionInfo(handleAvailable ? handleRotation : Quaternion.identity)
            });
        }

        // Unity 在无有效 Scene handle 时可能返回 NaN/Infinity；协议边界只允许有限 JSON number。
        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(Quaternion value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // 将 Unity Vector3 转为稳定 JSON DTO。
        private static Vector3Info ToVector3Info(Vector3 value) => new Vector3Info
        {
            X = value.x,
            Y = value.y,
            Z = value.z
        };

        // 将 Unity Quaternion 转为稳定 JSON DTO，并保留欧拉角便于 AI 直接读取。
        private static QuaternionInfo ToQuaternionInfo(Quaternion value) => new QuaternionInfo
        {
            X = value.x,
            Y = value.y,
            Z = value.z,
            W = value.w,
            EulerAngles = ToVector3Info(value.eulerAngles)
        };
    }
}
#endif
