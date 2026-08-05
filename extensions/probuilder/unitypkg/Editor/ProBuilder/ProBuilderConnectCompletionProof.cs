#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

namespace UnityCli.ProBuilder
{
    /// <summary>以 5.2.4 Connect 的私有纯重建器建立唯一的三角面终态计划。</summary>
    internal static class ProBuilderConnectCompletionProof
    {
        internal sealed class RawPlan
        {
            internal readonly Vertex Value;
            internal CanonicalMeshSnapshot.ChannelValue Normal;
            internal CanonicalMeshSnapshot.ChannelValue Tangent;
            internal RawPlan(Vertex value, CanonicalMeshSnapshot.RawVertex profile) { Value = NormalizeVertex(value, profile); }
        }

        internal sealed class FacePlan
        {
            internal readonly Face Face;
            internal readonly List<Vertex> Vertices;
            internal readonly List<int> NewVertexIndexes;
            internal FacePlan(Face face, IEnumerable<Vertex> vertices, IEnumerable<int> newVertexIndexes)
            { Face = new Face(face); Vertices = vertices.Select(value => new Vertex(value)).ToList(); NewVertexIndexes = newVertexIndexes.ToList(); }
        }

        internal sealed class Plan
        {
            internal CanonicalMeshSnapshot Before;
            internal bool FaceMode;
            internal Edge[] FrozenEdges;
            internal Face[] FrozenFaces;
            internal int ExpectedFaceCount;
            internal RawPlan[] Raw;
            internal FacePlan[] Faces;
            internal int[][] SharedVertices;
        }

        internal static Plan CaptureFace(GameObject go, Face[] faces) => Capture(go, true, null, faces);

        /// <summary>effect 前只读取 Face/Wing 和 managed Vertex clone；反射失败或任何非唯一输入均拒绝。</summary>
        internal static Plan Capture(GameObject go, bool faceMode, Edge[] edges, Face[] faces)
        {
            if (go == null || (faceMode ? faces == null || faces.Length != 1 || edges != null : edges == null || edges.Length != 2 || faces != null)) return null;
            var mesh = go.GetComponent<ProBuilderMesh>();
            var before = CanonicalMeshSnapshot.Capture(go);
            if (mesh == null || !CanonicalMeshSnapshot.CurrentMeshFormat(mesh) || before == null || before.Faces.Length != 1 || before.RawVertices.Length != 3 || before.SharedVertices.Length != 3
                || !before.HasSharedTextures || before.SharedTextures.Length != 0 || !UniquePositions(before) || !CanonicalMeshSnapshot.RefreshArithmeticFinite(before, true)) return null;
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null || renderer.sharedMaterials == null || renderer.sharedMaterials.Length != 1 || renderer.sharedMaterials[0] == null) return null;
            var source = mesh.faces.SingleOrDefault();
            if (source == null) return null;
            var sourceIndexes = source.indexes.ToArray();
            var sourceEdges = source.edges.ToArray();
            if (sourceIndexes.Length != 3 || source.submeshIndex != 0 || sourceEdges.Length != 3) return null;
            if (!faceMode && !edges.All(edge => sourceEdges.Any(value => SameEdge(edge, value)))) return null;

            var lookup = BuildSharedLookup(mesh);
            if (lookup == null) return null;
            var frozenEdges = edges == null ? null : (Edge[])edges.Clone();
            var frozenFaces = faces == null ? null : (Face[])faces.Clone();
            var distinctEdges = new HashSet<EdgeLookup>(EdgeLookup.GetEdgeLookup(faceMode ? sourceEdges : frozenEdges, lookup));
            var wings = WingedEdge.GetWingedEdges(mesh);
            var targetEdges = wings.Where(wing => wing.face == source && distinctEdges.Contains(wing.edge)).ToList();
            var required = faceMode ? 3 : 2;
            if (targetEdges.Count != required || targetEdges.Any(wing => wing.face != source)) return null;

            var vertices = CanonicalMeshSnapshot.BuildManagedVertices(before);
            if (vertices == null) return null;
            var unchanged = CanonicalMeshSnapshot.Capture(go);
            if (unchanged == null || !SameCapture(before, unchanged)) return null;
            var result = InvokePure(source, targetEdges, vertices, faceMode);
            var afterInvoke = CanonicalMeshSnapshot.Capture(go);
            if (result == null || afterInvoke == null || !SameCapture(before, afterInvoke)) return null;

            var children = BuildChildren(result, source, vertices);
            if (children == null || children.Length != required) return null;
            var profile = before.RawVertices[0];
            var raw = children.SelectMany(child => child.Vertices).Select(value => new RawPlan(value, profile)).ToArray();
            if (raw.Length == 0 || raw.Any(value => !Finite(value.Value))) return null;
            var positions = raw.Select(value => value.Value.position).ToArray();
            if (!TryBuildSharedVertices(positions, out var sharedVertices)) return null;
            var plan = new Plan { Before = before, FaceMode = faceMode, FrozenEdges = frozenEdges, FrozenFaces = frozenFaces, ExpectedFaceCount = required, Raw = raw, Faces = children, SharedVertices = sharedVertices };
            var terminal = FinalStreams(plan).ToArray();
            var streams = terminal.Select(value => value.Stream).ToArray();
            var uv0 = raw.Select(value => new CanonicalMeshSnapshot.ChannelValue(profile.Uvs[0].IsPresent, value.Value.uv0)).ToArray();
            return CanonicalMeshSnapshot.TerminalTopologyReadable(positions, streams, sharedVertices)
                && CanonicalMeshSnapshot.RefreshArithmeticFinite(positions, uv0, streams, terminal.Select(value => value.SmoothingGroup), true)
                && CalculateNormalsAndTangents(plan) && raw.All(value => Finite(value.Normal.Value) && Finite(value.Tangent.Value)) ? plan : null;
        }

        /// <summary>SetVertices 由全局首 raw 决定 channel presence；Mix 的隐藏字段必须按该 profile 显式恢复 attributes。</summary>
        private static Vertex NormalizeVertex(Vertex value, CanonicalMeshSnapshot.RawVertex profile)
        {
            if (value == null || profile == null) return null;
            var result = new Vertex { position = value.position };
            if (profile.Color.IsPresent) result.color = value.color;
            if (profile.LocalNormal.IsPresent) result.normal = value.normal;
            if (profile.Tangent.IsPresent) result.tangent = value.tangent;
            var uvs = profile.Uvs;
            if (uvs[0].IsPresent) result.uv0 = value.uv0;
            if (uvs[1].IsPresent) result.uv2 = value.uv2;
            if (uvs[2].IsPresent) result.uv3 = value.uv3;
            if (uvs[3].IsPresent) result.uv4 = value.uv4;
            return result;
        }

        /// <summary>严格按 internal caller 的 overload、winding、metadata 与 Apply/Delete offset 模拟，不保留内部可变对象。</summary>
        private static FacePlan[] BuildChildren(IEnumerable result, Face source, List<Vertex> vertices)
        {
            var records = result.Cast<object>().ToArray();
            var output = new List<FacePlan>();
            var sourceNormal = Normal(vertices, source.indexes.ToArray());
            var used = new HashSet<int> { source.textureGroup };
            var textureGroup = source.textureGroup;
            if (textureGroup < 0) { textureGroup = 1; while (used.Contains(textureGroup)) textureGroup++; }
            foreach (var record in records)
            {
                var rebuild = record.GetType().GetField("faceRebuildData", BindingFlags.Instance | BindingFlags.Public)?.GetValue(record);
                var face = rebuild?.GetType().GetField("face", BindingFlags.Instance | BindingFlags.Public)?.GetValue(rebuild) as Face;
                var childVertices = rebuild?.GetType().GetField("vertices", BindingFlags.Instance | BindingFlags.Public)?.GetValue(rebuild) as IEnumerable<Vertex>;
                var newVertexIndexes = record.GetType().GetField("newVertexIndexes", BindingFlags.Instance | BindingFlags.Public)?.GetValue(record) as IEnumerable<int>;
                if (face == null || childVertices == null || newVertexIndexes == null) return null;
                var copied = childVertices.Select(value => new Vertex(value)).ToList();
                var child = new Face(face);
                if (Vector3.Dot(sourceNormal, Normal(copied, child.indexes.ToArray())) < 0f) child.Reverse();
                child.textureGroup = textureGroup; child.uv = new AutoUnwrapSettings(source.uv); child.submeshIndex = source.submeshIndex;
                child.smoothingGroup = source.smoothingGroup; child.manualUV = source.manualUV;
                output.Add(new FacePlan(child, copied, newVertexIndexes));
            }
            return output.ToArray();
        }

        private static IEnumerable InvokePure(Face source, List<WingedEdge> targetEdges, List<Vertex> vertices, bool faceMode)
        {
            var types = faceMode
                ? new[] { typeof(Face), typeof(List<WingedEdge>), typeof(List<Vertex>) }
                : new[] { typeof(Face), typeof(WingedEdge), typeof(WingedEdge), typeof(List<Vertex>) };
            var method = typeof(ConnectElements).GetMethod("ConnectEdgesInFace", BindingFlags.Static | BindingFlags.NonPublic, null, types, null);
            if (method == null) return null;
            try
            {
                var values = faceMode ? new object[] { source, targetEdges, vertices } : new object[] { source, targetEdges[0], targetEdges[1], vertices };
                return method.Invoke(null, values) as IEnumerable;
            }
            catch { return null; }
        }

        /// <summary>fresh readback 必须与反射计划的 raw/face 顺序、属性和 position shared partition 完全相同。</summary>
        internal static bool Matches(Plan plan, CanonicalMeshSnapshot after)
        {
            if (plan == null || after == null || !SameCaptureIdentity(plan.Before, after) || after.RawVertices.Length != plan.Raw.Length || after.Faces.Length != plan.Faces.Length
                || !after.HasSharedTextures || after.SharedTextures.Length != 0 || after.Faces.Length != plan.ExpectedFaceCount) return false;
            if (!RawOrderMatches(plan.Raw, after.RawVertices) || !FaceOrderMatches(plan, after) || !PositionPartitionsMatch(plan.SharedVertices, after)) return false;
            return after.RawVertices.All(value => Finite(value));
        }

        private static bool RawOrderMatches(RawPlan[] expected, CanonicalMeshSnapshot.RawVertex[] actual)
        {
            for (var index = 0; index < expected.Length; index++)
            {
                var vertex = expected[index].Value; var raw = actual[index]; var uvs = raw.Uvs;
                if (!CanonicalMeshSnapshot.ExactEquals(vertex.position, raw.LocalPosition) || !Optional(vertex, MeshArrays.Color, raw.Color)
                    || !Optional(vertex, MeshArrays.Texture0, uvs[0]) || uvs[1].IsPresent || !Optional(vertex, MeshArrays.Texture2, uvs[2]) || !Optional(vertex, MeshArrays.Texture3, uvs[3])
                    || !CanonicalMeshSnapshot.ChannelValueEquals(expected[index].Normal, raw.LocalNormal) || !CanonicalMeshSnapshot.ChannelValueEquals(expected[index].Tangent, raw.Tangent)) return false;
            }
            return true;
        }

        /// <summary>逐句重放 PB 5.2.4 CalculateNormals/CalculateTangents；结果仅存在于 effect 前 immutable plan。</summary>
        private static bool CalculateNormalsAndTangents(Plan plan)
        {
            var count = plan.Raw.Length; var positions = plan.Raw.Select(value => value.Value.position).ToArray();
            var normals = new Vector3[count]; var normalCounts = new int[count]; var smoothing = new int[count];
            var streams = FinalStreams(plan).ToArray();
            foreach (var item in streams)
            {
                for (var triangle = 0; triangle < item.Stream.Length; triangle += 3)
                {
                    var a = item.Stream[triangle]; var b = item.Stream[triangle + 1]; var c = item.Stream[triangle + 2];
                    var cross = UnityEngine.ProBuilder.Math.Normal(positions[a], positions[b], positions[c]); cross.Normalize();
                    normals[a] += cross; normals[b] += cross; normals[c] += cross; normalCounts[a]++; normalCounts[b]++; normalCounts[c]++;
                }
                foreach (var index in item.Stream.Distinct()) smoothing[index] = item.SmoothingGroup;
            }
            if (normalCounts.Any(value => value == 0)) return false;
            for (var index = 0; index < count; index++) normals[index] /= normalCounts[index];
            foreach (var shared in plan.SharedVertices)
            {
                var averages = new Dictionary<int, Vector3>(); var counts = new Dictionary<int, float>();
                foreach (var index in shared)
                {
                    var group = smoothing[index];
                    if (group <= 0 || group > 24 && group < 42) continue;
                    averages[group] = averages.TryGetValue(group, out var sum) ? sum + normals[index] : normals[index];
                    counts[group] = counts.TryGetValue(group, out var value) ? value + 1f : 1f;
                }
                foreach (var index in shared)
                {
                    var group = smoothing[index];
                    if (group <= 0 || group > 24 && group < 42) continue;
                    normals[index] = EnsureUnitVector(averages[group] / counts[group]);
                }
            }
            var tangents = new Vector4[count];
            var hasUv0 = plan.Raw.All(value => value.Value.HasArrays(MeshArrays.Texture0));
            if (hasUv0)
            {
                var tan1 = new Vector3[count]; var tan2 = new Vector3[count]; var uvs = plan.Raw.Select(value => value.Value.uv0).ToArray();
                foreach (var item in streams) for (var triangle = 0; triangle < item.Stream.Length; triangle += 3)
                {
                    var i1 = item.Stream[triangle]; var i2 = item.Stream[triangle + 1]; var i3 = item.Stream[triangle + 2];
                    var v1 = positions[i1]; var v2 = positions[i2]; var v3 = positions[i3]; var w1 = uvs[i1]; var w2 = uvs[i2]; var w3 = uvs[i3];
                    var x1 = v2.x - v1.x; var x2 = v3.x - v1.x; var y1 = v2.y - v1.y; var y2 = v3.y - v1.y; var z1 = v2.z - v1.z; var z2 = v3.z - v1.z;
                    var s1 = w2.x - w1.x; var s2 = w3.x - w1.x; var t1 = w2.y - w1.y; var t2 = w3.y - w1.y; var r = 1f / (s1 * t2 - s2 * t1);
                    var sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
                    var tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);
                    tan1[i1] += sdir; tan1[i2] += sdir; tan1[i3] += sdir; tan2[i1] += tdir; tan2[i2] += tdir; tan2[i3] += tdir;
                }
                for (var index = 0; index < count; index++)
                {
                    var normal = normals[index]; var tangent = EnsureUnitVector(tan1[index]); Vector3.OrthoNormalize(ref normal, ref tangent);
                    tangents[index] = new Vector4(tangent.x, tangent.y, tangent.z, Vector3.Dot(Vector3.Cross(normal, tangent), tan2[index]) < 0f ? -1f : 1f);
                }
            }
            for (var index = 0; index < count; index++)
            {
                if (!Finite(normals[index]) || !Finite(tangents[index])) return false;
                plan.Raw[index].Normal = new CanonicalMeshSnapshot.ChannelValue(true, normals[index]);
                plan.Raw[index].Tangent = new CanonicalMeshSnapshot.ChannelValue(true, tangents[index]);
            }
            return true;
        }

        private static IEnumerable<(int[] Stream, int SmoothingGroup)> FinalStreams(Plan plan)
        {
            var offset = 0;
            foreach (var face in plan.Faces)
            {
                yield return (face.Face.indexes.ToArray().Select(index => index + offset).ToArray(), face.Face.smoothingGroup);
                offset += face.Vertices.Count;
            }
        }

        private static bool FaceOrderMatches(Plan plan, CanonicalMeshSnapshot after)
        {
            var offset = 0;
            var sourceMaterial = plan.Before.Faces[0].Material;
            var element = typeof(Face).GetField("elementGroup", BindingFlags.Instance | BindingFlags.NonPublic);
            if (element == null) return false;
            for (var index = 0; index < plan.Faces.Length; index++)
            {
                var expected = plan.Faces[index]; var actual = after.Faces[index]; var stream = expected.Face.indexes.ToArray();
                var expectedMaterial = CanonicalMeshSnapshot.WithSerializedFaceMaterialFromFace(sourceMaterial, expected.Face);
                if (expectedMaterial == null || stream.Length != actual.DirectedRawTriangleStream.Length || expected.Face.submeshIndex != actual.SubmeshIndex || expected.Face.smoothingGroup != actual.SmoothingGroup
                    || expected.Face.textureGroup != actual.TextureGroup || expected.Face.manualUV != actual.ManualUv || !CanonicalMeshSnapshot.UvValueEquals(expected.Face.uv, actual.Uv)
                    || !CanonicalMeshSnapshot.MaterialValueEquals(expectedMaterial, actual.Material) || !(element.GetValue(expected.Face) is int group) || group != actual.ElementGroup) return false;
                var actualStream = actual.DirectedRawTriangleStream;
                for (var n = 0; n < stream.Length; n++) if (actualStream[n] != stream[n] + offset) return false;
                offset += expected.Vertices.Count;
            }
            return true;
        }

        private static bool PositionPartitionsMatch(int[][] expected, CanonicalMeshSnapshot after)
        {
            var actual = after.SharedVertices.Select(group => group.MemberRawIndexes).ToArray();
            return expected.Length == actual.Length && expected.Select((members, index) => members.SequenceEqual(actual[index])).All(equal => equal);
        }

        private static bool Optional(Vertex value, MeshArrays attribute, CanonicalMeshSnapshot.ChannelValue actual)
        {
            if (value.HasArrays(attribute) != actual.IsPresent) return false;
            if (!actual.IsPresent) return true;
            if (attribute == MeshArrays.Color) return CanonicalMeshSnapshot.ExactEquals(new Vector4(value.color.r, value.color.g, value.color.b, value.color.a), actual.Value);
            if (attribute == MeshArrays.Texture0) return CanonicalMeshSnapshot.ExactEquals((Vector4)value.uv0, actual.Value);
            if (attribute == MeshArrays.Texture2) return CanonicalMeshSnapshot.ExactEquals(value.uv3, actual.Value);
            return CanonicalMeshSnapshot.ExactEquals(value.uv4, actual.Value);
        }

        /// <summary>私有 pure overload 前后必须是同一完整 canonical state；任一 cache、partition、属性或 incidence 改变均拒绝 effect。</summary>
        private static bool SameCapture(CanonicalMeshSnapshot left, CanonicalMeshSnapshot right)
        {
            if (!SameCaptureIdentity(left, right) || left.RawVertices.Length != right.RawVertices.Length || left.Faces.Length != right.Faces.Length
                || left.SharedVertices.Length != right.SharedVertices.Length || left.HasSharedTextures != right.HasSharedTextures || left.SharedTextures.Length != right.SharedTextures.Length || left.Edges.Length != right.Edges.Length) return false;
            for (var index = 0; index < left.RawVertices.Length; index++) if (!SameRaw(left.RawVertices[index], right.RawVertices[index])) return false;
            for (var index = 0; index < left.SharedVertices.Length; index++) if (!SameShared(left.SharedVertices[index], right.SharedVertices[index])) return false;
            for (var index = 0; index < left.SharedTextures.Length; index++) if (!SameTexture(left.SharedTextures[index], right.SharedTextures[index])) return false;
            for (var index = 0; index < left.Faces.Length; index++) if (!SameFace(left.Faces[index], right.Faces[index])) return false;
            for (var index = 0; index < left.Edges.Length; index++) if (!SameSharedEdge(left.Edges[index], right.Edges[index])) return false;
            return true;
        }

        private static bool SameRaw(CanonicalMeshSnapshot.RawVertex left, CanonicalMeshSnapshot.RawVertex right)
        {
            return left != null && right != null && left.RawIndex == right.RawIndex && left.SharedHandle == right.SharedHandle
                && CanonicalMeshSnapshot.ExactEquals(left.LocalPosition, right.LocalPosition) && CanonicalMeshSnapshot.ExactEquals(left.WorldPosition, right.WorldPosition)
                && CanonicalMeshSnapshot.ChannelValueEquals(left.LocalNormal, right.LocalNormal) && CanonicalMeshSnapshot.ChannelValueEquals(left.Tangent, right.Tangent)
                && CanonicalMeshSnapshot.ChannelValueEquals(left.Color, right.Color) && left.Uvs.Zip(right.Uvs, CanonicalMeshSnapshot.ChannelValueEquals).All(value => value);
        }

        private static bool SameShared(CanonicalMeshSnapshot.SharedVertexSnapshot left, CanonicalMeshSnapshot.SharedVertexSnapshot right)
            => left != null && right != null && left.Handle == right.Handle && CanonicalMeshSnapshot.ExactEquals(left.CanonicalPosition, right.CanonicalPosition) && left.MemberRawIndexes.SequenceEqual(right.MemberRawIndexes);
        private static bool SameTexture(CanonicalMeshSnapshot.SharedTextureSnapshot left, CanonicalMeshSnapshot.SharedTextureSnapshot right)
            => left != null && right != null && left.Group == right.Group && left.MemberRawIndexes.SequenceEqual(right.MemberRawIndexes);

        private static bool SameFace(CanonicalMeshSnapshot.FaceOccurrence left, CanonicalMeshSnapshot.FaceOccurrence right)
        {
            if (left == null || right == null || left.OccurrenceIndex != right.OccurrenceIndex || left.SubmeshIndex != right.SubmeshIndex || left.SmoothingGroup != right.SmoothingGroup
                || left.TextureGroup != right.TextureGroup || left.ElementGroup != right.ElementGroup || left.ManualUv != right.ManualUv || !CanonicalMeshSnapshot.UvValueEquals(left.Uv, right.Uv)
                || !CanonicalMeshSnapshot.MaterialValueEquals(left.Material, right.Material) || !left.DirectedRawTriangleStream.SequenceEqual(right.DirectedRawTriangleStream) || !left.DirectedSharedTriangleStream.SequenceEqual(right.DirectedSharedTriangleStream)
                || left.Boundary.Length != right.Boundary.Length) return false;
            for (var index = 0; index < left.Boundary.Length; index++)
            {
                var a = left.Boundary[index]; var b = right.Boundary[index];
                if (a.RawA != b.RawA || a.RawB != b.RawB || a.SharedA != b.SharedA || a.SharedB != b.SharedB) return false;
            }
            return true;
        }

        private static bool SameSharedEdge(CanonicalMeshSnapshot.SharedEdgeOccurrence left, CanonicalMeshSnapshot.SharedEdgeOccurrence right)
        {
            if (left == null || right == null || left.SharedMin != right.SharedMin || left.SharedMax != right.SharedMax || left.Incidences.Length != right.Incidences.Length) return false;
            for (var index = 0; index < left.Incidences.Length; index++)
            {
                var a = left.Incidences[index]; var b = right.Incidences[index];
                if (a.FaceOccurrenceIndex != b.FaceOccurrenceIndex || a.RawA != b.RawA || a.RawB != b.RawB || a.SharedA != b.SharedA || a.SharedB != b.SharedB) return false;
            }
            return true;
        }
        /// <summary>调用 Package 自身的 1/1000 position quantizer；超出 Int32 域或不可比较时稳定拒绝。</summary>
        private static bool TryBuildSharedVertices(IList<Vector3> positions, out int[][] sharedVertices)
        {
            sharedVertices = null;
            try
            {
                var groups = SharedVertex.GetSharedVerticesWithPositions(positions);
                if (groups == null) return false;
                var result = groups.Select(value => value == null ? null : value.ToArray()).ToArray();
                if (result.Any(value => value == null || value.Length == 0)) return false;
                sharedVertices = result;
                return true;
            }
            catch { return false; }
        }

        private static Dictionary<int, int> BuildSharedLookup(ProBuilderMesh mesh)
        {
            if (mesh == null || mesh.sharedVertices == null) return null;
            var lookup = new Dictionary<int, int>();
            var groups = mesh.sharedVertices;
            for (var handle = 0; handle < groups.Count; handle++)
            {
                var shared = groups[handle];
                if (shared == null || shared.Count == 0) return null;
                foreach (var index in shared)
                {
                    if (lookup.ContainsKey(index)) return null;
                    lookup.Add(index, handle);
                }
            }
            return lookup;
        }

        /// <summary>逐句复演 PB 5.2.4 的 list/index normal；多三角 child 的累加、除法与最终 Normalize 顺序不可改变。</summary>
        private static Vector3 Normal(IList<Vertex> vertices, IList<int> indexes)
        {
            if (vertices == null || indexes == null || indexes.Count == 0 || indexes.Count % 3 != 0) return Vector3.zero;
            var normal = Vector3.zero;
            for (var index = 0; index < indexes.Count; index += 3)
                normal += UnityEngine.ProBuilder.Math.Normal(vertices[indexes[index]].position, vertices[indexes[index + 1]].position, vertices[indexes[index + 2]].position);
            normal /= indexes.Count / 3f;
            normal.Normalize();
            return normal;
        }

        // 逐句复演 PB 5.2.4 的单位向量计算；Package helper 为 internal，不能直接引用。
        private static Vector3 EnsureUnitVector(Vector3 value) => Mathf.Abs(value.sqrMagnitude) < float.Epsilon ? Vector3.up : value.normalized;

        private static bool SameCaptureIdentity(CanonicalMeshSnapshot left, CanonicalMeshSnapshot right) => CanonicalMeshSnapshot.IdentityAndTransformEqual(left, right);
        private static bool UniquePositions(CanonicalMeshSnapshot snapshot) => snapshot.RawVertices.All(left => snapshot.RawVertices.Count(right => CanonicalMeshSnapshot.ExactEquals(left.LocalPosition, right.LocalPosition)) == 1);
        private static bool SameEdge(Edge left, Edge right) => left.Equals(right);
        private static bool Finite(Vertex value) => value != null && Finite(value.position)
            && (!value.HasArrays(MeshArrays.Color) || Finite(value.color))
            && (!value.HasArrays(MeshArrays.Normal) || Finite(value.normal))
            && (!value.HasArrays(MeshArrays.Tangent) || Finite(value.tangent))
            && (!value.HasArrays(MeshArrays.Texture0) || Finite(value.uv0))
            && (!value.HasArrays(MeshArrays.Texture1) || Finite(value.uv2))
            && (!value.HasArrays(MeshArrays.Texture2) || Finite(value.uv3))
            && (!value.HasArrays(MeshArrays.Texture3) || Finite(value.uv4));
        private static bool Finite(CanonicalMeshSnapshot.RawVertex value) => value != null && Finite(value.LocalPosition)
            && (!value.LocalNormal.IsPresent || Finite(value.LocalNormal.Value)) && (!value.Tangent.IsPresent || Finite(value.Tangent.Value))
            && (!value.Color.IsPresent || Finite(value.Color.Value)) && value.Uvs.All(channel => !channel.IsPresent || Finite(channel.Value));
        private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        private static bool Finite(Color value) => Finite(value.r) && Finite(value.g) && Finite(value.b) && Finite(value.a);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
#endif
