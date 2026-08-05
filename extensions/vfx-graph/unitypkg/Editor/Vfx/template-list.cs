#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>列出当前 VFX Graph package 提供的模板。</summary>
    public sealed class VfxTemplateListTool : IUnityCliTool<VfxTemplateListTool.Request, VfxTemplateListTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public List<string> Templates { get; set; } public long Count { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.template.list", Summary = "List available VFX Graph templates.",
            Description = "List template names discovered in the installed Visual Effect Graph package without creating assets.",
            Group = "vfx",RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new string[0], Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "List graph templates", Input = new Request() } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.template.list");
            VfxToolSupport.RequireVfxPackageInstalled();
            var templates = VfxToolSupport.DiscoverVfxTemplates();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Templates = templates, Count = templates.Count });
        }
    }
}
#endif
