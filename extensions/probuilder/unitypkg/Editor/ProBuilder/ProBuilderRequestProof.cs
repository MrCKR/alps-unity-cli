#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>按各 Tool 请求字段读取 ProBuilder 实际状态；所有读取失败均拒绝完成。</summary>
    internal static class ProBuilderRequestProof
    {
        private const float Epsilon = 0.0001f;

        internal sealed class SourceMeshGeometry
        {
            internal Vector3[] Vertices;
            internal int[] Triangles;
        }

        public static List<Vector3> CapturePositions(object mesh)
        {
            var positions = ProBuilderReflection.GetPositions(mesh);
            return positions == null ? null : new List<Vector3>(positions);
        }

        internal static SourceMeshGeometry CaptureSourceMeshGeometry(MeshFilter filter)
        {
            var source = filter == null ? null : filter.sharedMesh;
            if (source == null || source.vertexCount < 1) return null;
            var vertices = source.vertices;
            var triangles = source.triangles;
            if (vertices == null || triangles == null || triangles.Length < 3 || triangles.Length % 3 != 0 || vertices.Length != source.vertexCount
                || triangles.Any(index => index < 0 || index >= vertices.Length)) return null;
            return new SourceMeshGeometry { Vertices = vertices, Triangles = triangles };
        }

        internal static bool ConvertedGeometryMatchesSource(object mesh, SourceMeshGeometry source)
        {
            var positions = CapturePositions(mesh);
            var faces = ProBuilderReflection.GetAllFaces(mesh);
            if (source == null || source.Vertices == null || source.Triangles == null || positions == null || faces == null
                || positions.Count != source.Vertices.Length || positions.Where((position, index) => !Approximately(position, source.Vertices[index])).Any()) return false;
            var expected = TriangleSignatures(source.Vertices, source.Triangles);
            var actual = new List<string>();
            foreach (var face in faces)
            {
                var indexes = ProBuilderReflection.GetFaceIndexes(face);
                if (indexes == null || indexes.Count < 3 || indexes.Count % 3 != 0 || indexes.Any(index => index < 0 || index >= positions.Count)) return false;
                for (var index = 0; index < indexes.Count; index += 3) actual.Add(TriangleSignature(positions[indexes[index]], positions[indexes[index + 1]], positions[indexes[index + 2]]));
            }
            actual.Sort(System.StringComparer.Ordinal);
            return expected.SequenceEqual(actual);
        }

        public static bool VerticesMovedByRequestedOffset(object mesh, IList<Vector3> before, int[] indices, Vector3 offset)
        {
            var after = CapturePositions(mesh);
            return before != null && after != null && indices != null && indices.Length > 0
                && indices.All(index => index >= 0 && index < before.Count && index < after.Count && Approximately(after[index], before[index] + offset));
        }

        public static bool TransformMatchesRequestedPivot(GameObject go, Vector3 expected)
        {
            return go != null && Approximately(go.transform.position, expected);
        }

        public static bool CenteredPivotPreservesWorldGeometry(GameObject go, object mesh, Vector3 expectedPivot, IList<Vector3> worldPositions)
        {
            var localPositions = CapturePositions(mesh);
            if (go == null || localPositions == null || worldPositions == null || localPositions.Count != worldPositions.Count
                || !Approximately(go.transform.position, expectedPivot)) return false;
            var renderer = go.GetComponent<Renderer>();
            return renderer != null && Approximately(renderer.bounds.center, expectedPivot)
                && localPositions.Where((position, index) => !Approximately(go.transform.TransformPoint(position), worldPositions[index])).Count() == 0;
        }

        public static bool TransformWasBaked(GameObject go, object mesh, IList<Vector3> beforePositions)
        {
            var after = CapturePositions(mesh);
            return go != null && after != null && beforePositions != null && after.Count == beforePositions.Count
                && go.transform.position == Vector3.zero && go.transform.rotation == Quaternion.identity && go.transform.localScale == Vector3.one
                && after.Where((value, index) => !Approximately(value, beforePositions[index])).Any();
        }

        public static bool PolygonWasCreatedFromRequestedVertices(object mesh, int[] vertexIndices, int expectedFaceCount)
        {
            var faces = ProBuilderReflection.GetAllFaces(mesh);
            if (faces == null || faces.Length != expectedFaceCount || vertexIndices == null) return false;
            var indexes = ProBuilderReflection.GetFaceIndexes(faces.GetValue(faces.Length - 1));
            var requested = vertexIndices.Distinct().ToArray();
            return indexes != null && indexes.Count == requested.Length && requested.All(indexes.Contains);
        }

        public static bool RequestedFacesHaveColor(object mesh, int[] faceIndices, Color color)
        {
            var positions = ProBuilderReflection.GetPositions(mesh);
            var colors = ProBuilderReflection.GetColors(mesh);
            var faces = ProBuilderReflection.GetFacesByIndices(mesh, faceIndices);
            if (positions == null || colors == null || faces == null || colors.Count != positions.Count) return false;
            foreach (var face in faces)
            {
                var indexes = ProBuilderReflection.GetFaceIndexes(face);
                if (indexes == null || indexes.Count == 0 || indexes.Any(index => index < 0 || index >= colors.Count || !Approximately(colors[index], color))) return false;
            }
            return true;
        }

        public static bool RequestedFacesUseMaterial(object mesh, int[] faceIndices, Material material)
        {
            var faces = ProBuilderReflection.GetFacesByIndices(mesh, faceIndices);
            var materials = ProBuilderReflection.GetMaterials(mesh);
            if (faces == null || materials == null || material == null) return false;
            var slot = materials.IndexOf(material);
            return slot >= 0 && faces.Cast<object>().All(face => ProBuilderReflection.GetFaceMaterialIndex(face) == slot);
        }

        public static bool RequestedFacesHaveUv(object mesh, int[] faceIndices, float? scale, float? offsetU, float? offsetV, float? rotation, bool flipU, bool flipV)
        {
            var faces = ProBuilderReflection.GetFacesByIndices(mesh, faceIndices);
            return faces != null && faces.Cast<object>().All(face => ProBuilderReflection.FaceUvMatches(face, scale, offsetU, offsetV, rotation, flipU, flipV));
        }

        public static bool RequestedFacesHaveSmoothingGroup(object mesh, int[] faceIndices, int smoothingGroup)
        {
            return ProBuilderReflection.HasSmoothingGroup(mesh, faceIndices, smoothingGroup);
        }

        public static bool RepairReducedInvalidGeometry(object mesh, int beforeDegenerateFaces, int beforeUnusedVertices)
        {
            var afterDegenerateFaces = ProBuilderReflection.CountDegenerateFaces(mesh);
            var afterUnusedVertices = ProBuilderReflection.CountUnusedVertices(mesh);
            return afterDegenerateFaces >= 0 && afterUnusedVertices >= 0
                && (afterDegenerateFaces < beforeDegenerateFaces || afterUnusedVertices < beforeUnusedVertices);
        }

        public static bool SelectedVerticesWereWelded(object mesh, int[] indices, float radius)
        {
            var positions = CapturePositions(mesh);
            if (positions == null || indices == null || indices.Length < 2 || indices.Any(index => index < 0 || index >= positions.Count)) return false;
            var first = positions[indices[0]];
            return indices.Skip(1).All(index => Vector3.Distance(first, positions[index]) <= radius + Epsilon);
        }

        public static bool CreatedShapeMatchesRequest(GameObject go, object mesh, Vector3 position, Vector3? requestedSize)
        {
            if (go == null || mesh == null || !ProBuilderReflection.HasReadableGeometry(mesh) || !Approximately(go.transform.position, position)) return false;
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return false;
            return !requestedSize.HasValue || Approximately(renderer.bounds.size, requestedSize.Value);
        }

        public static bool PolyShapeMatchesRequest(GameObject go, object mesh, IList<Vector3> points, float height, bool flipNormals)
        {
            if (go == null || !ProBuilderReflection.HasReadableGeometry(mesh) || points == null || points.Count < 3) return false;
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null || !Approximately(Mathf.Abs(renderer.bounds.size.y), Mathf.Abs(height))) return false;
            var positions = CapturePositions(mesh);
            if (positions == null || !points.All(point => positions.Any(position => Approximately(position.x, point.x) && Approximately(position.z, point.z)))) return false;
            return ProBuilderReflection.HasPolygonNormalOrientation(mesh, points, flipNormals);
        }

        public static bool SourcesWereRemovedAndGeometryIncluded(GameObject result, object mesh, int[] sourceIds, int minimumFaceCount)
        {
            return result != null && ProBuilderReflection.HasReadableGeometry(mesh) && ProBuilderReflection.GetFaceCount(mesh) >= minimumFaceCount
                && sourceIds.Skip(1).All(id => EditorUtility.InstanceIDToObject(id) == null);
        }

        private static List<string> TriangleSignatures(IList<Vector3> vertices, IList<int> triangles)
        {
            var signatures = new List<string>();
            for (var index = 0; index < triangles.Count; index += 3) signatures.Add(TriangleSignature(vertices[triangles[index]], vertices[triangles[index + 1]], vertices[triangles[index + 2]]));
            signatures.Sort(System.StringComparer.Ordinal);
            return signatures;
        }

        private static string TriangleSignature(Vector3 a, Vector3 b, Vector3 c)
        {
            var vertices = new[] { Describe(a), Describe(b), Describe(c) };
            System.Array.Sort(vertices, System.StringComparer.Ordinal);
            return string.Join("|", vertices);
        }

        private static string Describe(Vector3 value) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", value.x, value.y, value.z);
        private static bool Approximately(Vector3 left, Vector3 right) => (left - right).sqrMagnitude <= Epsilon * Epsilon;
        private static bool Approximately(Color left, Color right) => Mathf.Abs(left.r - right.r) <= Epsilon && Mathf.Abs(left.g - right.g) <= Epsilon && Mathf.Abs(left.b - right.b) <= Epsilon && Mathf.Abs(left.a - right.a) <= Epsilon;
        private static bool Approximately(float left, float right) => Mathf.Abs(left - right) <= Epsilon;
    }
}
#endif
