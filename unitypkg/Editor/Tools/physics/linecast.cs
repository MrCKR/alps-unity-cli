#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.linecast — 检测两点间是否有碰撞。
    /// </summary>
    public class PhysicsLinecastTool : IUnityCliTool<PhysicsLinecastTool.Request, PhysicsLinecastTool.Result>
    {
        public sealed class Request
        {
            public string Mode { get; set; }
            public float StartX { get; set; }
            public float StartY { get; set; }
            public float StartZ { get; set; }
            public float EndX { get; set; }
            public float EndY { get; set; }
            public float EndZ { get; set; }
            public int? LayerMask { get; set; }
        }

        public sealed class Result
        {
            public bool Hit { get; set; }
            public RaycastHitInfo HitInfo { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.linecast",
            Summary = "Check if a 3D or 2D line segment between two points is blocked by a collider.",
            Description = "Cast a line from start to end and report whether any collider blocks the path. mode=3d (default) or 2d.",
            Group = "physics",DestructiveHint = false,
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Linecast between two points", Input = new Request { StartX = 0, EndX = 10 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var mode = PhysicsToolSupport.NormalizeMode(request.Mode);
            var layerMask = request.LayerMask ?? Physics.DefaultRaycastLayers;
            var start = new Vector3(request.StartX, request.StartY, request.StartZ);
            var end = new Vector3(request.EndX, request.EndY, request.EndZ);
            if (mode == "2d")
            {
                var hit = Physics2D.Linecast(new Vector2(start.x, start.y), new Vector2(end.x, end.y), layerMask);
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Hit = hit.collider != null });
            }
            RaycastHit hit3d;
            var found = Physics.Linecast(start, end, out hit3d, layerMask);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Hit = found, HitInfo = found ? PhysicsToolSupport.BuildRaycast3DHit(hit3d) : null });
        }
    }
}
#endif
