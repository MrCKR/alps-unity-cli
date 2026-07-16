#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.overlap — 球体/盒子重叠查询(3D/2D)。
    /// </summary>
    public class PhysicsOverlapTool : IUnityCliTool<PhysicsOverlapTool.Request, PhysicsOverlapTool.Result>
    {
        public sealed class Request
        {
            public string Mode { get; set; }
            public string Shape { get; set; }
            public float OriginX { get; set; }
            public float OriginY { get; set; }
            public float OriginZ { get; set; }
            public float Radius { get; set; } = 1f;
            public float SizeX { get; set; } = 1f;
            public float SizeY { get; set; } = 1f;
            public float SizeZ { get; set; } = 1f;
            public float Angle { get; set; }
            public int? LayerMask { get; set; }
        }

        public sealed class Result
        {
            public string Mode { get; set; }
            public Vector3Data Origin { get; set; }
            public long HitCount { get; set; }
            public List<ColliderRef> Hits { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.overlap",
            Summary = "Find colliders overlapping a sphere or box volume in 3D or 2D.",
            Description = "Overlap query: sphere (default) or box. mode=3d (default) or 2d. 2D supports circle (default) and box shapes.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false,
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Overlap sphere at origin", Input = new Request { Radius = 5f } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var mode = PhysicsToolSupport.NormalizeMode(request.Mode);
            var origin = new Vector3(request.OriginX, request.OriginY, request.OriginZ);
            var layerMask = request.LayerMask ?? Physics.DefaultRaycastLayers;
            if (mode == "2d")
            {
                var origin2d = new Vector2(origin.x, origin.y);
                Collider2D[] colliders2d;
                if (string.Equals(request.Shape, "box", StringComparison.OrdinalIgnoreCase))
                {
                    var size = new Vector2(request.SizeX, request.SizeY);
                    colliders2d = Physics2D.OverlapBoxAll(origin2d, size, request.Angle, layerMask);
                }
                else
                {
                    colliders2d = Physics2D.OverlapCircleAll(origin2d, Math.Max(0.0001f, request.Radius), layerMask);
                }
                var hits = new List<ColliderRef>();
                foreach (var c in colliders2d) hits.Add(PhysicsToolSupport.BuildColliderRef(c.gameObject, c.GetType().Name));
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Mode = "2d", Origin = new Vector3Data(new Vector3(origin2d.x, origin2d.y, 0)), HitCount = colliders2d.Length, Hits = hits });
            }
            Collider[] colliders;
            if (string.Equals(request.Shape, "box", StringComparison.OrdinalIgnoreCase))
            {
                var halfExtents = new Vector3(request.SizeX, request.SizeY, request.SizeZ) * 0.5f;
                colliders = Physics.OverlapBox(origin, halfExtents, Quaternion.identity, layerMask);
            }
            else
            {
                colliders = Physics.OverlapSphere(origin, Math.Max(0.0001f, request.Radius), layerMask);
            }
            var hits3d = new List<ColliderRef>();
            foreach (var c in colliders) hits3d.Add(PhysicsToolSupport.BuildColliderRef(c.gameObject, c.GetType().Name));
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Mode = "3d", Origin = new Vector3Data(origin), HitCount = colliders.Length, Hits = hits3d });
        }
    }
}
#endif
