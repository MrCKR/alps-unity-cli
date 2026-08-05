#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>为 ProBuilder 5.2.4 的 face/edge vertex insertion 冻结请求并验证完整 fresh 拓扑。</summary>
    internal static class ProBuilderInsertCompletionProof
    {
        internal sealed class Request
        {
            internal CanonicalMeshSnapshot Before;
            internal bool EdgeMode;
            internal int SourceFaceOccurrence;
            internal int[] AffectedOccurrences;
            internal int SharedA;
            internal int SharedB;
            internal int RawA;
            internal int RawB;
            internal Vector3 LocalA;
            internal Vector3 LocalB;
            internal Vector3 RequestedPoint;
            internal PointPayloadClaim ExpectedPoint;
            internal Dictionary<int, Vector3> SourceNormals;
        }

        internal sealed class PointPayloadClaim
        {
            internal readonly Vector3 Position;
            internal readonly CanonicalMeshSnapshot.ChannelValue Color;
            private readonly CanonicalMeshSnapshot.ChannelValue[] uvs;
            internal CanonicalMeshSnapshot.ChannelValue[] Uvs { get { return (CanonicalMeshSnapshot.ChannelValue[])uvs.Clone(); } }
            internal PointPayloadClaim(Vector3 position, CanonicalMeshSnapshot.ChannelValue color, CanonicalMeshSnapshot.ChannelValue[] values)
            { Position = position; Color = color; uvs = (CanonicalMeshSnapshot.ChannelValue[])values.Clone(); }
        }

        private struct Point2
        {
            internal float X;
            internal float Y;
            internal Point2(float x, float y) { X = x; Y = y; }
        }

        /// <summary>effect 前冻结 affected face 闭集；存在 UV sharing 或值级身份歧义时拒绝调用有损的 5.2.4 API。</summary>
        internal static Request Capture(GameObject go, int[] edge, int face, out string failureReason)
        {
            failureReason = null;
            if (go == null || (edge == null) != (face >= 0)) { failureReason = "exactly one insertion mode is required"; return null; }
            var initial = CanonicalMeshSnapshot.Capture(go, face >= 0 ? new[] { face } : Array.Empty<int>());
            if (initial == null || !initial.HasSharedTextures || initial.RawVertices.Any(raw => raw.Uvs[1].IsPresent)
                || !CanonicalMeshSnapshot.RefreshArithmeticFinite(initial) || !ProBuilderRefreshUvProof.RefreshUvsAreTerminal(initial))
            { failureReason = "canonical mesh state must have initialized sharedTextures, absent UV1, auto-UV terminal data, and fully finite bounded 5.2.4 refresh arithmetic"; return null; }
            if (!SharedPositionsAreUnique(initial)) { failureReason = "coincident shared handles make cross-snapshot identity ambiguous"; return null; }

            if (face >= 0)
            {
                if (HasSharedTextureGroups(initial)) { failureReason = "face insertion cannot preserve existing shared texture groups in ProBuilder 5.2.4"; return null; }
                var source = initial.Faces.SingleOrDefault(value => value.OccurrenceIndex == face);
                if (source == null || !source.ManualUv) { failureReason = "requested face occurrence must use manual UVs"; return null; }
                if (!FacesHaveExclusiveRawOwnership(initial, new[] { face })) { failureReason = "requested face raw vertices must not be referenced by another face"; return null; }
                if (!TryPlanarFace(initial, source, out var normal) || !IsCompleteTriangulation(initial, source, source, normal))
                { failureReason = "requested face is not a complete planar simple triangulation"; return null; }
                return new Request
                {
                    Before = initial,
                    EdgeMode = false,
                    SourceFaceOccurrence = face,
                    AffectedOccurrences = new[] { face },
                    SourceNormals = new Dictionary<int, Vector3> { { face, normal } }
                };
            }

            if (edge.Length != 2 || edge[0] == edge[1]) { failureReason = "edge must contain two different raw vertex indices"; return null; }
            var rawA = initial.RawVertices.SingleOrDefault(value => value.RawIndex == edge[0]);
            var rawB = initial.RawVertices.SingleOrDefault(value => value.RawIndex == edge[1]);
            if (rawA == null || rawB == null || rawA.SharedHandle == rawB.SharedHandle)
            { failureReason = "edge endpoints do not resolve to two distinct shared vertices"; return null; }
            var sharedEdge = initial.Edges.SingleOrDefault(value => SameEdge(value.SharedMin, value.SharedMax, rawA.SharedHandle, rawB.SharedHandle));
            if (sharedEdge == null || sharedEdge.Incidences.Length == 0) { failureReason = "requested edge has no readable face incidence"; return null; }
            var affected = sharedEdge.Incidences.Select(value => value.FaceOccurrenceIndex).ToArray();
            if (affected.Distinct().Count() != affected.Length) { failureReason = "requested edge has duplicate face incidence"; return null; }

            var before = CanonicalMeshSnapshot.Capture(go, affected);
            if (before == null) { failureReason = "affected edge faces are degenerate, non-simple, or unreadable"; return null; }
            if (!SharedPositionsAreUnique(before)) { failureReason = "affected edge faces have ambiguous coincident shared handles"; return null; }
            var capturedEdge = before.Edges.SingleOrDefault(value => SameEdge(value.SharedMin, value.SharedMax, rawA.SharedHandle, rawB.SharedHandle));
            if (capturedEdge == null || capturedEdge.Incidences.Length != affected.Length)
            { failureReason = "edge incidence changed during preflight"; return null; }
            if (!AffectedTextureCoverageIsEmpty(before, affected))
            { failureReason = "an affected edge face has shared texture mappings whose 5.2.4 boundary reorder cannot be proven"; return null; }
            if (!FacesHaveExclusiveRawOwnership(before, affected))
            { failureReason = "affected edge face raw vertices must be exclusively owned by one affected face"; return null; }
            var normals = new Dictionary<int, Vector3>();
            foreach (var occurrence in affected)
            {
                var source = before.Faces.SingleOrDefault(value => value.OccurrenceIndex == occurrence);
                if (source == null || !source.ManualUv || source.DirectedRawTriangleStream.Length != 3 || source.Boundary.Length != 3 || !TryPlanarFace(before, source, out var normal)
                    || !IsCompleteTriangulation(before, source, source, normal))
                { failureReason = "an affected edge face is not a complete planar simple triangulation"; return null; }
                normals.Add(occurrence, normal);
            }
            return new Request
            {
                Before = before,
                EdgeMode = true,
                SourceFaceOccurrence = -1,
                AffectedOccurrences = affected,
                SharedA = rawA.SharedHandle,
                SharedB = rawB.SharedHandle,
                RawA = rawA.RawIndex,
                RawB = rawB.RawIndex,
                LocalA = rawA.LocalPosition,
                LocalB = rawB.LocalPosition,
                SourceNormals = normals
            };
        }

        /// <summary>5.2.4 edge insertion 会重置 face metadata；在 ToMesh/Refresh 前按 effect 前 occurrence 原位恢复。</summary>
        internal static bool RestoreEdgeFaceAttributes(object value, Request request)
        {
            var mesh = value as ProBuilderMesh;
            if (mesh == null || request == null || !request.EdgeMode || request.Before == null || request.AffectedOccurrences == null) return false;
            var faces = mesh.faces;
            var elementGroup = typeof(Face).GetField("elementGroup", BindingFlags.Instance | BindingFlags.NonPublic);
            if (faces == null || elementGroup == null) return false;
            try
            {
                foreach (var occurrence in request.AffectedOccurrences)
                {
                    if (occurrence < 0 || occurrence >= faces.Count) return false;
                    var source = request.Before.Faces.SingleOrDefault(item => item.OccurrenceIndex == occurrence);
                    var face = faces[occurrence];
                    if (source == null || face == null) return false;
                    face.submeshIndex = source.SubmeshIndex;
                    face.smoothingGroup = source.SmoothingGroup;
                    face.manualUV = source.ManualUv;
                    face.textureGroup = source.TextureGroup;
                    face.uv = new AutoUnwrapSettings(source.Uv);
                    elementGroup.SetValue(face, source.ElementGroup);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>face mode 要求同平面且严格位于 polygon 内；edge mode 要求严格位于请求 segment 内部。</summary>
        internal static bool PointIsStrictlyInterior(Request request, Vector3 point)
        {
            if (request == null || request.Before == null || !Finite(point)
                || request.Before.SharedVertices.Any(value => CanonicalMeshSnapshot.ExactEquals(value.CanonicalPosition, point))) return false;
            if (request.EdgeMode) return OnSegmentInterior(point, request.LocalA, request.LocalB);
            var source = request.Before.Faces.SingleOrDefault(value => value.OccurrenceIndex == request.SourceFaceOccurrence);
            return source != null && request.SourceNormals != null && request.SourceNormals.TryGetValue(source.OccurrenceIndex, out var normal)
                && PointInFaceInterior(point, request.Before, source, normal);
        }

        /// <summary>按 5.2.4 新 raw payload 与两个 insertion 分支冻结真实 point payload，并预演每个新 triangle 的全 Refresh 算术。</summary>
        internal static bool TerminalCanRefresh(Request request, Vector3 point)
        {
            if (request == null || request.Before == null || !Finite(point) || !CanonicalMeshSnapshot.RefreshArithmeticFinite(request.Before)) return false;
            request.RequestedPoint = point;
            request.ExpectedPoint = null;
            if (!request.EdgeMode)
            {
                var source = request.Before.Faces.SingleOrDefault(face => face.OccurrenceIndex == request.SourceFaceOccurrence);
                var expectedPoint = FreezePointPayload(request.Before, point, null);
                if (source == null || expectedPoint == null || !source.Boundary.All(edge => TriangleRefreshFinite(
                    request.Before.RawVertices[edge.RawA].LocalPosition,
                    request.Before.RawVertices[edge.RawB].LocalPosition,
                    expectedPoint.Position,
                    request.Before.RawVertices[edge.RawA].Uvs[0],
                    request.Before.RawVertices[edge.RawB].Uvs[0],
                    expectedPoint.Uvs[0],
                    source.SmoothingGroup))) return false;
                request.ExpectedPoint = expectedPoint;
                return true;
            }
            var vertices = CanonicalMeshSnapshot.BuildManagedVertices(request.Before);
            if (vertices == null || request.RawA < 0 || request.RawB < 0 || request.RawA >= vertices.Count || request.RawB >= vertices.Count) return false;
            var a = point - vertices[request.RawA].position; var b = vertices[request.RawB].position - vertices[request.RawA].position;
            var weight = Vector3.Magnitude(a) * Mathf.Cos(Vector3.Angle(b, a) * Mathf.Deg2Rad) / Vector3.Magnitude(b);
            var mixed = Vertex.Mix(vertices[request.RawA], vertices[request.RawB], weight);
            var expected = FreezePointPayload(request.Before, point, mixed);
            if (expected == null) return false;
            foreach (var occurrence in request.AffectedOccurrences)
            {
                var source = request.Before.Faces.SingleOrDefault(face => face.OccurrenceIndex == occurrence);
                var edge = source?.Boundary.SingleOrDefault(value => SameEdge(value.SharedA, value.SharedB, request.SharedA, request.SharedB));
                if (source == null || edge == null || source.DirectedRawTriangleStream.Length != 3) return false;
                var other = source.DirectedRawTriangleStream.SingleOrDefault(raw => raw != edge.RawA && raw != edge.RawB);
                if (other == edge.RawA || other == edge.RawB) return false;
                var first = TriangleRefreshFinite(request.Before.RawVertices[edge.RawA].LocalPosition, expected.Position, request.Before.RawVertices[other].LocalPosition,
                    request.Before.RawVertices[edge.RawA].Uvs[0], expected.Uvs[0], request.Before.RawVertices[other].Uvs[0], source.SmoothingGroup);
                var second = TriangleRefreshFinite(expected.Position, request.Before.RawVertices[edge.RawB].LocalPosition, request.Before.RawVertices[other].LocalPosition,
                    expected.Uvs[0], request.Before.RawVertices[edge.RawB].Uvs[0], request.Before.RawVertices[other].Uvs[0], source.SmoothingGroup);
                if (!first || !second) return false;
            }
            request.ExpectedPoint = expected;
            return true;
        }

        /// <summary>SetVertices 使用首个 raw 的全局 channel profile；Mix 的隐藏字段值仍会进入最终数组，UV1 则由 ToMesh 清除。</summary>
        private static PointPayloadClaim FreezePointPayload(CanonicalMeshSnapshot before, Vector3 requestedPoint, Vertex mixed)
        {
            if (before == null || before.RawVertices.Length == 0 || before.RawVertices.Any(raw => raw.Uvs[1].IsPresent)) return null;
            var profile = before.RawVertices[0]; var profileUvs = profile.Uvs;
            var position = mixed == null ? requestedPoint : mixed.position;
            var colorValue = mixed == null ? Vector4.zero : new Vector4(mixed.color.r, mixed.color.g, mixed.color.b, mixed.color.a);
            var uvs = new[]
            {
                new CanonicalMeshSnapshot.ChannelValue(profileUvs[0].IsPresent, mixed == null ? Vector4.zero : (Vector4)mixed.uv0),
                new CanonicalMeshSnapshot.ChannelValue(false, Vector4.zero),
                new CanonicalMeshSnapshot.ChannelValue(profileUvs[2].IsPresent, mixed == null ? Vector4.zero : mixed.uv3),
                new CanonicalMeshSnapshot.ChannelValue(profileUvs[3].IsPresent, mixed == null ? Vector4.zero : mixed.uv4)
            };
            var color = new CanonicalMeshSnapshot.ChannelValue(profile.Color.IsPresent, colorValue);
            if (!Finite(position) || before.SharedVertices.Any(shared => CanonicalMeshSnapshot.ExactEquals(shared.CanonicalPosition, position))
                || !Finite(color) || uvs.Any(value => !Finite(value))) return null;
            return new PointPayloadClaim(position, color, uvs);
        }

        private static bool TriangleRefreshFinite(Vector3 a, Vector3 b, Vector3 c, CanonicalMeshSnapshot.ChannelValue uvA, CanonicalMeshSnapshot.ChannelValue uvB, CanonicalMeshSnapshot.ChannelValue uvC, int smoothing)
            => CanonicalMeshSnapshot.RefreshArithmeticFinite(new[] { a, b, c }, new[] { uvA, uvB, uvC }, new[] { new[] { 0, 1, 2 } }, new[] { smoothing });

        /// <summary>fresh result 必须完整消费 affected face，并证明新 shared point、raw remap、winding、属性和未选 face。</summary>
        internal static bool Matches(Request request, CanonicalMeshSnapshot after, Vector3 point, out string failureReason)
        {
            failureReason = null;
            if (request == null || request.Before == null || request.ExpectedPoint == null || after == null || !Finite(point)
                || !CanonicalMeshSnapshot.ExactEquals(request.RequestedPoint, point)) { failureReason = "request, frozen point payload, or fresh canonical snapshot is unavailable"; return false; }
            var terminalPoint = request.ExpectedPoint.Position;
            if (!Stable(request.Before, after)) { failureReason = "fresh object, scene, component, or transform identity changed"; return false; }
            if (!SharedPositionsAreUnique(after)) { failureReason = "fresh shared positions are ambiguous"; return false; }
            if (!SharedLayoutMatches(request, after, terminalPoint)) { failureReason = "shared/raw member budget or point allocation differs from the 5.2.4 result"; return false; }
            if (!SharedTextureLayoutMatches(request, after, terminalPoint)) { failureReason = "shared texture groups differ from the 5.2.4 result: " + SharedTextureDiagnostic(request, after, terminalPoint); return false; }

            var remaining = new List<CanonicalMeshSnapshot.FaceOccurrence>(after.Faces);
            var affected = new HashSet<int>(request.AffectedOccurrences);
            foreach (var source in request.Before.Faces.Where(value => !affected.Contains(value.OccurrenceIndex)))
            {
                var matches = remaining.Where(candidate => PreservedFaceEquals(request.Before, source, after, candidate)).ToArray();
                if (matches.Length != 1) { failureReason = "an unselected face did not have exactly one preserved fresh match"; return false; }
                remaining.Remove(matches[0]);
            }

            if (request.EdgeMode)
            {
                var sources = request.AffectedOccurrences.Select(occurrence => request.Before.Faces.Single(value => value.OccurrenceIndex == occurrence)).ToArray();
                if (remaining.Count != sources.Length) { failureReason = "fresh edge face count differs from the affected face count"; return false; }
                if (!CanonicalMeshSnapshot.ExactMultisetEquals(sources, remaining, (source, candidate) => EdgeReplacementMatches(request, source, after, candidate, terminalPoint)))
                { failureReason = "affected edge faces do not form an exact boundary-replacement bijection"; return false; }
                if (HasValueEdge(after, request.LocalA, request.LocalB)) { failureReason = "the original edge still exists after insertion"; return false; }
                if (!TerminalChannelsAndMaterials(request.Before, after)) { failureReason = "renderer materials or derived channels differ from the 5.2.4 terminal"; return false; }
                return true;
            }

            var face = request.Before.Faces.Single(value => value.OccurrenceIndex == request.SourceFaceOccurrence);
            if (remaining.Count != face.Boundary.Length) { failureReason = "fresh face child count differs from the source boundary count"; return false; }
            if (!ExactBijection(face.Boundary, remaining, (edge, child) => FaceChildMatches(request, face, edge, after, child, terminalPoint)))
            {
                var candidateCounts = face.Boundary.Select(edge => remaining.Count(child => FaceChildMatches(request, face, edge, after, child, terminalPoint))).ToArray();
                failureReason = "face children do not form an exact boundary-edge bijection; candidate counts=" + string.Join(",", candidateCounts);
                return false;
            }
            if (!TerminalChannelsAndMaterials(request.Before, after)) { failureReason = "renderer materials or derived channels differ from the 5.2.4 terminal"; return false; }
            return true;
        }

        private static bool TerminalChannelsAndMaterials(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after)
            => CanonicalMeshSnapshot.RendererMaterialsEqual(before, after) && CanonicalMeshSnapshot.DerivedChannelsMatch(after);

        /// <summary>旧 shared groups 必须按值保留；face fan 只允许每个 boundary endpoint 多一个副本，并新增唯一 point group。</summary>
        private static bool SharedLayoutMatches(Request request, CanonicalMeshSnapshot after, Vector3 point)
        {
            var before = request.Before;
            var source = request.EdgeMode ? null : before.Faces.Single(value => value.OccurrenceIndex == request.SourceFaceOccurrence);
            var duplicatedHandles = source == null ? new HashSet<int>() : new HashSet<int>(source.Boundary.Select(value => value.SharedA));
            var expectedAdditionalRaw = request.EdgeMode ? request.AffectedOccurrences.Length : source.Boundary.Length * 2;
            if (after.SharedVertices.Length != before.SharedVertices.Length + 1 || after.RawVertices.Length != before.RawVertices.Length + expectedAdditionalRaw) return false;

            foreach (var group in before.SharedVertices)
            {
                var candidates = after.SharedVertices.Where(value => CanonicalMeshSnapshot.ExactEquals(value.CanonicalPosition, group.CanonicalPosition)).ToArray();
                if (candidates.Length != 1) return false;
                var expected = group.MemberRawIndexes.Select(index => before.RawVertices[index]).ToList();
                if (duplicatedHandles.Contains(group.Handle))
                {
                    var duplicateRaw = BoundaryRawForHandle(source, group.Handle);
                    if (duplicateRaw < 0) return false;
                    expected.Add(before.RawVertices[duplicateRaw]);
                }
                var actual = candidates[0].MemberRawIndexes.Select(index => after.RawVertices[index]).ToArray();
                if (!CanonicalMeshSnapshot.ExactMultisetEquals(expected, actual, RawValueEquals)) return false;
            }

            var pointGroups = after.SharedVertices.Where(value => CanonicalMeshSnapshot.ExactEquals(value.CanonicalPosition, point)).ToArray();
            var expectedPointMembers = request.EdgeMode ? request.AffectedOccurrences.Length : source.Boundary.Length;
            if (pointGroups.Length != 1 || pointGroups[0].MemberRawIndexes.Length != expectedPointMembers) return false;
            var pointMembers = pointGroups[0].MemberRawIndexes.Select(index => after.RawVertices[index]).ToArray();
            return pointMembers.Length > 0 && pointMembers.All(value => PointMemberMatches(request, value));
        }

        /// <summary>edge insertion 重建每个 incident face；边界只允许 A-B 被 A-P、P-B 替换。</summary>
        private static bool EdgeReplacementMatches(Request request, CanonicalMeshSnapshot.FaceOccurrence source, CanonicalMeshSnapshot after, CanonicalMeshSnapshot.FaceOccurrence candidate, Vector3 point)
        {
            var rebuiltMaterial = CanonicalMeshSnapshot.WithSerializedFaceMaterial(source.Material, null);
            if (rebuiltMaterial == null || !SameFaceAttributes(source, candidate) || !CanonicalMeshSnapshot.MaterialValueEquals(rebuiltMaterial, candidate.Material)
                || !CanonicalMeshSnapshot.UvValueEquals(source.Uv, candidate.Uv)) return false;
            var expected = new List<Vector3[]>();
            var replaced = 0;
            foreach (var edge in source.Boundary)
            {
                var a = request.Before.RawVertices[edge.RawA].LocalPosition;
                var b = request.Before.RawVertices[edge.RawB].LocalPosition;
                if (SameEdge(edge.SharedA, edge.SharedB, request.SharedA, request.SharedB))
                {
                    expected.Add(new[] { a, point });
                    expected.Add(new[] { point, b });
                    replaced++;
                }
                else expected.Add(new[] { a, b });
            }
            return replaced == 1 && request.SourceNormals != null && request.SourceNormals.TryGetValue(source.OccurrenceIndex, out var normal)
                && BoundaryEquals(after, candidate, expected)
                && IsCompleteTriangulation(after, candidate, source, normal, request.Before)
                && !HasBoundaryValue(after, candidate, request.LocalA, request.LocalB);
        }

        /// <summary>face insertion 必须为每条原 boundary edge 分配一个且仅一个同向 triangle child。</summary>
        private static bool FaceChildMatches(Request request, CanonicalMeshSnapshot.FaceOccurrence source, CanonicalMeshSnapshot.BoundaryReference sourceEdge, CanonicalMeshSnapshot after, CanonicalMeshSnapshot.FaceOccurrence child, Vector3 point)
        {
            var a = request.Before.RawVertices[sourceEdge.RawA].LocalPosition;
            var b = request.Before.RawVertices[sourceEdge.RawB].LocalPosition;
            if (child.SubmeshIndex != source.SubmeshIndex || child.SmoothingGroup != source.SmoothingGroup || child.ManualUv != source.ManualUv
                || child.TextureGroup != source.TextureGroup || child.ElementGroup != -1 || child.Boundary.Length != 3 || child.DirectedSharedTriangleStream.Length != 3
                || !CanonicalMeshSnapshot.MaterialValueEquals(CanonicalMeshSnapshot.WithSerializedFaceMaterial(source.Material, null), child.Material) || !CanonicalMeshSnapshot.UvValueEquals(source.Uv, child.Uv)) return false;
            var expected = new List<Vector3[]> { new[] { a, b }, new[] { b, point }, new[] { point, a } };
            return request.SourceNormals != null && request.SourceNormals.TryGetValue(source.OccurrenceIndex, out var normal)
                && BoundaryEquals(after, child, expected)
                && UsesOnlyValues(after, child, new[] { a, b, point })
                && IsCompleteTriangulation(after, child, source, normal, request.Before);
        }

        /// <summary>未选 face 用局部 raw payload 比较，避免 affected face 合法增加 shared member 时产生假阴性。</summary>
        private static bool PreservedFaceEquals(CanonicalMeshSnapshot before, CanonicalMeshSnapshot.FaceOccurrence source, CanonicalMeshSnapshot after, CanonicalMeshSnapshot.FaceOccurrence candidate)
        {
            return SameFaceAttributes(source, candidate) && CanonicalMeshSnapshot.MaterialValueEquals(source.Material, candidate.Material)
                && CanonicalMeshSnapshot.UvValueEquals(source.Uv, candidate.Uv)
                && CanonicalMeshSnapshot.ExactMultisetEquals(Split(source.DirectedRawTriangleStream), Split(candidate.DirectedRawTriangleStream),
                    (left, right) => RawTriangleCyclicEquals(before, left, after, right))
                && CanonicalMeshSnapshot.ExactMultisetEquals(source.Boundary, candidate.Boundary,
                    (left, right) => RawValueEquals(before.RawVertices[left.RawA], after.RawVertices[right.RawA])
                        && RawValueEquals(before.RawVertices[left.RawB], after.RawVertices[right.RawB]));
        }

        /// <summary>验证候选 face 是 source polygon 内的完整同向 triangulation，而非只看数量或面积。</summary>
        private static bool IsCompleteTriangulation(CanonicalMeshSnapshot snapshot, CanonicalMeshSnapshot.FaceOccurrence candidate, CanonicalMeshSnapshot.FaceOccurrence source, Vector3 sourceNormal, CanonicalMeshSnapshot sourceSnapshot = null)
        {
            sourceSnapshot = sourceSnapshot ?? snapshot;
            if (candidate == null || source == null || candidate.Boundary.Length < 3 || candidate.DirectedSharedTriangleStream.Length / 3 != candidate.Boundary.Length - 2
                || !TryPlanarFace(snapshot, candidate, out var candidateNormal) || !CanonicalMeshSnapshot.ExactEquals(candidateNormal, sourceNormal)) return false;
            var sourceBoundary = OrderedBoundaryPositions(sourceSnapshot, source);
            if (sourceBoundary == null) return false;
            var stream = candidate.DirectedRawTriangleStream;
            for (var index = 0; index < stream.Length; index += 3)
            {
                var a = snapshot.RawVertices[stream[index]].LocalPosition;
                var b = snapshot.RawVertices[stream[index + 1]].LocalPosition;
                var c = snapshot.RawVertices[stream[index + 2]].LocalPosition;
                var normal = Vector3.Cross(b - a, c - a);
                if (!(UnityEngine.ProBuilder.Math.TriangleArea(a, b, c) > Mathf.Epsilon)
                    || !CanonicalMeshSnapshot.ExactEquals(normal.normalized, sourceNormal)
                    || !PointInPolygon((a + b + c) / 3f, sourceBoundary, sourceNormal, true)) return false;
            }
            return true;
        }

        private static bool TryPlanarFace(CanonicalMeshSnapshot snapshot, CanonicalMeshSnapshot.FaceOccurrence face, out Vector3 normal)
        {
            normal = Vector3.zero;
            if (snapshot == null || face == null || face.DirectedRawTriangleStream.Length < 3) return false;
            var stream = face.DirectedRawTriangleStream;
            var origin = snapshot.RawVertices[stream[0]].LocalPosition;
            for (var index = 0; index < stream.Length; index += 3)
            {
                var a = snapshot.RawVertices[stream[index]].LocalPosition;
                var b = snapshot.RawVertices[stream[index + 1]].LocalPosition;
                var c = snapshot.RawVertices[stream[index + 2]].LocalPosition;
                var triangleNormal = Vector3.Cross(b - a, c - a);
                if (!(UnityEngine.ProBuilder.Math.TriangleArea(a, b, c) > Mathf.Epsilon)) return false;
                triangleNormal.Normalize();
                if (normal == Vector3.zero) normal = triangleNormal;
                else if (!CanonicalMeshSnapshot.ExactEquals(normal, triangleNormal)) return false;
            }
            if (normal == Vector3.zero) return false;
            var capturedNormal = normal;
            foreach (var edge in face.Boundary)
                if (!PlaneContains(snapshot.RawVertices[edge.RawA].LocalPosition, origin, capturedNormal)
                    || !PlaneContains(snapshot.RawVertices[edge.RawB].LocalPosition, origin, capturedNormal)) return false;
            return true;
        }

        private static bool PointInFaceInterior(Vector3 point, CanonicalMeshSnapshot snapshot, CanonicalMeshSnapshot.FaceOccurrence face, Vector3 normal)
        {
            var boundary = OrderedBoundaryPositions(snapshot, face);
            return boundary != null && PlaneContains(point, boundary[0], normal) && PointInPolygon(point, boundary, normal, false);
        }

        private static Vector3[] OrderedBoundaryPositions(CanonicalMeshSnapshot snapshot, CanonicalMeshSnapshot.FaceOccurrence face)
        {
            if (snapshot == null || face == null || face.Boundary.Length < 3) return null;
            var remaining = face.Boundary.ToList();
            var first = remaining[0];
            var handles = new List<int> { first.SharedA, first.SharedB };
            remaining.RemoveAt(0);
            while (remaining.Count > 0)
            {
                var nextIndex = remaining.FindIndex(value => value.SharedA == handles[handles.Count - 1]);
                if (nextIndex < 0) return null;
                handles.Add(remaining[nextIndex].SharedB);
                remaining.RemoveAt(nextIndex);
            }
            if (handles[handles.Count - 1] != handles[0] || handles.Take(handles.Count - 1).Distinct().Count() != handles.Count - 1) return null;
            return handles.Take(handles.Count - 1).Select(handle => Position(snapshot, handle)).ToArray();
        }

        private static bool PointInPolygon(Vector3 point, Vector3[] boundary, Vector3 normal, bool includeBoundary)
        {
            if (boundary == null || boundary.Length < 3) return false;
            var projectedPoint = Project(point, normal);
            var projected = boundary.Select(value => Project(value, normal)).ToArray();
            for (var index = 0; index < projected.Length; index++)
                if (OnSegment(projectedPoint, projected[index], projected[(index + 1) % projected.Length])) return includeBoundary;
            var inside = false;
            for (int left = 0, right = projected.Length - 1; left < projected.Length; right = left++)
            {
                var a = projected[left]; var b = projected[right];
                if ((a.Y > projectedPoint.Y) != (b.Y > projectedPoint.Y)
                    && projectedPoint.X < (b.X - a.X) * (projectedPoint.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        private static Point2 Project(Vector3 value, Vector3 normal)
        {
            var x = Mathf.Abs(normal.x); var y = Mathf.Abs(normal.y); var z = Mathf.Abs(normal.z);
            return x >= y && x >= z ? new Point2(value.y, value.z) : y >= z ? new Point2(value.x, value.z) : new Point2(value.x, value.y);
        }

        private static bool OnSegment(Point2 point, Point2 a, Point2 b)
        {
            var cross = (point.X - a.X) * (b.Y - a.Y) - (point.Y - a.Y) * (b.X - a.X);
            if (!CanonicalMeshSnapshot.ExactEquals(cross, 0f)) return false;
            var dot = (point.X - a.X) * (point.X - b.X) + (point.Y - a.Y) * (point.Y - b.Y);
            return dot <= 0f;
        }

        private static bool OnSegmentInterior(Vector3 point, Vector3 a, Vector3 b)
        {
            var direction = b - a;
            if (direction.sqrMagnitude == 0f) return false;
            var t = Vector3.Dot(point - a, direction) / direction.sqrMagnitude;
            return t > 0f && t < 1f
                && CanonicalMeshSnapshot.ExactEquals(point, Vector3.Lerp(a, b, t));
        }

        private static bool IsCompleteTriangulation(CanonicalMeshSnapshot snapshot, CanonicalMeshSnapshot.FaceOccurrence candidate, CanonicalMeshSnapshot.FaceOccurrence source, Vector3 normal)
        {
            return IsCompleteTriangulation(snapshot, candidate, source, normal, snapshot);
        }

        private static bool BoundaryEquals(CanonicalMeshSnapshot snapshot, CanonicalMeshSnapshot.FaceOccurrence face, IList<Vector3[]> expected)
        {
            return expected != null && face.Boundary.Length == expected.Count
                && ExactBijection(expected, face.Boundary,
                    (edge, actual) => CanonicalMeshSnapshot.ExactEquals(edge[0], snapshot.RawVertices[actual.RawA].LocalPosition)
                        && CanonicalMeshSnapshot.ExactEquals(edge[1], snapshot.RawVertices[actual.RawB].LocalPosition));
        }

        private static bool UsesOnlyValues(CanonicalMeshSnapshot snapshot, CanonicalMeshSnapshot.FaceOccurrence face, IList<Vector3> values)
        {
            return face.DirectedSharedTriangleStream.All(handle => values.Count(value => CanonicalMeshSnapshot.ExactEquals(value, Position(snapshot, handle))) == 1);
        }

        private static bool HasBoundaryValue(CanonicalMeshSnapshot snapshot, CanonicalMeshSnapshot.FaceOccurrence face, Vector3 a, Vector3 b)
        {
            return face.Boundary.Any(edge => (CanonicalMeshSnapshot.ExactEquals(snapshot.RawVertices[edge.RawA].LocalPosition, a)
                && CanonicalMeshSnapshot.ExactEquals(snapshot.RawVertices[edge.RawB].LocalPosition, b))
                || (CanonicalMeshSnapshot.ExactEquals(snapshot.RawVertices[edge.RawA].LocalPosition, b)
                    && CanonicalMeshSnapshot.ExactEquals(snapshot.RawVertices[edge.RawB].LocalPosition, a)));
        }

        private static bool HasValueEdge(CanonicalMeshSnapshot snapshot, Vector3 a, Vector3 b)
        {
            return snapshot.Edges.Any(edge => (CanonicalMeshSnapshot.ExactEquals(Position(snapshot, edge.SharedMin), a)
                && CanonicalMeshSnapshot.ExactEquals(Position(snapshot, edge.SharedMax), b))
                || (CanonicalMeshSnapshot.ExactEquals(Position(snapshot, edge.SharedMin), b)
                    && CanonicalMeshSnapshot.ExactEquals(Position(snapshot, edge.SharedMax), a)));
        }

        private static bool RawTriangleCyclicEquals(CanonicalMeshSnapshot leftSnapshot, int[] left, CanonicalMeshSnapshot rightSnapshot, int[] right)
        {
            for (var offset = 0; offset < 3; offset++)
                if (RawValueEquals(leftSnapshot.RawVertices[left[0]], rightSnapshot.RawVertices[right[offset]])
                    && RawValueEquals(leftSnapshot.RawVertices[left[1]], rightSnapshot.RawVertices[right[(offset + 1) % 3]])
                    && RawValueEquals(leftSnapshot.RawVertices[left[2]], rightSnapshot.RawVertices[right[(offset + 2) % 3]])) return true;
            return false;
        }

        private static bool RawValueEquals(CanonicalMeshSnapshot.RawVertex left, CanonicalMeshSnapshot.RawVertex right)
        {
            return left != null && right != null && CanonicalMeshSnapshot.ExactEquals(left.LocalPosition, right.LocalPosition)
                && CanonicalMeshSnapshot.RawChannelValueEquals(left, right);
        }

        /// <summary>fresh point raw 必须逐值等于 effect 前冻结的 Vertex.Mix 或 new Vertex payload；normal/tangent 由全局 derived-channel proof 负责。</summary>
        private static bool PointMemberMatches(Request request, CanonicalMeshSnapshot.RawVertex actual)
        {
            if (request == null || request.ExpectedPoint == null || actual == null
                || !CanonicalMeshSnapshot.ExactEquals(actual.LocalPosition, request.ExpectedPoint.Position)
                || !CanonicalMeshSnapshot.ChannelValueEquals(actual.Color, request.ExpectedPoint.Color)) return false;
            var expectedUvs = request.ExpectedPoint.Uvs; var actualUvs = actual.Uvs;
            return actualUvs.Length == expectedUvs.Length && actualUvs.Zip(expectedUvs, CanonicalMeshSnapshot.ChannelValueEquals).All(value => value);
        }

        /// <summary>异类型关系同样使用增广路径求完整双射，不能按枚举顺序贪心消费。</summary>
        private static bool ExactBijection<TLeft, TRight>(IEnumerable<TLeft> left, IEnumerable<TRight> right, Func<TLeft, TRight, bool> equals)
        {
            if (left == null || right == null || equals == null) return false;
            var leftValues = left.ToArray(); var rightValues = right.ToArray();
            if (leftValues.Length != rightValues.Length) return false;
            var matchedLeftByRight = Enumerable.Repeat(-1, rightValues.Length).ToArray();
            for (var leftIndex = 0; leftIndex < leftValues.Length; leftIndex++)
                if (!TryAugment(leftIndex, leftValues, rightValues, equals, matchedLeftByRight, new bool[rightValues.Length])) return false;
            return true;
        }

        private static bool TryAugment<TLeft, TRight>(int leftIndex, TLeft[] left, TRight[] right, Func<TLeft, TRight, bool> equals, int[] matchedLeftByRight, bool[] visitedRight)
        {
            for (var rightIndex = 0; rightIndex < right.Length; rightIndex++)
            {
                if (visitedRight[rightIndex] || !equals(left[leftIndex], right[rightIndex])) continue;
                visitedRight[rightIndex] = true;
                if (matchedLeftByRight[rightIndex] < 0 || TryAugment(matchedLeftByRight[rightIndex], left, right, equals, matchedLeftByRight, visitedRight))
                { matchedLeftByRight[rightIndex] = leftIndex; return true; }
            }
            return false;
        }

        private static int[][] Split(int[] stream)
        {
            return Enumerable.Range(0, stream.Length / 3).Select(index => new[] { stream[index * 3], stream[index * 3 + 1], stream[index * 3 + 2] }).ToArray();
        }

        private static int BoundaryRawForHandle(CanonicalMeshSnapshot.FaceOccurrence face, int handle)
        {
            var edge = face.Boundary.FirstOrDefault(value => value.SharedA == handle || value.SharedB == handle);
            return edge == null ? -1 : edge.SharedA == handle ? edge.RawA : edge.RawB;
        }

        /// <summary>5.2.4 会删除每个重建 face 的 distinct raw；预算只在这些 raw 不被其他 face 复用时可精确重放。</summary>
        private static bool FacesHaveExclusiveRawOwnership(CanonicalMeshSnapshot snapshot, IEnumerable<int> occurrences)
        {
            if (snapshot == null || occurrences == null) return false;
            var selected = new HashSet<int>(occurrences);
            if (selected.Count == 0 || selected.Any(occurrence => !snapshot.Faces.Any(face => face.OccurrenceIndex == occurrence))) return false;
            var ownerCounts = snapshot.Faces
                .SelectMany(face => face.DirectedRawTriangleStream.Distinct().Select(raw => new { raw, face.OccurrenceIndex }))
                .GroupBy(value => value.raw)
                .ToDictionary(group => group.Key, group => group.Count());
            return snapshot.Faces.Where(face => selected.Contains(face.OccurrenceIndex))
                .SelectMany(face => face.DirectedRawTriangleStream).Distinct()
                .All(raw => ownerCounts.TryGetValue(raw, out var count) && count == 1);
        }

        private static bool SharedPositionsAreUnique(CanonicalMeshSnapshot snapshot)
        {
            return snapshot != null && snapshot.SharedVertices.All(left => snapshot.SharedVertices.Count(right => CanonicalMeshSnapshot.ExactEquals(left.CanonicalPosition, right.CanonicalPosition)) == 1);
        }

        /// <summary>face insertion 的 5.2.4 实现会清空全 mesh sharedTexture lookup；必须在 effect 前显式拒绝。</summary>
        internal static bool HasFaceInsertionSharedTextureConflict(GameObject go, int face)
        {
            var snapshot = CanonicalMeshSnapshot.Capture(go, new[] { face });
            return snapshot != null && snapshot.SharedTextures.Length != 0;
        }

        private static bool HasSharedTextureGroups(CanonicalMeshSnapshot snapshot)
        {
            return snapshot == null || snapshot.SharedTextures.Length != 0;
        }

        /// <summary>face 分支令全部新点共享 vertices.Count UV id，并把每个 `-1` 端点 mapping 转为 singleton；edge 分支精确保留旧 groups。</summary>
        private static bool SharedTextureLayoutMatches(Request request, CanonicalMeshSnapshot after, Vector3 point)
        {
            if (request == null || request.Before == null || after == null || !Finite(point)) return false;
            var before = request.Before;
            if (!request.EdgeMode)
            {
                var generatedRaw = after.Faces.Where(face => FaceContainsValue(after, face, point))
                    .SelectMany(face => face.DirectedRawTriangleStream).Distinct().ToArray();
                var members = after.SharedTextures.SelectMany(group => group.MemberRawIndexes).ToArray();
                var boundaryCount = before.Faces.Single(face => face.OccurrenceIndex == request.SourceFaceOccurrence).Boundary.Length;
                var pointGroups = after.SharedTextures.Where(group => group.MemberRawIndexes.All(raw => CanonicalMeshSnapshot.ExactEquals(after.RawVertices[raw].LocalPosition, point))).ToArray();
                var endpointGroups = after.SharedTextures.Except(pointGroups).ToArray();
                return before.HasSharedTextures && before.SharedTextures.Length == 0 && after.HasSharedTextures
                    && generatedRaw.Length == boundaryCount * 3 && after.SharedTextures.Length == boundaryCount * 2 + 1
                    && pointGroups.Length == 1 && pointGroups[0].MemberRawIndexes.Length == boundaryCount
                    && endpointGroups.Length == boundaryCount * 2 && endpointGroups.All(group => group.MemberRawIndexes.Length == 1)
                    && members.Length == generatedRaw.Length && members.Distinct().Count() == members.Length
                    && generatedRaw.All(raw => members.Contains(raw));
            }
            return CanonicalMeshSnapshot.ExactMultisetEquals(before.SharedTextures, after.SharedTextures,
                (left, right) => TextureGroupValueEquals(before, left, after, right));
        }

        /// <summary>5.2.4 edge insertion 会重排 boundary vertices 却不重排 sharedIndexesUV，因此 affected faces 只能接受空映射。</summary>
        private static bool AffectedTextureCoverageIsEmpty(CanonicalMeshSnapshot snapshot, IEnumerable<int> occurrences)
        {
            var mapped = new HashSet<int>(snapshot.SharedTextures.SelectMany(group => group.MemberRawIndexes));
            return occurrences.All(occurrence => snapshot.Faces.Single(face => face.OccurrenceIndex == occurrence)
                .DirectedRawTriangleStream.Distinct().All(raw => !mapped.Contains(raw)));
        }

        private static bool TextureGroupValueEquals(CanonicalMeshSnapshot leftSnapshot, CanonicalMeshSnapshot.SharedTextureSnapshot left, CanonicalMeshSnapshot rightSnapshot, CanonicalMeshSnapshot.SharedTextureSnapshot right)
        {
            return CanonicalMeshSnapshot.ExactMultisetEquals(
                left.MemberRawIndexes.Select(index => leftSnapshot.RawVertices[index]),
                right.MemberRawIndexes.Select(index => rightSnapshot.RawVertices[index]),
                RawValueEquals);
        }

        private static bool FaceContainsValue(CanonicalMeshSnapshot snapshot, CanonicalMeshSnapshot.FaceOccurrence face, Vector3 value)
        {
            return face.DirectedRawTriangleStream.Any(raw => CanonicalMeshSnapshot.ExactEquals(snapshot.RawVertices[raw].LocalPosition, value));
        }

        private static string SharedTextureDiagnostic(Request request, CanonicalMeshSnapshot after, Vector3 point)
        {
            var before = request.Before;
            var pointFaces = after.Faces.Where(face => FaceContainsValue(after, face, point)).ToArray();
            var pointRaw = pointFaces.SelectMany(face => face.DirectedRawTriangleStream).Distinct().Count();
            return "beforePresent=" + before.HasSharedTextures + ",beforeGroups=" + before.SharedTextures.Length
                + ",afterPresent=" + after.HasSharedTextures + ",afterGroups=" + after.SharedTextures.Length
                + ",afterMembers=" + after.SharedTextures.Sum(group => group.MemberRawIndexes.Length)
                + ",singletonGroups=" + after.SharedTextures.Count(group => group.MemberRawIndexes.Length == 1)
                + ",pointFaces=" + pointFaces.Length + ",pointRaw=" + pointRaw;
        }

        private static bool SameFaceAttributes(CanonicalMeshSnapshot.FaceOccurrence left, CanonicalMeshSnapshot.FaceOccurrence right)
        {
            return left != null && right != null && left.SubmeshIndex == right.SubmeshIndex && left.SmoothingGroup == right.SmoothingGroup
                && left.ManualUv == right.ManualUv && left.TextureGroup == right.TextureGroup && left.ElementGroup == right.ElementGroup;
        }

        private static bool Stable(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after)
        {
            return CanonicalMeshSnapshot.IdentityAndTransformEqual(before, after);
        }

        private static bool SameEdge(int a, int b, int c, int d) { return a == c && b == d || a == d && b == c; }
        private static Vector3 Position(CanonicalMeshSnapshot snapshot, int handle) { return snapshot.SharedVertices.Single(value => value.Handle == handle).CanonicalPosition; }
        private static bool PlaneContains(Vector3 point, Vector3 origin, Vector3 normal) { return CanonicalMeshSnapshot.ExactEquals(Vector3.Dot(point - origin, normal), 0f); }
        private static bool Finite(CanonicalMeshSnapshot.ChannelValue value) { return value != null && (!value.IsPresent || Finite(value.Value)); }
        private static bool Finite(Vector2 value) { return !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y); }
        private static bool Finite(Vector3 value) { return !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z); }
        private static bool Finite(Vector4 value) { return !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z) && !float.IsNaN(value.w) && !float.IsInfinity(value.w); }
    }
}
#endif
