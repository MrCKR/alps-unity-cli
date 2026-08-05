#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>AppendVerticesToEdge 的请求终态证明；planner 只操作托管 Vertex/Face 克隆，绝不读取或修改 effect mesh。</summary>
    internal static class ProBuilderAppendCompletionProof
    {
        // PB 5.2.4 Smoothing 的 internal 范围常量；proof 必须保持同一分组判定但不能引用 internal API。
        private const int SmoothingGroupNone = 0;
        private const int SmoothRangeMax = 24;
        private const int HardRangeMin = 42;

        internal sealed class Plan
        {
            internal readonly CanonicalMeshSnapshot Before;
            internal readonly Edge[] FrozenEdges;
            internal readonly int Count;
            internal readonly long InsertedCount;
            internal readonly RawPlan[] Raw;
            internal readonly FacePlan[] Faces;
            internal readonly int[][] Shared;
            internal readonly int[][] SharedTextures;
            internal Plan(CanonicalMeshSnapshot before, Edge[] edges, int count, long insertedCount, IEnumerable<RawPlan> raw, IEnumerable<FacePlan> faces, IEnumerable<int[]> shared, IEnumerable<int[]> textures)
            { Before = before; FrozenEdges = edges.Select(edge => new Edge(edge.a, edge.b)).ToArray(); Count = count; InsertedCount = insertedCount; Raw = raw.ToArray(); Faces = faces.ToArray(); Shared = shared.Select(value => value.ToArray()).ToArray(); SharedTextures = textures.Select(value => value.ToArray()).ToArray(); }
        }

        internal sealed class RawPlan
        {
            internal readonly Vector3 Position; internal CanonicalMeshSnapshot.ChannelValue Normal, Tangent; internal readonly CanonicalMeshSnapshot.ChannelValue Color; internal readonly CanonicalMeshSnapshot.ChannelValue[] Uvs;
            internal RawPlan(Vertex vertex, CanonicalMeshSnapshot.RawVertex profile) { Position = vertex.position; Normal = Channel(profile.LocalNormal.IsPresent, vertex.normal); Tangent = Channel(profile.Tangent.IsPresent, vertex.tangent); Color = Channel(profile.Color.IsPresent, (Vector4)vertex.color); var uv = profile.Uvs; Uvs = new[] { Channel(uv[0].IsPresent, (Vector4)vertex.uv0), Channel(uv[1].IsPresent, (Vector4)vertex.uv2), Channel(uv[2].IsPresent, vertex.uv3), Channel(uv[3].IsPresent, vertex.uv4) }; }
            internal RawPlan(CanonicalMeshSnapshot.RawVertex vertex) { Position = vertex.LocalPosition; Normal = vertex.LocalNormal; Tangent = vertex.Tangent; Color = vertex.Color; Uvs = vertex.Uvs; }
        }

        internal sealed class FacePlan
        {
            internal readonly CanonicalMeshSnapshot.FaceOccurrence Source; internal readonly int[] RawStream; internal readonly bool Rebuilt;
            internal readonly CanonicalMeshSnapshot.MaterialClaim Material;
            internal FacePlan(CanonicalMeshSnapshot.FaceOccurrence source, IEnumerable<int> raw, bool rebuilt = false, Material rebuiltMaterial = null)
            { Source = source; RawStream = raw.ToArray(); Rebuilt = rebuilt; Material = rebuilt ? CanonicalMeshSnapshot.WithSerializedFaceMaterial(source.Material, rebuiltMaterial) : source.Material; }
        }

        /// <summary>effect 前冻结 target identity、typed edges、全部 canonical channels，并忠实模拟 5.2.4 append 的 terminal mesh。</summary>
        internal static Plan TryCreate(GameObject target, ProBuilderMesh mesh, Edge[] requestedEdges, int count)
        {
            if (target == null || mesh == null || requestedEdges == null || requestedEdges.Length == 0 || count < 1 || count > 512) return null;
            var frozenEdges = requestedEdges.Select(edge => new Edge(edge.a, edge.b)).ToArray();
            var before = CanonicalMeshSnapshot.Capture(target);
            var renderer = target.GetComponent<Renderer>();
            if (before == null || !before.HasSharedTextures || !CanonicalMeshSnapshot.RefreshArithmeticFinite(before) || !MaterialSlotsStable(before, renderer) || !FrozenEdgesAreReadable(before, frozenEdges)) return null;
            // sharedVertices 的原成员顺序决定 AppendElements localEdge 端点；必须从 live group 冻结首 raw。
            var firstRaw = FreezeFirstRaw(mesh, before);
            if (firstRaw == null) return null;

            // Dictionary insertion order follows frozen edge then exact neighbor-face order; overlapping faces would require mutable loop state, so reject before effect.
            var selected = frozenEdges.Select(edge => SharedEdge(before, edge)).ToArray();
            if (selected.Any(edge => edge == null) || selected.Distinct().Count() != selected.Length) return null;
            var affected = selected.SelectMany(edge => NeighborFaces(before, edge.Item1, edge.Item2)).ToArray();
            if (affected.Length == 0 || affected.Distinct().Count() != affected.Length || !FacesHaveExclusiveRawOwnership(before, affected)) return null;
            var insertedCount = (long)frozenEdges.Length * count;
            var workingRawCount = (long)before.RawVertices.Length + affected.Sum(face => (long)face.Boundary.Length + count);
            if (insertedCount <= 0L || insertedCount > int.MaxValue - (long)before.RawVertices.Length || workingRawCount > int.MaxValue) return null;

            try
            {
                var rebuiltMaterial = BuiltinMaterials.defaultMaterial;
                if (rebuiltMaterial == null) return null;
                var deleted = new HashSet<int>(affected.SelectMany(face => face.DirectedRawTriangleStream).Distinct());
                var raw = before.RawVertices.Where(vertex => !deleted.Contains(vertex.RawIndex)).Select(vertex => new RawPlan(vertex)).ToList();
                // lookup handle 是 raw-index keyed Dictionary；保留空 slot 直至所有 AddShared 完成，不能压缩旧 handle。
                var shared = before.SharedVertices.Select(group => group.MemberRawIndexes.Where(index => !deleted.Contains(index)).Select(index => Remap(index, deleted)).ToList()).ToList();
                var textures = before.SharedTextures.Select(group => group.MemberRawIndexes.Where(index => !deleted.Contains(index)).Select(index => Remap(index, deleted)).ToArray()).Where(group => group.Length > 0).ToList();
                var modified = new Dictionary<int, FacePlan>();

                // 5.2.4 Dictionary insertion/raw append 顺序是 frozen common-edge order 再 GetNeighborFaces order，不是 face occurrence order。
                foreach (var face in affected)
                {
                    var edge = selected.Single(value => NeighborFaces(before, value.Item1, value.Item2).Contains(face));
                    var ordered = OrderedFaceVertices(before, face);
                    var edgePosition = FindBoundaryPosition(before, ordered.RawIndexes, edge.Item1, edge.Item2);
                    if (edgePosition < 0) return null;
                    var profile = before.RawVertices[0];
                    var inserted = MixedVertices(before, firstRaw, edge.Item1, edge.Item2, count);
                    if (inserted.Any(vertex => !FiniteForProfile(vertex, profile))) return null;
                    var insertedHandles = Enumerable.Range(0, count).Select(index => before.RawVertices.Length + selected.ToList().IndexOf(edge) * count + index).ToArray();
                    if (ordered.Forward(edgePosition, edge.Item1, edge.Item2)) { ordered.Insert(edgePosition + 1, inserted, insertedHandles); }
                    else { ordered.Insert(edgePosition + 1, inserted.Reverse<Vertex>(), insertedHandles.Reverse()); }

                    var relative = TriangulateExact(ordered.Vertices);
                    if (relative == null) return null;
                    var offset = raw.Count;
                    raw.AddRange(ordered.Vertices.Select(vertex => new RawPlan(vertex, profile)));
                    for (var index = 0; index < ordered.Handles.Count; index++) AddShared(shared, ordered.Handles[index], offset + index);
                    modified.Add(face.OccurrenceIndex, new FacePlan(face, relative.Select(index => offset + index), true, rebuiltMaterial));
                }

                // face array 保留原 occurrence 顺序；仅 raw append 使用 modifiedFaces insertion 顺序。
                var orderedFaces = before.Faces.Select(face => modified.TryGetValue(face.OccurrenceIndex, out var replacement) ? replacement : new FacePlan(face, face.DirectedRawTriangleStream.Select(index => Remap(index, deleted)))).ToArray();
                var terminalShared = shared.Where(group => group.Count > 0).Select(group => group.ToArray()).ToArray();
                // ProBuilderMeshFunction.ToMesh 无条件清空 mesh.uv2；canonical UV1 从 MeshFilter.sharedMesh.uv2 读取。
                foreach (var vertex in raw) vertex.Uvs[1] = new CanonicalMeshSnapshot.ChannelValue(false, Vector4.zero);
                var positions = raw.Select(vertex => vertex.Position).ToArray();
                var streams = orderedFaces.Select(face => face.RawStream).ToArray();
                // 5.2.4 会把跨面的插入顶点共享给不同 UV 岛；tangent 中间倒数可非有限，但最终归一化结果仍确定且有限。
                // CalculateDerivedChannels 逐句重放该终态并验证最终 normal/tangent，不能再用通用中间有限性门禁拒绝合法 Cube edge。
                if (!CanonicalMeshSnapshot.TerminalTopologyReadable(positions, streams, terminalShared)
                    || !CalculateDerivedChannels(raw, orderedFaces, terminalShared)) return null;
                return new Plan(before, frozenEdges, count, insertedCount, raw, orderedFaces, terminalShared, textures);
            }
            catch { return null; }
        }

        /// <summary>effect 后必须从 effect 前 identity fresh-resolve，并对全部 operation-owned terminal channels 作精确比较。</summary>
        internal static bool Matches(Plan plan, string target)
        {
            if (plan == null) return false;
            var freshMesh = CanonicalMeshSnapshot.ResolveFreshMesh(target, plan.Before.Identity);
            var after = freshMesh == null ? null : CanonicalMeshSnapshot.Capture(freshMesh.gameObject);
            if (after == null || !IdentityAndTransformEqual(plan.Before, after) || after.RawVertices.Length != plan.Raw.Length || after.Faces.Length != plan.Faces.Length || after.HasSharedTextures != true) return false;
            for (var index = 0; index < plan.Raw.Length; index++) if (!RawBaseEquals(after.RawVertices[index], plan.Raw[index])) return false;
            for (var index = 0; index < plan.Faces.Length; index++) if (!FaceEquals(after.Faces[index], plan.Faces[index])) return false;
            if (!OrderedPartitionsEqual(after.SharedVertices.Select(group => group.MemberRawIndexes), plan.Shared) || !OrderedPartitionsEqual(after.SharedTextures.Select(group => group.MemberRawIndexes), plan.SharedTextures)) return false;
            // positions/faces/attrs/shared/UV0 已先完整证明，derived replay 才消费最终 normal/tangent。
            return plan.Raw.Select((expected, index) => CanonicalMeshSnapshot.ChannelValueEquals(after.RawVertices[index].LocalNormal, expected.Normal) && CanonicalMeshSnapshot.ChannelValueEquals(after.RawVertices[index].Tangent, expected.Tangent)).All(value => value);
        }

        private sealed class OrderedFace
        {
            internal readonly List<Vertex> Vertices; internal readonly List<int> RawIndexes; internal readonly List<int> Handles;
            internal OrderedFace(IEnumerable<Vertex> vertices, IEnumerable<int> raw, IEnumerable<int> handles) { Vertices = vertices.ToList(); RawIndexes = raw.ToList(); Handles = handles.ToList(); }
            internal bool Forward(int index, int a, int b) => Handles[index] == a && Handles[(index + 1) % Handles.Count] == b;
            internal void Insert(int index, IEnumerable<Vertex> vertices, IEnumerable<int> handles) { var values = vertices.ToArray(); Vertices.InsertRange(index, values); RawIndexes.InsertRange(index, Enumerable.Repeat(-1, values.Length)); Handles.InsertRange(index, handles); }
        }

        // 通过 package-internal exact helper 获取外围循环；reflection 的输入是新建 Face，不访问真实 mesh。
        private static OrderedFace OrderedFaceVertices(CanonicalMeshSnapshot before, CanonicalMeshSnapshot.FaceOccurrence face)
        {
            var clone = new Face(face.DirectedRawTriangleStream);
            var type = typeof(Face).Assembly.GetType("UnityEngine.ProBuilder.WingedEdge");
            var method = type?.GetMethod("SortEdgesByAdjacency", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Face) }, null);
            var edges = method?.Invoke(null, new object[] { clone }) as IEnumerable;
            if (edges == null) return null;
            var raw = edges.Cast<Edge>().Select(edge => edge.a).ToArray();
            return raw.Length < 3 || raw.Distinct().Count() != raw.Length ? null : new OrderedFace(raw.Select(index => ToVertex(before.RawVertices[index])), raw, raw.Select(index => before.RawVertices[index].SharedHandle));
        }

        // 通过 package-internal exact helper 三角化克隆的 managed Vertex 列表；失败即 effect 前拒绝。
        private static List<int> TriangulateExact(List<Vertex> vertices)
        {
            var type = typeof(Face).Assembly.GetType("UnityEngine.ProBuilder.MeshOperations.Triangulation");
            var method = type?.GetMethod("TriangulateVertices", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(IList<Vertex>), typeof(List<int>).MakeByRefType(), typeof(bool), typeof(bool) }, null);
            var args = new object[] { vertices.Select(vertex => new Vertex(vertex)).ToList(), null, false, false };
            return method != null && (bool)method.Invoke(null, args) && args[1] is List<int> result ? result : null;
        }

        private static bool FrozenEdgesAreReadable(CanonicalMeshSnapshot before, IEnumerable<Edge> edges) => edges.All(edge => edge.a >= 0 && edge.b >= 0 && edge.a < before.RawVertices.Length && edge.b < before.RawVertices.Length && edge.a != edge.b);
        /// <summary>ToMesh 会删除空 submesh 并重排 material slots；仅接受每个 slot 都有原始 face 精确引用的稳定布局。</summary>
        private static bool MaterialSlotsStable(CanonicalMeshSnapshot before, Renderer renderer)
        {
            var materials = renderer == null ? null : renderer.sharedMaterials;
            return before != null && materials != null && materials.Length > 0
                && before.Faces.All(face => face.SubmeshIndex >= 0 && face.SubmeshIndex < materials.Length)
                && Enumerable.Range(0, materials.Length).All(slot => before.Faces.Any(face => face.SubmeshIndex == slot));
        }
        private static Tuple<int, int> SharedEdge(CanonicalMeshSnapshot before, Edge edge) { var a = before.RawVertices[edge.a].SharedHandle; var b = before.RawVertices[edge.b].SharedHandle; return Tuple.Create(a, b); }
        private static CanonicalMeshSnapshot.FaceOccurrence[] NeighborFaces(CanonicalMeshSnapshot before, int a, int b) => before.Faces.Where(face => face.Boundary.Any(edge => (edge.SharedA == a && edge.SharedB == b) || (edge.SharedA == b && edge.SharedB == a))).ToArray();
        private static bool FacesHaveExclusiveRawOwnership(CanonicalMeshSnapshot before, IEnumerable<CanonicalMeshSnapshot.FaceOccurrence> faces)
        {
            if (before == null || faces == null) return false;
            var affected = faces.ToArray();
            var occurrences = new HashSet<int>(affected.Select(face => face.OccurrenceIndex));
            var perFace = affected.Select(face => face.DirectedRawTriangleStream.Distinct().ToArray()).ToArray();
            var deleted = new HashSet<int>(perFace.SelectMany(raw => raw));
            return deleted.Count == perFace.Sum(raw => raw.Length)
                && before.Faces.Where(face => !occurrences.Contains(face.OccurrenceIndex)).All(face => face.DirectedRawTriangleStream.All(raw => !deleted.Contains(raw)));
        }
        private static int FindBoundaryPosition(CanonicalMeshSnapshot before, List<int> raw, int a, int b) { for (var index = 0; index < raw.Count; index++) { var x = before.RawVertices[raw[index]].SharedHandle; var y = before.RawVertices[raw[(index + 1) % raw.Count]].SharedHandle; if ((x == a && y == b) || (x == b && y == a)) return index; } return -1; }
        private static int[] FreezeFirstRaw(ProBuilderMesh mesh, CanonicalMeshSnapshot before) { var groups = mesh.sharedVertices; if (groups == null || groups.Count != before.SharedVertices.Length) return null; var first = new int[groups.Count]; for (var handle = 0; handle < groups.Count; handle++) { if (groups[handle] == null || groups[handle].Count == 0) return null; first[handle] = groups[handle][0]; if (!before.SharedVertices[handle].MemberRawIndexes.Contains(first[handle])) return null; } return first; }
        private static List<Vertex> MixedVertices(CanonicalMeshSnapshot before, int[] firstRaw, int a, int b, int count) { var x = ToVertex(before.RawVertices[firstRaw[a]]); var y = ToVertex(before.RawVertices[firstRaw[b]]); return Enumerable.Range(0, count).Select(index => Vertex.Mix(x, y, (index + 1) / ((float)count + 1))).ToList(); }
        private static Vertex ToVertex(CanonicalMeshSnapshot.RawVertex raw) { var vertex = new Vertex { position = raw.LocalPosition }; Set(vertex, raw.LocalNormal, MeshArrays.Normal, value => vertex.normal = (Vector3)value); Set(vertex, raw.Tangent, MeshArrays.Tangent, value => vertex.tangent = value); Set(vertex, raw.Color, MeshArrays.Color, value => vertex.color = (Color)value); var uv = raw.Uvs; Set(vertex, uv[0], MeshArrays.Texture0, value => vertex.uv0 = (Vector2)value); Set(vertex, uv[1], MeshArrays.Texture1, value => vertex.uv2 = (Vector2)value); Set(vertex, uv[2], MeshArrays.Texture2, value => vertex.uv3 = value); Set(vertex, uv[3], MeshArrays.Texture3, value => vertex.uv4 = value); return vertex; }
        private static void Set(Vertex vertex, CanonicalMeshSnapshot.ChannelValue value, MeshArrays attribute, Action<Vector4> set) { if (value.IsPresent) set(value.Value); }
        private static CanonicalMeshSnapshot.ChannelValue Channel(bool present, Vector4 value) => new CanonicalMeshSnapshot.ChannelValue(present, value);
        private static bool FiniteForProfile(Vertex vertex, CanonicalMeshSnapshot.RawVertex profile)
        {
            if (vertex == null || profile == null || !Finite(vertex.position)) return false;
            var uv = profile.Uvs;
            return (!profile.LocalNormal.IsPresent || Finite(vertex.normal)) && (!profile.Tangent.IsPresent || Finite(vertex.tangent))
                && (!profile.Color.IsPresent || Finite((Vector4)vertex.color)) && (!uv[0].IsPresent || Finite((Vector4)vertex.uv0))
                && (!uv[1].IsPresent || Finite((Vector4)vertex.uv2)) && (!uv[2].IsPresent || Finite(vertex.uv3)) && (!uv[3].IsPresent || Finite(vertex.uv4));
        }
        private static void AddShared(List<List<int>> shared, int handle, int raw) { while (shared.Count <= handle) shared.Add(new List<int>()); shared[handle].Add(raw); }
        private static int Remap(int raw, HashSet<int> deleted) => raw - deleted.Count(index => index < raw);
        private static bool IdentityAndTransformEqual(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after) => CanonicalMeshSnapshot.IdentityAndTransformEqual(before, after);
        private static bool RawBaseEquals(CanonicalMeshSnapshot.RawVertex actual, RawPlan expected) => CanonicalMeshSnapshot.ExactEquals(actual.LocalPosition, expected.Position) && CanonicalMeshSnapshot.ChannelValueEquals(actual.Color, expected.Color) && actual.Uvs.Zip(expected.Uvs, CanonicalMeshSnapshot.ChannelValueEquals).All(value => value);
        private static bool FaceEquals(CanonicalMeshSnapshot.FaceOccurrence actual, FacePlan expected) => actual.SubmeshIndex == expected.Source.SubmeshIndex && (!expected.Rebuilt ? actual.SmoothingGroup == expected.Source.SmoothingGroup && actual.TextureGroup == expected.Source.TextureGroup && actual.ElementGroup == expected.Source.ElementGroup && actual.ManualUv == expected.Source.ManualUv && CanonicalMeshSnapshot.UvValueEquals(actual.Uv, expected.Source.Uv) : actual.SmoothingGroup == 0 && actual.TextureGroup == -1 && actual.ElementGroup == 0 && !actual.ManualUv && CanonicalMeshSnapshot.UvValueEquals(actual.Uv, AutoUnwrapSettings.tile)) && CanonicalMeshSnapshot.MaterialValueEquals(actual.Material, expected.Material) && actual.DirectedRawTriangleStream.SequenceEqual(expected.RawStream);
        /// <summary>逐句重放 5.2.4 CalculateHardNormals、shared smoothing pass 和 CalculateTangents；只使用 terminal managed plan。</summary>
        private static bool CalculateDerivedChannels(IList<RawPlan> raw, IList<FacePlan> faces, IEnumerable<int[]> shared)
        {
            var count = raw.Count; var normals = new Vector3[count]; var occurrences = new int[count];
            foreach (var face in faces) for (var tri = 0; tri < face.RawStream.Length; tri += 3)
            {
                var a = face.RawStream[tri]; var b = face.RawStream[tri + 1]; var c = face.RawStream[tri + 2];
                var cross = UnityEngine.ProBuilder.Math.Normal(raw[a].Position, raw[b].Position, raw[c].Position); cross.Normalize();
                normals[a] += cross; normals[b] += cross; normals[c] += cross; occurrences[a]++; occurrences[b]++; occurrences[c]++;
            }
            for (var index = 0; index < count; index++) normals[index] /= occurrences[index];
            var smoothing = new int[count]; var smoothGroupMax = SmoothRangeMax;
            foreach (var face in faces)
            {
                foreach (var index in face.RawStream.Distinct()) { smoothing[index] = face.Rebuilt ? 0 : face.Source.SmoothingGroup; if (smoothing[index] >= smoothGroupMax) smoothGroupMax = smoothing[index] + 1; }
            }
            foreach (var members in shared)
            {
                var sums = new Vector3[smoothGroupMax]; var groupCounts = new float[smoothGroupMax];
                foreach (var index in members) { var group = smoothing[index]; if (group <= SmoothingGroupNone || (group > SmoothRangeMax && group < HardRangeMin)) continue; sums[group] += normals[index]; groupCounts[group] += 1f; }
                foreach (var index in members) { var group = smoothing[index]; if (group <= SmoothingGroupNone || (group > SmoothRangeMax && group < HardRangeMin)) continue; normals[index] = EnsureUnitVector(sums[group] / groupCounts[group]); }
            }
            for (var index = 0; index < count; index++) raw[index].Normal = new CanonicalMeshSnapshot.ChannelValue(true, normals[index]);
            // CalculateTangents creates tangent storage before the position/UV0 early-return path.
            if (raw.Any(vertex => !vertex.Uvs[0].IsPresent)) { for (var index = 0; index < count; index++) raw[index].Tangent = new CanonicalMeshSnapshot.ChannelValue(true, Vector4.zero); return Finite(raw); }
            var tan1 = new Vector3[count]; var tan2 = new Vector3[count];
            foreach (var face in faces) for (var tri = 0; tri < face.RawStream.Length; tri += 3)
            {
                var i1 = face.RawStream[tri]; var i2 = face.RawStream[tri + 1]; var i3 = face.RawStream[tri + 2]; var v1 = raw[i1].Position; var v2 = raw[i2].Position; var v3 = raw[i3].Position; var w1 = raw[i1].Uvs[0].Value; var w2 = raw[i2].Uvs[0].Value; var w3 = raw[i3].Uvs[0].Value;
                var x1 = v2.x-v1.x; var x2 = v3.x-v1.x; var y1 = v2.y-v1.y; var y2 = v3.y-v1.y; var z1 = v2.z-v1.z; var z2 = v3.z-v1.z; var s1 = w2.x-w1.x; var s2 = w3.x-w1.x; var t1 = w2.y-w1.y; var t2 = w3.y-w1.y; var r = 1.0f/(s1*t2-s2*t1);
                var sdir = new Vector3((t2*x1-t1*x2)*r,(t2*y1-t1*y2)*r,(t2*z1-t1*z2)*r); var tdir = new Vector3((s1*x2-s2*x1)*r,(s1*y2-s2*y1)*r,(s1*z2-s2*z1)*r);
                tan1[i1]+=sdir; tan1[i2]+=sdir; tan1[i3]+=sdir; tan2[i1]+=tdir; tan2[i2]+=tdir; tan2[i3]+=tdir;
            }
            for (var index = 0; index < count; index++) { var n = normals[index]; var t = EnsureUnitVector(tan1[index]); Vector3.OrthoNormalize(ref n, ref t); raw[index].Tangent = new CanonicalMeshSnapshot.ChannelValue(true, new Vector4(t.x,t.y,t.z,Vector3.Dot(Vector3.Cross(n,t),tan2[index]) < 0f ? -1f : 1f)); }
            return Finite(raw);
        }
        private static Vector3 EnsureUnitVector(Vector3 v) => Mathf.Abs(v.sqrMagnitude) < float.Epsilon ? Vector3.up : v.normalized;
        private static bool Finite(IEnumerable<RawPlan> raw) => raw.All(vertex => Finite(vertex.Normal.Value.x) && Finite(vertex.Normal.Value.y) && Finite(vertex.Normal.Value.z) && Finite(vertex.Tangent.Value.x) && Finite(vertex.Tangent.Value.y) && Finite(vertex.Tangent.Value.z) && Finite(vertex.Tangent.Value.w));
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        /// <summary>Append 后的 group 与 member 顺序会决定后续 first-member 语义，必须逐层精确比较。</summary>
        private static bool OrderedPartitionsEqual(IEnumerable<IEnumerable<int>> actual, IEnumerable<IEnumerable<int>> expected)
        {
            if (actual == null || expected == null) return false;
            var left = actual.Select(group => group == null ? null : group.ToArray()).ToArray();
            var right = expected.Select(group => group == null ? null : group.ToArray()).ToArray();
            return left.Length == right.Length && left.Select((group, index) => group != null && right[index] != null && group.SequenceEqual(right[index])).All(value => value);
        }
    }
}
#endif
