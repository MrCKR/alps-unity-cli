#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>flip-normals 与 duplicate-and-flip 的请求专属终态证明；planner 只读取 barrier 前快照。</summary>
    internal static class ProBuilderFlipCompletionProof
    {
        private const int SmoothingNone = 0;
        private const int SmoothRangeMax = 24;
        private const int HardRangeMax = 42;

        internal sealed class Plan
        {
            internal readonly CanonicalMeshSnapshot Before;
            internal readonly Face[] FrozenFaces;
            internal readonly RawPlan[] Raw;
            internal readonly FacePlan[] Faces;
            internal readonly int[][] Shared;
            internal readonly int[][] SharedTextures;
            internal readonly bool HasSharedTextures;
            internal Plan(CanonicalMeshSnapshot before, Face[] frozen, IEnumerable<RawPlan> raw, IEnumerable<FacePlan> faces, IEnumerable<int[]> shared, bool hasTextures, IEnumerable<int[]> textures)
            { Before = before; FrozenFaces = frozen.ToArray(); Raw = raw.ToArray(); Faces = faces.ToArray(); Shared = shared.Select(x => x.ToArray()).ToArray(); HasSharedTextures = hasTextures; SharedTextures = textures.Select(x => x.ToArray()).ToArray(); }
        }
        internal sealed class RawPlan
        {
            internal readonly Vector3 Position;
            internal CanonicalMeshSnapshot.ChannelValue Normal, Tangent;
            internal readonly CanonicalMeshSnapshot.ChannelValue Color;
            internal readonly CanonicalMeshSnapshot.ChannelValue[] Uvs;
            internal RawPlan(CanonicalMeshSnapshot.RawVertex raw) { Position = raw.LocalPosition; Normal = raw.LocalNormal; Tangent = raw.Tangent; Color = raw.Color; Uvs = raw.Uvs; }
        }
        internal sealed class FacePlan
        {
            internal readonly CanonicalMeshSnapshot.FaceOccurrence Source;
            internal readonly int[] Stream;
            internal FacePlan(CanonicalMeshSnapshot.FaceOccurrence source, IEnumerable<int> stream) { Source = source; Stream = stream.ToArray(); }
        }

        /// <summary>冻结 Face[] 与完整 canonical 状态；selected occurrence 必须去重且与 typed Face 引用一一对应。</summary>
        internal static Plan CaptureFlip(GameObject target, Face[] faces, int[] occurrences)
        {
            var before = Capture(target, faces, occurrences);
            if (before == null) return null;
            var selected = new HashSet<int>(occurrences);
            var raw = before.RawVertices.Select(x => new RawPlan(x)).ToArray();
            var terminal = before.Faces.Select(face => new FacePlan(face, selected.Contains(face.OccurrenceIndex) ? face.DirectedRawTriangleStream.Reverse() : face.DirectedRawTriangleStream)).ToArray();
            ClearUnityMeshUv2(raw);
            if (!RefreshArithmeticFinite(raw, terminal) || !CalculateNormalsAndTangents(raw, terminal, before.SharedVertices.Select(x => x.MemberRawIndexes))) return null;
            return new Plan(before, faces, raw, terminal, before.SharedVertices.Select(x => x.MemberRawIndexes), before.HasSharedTextures, before.SharedTextures.Select(x => x.MemberRawIndexes));
        }

        /// <summary>逐句镜像 5.2.4 DuplicateAndFlip：按冻结请求顺序及 raw 首遇顺序复制顶点，再用反向 mapped stream 重建 Face。</summary>
        internal static Plan CaptureDuplicateAndFlip(GameObject target, Face[] faces, int[] occurrences)
        {
            var before = Capture(target, faces, occurrences);
            if (before == null) return null;
            var raw = before.RawVertices.Select(x => new RawPlan(x)).ToList();
            var shared = before.SharedVertices.Select(x => x.MemberRawIndexes.ToList()).ToList();
            var terminal = before.Faces.Select(face => new FacePlan(face, face.DirectedRawTriangleStream)).ToList();
            foreach (var occurrence in occurrences)
            {
                // 5.2.4 在每个 FaceRebuildData group 内新建 map；共享 raw 被不同 selected face 各自独立复制。
                var map = new Dictionary<int, int>();
                var source = before.Faces.Single(face => face.OccurrenceIndex == occurrence);
                foreach (var oldRaw in source.DirectedRawTriangleStream)
                {
                    if (map.ContainsKey(oldRaw)) continue;
                    var newRaw = raw.Count; map.Add(oldRaw, newRaw); raw.Add(new RawPlan(before.RawVertices[oldRaw]));
                    shared[before.RawVertices[oldRaw].SharedHandle].Add(newRaw);
                }
                // FaceRebuildData.Apply 将每个 request group 的 raw 与其 duplicate face 依次 append；Face attrs 原样复制。
                terminal.Add(new FacePlan(source, source.DirectedRawTriangleStream.Reverse().Select(oldRaw => map[oldRaw])));
            }
            ClearUnityMeshUv2(raw);
            if (!RefreshArithmeticFinite(raw, terminal) || !CalculateNormalsAndTangents(raw, terminal, shared)) return null;
            // SetSharedTextures 即使原值为 null 也会写入 non-null 分区；新 raw 不加入旧 texture group。
            return new Plan(before, faces, raw, terminal, shared.Select(x => x.ToArray()), true, before.SharedTextures.Select(x => x.MemberRawIndexes));
        }

        private static bool RefreshArithmeticFinite(IEnumerable<RawPlan> raw, IEnumerable<FacePlan> faces)
        {
            var vertices = raw.ToArray(); var terminal = faces.ToArray();
            return CanonicalMeshSnapshot.RefreshArithmeticFinite(vertices.Select(value => value.Position).ToArray(), vertices.Select(value => value.Uvs[0]).ToArray(), terminal.Select(face => face.Stream), terminal.Select(face => face.Source.SmoothingGroup), true);
        }

        /// <summary>effect 后只允许用 effect 前 identity fresh-resolve；逐 raw、逐 face、逐 partition 比较完整 terminal state。</summary>
        internal static bool FlipMatches(Plan plan, string target) => Matches(plan, target);
        internal static bool DuplicateAndFlipMatches(Plan plan, string target) => Matches(plan, target);
        private static bool Matches(Plan plan, string target)
        {
            if (plan == null) return false;
            var fresh = CanonicalMeshSnapshot.ResolveFreshMesh(target, plan.Before.Identity);
            var after = fresh == null ? null : CanonicalMeshSnapshot.Capture(fresh.gameObject, Array.Empty<int>());
            if (after == null || !Stable(plan.Before, after) || after.RawVertices.Length != plan.Raw.Length || after.Faces.Length != plan.Faces.Length || after.HasSharedTextures != plan.HasSharedTextures) return false;
            for (var i = 0; i < plan.Raw.Length; i++) if (!RawEquals(after.RawVertices[i], plan.Raw[i])) return false;
            for (var i = 0; i < plan.Faces.Length; i++) if (!FaceEquals(after.Faces[i], plan.Faces[i])) return false;
            return PartitionsEqual(after.SharedVertices.Select(x => x.MemberRawIndexes), plan.Shared) && PartitionsEqual(after.SharedTextures.Select(x => x.MemberRawIndexes), plan.SharedTextures);
        }

        private static CanonicalMeshSnapshot Capture(GameObject target, Face[] faces, int[] occurrences)
        {
            if (target == null || faces == null || occurrences == null || faces.Length == 0 || faces.Length != occurrences.Length || occurrences.Distinct().Count() != occurrences.Length) return null;
            var before = CanonicalMeshSnapshot.Capture(target, occurrences);
            if (before == null || before.Faces.Length == 0 || !MaterialSlotsStable(target, before)) return null;
            for (var i = 0; i < occurrences.Length; i++) if (occurrences[i] < 0 || occurrences[i] >= before.Faces.Length || !ReferenceEquals(faces[i], target.GetComponent<ProBuilderMesh>().faces[occurrences[i]])) return null;
            return before;
        }
        /// <summary>ToMesh 会压缩空 submesh；只有所有 material slot 被一个合法 face 使用时，slot/material 对应关系才可完整证明。</summary>
        private static bool MaterialSlotsStable(GameObject target, CanonicalMeshSnapshot before)
        {
            var renderer = target.GetComponent<Renderer>(); var materials = renderer == null ? null : renderer.sharedMaterials;
            if (materials == null || materials.Length == 0) return false;
            var used = new HashSet<int>();
            foreach (var face in before.Faces)
            {
                if (face.SubmeshIndex < 0 || face.SubmeshIndex >= materials.Length) return false;
                used.Add(face.SubmeshIndex);
            }
            return used.SetEquals(Enumerable.Range(0, materials.Length));
        }
        /// <summary>RefreshMeshPreservingVertexPayload 仍经 ToMesh，Unity Mesh 的 uv2 会被无条件清除。</summary>
        private static void ClearUnityMeshUv2(IEnumerable<RawPlan> raw)
        {
            foreach (var vertex in raw) vertex.Uvs[1] = new CanonicalMeshSnapshot.ChannelValue(false, Vector4.zero);
        }
        private static bool Stable(CanonicalMeshSnapshot a, CanonicalMeshSnapshot b) => CanonicalMeshSnapshot.IdentityAndTransformEqual(a, b);
        private static bool RawEquals(CanonicalMeshSnapshot.RawVertex actual, RawPlan expected) => CanonicalMeshSnapshot.ExactEquals(actual.LocalPosition, expected.Position) && CanonicalMeshSnapshot.ChannelValueEquals(actual.LocalNormal, expected.Normal) && CanonicalMeshSnapshot.ChannelValueEquals(actual.Tangent, expected.Tangent) && CanonicalMeshSnapshot.ChannelValueEquals(actual.Color, expected.Color) && actual.Uvs.Zip(expected.Uvs, CanonicalMeshSnapshot.ChannelValueEquals).All(x => x);
        private static bool FaceEquals(CanonicalMeshSnapshot.FaceOccurrence actual, FacePlan expected) => actual.DirectedRawTriangleStream.SequenceEqual(expected.Stream) && actual.SubmeshIndex == expected.Source.SubmeshIndex && actual.SmoothingGroup == expected.Source.SmoothingGroup && actual.TextureGroup == expected.Source.TextureGroup && actual.ElementGroup == expected.Source.ElementGroup && actual.ManualUv == expected.Source.ManualUv && CanonicalMeshSnapshot.UvValueEquals(actual.Uv, expected.Source.Uv) && CanonicalMeshSnapshot.MaterialValueEquals(actual.Material, expected.Source.Material);
        /// <summary>raw index 在此终态保持稳定；shared/sharedTexture 的每个 handle 与成员流均逐项精确比较。</summary>
        private static bool PartitionsEqual(IEnumerable<IEnumerable<int>> actual, IEnumerable<IEnumerable<int>> expected)
        {
            var left = actual.Select(x => x.ToArray()).ToArray(); var right = expected.Select(x => x.ToArray()).ToArray();
            return left.Length == right.Length && left.Select((members, index) => members.SequenceEqual(right[index])).All(value => value);
        }

        /// <summary>纯 5.2.4 Normals/CalculateTangents replay；先重建 normal，再由终态 position 与 UV0 得到 tangent。</summary>
        private static bool CalculateNormalsAndTangents(IList<RawPlan> raw, IList<FacePlan> faces, IEnumerable<IEnumerable<int>> shared)
        {
            var normals = new Vector3[raw.Count]; var counts = new int[raw.Count];
            foreach (var face in faces) for (var i = 0; i < face.Stream.Length; i += 3)
            {
                var a = face.Stream[i]; var b = face.Stream[i + 1]; var c = face.Stream[i + 2]; var normal = UnityEngine.ProBuilder.Math.Normal(raw[a].Position, raw[b].Position, raw[c].Position); normal.Normalize();
                normals[a] += normal; normals[b] += normal; normals[c] += normal; counts[a]++; counts[b]++; counts[c]++;
            }
            if (counts.Any(x => x == 0)) return false;
            for (var i = 0; i < raw.Count; i++) normals[i] /= counts[i];
            var smoothing = new int[raw.Count]; var maximum = SmoothRangeMax;
            foreach (var face in faces) foreach (var index in face.Stream.Distinct()) { smoothing[index] = face.Source.SmoothingGroup; if (smoothing[index] >= maximum) maximum = smoothing[index] + 1; }
            foreach (var members in shared) { var sums = new Vector3[maximum]; var groupCounts = new float[maximum]; foreach (var i in members) { var group = smoothing[i]; if (group <= SmoothingNone || group > SmoothRangeMax && group < HardRangeMax) continue; sums[group] += normals[i]; groupCounts[group]++; } foreach (var i in members) { var group = smoothing[i]; if (group <= SmoothingNone || group > SmoothRangeMax && group < HardRangeMax) continue; normals[i] = EnsureUnitVector(sums[group] / groupCounts[group]); } }
            for (var i = 0; i < raw.Count; i++) raw[i].Normal = new CanonicalMeshSnapshot.ChannelValue(true, normals[i]);
            if (raw.Any(x => !x.Uvs[0].IsPresent)) { for (var i = 0; i < raw.Count; i++) raw[i].Tangent = new CanonicalMeshSnapshot.ChannelValue(true, Vector4.zero); return Finite(raw); }
            var tan1 = new Vector3[raw.Count]; var tan2 = new Vector3[raw.Count];
            foreach (var face in faces) for (var i = 0; i < face.Stream.Length; i += 3)
            {
                var a = face.Stream[i]; var b = face.Stream[i + 1]; var c = face.Stream[i + 2]; var p1 = raw[a].Position; var p2 = raw[b].Position; var p3 = raw[c].Position; var uv1 = raw[a].Uvs[0].Value; var uv2 = raw[b].Uvs[0].Value; var uv3 = raw[c].Uvs[0].Value; var r = 1f / ((uv2.x - uv1.x) * (uv3.y - uv1.y) - (uv3.x - uv1.x) * (uv2.y - uv1.y)); var s = new Vector3(((uv3.y-uv1.y)*(p2.x-p1.x)-(uv2.y-uv1.y)*(p3.x-p1.x))*r, ((uv3.y-uv1.y)*(p2.y-p1.y)-(uv2.y-uv1.y)*(p3.y-p1.y))*r, ((uv3.y-uv1.y)*(p2.z-p1.z)-(uv2.y-uv1.y)*(p3.z-p1.z))*r); var t = new Vector3(((uv2.x-uv1.x)*(p3.x-p1.x)-(uv3.x-uv1.x)*(p2.x-p1.x))*r, ((uv2.x-uv1.x)*(p3.y-p1.y)-(uv3.x-uv1.x)*(p2.y-p1.y))*r, ((uv2.x-uv1.x)*(p3.z-p1.z)-(uv3.x-uv1.x)*(p2.z-p1.z))*r); foreach (var x in new[] { a, b, c }) { tan1[x] += s; tan2[x] += t; }
            }
            for (var i = 0; i < raw.Count; i++) { var n = normals[i]; var t = EnsureUnitVector(tan1[i]); Vector3.OrthoNormalize(ref n, ref t); raw[i].Tangent = new CanonicalMeshSnapshot.ChannelValue(true, new Vector4(t.x, t.y, t.z, Vector3.Dot(Vector3.Cross(n, t), tan2[i]) < 0f ? -1f : 1f)); }
            return Finite(raw);
        }
        private static bool Finite(IEnumerable<RawPlan> raw) => raw.All(x => Finite(x.Normal.Value) && Finite(x.Tangent.Value));
        private static bool Finite(Vector4 x) => !float.IsNaN(x.x) && !float.IsInfinity(x.x) && !float.IsNaN(x.y) && !float.IsInfinity(x.y) && !float.IsNaN(x.z) && !float.IsInfinity(x.z) && !float.IsNaN(x.w) && !float.IsInfinity(x.w);
        private static Vector3 EnsureUnitVector(Vector3 value) => Mathf.Abs(value.sqrMagnitude) < float.Epsilon ? Vector3.up : value.normalized;
    }
}
#endif
