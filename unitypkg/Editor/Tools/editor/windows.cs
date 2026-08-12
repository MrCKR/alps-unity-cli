#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Tools.Editor
{
    /// <summary>
    /// editor.windows 只读 Tool：读取当前打开的 Unity EditorWindow 列表。
    /// </summary>
    public class EditorWindowsTool : IUnityCliTool<EditorWindowsTool.Request, EditorWindowsTool.Result>
    {
        public sealed class Request { }

        public sealed class RectInfo
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Width { get; set; }
            public float Height { get; set; }
        }

        public sealed class WindowInfo
        {
            public string Title { get; set; }
            public string TypeName { get; set; }
            public string FullName { get; set; }
            public bool IsFocused { get; set; }
            public RectInfo Position { get; set; }
            public int InstanceID { get; set; }
        }

        public sealed class Result
        {
            public WindowInfo[] Windows { get; set; }
            public int Count { get; set; }
        }

        // ── desc:只声明人工字段,source=built-in 表示进入只读 Tool 表 ──
        public ToolDescriptor Describe() => new()
        {
            Name = "editor.windows", Summary = "Read currently open Unity EditorWindow instances with title, type names, focused state, and instance IDs.",
            Description = "Read currently open Unity EditorWindow instances with title, type names, focused state, and instance IDs. This observes editor UI state without opening, closing, focusing, or moving windows.",
            DestructiveHint = false,
            Group = "editor",
            Target = "editor",
            PlayModeSupport = "supported",
            Examples = new[] { new ToolExample { Description = "List open Editor windows", Input = new Request { } } }
        };

        /// <summary>
        /// 读取当前 EditorWindow 清单；只观察 UI 状态，不打开、关闭或聚焦窗口。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var entries = new List<WindowInfo>();
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null) continue;
                var type = window.GetType();
                var position = window.position;
                entries.Add(new WindowInfo
                {
                    Title = window.titleContent.text,
                    TypeName = type.Name,
                    FullName = type.FullName,
                    IsFocused = EditorWindow.focusedWindow == window,
                    Position = new RectInfo
                    {
                        X = position.x,
                        Y = position.y,
                        Width = position.width,
                        Height = position.height
                    },
                    InstanceID = window.GetInstanceID()
                });
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Windows = entries.ToArray(),
                Count = entries.Count
            });
        }
    }
}
#endif
