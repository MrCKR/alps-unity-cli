#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.collision-matrix — 读取/写入 Unity 层碰撞矩阵(32 层)。
    /// 对齐旧 PhysicsCollisionMatrix handler。
    /// </summary>
    public class PhysicsCollisionMatrixTool : IUnityCliTool<PhysicsCollisionMatrixTool.Request, PhysicsCollisionMatrixTool.Result>
    {
        public sealed class Request
        {
            public string LayerA { get; set; }
            public string LayerB { get; set; }
            public bool? Ignore { get; set; }
        }

        public sealed class Result
        {
            public string Mode { get; set; }
            public long LayerCount { get; set; }
            public List<CollisionLayerEntry> Layers { get; set; }
            public List<CollisionMatrixRow> Matrix { get; set; }
            public CollisionPairInfo Collision { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.collision-matrix",
            Summary = "Read or update the Unity 32-layer physics collision matrix, optionally ignoring a single layer pair.",
            Description = "Read the full 32-layer collision matrix by default. Pass layerA and layerB with ignore=true/false to update a single pair. Layer names or numeric indexes are accepted.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read full collision matrix", Input = new Request { } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            // 单层对读写模式
            if (!string.IsNullOrEmpty(request.LayerA) || !string.IsNullOrEmpty(request.LayerB))
            {
                if (string.IsNullOrEmpty(request.LayerA) || string.IsNullOrEmpty(request.LayerB))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_COLLISION_PAIR_REQUIRED", "Reading or writing a single collision pair requires both layerA and layerB.", "Pass both layerA and layerB, or omit both to read the full 32-layer matrix.");
                }
                var layerA = PhysicsToolSupport.ParseLayer(request.LayerA);
                var layerB = PhysicsToolSupport.ParseLayer(request.LayerB);
                if (request.Ignore.HasValue)
                {
                    Physics.IgnoreLayerCollision(layerA, layerB, request.Ignore.Value);
                }
                var effects = new List<PhysicsPlannedEffect>();
                if (request.Ignore.HasValue)
                    effects.Add(new PhysicsPlannedEffect { Type = "physics.collision-matrix", Property = "ignore", Value = request.Ignore.Value.ToString().ToLowerInvariant() });
                return new Result
                {
                    Collision = new CollisionPairInfo { LayerA = layerA, LayerB = layerB, Ignored = Physics.GetIgnoreLayerCollision(layerA, layerB) },
                    PlannedEffects = effects
                };
            }
            // 全量矩阵读取
            var layers = new List<CollisionLayerEntry>();
            for (var i = 0; i < 32; i++)
                layers.Add(new CollisionLayerEntry { Index = i, Name = LayerMask.LayerToName(i) });
            var matrix = new List<CollisionMatrixRow>();
            for (var a = 0; a < 32; a++)
            {
                var pairs = new List<CollisionMatrixPair>();
                for (var b = 0; b < 32; b++)
                    pairs.Add(new CollisionMatrixPair { Layer = b, Name = LayerMask.LayerToName(b), Ignored = Physics.GetIgnoreLayerCollision(a, b) });
                matrix.Add(new CollisionMatrixRow { Layer = a, Name = LayerMask.LayerToName(a), Pairs = pairs });
            }
            return new Result
            {
                Mode = "3d",
                LayerCount = 32,
                Layers = layers,
                Matrix = matrix,
                PlannedEffects = new List<PhysicsPlannedEffect>()
            };
        }
    }
}
#endif
