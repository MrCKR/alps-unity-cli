#if UNITY_EDITOR
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.active — 返回当前激活场景的 name/path/buildIndex/isLoaded/isDirty。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class SceneActiveTool : IUnityCliTool<SceneActiveTool.Request, SceneActiveTool.Result>
    {
        public sealed class Request
        {
            // scene.active 无入参
        }

        public sealed class Result
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public long BuildIndex { get; set; }
            public bool IsLoaded { get; set; }
            public bool IsDirty { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.active",
            Summary = "Return name, path and build index of the currently active scene.",
            Description = "Return name, path and build index of the currently active scene. Read-only; use scene.list-opened for all loaded scenes and scene.set-active to change the active scene.",
            Group = "scene",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Get the active scene", Input = new Request { } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var scene = SceneManager.GetActiveScene();
            return new Result
            {
                Name = scene.name,
                Path = NormalizeAssetPath(scene.path),
                BuildIndex = scene.buildIndex,
                IsLoaded = scene.isLoaded,
                IsDirty = scene.isDirty
            };
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.Replace("\\", "/");
        }
    }
}
#endif
