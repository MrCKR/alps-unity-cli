#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>内部证明辅助逻辑。</summary>
    internal static class ProBuilderVertexObjectCompletionProof
    {
        /// <summary>内部证明辅助逻辑。</summary>
        internal sealed class CreateShapePlan
        {
            internal readonly ProBuilderShapeTopologyRecords.Record Record;
            internal readonly HashSet<int> IdentitiesBefore;
            internal readonly Scene Scene;
            internal readonly string Name;
            internal readonly Vector3 Position, RequestedWorldSize, LocalScale, ExpectedWorldBoundsCenter;
            internal CreateShapePlan(ProBuilderShapeTopologyRecords.Record record, HashSet<int> before, Scene scene, string name, Vector3 position, Vector3 requestedWorldSize, Vector3 localScale, Vector3 expectedWorldBoundsCenter)
            { Record = record; IdentitiesBefore = before; Scene = scene; Name = name; Position = position; RequestedWorldSize = requestedWorldSize; LocalScale = localScale; ExpectedWorldBoundsCenter = expectedWorldBoundsCenter; }
        }

        /// <summary>内部证明辅助逻辑。</summary>
        internal static CreateShapePlan CaptureCreateShapePlan(string shape, string name, Vector3 position, float? width, float? height, float? depth, Scene activeScene)
        {
            if (ProBuilderReflection.InstalledPackageVersion != ProBuilderShapeTopologyRecords.PackageVersion
                || !ProBuilderShapeTopologyRecords.TryGet(shape, out var record)
                || activeScene != SceneManager.GetActiveScene()
                || !Finite(record.DefaultLocalBounds.center)
                || !Finite(record.DefaultLocalBounds.size)
                || record.DefaultLocalBounds.center != Vector3.zero) return null;
            var baseSize = record.DefaultLocalBounds.size;
            var requested = new Vector3(width ?? baseSize.x, height ?? baseSize.y, depth ?? baseSize.z);
            if (!Finite(position) || !Finite(requested) || !ScaleForBounds(baseSize, requested, out var scale)) return null;
            return new CreateShapePlan(record, CaptureObjectIdentities(), activeScene, name, position, requested, scale, position);
        }

        /// <summary>内部证明辅助逻辑。</summary>
        private static bool ScaleForBounds(Vector3 baseSize, Vector3 requested, out Vector3 scale)
        {
            scale = Vector3.one;
            for (var axis = 0; axis < 3; axis++)
            {
                var source = baseSize[axis]; var target = requested[axis];
                if (source == 0f) { if (target != 0f) return false; continue; }
                if (!(target > 0f)) return false;
                scale[axis] = target / source;
                if (!Finite(scale[axis])) return false;
            }
            return Finite(scale);
        }

        /// <summary>内部证明辅助逻辑。</summary>
        internal static HashSet<int> CaptureObjectIdentities() => new HashSet<int>(UnityEngine.Object.FindObjectsOfType<GameObject>(true)
            .Where(value => value != null && value.scene.IsValid() && value.scene.isLoaded).Select(value => value.GetInstanceID()));

        /// <summary>内部证明辅助逻辑。</summary>
        internal static string CreatedShapeMismatch(CreateShapePlan plan, GameObject go, object mesh, ProBuilderOperationProof.ObjectIdentityClaim identity)
        {
            if (plan == null) return "plan";
            if (go == null || mesh == null || identity == null || plan.IdentitiesBefore == null) return "object-identity";
            if (plan.IdentitiesBefore.Contains(go.GetInstanceID()) || go.GetInstanceID() != identity.InstanceId) return "fresh-object-identity";
            if (go.scene != plan.Scene || go.scene.handle != identity.SceneHandle) return "scene-identity";
            if (!string.Equals(GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(), identity.GlobalObjectId, StringComparison.Ordinal)) return "global-object-id";
            if (!string.Equals(go.name, plan.Name, StringComparison.Ordinal)) return "name";
            if (!Approximately(go.transform.position, plan.Position)) return "position";
            var pb = mesh as ProBuilderMesh;
            if (pb == null || !BoundsFromPositions(pb.positions, out var localBounds) || !Approximately(localBounds.center, Vector3.zero)) return "center-pivot";
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return "renderer";
            if (!Approximately(renderer.bounds.center, plan.ExpectedWorldBoundsCenter)) return "renderer-bounds-center";
            if (!Approximately(renderer.bounds.size, plan.RequestedWorldSize)) return "renderer-bounds-size";
            return plan.Record.Mismatch(mesh);
        }

        /// <summary>内部证明辅助逻辑。</summary>
        private static bool BoundsFromPositions(IList<Vector3> positions, out Bounds bounds)
        {
            bounds = default;
            if (positions == null || positions.Count == 0 || !Finite(positions[0])) return false;
            bounds = new Bounds(positions[0], Vector3.zero);
            for (var i = 1; i < positions.Count; i++)
            {
                if (!Finite(positions[i])) return false;
                bounds.Encapsulate(positions[i]);
            }
            return Finite(bounds.center) && Finite(bounds.size);
        }

        // 以下类型仅服务于批准的 merge-objects、merge-vertices 与 weld-vertices dedicated proof。
        internal sealed class ExpectedMesh
        {
            internal readonly CanonicalMeshSnapshot.RawVertex[] Raw;
            internal readonly ExpectedFace[] Faces;
            internal readonly int[][] SharedPartitions, TexturePartitions;
            internal readonly bool HasSharedTextures;
            internal ExpectedMesh(CanonicalMeshSnapshot.RawVertex[] raw, IEnumerable<ExpectedFace> faces, IEnumerable<int[]> shared, bool hasSharedTextures, IEnumerable<int[]> textures)
            { Raw = (CanonicalMeshSnapshot.RawVertex[])raw.Clone(); Faces = faces.ToArray(); SharedPartitions = shared.Select(value => value.ToArray()).ToArray(); HasSharedTextures = hasSharedTextures; TexturePartitions = textures.Select(value => value.ToArray()).ToArray(); }
        }
        internal sealed class ExpectedFace
        {
            internal readonly CanonicalMeshSnapshot.FaceOccurrence Source; internal readonly int[] RawStream; internal readonly int SubmeshIndex; internal readonly CanonicalMeshSnapshot.MaterialClaim Material;
            internal ExpectedFace(CanonicalMeshSnapshot.FaceOccurrence source, IEnumerable<int> raw, int? submeshIndex = null, CanonicalMeshSnapshot.MaterialClaim material = null) { Source = source; RawStream = raw.ToArray(); SubmeshIndex = submeshIndex ?? source.SubmeshIndex; Material = material ?? source.Material; }
        }
        internal sealed class MergeVerticesPlan { internal CanonicalMeshSnapshot Before; internal ExpectedMesh Expected; internal int ExpectedReturnedIndex; internal bool IsNoOp; }
        internal sealed class WeldPlan { internal CanonicalMeshSnapshot Before; internal ExpectedMesh Expected; internal int[][] Clusters; internal int SelectedHandleCount; internal bool IsUnambiguous; }
        // merge-objects 继续使用完整 request-specific terminal proof。
        internal sealed class MergeObjectsPlan
        {
            internal readonly CanonicalMeshSnapshot[] Sources;
            internal readonly ExpectedMesh Expected;
            internal readonly Material[] MaterialSlots;
            internal readonly string Name;
            internal MergeObjectsPlan(IEnumerable<CanonicalMeshSnapshot> sources, ExpectedMesh expected, IEnumerable<Material> slots, string name)
            { Sources = sources.ToArray(); Expected = expected; MaterialSlots = slots.ToArray(); Name = name; }
        }

        // 仅保留已批准的请求语义。
        internal static MergeObjectsPlan CaptureMergeObjects(GameObject[] targets, string name)
        {
            if (targets == null || targets.Length < 2 || targets.Any(target => target == null) || string.IsNullOrWhiteSpace(name)
                || targets.Select(target => target.GetInstanceID()).Distinct().Count() != targets.Length
                || targets.Any(left => targets.Any(right => left != right && (left.transform.IsChildOf(right.transform) || right.transform.IsChildOf(left.transform))))) return null;
            var sources = targets.Select(target => CanonicalMeshSnapshot.Capture(target)).ToArray();
            if (sources.Any(source => source == null || !source.HasSharedTextures || HasUv1(source) || !ProBuilderRefreshUvProof.RefreshUvsAreTerminal(source) || Mathf.Abs(source.LocalToWorldMatrix.determinant) <= Mathf.Epsilon || Mathf.Abs(source.WorldToLocalMatrix.determinant) <= Mathf.Epsilon)
                || targets.Any(target => !target.scene.IsValid() || !target.scene.isLoaded || target.scene.handle != targets[0].scene.handle || (target != targets[0] && target.transform.childCount != 0))) return null;
            var renderers = targets.Select(target => target.GetComponent<Renderer>()).ToArray();
            if (renderers.Any(renderer => renderer == null || renderer.sharedMaterials == null || renderer.sharedMaterials.Length == 0 || renderer.sharedMaterials.Any(material => material == null))
                || sources.SelectMany(source => source.Faces).Any(face => face.Material.State != CanonicalMeshSnapshot.MaterialState.Object)
                || sources.Skip(1).SelectMany(source => source.Faces).Any(face => !face.ManualUv && !face.Uv.useWorldSpace)
                || sources.Sum(source => source.RawVertices.Length) >= 65535) return null;
            var target = sources[0]; var slots = new List<Material>(renderers[0].sharedMaterials); var raw = new List<CanonicalMeshSnapshot.RawVertex>(); var faces = new List<ExpectedFace>(); var shared = new List<int[]>(); var textures = new List<int[]>();
            for (var sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                var source = sources[sourceIndex]; var offset = raw.Count;
                foreach (var vertex in source.RawVertices)
                {
                    var local = sourceIndex == 0 ? vertex.LocalPosition : target.WorldToLocalMatrix.MultiplyPoint3x4(source.LocalToWorldMatrix.MultiplyPoint3x4(vertex.LocalPosition));
                    raw.Add(CopyRaw(vertex, local, offset + vertex.RawIndex, target.LocalToWorldMatrix.MultiplyPoint3x4(local)));
                }
                foreach (var face in source.Faces)
                {
                    var material = renderers[sourceIndex].sharedMaterials[Mathf.Clamp(face.SubmeshIndex, 0, renderers[sourceIndex].sharedMaterials.Length - 1)];
                    var slot = slots.IndexOf(material); if (slot < 0) { slots.Add(material); slot = slots.Count - 1; }
                    faces.Add(new ExpectedFace(face, face.DirectedRawTriangleStream.Select(index => offset + index), slot, CanonicalMeshSnapshot.WithEffectiveSlot(face.Material, slot)));
                }
                shared.AddRange(source.SharedVertices.Select(group => group.MemberRawIndexes.Select(index => offset + index).ToArray()));
                textures.AddRange(source.SharedTextures.Select(group => group.MemberRawIndexes.Select(index => offset + index).ToArray()));
            }
            var expectedRaw = raw.ToArray();
            var expected = new ExpectedMesh(expectedRaw, faces, shared, true, textures);
            if (faces.Any(face => HasTriangleAtOrBelowEpsilon(face.RawStream, expectedRaw)) || !ExpectedMeshFinite(target, expected)) return null;
            return new MergeObjectsPlan(sources, expected, slots, name);
        }

        internal static bool MergeObjectsMatches(MergeObjectsPlan plan, GameObject freshGo, ProBuilderMesh freshMesh)
        {
            if (plan == null || freshGo == null || freshMesh == null || plan.Sources.Length < 2) return false;
            var first = plan.Sources[0];
            if (freshGo.GetInstanceID() != first.Identity.GameObjectInstanceId || freshMesh.GetInstanceID() != first.Identity.ProBuilderMeshInstanceId || freshGo.scene.handle != first.Identity.SceneHandle || !string.Equals(freshGo.scene.path, first.Identity.ScenePath, StringComparison.Ordinal) || !string.Equals(GlobalObjectId.GetGlobalObjectIdSlow(freshGo).ToString(), first.Identity.GlobalObjectId, StringComparison.Ordinal) || !string.Equals(freshGo.name, plan.Name, StringComparison.Ordinal) || plan.Sources.Skip(1).Any(source => EditorUtility.InstanceIDToObject(source.Identity.GameObjectInstanceId) != null)) return false;
            var after = CanonicalMeshSnapshot.Capture(freshGo); var renderer = freshGo.GetComponent<Renderer>();
            return after != null && SameIdentityAndTransform(first, after) && ExactExpectedEquals(after, plan.Expected) && renderer != null && renderer.sharedMaterials != null && renderer.sharedMaterials.Length == plan.MaterialSlots.Length && renderer.sharedMaterials.Zip(plan.MaterialSlots, (actual, expected) => actual == expected).All(value => value);
        }

        internal static MergeVerticesPlan CaptureMergeVertices(object value, int[] requested, bool collapseToFirst)
        {
            var mesh = value as ProBuilderMesh; var before = mesh == null ? null : CanonicalMeshSnapshot.Capture(mesh.gameObject);
            if (before == null || requested == null || requested.Length < 2 || requested.Any(raw => raw < 0 || raw >= before.RawVertices.Length)) return null;
            var center = collapseToFirst ? before.RawVertices[requested[0]] : AverageRaw(before.RawVertices, requested);
            if (!RawTerminalFinite(before, center)) return null;
            var requestedHandles = requested.Select(raw => before.RawVertices[raw].SharedHandle).Distinct().ToArray();
            var sharedGroups = mesh.sharedVertices;
            if (sharedGroups == null || requestedHandles.Any(handle => handle < 0 || handle >= sharedGroups.Count)) return null;
            // 仅保留已批准的请求语义。
            var mergedMemberOrder = requestedHandles.OrderBy(handle => handle).SelectMany(handle => sharedGroups[handle].ToArray()).ToArray();
            var memberSet = new HashSet<int>(mergedMemberOrder);
            var raw = before.RawVertices.Select(value => CopyRaw(value, memberSet.Contains(value.RawIndex) ? center : value)).ToArray();
            var handles = before.RawVertices.Select(value => requestedHandles.Contains(value.SharedHandle) ? requestedHandles[0] : value.SharedHandle).ToArray();
            var textures = SplitRequestedTexturePartitions(before, requested);
            var expected = RemoveDegenerateAndReindex(before, raw, handles, textures);
            if (expected == null || !ExpectedMeshFinite(before, expected.Mesh)) return null;
            var survivingMembers = mergedMemberOrder.Where(rawIndex => expected.OldToNew.ContainsKey(rawIndex)).ToArray();
            var survivorOld = survivingMembers.Length == 0 ? -1 : survivingMembers[survivingMembers.Length - 1];
            var survivor = survivorOld < 0 ? -1 : expected.OldToNew[survivorOld];
            var isNoOp = ExactExpectedEquals(before, expected.Mesh);
            if (!isNoOp && (!before.HasSharedTextures || HasUv1(before))) return null;
            return new MergeVerticesPlan { Before = before, Expected = expected.Mesh, ExpectedReturnedIndex = survivor, IsNoOp = isNoOp };
        }

        internal static bool MergeVerticesMatches(MergeVerticesPlan plan, object value, int returnedIndex)
        {
            var mesh = value as ProBuilderMesh; var after = mesh == null ? null : CanonicalMeshSnapshot.CaptureAfterMergeVertices(mesh.gameObject);
            return plan != null && after != null && returnedIndex == plan.ExpectedReturnedIndex && SameIdentityAndTransform(plan.Before, after) && CanonicalMeshSnapshot.RendererMaterialsEqual(plan.Before, after) && ExactExpectedEquals(after, plan.Expected);
        }

        internal static WeldPlan CaptureWeld(object value, int[] requested, float radius)
        {
            var mesh = value as ProBuilderMesh; var before = mesh == null ? null : CanonicalMeshSnapshot.Capture(mesh.gameObject);
            if (before == null || requested == null || float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0 || !Finite(radius * radius) || requested.Any(raw => raw < 0 || raw >= before.RawVertices.Length)) return null;
            var handles = requested.Select(raw => before.RawVertices[raw].SharedHandle).Distinct().OrderBy(value => value).ToArray();
            var components = Components(before, handles, radius);
            // 5.2.4 KdTree 对大于两个 handle 的 transitive cluster 存在顺序歧义，effect 前必须拒绝。
            var unambiguous = components.All(component => component.Length <= 2 && component.All(a => component.All(b => a == b || Near(before, a, b, radius))));
            if (!unambiguous) return new WeldPlan { Before = before, IsUnambiguous = false, SelectedHandleCount = handles.Length };
            if (components.All(component => component.Length == 1)) return new WeldPlan { Before = before, Clusters = components, SelectedHandleCount = handles.Length, IsUnambiguous = true };
            if (HasUv1(before)) return null;
            var raw = before.RawVertices.Select(value => CopyRaw(value, value)).ToArray();
            var remap = before.RawVertices.Select(value => value.SharedHandle).ToArray();
            foreach (var cluster in components)
            {
                var average = Average(cluster.Select(handle => Shared(before, handle).CanonicalPosition));
                if (!Finite(average)) return null;
                foreach (var rawIndex in cluster.SelectMany(handle => Shared(before, handle).MemberRawIndexes))
                { raw[rawIndex] = CopyRaw(raw[rawIndex], average); remap[rawIndex] = cluster[0]; }
            }
            var expected = new ExpectedMesh(raw, before.Faces.Select(face => new ExpectedFace(face, face.DirectedRawTriangleStream)), PartitionsFromHandles(remap), before.HasSharedTextures, before.SharedTextures.Select(group => group.MemberRawIndexes));
            if (!ExpectedMeshFinite(before, expected)) return null;
            return new WeldPlan { Before = before, Expected = expected, Clusters = components, SelectedHandleCount = handles.Length, IsUnambiguous = true };
        }

        internal static bool WeldPlanIsUnambiguous(WeldPlan plan) => plan != null && plan.IsUnambiguous;
        internal static bool WeldIsNoOp(WeldPlan plan) => plan != null && plan.IsUnambiguous && plan.Clusters.All(cluster => cluster.Length == 1);
        internal static bool WeldMatches(WeldPlan plan, object value, int weldedCount)
        {
            var mesh = value as ProBuilderMesh; var after = mesh == null ? null : CanonicalMeshSnapshot.CaptureRelaxedAfterWeld(mesh.gameObject);
            return plan != null && plan.IsUnambiguous && after != null && weldedCount == plan.SelectedHandleCount && SameIdentityAndTransform(plan.Before, after) && CanonicalMeshSnapshot.RendererMaterialsEqual(plan.Before, after) && ExactExpectedEquals(after, plan.Expected);
        }

        /// <summary>内部证明辅助逻辑。</summary>
        private static ReindexedExpected RemoveDegenerateAndReindex(CanonicalMeshSnapshot before, CanonicalMeshSnapshot.RawVertex[] raw, int[] handles, int[][] textures)
        {
            var faces = new List<ExpectedFace>();
            foreach (var face in before.Faces)
            {
                var canonical = new Dictionary<int, int>(); var stream = new List<int>(); var source = face.DirectedRawTriangleStream;
                for (var i = 0; i < source.Length; i += 3)
                {
                    var a = source[i]; var b = source[i + 1]; var c = source[i + 2];
                    var area = UnityEngine.ProBuilder.Math.TriangleArea(raw[a].LocalPosition, raw[b].LocalPosition, raw[c].LocalPosition);
                    if (!(area > Mathf.Epsilon) || handles[a] == handles[b] || handles[a] == handles[c] || handles[b] == handles[c]) continue;
                    if (!canonical.TryGetValue(handles[a], out var ca)) canonical.Add(handles[a], ca = a);
                    if (!canonical.TryGetValue(handles[b], out var cb)) canonical.Add(handles[b], cb = b);
                    if (!canonical.TryGetValue(handles[c], out var cc)) canonical.Add(handles[c], cc = c);
                    stream.Add(ca); stream.Add(cb); stream.Add(cc);
                }
                if (stream.Count > 0) faces.Add(new ExpectedFace(face, stream));
            }
            var used = new HashSet<int>(faces.SelectMany(face => face.RawStream)); var map = new Dictionary<int, int>(); var compact = new List<CanonicalMeshSnapshot.RawVertex>();
            for (var old = 0; old < raw.Length; old++) if (used.Contains(old)) { map.Add(old, compact.Count); compact.Add(CopyRaw(raw[old], raw[old].LocalPosition, compact.Count)); }
            var compactFaces = faces.Select(face => new ExpectedFace(face.Source, face.RawStream.Select(index => map[index]))).ToArray();
            var compactHandles = map.OrderBy(pair => pair.Value).Select(pair => handles[pair.Key]).ToArray();
            var compactTextures = textures.Select(group => group.Where(map.ContainsKey).Select(index => map[index]).ToArray()).Where(group => group.Length > 0);
            return new ReindexedExpected { Mesh = new ExpectedMesh(compact.ToArray(), compactFaces, PartitionsFromHandles(compactHandles), before.HasSharedTextures, compactTextures), OldToNew = map };
        }
        private sealed class ReindexedExpected { internal ExpectedMesh Mesh; internal Dictionary<int, int> OldToNew; }
        private static CanonicalMeshSnapshot.RawVertex CopyRaw(CanonicalMeshSnapshot.RawVertex source, CanonicalMeshSnapshot.RawVertex authoritative)
            => CopyRaw(source, authoritative.LocalPosition, source.RawIndex, authoritative.LocalNormal, authoritative.Tangent, authoritative.Color, authoritative.Uvs);
        private static CanonicalMeshSnapshot.RawVertex CopyRaw(CanonicalMeshSnapshot.RawVertex source, Vector3 position)
            => CopyRaw(source, position, source.RawIndex, source.Color, source.Uvs);
        private static CanonicalMeshSnapshot.RawVertex CopyRaw(CanonicalMeshSnapshot.RawVertex source, Vector3 position, int rawIndex)
            => CopyRaw(source, position, rawIndex, source.Color, source.Uvs);
        private static CanonicalMeshSnapshot.RawVertex CopyRaw(CanonicalMeshSnapshot.RawVertex source, Vector3 position, int rawIndex, Vector3 world)
            => new CanonicalMeshSnapshot.RawVertex(rawIndex, source.SharedHandle, position, world, source.LocalNormal, source.Tangent, source.Color, source.Uvs);
        private static CanonicalMeshSnapshot.RawVertex AverageRaw(CanonicalMeshSnapshot.RawVertex[] raw, int[] requested)
        {
            var selected = requested.Select(index => raw[index]).ToArray();
            return CopyRaw(selected[0], Average(selected.Select(value => value.LocalPosition)), selected[0].RawIndex,
                AverageChannel(selected.Select(value => value.LocalNormal)), AverageChannel(selected.Select(value => value.Tangent)),
                AverageChannel(selected.Select(value => value.Color)), AverageUv(selected, 0, 1), AverageUv(selected, 1, 1), AverageUv(selected, 2, 1), AverageUv(selected, 3, 1));
        }
        private static CanonicalMeshSnapshot.RawVertex CopyRaw(CanonicalMeshSnapshot.RawVertex source, Vector3 position, int rawIndex, CanonicalMeshSnapshot.ChannelValue color, params CanonicalMeshSnapshot.ChannelValue[] uvs)
            => CopyRaw(source, position, rawIndex, source.LocalNormal, source.Tangent, color, uvs);
        private static CanonicalMeshSnapshot.RawVertex CopyRaw(CanonicalMeshSnapshot.RawVertex source, Vector3 position, int rawIndex,
            CanonicalMeshSnapshot.ChannelValue normal, CanonicalMeshSnapshot.ChannelValue tangent, CanonicalMeshSnapshot.ChannelValue color,
            params CanonicalMeshSnapshot.ChannelValue[] uvs)
            => new CanonicalMeshSnapshot.RawVertex(rawIndex, source.SharedHandle, position, source.WorldPosition, normal, tangent, color, uvs);
        private static CanonicalMeshSnapshot.ChannelValue AverageChannel(IEnumerable<CanonicalMeshSnapshot.ChannelValue> values)
        { var present = values.Where(value => value.IsPresent).ToArray(); return present.Length == 0 ? new CanonicalMeshSnapshot.ChannelValue(false, Vector4.zero) : new CanonicalMeshSnapshot.ChannelValue(true, present.Aggregate(Vector4.zero, (sum, value) => sum + value.Value) / present.Length); }
        private static CanonicalMeshSnapshot.ChannelValue AverageUv(CanonicalMeshSnapshot.RawVertex[] values, int channel, int ignored) => AverageChannel(values.Select(value => value.Uvs[channel]));
        private static int[][] SplitRequestedTexturePartitions(CanonicalMeshSnapshot before, int[] requested)
        { var selected = new HashSet<int>(requested); return before.SharedTextures.SelectMany(group => group.MemberRawIndexes.Where(selected.Contains).Select(raw => new[] { raw }).Concat(new[] { group.MemberRawIndexes.Where(raw => !selected.Contains(raw)).ToArray() })).Where(group => group.Length > 0).ToArray(); }
        private static int[][] PartitionsFromHandles(int[] handles) => handles.Select((handle, raw) => new { handle, raw }).GroupBy(value => value.handle).Select(group => group.Select(value => value.raw).ToArray()).ToArray();
        private static int[][] Components(CanonicalMeshSnapshot before, int[] handles, float radius)
        { var remaining = new HashSet<int>(handles); var result = new List<int[]>(); while (remaining.Count > 0) { var root = remaining.Min(); var component = new List<int> { root }; remaining.Remove(root); for (var index = 0; index < component.Count; index++) foreach (var candidate in remaining.Where(candidate => Near(before, component[index], candidate, radius)).ToArray()) { remaining.Remove(candidate); component.Add(candidate); } result.Add(component.OrderBy(value => value).ToArray()); } return result.ToArray(); }
        private static bool SameIdentityAndTransform(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after) => CanonicalMeshSnapshot.IdentityAndTransformEqual(before, after);
        private static bool ExactExpectedEquals(CanonicalMeshSnapshot actual, ExpectedMesh expected)
        {
            if (actual == null || expected == null || actual.HasSharedTextures != expected.HasSharedTextures || actual.RawVertices.Length != expected.Raw.Length || actual.Faces.Length != expected.Faces.Length || !CanonicalMeshSnapshot.DerivedChannelsMatch(actual)) return false;
            for (var index = 0; index < expected.Raw.Length; index++) if (!RawExpectedEquals(actual.RawVertices[index], expected.Raw[index])) return false;
            var remaining = actual.Faces.ToList(); foreach (var face in expected.Faces) { var index = remaining.FindIndex(candidate => FaceExpectedEquals(candidate, face)); if (index < 0) return false; remaining.RemoveAt(index); }
            return PartitionEquals(actual.SharedVertices.Select(group => group.MemberRawIndexes), expected.SharedPartitions) && PartitionEquals(actual.SharedTextures.Select(group => group.MemberRawIndexes), expected.TexturePartitions);
        }
        private static bool RawExpectedEquals(CanonicalMeshSnapshot.RawVertex actual, CanonicalMeshSnapshot.RawVertex expected) => CanonicalMeshSnapshot.ExactEquals(actual.LocalPosition, expected.LocalPosition) && CanonicalMeshSnapshot.RawChannelValueEquals(actual, expected);
        private static bool FaceExpectedEquals(CanonicalMeshSnapshot.FaceOccurrence actual, ExpectedFace expected) => actual.SubmeshIndex == expected.SubmeshIndex && actual.SmoothingGroup == expected.Source.SmoothingGroup && actual.TextureGroup == expected.Source.TextureGroup && actual.ElementGroup == expected.Source.ElementGroup && actual.ManualUv == expected.Source.ManualUv && CanonicalMeshSnapshot.UvValueEquals(actual.Uv, expected.Source.Uv) && CanonicalMeshSnapshot.MaterialValueEquals(actual.Material, expected.Material) && actual.DirectedRawTriangleStream.SequenceEqual(expected.RawStream);
        private static bool PartitionEquals(IEnumerable<int[]> actual, IEnumerable<int[]> expected) => CanonicalMeshSnapshot.ExactMultisetEquals(actual.Select(group => group.OrderBy(value => value).ToArray()), expected.Select(group => group.OrderBy(value => value).ToArray()), (left, right) => left.SequenceEqual(right));
        /// <summary>内部证明辅助逻辑。</summary>
        private static bool ExpectedMeshFinite(CanonicalMeshSnapshot before, ExpectedMesh expected)
        {
            if (before == null || expected == null || expected.Raw == null || expected.Faces == null || expected.Raw.Any(raw => !RawTerminalFinite(before, raw))) return false;
            foreach (var face in expected.Faces)
            {
                var stream = face?.RawStream;
                if (stream == null || stream.Length < 3 || stream.Length % 3 != 0 || stream.Any(raw => raw < 0 || raw >= expected.Raw.Length)) return false;
            }
            return CanonicalMeshSnapshot.RefreshArithmeticFinite(
                expected.Raw.Select(raw => raw.LocalPosition).ToArray(),
                expected.Raw.Select(raw => raw.Uvs[0]).ToArray(),
                expected.Faces.Select(face => face.RawStream),
                expected.Faces.Select(face => face.Source.SmoothingGroup));
        }
        private static bool RawTerminalFinite(CanonicalMeshSnapshot before, CanonicalMeshSnapshot.RawVertex raw)
        {
            if (before == null || raw == null || !Finite(raw.LocalPosition) || !Finite(before.LocalToWorldMatrix.MultiplyPoint3x4(raw.LocalPosition))
                || !Finite(raw.LocalNormal) || !Finite(raw.Tangent) || !Finite(raw.Color)) return false;
            return raw.Uvs.All(Finite);
        }
        private static bool HasUv1(CanonicalMeshSnapshot snapshot) => snapshot != null && snapshot.RawVertices.Any(raw => raw.Uvs[1].IsPresent);
        private static bool Finite(CanonicalMeshSnapshot.ChannelValue value) => value != null && (!value.IsPresent || Finite(value.Value));
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Approximately(Vector3 left, Vector3 right) => (left - right).sqrMagnitude <= 0.00000001f;
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        /// <summary>内部证明辅助逻辑。</summary>
        private static bool HasTriangleAtOrBelowEpsilon(IEnumerable<int> stream, IReadOnlyList<CanonicalMeshSnapshot.RawVertex> raw)
        {
            var indexes = stream.ToArray();
            for (var index = 0; index < indexes.Length; index += 3)
                if (!(UnityEngine.ProBuilder.Math.TriangleArea(raw[indexes[index]].LocalPosition, raw[indexes[index + 1]].LocalPosition, raw[indexes[index + 2]].LocalPosition) > Mathf.Epsilon)) return true;
            return false;
        }

        private static CanonicalMeshSnapshot.SharedVertexSnapshot Shared(CanonicalMeshSnapshot snapshot, int handle) => snapshot.SharedVertices.Single(item => item.Handle == handle);
        private static bool Near(CanonicalMeshSnapshot snapshot, int a, int b, float radius)
        {
            var delta = Shared(snapshot, a).CanonicalPosition - Shared(snapshot, b).CanonicalPosition;
            var squared = delta.sqrMagnitude; var radiusSquared = radius * radius;
            return Finite(delta) && Finite(squared) && Finite(radiusSquared) && squared <= radiusSquared;
        }
        private static Vector3 Average(IEnumerable<Vector3> values) { var all = values.ToArray(); return all.Aggregate(Vector3.zero, (sum, item) => sum + item) / all.Length; }
    }
}
#endif
