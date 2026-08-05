#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>Canonical, request-specific proofs for ProBuilder 5.2.4 edge effects.</summary>
    internal static class ProBuilderEdgeCompletionProof
    {
        internal sealed class EdgeSource
        {
            internal CanonicalMeshSnapshot Before;
            internal CanonicalMeshSnapshot.ObjectIdentityClaim Identity;
            internal int RawA, RawB, SharedA, SharedB;
            internal Vector3 LocalA, LocalB, ExpectedOuterA, ExpectedOuterB;
            internal bool BridgeCrossed;
            internal CanonicalMeshSnapshot.FaceOccurrence BridgeAttributeFace;
            internal CanonicalMeshSnapshot.BoundaryIncidence[] Incidences;
        }

        internal static List<EdgeSource> Capture(GameObject go, object ignoredMesh, Array requestedEdges)
        {
            if (requestedEdges == null || requestedEdges.Length == 0) return null;
            // 首遍只解析 edge incidence；未触及的既有 collapsed face 必须保持可比较而不能阻断无关请求。
            var broad = ProBuilderOperationProof.CaptureCanonicalSnapshot(go, Array.Empty<int>());
            if (broad == null) return null;
            var requested = new List<Tuple<int, int>>(); var touched = new HashSet<int>();
            foreach (var edge in requestedEdges)
            {
                var rawA = ProBuilderReflection.GetEdgeVertexA(edge); var rawB = ProBuilderReflection.GetEdgeVertexB(edge);
                var a = broad.RawVertices.FirstOrDefault(value => value.RawIndex == rawA); var b = broad.RawVertices.FirstOrDefault(value => value.RawIndex == rawB);
                if (a == null || b == null || a.SharedHandle == b.SharedHandle) return null;
                var shared = broad.Edges.FirstOrDefault(value => SameEdge(value.SharedMin, value.SharedMax, a.SharedHandle, b.SharedHandle));
                if (shared == null || shared.Incidences.Length == 0) return null;
                requested.Add(Tuple.Create(rawA, rawB));
                foreach (var incidence in shared.Incidences) touched.Add(incidence.FaceOccurrenceIndex);
            }
            var before = ProBuilderOperationProof.CaptureCanonicalSnapshot(go, touched.OrderBy(value => value).ToArray());
            if (before == null || !CanonicalMeshSnapshot.IdentityAndTransformEqual(broad, before)) return null;
            var result = new List<EdgeSource>();
            foreach (var pair in requested)
            {
                var a = before.RawVertices.FirstOrDefault(value => value.RawIndex == pair.Item1); var b = before.RawVertices.FirstOrDefault(value => value.RawIndex == pair.Item2);
                if (a == null || b == null || a.SharedHandle == b.SharedHandle) return null;
                var shared = before.Edges.FirstOrDefault(value => SameEdge(value.SharedMin, value.SharedMax, a.SharedHandle, b.SharedHandle));
                if (shared == null || shared.Incidences.Length == 0
                    || result.Any(value => SameEdge(value.SharedA, value.SharedB, a.SharedHandle, b.SharedHandle))) return null;
                result.Add(new EdgeSource { Before = before, Identity = before.Identity, RawA = pair.Item1, RawB = pair.Item2, SharedA = a.SharedHandle, SharedB = b.SharedHandle, LocalA = a.LocalPosition, LocalB = b.LocalPosition, Incidences = shared.Incidences });
            }
            return result;
        }

        internal static object ResolveFresh(EdgeSource source, string target) => source == null ? null : ProBuilderOperationProof.ResolveFreshCanonicalMesh(target, source.Identity);

        /// <summary>Bridge 的公开终态只接受源码 quad 分支；所有端点和 append 顺序在 effect 前冻结。</summary>
        internal static bool TryPlanBridge(List<EdgeSource> sources, bool allowNonManifold = false)
        {
            if (sources == null || sources.Count != 2 || sources.Any(value => value.Before != sources[0].Before || value.RawA == value.RawB)) return false;
            var before = sources[0].Before;
            var endpoints = new[] { sources[0].SharedA, sources[0].SharedB, sources[1].SharedA, sources[1].SharedB };
            if (endpoints.Distinct().Count() != 4 || sources.Any(value => value.Incidences.Length < 1 || value.Incidences.Length > 2)
                || before.RawVertices.Any(raw => raw.Uvs[1].IsPresent) || !TangentInputsFinite(before)) return false;
            if (!allowNonManifold && sources.Any(value => value.Incidences.Length > 1)) return false;
            if (before.Faces.Any(face => HasBoundary(face, sources[0].SharedA, sources[0].SharedB) && HasBoundary(face, sources[1].SharedA, sources[1].SharedB))) return false;
            // 精确复演 AppendElements.Bridge 的 Projection.PlanarProject / segment-intersection 分支。
            var a = sources[0]; var b = sources[1];
            // AppendElements.Bridge 通过 ValidateEdge 按 face 顺序选择首个同时包含两个 shared endpoint 的面，不要求两点在该面构成边。
            a.BridgeAttributeFace = FirstValidateEdgeFace(before, a) ?? FirstValidateEdgeFace(before, b);
            if (a.BridgeAttributeFace == null) return false;
            var normal = Vector3.Cross(b.LocalA - a.LocalA, a.LocalB - a.LocalA).normalized;
            if (!Finite(normal) || normal.sqrMagnitude == 0f) return false;
            var projected = Projection.PlanarProject(new[] { a.LocalA, a.LocalB, b.LocalA, b.LocalB }, null, normal);
            sources[0].BridgeCrossed = SegmentsIntersect(projected[0], projected[2], projected[1], projected[3]);
            var orderedShared = sources[0].BridgeCrossed ? new[] { a.SharedA, a.SharedB, b.SharedB, b.SharedA } : new[] { a.SharedA, a.SharedB, b.SharedA, b.SharedB };
            var orderedPositions = sources[0].BridgeCrossed ? new[] { a.LocalA, a.LocalB, b.LocalB, b.LocalA } : new[] { a.LocalA, a.LocalB, b.LocalA, b.LocalB };
            if (before.Edges.Any(edge => SameEdge(edge.SharedMin, edge.SharedMax, orderedShared[1], orderedShared[2]) || SameEdge(edge.SharedMin, edge.SharedMax, orderedShared[3], orderedShared[0]))) return false;
            var firstNormal = UnityEngine.ProBuilder.Math.Normal(orderedPositions[2], orderedPositions[1], orderedPositions[0]);
            var secondNormal = UnityEngine.ProBuilder.Math.Normal(orderedPositions[2], orderedPositions[3], orderedPositions[1]);
            return Finite(firstNormal) && Finite(secondNormal) && firstNormal.sqrMagnitude > 0f && secondNormal.sqrMagnitude > 0f
                && UnityEngine.ProBuilder.Math.TriangleArea(orderedPositions[2], orderedPositions[1], orderedPositions[0]) > Mathf.Epsilon
                && UnityEngine.ProBuilder.Math.TriangleArea(orderedPositions[2], orderedPositions[3], orderedPositions[1]) > Mathf.Epsilon
                && QuadTangentInputsFinite(orderedPositions);
        }

        // The wrapper calls Extrude(... enableManifoldExtrude: false), therefore 5.2.4 accepts only boundary edges.
        internal static bool TryPlanExtrude(List<EdgeSource> sources, float distance, bool asGroup = true)
        {
            if (sources == null || sources.Count == 0 || !Finite(distance) || distance == 0f || sources.Any(value => value.Incidences.Length != 1 || value.Before != sources[0].Before)) return false;
            var before = sources[0].Before;
            // ToMesh clears uv2; this proof only accepts the unambiguous no-UV1 state.
            if (before.RawVertices.Any(raw => raw.Uvs[1].IsPresent || !raw.LocalNormal.IsPresent || !Finite(raw.LocalNormal.Value)) || !TangentInputsFinite(before)) return false;
            var all = sources.SelectMany(value => new[] { value.RawA, value.RawB }).ToArray();
            foreach (var source in sources)
            {
                var face = before.Faces.First(value => value.OccurrenceIndex == source.Incidences[0].FaceOccurrenceIndex);
                var a = asGroup ? AverageNormal(before, source.SharedA, all) : FaceNormal(before, face);
                var b = asGroup ? AverageNormal(before, source.SharedB, all) : FaceNormal(before, face);
                if (!Finite(a) || !Finite(b) || a.sqrMagnitude <= Mathf.Epsilon || b.sqrMagnitude <= Mathf.Epsilon) return false;
                source.ExpectedOuterA = source.LocalA + a.normalized * distance;
                source.ExpectedOuterB = source.LocalB + b.normalized * distance;
                if (!Finite(source.ExpectedOuterA) || !Finite(source.ExpectedOuterB)
                    || !Finite(before.LocalToWorldMatrix.MultiplyPoint3x4(source.ExpectedOuterA))
                    || !Finite(before.LocalToWorldMatrix.MultiplyPoint3x4(source.ExpectedOuterB))
                    || !FiniteExtrudeSide(source)) return false;
            }
            return true;
        }

        /// <summary>逐层返回 Bridge exact terminal 的首个不匹配分类，便于 effect 后禁止重放时定位。</summary>
        internal static bool BridgeMatches(List<EdgeSource> sources, GameObject go, object mesh, bool allowNonManifold, out string mismatch)
        {
            mismatch = null;
            var after = Fresh(go, mesh, sources);
            if (after == null || sources == null || sources.Count != 2) { mismatch = "fresh-identity"; return false; }
            var before = sources[0].Before; var a = sources[0]; var b = sources[1];
            if (!IdentityAndTransformEqual(before, after)) { mismatch = "identity-transform"; return false; }
            if (!OldTerminalExactlyPreserved(before, after)) { mismatch = "old-terminal"; return false; }
            if (after.RawVertices.Length != before.RawVertices.Length + 4 || after.Faces.Length != before.Faces.Length + 1 || after.Edges.Length != before.Edges.Length + 2)
            { mismatch = "counts:" + after.RawVertices.Length + ":" + after.Faces.Length + ":" + after.Edges.Length; return false; }
            var n = before.RawVertices.Length;
            var raw = a.BridgeCrossed ? new[] { a.RawA, a.RawB, b.RawB, b.RawA } : new[] { a.RawA, a.RawB, b.RawA, b.RawB };
            var shared = a.BridgeCrossed ? new[] { a.SharedA, a.SharedB, b.SharedB, b.SharedA } : new[] { a.SharedA, a.SharedB, b.SharedA, b.SharedB };
            if (!NewRawExactly(before, after, n, raw, null)) { mismatch = "new-raw:" + NewRawMismatch(before, after, n, raw); return false; }
            if (!SharedAppendExactly(before, after, n, shared)) { mismatch = "shared-partition"; return false; }
            if (!SharedTexturesExactlyPreserved(before, after)) { mismatch = "shared-texture"; return false; }
            var firstFace = a.BridgeAttributeFace;
            if (firstFace == null || !NewFaceExactly(after.Faces[before.Faces.Length], n, firstFace, firstFace.Uv, new[] { 2, 1, 0, 2, 3, 1 }))
            { mismatch = "new-face"; return false; }
            if (!CanonicalMeshSnapshot.RendererMaterialsEqual(before, after)) { mismatch = "renderer-materials"; return false; }
            if (!CanonicalMeshSnapshot.DerivedChannelsMatch(after)) { mismatch = "derived-channels"; return false; }
            return true;
        }

        internal static bool ExtrudeMatches(List<EdgeSource> sources, GameObject go, object mesh, float distance, bool asGroup, int actualCount)
        {
            var after = Fresh(go, mesh, sources); if (after == null || sources == null || actualCount != sources.Count || sources.Any(source => !Finite(source.ExpectedOuterA) || !Finite(source.ExpectedOuterB))) return false;
            var before = sources[0].Before; if (!IdentityAndTransformEqual(before, after) || !OldTerminalExactlyPreserved(before, after) || after.RawVertices.Length != before.RawVertices.Length + 4 * sources.Count || after.Faces.Length != before.Faces.Length + sources.Count || after.RawVertices.Any(raw => raw.Uvs[1].IsPresent) || !SharedTexturesExactlyPreserved(before, after)) return false;
            var outerGroups = new List<int[]>();
            for (var i = 0; i < sources.Count; i++)
            {
                var source = sources[i]; var face = before.Faces.First(value => value.OccurrenceIndex == source.Incidences[0].FaceOccurrenceIndex);
                var offset = before.RawVertices.Length + i * 4;
                if (!NewRawExactly(before, after, offset, new[] { source.RawA, source.RawB }, new[] { source.ExpectedOuterA, source.ExpectedOuterB })) return false;
                if (!NewFaceExactly(after.Faces[before.Faces.Length + i], offset, face, AutoUnwrapSettings.tile, new[] { 2, 1, 0, 2, 3, 1 })) return false;
                outerGroups.Add(new[] { offset + 2, offset + 3 });
            }
            return SharedExtrudeExactly(before, after, sources, outerGroups, asGroup)
                && CanonicalMeshSnapshot.RendererMaterialsEqual(before, after) && CanonicalMeshSnapshot.DerivedChannelsMatch(after);
        }

        // Bridge/extrude terminal proof: append APIs never reindex raw vertices, so every expected index is exact.
        private static bool IdentityAndTransformEqual(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after)
        {
            return CanonicalMeshSnapshot.IdentityAndTransformEqual(before, after);
        }
        private static bool OldTerminalExactlyPreserved(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after)
        {
            if (after.RawVertices.Length < before.RawVertices.Length || after.Faces.Length < before.Faces.Length) return false;
            for (var i = 0; i < before.RawVertices.Length; i++) if (!OldRawPayloadEquals(before.RawVertices[i], after.RawVertices[i])) return false;
            for (var i = 0; i < before.Faces.Length; i++) if (!FaceExact(before.Faces[i], after.Faces[i])) return false;
            return true;
        }
        private static bool NewRawExactly(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after, int offset, int[] sources, Vector3[] outer)
        {
            if (sources == null || (outer == null ? sources.Length != 4 : sources.Length != 2) || offset < before.RawVertices.Length || offset + 3 >= after.RawVertices.Length) return false;
            var positions = outer == null ? new[] { before.RawVertices[sources[0]].LocalPosition, before.RawVertices[sources[1]].LocalPosition, before.RawVertices[sources[2]].LocalPosition, before.RawVertices[sources[3]].LocalPosition }
                : new[] { before.RawVertices[sources[0]].LocalPosition, before.RawVertices[sources[1]].LocalPosition, outer[0], outer[1] };
            var payloadSources = outer == null ? sources : new[] { sources[0], sources[1], sources[0], sources[1] };
            for (var i = 0; i < 4; i++) if (!NewAppendRawPayloadEquals(before.RawVertices[payloadSources[i]], after.RawVertices[offset + i], positions[i], i)) return false;
            return true;
        }
        /// <summary>定位新增 raw payload 首个不匹配 channel，不改变 completion 判定。</summary>
        private static string NewRawMismatch(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after, int offset, int[] sources)
        {
            if (sources == null || sources.Length != 4 || offset < before.RawVertices.Length || offset + 3 >= after.RawVertices.Length) return "range";
            for (var i = 0; i < 4; i++)
            {
                var source = before.RawVertices[sources[i]]; var actual = after.RawVertices[offset + i];
                if (!CanonicalMeshSnapshot.ExactEquals(source.LocalPosition, actual.LocalPosition)) return "position-" + i;
                if (!CanonicalMeshSnapshot.ChannelValueEquals(source.Color, actual.Color)) return "color-" + i;
                for (var channel = 0; channel < 4; channel++)
                {
                    if (channel == 1) { if (actual.Uvs[channel].IsPresent) return "uv1-" + i; continue; }
                    var expected = channel == 0 ? SideUv(i) : Vector4.zero;
                    if (!actual.Uvs[channel].IsPresent || !CanonicalMeshSnapshot.ExactEquals(expected, actual.Uvs[channel].Value)) return "uv" + channel + "-" + i;
                }
            }
            return "unknown";
        }
        private static bool OldRawPayloadEquals(CanonicalMeshSnapshot.RawVertex expected, CanonicalMeshSnapshot.RawVertex actual)
        {
            if (expected == null || actual == null || !CanonicalMeshSnapshot.ExactEquals(expected.LocalPosition, actual.LocalPosition) || !CanonicalMeshSnapshot.ChannelValueEquals(expected.Color, actual.Color)) return false;
            var oldUvs = expected.Uvs; var newUvs = actual.Uvs;
            if (oldUvs[1].IsPresent || newUvs[1].IsPresent) return false;
            for (var channel = 0; channel < 4; channel++)
            {
                if (channel == 1) continue;
                if (oldUvs[channel].IsPresent ? !CanonicalMeshSnapshot.ChannelValueEquals(oldUvs[channel], newUvs[channel]) : (!newUvs[channel].IsPresent || !CanonicalMeshSnapshot.ExactEquals(newUvs[channel].Value, Vector4.zero))) return false;
            }
            return true;
        }
        private static bool NewAppendRawPayloadEquals(CanonicalMeshSnapshot.RawVertex source, CanonicalMeshSnapshot.RawVertex actual, Vector3 position, int quadRawIndex)
        {
            if (source == null || actual == null || !CanonicalMeshSnapshot.ExactEquals(actual.LocalPosition, position) || !CanonicalMeshSnapshot.ChannelValueEquals(source.Color, actual.Color)) return false;
            var actualUvs = actual.Uvs;
            return actualUvs[0].IsPresent && CanonicalMeshSnapshot.ExactEquals(actualUvs[0].Value, SideUv(quadRawIndex))
                && !actualUvs[1].IsPresent && new[] { 2, 3 }.All(channel => actualUvs[channel].IsPresent && CanonicalMeshSnapshot.ExactEquals(actualUvs[channel].Value, Vector4.zero));
        }
        private static Vector4 SideUv(int raw) => raw == 0 ? new Vector4(0f, 0f, 0f, 0f) : raw == 1 ? new Vector4(1f, 0f, 0f, 0f) : raw == 2 ? new Vector4(0f, 1f, 0f, 0f) : new Vector4(1f, 1f, 0f, 0f);
        private static bool FaceExact(CanonicalMeshSnapshot.FaceOccurrence left, CanonicalMeshSnapshot.FaceOccurrence right)
        {
            return left.SubmeshIndex == right.SubmeshIndex && left.SmoothingGroup == right.SmoothingGroup && left.TextureGroup == right.TextureGroup && left.ElementGroup == right.ElementGroup && left.ManualUv == right.ManualUv
                && CanonicalMeshSnapshot.MaterialValueEquals(left.Material, right.Material) && CanonicalMeshSnapshot.UvValueEquals(left.Uv, right.Uv) && left.DirectedRawTriangleStream.SequenceEqual(right.DirectedRawTriangleStream);
        }
        private static bool NewFaceExactly(CanonicalMeshSnapshot.FaceOccurrence actual, int offset, CanonicalMeshSnapshot.FaceOccurrence source, AutoUnwrapSettings expectedUv, int[] relativeStream)
        {
            return actual != null && source != null && actual.SubmeshIndex == source.SubmeshIndex && actual.SmoothingGroup == 0 && actual.TextureGroup == -1 && actual.ElementGroup == -1 && actual.ManualUv
                && CanonicalMeshSnapshot.MaterialValueEquals(actual.Material, CanonicalMeshSnapshot.WithSerializedFaceMaterial(source.Material, null)) && CanonicalMeshSnapshot.UvValueEquals(actual.Uv, expectedUv)
                && actual.DirectedRawTriangleStream.SequenceEqual(relativeStream.Select(value => offset + value));
        }
        private static bool SharedAppendExactly(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after, int offset, int[] shared)
        {
            var expected = before.SharedVertices.Select(value => value.MemberRawIndexes.ToList()).ToList();
            for (var i = 0; i < shared.Length; i++) expected[shared[i]].Add(offset + i);
            return PartitionEquals(expected, after.SharedVertices.Select(value => value.MemberRawIndexes));
        }
        private static bool SharedExtrudeExactly(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after, List<EdgeSource> sources, List<int[]> outer, bool asGroup)
        {
            var expected = before.SharedVertices.Select(value => value.MemberRawIndexes.ToList()).ToList();
            var outerBySource = new List<Tuple<int, int>>();
            for (var i = 0; i < sources.Count; i++) { var offset = before.RawVertices.Length + i * 4; expected[sources[i].SharedA].Add(offset); expected[sources[i].SharedB].Add(offset + 1); outerBySource.Add(Tuple.Create(sources[i].SharedA, offset + 2)); outerBySource.Add(Tuple.Create(sources[i].SharedB, offset + 3)); }
            foreach (var group in outerBySource.GroupBy(value => asGroup ? value.Item1 : -value.Item2)) expected.Add(group.Select(value => value.Item2).ToList());
            return PartitionEquals(expected, after.SharedVertices.Select(value => value.MemberRawIndexes));
        }
        private static bool SharedTexturesExactlyPreserved(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after)
        {
            return before.HasSharedTextures == after.HasSharedTextures && PartitionEquals(before.SharedTextures.Select(value => value.MemberRawIndexes), after.SharedTextures.Select(value => value.MemberRawIndexes))
                && after.SharedTextures.All(group => group.MemberRawIndexes.All(index => index < before.RawVertices.Length));
        }
        private static bool PartitionEquals(IEnumerable<IEnumerable<int>> expected, IEnumerable<IEnumerable<int>> actual)
        {
            var left = expected.Select(value => value.OrderBy(index => index).ToArray()).ToList(); var right = actual.Select(value => value.OrderBy(index => index).ToArray()).ToList();
            if (left.Count != right.Count) return false;
            foreach (var value in left) { var index = right.FindIndex(candidate => value.SequenceEqual(candidate)); if (index < 0) return false; right.RemoveAt(index); }
            return right.Count == 0;
        }
        private static Vector3 AverageNormal(CanonicalMeshSnapshot snapshot, int shared, int[] all)
        {
            var group = snapshot.SharedVertices.FirstOrDefault(value => value.Handle == shared); if (group == null) return new Vector3(float.NaN, 0, 0);
            var sum = Vector3.zero; var count = 0;
            foreach (var index in all) if (group.MemberRawIndexes.Contains(index)) { sum += (Vector3)snapshot.RawVertices[index].LocalNormal.Value; count++; }
            return count == 0 ? new Vector3(float.NaN, 0, 0) : sum / count;
        }
        /// <summary>5.2.4 append side 的两个预测 triangle normal 必须在 effect 前保持有限且非零。</summary>
        private static bool FiniteExtrudeSide(EdgeSource source)
        {
            if (source == null) return false;
            var first = UnityEngine.ProBuilder.Math.Normal(source.ExpectedOuterA, source.LocalB, source.LocalA);
            var second = UnityEngine.ProBuilder.Math.Normal(source.ExpectedOuterA, source.ExpectedOuterB, source.LocalB);
            return Finite(first) && Finite(second) && first.sqrMagnitude > 0f && second.sqrMagnitude > 0f
                && QuadTangentInputsFinite(new[] { source.LocalA, source.LocalB, source.ExpectedOuterA, source.ExpectedOuterB });
        }
        private static Vector3 FaceNormal(CanonicalMeshSnapshot snapshot, CanonicalMeshSnapshot.FaceOccurrence face)
        {
            var stream = face.DirectedRawTriangleStream; if (stream.Length < 3) return new Vector3(float.NaN, 0, 0);
            var positions = snapshot.RawVertices.Select(value => value.LocalPosition).ToArray();
            var normal = UnityEngine.ProBuilder.Math.Normal(positions[stream[0]], positions[stream[1]], positions[stream[2]]);
            if (stream.Length > 6)
            {
                var best = Projection.FindBestPlane(positions, stream.Distinct().ToArray()).normal;
                normal = Vector3.Dot(normal, best) < 0f ? -best : best;
            }
            return normal;
        }
        /// <summary>tangent refresh 会遍历全部旧 triangle；每个 UV0 Jacobian 都必须在 effect 前存在、有限且非零。</summary>
        private static bool TangentInputsFinite(CanonicalMeshSnapshot snapshot) => CanonicalMeshSnapshot.RefreshArithmeticFinite(snapshot);
        private static bool QuadTangentInputsFinite(IList<Vector3> positions)
        {
            return positions != null && positions.Count == 4 && CanonicalMeshSnapshot.RefreshArithmeticFinite(
                positions,
                new[] { new CanonicalMeshSnapshot.ChannelValue(true, new Vector2(0f, 0f)), new CanonicalMeshSnapshot.ChannelValue(true, new Vector2(1f, 0f)), new CanonicalMeshSnapshot.ChannelValue(true, new Vector2(0f, 1f)), new CanonicalMeshSnapshot.ChannelValue(true, new Vector2(1f, 1f)) },
                new[] { new[] { 2, 1, 0, 2, 3, 1 } }, new[] { 0 });
        }

        /// <summary>逐项复制 5.2.4 Math.GetLineSegmentIntersect 的 closed-segment 判定。</summary>
        private static bool SegmentsIntersect(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            var s1 = p1 - p0; var s2 = p3 - p2;
            var denominator = -s2.x * s1.y + s1.x * s2.y;
            var s = (-s1.y * (p0.x - p2.x) + s1.x * (p0.y - p2.y)) / denominator;
            var t = (s2.x * (p0.y - p2.y) - s2.y * (p0.x - p2.x)) / denominator;
            return s >= 0f && s <= 1f && t >= 0f && t <= 1f;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);

        // Canonical proofs deliberately reject old boundary containment and world-space approximation predicates; raw/shared numbers only map the request inside its pre-effect snapshot.

        private static CanonicalMeshSnapshot Fresh(GameObject ignoredGo, object mesh, List<EdgeSource> sources)
        {
            var freshMesh = mesh as ProBuilderMesh;
            if (freshMesh == null || freshMesh.gameObject == null || sources == null || sources.Count == 0) return null;
            var touched = sources.SelectMany(source => source.Incidences).Select(incidence => incidence.FaceOccurrenceIndex).Distinct().OrderBy(value => value).ToArray();
            var after = ProBuilderOperationProof.CaptureCanonicalSnapshot(freshMesh.gameObject, touched);
            return after != null && IdentityAndTransformEqual(sources[0].Before, after) ? after : null;
        }
        private static bool SameEdge(int a, int b, int c, int d) => (a == c && b == d) || (a == d && b == c);
        private static bool HasBoundary(CanonicalMeshSnapshot.FaceOccurrence face, int a, int b) => face.Boundary.Any(edge => SameEdge(edge.SharedA, edge.SharedB, a, b));
        private static CanonicalMeshSnapshot.FaceOccurrence FirstValidateEdgeFace(CanonicalMeshSnapshot snapshot, EdgeSource source)
            => snapshot?.Faces.FirstOrDefault(face => face.DirectedSharedTriangleStream.Contains(source.SharedA) && face.DirectedSharedTriangleStream.Contains(source.SharedB));
    }
}
#endif
