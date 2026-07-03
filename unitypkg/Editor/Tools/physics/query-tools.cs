#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.raycast — 3D/2D 射线查询,返回首个命中或全部命中。
    /// 对齐旧 PhysicsRaycast handler。
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
            Group = "physics", Version = "1.0.0", DestructiveHint = false,
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "3D raycast forward", Input = new Request { OriginY = 1f, DirectionZ = 1f } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
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
                    return new Result { Mode = "2d", Query = PhysicsToolSupport.BuildRaycast2DQuery(origin, direction, distance, layerMask), HitCount = hits.Length, Hits = hitList };
                }
                var hit = Physics2D.Raycast(origin, direction, distance, layerMask);
                var list = new List<RaycastHitInfo>();
                if (hit.collider != null) list.Add(PhysicsToolSupport.BuildRaycast2DHit(hit));
                return new Result
                {
                    Mode = "2d", Query = PhysicsToolSupport.BuildRaycast2DQuery(origin, direction, distance, layerMask),
                    Hit = hit.collider != null, HitCount = hit.collider != null ? 1 : 0, Hits = list,
                    NextActions = hit.collider != null ? new List<string>() : new List<string> { "No Physics collider matched the ray; adjust origin, direction, distance or layer mask." }
                };
            }
            var origin3d = new Vector3(request.OriginX, request.OriginY, request.OriginZ);
            var direction3d = PhysicsToolSupport.NormalizeVector3(new Vector3(request.DirectionX, request.DirectionY, request.DirectionZ));
            if (request.All)
            {
                var hits = Physics.RaycastAll(origin3d, direction3d, distance, layerMask);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                var hitList = new List<RaycastHitInfo>();
                foreach (var h in hits) hitList.Add(PhysicsToolSupport.BuildRaycast3DHit(h));
                return new Result { Mode = "3d", Query = PhysicsToolSupport.BuildRaycast3DQuery(origin3d, direction3d, distance, layerMask), HitCount = hits.Length, Hits = hitList };
            }
            RaycastHit hit3d;
            var didHit = Physics.Raycast(origin3d, direction3d, out hit3d, distance, layerMask);
            var list3d = new List<RaycastHitInfo>();
            if (didHit) list3d.Add(PhysicsToolSupport.BuildRaycast3DHit(hit3d));
            return new Result
            {
                Mode = "3d", Query = PhysicsToolSupport.BuildRaycast3DQuery(origin3d, direction3d, distance, layerMask),
                Hit = didHit, HitCount = didHit ? 1 : 0, Hits = list3d,
                NextActions = didHit ? new List<string>() : new List<string> { "No Physics collider matched the ray; adjust origin, direction, distance or layer mask." }
            };
        }
    }

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
            public string Command { get; set; }
            public string Mode { get; set; }
            public long HitCount { get; set; }
            public List<RaycastHitInfo> Hits { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.raycast-all",
            Summary = "Cast a 3D or 2D ray and return all hits along the path.",
            Description = "Cast a ray and return all collider hits, sorted by distance (3D). mode=3d (default) or 2d.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false,
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "All hits forward", Input = new Request { OriginY = 1f, DirectionZ = 1f } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
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
                return new Result { Command = "physics.raycast-all", Mode = "2d", HitCount = hits.Length, Hits = hitList };
            }
            var origin3d = new Vector3(request.OriginX, request.OriginY, request.OriginZ);
            var direction3d = PhysicsToolSupport.NormalizeVector3(new Vector3(request.DirectionX, request.DirectionY, request.DirectionZ));
            var hits3d = Physics.RaycastAll(origin3d, direction3d, distance, layerMask);
            Array.Sort(hits3d, (a, b) => a.distance.CompareTo(b.distance));
            var list = new List<RaycastHitInfo>();
            foreach (var h in hits3d) list.Add(PhysicsToolSupport.BuildRaycast3DHit(h));
            return new Result { Command = "physics.raycast-all", Mode = "3d", HitCount = hits3d.Length, Hits = list };
        }
    }

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
            public string Command { get; set; }
            public string Mode { get; set; }
            public bool Hit { get; set; }
            public RaycastHitInfo HitInfo { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.linecast",
            Summary = "Check if a 3D or 2D line segment between two points is blocked by a collider.",
            Description = "Cast a line from start to end and report whether any collider blocks the path. mode=3d (default) or 2d.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false,
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Linecast between two points", Input = new Request { StartX = 0, EndX = 10 } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var mode = PhysicsToolSupport.NormalizeMode(request.Mode);
            var layerMask = request.LayerMask ?? Physics.DefaultRaycastLayers;
            var start = new Vector3(request.StartX, request.StartY, request.StartZ);
            var end = new Vector3(request.EndX, request.EndY, request.EndZ);
            if (mode == "2d")
            {
                var hit = Physics2D.Linecast(new Vector2(start.x, start.y), new Vector2(end.x, end.y), layerMask);
                return new Result { Command = "physics.linecast", Mode = "2d", Hit = hit.collider != null };
            }
            RaycastHit hit3d;
            var found = Physics.Linecast(start, end, out hit3d, layerMask);
            return new Result { Command = "physics.linecast", Mode = "3d", Hit = found, HitInfo = found ? PhysicsToolSupport.BuildRaycast3DHit(hit3d) : null };
        }
    }

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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
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
                return new Result { Mode = "2d", Origin = new Vector3Data(new Vector3(origin2d.x, origin2d.y, 0)), HitCount = colliders2d.Length, Hits = hits };
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
            return new Result { Mode = "3d", Origin = new Vector3Data(origin), HitCount = colliders.Length, Hits = hits3d };
        }
    }

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
            public string Command { get; set; }
            public string Mode { get; set; }
            public string Shape { get; set; }
            public RaycastQuery Query { get; set; }
            public long HitCount { get; set; }
            public List<RaycastHitInfo> Hits { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.shapecast",
            Summary = "Cast a sphere, box, or capsule shape and return all hits in 3D or 2D.",
            Description = "Shape cast: sphere (default), box, or capsule. mode=3d (default) or 2d. 2D supports circle (default), box, and capsule.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false,
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Sphere cast forward", Input = new Request { Shape = "sphere", DirectionZ = 1f } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
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
                return new Result { Command = "physics.shapecast", Mode = "2d", Shape = shape, Query = PhysicsToolSupport.BuildRaycast2DQuery(origin2d, direction2d, distance, layerMask), HitCount = hits2d.Length, Hits = hitList };
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
            return new Result { Command = "physics.shapecast", Mode = "3d", Shape = shape, Query = PhysicsToolSupport.BuildRaycast3DQuery(origin, direction, distance, layerMask), HitCount = hits.Length, Hits = list };
        }
    }
}
#endif
