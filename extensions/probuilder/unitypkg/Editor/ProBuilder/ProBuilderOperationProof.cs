#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

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

        internal sealed class Selection
        {
            internal int VertexCount;
            internal int FaceCount;
            internal int EdgeCount;
            internal string[] Faces;
            internal string[] Edges;
            internal string[] Vertices;
            internal string[] Smoothing;
            internal string[] SharedVertices;
        }

        internal static void ValidateFaceIndices(object mesh, int[] indices)
        {
            if (indices == null || indices.Length == 0 || ProBuilderReflection.GetFacesByIndices(mesh, indices) == null)
                throw ProBuilderErrors.SchemaInvalid("faceIndices contains an invalid face index.");
        }

        internal static void ValidateVertexIndices(object mesh, int[] indices)
        {
            var count = ProBuilderReflection.GetVertexCount(mesh);
            if (indices == null || indices.Length == 0 || count < 0 || indices.Any(index => index < 0 || index >= count))
                throw ProBuilderErrors.SchemaInvalid("vertexIndices contains an invalid vertex index.");
        }

        internal static Array ValidateEdges(object mesh, int[] edgeIndices, ProBuilderInput.EdgePair[] pairs)
        {
            var edges = ProBuilderReflection.ResolveEdges(mesh, edgeIndices, pairs);
            if (edges == null || edges.Length == 0 || (edgeIndices != null && edgeIndices.Length > 0 && edges.Length != edgeIndices.Length)
                || (pairs != null && pairs.Length > 0 && edges.Length != pairs.Length))
                throw ProBuilderErrors.SchemaInvalid("The requested edge selection contains an invalid or unresolved edge.");
            return edges;
        }

        internal static Selection Capture(object mesh, int[] faceIndices = null, Array edges = null, int[] vertexIndices = null)
        {
            var positions = ProBuilderReflection.GetPositions(mesh);
            var faces = faceIndices == null ? null : ProBuilderReflection.GetFacesByIndices(mesh, faceIndices);
            if (positions == null || (faceIndices != null && faces == null)) return null;
            return new Selection
            {
                VertexCount = ProBuilderReflection.GetVertexCount(mesh),
                FaceCount = ProBuilderReflection.GetFaceCount(mesh),
                EdgeCount = ProBuilderReflection.GetEdgeCount(mesh),
                Faces = faces == null ? Array.Empty<string>() : faces.Cast<object>().Select(face => FaceSignature(face, positions, false)).ToArray(),
                Edges = edges == null ? Array.Empty<string>() : edges.Cast<object>().Select(edge => EdgeSignature(edge, positions)).ToArray(),
                Vertices = vertexIndices == null ? Array.Empty<string>() : vertexIndices.Select(index => VertexSignature(positions, index)).ToArray(),
                Smoothing = faces == null ? Array.Empty<string>() : faces.Cast<object>().Select(SmoothingSignature).ToArray(),
                SharedVertices = SharedVertexSignatures(mesh, vertexIndices)
            };
        }

        internal static object ResolveFreshMesh(string target, GameObject expected)
        {
            var fresh = ProBuilderInput.ResolveTarget(target);
            if (fresh == null || expected == null || fresh.GetInstanceID() != expected.GetInstanceID()) return null;
            return ProBuilderReflection.GetProBuilderMesh(fresh);
        }

        internal static bool HasInsertedVertices(Selection before, Selection after) => Readable(before, after) && after.VertexCount > before.VertexCount;
        internal static bool HasTopologyExpansion(Selection before, Selection after) => Readable(before, after) && (after.VertexCount > before.VertexCount || after.FaceCount > before.FaceCount || after.EdgeCount > before.EdgeCount);
        internal static bool HasRemovedRequestedFaces(Selection before, Selection after) => Readable(before, after) && before.Faces.Length > 0 && before.Faces.All(face => !after.Faces.Contains(face)) && after.FaceCount < before.FaceCount;
        internal static bool HasMergedRequestedFaces(Selection before, Selection after) => Readable(before, after) && after.FaceCount < before.FaceCount && before.Faces.Length > 1 && before.Faces.All(face => !after.Faces.Contains(face));
        internal static bool HasSubdividedRequestedFaces(Selection before, Selection after) => Readable(before, after) && after.FaceCount > before.FaceCount && before.Faces.Any(face => !after.Faces.Contains(face));
        internal static bool HasFlippedRequestedFaces(Selection before, object mesh, int[] faceIndices)
        {
            var positions = ProBuilderReflection.GetPositions(mesh);
            var faces = ProBuilderReflection.GetFacesByIndices(mesh, faceIndices);
            return Readable(before, Capture(mesh, faceIndices)) && positions != null && faces != null
                && faces.Cast<object>().Select(face => FaceSignature(face, positions, true)).Any(after => before.Faces.Contains(after));
        }
        internal static bool HasChangedSmoothing(Selection before, Selection after) => Readable(before, after) && before.Smoothing.Length > 0 && !before.Smoothing.SequenceEqual(after.Smoothing);
        internal static bool HasMergedVertices(Selection before, Selection after) => Readable(before, after) && (after.VertexCount < before.VertexCount || before.Vertices.Any(vertex => !after.Vertices.Contains(vertex)));
        internal static bool HasSplitVertices(Selection before, Selection after) => Readable(before, after) && before.SharedVertices.Length > 0 && !before.SharedVertices.SequenceEqual(after.SharedVertices);
        internal static bool ContainsFaceSignatures(object mesh, IEnumerable<string> signatures)
        {
            var positions = ProBuilderReflection.GetPositions(mesh);
            var faces = ProBuilderReflection.GetAllFaces(mesh);
            return positions != null && faces != null && signatures.All(signature => faces.Cast<object>().Any(face => FaceSignature(face, positions, false) == signature));
        }

        private static bool Readable(Selection before, Selection after) => before != null && after != null && before.VertexCount >= 0 && after.VertexCount >= 0 && before.FaceCount >= 0 && after.FaceCount >= 0;
        private static string VertexSignature(IList<Vector3> positions, int index) => index >= 0 && index < positions.Count ? Describe(positions[index]) : null;
        private static string EdgeSignature(object edge, IList<Vector3> positions)
        {
            var a = ProBuilderReflection.GetEdgeVertexA(edge);
            var b = ProBuilderReflection.GetEdgeVertexB(edge);
            var ends = new[] { VertexSignature(positions, a), VertexSignature(positions, b) };
            Array.Sort(ends, StringComparer.Ordinal);
            return string.Join("|", ends);
        }
        private static string FaceSignature(object face, IList<Vector3> positions, bool reverse)
        {
            var indexes = face.GetType().GetProperty("indexes")?.GetValue(face) as IEnumerable;
            if (indexes == null) return null;
            var vertices = indexes.Cast<object>().Select(value => VertexSignature(positions, Convert.ToInt32(value, CultureInfo.InvariantCulture))).ToArray();
            if (reverse) Array.Reverse(vertices);
            return string.Join("|", vertices);
        }
        private static string SmoothingSignature(object face) => Convert.ToString(face.GetType().GetProperty("smoothingGroup")?.GetValue(face), CultureInfo.InvariantCulture);
        private static string[] SharedVertexSignatures(object mesh, int[] indices)
        {
            var groups = mesh.GetType().GetProperty("sharedVertices")?.GetValue(mesh) as IEnumerable;
            if (groups == null || indices == null) return Array.Empty<string>();
            return indices.Select(index => groups.Cast<object>().Select(group => group as IEnumerable).FirstOrDefault(group => group != null && group.Cast<object>().Any(value => Convert.ToInt32(value, CultureInfo.InvariantCulture) == index)))
                .Select(group => group == null ? null : string.Join(",", group.Cast<object>().Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)).OrderBy(value => value, StringComparer.Ordinal))).ToArray();
        }
        private static string Describe(Vector3 value) => string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", value.x, value.y, value.z);
    }
}
#endif
