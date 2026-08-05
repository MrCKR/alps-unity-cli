#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>ProBuilder completion proof 的不可变规范快照；只读捕获，任何不可读、非有限或映射不完整状态都拒绝证明。</summary>
    internal sealed class CanonicalMeshSnapshot
    {
        /// <summary>effect 前冻结的对象和组件身份；listener generation 在当前接口不可取得，故不伪造该字段。</summary>
        internal sealed class ObjectIdentityClaim
        {
            internal readonly int GameObjectInstanceId, TransformInstanceId, ParentTransformInstanceId, SceneHandle, ProBuilderMeshInstanceId, MeshFilterInstanceId, MeshRendererInstanceId, CompiledMeshInstanceId;
            internal readonly string GlobalObjectId, ScenePath;
            internal ObjectIdentityClaim(GameObject go, ProBuilderMesh mesh, MeshFilter filter, MeshRenderer renderer, Mesh compiled)
            {
                GameObjectInstanceId = go.GetInstanceID(); TransformInstanceId = go.transform.GetInstanceID(); ParentTransformInstanceId = go.transform.parent == null ? 0 : go.transform.parent.GetInstanceID();
                GlobalObjectId = UnityEditor.GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(); SceneHandle = go.scene.handle; ScenePath = go.scene.path;
                ProBuilderMeshInstanceId = mesh.GetInstanceID(); MeshFilterInstanceId = filter.GetInstanceID(); MeshRendererInstanceId = renderer.GetInstanceID(); CompiledMeshInstanceId = compiled.GetInstanceID();
            }
        }

        /// <summary>可选顶点通道同时保存存在性和值；IsPresent=false 时 Value 不具有数据语义。</summary>
        internal sealed class ChannelValue
        {
            internal readonly bool IsPresent;
            internal readonly Vector4 Value;
            internal ChannelValue(bool present, Vector4 value) { IsPresent = present; Value = value; }
        }

        /// <summary>Unity Mesh 的一个 submesh 终态；topology 与 index 流均为完成证明的一部分。</summary>
        internal sealed class CompiledSubmeshSnapshot
        {
            internal readonly MeshTopology Topology;
            private readonly int[] indexes;
            internal int[] Indexes { get { return (int[])indexes.Clone(); } }
            internal CompiledSubmeshSnapshot(MeshTopology topology, IEnumerable<int> values) { Topology = topology; indexes = values.ToArray(); }
        }

        /// <summary>MeshFilter.sharedMesh 的完整不可变终态，用于拒绝 stale 或部分 Refresh。</summary>
        internal sealed class CompiledMeshSnapshot
        {
            internal readonly int InstanceId;
            internal readonly UnityEngine.Rendering.IndexFormat IndexFormat;
            private readonly Vector3[] positions, normals;
            private readonly Vector4[] tangents;
            private readonly Color[] colors;
            private readonly Vector4[][] uvs;
            private readonly CompiledSubmeshSnapshot[] submeshes;
            internal readonly Bounds Bounds;
            internal Vector3[] Positions { get { return (Vector3[])positions.Clone(); } }
            internal Vector3[] Normals { get { return (Vector3[])normals.Clone(); } }
            internal Vector4[] Tangents { get { return (Vector4[])tangents.Clone(); } }
            internal Color[] Colors { get { return (Color[])colors.Clone(); } }
            internal Vector4[][] Uvs { get { return uvs.Select(values => (Vector4[])values.Clone()).ToArray(); } }
            internal CompiledSubmeshSnapshot[] Submeshes { get { return (CompiledSubmeshSnapshot[])submeshes.Clone(); } }
            internal CompiledMeshSnapshot(Mesh mesh, Vector3[] positionValues, Vector3[] normalValues, Vector4[] tangentValues, Color[] colorValues, Vector4[][] uvValues, CompiledSubmeshSnapshot[] submeshValues)
            {
                InstanceId = mesh.GetInstanceID(); IndexFormat = mesh.indexFormat; positions = (Vector3[])positionValues.Clone(); normals = (Vector3[])normalValues.Clone(); tangents = (Vector4[])tangentValues.Clone(); colors = (Color[])colorValues.Clone();
                uvs = uvValues.Select(values => (Vector4[])values.Clone()).ToArray(); submeshes = (CompiledSubmeshSnapshot[])submeshValues.Clone(); Bounds = mesh.bounds;
            }
        }

        /// <summary>raw index 和 shared handle 仅在本快照内定位；跨快照比较必须经 shared value claim，不能直接比较这些数字。</summary>
        internal sealed class RawVertex
        {
            internal readonly int RawIndex, SharedHandle;
            internal readonly Vector3 LocalPosition, WorldPosition;
            internal readonly ChannelValue LocalNormal, Tangent, Color;
            private readonly ChannelValue[] uvs;
            internal ChannelValue[] Uvs { get { return (ChannelValue[])uvs.Clone(); } }
            internal RawVertex(int raw, int handle, Vector3 local, Vector3 world, ChannelValue normal, ChannelValue tangent, ChannelValue color, ChannelValue[] values)
            { RawIndex = raw; SharedHandle = handle; LocalPosition = local; WorldPosition = world; LocalNormal = normal; Tangent = tangent; Color = color; uvs = (ChannelValue[])values.Clone(); }
        }

        /// <summary>shared handle 的成员闭集、原始迭代顺序和规范 local position；每个 raw index 必须恰好属于一个 handle。</summary>
        internal sealed class SharedVertexSnapshot
        {
            internal readonly int Handle;
            private readonly int[] members;
            internal int[] MemberRawIndexes { get { return (int[])members.Clone(); } }
            internal readonly Vector3 CanonicalPosition;
            internal SharedVertexSnapshot(int handle, IEnumerable<int> rawMembers, Vector3 position) { Handle = handle; members = rawMembers.ToArray(); CanonicalPosition = position; }
        }

        /// <summary>effective material 按 5.2.4 的 clamped submesh slot 解析；raw submeshIndex 由 FaceOccurrence 独立保存。</summary>
        internal sealed class MaterialClaim
        {
            internal readonly MaterialState State;
            internal readonly int EffectiveSlot, InstanceId;
            internal readonly bool HasAssetIdentity;
            internal readonly string AssetGuid;
            internal readonly long AssetLocalId;
            internal readonly MaterialClaim SerializedFaceMaterial;
            internal MaterialClaim(MaterialState state, int effectiveSlot, int instanceId, bool hasAsset, string guid, long localId, MaterialClaim serializedFaceMaterial = null)
            { State = state; EffectiveSlot = effectiveSlot; InstanceId = instanceId; HasAssetIdentity = hasAsset; AssetGuid = guid; AssetLocalId = localId; SerializedFaceMaterial = serializedFaceMaterial; }
        }
        internal enum MaterialState { RendererMissing, NoSlots, Null, Object }

        /// <summary>Face.edges 的原始有向引用；raw/shared 数字只供本快照请求映射，跨快照由共享值端点比较。</summary>
        internal sealed class BoundaryReference
        {
            internal readonly int RawA, RawB, SharedA, SharedB;
            internal BoundaryReference(int rawA, int rawB, int sharedA, int sharedB) { RawA = rawA; RawB = rawB; SharedA = sharedA; SharedB = sharedB; }
        }

        /// <summary>occurrence index 是 capture 时的位置，不是跨 effect 身份；面保存原始流以支持同快照请求映射。</summary>
        internal sealed class FaceOccurrence
        {
            internal readonly int OccurrenceIndex, SubmeshIndex, SmoothingGroup, TextureGroup, ElementGroup;
            internal readonly bool ManualUv;
            private readonly int[] directedRawTriangleStream, directedSharedTriangleStream;
            private readonly BoundaryReference[] boundary;
            internal int[] DirectedRawTriangleStream { get { return (int[])directedRawTriangleStream.Clone(); } }
            internal int[] DirectedSharedTriangleStream { get { return (int[])directedSharedTriangleStream.Clone(); } }
            internal BoundaryReference[] Boundary { get { return (BoundaryReference[])boundary.Clone(); } }
            internal readonly AutoUnwrapSettings Uv;
            internal readonly MaterialClaim Material;
            internal FaceOccurrence(int occurrence, Face face, int[] raw, int[] shared, BoundaryReference[] edges, int elementGroup, MaterialClaim material)
            {
                OccurrenceIndex = occurrence; SubmeshIndex = face.submeshIndex; SmoothingGroup = face.smoothingGroup; ManualUv = face.manualUV;
                TextureGroup = face.textureGroup; ElementGroup = elementGroup; directedRawTriangleStream = (int[])raw.Clone(); directedSharedTriangleStream = (int[])shared.Clone();
                boundary = edges.ToArray(); Uv = new AutoUnwrapSettings(face.uv); Material = material;
            }
        }

        /// <summary>每个无向 shared 边保留所有 incident face occurrence 及原有方向；不会因去重丢失非流形重数。</summary>
        internal sealed class SharedEdgeOccurrence
        {
            internal readonly int SharedMin, SharedMax;
            private readonly BoundaryIncidence[] incidences;
            internal BoundaryIncidence[] Incidences { get { return (BoundaryIncidence[])incidences.Clone(); } }
            internal SharedEdgeOccurrence(int min, int max, IEnumerable<BoundaryIncidence> values) { SharedMin = min; SharedMax = max; incidences = values.ToArray(); }
        }
        internal sealed class BoundaryIncidence
        {
            internal readonly int FaceOccurrenceIndex, RawA, RawB, SharedA, SharedB;
            internal BoundaryIncidence(int occurrence, BoundaryReference edge) { FaceOccurrenceIndex = occurrence; RawA = edge.RawA; RawB = edge.RawB; SharedA = edge.SharedA; SharedB = edge.SharedB; }
        }

        internal sealed class SharedTextureSnapshot
        {
            internal readonly int Group;
            private readonly int[] members;
            internal int[] MemberRawIndexes { get { return (int[])members.Clone(); } }
            internal SharedTextureSnapshot(int group, IEnumerable<int> values) { Group = group; members = values.ToArray(); }
        }

        internal readonly ObjectIdentityClaim Identity;
        internal readonly Matrix4x4 LocalToWorldMatrix, WorldToLocalMatrix;
        internal readonly bool HasSharedTextures;
        internal readonly CompiledMeshSnapshot CompiledMesh;
        private readonly MaterialClaim[] rendererMaterials;
        private readonly RawVertex[] rawVertices;
        private readonly SharedVertexSnapshot[] sharedVertices;
        private readonly SharedTextureSnapshot[] sharedTextures;
        private readonly FaceOccurrence[] faces;
        private readonly SharedEdgeOccurrence[] edges;
        private readonly int[] touchedOccurrences;
        internal MaterialClaim[] RendererMaterials { get { return (MaterialClaim[])rendererMaterials.Clone(); } }
        internal RawVertex[] RawVertices { get { return (RawVertex[])rawVertices.Clone(); } }
        internal SharedVertexSnapshot[] SharedVertices { get { return (SharedVertexSnapshot[])sharedVertices.Clone(); } }
        internal SharedTextureSnapshot[] SharedTextures { get { return (SharedTextureSnapshot[])sharedTextures.Clone(); } }
        internal FaceOccurrence[] Faces { get { return (FaceOccurrence[])faces.Clone(); } }
        internal SharedEdgeOccurrence[] Edges { get { return (SharedEdgeOccurrence[])edges.Clone(); } }
        internal int[] TouchedOccurrences { get { return (int[])touchedOccurrences.Clone(); } }
        private CanonicalMeshSnapshot(ObjectIdentityClaim identity, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, MaterialClaim[] materialSlots, RawVertex[] raw, SharedVertexSnapshot[] shared, bool hasSharedTextures, SharedTextureSnapshot[] textureGroups, FaceOccurrence[] faceValues, SharedEdgeOccurrence[] edgeValues, int[] touched, CompiledMeshSnapshot compiled)
        {
            Identity = identity; LocalToWorldMatrix = localToWorld; WorldToLocalMatrix = worldToLocal; rendererMaterials = (MaterialClaim[])materialSlots.Clone(); rawVertices = (RawVertex[])raw.Clone(); sharedVertices = (SharedVertexSnapshot[])shared.Clone();
            HasSharedTextures = hasSharedTextures; sharedTextures = (SharedTextureSnapshot[])textureGroups.Clone(); faces = (FaceOccurrence[])faceValues.Clone(); edges = (SharedEdgeOccurrence[])edgeValues.Clone(); touchedOccurrences = (int[])touched.Clone(); CompiledMesh = compiled;
        }

        /// <summary>从 canonical raw 通道构造纯 managed clone，禁止 GetVertices 物化 ProBuilder 的 serialized normals/tangents 缓存。</summary>
        internal static List<Vertex> BuildManagedVertices(CanonicalMeshSnapshot snapshot)
        {
            if (snapshot == null || snapshot.RawVertices == null) return null;
            var result = new List<Vertex>(snapshot.RawVertices.Length);
            foreach (var raw in snapshot.RawVertices)
            {
                var vertex = new Vertex { position = raw.LocalPosition };
                if (raw.LocalNormal.IsPresent) vertex.normal = new Vector3(raw.LocalNormal.Value.x, raw.LocalNormal.Value.y, raw.LocalNormal.Value.z);
                if (raw.Tangent.IsPresent) vertex.tangent = raw.Tangent.Value;
                if (raw.Color.IsPresent) vertex.color = new Color(raw.Color.Value.x, raw.Color.Value.y, raw.Color.Value.z, raw.Color.Value.w);
                var uvs = raw.Uvs;
                if (uvs[0].IsPresent) vertex.uv0 = new Vector2(uvs[0].Value.x, uvs[0].Value.y);
                if (uvs[1].IsPresent) vertex.uv2 = new Vector2(uvs[1].Value.x, uvs[1].Value.y);
                if (uvs[2].IsPresent) vertex.uv3 = uvs[2].Value;
                if (uvs[3].IsPresent) vertex.uv4 = uvs[3].Value;
                result.Add(vertex);
            }
            return result;
        }

        /// <summary>验证 serialized ProBuilder 格式与当前锁定的 5.2.4 profile 一致；反射不可读时 fail closed。</summary>
        internal static bool CurrentMeshFormat(ProBuilderMesh mesh)
        {
            try
            {
                var field = typeof(ProBuilderMesh).GetField("m_MeshFormatVersion", BindingFlags.Instance | BindingFlags.NonPublic);
                return mesh != null && field != null && field.FieldType == typeof(int) && field.GetValue(mesh) is int version && version == 2;
            }
            catch { return false; }
        }

        /// <summary>总是捕获全部 face occurrence 和完整 shared-edge incidence；touched 只决定哪些面必须通过非退化和单一简单环预检。</summary>
        internal static CanonicalMeshSnapshot Capture(GameObject go, int[] touchedFaceOccurrences = null)
        {
            return CaptureCore(go, touchedFaceOccurrences, false, false);
        }

        /// <summary>weld effect 后专用的宽松 fresh capture；仍验证所有原始流，只允许 shared 折叠后的退化边存在。</summary>
        internal static CanonicalMeshSnapshot CaptureRelaxedAfterWeld(GameObject go)
        {
            return CaptureCore(go, null, true, false);
        }

        /// <summary>merge-vertices effect 后专用 fresh capture；只额外允许 5.2.4 删除全部 face/raw 后形成空 mesh。</summary>
        internal static CanonicalMeshSnapshot CaptureAfterMergeVertices(GameObject go)
        {
            return CaptureCore(go, null, false, true);
        }

        private static CanonicalMeshSnapshot CaptureCore(GameObject go, int[] touchedFaceOccurrences, bool allowCollapsedSharedEdges, bool allowEmptyMesh)
        {
            var mesh = go == null ? null : go.GetComponent<ProBuilderMesh>();
            var filter = go == null ? null : go.GetComponent<MeshFilter>(); var renderer = go == null ? null : go.GetComponent<MeshRenderer>(); var compiledMesh = filter == null ? null : filter.sharedMesh;
            if (go == null || mesh == null || filter == null || renderer == null || compiledMesh == null || !go.scene.IsValid() || !go.scene.isLoaded || !Finite(go.transform.localToWorldMatrix) || !Finite(go.transform.worldToLocalMatrix)
                || !CurrentMeshFormat(mesh) || !CompiledMeshBindingMatches(mesh, compiledMesh)) return null;
            var positions = mesh.positions; var allFaces = mesh.faces; var groups = mesh.sharedVertices;
            if (positions == null || (!allowEmptyMesh && positions.Count == 0) || allFaces == null || groups == null) return null;
            var touched = SelectOccurrences(allFaces.Count, touchedFaceOccurrences); if (touched == null) return null;
            var touchedSet = new HashSet<int>(touched);
            var handles = Enumerable.Repeat(-1, positions.Count).ToArray(); var shared = new List<SharedVertexSnapshot>();
            for (var handle = 0; handle < groups.Count; handle++)
            {
                var group = groups[handle]; if (group == null || group.Count == 0) return null;
                var members = group.ToArray(); if (members.Any(raw => raw < 0 || raw >= positions.Count || handles[raw] >= 0)) return null;
                var canonical = positions[members[0]];
                if (!Finite(canonical) || members.Any(raw => !Finite(positions[raw]) || !ExactEquals(positions[raw], canonical))) return null;
                foreach (var raw in members) handles[raw] = handle;
                shared.Add(new SharedVertexSnapshot(handle, members, canonical));
            }
            if (handles.Any(handle => handle < 0)) return null;
            if (!TryReadSharedTextures(mesh, positions.Count, out var hasSharedTextures, out var sharedTextureGroups)) return null;
            var normals = ReadOptional(mesh.normals, positions.Count); var tangents = ReadOptional(mesh.tangents, positions.Count); var colors = ReadOptional(mesh.colors, positions.Count); var uv = ReadUvChannels(mesh, go, positions.Count);
            if (normals == null || tangents == null || colors == null || uv == null) return null;
            var rawVertices = new RawVertex[positions.Count];
            for (var raw = 0; raw < positions.Count; raw++)
            {
                var world = go.transform.TransformPoint(positions[raw]); if (!Finite(positions[raw]) || !Finite(world)) return null;
                rawVertices[raw] = new RawVertex(raw, handles[raw], positions[raw], world, normals[raw], tangents[raw], colors[raw], uv.Select(channel => channel[raw]).ToArray());
            }
            var materials = renderer.sharedMaterials;
            if (materials == null) return null;
            var serializedMaterialField = typeof(Face).GetField("m_Material", BindingFlags.Instance | BindingFlags.NonPublic);
            if (serializedMaterialField == null || serializedMaterialField.FieldType != typeof(Material)) return null;
            var materialSlots = materials.Select((material, slot) => CaptureMaterialSlot(material, slot)).ToArray();
            var captured = new List<FaceOccurrence>();
            for (var occurrence = 0; occurrence < allFaces.Count; occurrence++)
            {
                var face = allFaces[occurrence]; if (face == null || face.indexes == null || face.indexes.Count < 3 || face.indexes.Count % 3 != 0 || !Finite(face.uv)) return null;
                var triangles = face.indexes.ToArray(); if (triangles.Any(raw => raw < 0 || raw >= rawVertices.Length)) return null;
                // 未触及面仍需完整保留 raw/triangle/属性；其既有 shared 折叠边不能阻断无关请求。
                var touchedFace = touchedSet.Contains(occurrence);
                var boundary = CaptureBoundary(triangles, handles, allowCollapsedSharedEdges || !touchedFace); var elementGroup = ReadElementGroup(face);
                if (boundary == null || !elementGroup.HasValue) return null;
                if (!allowCollapsedSharedEdges && touchedFace && (HasDegenerateTriangle(triangles, positions) || !HasSimpleConnectedBoundary(boundary))) return null;
                var material = WithSerializedFaceMaterial(CaptureMaterial(renderer, materials, face.submeshIndex), serializedMaterialField.GetValue(face) as Material);
                captured.Add(new FaceOccurrence(occurrence, face, triangles, triangles.Select(raw => handles[raw]).ToArray(), boundary, elementGroup.Value, material));
            }
            var compiled = CaptureCompiledMesh(compiledMesh, renderer, rawVertices, captured.ToArray(), allowEmptyMesh);
            if (compiled == null) return null;
            return new CanonicalMeshSnapshot(new ObjectIdentityClaim(go, mesh, filter, renderer, compiledMesh), go.transform.localToWorldMatrix, go.transform.worldToLocalMatrix, materialSlots, rawVertices, shared.ToArray(), hasSharedTextures, sharedTextureGroups, captured.ToArray(), BuildEdges(captured), touched, compiled);
        }

        /// <summary>fresh 解析只能校验 effect 前 claim，候选 target 不能替代对象、场景或组件身份。</summary>
        internal static ProBuilderMesh ResolveFreshMesh(string target, ObjectIdentityClaim claim)
        {
            var go = ProBuilderInput.ResolveTarget(target); var mesh = go == null ? null : go.GetComponent<ProBuilderMesh>(); var filter = go == null ? null : go.GetComponent<MeshFilter>(); var renderer = go == null ? null : go.GetComponent<MeshRenderer>(); var compiled = filter == null ? null : filter.sharedMesh;
            return claim == null || go == null || mesh == null || filter == null || renderer == null || compiled == null || !go.scene.IsValid() || !go.scene.isLoaded
                || go.GetInstanceID() != claim.GameObjectInstanceId || go.transform.GetInstanceID() != claim.TransformInstanceId || (go.transform.parent == null ? 0 : go.transform.parent.GetInstanceID()) != claim.ParentTransformInstanceId
                || mesh.GetInstanceID() != claim.ProBuilderMeshInstanceId || filter.GetInstanceID() != claim.MeshFilterInstanceId || renderer.GetInstanceID() != claim.MeshRendererInstanceId || compiled.GetInstanceID() != claim.CompiledMeshInstanceId
                || go.scene.handle != claim.SceneHandle || !string.Equals(go.scene.path, claim.ScenePath, StringComparison.Ordinal) || !string.Equals(UnityEditor.GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(), claim.GlobalObjectId, StringComparison.Ordinal) ? null : mesh;
        }

        /// <summary>同一 snapshot 内的有向三角形 helper 只允许循环旋转，不允许反向 winding 或排序。</summary>
        internal static bool DirectedTriangleCyclicEquals(IList<int> left, IList<int> right)
        {
            if (left == null || right == null || left.Count != 3 || right.Count != 3) return false;
            for (var offset = 0; offset < 3; offset++) if (left[0] == right[offset] && left[1] == right[(offset + 1) % 3] && left[2] == right[(offset + 2) % 3]) return true;
            return false;
        }
        /// <summary>同一 snapshot 的数字三角流逐段比较，供请求映射使用，不能作为跨 snapshot preservation 的身份比较。</summary>
        internal static bool DirectedTriangleStreamEquals(IList<int> left, IList<int> right)
        {
            if (left == null || right == null || left.Count != right.Count || left.Count % 3 != 0) return false;
            for (var index = 0; index < left.Count; index += 3) if (!DirectedTriangleCyclicEquals(new[] { left[index], left[index + 1], left[index + 2] }, new[] { right[index], right[index + 1], right[index + 2] })) return false;
            return true;
        }
        /// <summary>预演 5.2.4 CalculateNormals 的全 triangle normal，防止有限坐标差值或叉乘溢出。</summary>
        internal static bool NormalArithmeticFinite(IList<Vector3> positions, IEnumerable<int[]> streams)
        {
            if (positions == null || streams == null || positions.Count == 0 || positions.Any(value => !Finite(value))) return false;
            foreach (var stream in streams)
            {
                if (stream == null || stream.Length < 3 || stream.Length % 3 != 0 || stream.Any(raw => raw < 0 || raw >= positions.Count)) return false;
                for (var index = 0; index < stream.Length; index += 3)
                    if (!Finite(UnityEngine.ProBuilder.Math.Normal(positions[stream[index]], positions[stream[index + 1]], positions[stream[index + 2]]))) return false;
            }
            return true;
        }

        /// <summary>逐句预演 5.2.4 CalculateTangents 的倒数、方向与 raw 累加；任一步非有限即禁止进入 effect。</summary>
        internal static bool TangentArithmeticFinite(IList<Vector3> positions, IList<Vector2> uvs, IEnumerable<int[]> streams)
        {
            if (positions == null || uvs == null || streams == null || positions.Count == 0 || positions.Count != uvs.Count || positions.Any(value => !Finite(value)) || uvs.Any(value => !Finite(value))) return false;
            var tan1 = new Vector3[positions.Count]; var tan2 = new Vector3[positions.Count];
            foreach (var stream in streams)
            {
                if (stream == null || stream.Length < 3 || stream.Length % 3 != 0 || stream.Any(raw => raw < 0 || raw >= positions.Count)) return false;
                for (var index = 0; index < stream.Length; index += 3)
                {
                    var i1 = stream[index]; var i2 = stream[index + 1]; var i3 = stream[index + 2];
                    var v1 = positions[i1]; var v2 = positions[i2]; var v3 = positions[i3]; var w1 = uvs[i1]; var w2 = uvs[i2]; var w3 = uvs[i3];
                    var x1 = v2.x - v1.x; var x2 = v3.x - v1.x; var y1 = v2.y - v1.y; var y2 = v3.y - v1.y; var z1 = v2.z - v1.z; var z2 = v3.z - v1.z;
                    var s1 = w2.x - w1.x; var s2 = w3.x - w1.x; var t1 = w2.y - w1.y; var t2 = w3.y - w1.y; var r = 1.0f / (s1 * t2 - s2 * t1);
                    var sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
                    var tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);
                    if (!IsFinite(r) || !Finite(sdir) || !Finite(tdir)) return false;
                    foreach (var raw in new[] { i1, i2, i3 })
                    {
                        var next1 = tan1[raw] + sdir; var next2 = tan2[raw] + tdir;
                        if (!Finite(next1) || !Finite(next2)) return false;
                        tan1[raw] = next1; tan2[raw] = next2;
                    }
                }
            }
            return true;
        }

        /// <summary>统一验证会触发全网格 Refresh 的预测终态；positive smoothing lookup、normal 与 tangent 必须全部有界。</summary>
        internal static bool RefreshArithmeticFinite(CanonicalMeshSnapshot snapshot, bool allowAbsentUv0 = false)
        {
            return snapshot != null && RefreshArithmeticFinite(
                snapshot.RawVertices.Select(raw => raw.LocalPosition).ToArray(),
                snapshot.RawVertices.Select(raw => raw.Uvs[0]).ToArray(),
                snapshot.Faces.Select(face => face.DirectedRawTriangleStream),
                snapshot.Faces.Select(face => face.SmoothingGroup),
                allowAbsentUv0);
        }
        /// <summary>payload-preserving refresh 可接受全缺 UV0；部分缺失仍不是合法的 ProBuilder channel profile。</summary>
        internal static bool RefreshArithmeticFinite(IList<Vector3> positions, IList<ChannelValue> uv0, IEnumerable<int[]> streams, IEnumerable<int> smoothingGroups, bool allowAbsentUv0 = false)
        {
            if (positions == null || uv0 == null || streams == null || smoothingGroups == null || positions.Count != uv0.Count
                || uv0.Any(value => value == null)) return false;
            var allUv0Absent = uv0.All(value => !value.IsPresent);
            if (allUv0Absent ? !allowAbsentUv0 : uv0.Any(value => !value.IsPresent || !Finite(value.Value))) return false;
            var frozenStreams = streams.Select(stream => stream == null ? null : stream.ToArray()).ToArray();
            var groups = smoothingGroups.ToArray();
            var referenced = new HashSet<int>(frozenStreams.Where(stream => stream != null).SelectMany(stream => stream));
            return referenced.Count == positions.Count && referenced.All(raw => raw >= 0 && raw < positions.Count)
                && groups.All(group => group <= ushort.MaxValue)
                && NormalArithmeticFinite(positions, frozenStreams)
                && (allUv0Absent || TangentArithmeticFinite(positions, uv0.Select(value => (Vector2)value.Value).ToArray(), frozenStreams));
        }

        /// <summary>验证 effect 前预测终态能被 strict canonical capture 完整读取；非逐值相等的 shared 合并、退化三角或非简单边界一律拒绝。</summary>
        internal static bool TerminalTopologyReadable(IList<Vector3> positions, IEnumerable<int[]> streams, IEnumerable<int[]> sharedPartitions)
        {
            if (positions == null || streams == null || sharedPartitions == null || positions.Count == 0 || positions.Any(value => !Finite(value))) return false;
            var handles = Enumerable.Repeat(-1, positions.Count).ToArray();
            var groups = sharedPartitions.Select(group => group == null ? null : group.ToArray()).ToArray();
            for (var handle = 0; handle < groups.Length; handle++)
            {
                var members = groups[handle];
                if (members == null || members.Length == 0) return false;
                var canonical = members[0] < 0 || members[0] >= positions.Count ? Vector3.zero : positions[members[0]];
                if (!Finite(canonical)) return false;
                foreach (var raw in members)
                {
                    if (raw < 0 || raw >= positions.Count || handles[raw] >= 0 || !ExactEquals(positions[raw], canonical)) return false;
                    handles[raw] = handle;
                }
            }
            if (handles.Any(handle => handle < 0)) return false;
            foreach (var stream in streams)
            {
                if (stream == null || stream.Length < 3 || stream.Length % 3 != 0 || stream.Any(raw => raw < 0 || raw >= positions.Count) || HasDegenerateTriangle(stream, positions)) return false;
                var boundary = CaptureBoundary(stream, handles);
                if (boundary == null || !HasSimpleConnectedBoundary(boundary)) return false;
            }
            return true;
        }

        /// <summary>逐项移除的 exact multiset/bijection；hash 只能候选加速，绝不能成为最终相等结论。</summary>
        internal static bool ExactMultisetEquals<T>(IEnumerable<T> left, IEnumerable<T> right, Func<T, T, bool> equals)
        {
            if (left == null || right == null || equals == null) return false;
            var leftValues = left.ToArray(); var rightValues = right.ToArray();
            if (leftValues.Length != rightValues.Length) return false;
            var matchedLeftByRight = Enumerable.Repeat(-1, rightValues.Length).ToArray();
            for (var leftIndex = 0; leftIndex < leftValues.Length; leftIndex++)
                if (!TryAugment(leftIndex, leftValues, rightValues, equals, matchedLeftByRight, new bool[rightValues.Length])) return false;
            return true;
        }
        /// <summary>重复值的 multiset 必须用增广路径逐项配对，不能重复消费同一候选。</summary>
        private static bool TryAugment<T>(int leftIndex, T[] left, T[] right, Func<T, T, bool> equals, int[] matchedLeftByRight, bool[] visitedRight)
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
        /// <summary>跨 snapshot 的面全属性比较只使用 value claim；occurrence/raw/shared 数字均不是稳定身份，三角形列表可重排但保留 winding 与重数。</summary>
        internal static bool FaceFullAttributeEquals(CanonicalMeshSnapshot leftSnapshot, FaceOccurrence left, CanonicalMeshSnapshot rightSnapshot, FaceOccurrence right)
        {
            return leftSnapshot != null && rightSnapshot != null && left != null && right != null && left.SubmeshIndex == right.SubmeshIndex && left.SmoothingGroup == right.SmoothingGroup
                && left.ManualUv == right.ManualUv && left.TextureGroup == right.TextureGroup && left.ElementGroup == right.ElementGroup && MaterialValueEquals(left.Material, right.Material)
                && UvValueEquals(left.Uv, right.Uv) && DirectedTriangleValueMultisetEquals(leftSnapshot, left, rightSnapshot, right) && BoundaryValueMultisetEquals(leftSnapshot, left, rightSnapshot, right);
        }
        /// <summary>auto-smooth 专用比较：忽略会被框架重算的 normal 和请求允许变化的 smoothingGroup，其余 face/vertex 值必须完整相等。</summary>
        internal static bool FaceEqualsIgnoringSmoothingAndNormals(CanonicalMeshSnapshot leftSnapshot, FaceOccurrence left, CanonicalMeshSnapshot rightSnapshot, FaceOccurrence right)
        {
            return leftSnapshot != null && rightSnapshot != null && left != null && right != null && left.SubmeshIndex == right.SubmeshIndex
                && left.ManualUv == right.ManualUv && left.TextureGroup == right.TextureGroup && left.ElementGroup == right.ElementGroup
                && MaterialValueEquals(left.Material, right.Material) && UvValueEquals(left.Uv, right.Uv)
                && DirectedTriangleValueMultisetEqualsIgnoringNormals(leftSnapshot, left, rightSnapshot, right)
                && BoundaryValueMultisetEqualsIgnoringNormals(leftSnapshot, left, rightSnapshot, right);
        }

        /// <summary>从完整 fresh face multiset 逐一消费每个 effect 前未选面；after occurrence 不稳定，不能再按原数字过滤。</summary>
        internal static bool UnselectedFacesExactlyPreserved(CanonicalMeshSnapshot before, CanonicalMeshSnapshot after, IEnumerable<int> selectedOccurrences)
        {
            if (before == null || after == null || selectedOccurrences == null) return false;
            var selected = new HashSet<int>(selectedOccurrences); var remaining = new List<FaceOccurrence>(after.faces);
            foreach (var source in before.faces.Where(face => !selected.Contains(face.OccurrenceIndex)))
            {
                var index = remaining.FindIndex(candidate => FaceFullAttributeEquals(before, source, after, candidate));
                if (index < 0) return false;
                remaining.RemoveAt(index);
            }
            return true;
        }
        /// <summary>调用方可按操作语义要求 transform 不变；矩阵和 identity 均逐值精确比较。</summary>
        internal static bool TransformMatricesEqual(CanonicalMeshSnapshot left, CanonicalMeshSnapshot right) => left != null && right != null && ExactEquals(left.LocalToWorldMatrix, right.LocalToWorldMatrix) && ExactEquals(left.WorldToLocalMatrix, right.WorldToLocalMatrix);
        /// <summary>不改变对象身份的 mesh operation 必须保持对象、父级、组件、compiled Mesh、场景与 transform identity。</summary>
        internal static bool IdentityAndTransformEqual(CanonicalMeshSnapshot left, CanonicalMeshSnapshot right)
        {
            if (left == null || right == null || left.Identity == null || right.Identity == null) return false;
            var a = left.Identity; var b = right.Identity;
            return a.GameObjectInstanceId == b.GameObjectInstanceId && a.TransformInstanceId == b.TransformInstanceId && a.ParentTransformInstanceId == b.ParentTransformInstanceId
                && a.SceneHandle == b.SceneHandle && a.ProBuilderMeshInstanceId == b.ProBuilderMeshInstanceId && a.MeshFilterInstanceId == b.MeshFilterInstanceId
                && a.MeshRendererInstanceId == b.MeshRendererInstanceId && a.CompiledMeshInstanceId == b.CompiledMeshInstanceId
                && string.Equals(a.GlobalObjectId, b.GlobalObjectId, StringComparison.Ordinal) && string.Equals(a.ScenePath, b.ScenePath, StringComparison.Ordinal)
                && TransformMatricesEqual(left, right);
        }
        /// <summary>renderer slots 按顺序和 material identity 全量比较；face 为零时仍保留该契约。</summary>
        internal static bool RendererMaterialsEqual(CanonicalMeshSnapshot left, CanonicalMeshSnapshot right)
        {
            if (left == null || right == null) return false;
            var a = left.rendererMaterials; var b = right.rendererMaterials;
            return a.Length == b.Length && a.Zip(b, MaterialValueEquals).All(value => value);
        }

        /// <summary>逐句复演 ProBuilder 5.2.4 Normals.CalculateNormals/CalculateTangents，拒绝 self-consistent 但非 package terminal 的通道。</summary>
        internal static bool DerivedChannelsMatch(CanonicalMeshSnapshot snapshot)
            => DerivedChannelsMatch(snapshot, snapshot == null ? 0 : snapshot.rawVertices.Length);

        /// <summary>repair 前只允许尾部 raw 未被 face 引用；其通道仍须有限并与 compiled Mesh 精确一致。</summary>
        internal static bool DerivedChannelsMatchReferencedPrefix(CanonicalMeshSnapshot snapshot, int referencedPrefixLength)
            => DerivedChannelsMatch(snapshot, referencedPrefixLength);

        private static bool DerivedChannelsMatch(CanonicalMeshSnapshot snapshot, int referencedPrefixLength)
        {
            if (snapshot == null) return false;
            var raw = snapshot.rawVertices;
            if (raw.Length == 0) return referencedPrefixLength == 0 && snapshot.faces.Length == 0;
            if (snapshot.faces.Length == 0 || referencedPrefixLength <= 0 || referencedPrefixLength > raw.Length) return false;
            var normals = new Vector3[raw.Length]; var counts = new int[raw.Length]; var smoothingByRaw = new int[raw.Length];
            foreach (var face in snapshot.faces)
            {
                var stream = face.DirectedRawTriangleStream;
                for (var index = 0; index < stream.Length; index += 3)
                {
                    var a = stream[index]; var b = stream[index + 1]; var c = stream[index + 2];
                    var normal = UnityEngine.ProBuilder.Math.Normal(raw[a].LocalPosition, raw[b].LocalPosition, raw[c].LocalPosition); normal.Normalize();
                    if (!Finite(normal)) return false;
                    normals[a] += normal; normals[b] += normal; normals[c] += normal; counts[a]++; counts[b]++; counts[c]++;
                }
                foreach (var index in stream.Distinct()) smoothingByRaw[index] = face.SmoothingGroup;
            }
            if (counts.Take(referencedPrefixLength).Any(count => count == 0) || counts.Skip(referencedPrefixLength).Any(count => count != 0)) return false;
            for (var index = 0; index < referencedPrefixLength; index++) normals[index] /= counts[index];
            foreach (var shared in snapshot.sharedVertices)
            {
                var sharedMembers = shared.MemberRawIndexes;
                var smoothGroups = sharedMembers.Where(index => index < referencedPrefixLength).Select(index => smoothingByRaw[index]).Where(group => group > 0 && (group <= 24 || group >= 42)).Distinct().ToArray();
                foreach (var group in smoothGroups)
                {
                    var members = sharedMembers.Where(index => index < referencedPrefixLength && smoothingByRaw[index] == group).ToArray();
                    var average = members.Aggregate(Vector3.zero, (sum, index) => sum + normals[index]) / members.Length;
                    average = EnsureUnitVector(average);
                    foreach (var index in members) normals[index] = average;
                }
            }
            for (var index = 0; index < raw.Length; index++)
            {
                var actual = raw[index].LocalNormal;
                if (actual == null || !actual.IsPresent || !Finite(actual.Value) || index < referencedPrefixLength && !ExactEquals(actual.Value, (Vector4)normals[index])) return false;
            }

            var uv0 = raw.Select(value => value.Uvs[0]).ToArray();
            if (uv0.All(value => !value.IsPresent))
                return raw.Select((value, index) => value.Tangent != null && value.Tangent.IsPresent && Finite(value.Tangent.Value) && (index >= referencedPrefixLength || ExactEquals(value.Tangent.Value, Vector4.zero))).All(value => value);
            if (uv0.Any(value => !value.IsPresent)) return false;
            var tan1 = new Vector3[raw.Length]; var tan2 = new Vector3[raw.Length];
            foreach (var face in snapshot.faces)
            {
                var stream = face.DirectedRawTriangleStream;
                for (var index = 0; index < stream.Length; index += 3)
                {
                    var a = stream[index]; var b = stream[index + 1]; var c = stream[index + 2];
                    var v1 = raw[a].LocalPosition; var v2 = raw[b].LocalPosition; var v3 = raw[c].LocalPosition;
                    var w1 = uv0[a].Value; var w2 = uv0[b].Value; var w3 = uv0[c].Value;
                    var x1 = v2.x - v1.x; var x2 = v3.x - v1.x; var y1 = v2.y - v1.y; var y2 = v3.y - v1.y; var z1 = v2.z - v1.z; var z2 = v3.z - v1.z;
                    var s1 = w2.x - w1.x; var s2 = w3.x - w1.x; var t1 = w2.y - w1.y; var t2 = w3.y - w1.y; var reciprocal = 1f / (s1 * t2 - s2 * t1);
                    var sdir = new Vector3((t2 * x1 - t1 * x2) * reciprocal, (t2 * y1 - t1 * y2) * reciprocal, (t2 * z1 - t1 * z2) * reciprocal);
                    var tdir = new Vector3((s1 * x2 - s2 * x1) * reciprocal, (s1 * y2 - s2 * y1) * reciprocal, (s1 * z2 - s2 * z1) * reciprocal);
                    tan1[a] += sdir; tan1[b] += sdir; tan1[c] += sdir; tan2[a] += tdir; tan2[b] += tdir; tan2[c] += tdir;
                }
            }
            for (var index = 0; index < raw.Length; index++)
            {
                var actual = raw[index].Tangent;
                if (actual == null || !actual.IsPresent || !Finite(actual.Value)) return false;
                if (index >= referencedPrefixLength) continue;
                var normal = normals[index]; var tangent = EnsureUnitVector(tan1[index]); Vector3.OrthoNormalize(ref normal, ref tangent);
                var expected = new Vector4(tangent.x, tangent.y, tangent.z, Vector3.Dot(Vector3.Cross(normal, tangent), tan2[index]) < 0f ? -1f : 1f);
                if (!Finite(expected) || !ExactEquals(actual.Value, expected)) return false;
            }
            return true;
        }

        private static Vector3 EnsureUnitVector(Vector3 value) => Mathf.Abs(value.sqrMagnitude) < float.Epsilon ? Vector3.up : value.normalized;

        /// <summary>terminal 数值逐分量精确相等；非有限值一律拒绝。</summary>
        internal static bool ExactEquals(float left, float right) => IsFinite(left) && IsFinite(right) && left == right;
        internal static bool ExactEquals(Vector2 left, Vector2 right) => ExactEquals(left.x, right.x) && ExactEquals(left.y, right.y);
        internal static bool ExactEquals(Vector3 left, Vector3 right) => ExactEquals(left.x, right.x) && ExactEquals(left.y, right.y) && ExactEquals(left.z, right.z);
        internal static bool ExactEquals(Vector4 left, Vector4 right) => ExactEquals(left.x, right.x) && ExactEquals(left.y, right.y) && ExactEquals(left.z, right.z) && ExactEquals(left.w, right.w);
        internal static bool ExactEquals(Matrix4x4 left, Matrix4x4 right) { for (var i = 0; i < 16; i++) if (!ExactEquals(left[i], right[i])) return false; return true; }

        /// <summary>null 表示整网格操作；空数组表示只捕获拓扑而不声明任何被请求消费的面。</summary>
        private static int[] SelectOccurrences(int count, int[] requested) => requested == null ? Enumerable.Range(0, count).ToArray() : requested.Distinct().Count() != requested.Length || requested.Any(index => index < 0 || index >= count) ? null : requested.ToArray();

        /// <summary>完整捕获 compiled Mesh，并证明它是当前 raw/face/material 状态的精确三角形投影。</summary>
        private static CompiledMeshSnapshot CaptureCompiledMesh(Mesh mesh, MeshRenderer renderer, RawVertex[] raw, FaceOccurrence[] faceValues, bool allowEmptyMesh)
        {
            try
            {
                if (mesh == null || renderer == null || raw == null || faceValues == null || mesh.vertexCount != raw.Length) return null;
                var expectedIndexFormat = raw.Length > ushort.MaxValue ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
                if (mesh.indexFormat != expectedIndexFormat) return null;
                var positions = mesh.vertices; var normals = mesh.normals; var tangents = mesh.tangents; var colors = mesh.colors;
                if (!CompiledPositionsMatch(positions, raw) || !CompiledChannelMatches(normals, raw.Select(value => value.LocalNormal).ToArray())
                    || !CompiledChannelMatches(tangents, raw.Select(value => value.Tangent).ToArray()) || !CompiledChannelMatches(colors, raw.Select(value => value.Color).ToArray())) return null;
                var uvs = new Vector4[4][];
                for (var channel = 0; channel < 4; channel++) { var values = new List<Vector4>(); mesh.GetUVs(channel, values); uvs[channel] = values.ToArray(); if (!CompiledChannelMatches(uvs[channel], raw.Select(value => value.Uvs[channel]).ToArray())) return null; }

                var materials = renderer.sharedMaterials;
                if (raw.Length == 0 && faceValues.Length == 0 && allowEmptyMesh)
                {
                    if (mesh.subMeshCount != 0 || positions.Length != 0 || !BoundsEqual(mesh.bounds, new Bounds(Vector3.zero, Vector3.zero))) return null;
                    return new CompiledMeshSnapshot(mesh, positions, normals, tangents, colors, uvs, Array.Empty<CompiledSubmeshSnapshot>());
                }
                if (faceValues.Length == 0 || materials == null || materials.Length == 0 || mesh.subMeshCount != materials.Length
                    || faceValues.Any(face => face.SubmeshIndex < 0 || face.SubmeshIndex >= materials.Length)
                    || Enumerable.Range(0, materials.Length).Any(slot => !faceValues.Any(face => face.SubmeshIndex == slot))) return null;
                var submeshes = new CompiledSubmeshSnapshot[mesh.subMeshCount];
                for (var slot = 0; slot < submeshes.Length; slot++)
                {
                    var topology = mesh.GetTopology(slot); var indexes = mesh.GetIndices(slot); var expected = faceValues.Where(face => face.SubmeshIndex == slot).SelectMany(face => face.DirectedRawTriangleStream).ToArray();
                    if (topology != MeshTopology.Triangles || !indexes.SequenceEqual(expected)) return null;
                    submeshes[slot] = new CompiledSubmeshSnapshot(topology, indexes);
                }
                var expectedBounds = BoundsFromPositions(raw.Select(value => value.LocalPosition).ToArray());
                return expectedBounds.HasValue && BoundsEqual(mesh.bounds, expectedBounds.Value) ? new CompiledMeshSnapshot(mesh, positions, normals, tangents, colors, uvs, submeshes) : null;
            }
            catch { return null; }
        }

        /// <summary>ToMesh 使用 serialized m_Mesh；它必须与当前 MeshFilter.sharedMesh 为同一对象，禁止 barrier 后隐式换绑。</summary>
        private static bool CompiledMeshBindingMatches(ProBuilderMesh mesh, Mesh compiledMesh)
        {
            try
            {
                var field = typeof(ProBuilderMesh).GetField("m_Mesh", BindingFlags.Instance | BindingFlags.NonPublic);
                return mesh != null && compiledMesh != null && field != null && field.FieldType == typeof(Mesh) && ReferenceEquals(field.GetValue(mesh), compiledMesh);
            }
            catch { return false; }
        }

        private static bool CompiledPositionsMatch(IList<Vector3> actual, IList<RawVertex> expected)
            => actual != null && expected != null && actual.Count == expected.Count && actual.Select((value, index) => Finite(value) && ExactEquals(value, expected[index].LocalPosition)).All(value => value);
        private static bool CompiledChannelMatches(IList<Vector3> actual, IList<ChannelValue> expected)
            => CompiledChannelMatches(actual == null ? null : actual.Select(value => (Vector4)value).ToArray(), expected);
        private static bool CompiledChannelMatches(IList<Vector4> actual, IList<ChannelValue> expected)
        {
            if (actual == null || expected == null || expected.Any(value => value == null)) return false;
            if (expected.All(value => !value.IsPresent)) return actual.Count == 0;
            return expected.All(value => value.IsPresent) && actual.Count == expected.Count && actual.Select((value, index) => Finite(value) && ExactEquals(value, expected[index].Value)).All(value => value);
        }
        private static bool CompiledChannelMatches(IList<Color> actual, IList<ChannelValue> expected)
            => CompiledChannelMatches(actual == null ? null : actual.Select(value => new Vector4(value.r, value.g, value.b, value.a)).ToArray(), expected);
        private static Bounds? BoundsFromPositions(IList<Vector3> positions)
        {
            if (positions == null || positions.Count == 0 || positions.Any(value => !Finite(value))) return null;
            var bounds = new Bounds(positions[0], Vector3.zero); for (var index = 1; index < positions.Count; index++) bounds.Encapsulate(positions[index]); return bounds;
        }
        private static bool BoundsEqual(Bounds left, Bounds right) => ExactEquals(left.center, right.center) && ExactEquals(left.size, right.size);

        private static ChannelValue[] ReadOptional(IList<Vector3> values, int count)
        {
            if (values == null || values.Count == 0) return Enumerable.Range(0, count).Select(_ => new ChannelValue(false, Vector4.zero)).ToArray();
            if (values.Count != count || values.Any(value => !Finite(value))) return null;
            return values.Select(value => new ChannelValue(true, value)).ToArray();
        }
        private static ChannelValue[] ReadOptional(IList<Vector4> values, int count)
        {
            if (values == null || values.Count == 0) return Enumerable.Range(0, count).Select(_ => new ChannelValue(false, Vector4.zero)).ToArray();
            if (values.Count != count || values.Any(value => !Finite(value))) return null;
            return values.Select(value => new ChannelValue(true, value)).ToArray();
        }
        private static ChannelValue[] ReadOptional(IList<Color> values, int count)
        {
            if (values == null || values.Count == 0) return Enumerable.Range(0, count).Select(_ => new ChannelValue(false, Vector4.zero)).ToArray();
            if (values.Count != count || values.Any(value => !Finite(value))) return null;
            return values.Select(value => new ChannelValue(true, new Vector4(value.r, value.g, value.b, value.a))).ToArray();
        }
        private static ChannelValue[] Channel(IEnumerable<Vector4> source, int count)
        {
            var values = source == null ? Array.Empty<Vector4>() : source.ToArray();
            if (values.Length != 0 && values.Length != count || values.Any(value => !Finite(value))) return null;
            return Enumerable.Range(0, count).Select(index => new ChannelValue(values.Length == count, values.Length == count ? values[index] : Vector4.zero)).ToArray();
        }
        private static ChannelValue[][] ReadUvChannels(ProBuilderMesh mesh, GameObject go, int count)
        {
            try
            {
                var result = new ChannelValue[4][];
                result[0] = Channel(mesh.textures?.Select(value => (Vector4)value), count);
                var unityMesh = go.GetComponent<MeshFilter>()?.sharedMesh;
                result[1] = Channel(unityMesh == null || unityMesh.uv2 == null ? null : unityMesh.uv2.Select(value => (Vector4)value), count);
                for (var channel = 2; channel <= 3; channel++)
                {
                    var values = new List<Vector4>(); mesh.GetUVs(channel, values); result[channel] = Channel(values, count);
                }
                return result.Any(values => values == null) ? null : result;
            }
            catch { return null; }
        }
        private static bool TryReadSharedTextures(ProBuilderMesh mesh, int count, out bool present, out SharedTextureSnapshot[] groups)
        {
            present = false; groups = null;
            try
            {
                var property = typeof(ProBuilderMesh).GetProperty("sharedTextures", BindingFlags.Instance | BindingFlags.NonPublic);
                if (property == null) return false;
                var values = property.GetValue(mesh) as IEnumerable<SharedVertex>;
                if (values == null) { groups = Array.Empty<SharedTextureSnapshot>(); return true; }
                present = true; var seen = new HashSet<int>(); var result = new List<SharedTextureSnapshot>(); var groupIndex = 0;
                foreach (var value in values)
                {
                    if (value == null || value.Count == 0) return false;
                    var members = value.ToArray();
                    if (members.Any(raw => raw < 0 || raw >= count || !seen.Add(raw))) return false;
                    result.Add(new SharedTextureSnapshot(groupIndex++, members));
                }
                groups = result.ToArray(); return true;
            }
            catch { return false; }
        }
        /// <summary>从有向 triangle half-edge 消除内部反向边；不访问 Face.edges cache，并保留 boundary 方向与异常重数。</summary>
        private static BoundaryReference[] CaptureBoundary(int[] triangles, int[] handles, bool allowCollapsedSharedEdges = false)
        {
            if (triangles == null || triangles.Length < 3 || triangles.Length % 3 != 0) return null;
            var grouped = new Dictionary<ulong, List<BoundaryReference>>();
            for (var index = 0; index < triangles.Length; index += 3)
            {
                foreach (var pair in new[] { new[] { triangles[index], triangles[index + 1] }, new[] { triangles[index + 1], triangles[index + 2] }, new[] { triangles[index + 2], triangles[index] } })
                {
                    var rawA = pair[0]; var rawB = pair[1];
                    if (rawA < 0 || rawB < 0 || rawA >= handles.Length || rawB >= handles.Length || rawA == rawB || (!allowCollapsedSharedEdges && handles[rawA] == handles[rawB])) return null;
                    var min = System.Math.Min(rawA, rawB); var max = System.Math.Max(rawA, rawB); var key = ((ulong)(uint)min << 32) | (uint)max;
                    if (!grouped.TryGetValue(key, out var values)) grouped.Add(key, values = new List<BoundaryReference>());
                    values.Add(new BoundaryReference(rawA, rawB, handles[rawA], handles[rawB]));
                }
            }
            var result = new List<BoundaryReference>();
            foreach (var values in grouped.Values)
            {
                if (values.Count == 1) { result.Add(values[0]); continue; }
                if (values.Count != 2 || values[0].RawA != values[1].RawB || values[0].RawB != values[1].RawA) return null;
            }
            return result.ToArray();
        }
        private static bool HasSimpleConnectedBoundary(IList<BoundaryReference> boundary)
        {
            if (boundary == null || boundary.Count < 3) return false;
            var outgoing = new Dictionary<int, int>(); var incoming = new Dictionary<int, int>(); var unique = new HashSet<ulong>();
            foreach (var edge in boundary)
            {
                var min = System.Math.Min(edge.SharedA, edge.SharedB); var max = System.Math.Max(edge.SharedA, edge.SharedB); var key = ((ulong)(uint)min << 32) | (uint)max;
                if (!unique.Add(key) || outgoing.ContainsKey(edge.SharedA) || incoming.ContainsKey(edge.SharedB)) return false;
                outgoing.Add(edge.SharedA, edge.SharedB); incoming.Add(edge.SharedB, edge.SharedA);
            }
            if (outgoing.Count < 3 || outgoing.Count != incoming.Count || outgoing.Keys.Any(vertex => !incoming.ContainsKey(vertex))) return false;
            var start = outgoing.Keys.First(); var current = start; var visited = new HashSet<int>();
            do { if (!visited.Add(current) || !outgoing.TryGetValue(current, out current)) return false; } while (current != start);
            return visited.Count == outgoing.Count && unique.Count == boundary.Count;
        }
        private static SharedEdgeOccurrence[] BuildEdges(IEnumerable<FaceOccurrence> faceValues)
        {
            var groups = new Dictionary<ulong, List<BoundaryIncidence>>();
            foreach (var face in faceValues) foreach (var edge in face.Boundary)
            {
                var min = System.Math.Min(edge.SharedA, edge.SharedB); var max = System.Math.Max(edge.SharedA, edge.SharedB); var key = ((ulong)(uint)min << 32) | (uint)max;
                if (!groups.TryGetValue(key, out var incidences)) groups.Add(key, incidences = new List<BoundaryIncidence>());
                incidences.Add(new BoundaryIncidence(face.OccurrenceIndex, edge));
            }
            return groups.Select(pair => new SharedEdgeOccurrence(System.Math.Min(pair.Value[0].SharedA, pair.Value[0].SharedB), System.Math.Max(pair.Value[0].SharedA, pair.Value[0].SharedB), pair.Value)).ToArray();
        }
        private static bool DirectedTriangleValueMultisetEquals(CanonicalMeshSnapshot leftSnapshot, FaceOccurrence left, CanonicalMeshSnapshot rightSnapshot, FaceOccurrence right)
        {
            var leftTriangles = SplitTriangles(left.DirectedSharedTriangleStream); var rightTriangles = SplitTriangles(right.DirectedSharedTriangleStream);
            return ExactMultisetEquals(leftTriangles, rightTriangles, (a, b) => DirectedSharedValueCyclicEquals(leftSnapshot, a, rightSnapshot, b));
        }
        private static bool BoundaryValueMultisetEquals(CanonicalMeshSnapshot leftSnapshot, FaceOccurrence left, CanonicalMeshSnapshot rightSnapshot, FaceOccurrence right)
        {
            return ExactMultisetEquals(left.Boundary, right.Boundary, (a, b) => SharedVertexValueEquals(leftSnapshot, a.SharedA, rightSnapshot, b.SharedA)
                && SharedVertexValueEquals(leftSnapshot, a.SharedB, rightSnapshot, b.SharedB));
        }
        private static bool DirectedSharedValueCyclicEquals(CanonicalMeshSnapshot leftSnapshot, int[] left, CanonicalMeshSnapshot rightSnapshot, int[] right)
        {
            for (var offset = 0; offset < 3; offset++) if (SharedVertexValueEquals(leftSnapshot, left[0], rightSnapshot, right[offset]) && SharedVertexValueEquals(leftSnapshot, left[1], rightSnapshot, right[(offset + 1) % 3]) && SharedVertexValueEquals(leftSnapshot, left[2], rightSnapshot, right[(offset + 2) % 3])) return true;
            return false;
        }
        /// <summary>跨快照 shared vertex 值比较包含规范位置和全部 raw member channel multiset，不比较 handle/raw 数字。</summary>
        /// <summary>纯只读镜像 ProBuilder 5.2.4 Normals.CalculateHardNormals；不调用 Refresh/GetNormals，也不修改 mesh 缓存。</summary>
        internal bool TryCalculateHardNormals(out Vector3[] normals)
        {
            normals = null;
            var sums = new Vector3[rawVertices.Length]; var counts = new int[rawVertices.Length];
            foreach (var face in faces)
            {
                var stream = face.DirectedRawTriangleStream;
                for (var index = 0; index < stream.Length; index += 3)
                {
                    var a = stream[index]; var b = stream[index + 1]; var c = stream[index + 2];
                    var cross = UnityEngine.ProBuilder.Math.Normal(rawVertices[a].LocalPosition, rawVertices[b].LocalPosition, rawVertices[c].LocalPosition);
                    cross.Normalize();
                    sums[a] += cross; sums[b] += cross; sums[c] += cross; counts[a]++; counts[b]++; counts[c]++;
                }
            }
            if (counts.Any(count => count == 0)) return false;
            var calculated = new Vector3[rawVertices.Length];
            for (var index = 0; index < calculated.Length; index++)
            {
                calculated[index] = sums[index] / counts[index];
                if (!Finite(calculated[index])) return false;
            }
            normals = calculated;
            return true;
        }

        /// <summary>
        /// 只读镜像 ProBuilder 5.2.4 Normals.CalculateNormals，并把即将由 ApplySmoothingGroups 清零的 face occurrence 视为 smoothing none。
        /// 输入必须来自同一 canonical snapshot；输出是 effect 前可冻结的 raw normal 终态，不读取或写入 Unity mesh cache。
        /// </summary>
        internal bool TryCalculateNormalsAfterSmoothingReset(ISet<int> resetOccurrences, out Vector3[] normals)
        {
            normals = null;
            if (resetOccurrences == null || resetOccurrences.Any(occurrence => !faces.Any(face => face.OccurrenceIndex == occurrence))
                || !TryCalculateHardNormals(out var calculated)) return false;

            var smoothingByRaw = new int[rawVertices.Length];
            var smoothGroupMax = 24;
            foreach (var face in faces)
            {
                var group = resetOccurrences.Contains(face.OccurrenceIndex) ? 0 : face.SmoothingGroup;
                if (group > ushort.MaxValue) return false;
                foreach (var raw in face.DirectedRawTriangleStream.Distinct())
                {
                    if (raw < 0 || raw >= smoothingByRaw.Length) return false;
                    smoothingByRaw[raw] = group;
                }
                if (group >= smoothGroupMax) smoothGroupMax = group + 1;
            }

            foreach (var shared in sharedVertices)
            {
                var smoothAverage = new Vector3[smoothGroupMax];
                var smoothAverageCount = new float[smoothGroupMax];
                foreach (var raw in shared.MemberRawIndexes)
                {
                    if (raw < 0 || raw >= calculated.Length) return false;
                    var group = smoothingByRaw[raw];
                    if (group <= 0 || (group > 24 && group < 42)) continue;
                    smoothAverage[group] += calculated[raw];
                    smoothAverageCount[group] += 1f;
                }
                foreach (var raw in shared.MemberRawIndexes)
                {
                    var group = smoothingByRaw[raw];
                    if (group <= 0 || (group > 24 && group < 42)) continue;
                    if (smoothAverageCount[group] <= 0f) return false;
                    calculated[raw] = EnsureUnitVector(smoothAverage[group] / smoothAverageCount[group]);
                    if (!Finite(calculated[raw])) return false;
                }
            }

            normals = calculated;
            return true;
        }

        internal static bool SharedVertexValueEquals(CanonicalMeshSnapshot leftSnapshot, int leftHandle, CanonicalMeshSnapshot rightSnapshot, int rightHandle)
        {
            var left = leftSnapshot.sharedVertices.FirstOrDefault(value => value.Handle == leftHandle); var right = rightSnapshot.sharedVertices.FirstOrDefault(value => value.Handle == rightHandle);
            if (!SharedVertexBaseValueEquals(leftSnapshot, left, rightSnapshot, right)) return false;
            return leftSnapshot.sharedVertices.Count(candidate => SharedVertexBaseValueEquals(leftSnapshot, candidate, rightSnapshot, right)) == 1
                && rightSnapshot.sharedVertices.Count(candidate => SharedVertexBaseValueEquals(leftSnapshot, left, rightSnapshot, candidate)) == 1;
        }
        private static bool SharedVertexBaseValueEquals(CanonicalMeshSnapshot leftSnapshot, SharedVertexSnapshot left, CanonicalMeshSnapshot rightSnapshot, SharedVertexSnapshot right)
        {
            return leftSnapshot != null && rightSnapshot != null && left != null && right != null && ExactEquals(left.CanonicalPosition, right.CanonicalPosition)
                && ExactMultisetEquals(left.MemberRawIndexes.Select(raw => leftSnapshot.rawVertices[raw]), right.MemberRawIndexes.Select(raw => rightSnapshot.rawVertices[raw]), RawChannelValueEquals);
        }
        /// <summary>跨快照稳定 raw payload 仅含 authoritative color/UV；normal/tangent cache 可由 Refresh 合法重建。</summary>
        internal static bool RawChannelValueEquals(RawVertex left, RawVertex right) => RawChannelValueEqualsIgnoringNormal(left, right);
        internal static bool RawChannelValueEqualsIgnoringNormal(RawVertex left, RawVertex right)
        {
            if (left == null || right == null || !ChannelValueEquals(left.Color, right.Color)) return false;
            var leftUvs = left.Uvs; var rightUvs = right.Uvs;
            return leftUvs.Length == rightUvs.Length && leftUvs.Zip(rightUvs, ChannelValueEquals).All(value => value);
        }
        internal static bool ChannelValueEquals(ChannelValue left, ChannelValue right) => left != null && right != null && left.IsPresent == right.IsPresent && (!left.IsPresent || ExactEquals(left.Value, right.Value));
        private static bool DirectedTriangleValueMultisetEqualsIgnoringNormals(CanonicalMeshSnapshot leftSnapshot, FaceOccurrence left, CanonicalMeshSnapshot rightSnapshot, FaceOccurrence right)
        {
            return ExactMultisetEquals(SplitTriangles(left.DirectedSharedTriangleStream), SplitTriangles(right.DirectedSharedTriangleStream),
                (a, b) => DirectedSharedValueCyclicEqualsIgnoringNormals(leftSnapshot, a, rightSnapshot, b));
        }
        private static bool BoundaryValueMultisetEqualsIgnoringNormals(CanonicalMeshSnapshot leftSnapshot, FaceOccurrence left, CanonicalMeshSnapshot rightSnapshot, FaceOccurrence right)
        {
            return ExactMultisetEquals(left.Boundary, right.Boundary, (a, b) => SharedVertexValueEqualsIgnoringNormal(leftSnapshot, a.SharedA, rightSnapshot, b.SharedA)
                && SharedVertexValueEqualsIgnoringNormal(leftSnapshot, a.SharedB, rightSnapshot, b.SharedB));
        }
        private static bool DirectedSharedValueCyclicEqualsIgnoringNormals(CanonicalMeshSnapshot leftSnapshot, int[] left, CanonicalMeshSnapshot rightSnapshot, int[] right)
        {
            for (var offset = 0; offset < 3; offset++)
                if (SharedVertexValueEqualsIgnoringNormal(leftSnapshot, left[0], rightSnapshot, right[offset])
                    && SharedVertexValueEqualsIgnoringNormal(leftSnapshot, left[1], rightSnapshot, right[(offset + 1) % 3])
                    && SharedVertexValueEqualsIgnoringNormal(leftSnapshot, left[2], rightSnapshot, right[(offset + 2) % 3])) return true;
            return false;
        }
        private static bool SharedVertexValueEqualsIgnoringNormal(CanonicalMeshSnapshot leftSnapshot, int leftHandle, CanonicalMeshSnapshot rightSnapshot, int rightHandle)
            => SharedVertexValueEquals(leftSnapshot, leftHandle, rightSnapshot, rightHandle);
        private static int[][] SplitTriangles(int[] stream) { var result = new List<int[]>(); for (var index = 0; index < stream.Length; index += 3) result.Add(new[] { stream[index], stream[index + 1], stream[index + 2] }); return result.ToArray(); }
        private static MaterialClaim CaptureMaterial(Renderer renderer, Material[] materials, int rawSubmeshIndex)
        {
            if (renderer == null) return new MaterialClaim(MaterialState.RendererMissing, -1, 0, false, null, 0);
            if (materials == null || materials.Length == 0) return new MaterialClaim(MaterialState.NoSlots, -1, 0, false, null, 0);
            return CaptureMaterialSlot(materials[Mathf.Clamp(rawSubmeshIndex, 0, materials.Length - 1)], Mathf.Clamp(rawSubmeshIndex, 0, materials.Length - 1));
        }
        /// <summary>把 5.2.4 Face 当前序列化的 material 值附加到 effective renderer slot claim。</summary>
        internal static MaterialClaim WithSerializedFaceMaterial(MaterialClaim effective, Material material)
        {
            if (effective == null) return null;
            var serialized = CaptureMaterialSlot(material, -1);
            return new MaterialClaim(effective.State, effective.EffectiveSlot, effective.InstanceId, effective.HasAssetIdentity, effective.AssetGuid, effective.AssetLocalId, serialized);
        }
        internal static MaterialClaim WithSerializedFaceMaterialFromFace(MaterialClaim effective, Face face)
        {
            var field = typeof(Face).GetField("m_Material", BindingFlags.Instance | BindingFlags.NonPublic);
            return effective == null || face == null || field == null || field.FieldType != typeof(Material) ? null : WithSerializedFaceMaterial(effective, field.GetValue(face) as Material);
        }
        /// <summary>Combine 只重映射 effective renderer slot；material identity 与序列化 face material 必须原样保留。</summary>
        internal static MaterialClaim WithEffectiveSlot(MaterialClaim source, int effectiveSlot)
        {
            return source == null ? null : new MaterialClaim(source.State, effectiveSlot, source.InstanceId, source.HasAssetIdentity, source.AssetGuid, source.AssetLocalId, source.SerializedFaceMaterial);
        }
        /// <summary>renderer material slot 独立于 face 捕获，空 mesh 也不能丢失 slot identity。</summary>
        private static MaterialClaim CaptureMaterialSlot(Material material, int slot)
        {
            if (material == null) return new MaterialClaim(MaterialState.Null, slot, 0, false, null, 0);
            var hasAsset = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId);
            return new MaterialClaim(MaterialState.Object, slot, material.GetInstanceID(), hasAsset, hasAsset ? guid : null, hasAsset ? localId : 0);
        }
        private static int? ReadElementGroup(Face face) { var field = typeof(Face).GetField("elementGroup", BindingFlags.Instance | BindingFlags.NonPublic); return field == null || !(field.GetValue(face) is int value) ? (int?)null : value; }
        private static bool HasDegenerateTriangle(int[] triangles, IList<Vector3> positions) { for (var index = 0; index < triangles.Length; index += 3) if (!(UnityEngine.ProBuilder.Math.TriangleArea(positions[triangles[index]], positions[triangles[index + 1]], positions[triangles[index + 2]]) > Mathf.Epsilon)) return true; return false; }
        internal static bool MaterialValueEquals(MaterialClaim left, MaterialClaim right)
        {
            if (!MaterialIdentityEquals(left, right)) return false;
            if (left.SerializedFaceMaterial == null || right.SerializedFaceMaterial == null)
                return left.SerializedFaceMaterial == null && right.SerializedFaceMaterial == null;
            return MaterialIdentityEquals(left.SerializedFaceMaterial, right.SerializedFaceMaterial);
        }
        internal static bool EffectiveMaterialMatches(MaterialClaim actual, Material expected)
            => actual != null && MaterialIdentityMatches(actual, expected);
        internal static bool SerializedFaceMaterialValueEquals(MaterialClaim left, MaterialClaim right)
        {
            if (left?.SerializedFaceMaterial == null || right?.SerializedFaceMaterial == null)
                return left?.SerializedFaceMaterial == null && right?.SerializedFaceMaterial == null;
            return MaterialIdentityEquals(left.SerializedFaceMaterial, right.SerializedFaceMaterial);
        }
        internal static bool SerializedFaceMaterialMatches(MaterialClaim actual, Material expected)
            => actual?.SerializedFaceMaterial != null && MaterialIdentityMatches(actual.SerializedFaceMaterial, expected);
        private static bool MaterialIdentityEquals(MaterialClaim left, MaterialClaim right)
        {
            if (left == null || right == null || left.State != right.State || left.EffectiveSlot != right.EffectiveSlot) return false;
            if (left.State != MaterialState.Object) return true;
            if (left.HasAssetIdentity || right.HasAssetIdentity) return left.HasAssetIdentity && right.HasAssetIdentity && left.AssetGuid == right.AssetGuid && left.AssetLocalId == right.AssetLocalId;
            return left.InstanceId == right.InstanceId;
        }
        private static bool MaterialIdentityMatches(MaterialClaim actual, Material expected)
        {
            if (actual == null || actual.EffectiveSlot < -1) return false;
            if (expected == null) return actual.State == MaterialState.Null;
            if (actual.State != MaterialState.Object) return false;
            var hasAsset = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(expected, out string guid, out long localId);
            return hasAsset ? actual.HasAssetIdentity && actual.AssetGuid == guid && actual.AssetLocalId == localId : !actual.HasAssetIdentity && actual.InstanceId == expected.GetInstanceID();
        }
        internal static bool UvValueEquals(AutoUnwrapSettings left, AutoUnwrapSettings right) => left.useWorldSpace == right.useWorldSpace && left.flipU == right.flipU && left.flipV == right.flipV && left.swapUV == right.swapUV && left.fill == right.fill && left.anchor == right.anchor && ExactEquals(left.scale, right.scale) && ExactEquals(left.offset, right.offset) && ExactEquals(left.rotation, right.rotation);
        private static bool Finite(AutoUnwrapSettings value) => Finite(value.scale) && Finite(value.offset) && IsFinite(value.rotation);
        private static bool Finite(Matrix4x4 value) { for (var index = 0; index < 16; index++) if (!IsFinite(value[index])) return false; return true; }
        private static bool Finite(Vector2 value) => IsFinite(value.x) && IsFinite(value.y);
        private static bool Finite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool Finite(Vector4 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        private static bool Finite(Color value) => IsFinite(value.r) && IsFinite(value.g) && IsFinite(value.b) && IsFinite(value.a);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
#endif
