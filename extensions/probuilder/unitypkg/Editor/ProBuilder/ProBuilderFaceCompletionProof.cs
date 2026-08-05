#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>Request-specific, value-based proofs over CanonicalMeshSnapshot.</summary>
    internal static class ProBuilderFaceCompletionProof
    {
        internal sealed class AutoSmoothPlan
        {
            internal CanonicalMeshSnapshot Before;
            internal int[] SelectedOccurrences;
            internal AutoSmoothFaceClaim[] ExpectedFaces;
        }
        internal sealed class AutoSmoothFaceClaim
        {
            internal int OccurrenceIndex;
            internal int ExpectedGroup;
        }

        /// <summary>逐步镜像 5.2.4：selected 先归零，再按 cached/raw normals 构造 soft component 并分配未占用 group。</summary>
        internal static AutoSmoothPlan CaptureAutoSmooth(GameObject go, int[] selectedOccurrences, float angleThreshold)
        {
            var before = CanonicalMeshSnapshot.Capture(go, selectedOccurrences);
            if (before == null || selectedOccurrences == null || selectedOccurrences.Length == 0
                || selectedOccurrences.Distinct().Count() != selectedOccurrences.Length || !CanonicalMeshSnapshot.RefreshArithmeticFinite(before)
                || !ProBuilderRefreshUvProof.RefreshUvsAreTerminal(before)) return null;
            var selected = new HashSet<int>(selectedOccurrences);
            if (selected.Any(occurrence => !before.Faces.Any(face => face.OccurrenceIndex == occurrence))) return null;
            // fresh 终态会匹配全部 face；任何未选面的 shared-value claim 若连自身都不唯一，必须在 effect 前拒绝。
            if (before.Faces.Any(face => !CanonicalMeshSnapshot.FaceEqualsIgnoringSmoothingAndNormals(before, face, before, face))) return null;
            foreach (var occurrence in selectedOccurrences)
            {
                var source = before.Faces.Single(face => face.OccurrenceIndex == occurrence);
                if (before.Faces.Count(candidate => CanonicalMeshSnapshot.FaceEqualsIgnoringSmoothingAndNormals(before, source, before, candidate)) != 1) return null;
            }

            Vector3[] normals;
            var cachedNormalsPresent = before.RawVertices.All(value => value.LocalNormal.IsPresent);
            var cachedNormalsAbsent = before.RawVertices.All(value => !value.LocalNormal.IsPresent);
            // 5.2.4 只清 compiled Mesh.normals；GetNormals 仍由 ProBuilderMesh.m_Normals 决定是否重算。
            if (cachedNormalsPresent)
                normals = before.RawVertices.Select(value => new Vector3(value.LocalNormal.Value.x, value.LocalNormal.Value.y, value.LocalNormal.Value.z)).ToArray();
            else if (!cachedNormalsAbsent || !before.TryCalculateNormalsAfterSmoothingReset(selected, out normals)) return null;

            var threshold = Mathf.Abs(Mathf.Cos(Mathf.Clamp(angleThreshold, 0f, 89.999f) * Mathf.Deg2Rad));
            var softGraph = selectedOccurrences.ToDictionary(occurrence => occurrence, occurrence => new HashSet<int>());
            foreach (var edge in before.Edges)
            {
                var incidences = edge.Incidences.Where(value => selected.Contains(value.FaceOccurrenceIndex)).ToArray();
                if (incidences.Length > 2) return null;
                if (incidences.Length != 2) continue;
                if (!TrySoftEdge(edge, incidences[0], incidences[1], normals, threshold, out var compatible)) return null;
                if (!compatible) continue;
                softGraph[incidences[0].FaceOccurrenceIndex].Add(incidences[1].FaceOccurrenceIndex);
                softGraph[incidences[1].FaceOccurrenceIndex].Add(incidences[0].FaceOccurrenceIndex);
            }

            var usedGroups = new HashSet<int>(before.Faces.Where(face => !selected.Contains(face.OccurrenceIndex)).Select(face => face.SmoothingGroup));
            usedGroups.Add(0);
            var nextGroup = NextUnusedSmoothingGroup(1, usedGroups);
            var processed = new HashSet<int>();
            var expected = selectedOccurrences.ToDictionary(occurrence => occurrence, occurrence => 0);
            foreach (var occurrence in selectedOccurrences)
            {
                if (processed.Contains(occurrence)) continue;
                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(occurrence); processed.Add(occurrence);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue(); component.Add(current);
                    foreach (var neighbor in softGraph[current]) if (processed.Add(neighbor)) queue.Enqueue(neighbor);
                }
                if (component.Count == 1) continue;
                foreach (var member in component) expected[member] = nextGroup;
                usedGroups.Add(nextGroup);
                nextGroup = NextUnusedSmoothingGroup(nextGroup, usedGroups);
            }
            if (expected.Values.Any(group => group > ushort.MaxValue)) return null;
            return new AutoSmoothPlan
            {
                Before = before,
                SelectedOccurrences = selectedOccurrences.ToArray(),
                ExpectedFaces = selectedOccurrences.Select(occurrence => new AutoSmoothFaceClaim { OccurrenceIndex = occurrence, ExpectedGroup = expected[occurrence] }).ToArray()
            };
        }

        /// <summary>no-op 只有在每个 selected face 已等于算法精确终态时成立，空 adjacency 不再自动判成功。</summary>
        internal static bool AutoSmoothAlreadyMatches(AutoSmoothPlan plan)
        {
            if (plan == null || plan.ExpectedFaces == null) return false;
            var beforeByOccurrence = plan.Before.Faces.ToDictionary(face => face.OccurrenceIndex);
            return plan.ExpectedFaces.All(claim => beforeByOccurrence.TryGetValue(claim.OccurrenceIndex, out var face) && face.SmoothingGroup == claim.ExpectedGroup)
                && CanonicalMeshSnapshot.DerivedChannelsMatch(plan.Before);
        }

        /// <summary>fresh face 必须与前态形成完整双射；selected group 逐项等于冻结终态，其他业务属性不得变化。</summary>
        internal static bool AutoSmoothMatches(AutoSmoothPlan plan, CanonicalMeshSnapshot after)
        {
            if (plan == null || plan.ExpectedFaces == null || after == null || !Stable(plan.Before, after)
                || plan.Before.Faces.Length != after.Faces.Length || !SharedTexturesEqual(plan.Before, after)) return false;
            var expectedGroups = plan.ExpectedFaces.ToDictionary(claim => claim.OccurrenceIndex, claim => claim.ExpectedGroup);
            return CanonicalMeshSnapshot.ExactMultisetEquals(plan.Before.Faces, after.Faces, (source, candidate) =>
                    CanonicalMeshSnapshot.FaceEqualsIgnoringSmoothingAndNormals(plan.Before, source, after, candidate)
                    && candidate.SmoothingGroup == (expectedGroups.TryGetValue(source.OccurrenceIndex, out var expected) ? expected : source.SmoothingGroup))
                && CanonicalMeshSnapshot.RendererMaterialsEqual(plan.Before, after) && CanonicalMeshSnapshot.DerivedChannelsMatch(after);
        }

        internal static long ExpectedSmoothedFaceCount(AutoSmoothPlan plan) => plan?.ExpectedFaces == null
            ? 0L
            : plan.ExpectedFaces.LongCount(claim => UnityEngine.ProBuilder.Smoothing.IsSmooth(claim.ExpectedGroup));

        private static bool Stable(CanonicalMeshSnapshot a, CanonicalMeshSnapshot b) => CanonicalMeshSnapshot.IdentityAndTransformEqual(a, b);
        private static bool TrySoftEdge(CanonicalMeshSnapshot.SharedEdgeOccurrence edge, CanonicalMeshSnapshot.BoundaryIncidence left, CanonicalMeshSnapshot.BoundaryIncidence right, Vector3[] normals, float threshold, out bool compatible)
        {
            compatible = false;
            var leftA = RawAtShared(left, edge.SharedMin); var leftB = RawAtShared(left, edge.SharedMax);
            var rightA = RawAtShared(right, edge.SharedMin); var rightB = RawAtShared(right, edge.SharedMax);
            if (new[] { leftA, leftB, rightA, rightB }.Any(raw => raw < 0 || raw >= normals.Length)) return false;
            compatible = Mathf.Abs(Vector3.Dot(normals[leftA].normalized, normals[rightA].normalized)) > threshold
                && Mathf.Abs(Vector3.Dot(normals[leftB].normalized, normals[rightB].normalized)) > threshold;
            return true;
        }
        private static int RawAtShared(CanonicalMeshSnapshot.BoundaryIncidence incidence, int shared) => incidence.SharedA == shared ? incidence.RawA : incidence.SharedB == shared ? incidence.RawB : -1;

        /// <summary>复制 5.2.4 Smoothing.GetNextUnusedSmoothingGroup 的区间跳跃与查重顺序。</summary>
        private static int NextUnusedSmoothingGroup(int start, ISet<int> used)
        {
            while (used.Contains(start) && start < int.MaxValue - 1)
            {
                start++;
                if (start > 24 && start < 42) start = 43;
            }
            return start;
        }

        /// <summary>auto-smooth 不拥有 shared texture；证明其 value partition 完整保持。</summary>
        private static bool SharedTexturesEqual(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after)
        {
            if (before.HasSharedTextures != after.HasSharedTextures) return false;
            if (!before.HasSharedTextures) return true;
            var beforeRaw = before.RawVertices; var afterRaw = after.RawVertices;
            return CanonicalMeshSnapshot.ExactMultisetEquals(before.SharedTextures, after.SharedTextures, (left, right) =>
                CanonicalMeshSnapshot.ExactMultisetEquals(left.MemberRawIndexes.Select(index => beforeRaw[index]), right.MemberRawIndexes.Select(index => afterRaw[index]),
                    (a, b) => CanonicalMeshSnapshot.ExactEquals(a.LocalPosition, b.LocalPosition) && CanonicalMeshSnapshot.RawChannelValueEquals(a, b)));
        }
    }
}
#endif
