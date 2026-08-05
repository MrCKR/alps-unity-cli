#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>为拓扑编辑工具保存请求选区的稳定几何读回，并按操作语义验证提交结果。</summary>
    internal static class ProBuilderOperationProof
    {
        internal static int CheckedInt(long value, string name)
        {
            if (value < int.MinValue || value > int.MaxValue)
                throw ProBuilderErrors.SchemaInvalid(name + " must be within Int32 range.");
            return (int)value;
        }

        internal static float CheckedFloat(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < -float.MaxValue || value > float.MaxValue)
                throw ProBuilderErrors.SchemaInvalid(name + " must be a finite single-precision value.");
            return (float)value;
        }

        internal static float CheckedPositiveFloat(double value, string name)
        {
            var converted = CheckedFloat(value, name);
            if (converted <= 0) throw ProBuilderErrors.SchemaInvalid(name + " must be positive.");
            return converted;
        }

        internal static Vector3 CheckedVector3(double x, double y, double z, string name)
        {
            return new Vector3(CheckedFloat(x, name + ".x"), CheckedFloat(y, name + ".y"), CheckedFloat(z, name + ".z"));
        }

        // effect 前冻结 Unity 对象身份；readback 只能与这份 claim 比较，不能重新生成前态。
        internal sealed class ObjectIdentityClaim
        {
            internal int InstanceId;
            internal string GlobalObjectId;
            internal int SceneHandle;
        }

        internal static ObjectIdentityClaim CaptureIdentity(GameObject go)
        {
            if (go == null || !go.scene.IsValid() || !go.scene.isLoaded) return null;
            return new ObjectIdentityClaim
            {
                InstanceId = go.GetInstanceID(),
                GlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                SceneHandle = go.scene.handle
            };
        }

        /// <summary>委托给 canonical snapshot 捕获 effect 前的完整 raw/shared/face/edge 闭集；touched 面可限制退化检查范围。</summary>
        internal static CanonicalMeshSnapshot CaptureCanonicalSnapshot(GameObject go, int[] touchedFaceOccurrences = null)
        {
            return CanonicalMeshSnapshot.Capture(go, touchedFaceOccurrences);
        }

        /// <summary>委托 canonical identity claim 做 fresh 组件解析；禁止用 effect 后对象重新 claim 身份。</summary>
        internal static UnityEngine.ProBuilder.ProBuilderMesh ResolveFreshCanonicalMesh(string target, CanonicalMeshSnapshot.ObjectIdentityClaim claim)
        {
            return CanonicalMeshSnapshot.ResolveFreshMesh(target, claim);
        }

        internal static void ValidateFaceIndices(object mesh, int[] indices)
        {
            if (indices == null || indices.Length == 0 || indices.Distinct().Count() != indices.Length || ProBuilderReflection.GetFacesByIndices(mesh, indices) == null)
                throw ProBuilderErrors.SchemaInvalid("faceIndices contains an invalid face index.");
        }

        internal static void ValidateVertexIndices(object mesh, int[] indices)
        {
            var count = ProBuilderReflection.GetVertexCount(mesh);
            if (indices == null || indices.Length == 0 || indices.Distinct().Count() != indices.Length || count < 0 || indices.Any(index => index < 0 || index >= count))
                throw ProBuilderErrors.SchemaInvalid("vertexIndices contains an invalid vertex index.");
        }

        internal static Array ValidateEdges(object mesh, int[] edgeIndices, ProBuilderInput.EdgePair[] pairs)
        {
            if (edgeIndices != null && edgeIndices.Length > 0 && pairs != null && pairs.Length > 0)
                throw ProBuilderErrors.SchemaInvalid("Pass exactly one of edgeIndices or edges.");
            var edges = ProBuilderReflection.ResolveEdges(mesh, edgeIndices, pairs);
            if (edges == null || edges.Length == 0 || (edgeIndices != null && edgeIndices.Length > 0 && edges.Length != edgeIndices.Length)
                || (pairs != null && pairs.Length > 0 && edges.Length != pairs.Length))
                throw ProBuilderErrors.SchemaInvalid("The requested edge selection contains an invalid or unresolved edge.");
            return edges;
        }

        /// <summary>effect 后、tangent refresh 前为新增 quad 写入固定非退化 UV0；先完整验证范围，再一次性修改。</summary>
        internal static bool ApplyDeterministicQuadUvs(object value, int firstFaceIndex, int faceCount)
        {
            var mesh = value as ProBuilderMesh; var faces = mesh == null ? null : mesh.faces; var textures = mesh?.textures?.ToArray();
            if (mesh == null || faces == null || textures == null || textures.Length != mesh.vertexCount || firstFaceIndex < 0 || faceCount < 1 || firstFaceIndex + faceCount > faces.Count) return false;
            var targets = new List<Tuple<Face, int>>();
            for (var index = 0; index < faceCount; index++)
            {
                var face = faces[firstFaceIndex + index]; var stream = face?.indexes?.ToArray();
                if (stream == null || stream.Length != 6) return false;
                var raw = stream.Distinct().OrderBy(item => item).ToArray();
                if (raw.Length != 4 || raw[3] != raw[0] + 3) return false;
                var edgeOrBridge = new[] { raw[0] + 2, raw[0] + 1, raw[0], raw[0] + 2, raw[0] + 3, raw[0] + 1 };
                var faceExtrude = new[] { raw[0], raw[0] + 1, raw[0] + 2, raw[0] + 1, raw[0] + 3, raw[0] + 2 };
                if (!stream.SequenceEqual(edgeOrBridge) && !stream.SequenceEqual(faceExtrude)) return false;
                targets.Add(Tuple.Create(face, raw[0]));
            }
            var layout = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            foreach (var target in targets)
            {
                target.Item1.manualUV = true; target.Item1.textureGroup = -1;
                for (var offset = 0; offset < layout.Length; offset++) textures[target.Item2 + offset] = layout[offset];
            }
            mesh.textures = textures;
            return true;
        }

        internal static GameObject ResolveFreshObject(string target, ObjectIdentityClaim claim)
        {
            var fresh = ProBuilderInput.ResolveTarget(target);
            if (fresh == null || claim == null || fresh.GetInstanceID() != claim.InstanceId || !fresh.scene.IsValid() || !fresh.scene.isLoaded || fresh.scene.handle != claim.SceneHandle)
                return null;
            return string.Equals(GlobalObjectId.GetGlobalObjectIdSlow(fresh).ToString(), claim.GlobalObjectId, StringComparison.Ordinal) ? fresh : null;
        }

        internal static object ResolveFreshMesh(string target, ObjectIdentityClaim claim)
        {
            return ProBuilderReflection.GetProBuilderMesh(ResolveFreshObject(target, claim));
        }

    }
}
#endif
