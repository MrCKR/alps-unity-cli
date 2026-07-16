#if UNITY_EDITOR
namespace UnityCli.Contexts.Editor
{
    /// <summary>
    /// editor.menu-items 内置 Context：说明 Unity Editor menu item 枚举限制。
    /// </summary>
    public class EditorMenuItemsContext : IUnityCliContext<EditorMenuItemsContext.Request, EditorMenuItemsContext.Result>
    {
        public sealed class Request
        {
            public string Search { get; set; }
        }

        public sealed class MenuItemInfo { }

        public sealed class Result
        {
            public MenuItemInfo[] Items { get; set; }
            public int Count { get; set; }
            public string Diagnostics { get; set; }
        }

        // ── desc:只声明人工字段,source=built-in 表示进入内置 Context 表 ──
        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "editor.menu-items",
            Summary = "Report Editor menu enumeration availability without executing menus.",
            Description = "Attempt to enumerate Unity Editor menu items as read-only discovery. Unity has no stable public API for full menu enumeration, so this context may return an empty items array with diagnostics; executing a menu path remains the destructive Tool editor.menu.",
            Category = "editor",
            Version = "1.0.0",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "static",
            Examples = new[] { new ContextExample { Description = "Search menu item metadata", Input = new Request { Search = "Save" } } }
        };

        /// <summary>
        /// 返回菜单枚举限制诊断；只读，不执行任何 menu path。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Items = new MenuItemInfo[0],
                Count = 0,
                Diagnostics = "Unity Editor menu items cannot be enumerated through a public API. Reflection on internal MenuItem cache is version-dependent and not guaranteed. This context returns an empty list); menu execution is a Tool (editor.menu), not a Context."
            });
        }
    }
}
#endif
