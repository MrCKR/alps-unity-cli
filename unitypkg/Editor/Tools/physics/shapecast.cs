#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.shapecast — 球体/盒子/胶囊体投射查询(3D/2D)。
    /// </summary>
    public class PhysicsShapecastTool : IUnityCliTool<PhysicsShapecastTool.Request, PhysicsShapecastTool.Result>
    {
        public sealed class Request
        {
            public string Shape { get; set; } = "sphere";
            public string Mode { get; set; }
            public float OriginX { get; set; }
            public float OriginY { get; set; }
            public float OriginZ { get; set; }
            public float DirectionX { get; set; }
            public float DirectionY { get; set; }
            public float DirectionZ { get; set; } = 1f;
            public float Distance { get; set; } = 100f;
            public float Radius { get; set; } = 0.5f;
            public float HalfExtentX { get; set; } = 0.5f;
            public float HalfExtentY { get; set; } = 0.5f;
            public float HalfExtentZ { get; set; } = 0.5f;
            public float Height { get; set; } = 1f;
            public int CapsuleDirection { get; set; } = 1;
            public float Angle { get; set; }
            public float SizeX { get; set; }
            public float SizeY { get; set; }
            public float? Point1X { get; set; }
            public float Point1Y { get; set; }
            public float Point1Z { get; set; }
            public float Point2X { get; set; }
            public float Point2Y { get; set; }
            public float Point2Z { get; set; }
            public int? LayerMask { get; set; }
        }

        public sealed class Result
        {
            public RaycastQuery Query { get; set; }
            public long HitCount { get; set; }
            public List<RaycastHitInfo> Hits { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.shapecast",
            Summary = "Cast a sphere, box, or capsule shape and return all hits in 3D or 2D.",
            Description = "Shape cast: sphere (default), box, or capsule. mode=3d (default) or 2d. 2D supports circle (default), box, and capsule.",
            Group = "physics",DestructiveHint = false,
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Sphere cast forward", Input = new Request { Shape = "sphere", DirectionZ = 1f } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var shape = request.Shape ?? "sphere";
            var mode = PhysicsToolSupport.NormalizeMode(request.Mode);
            var distance = Math.Max(0f, request.Distance);
            var layerMask = request.LayerMask ?? Physics.DefaultRaycastLayers;
            var origin = new Vector3(request.OriginX, request.OriginY, request.OriginZ);
            var direction = PhysicsToolSupport.NormalizeVector3(new Vector3(request.DirectionX, request.DirectionY, request.DirectionZ));
            var radius = request.Radius;
            var halfExtents = new Vector3(request.HalfExtentX, request.HalfExtentY, request.HalfExtentZ);

            if (mode == "2d")
            {
                var origin2d = new Vector2(origin.x, origin.y);
                var direction2d = PhysicsToolSupport.NormalizeVector2(new Vector2(direction.x, direction.y));
                RaycastHit2D[] hits2d;
                if (shape == "box")
                {
                    var size = new Vector2(request.SizeX > 0 ? request.SizeX : halfExtents.x * 2f, request.SizeY > 0 ? request.SizeY : halfExtents.y * 2f);
                    hits2d = Physics2D.BoxCastAll(origin2d, size, request.Angle, direction2d, distance, layerMask);
                }
                else if (shape == "capsule")
                {
                    var size = new Vector2(request.SizeX > 0 ? request.SizeX : radius * 2f, request.SizeY > 0 ? request.SizeY : request.Height);
                    hits2d = Physics2D.CapsuleCastAll(origin2d, size, PhysicsToolSupport.ParseCapsuleDirection2D(null, request.CapsuleDirection), request.Angle, direction2d, distance, layerMask);
                }
                else
                {
                    hits2d = Physics2D.CircleCastAll(origin2d, radius, direction2d, distance, layerMask);
                }
                var hitList = new List<RaycastHitInfo>();
                foreach (var h in hits2d) hitList.Add(PhysicsToolSupport.BuildRaycast2DHit(h));
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Query = PhysicsToolSupport.BuildRaycast2DQuery(origin2d, direction2d, distance, layerMask), HitCount = hits2d.Length, Hits = hitList });
            }
            RaycastHit[] hits;
            if (shape == "box")
            {
                hits = Physics.BoxCastAll(origin, halfExtents, direction, Quaternion.identity, distance, layerMask);
            }
            else if (shape == "capsule")
            {
                var capDir = request.CapsuleDirection;
                Vector3 point1, point2;
                if (request.Point1X.HasValue)
                {
                    point1 = new Vector3(request.Point1X.Value, request.Point1Y, request.Point1Z);
                    point2 = new Vector3(request.Point2X, request.Point2Y, request.Point2Z);
                }
                else
                {
                    var halfH = Math.Max(0, request.Height / 2f - radius);
                    var offset = capDir == 0 ? new Vector3(halfH, 0, 0) : capDir == 2 ? new Vector3(0, 0, halfH) : new Vector3(0, halfH, 0);
                    point1 = origin + offset;
                    point2 = origin - offset;
                }
                hits = Physics.CapsuleCastAll(point1, point2, radius, direction, distance, layerMask);
            }
            else
            {
                hits = Physics.SphereCastAll(origin, radius, direction, distance, layerMask);
            }
            var list = new List<RaycastHitInfo>();
            foreach (var h in hits) list.Add(PhysicsToolSupport.BuildRaycast3DHit(h));
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Query = PhysicsToolSupport.BuildRaycast3DQuery(origin, direction, distance, layerMask), HitCount = hits.Length, Hits = list });
        }
    }
}
#endif
