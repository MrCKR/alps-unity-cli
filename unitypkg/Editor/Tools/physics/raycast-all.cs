#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.raycast-all — 3D/2D 射线查询,返回所有命中。
    /// </summary>
    public class PhysicsRaycastAllTool : IUnityCliTool<PhysicsRaycastAllTool.Request, PhysicsRaycastAllTool.Result>
    {
        public sealed class Request
        {
            public string Mode { get; set; }
            public float OriginX { get; set; }
            public float OriginY { get; set; }
            public float OriginZ { get; set; }
            public float DirectionX { get; set; }
            public float DirectionY { get; set; }
            public float DirectionZ { get; set; } = 1f;
            public float Distance { get; set; } = 100f;
            public int? LayerMask { get; set; }
        }

        public sealed class Result
        {
            public string Mode { get; set; }
            public long HitCount { get; set; }
            public List<RaycastHitInfo> Hits { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.raycast-all",
            Summary = "Cast a 3D or 2D ray and return all hits along the path.",
            Description = "Cast a ray and return all collider hits, sorted by distance (3D). mode=3d (default) or 2d.",
            Group = "physics",DestructiveHint = false,
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "All hits forward", Input = new Request { OriginY = 1f, DirectionZ = 1f } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var mode = PhysicsToolSupport.NormalizeMode(request.Mode);
            var distance = Math.Max(0f, request.Distance);
            var layerMask = request.LayerMask ?? Physics.DefaultRaycastLayers;
            if (mode == "2d")
            {
                var origin = new Vector2(request.OriginX, request.OriginY);
                var direction = PhysicsToolSupport.NormalizeVector2(new Vector2(request.DirectionX, request.DirectionY));
                var hits = Physics2D.RaycastAll(origin, direction, distance, layerMask);
                var hitList = new List<RaycastHitInfo>();
                foreach (var h in hits) hitList.Add(PhysicsToolSupport.BuildRaycast2DHit(h));
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Mode = "2d", HitCount = hits.Length, Hits = hitList });
            }
            var origin3d = new Vector3(request.OriginX, request.OriginY, request.OriginZ);
            var direction3d = PhysicsToolSupport.NormalizeVector3(new Vector3(request.DirectionX, request.DirectionY, request.DirectionZ));
            var hits3d = Physics.RaycastAll(origin3d, direction3d, distance, layerMask);
            Array.Sort(hits3d, (a, b) => a.distance.CompareTo(b.distance));
            var list = new List<RaycastHitInfo>();
            foreach (var h in hits3d) list.Add(PhysicsToolSupport.BuildRaycast3DHit(h));
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Mode = "3d", HitCount = hits3d.Length, Hits = list });
        }
    }
}
#endif
