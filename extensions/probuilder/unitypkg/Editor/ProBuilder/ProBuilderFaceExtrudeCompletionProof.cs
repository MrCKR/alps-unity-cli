#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>仅接受一个独立三角面，逐句复演 ProBuilder 5.2.4 face extrusion 的可证明终态。</summary>
    internal static class ProBuilderFaceExtrudeCompletionProof
    {
        private const int SmoothingNone = 0;
        private const int SmoothRangeMax = 24;
        private const int HardRangeMax = 42;

        internal sealed class Plan
        {
            internal CanonicalMeshSnapshot Before;
            internal Face[] FrozenFaces;
            internal ExtrudeMethod Method;
            internal float Distance;
            internal int[] BoundaryRaw;
            internal Vector3 Delta;
        }

        /// <summary>effect 前冻结 identity、完整 canonical snapshot、typed Face[] 和唯一的 face.edges 顺序。</summary>
        internal static Plan TryCreate(GameObject go, ProBuilderMesh mesh, int[] requestedOccurrences, string method, float distance)
        {
            if (go == null || mesh == null || requestedOccurrences == null || requestedOccurrences.Length != 1 || requestedOccurrences[0] != 0
                || !Finite(distance) || distance == 0f || !TryMethod(method, out var extrudeMethod)) return null;
            var before = CanonicalMeshSnapshot.Capture(go, requestedOccurrences);
            if (before == null || before.Faces.Length != 1 || before.RawVertices.Length != 3 || before.SharedVertices.Length != 3
                || !before.HasSharedTextures || before.Faces[0].DirectedRawTriangleStream.Length != 3) return null;
            var face = before.Faces[0]; var stream = face.DirectedRawTriangleStream;
            var renderer = go.GetComponent<Renderer>(); var materials = renderer == null ? null : renderer.sharedMaterials;
            if (materials == null || materials.Length != 1 || materials[0] == null || face.SubmeshIndex != 0
                || stream.Distinct().Count() != 3 || face.DirectedSharedTriangleStream.Distinct().Count() != 3 || before.RawVertices.Any(raw => !Finite(raw.LocalPosition))
                || before.RawVertices.Select(raw => raw.LocalPosition).Distinct().Count() != 3 || before.SharedVertices.Any(group => group.MemberRawIndexes.Length != 1)
                || face.Boundary.Length != 3 || !stream.OrderBy(raw => raw).SequenceEqual(new[] { 0, 1, 2 }) || !HasFiniteNonDegenerateUv(before)) return null;
            var typedFaces = mesh.faces;
            if (typedFaces == null || typedFaces.Count != 1 || typedFaces[0] == null) return null;
            var typedFace = typedFaces[0]; var typedIndexes = typedFace.indexes.ToArray();
            if (!typedIndexes.SequenceEqual(stream)) return null;
            var edges = typedFace.edges.ToArray();
            if (edges == null || edges.Length != 3 || edges.Any(edge => edge.a < 0 || edge.b < 0)) return null;
            // group path 的 HashSet perimeter 遍历在单面时仍须证明为 face.edges 的唯一顺序，不能自行猜测。
            var boundary = edges.SelectMany(edge => new[] { edge.a, edge.b }).ToArray();
            if (!BoundaryExactlyMatchesTriangle(boundary, stream)) return null;
            var normal = UnityEngine.ProBuilder.Math.Normal(mesh, typedFaces[0]);
            if (!Finite(normal) || normal.sqrMagnitude == 0f) return null;
            var delta = normal * distance;
            if (extrudeMethod != ExtrudeMethod.IndividualFaces)
            {
                var direction = normal;
                direction.Normalize();
                var modifier = extrudeMethod == ExtrudeMethod.FaceNormal
                    ? UnityEngine.ProBuilder.Math.Secant(Vector3.Angle(direction, normal) * Mathf.Deg2Rad)
                    : 1f;
                direction.x *= distance * modifier;
                direction.y *= distance * modifier;
                direction.z *= distance * modifier;
                delta = direction;
            }
            if (!FiniteExtrudedGeometry(before, boundary, delta)) return null;
            return new Plan { Before = before, FrozenFaces = new[] { typedFace }, Method = extrudeMethod, Distance = distance, BoundaryRaw = boundary, Delta = delta };
        }

        /// <summary>逐顶点和逐 side triangle 证明预测终态有限，避免有限输入在 effect 后溢出。</summary>
        private static bool FiniteExtrudedGeometry(CanonicalMeshSnapshot before, int[] boundary, Vector3 delta)
        {
            if (before == null || boundary == null || boundary.Length != 6 || !Finite(delta)) return false;
            if (before.RawVertices.Any(raw => !Finite(raw.LocalPosition + delta)
                || !Finite(before.LocalToWorldMatrix.MultiplyPoint3x4(raw.LocalPosition + delta)))) return false;
            var capPositions = before.RawVertices.Select(raw => raw.LocalPosition + delta).ToArray();
            if (!CanonicalMeshSnapshot.RefreshArithmeticFinite(capPositions, before.RawVertices.Select(raw => raw.Uvs[0]).ToArray(), before.Faces.Select(face => face.DirectedRawTriangleStream), before.Faces.Select(face => face.SmoothingGroup))) return false;
            for (var edge = 0; edge < 3; edge++)
            {
                var x = boundary[edge * 2]; var y = boundary[edge * 2 + 1];
                if (x < 0 || y < 0 || x >= before.RawVertices.Length || y >= before.RawVertices.Length) return false;
                var a = before.RawVertices[x].LocalPosition; var b = before.RawVertices[y].LocalPosition; var c = a + delta; var d = b + delta;
                var first = UnityEngine.ProBuilder.Math.Normal(a, b, c); var second = UnityEngine.ProBuilder.Math.Normal(b, d, c);
                if (!Finite(first) || !Finite(second) || first.sqrMagnitude == 0f || second.sqrMagnitude == 0f
                    || !CanonicalMeshSnapshot.RefreshArithmeticFinite(
                        new[] { a, b, c, d },
                        new[] { new CanonicalMeshSnapshot.ChannelValue(true, new Vector2(0f, 0f)), new CanonicalMeshSnapshot.ChannelValue(true, new Vector2(1f, 0f)), new CanonicalMeshSnapshot.ChannelValue(true, new Vector2(0f, 1f)), new CanonicalMeshSnapshot.ChannelValue(true, new Vector2(1f, 1f)) },
                        new[] { new[] { 0, 1, 2, 1, 3, 2 } }, new[] { 0 })) return false;
            }
            return true;
        }

        internal static bool Matches(Plan plan, string target)
        {
            if (plan == null || plan.Before == null || plan.FrozenFaces == null || plan.FrozenFaces.Length != 1) return false;
            var freshMesh = CanonicalMeshSnapshot.ResolveFreshMesh(target, plan.Before.Identity);
            var after = freshMesh == null ? null : CanonicalMeshSnapshot.Capture(freshMesh.gameObject);
            if (after == null || !IdentityAndTransformEqual(plan.Before, after) || after.RawVertices.Length != 15 || after.Faces.Length != 4 || !after.HasSharedTextures) return false;
            if (!RawPositionsAndPayloadExactly(plan, after) || !FacesExactly(plan, after) || !SharedPartitionExactly(plan, after)
                || !SharedTexturesExactly(plan, after) || !DerivedNormalsAndTangentsMatch(after)) return false;
            return AllRawReferenced(after);
        }

        private static bool RawPositionsAndPayloadExactly(Plan plan, CanonicalMeshSnapshot after)
        {
            var before = plan.Before; var expectedPosition = new Vector3[15]; var source = new int[15];
            for (var raw = 0; raw < 3; raw++) { expectedPosition[raw] = before.RawVertices[raw].LocalPosition + plan.Delta; source[raw] = raw; }
            for (var edge = 0; edge < 3; edge++)
            {
                var offset = 3 + edge * 4; var x = plan.BoundaryRaw[edge * 2]; var y = plan.BoundaryRaw[edge * 2 + 1];
                expectedPosition[offset] = before.RawVertices[x].LocalPosition; expectedPosition[offset + 1] = before.RawVertices[y].LocalPosition;
                expectedPosition[offset + 2] = before.RawVertices[x].LocalPosition + plan.Delta; expectedPosition[offset + 3] = before.RawVertices[y].LocalPosition + plan.Delta;
                source[offset] = source[offset + 2] = x; source[offset + 1] = source[offset + 3] = y;
            }
            for (var raw = 0; raw < 15; raw++)
            {
                if (!CanonicalMeshSnapshot.ExactEquals(after.RawVertices[raw].LocalPosition, expectedPosition[raw])) return false;
                if (raw < 3 ? !ClonePayloadEquals(before.RawVertices[source[raw]], after.RawVertices[raw])
                    : !SidePayloadEquals(before.RawVertices[source[raw]], after.RawVertices[raw], (raw - 3) % 4)) return false;
            }
            return true;
        }

        private static bool FacesExactly(Plan plan, CanonicalMeshSnapshot after)
        {
            var beforeFace = plan.Before.Faces[0]; var cap = after.Faces[0];
            var expectedSmoothing = plan.Method == ExtrudeMethod.IndividualFaces ? 0 : beforeFace.SmoothingGroup;
            if (!FaceEquals(cap, beforeFace, beforeFace.DirectedRawTriangleStream, expectedSmoothing, -1, beforeFace.ElementGroup, beforeFace.ManualUv, beforeFace.Uv)) return false;
            for (var edge = 0; edge < 3; edge++)
            {
                var offset = 3 + edge * 4;
                if (!FaceEquals(after.Faces[edge + 1], beforeFace, new[] { offset, offset + 1, offset + 2, offset + 1, offset + 3, offset + 2 }, 0, -1, -1, true, beforeFace.Uv, CanonicalMeshSnapshot.WithSerializedFaceMaterial(beforeFace.Material, null))) return false;
            }
            return true;
        }

        private static bool SharedPartitionExactly(Plan plan, CanonicalMeshSnapshot after)
        {
            // 初始 lookup key 先按原 shared group/member 顺序枚举，因此所有 outer group 先确定 handle。
            var expected = new List<List<int>>(); var outerByRaw = new Dictionary<int, List<int>>();
            foreach (var sourceGroup in plan.Before.SharedVertices)
            {
                if (sourceGroup.MemberRawIndexes.Length != 1) return false;
                var raw = sourceGroup.MemberRawIndexes[0];
                if (outerByRaw.ContainsKey(raw)) return false;
                var outer = new List<int> { raw }; outerByRaw.Add(raw, outer); expected.Add(outer);
            }

            // side raw key 随 boundary 顺序追加；old group 在 base clone 首遇时才获得 terminal handle。
            var oldByRaw = new Dictionary<int, List<int>>();
            for (var edge = 0; edge < 3; edge++)
            {
                var offset = 3 + edge * 4; var x = plan.BoundaryRaw[edge * 2]; var y = plan.BoundaryRaw[edge * 2 + 1];
                if (!outerByRaw.TryGetValue(x, out var outerX) || !outerByRaw.TryGetValue(y, out var outerY)) return false;
                if (!oldByRaw.TryGetValue(x, out var oldX)) { oldX = new List<int>(); oldByRaw.Add(x, oldX); expected.Add(oldX); }
                oldX.Add(offset);
                if (!oldByRaw.TryGetValue(y, out var oldY)) { oldY = new List<int>(); oldByRaw.Add(y, oldY); expected.Add(oldY); }
                oldY.Add(offset + 1);
                outerX.Add(offset + 2); outerY.Add(offset + 3);
            }
            return OrderedPartitionEquals(expected, after.SharedVertices.Select(group => group.MemberRawIndexes));
        }

        private static bool SharedTexturesExactly(Plan plan, CanonicalMeshSnapshot after)
        {
            var expected = plan.Before.SharedTextures.Select(group => group.MemberRawIndexes.Where(raw => raw >= 3).ToArray()).Where(group => group.Length > 0);
            return PartitionEquals(expected, after.SharedTextures.Select(group => group.MemberRawIndexes));
        }

        /// <summary>按 5.2.4 Normals.CalculateNormals 的 hard-normal 与 shared/smoothing 分支复演，不将 Refresh 缓存当作未经验证的事实。</summary>
        private static bool DerivedNormalsAndTangentsMatch(CanonicalMeshSnapshot snapshot)
        {
            var raw = snapshot.RawVertices; var normals = new Vector3[raw.Length]; var counts = new int[raw.Length];
            foreach (var face in snapshot.Faces)
            {
                var stream = face.DirectedRawTriangleStream;
                for (var index = 0; index < stream.Length; index += 3)
                {
                    var a = stream[index]; var b = stream[index + 1]; var c = stream[index + 2];
                    var cross = UnityEngine.ProBuilder.Math.Normal(raw[a].LocalPosition, raw[b].LocalPosition, raw[c].LocalPosition);
                    cross.Normalize();
                    normals[a] += cross; normals[b] += cross; normals[c] += cross;
                    counts[a]++; counts[b]++; counts[c]++;
                }
            }
            if (counts.Any(count => count == 0)) return false;
            for (var index = 0; index < normals.Length; index++) normals[index] /= counts[index];
            foreach (var group in snapshot.SharedVertices)
            foreach (var smoothing in snapshot.Faces.Select(face => face.SmoothingGroup).Where(value => value > SmoothingNone && (value <= SmoothRangeMax || value >= HardRangeMax)).Distinct())
            {
                var members = group.MemberRawIndexes.Where(index => SmoothingAt(snapshot, index) == smoothing).ToArray();
                if (members.Length == 0) continue;
                var average = Vector3.zero; foreach (var member in members) average += normals[member]; average /= members.Length; average = EnsureUnitVector(average);
                foreach (var member in members) normals[member] = average;
            }
            for (var index = 0; index < raw.Length; index++)
            {
                var actual = new Vector3(raw[index].LocalNormal.Value.x, raw[index].LocalNormal.Value.y, raw[index].LocalNormal.Value.z);
                if (!raw[index].LocalNormal.IsPresent || !Finite(normals[index]) || !Finite(actual) || !CanonicalMeshSnapshot.ExactEquals(actual, normals[index])) return false;
            }
            return TangentsMatch(snapshot, normals);
        }

        /// <summary>按 5.2.4 Normals.CalculateTangents 复演；无 UV0 或奇异 UV 映射无法形成精确有限终态，预检已拒绝。</summary>
        private static bool TangentsMatch(CanonicalMeshSnapshot snapshot, Vector3[] normals)
        {
            var raw = snapshot.RawVertices;
            if (raw.Any(vertex => !vertex.Uvs[0].IsPresent || !vertex.Tangent.IsPresent)) return false;
            var tan1 = new Vector3[raw.Length]; var tan2 = new Vector3[raw.Length];
            foreach (var face in snapshot.Faces)
            {
                var stream = face.DirectedRawTriangleStream;
                for (var index = 0; index < stream.Length; index += 3)
                {
                    var a = stream[index]; var b = stream[index + 1]; var c = stream[index + 2];
                    var v1 = raw[a].LocalPosition; var v2 = raw[b].LocalPosition; var v3 = raw[c].LocalPosition;
                    var w1 = raw[a].Uvs[0].Value; var w2 = raw[b].Uvs[0].Value; var w3 = raw[c].Uvs[0].Value;
                    float x1 = v2.x - v1.x; float x2 = v3.x - v1.x; float y1 = v2.y - v1.y; float y2 = v3.y - v1.y; float z1 = v2.z - v1.z; float z2 = v3.z - v1.z;
                    float s1 = w2.x - w1.x; float s2 = w3.x - w1.x; float t1 = w2.y - w1.y; float t2 = w3.y - w1.y;
                    float r = 1.0f / (s1 * t2 - s2 * t1);
                    Vector3 sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
                    Vector3 tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);
                    tan1[a] += sdir; tan1[b] += sdir; tan1[c] += sdir;
                    tan2[a] += tdir; tan2[b] += tdir; tan2[c] += tdir;
                }
            }
            for (var index = 0; index < raw.Length; index++)
            {
                var normal = normals[index]; var tangent = EnsureUnitVector(tan1[index]); Vector3.OrthoNormalize(ref normal, ref tangent);
                var expected = new Vector4(tangent.x, tangent.y, tangent.z, Vector3.Dot(Vector3.Cross(normal, tangent), tan2[index]) < 0f ? -1f : 1f);
                if (!Finite(expected) || !Finite(raw[index].Tangent.Value) || !CanonicalMeshSnapshot.ExactEquals(raw[index].Tangent.Value, expected)) return false;
            }
            return true;
        }

        private static bool HasFiniteNonDegenerateUv(CanonicalMeshSnapshot snapshot) => CanonicalMeshSnapshot.RefreshArithmeticFinite(snapshot);
        private static int SmoothingAt(CanonicalMeshSnapshot snapshot, int raw) => snapshot.Faces.First(face => face.DirectedRawTriangleStream.Contains(raw)).SmoothingGroup;
        /// <summary>ToMesh 清除 mesh.uv2；其余 authoritative UV/color 必须逐 raw 保持 source payload。</summary>
        private static bool ClonePayloadEquals(CanonicalMeshSnapshot.RawVertex source, CanonicalMeshSnapshot.RawVertex actual)
        {
            if (!CanonicalMeshSnapshot.ChannelValueEquals(source.Color, actual.Color)) return false;
            var sourceUvs = source.Uvs; var actualUvs = actual.Uvs;
            return !actualUvs[1].IsPresent && new[] { 0, 2, 3 }.All(channel => CanonicalMeshSnapshot.ChannelValueEquals(sourceUvs[channel], actualUvs[channel]));
        }
        private static bool SidePayloadEquals(CanonicalMeshSnapshot.RawVertex source, CanonicalMeshSnapshot.RawVertex actual, int quadRawIndex)
        {
            if (!CanonicalMeshSnapshot.ChannelValueEquals(source.Color, actual.Color)) return false;
            var sourceUvs = source.Uvs; var actualUvs = actual.Uvs;
            return actualUvs[0].IsPresent && CanonicalMeshSnapshot.ExactEquals(actualUvs[0].Value, SideUv(quadRawIndex))
                && !actualUvs[1].IsPresent && new[] { 2, 3 }.All(channel => CanonicalMeshSnapshot.ChannelValueEquals(sourceUvs[channel], actualUvs[channel]));
        }
        private static Vector4 SideUv(int raw) => raw == 0 ? new Vector4(0f, 0f, 0f, 0f) : raw == 1 ? new Vector4(1f, 0f, 0f, 0f) : raw == 2 ? new Vector4(0f, 1f, 0f, 0f) : new Vector4(1f, 1f, 0f, 0f);
        private static bool FaceEquals(CanonicalMeshSnapshot.FaceOccurrence actual, CanonicalMeshSnapshot.FaceOccurrence source, int[] stream, int smoothing, int texture, int element, bool manual, AutoUnwrapSettings uv, CanonicalMeshSnapshot.MaterialClaim expectedMaterial = null) => actual != null && actual.SubmeshIndex == source.SubmeshIndex && actual.SmoothingGroup == smoothing && actual.TextureGroup == texture && actual.ElementGroup == element && actual.ManualUv == manual && CanonicalMeshSnapshot.MaterialValueEquals(actual.Material, expectedMaterial ?? source.Material) && CanonicalMeshSnapshot.UvValueEquals(actual.Uv, uv) && actual.DirectedRawTriangleStream.SequenceEqual(stream);
        private static bool BoundaryExactlyMatchesTriangle(int[] edges, int[] stream) => edges.Length == 6 && edges[0] == stream[0] && edges[1] == stream[1] && edges[2] == stream[1] && edges[3] == stream[2] && edges[4] == stream[2] && edges[5] == stream[0];
        private static bool AllRawReferenced(CanonicalMeshSnapshot snapshot) => snapshot.RawVertices.All(raw => snapshot.Faces.Any(face => face.DirectedRawTriangleStream.Contains(raw.RawIndex)));
        private static bool IdentityAndTransformEqual(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after) => CanonicalMeshSnapshot.IdentityAndTransformEqual(before, after);
        /// <summary>shared handle 与 first-member 都是后续操作语义，必须逐组逐成员保持 package 顺序。</summary>
        private static bool OrderedPartitionEquals(IEnumerable<IEnumerable<int>> expected, IEnumerable<IEnumerable<int>> actual)
        {
            var left = expected.Select(group => group.ToArray()).ToArray(); var right = actual.Select(group => group.ToArray()).ToArray();
            return left.Length == right.Length && left.Select((members, index) => members.SequenceEqual(right[index])).All(equal => equal);
        }
        private static bool PartitionEquals(IEnumerable<IEnumerable<int>> expected, IEnumerable<IEnumerable<int>> actual)
        {
            var left = expected.Select(group => group.OrderBy(raw => raw).ToArray()).ToList(); var right = actual.Select(group => group.OrderBy(raw => raw).ToArray()).ToList();
            if (left.Count != right.Count) return false;
            foreach (var group in left) { var index = right.FindIndex(candidate => group.SequenceEqual(candidate)); if (index < 0) return false; right.RemoveAt(index); }
            return right.Count == 0;
        }
        private static bool TryMethod(string value, out ExtrudeMethod method)
        {
            method = ExtrudeMethod.VertexNormal;
            if (value == "individual") { method = ExtrudeMethod.IndividualFaces; return true; }
            if (value == "vertexNormal") { method = ExtrudeMethod.VertexNormal; return true; }
            if (value == "faceNormal") { method = ExtrudeMethod.FaceNormal; return true; }
            return false;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        private static Vector3 EnsureUnitVector(Vector3 value) => Mathf.Abs(value.sqrMagnitude) < float.Epsilon ? Vector3.up : value.normalized;
    }
}
#endif
