#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>普通属性、变换和拓扑 Tool 的最小业务读回；不捕获完整 canonical 状态。</summary>
    internal static class ProBuilderAtomicReadback
    {
        private const float Epsilon = 0.0001f;

        internal sealed class SourceMeshGeometry
        {
            internal Vector3[] Vertices;
            internal int[] Triangles;
        }

        internal sealed class ScaleBakeClaim
        {
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal Vector3[] ExpectedLocalPositions;
            internal Vector3[] ExpectedWorldPositions;
        }

        internal sealed class SplitVertexClaim
        {
            internal int[] Requested;
            internal Vector3[] Positions;
            internal bool HasObservableEffect;
        }

        /// <summary>读取 raw local positions，调用方必须将返回值视为不可变快照。</summary>
        internal static List<Vector3> CapturePositions(object mesh)
        {
            var positions = ProBuilderReflection.GetPositions(mesh);
            return positions == null ? null : new List<Vector3>(positions);
        }

        /// <summary>冻结 FreezeScaleTransform 的最小业务终态：local scale 烘焙、局部姿态不变且世界几何不变。</summary>
        internal static ScaleBakeClaim CaptureScaleBake(GameObject go, object mesh)
        {
            var positions = CapturePositions(mesh);
            if (go == null || positions == null || positions.Count == 0 || !Finite(go.transform.localScale)) return null;
            var expectedLocal = positions.Select(value => Vector3.Scale(value, go.transform.localScale)).ToArray();
            var expectedWorld = positions.Select(go.transform.TransformPoint).ToArray();
            if (expectedLocal.Any(value => !Finite(value)) || expectedWorld.Any(value => !Finite(value))) return null;
            return new ScaleBakeClaim
            {
                LocalPosition = go.transform.localPosition,
                LocalRotation = go.transform.localRotation,
                ExpectedLocalPositions = expectedLocal,
                ExpectedWorldPositions = expectedWorld
            };
        }

        /// <summary>验证 FreezeScaleTransform 的 fresh typed readback，不比较无关 mesh channel。</summary>
        internal static bool ScaleWasBaked(GameObject go, object mesh, ScaleBakeClaim claim)
        {
            var after = CapturePositions(mesh);
            if (go == null || claim == null || after == null || claim.ExpectedLocalPositions == null || claim.ExpectedWorldPositions == null
                || after.Count != claim.ExpectedLocalPositions.Length || after.Count != claim.ExpectedWorldPositions.Length
                || !Approximately(go.transform.localScale, Vector3.one)
                || go.transform.localPosition != claim.LocalPosition || go.transform.localRotation != claim.LocalRotation) return false;
            for (var index = 0; index < after.Count; index++)
                if (!Approximately(after[index], claim.ExpectedLocalPositions[index])
                    || !Approximately(go.transform.TransformPoint(after[index]), claim.ExpectedWorldPositions[index])) return false;
            return true;
        }

        /// <summary>冻结 split 请求对应的 raw indexes、positions 与是否存在可观察 shared partition 变化。</summary>
        internal static SplitVertexClaim CaptureSplitVertexClaim(object mesh, int[] requested)
        {
            var pb = mesh as ProBuilderMesh;
            var positions = CapturePositions(mesh);
            if (pb == null || requested == null || requested.Length == 0 || positions == null || pb.sharedVertices == null) return null;
            var groups = pb.sharedVertices.Select(value => value == null ? null : value.ToArray()).ToArray();
            if (groups.Any(value => value == null) || requested.Any(index => index < 0 || index >= positions.Count)) return null;
            var observable = false;
            foreach (var raw in requested)
            {
                var owners = groups.Where(group => group.Contains(raw)).ToArray();
                if (owners.Length != 1) return null;
                if (owners[0].Length > 1) observable = true;
            }
            return new SplitVertexClaim { Requested = (int[])requested.Clone(), Positions = positions.ToArray(), HasObservableEffect = observable };
        }

        /// <summary>fresh shared partition 中每个请求 raw 必须成为 singleton，且 split 不得移动顶点。</summary>
        internal static bool SplitVerticesMatch(object mesh, SplitVertexClaim claim)
        {
            var pb = mesh as ProBuilderMesh;
            var positions = CapturePositions(mesh);
            if (pb == null || claim == null || claim.Requested == null || claim.Positions == null || positions == null
                || positions.Count != claim.Positions.Length || pb.sharedVertices == null) return false;
            if (positions.Where((value, index) => !Approximately(value, claim.Positions[index])).Any()) return false;
            var groups = pb.sharedVertices.Select(value => value == null ? null : value.ToArray()).ToArray();
            return !groups.Any(value => value == null)
                && claim.Requested.All(raw => groups.Count(group => group.Contains(raw)) == 1 && groups.Single(group => group.Contains(raw)).Length == 1);
        }

        /// <summary>为 ProBuilder scene mutation 登记真正持有 serialized mesh 状态的对象。</summary>
        internal static void RegisterMeshUndo(GameObject go, object mesh, string name, bool includeTransform = false, params UnityEngine.Object[] additional)
        {
            var values = new List<UnityEngine.Object>();
            if (go != null) values.Add(go);
            if (includeTransform && go != null) values.Add(go.transform);
            if (mesh is UnityEngine.Object meshObject) values.Add(meshObject);
            var compiled = go == null ? null : go.GetComponent<MeshFilter>()?.sharedMesh;
            if (compiled != null) values.Add(compiled);
            if (additional != null) values.AddRange(additional.Where(value => value != null));
            var unique = values.Where(value => value != null).Distinct().ToArray();
            if (unique.Length > 0) Undo.RegisterCompleteObjectUndo(unique, name);
        }

        internal static SourceMeshGeometry CaptureSourceMeshGeometry(MeshFilter filter)
        {
            var source = filter == null ? null : filter.sharedMesh;
            if (source == null || source.vertexCount < 1) return null;
            var vertices = source.vertices;
            var triangles = source.triangles;
            if (vertices == null || triangles == null || triangles.Length < 3 || triangles.Length % 3 != 0
                || vertices.Length != source.vertexCount || vertices.Any(value => !Finite(value))
                || triangles.Any(index => index < 0 || index >= vertices.Length)) return null;
            return new SourceMeshGeometry { Vertices = vertices, Triangles = triangles };
        }

        /// <summary>以有向 triangle 几何多重集验证 MeshImporter 结果，允许 importer 重排或复制 raw vertices。</summary>
        internal static bool ConvertedGeometryMatchesSource(object mesh, SourceMeshGeometry source)
        {
            var positions = CapturePositions(mesh);
            var faces = ProBuilderReflection.GetAllFaces(mesh);
            if (source == null || positions == null || faces == null || positions.Count == 0) return false;
            var expected = TriangleSignatures(source.Vertices, source.Triangles);
            var actual = new List<string>();
            foreach (var value in faces)
            {
                var indexes = (value as Face)?.indexes;
                if (indexes == null || indexes.Count < 3 || indexes.Count % 3 != 0 || indexes.Any(index => index < 0 || index >= positions.Count)) return false;
                for (var index = 0; index < indexes.Count; index += 3)
                    actual.Add(TriangleSignature(positions[indexes[index]], positions[indexes[index + 1]], positions[indexes[index + 2]]));
            }
            actual.Sort(StringComparer.Ordinal);
            return expected.SequenceEqual(actual);
        }

        /// <summary>验证输入 polygon、挤出距离与 Package 使用的 up-relative normal 方向。</summary>
        internal static bool PolyShapeMatchesRequest(GameObject go, object mesh, IList<Vector3> points, float height, bool flipNormals)
        {
            var pb = mesh as ProBuilderMesh;
            var positions = CapturePositions(mesh);
            if (go == null || pb == null || points == null || points.Count < 3 || positions == null || !ReadableGeometry(mesh)
                || points.Any(point => !positions.Any(value => Approximately(value, point)))) return false;
            if (!Approximately(height, 0f) && !HasCommonExtrusionOffset(positions, points, Mathf.Abs(height))) return false;
            if (pb.faces == null || pb.faces.Count == 0 || pb.faces[0] == null) return false;
            var localNormal = UnityEngine.ProBuilder.Math.Normal(pb, pb.faces[0]);
            if (!Finite(localNormal)) return false;
            var dot = Vector3.Dot(go.transform.up, go.transform.TransformDirection(localNormal));
            return flipNormals ? dot <= Epsilon : dot >= -Epsilon;
        }

        internal static bool SourcesWereRemovedAndGeometryIncluded(GameObject result, object mesh, int[] sourceIds, int minimumFaceCount)
            => result != null && sourceIds != null && sourceIds.Length > 1 && sourceIds[0] == result.GetInstanceID()
                && ReadableGeometry(mesh) && ProBuilderReflection.GetFaceCount(mesh) >= minimumFaceCount
                && sourceIds.Skip(1).All(id => EditorUtility.InstanceIDToObject(id) == null);

        internal static bool PolygonWasCreatedFromRequestedVertices(object mesh, int[] vertexIndices, int expectedFaceCount)
        {
            var faces = ProBuilderReflection.GetAllFaces(mesh);
            if (faces == null || faces.Length != expectedFaceCount || vertexIndices == null) return false;
            var indexes = (faces.GetValue(faces.Length - 1) as Face)?.indexes;
            var requested = vertexIndices.Distinct().OrderBy(value => value).ToArray();
            var actual = indexes?.Distinct().OrderBy(value => value).ToArray();
            return actual != null && actual.SequenceEqual(requested);
        }

        internal static bool VerticesMovedByOffset(object mesh, IList<Vector3> before, int[] indices, Vector3 offset)
        {
            var after = CapturePositions(mesh);
            return before != null && after != null && indices != null && indices.Length > 0
                && indices.All(index => index >= 0 && index < before.Count && index < after.Count && Approximately(after[index], before[index] + offset));
        }

        internal static List<Vector3> CaptureWorldPositions(GameObject go, object mesh)
        {
            var positions = CapturePositions(mesh);
            return go == null || positions == null ? null : positions.Select(go.transform.TransformPoint).ToList();
        }

        internal static bool PivotPreservesWorldGeometry(GameObject go, object mesh, Vector3 expectedPivot, IList<Vector3> worldPositions, bool requireBoundsCenter)
        {
            var positions = CapturePositions(mesh);
            var renderer = go == null ? null : go.GetComponent<Renderer>();
            return go != null && positions != null && worldPositions != null && positions.Count == worldPositions.Count
                && Approximately(go.transform.position, expectedPivot)
                && (!requireBoundsCenter || renderer != null && Approximately(renderer.bounds.center, expectedPivot))
                && !positions.Where((value, index) => !Approximately(go.transform.TransformPoint(value), worldPositions[index])).Any();
        }

        internal static bool RequestedFacesHaveColor(object mesh, int[] faceIndices, Color color)
        {
            var pb = mesh as ProBuilderMesh;
            var colors = pb?.colors;
            var faces = ProBuilderReflection.GetFacesByIndices(mesh, faceIndices);
            return pb != null && colors != null && faces != null && colors.Count == pb.vertexCount
                && faces.Cast<Face>().All(face => face != null && face.indexes.Count > 0
                    && !face.indexes.Any(index => index < 0 || index >= colors.Count || !Approximately(colors[index], color)));
        }

        internal static bool RequestedFacesUseMaterial(object mesh, int[] faceIndices, Material material)
        {
            var pb = mesh as ProBuilderMesh;
            var faces = ProBuilderReflection.GetFacesByIndices(mesh, faceIndices);
            var materials = pb == null ? null : pb.GetComponent<Renderer>()?.sharedMaterials;
            var slot = materials == null || material == null ? -1 : Array.IndexOf(materials, material);
            return faces != null && slot >= 0 && faces.Cast<Face>().All(face => face != null && face.submeshIndex == slot);
        }

        internal static bool RequestedFacesHaveUv(object mesh, int[] faceIndices, float? scale, float? offsetU, float? offsetV, float? rotation, bool flipU, bool flipV)
        {
            var faces = ProBuilderReflection.GetFacesByIndices(mesh, faceIndices);
            return faces != null && faces.Cast<Face>().All(face => FaceUvMatches(face, scale, offsetU, offsetV, rotation, flipU, flipV));
        }

        internal static bool RequestedFacesHaveSmoothingGroup(object mesh, int[] faceIndices, int smoothingGroup)
        {
            var faces = ProBuilderReflection.GetFacesByIndices(mesh, faceIndices);
            return faces != null && faces.Cast<Face>().All(face => face != null && face.smoothingGroup == smoothingGroup);
        }

        private static bool FaceUvMatches(Face face, float? scale, float? offsetU, float? offsetV, float? rotation, bool flipU, bool flipV)
        {
            if (face == null) return false;
            var uv = face.uv;
            return (!scale.HasValue || Approximately(uv.scale.x, scale.Value) && Approximately(uv.scale.y, scale.Value))
                && (!offsetU.HasValue || Approximately(uv.offset.x, offsetU.Value))
                && (!offsetV.HasValue || Approximately(uv.offset.y, offsetV.Value))
                && (!rotation.HasValue || Approximately(uv.rotation, rotation.Value))
                && uv.flipU == flipU && uv.flipV == flipV;
        }

        private static bool HasCommonExtrusionOffset(IList<Vector3> positions, IList<Vector3> points, float distance)
        {
            var first = points[0];
            foreach (var candidate in positions)
            {
                var offset = candidate - first;
                if (!Finite(offset) || !Approximately(offset.magnitude, distance)) continue;
                if (points.All(point => positions.Any(value => Approximately(value, point + offset)))) return true;
            }
            return false;
        }

        private static bool ReadableGeometry(object mesh)
            => mesh != null && ProBuilderReflection.GetVertexCount(mesh) > 0 && ProBuilderReflection.GetFaceCount(mesh) > 0 && CapturePositions(mesh) != null;

        private static List<string> TriangleSignatures(IList<Vector3> vertices, IList<int> triangles)
        {
            var result = new List<string>();
            for (var index = 0; index < triangles.Count; index += 3)
                result.Add(TriangleSignature(vertices[triangles[index]], vertices[triangles[index + 1]], vertices[triangles[index + 2]]));
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static string TriangleSignature(Vector3 a, Vector3 b, Vector3 c)
        {
            var values = new[] { Describe(a), Describe(b), Describe(c) };
            var rotations = new[]
            {
                values[0] + "|" + values[1] + "|" + values[2],
                values[1] + "|" + values[2] + "|" + values[0],
                values[2] + "|" + values[0] + "|" + values[1]
            };
            Array.Sort(rotations, StringComparer.Ordinal);
            return rotations[0];
        }

        private static string Describe(Vector3 value)
            => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", value.x, value.y, value.z);

        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Approximately(Vector3 left, Vector3 right) => (left - right).sqrMagnitude <= Epsilon * Epsilon;
        private static bool Approximately(Color left, Color right) => Mathf.Abs(left.r - right.r) <= Epsilon && Mathf.Abs(left.g - right.g) <= Epsilon && Mathf.Abs(left.b - right.b) <= Epsilon && Mathf.Abs(left.a - right.a) <= Epsilon;
        private static bool Approximately(float left, float right) => Mathf.Abs(left - right) <= Epsilon;
    }
}
#endif
