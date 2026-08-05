#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.raycast — 3D/2D 射线查询,返回首个命中或全部命中。
    /// </summary>
    public class PhysicsRaycastTool : IUnityCliTool<PhysicsRaycastTool.Request, PhysicsRaycastTool.Result>
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
            public bool All { get; set; }
        }

        public sealed class Result
        {
            public string Mode { get; set; }
            public RaycastQuery Query { get; set; }
            public bool Hit { get; set; }
            public long HitCount { get; set; }
            public List<RaycastHitInfo> Hits { get; set; }
            public List<string> NextActions { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.raycast",
            Summary = "Cast a 3D or 2D ray and return the first hit, or all hits with all=true.",
            Description = "Cast a ray from origin along direction up to distance. mode=3d (default) or 2d. all=true returns all hits sorted by distance. layerMask defaults to DefaultRaycastLayers.",
            Group = "physics",DestructiveHint = false,
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "3D raycast forward", Input = new Request { OriginY = 1f, DirectionZ = 1f } } }
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
                if (request.All)
                {
                    var hits = Physics2D.RaycastAll(origin, direction, distance, layerMask);
                    var hitList = new List<RaycastHitInfo>();
                    foreach (var h in hits) hitList.Add(PhysicsToolSupport.BuildRaycast2DHit(h));
                    return new System.Threading.Tasks.ValueTask<Result>(new Result { Mode = "2d", Query = PhysicsToolSupport.BuildRaycast2DQuery(origin, direction, distance, layerMask), HitCount = hits.Length, Hits = hitList });
                }
                var hit = Physics2D.Raycast(origin, direction, distance, layerMask);
                var list = new List<RaycastHitInfo>();
                if (hit.collider != null) list.Add(PhysicsToolSupport.BuildRaycast2DHit(hit));
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Mode = "2d", Query = PhysicsToolSupport.BuildRaycast2DQuery(origin, direction, distance, layerMask),
                    Hit = hit.collider != null, HitCount = hit.collider != null ? 1 : 0, Hits = list,
                    NextActions = hit.collider != null ? new List<string>() : new List<string> { "No Physics collider matched the ray); adjust origin, direction, distance or layer mask." }
                });
            }
            var origin3d = new Vector3(request.OriginX, request.OriginY, request.OriginZ);
            var direction3d = PhysicsToolSupport.NormalizeVector3(new Vector3(request.DirectionX, request.DirectionY, request.DirectionZ));
            if (request.All)
            {
                var hits = Physics.RaycastAll(origin3d, direction3d, distance, layerMask);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                var hitList = new List<RaycastHitInfo>();
                foreach (var h in hits) hitList.Add(PhysicsToolSupport.BuildRaycast3DHit(h));
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Mode = "3d", Query = PhysicsToolSupport.BuildRaycast3DQuery(origin3d, direction3d, distance, layerMask), HitCount = hits.Length, Hits = hitList });
            }
            RaycastHit hit3d;
            var didHit = Physics.Raycast(origin3d, direction3d, out hit3d, distance, layerMask);
            var list3d = new List<RaycastHitInfo>();
            if (didHit) list3d.Add(PhysicsToolSupport.BuildRaycast3DHit(hit3d));
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Mode = "3d", Query = PhysicsToolSupport.BuildRaycast3DQuery(origin3d, direction3d, distance, layerMask),
                Hit = didHit, HitCount = didHit ? 1 : 0, Hits = list3d,
                NextActions = didHit ? new List<string>() : new List<string> { "No Physics collider matched the ray); adjust origin, direction, distance or layer mask." }
            });
        }
    }
}
#endif
