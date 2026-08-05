#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

namespace UnityCli.ProBuilder
{
    /// <summary>ProBuilder 5.2.4 单一三角形流形边倒角的完整终态证明。</summary>
    internal static class ProBuilderBevelCompletionProof
    {
        internal sealed class Plan
        {
            internal CanonicalMeshSnapshot Before;
            internal Edge[] FrozenEdges;
            internal RawPlan[] Raw;
            internal FacePlan[] Faces;
            internal int[][] Shared;
        }
        internal sealed class RawPlan
        {
            internal Vector3 Position; internal CanonicalMeshSnapshot.ChannelValue Normal, Tangent, Color; internal CanonicalMeshSnapshot.ChannelValue[] Uvs;
            internal RawPlan(Vertex v) { Position = v.position; Normal = Channel(v, MeshArrays.Normal, v.normal); Tangent = Channel(v, MeshArrays.Tangent, v.tangent); Color = Channel(v, MeshArrays.Color, v.color); Uvs = new[] { Channel(v, MeshArrays.Texture0, v.uv0), Channel(v, MeshArrays.Texture1, v.uv2), Channel(v, MeshArrays.Texture2, v.uv3), Channel(v, MeshArrays.Texture3, v.uv4) }; }
        }
        internal sealed class FacePlan
        {
            internal int[] Stream; internal int Submesh, Smoothing, Texture, Element; internal bool Manual; internal AutoUnwrapSettings Uv; internal CanonicalMeshSnapshot.MaterialClaim Material;
        }
        private sealed class Bridge { internal List<Vertex> Vertices; internal FacePlan Face; }
        private const int SmoothingGroupNone = 0;
        private const int SmoothRangeMax = 24;
        private const int HardRangeMax = 42;

        /// <summary>effect 前只接受一个冻结 typed edge 与两个独占非退化三角形；无法关闭 Bevel 内部路径即拒绝。</summary>
        internal static Plan Capture(GameObject target, ProBuilderMesh typedMesh, Edge[] frozenEdges, float requestedAmount)
        {
            if (target == null || typedMesh == null || frozenEdges == null || frozenEdges.Length != 1 || !Finite(requestedAmount) || requestedAmount < .001f) return null;
            var before = CanonicalMeshSnapshot.Capture(target); var frozen = new Edge(frozenEdges[0].a, frozenEdges[0].b); var renderer = target.GetComponent<Renderer>();
            if (before == null || !before.HasSharedTextures || !CanonicalMeshSnapshot.CurrentMeshFormat(typedMesh) || !CanonicalMeshSnapshot.RefreshArithmeticFinite(before) || !MaterialSlotsStable(before, renderer) || before.Faces.Length != 2 || before.Faces.Any(x => x.DirectedRawTriangleStream.Length != 3) || frozen.a < 0 || frozen.b < 0 || frozen.a == frozen.b || frozen.a >= before.RawVertices.Length || frozen.b >= before.RawVertices.Length) return null;
            try
            {
                // 精确镜像 Bevel.cs：只读 typed mesh，EdgeLookup 后以 Equals 定位 exact wing/opposite。
                var lup = EdgeLookup.GetEdgeLookup(new[] { frozen }, BuildSharedLookup(typedMesh)).SingleOrDefault();
                var wings = WingedEdge.GetWingedEdges(typedMesh);
                var wing = wings.FirstOrDefault(x => x.edge.Equals(lup));
                if (wing == null || wing.opposite == null || !StrictDomain(before, frozen, lup, wing)) return null;
                var vertices = before.RawVertices.Select(ToVertex).ToList(); var faces = before.Faces.Select(ToFace).ToArray();
                var amount = ClampExact(wings, vertices, lup, requestedAmount);
                if (amount != requestedAmount || amount < .001f) return null;
                // 此闭集使 sorted、VertexEditing.ExplodeVertex、holeFaces 始终为空，只需两次 exact SlideEdge。
                if (!SlideEdgeExact(vertices, faces, before, wing, amount) || !SlideEdgeExact(vertices, faces, before, wing.opposite, amount)) return null;
                // 此 strict wedge 中 appendFaces 只有 bridge，sorted 与 holeFaces 都为空；后续只以 bridge 枚举 wing，无法跨到原 face。
                var bridge = GetBridgeFacesExact(vertices, before, wing, wing.opposite);
                if (bridge == null) return null;
                var raw = vertices.Select(x => new RawPlan(x)).ToList(); var offset = raw.Count; raw.AddRange(bridge.Vertices.Select(x => new RawPlan(x)));
                bridge.Face.Stream = bridge.Face.Stream.Select(x => x + offset).ToArray();
                var terminalFaces = faces.Concat(new[] { bridge.Face }).ToArray();
                // FaceRebuildData.Apply(appendFaces) 只追加 bridge；holeFaces 为空，sharedTextures 为 non-null empty。
                var positions = raw.Select(x => x.Position).ToArray();
                var shared = SharedVertex.GetSharedVerticesWithPositions(positions).Select(x => x.ToArray()).ToArray();
                if (!CanonicalMeshSnapshot.TerminalTopologyReadable(positions, terminalFaces.Select(face => face.Stream), shared)
                    || !CanonicalMeshSnapshot.RefreshArithmeticFinite(positions, raw.Select(vertex => vertex.Uvs[0]).ToArray(), terminalFaces.Select(face => face.Stream), terminalFaces.Select(face => face.Smoothing))
                    || !CalculateNormalsAndTangents(raw, terminalFaces, shared)) return null;
                // ProBuilderMeshFunction.ToMesh 与 handler refresh 都会清除 Unity mesh uv2，canonical UV1 必须在完整 plan 完成后覆盖。
                ClearUnityMeshUv1(raw);
                return new Plan { Before = before, FrozenEdges = new[] { frozen }, Raw = raw.ToArray(), Faces = terminalFaces, Shared = shared };
            }
            catch { return null; }
        }

        /// <summary>fresh readback 对 identity、transform、raw、face、shared partition 和 derived channels 完整比较。</summary>
        internal static bool Matches(Plan plan, CanonicalMeshSnapshot after) => Mismatch(plan, after) == null;

        /// <summary>返回首个 exact mismatch path；仅用于 post-effect 诊断，不改变成功判定。</summary>
        internal static string Mismatch(Plan plan, CanonicalMeshSnapshot after)
        {
            if (plan == null) return "plan";
            if (after == null) return "readback";
            if (!Stable(plan.Before, after)) return "identity-or-transform";
            if (after.RawVertices.Length != plan.Raw.Length) return "raw.length";
            if (after.Faces.Length != plan.Faces.Length) return "faces.length";
            if (!after.HasSharedTextures || after.SharedTextures.Length != 0) return "sharedTextures";
            for (var i = 0; i < plan.Raw.Length; i++)
            {
                if (RawEquals(after.RawVertices[i], plan.Raw[i])) continue;
                return "raw[" + i + "]." + RawMismatch(after.RawVertices[i], plan.Raw[i]);
            }
            for (var i = 0; i < plan.Faces.Length; i++)
            {
                if (FaceEquals(after.Faces[i], plan.Faces[i])) continue;
                return "faces[" + i + "]." + FaceMismatch(after.Faces[i], plan.Faces[i]);
            }
            return OrderedSharedPartitionsEqual(after.SharedVertices, plan.Shared) ? null : "sharedVertices";
        }

        /// <summary>保持 package 生成的 group 与 member 顺序，避免改变 shared handle 和 first-member 语义。</summary>
        private static bool OrderedSharedPartitionsEqual(CanonicalMeshSnapshot.SharedVertexSnapshot[] actual, int[][] expected)
        {
            if (actual == null || expected == null || actual.Length != expected.Length) return false;
            for (var i = 0; i < expected.Length; i++)
                if (actual[i] == null || expected[i] == null || !actual[i].MemberRawIndexes.SequenceEqual(expected[i])) return false;
            return true;
        }

        private static bool StrictDomain(CanonicalMeshSnapshot before, Edge frozen, EdgeLookup lup, WingedEdge wing)
        {
            var a = before.RawVertices[frozen.a]; var b = before.RawVertices[frozen.b];
            var wingIndexes = wing.face.indexes.ToArray(); var oppositeIndexes = wing.opposite.face.indexes.ToArray();
            if (a.SharedHandle == b.SharedHandle || a.SharedHandle != lup.common.a || b.SharedHandle != lup.common.b || wingIndexes.Length != 3 || oppositeIndexes.Length != 3) return false;
            var requested = before.Edges.SingleOrDefault(x => Same(x.SharedMin, x.SharedMax, lup.common.a, lup.common.b));
            if (requested == null || requested.Incidences.Length != 2) return false;
            var both = before.Faces.Where(x => x.DirectedSharedTriangleStream.Contains(lup.common.a) || x.DirectedSharedTriangleStream.Contains(lup.common.b)).ToArray();
            if (both.Length != 2 || both.SelectMany(x => x.DirectedRawTriangleStream).Distinct().Count() != 6) return false;
            // endpoint handle 只能有两个 raw occurrence，且仅在两面 incident；禁止额外 raw/face/position coincidence。
            foreach (var handle in new[] { lup.common.a, lup.common.b })
            {
                var group = before.SharedVertices.Single(x => x.Handle == handle);
                if (group.MemberRawIndexes.Length != 2 || before.Edges.Count(x => x.SharedMin == handle || x.SharedMax == handle) != 3) return false;
            }
            var used = both.SelectMany(x => x.DirectedRawTriangleStream).ToArray();
            return before.RawVertices.Where(x => !used.Contains(x.RawIndex)).All(x => !used.Select(i => before.RawVertices[i].LocalPosition).Any(p => CanonicalMeshSnapshot.ExactEquals(p, x.LocalPosition)));
        }
        /// <summary>从公开 sharedVertices 的 group/member 重放 5.2.4 shared lookup。</summary>
        private static Dictionary<int, int> BuildSharedLookup(ProBuilderMesh mesh)
        {
            var lookup = new Dictionary<int, int>(); var sharedVertices = mesh.sharedVertices;
            for (int group = 0, count = sharedVertices.Count; group < count; group++)
                foreach (var member in sharedVertices[group])
                    if (!lookup.ContainsKey(member)) lookup.Add(member, group);
            return lookup;
        }
        /// <summary>完整复制 5.2.4 顶点索引 normal 的逐三角形平均路径。</summary>
        private static Vector3 Normal(IList<Vertex> vertices, IList<int> indexes = null)
        {
            if (indexes == null || indexes.Count % 3 != 0)
            {
                Vector3 cross = Vector3.Cross(vertices[1].position - vertices[0].position, vertices[2].position - vertices[0].position);
                cross.Normalize();
                return cross;
            }
            else
            {
                int len = indexes.Count;
                Vector3 nrm = Vector3.zero;

                for (int i = 0; i < len; i += 3)
                    nrm += UnityEngine.ProBuilder.Math.Normal(vertices[indexes[i]].position, vertices[indexes[i + 1]].position, vertices[indexes[i + 2]].position);

                nrm /= (len / 3f);
                nrm.Normalize();

                return nrm;
            }
        }
        /// <summary>完整复制 5.2.4 Vector3 零向量单位化规则。</summary>
        private static Vector3 EnsureUnitVector(Vector3 value)
        {
            return Mathf.Abs(value.sqrMagnitude) < float.Epsilon ? Vector3.up : value.normalized;
        }
        private static float ClampExact(IEnumerable<WingedEdge> wings, IList<Vertex> vertices, EdgeLookup requested, float amount)
        {
            foreach (var common in new[] { requested.common.a, requested.common.b }) foreach (var wing in wings.Where(x => x.edge.common.Contains(common))) { var edge = wing.edge.local; amount = Mathf.Min(amount, Vector3.Distance(vertices[edge.a].position, vertices[edge.b].position) - .001f); }
            return amount;
        }
        /// <summary>ToMesh 会压缩空 submesh；所有 renderer slot 均须由一个合法 face 使用，才能保持 material claim。</summary>
        private static bool MaterialSlotsStable(CanonicalMeshSnapshot before, Renderer renderer)
        {
            var materials = renderer == null ? null : renderer.sharedMaterials;
            if (before == null || materials == null || materials.Length == 0) return false;
            var used = new HashSet<int>();
            foreach (var face in before.Faces)
            {
                if (face.SubmeshIndex < 0 || face.SubmeshIndex >= materials.Length) return false;
                used.Add(face.SubmeshIndex);
            }
            return used.SetEquals(Enumerable.Range(0, materials.Length));
        }
        private static bool SlideEdgeExact(IList<Vertex> vertices, FacePlan[] faces, CanonicalMeshSnapshot before, WingedEdge wing, float amount)
        {
            var wingIndexes = wing.face.indexes.ToArray(); var occurrence = Array.FindIndex(before.Faces, x => x.DirectedRawTriangleStream.SequenceEqual(wingIndexes)); if (occurrence < 0) return false;
            faces[occurrence].Manual = true; faces[occurrence].Texture = -1;
            var x = Leading(wing, wing.edge.common.a); var y = Leading(wing, wing.edge.common.b); if (!x.IsValid() || !y.IsValid()) return false;
            var dx = vertices[x.a] - vertices[x.b]; var dy = vertices[y.a] - vertices[y.b]; dx.Normalize(); dy.Normalize(); vertices[wing.edge.local.a].Add(dx * amount); vertices[wing.edge.local.b].Add(dy * amount);
            return Finite(vertices[wing.edge.local.a].position) && Finite(vertices[wing.edge.local.b].position);
        }
        private static Edge Leading(WingedEdge wing, int common) => wing.previous.edge.common.a == common ? new Edge(wing.previous.edge.local.b, wing.previous.edge.local.a) : wing.previous.edge.common.b == common ? new Edge(wing.previous.edge.local.a, wing.previous.edge.local.b) : wing.next.edge.common.a == common ? new Edge(wing.next.edge.local.b, wing.next.edge.local.a) : wing.next.edge.common.b == common ? new Edge(wing.next.edge.local.a, wing.next.edge.local.b) : Edge.Empty;
        private static Bridge GetBridgeFacesExact(IList<Vertex> vertices, CanonicalMeshSnapshot before, WingedEdge left, WingedEdge right)
        {
            var a = left.edge; var b = right.edge; var values = new List<Vertex> { new Vertex(vertices[a.local.a]), new Vertex(vertices[a.local.b]), new Vertex(vertices[a.common.a == b.common.a ? b.local.a : b.local.b]), new Vertex(vertices[a.common.a == b.common.a ? b.local.b : b.local.a]) };
            var leftIndexes = left.face.indexes.ToArray(); var stream = new[] { 2, 1, 0, 2, 3, 1 }; var an = Normal(vertices, leftIndexes); var bn = Normal(values, new[] { 2, 1, 0 }); if (Vector3.Dot(an, bn) < 0f) Array.Reverse(stream);
            var source = before.Faces.Single(x => x.DirectedRawTriangleStream.SequenceEqual(leftIndexes));
            return new Bridge { Vertices = values, Face = new FacePlan { Stream = stream, Submesh = source.SubmeshIndex, Smoothing = -1, Texture = -1, Element = -1, Manual = false, Uv = AutoUnwrapSettings.tile, Material = CanonicalMeshSnapshot.WithSerializedFaceMaterial(source.Material, null) } };
        }
        private static bool CalculateNormalsAndTangents(IList<RawPlan> raw, IList<FacePlan> faces, IEnumerable<int[]> shared)
        {
            // 逐句重放 5.2.4 CalculateHardNormals、shared smoothing pass 与 CalculateTangents。
            var normals = new Vector3[raw.Count]; var counts = new int[raw.Count];
            foreach (var face in faces) for (var i = 0; i < face.Stream.Length; i += 3) { var a = face.Stream[i]; var b = face.Stream[i + 1]; var c = face.Stream[i + 2]; var n = UnityEngine.ProBuilder.Math.Normal(raw[a].Position, raw[b].Position, raw[c].Position); n.Normalize(); normals[a] += n; normals[b] += n; normals[c] += n; counts[a]++; counts[b]++; counts[c]++; }
            if (counts.Any(x => x == 0)) return false;
            for (var i = 0; i < raw.Count; i++) normals[i] /= counts[i];
            var smoothing = new int[raw.Count]; var maximum = SmoothRangeMax;
            foreach (var face in faces) foreach (var index in face.Stream.Distinct()) { smoothing[index] = face.Smoothing; if (smoothing[index] >= maximum) maximum = smoothing[index] + 1; }
            foreach (var members in shared) { var sums = new Vector3[maximum]; var groupCounts = new float[maximum]; foreach (var index in members) { var group = smoothing[index]; if (group <= SmoothingGroupNone || group > SmoothRangeMax && group < HardRangeMax) continue; sums[group] += normals[index]; groupCounts[group]++; } foreach (var index in members) { var group = smoothing[index]; if (group <= SmoothingGroupNone || group > SmoothRangeMax && group < HardRangeMax) continue; normals[index] = EnsureUnitVector(sums[group] / groupCounts[group]); } }
            for (var i = 0; i < raw.Count; i++) raw[i].Normal = new CanonicalMeshSnapshot.ChannelValue(true, normals[i]);
            // CalculateTangents 先分配 tangent；当 UV0 缺失时保留全零，而不是跳过该 channel。
            if (raw.Any(x => !x.Uvs[0].IsPresent)) { for (var i = 0; i < raw.Count; i++) raw[i].Tangent = new CanonicalMeshSnapshot.ChannelValue(true, Vector4.zero); return Finite(raw); }
            var positions = raw.Select(x => x.Position).ToArray();
            var textures = raw.Select(x => new Vector2(x.Uvs[0].Value.x, x.Uvs[0].Value.y)).ToArray();
            var tan1 = new Vector3[raw.Count];
            var tan2 = new Vector3[raw.Count];
            foreach (var face in faces)
            {
                int[] triangles = face.Stream;
                for (int a = 0, c = triangles.Length; a < c; a += 3)
                {
                    long i1 = triangles[a + 0];
                    long i2 = triangles[a + 1];
                    long i3 = triangles[a + 2];
                    Vector3 v1 = positions[i1];
                    Vector3 v2 = positions[i2];
                    Vector3 v3 = positions[i3];
                    Vector2 w1 = textures[i1];
                    Vector2 w2 = textures[i2];
                    Vector2 w3 = textures[i3];
                    float x1 = v2.x - v1.x;
                    float x2 = v3.x - v1.x;
                    float y1 = v2.y - v1.y;
                    float y2 = v3.y - v1.y;
                    float z1 = v2.z - v1.z;
                    float z2 = v3.z - v1.z;
                    float s1 = w2.x - w1.x;
                    float s2 = w3.x - w1.x;
                    float t1 = w2.y - w1.y;
                    float t2 = w3.y - w1.y;
                    float r = 1.0f / (s1 * t2 - s2 * t1);
                    Vector3 sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
                    Vector3 tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);
                    tan1[i1] += sdir;
                    tan1[i2] += sdir;
                    tan1[i3] += sdir;
                    tan2[i1] += tdir;
                    tan2[i2] += tdir;
                    tan2[i3] += tdir;
                }
            }
            for (var i = 0; i < raw.Count; i++)
            {
                var n = normals[i];
                var t = EnsureUnitVector(tan1[i]);
                Vector3.OrthoNormalize(ref n, ref t);
                var tangent = Vector4.zero;
                tangent.x = t.x;
                tangent.y = t.y;
                tangent.z = t.z;
                tangent.w = Vector3.Dot(Vector3.Cross(n, t), tan2[i]) < 0.0f ? -1.0f : 1.0f;
                raw[i].Tangent = new CanonicalMeshSnapshot.ChannelValue(true, tangent);
            }
            return Finite(raw);
        }
        private static FacePlan ToFace(CanonicalMeshSnapshot.FaceOccurrence x) => new FacePlan { Stream = x.DirectedRawTriangleStream, Submesh = x.SubmeshIndex, Smoothing = x.SmoothingGroup, Texture = x.TextureGroup, Element = x.ElementGroup, Manual = x.ManualUv, Uv = x.Uv, Material = x.Material };
        private static Vertex ToVertex(CanonicalMeshSnapshot.RawVertex x) { var v = new Vertex { position = x.LocalPosition }; Set(v, x.LocalNormal, MeshArrays.Normal, y => v.normal = y); Set(v, x.Tangent, MeshArrays.Tangent, y => v.tangent = y); Set(v, x.Color, MeshArrays.Color, y => v.color = y); var uv = x.Uvs; Set(v, uv[0], MeshArrays.Texture0, y => v.uv0 = y); Set(v, uv[1], MeshArrays.Texture1, y => v.uv2 = y); Set(v, uv[2], MeshArrays.Texture2, y => v.uv3 = y); Set(v, uv[3], MeshArrays.Texture3, y => v.uv4 = y); return v; }
        private static void Set(Vertex v, CanonicalMeshSnapshot.ChannelValue x, MeshArrays a, Action<Vector4> set) { if (x.IsPresent) set(x.Value); }
        private static CanonicalMeshSnapshot.ChannelValue Channel(Vertex v, MeshArrays a, Vector4 x) => new CanonicalMeshSnapshot.ChannelValue(v.HasArrays(a), x);
        /// <summary>RefreshMeshPreservingVertexPayload 经 ToMesh，所有 raw 的 canonical UV1 都必须缺失。</summary>
        private static void ClearUnityMeshUv1(IEnumerable<RawPlan> raw) { foreach (var vertex in raw) vertex.Uvs[1] = new CanonicalMeshSnapshot.ChannelValue(false, Vector4.zero); }
        private static bool RawEquals(CanonicalMeshSnapshot.RawVertex a, RawPlan b) => RawMismatch(a, b) == null;
        private static string RawMismatch(CanonicalMeshSnapshot.RawVertex a, RawPlan b)
        {
            if (!CanonicalMeshSnapshot.ExactEquals(a.LocalPosition, b.Position)) return "position";
            if (!CanonicalMeshSnapshot.ChannelValueEquals(a.LocalNormal, b.Normal)) return "normal";
            if (!CanonicalMeshSnapshot.ChannelValueEquals(a.Tangent, b.Tangent)) return "tangent";
            if (!CanonicalMeshSnapshot.ChannelValueEquals(a.Color, b.Color)) return "color";
            for (var i = 0; i < b.Uvs.Length; i++) if (!CanonicalMeshSnapshot.ChannelValueEquals(a.Uvs[i], b.Uvs[i])) return "uv" + i;
            return null;
        }
        private static bool FaceEquals(CanonicalMeshSnapshot.FaceOccurrence a, FacePlan b) => FaceMismatch(a, b) == null;
        private static string FaceMismatch(CanonicalMeshSnapshot.FaceOccurrence a, FacePlan b)
        {
            if (!a.DirectedRawTriangleStream.SequenceEqual(b.Stream)) return "stream";
            if (a.SubmeshIndex != b.Submesh) return "submesh";
            if (a.SmoothingGroup != b.Smoothing) return "smoothing";
            if (a.TextureGroup != b.Texture) return "textureGroup";
            if (a.ElementGroup != b.Element) return "elementGroup";
            if (a.ManualUv != b.Manual) return "manualUv";
            if (!CanonicalMeshSnapshot.UvValueEquals(a.Uv, b.Uv)) return "uv";
            return CanonicalMeshSnapshot.MaterialValueEquals(a.Material, b.Material) ? null : "material";
        }
        private static bool Stable(CanonicalMeshSnapshot a, CanonicalMeshSnapshot b) => CanonicalMeshSnapshot.IdentityAndTransformEqual(a, b);
        private static bool Same(int a, int b, int c, int d) => a == c && b == d || a == d && b == c;
        private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        private static bool Finite(Vector3 x) => Finite(x.x) && Finite(x.y) && Finite(x.z);
        private static bool Finite(Vector4 x) => Finite(x.x) && Finite(x.y) && Finite(x.z) && Finite(x.w);
        private static bool Finite(IEnumerable<RawPlan> raw) => raw.All(x => Finite(x.Position) && Finite(x.Normal.Value) && Finite(x.Tangent.Value));
    }
}
#endif
