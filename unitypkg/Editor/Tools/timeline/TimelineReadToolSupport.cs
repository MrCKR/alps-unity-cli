#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>Timeline structure 的 bounded traversal、stable sub-asset identity 与 package-optional 反射 owner。</summary>
    internal static class TimelineReadToolSupport
    {
        private const int MaxPageSize = 200;
        private const int MaxDepthLimit = 32;
        private const int MaxClipsPerTrackLimit = 500;
        private const int MaxScannedTracks = 10000;
        private const int MaxScannedClipsPerTrack = 10000;
        private const int MaxBindingsPerTrack = 64;

        internal sealed class ReadPlan
        {
            internal UnityEngine.Object Asset;
            internal Type TimelineType;
            internal string Path;
            internal string Guid;
            internal int Cursor;
            internal int PageSize;
            internal int MaxDepth;
            internal int MaxClipsPerTrack;
        }

        private sealed class TrackNode
        {
            internal UnityEngine.Object Track;
            internal UnityEngine.Object Parent;
            internal int Depth;
            internal int Index;
        }

        /// <summary>解析 path/GUID、分页上限和 Timeline type；只读取现有资产，不产生 import/refresh。</summary>
        internal static ReadPlan Prepare(string reference, int? cursor, int? pageSize, int? maxDepth, int? maxClipsPerTrack)
        {
            var timelineType = TimelineToolSupport.RequireTimelineAssetType();
            var path = ResolveTimelinePath(reference);
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
                throw Error("E_TIMELINE_NOT_FOUND", "Timeline asset was not found.", path, null);
            if (!timelineType.IsInstanceOfType(asset))
                throw Error("E_TIMELINE_TYPE_INVALID", "The requested asset is not a TimelineAsset.", path, AssetDatabase.AssetPathToGUID(path));

            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid) || !string.Equals(AssetDatabase.GUIDToAssetPath(guid), path, StringComparison.Ordinal))
                throw Error("E_TIMELINE_IDENTITY_UNAVAILABLE", "Timeline path and GUID did not round-trip.", path, guid);
            var resolvedCursor = cursor ?? 0;
            var resolvedPageSize = pageSize ?? 50;
            var resolvedDepth = maxDepth ?? 8;
            var resolvedClips = maxClipsPerTrack ?? 100;
            if (resolvedCursor < 0)
                throw Error("E_TIMELINE_PAGE_INVALID", "cursor must be zero or greater.", path, guid);
            if (resolvedPageSize < 1 || resolvedPageSize > MaxPageSize)
                throw Error("E_TIMELINE_PAGE_INVALID", "pageSize must be within [1,200].", path, guid);
            if (resolvedDepth < 0 || resolvedDepth > MaxDepthLimit)
                throw Error("E_TIMELINE_PAGE_INVALID", "maxDepth must be within [0,32].", path, guid);
            if (resolvedClips < 1 || resolvedClips > MaxClipsPerTrackLimit)
                throw Error("E_TIMELINE_PAGE_INVALID", "maxClipsPerTrack must be within [1,500].", path, guid);
            return new ReadPlan
            {
                Asset = asset,
                TimelineType = timelineType,
                Path = path,
                Guid = guid,
                Cursor = resolvedCursor,
                PageSize = resolvedPageSize,
                MaxDepth = resolvedDepth,
                MaxClipsPerTrack = resolvedClips
            };
        }

        /// <summary>构造当前 AssetDatabase 对象的 bounded fresh snapshot；超过硬扫描上限时 fail closed。</summary>
        internal static TimelineInfoResult Read(ReadPlan plan, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var nodes = new List<TrackNode>();
            var visited = new HashSet<int>();
            var depthTruncated = false;
            foreach (var root in InvokeEnumerable(plan.Asset, plan.TimelineType, "GetRootTracks"))
            {
                ct.ThrowIfCancellationRequested();
                var track = root as UnityEngine.Object;
                if (track == null) continue;
                WalkTrack(track, null, 0, plan.MaxDepth, nodes, visited, ref depthTruncated, ct);
            }
            if (plan.Cursor > nodes.Count)
                throw Error("E_TIMELINE_CURSOR_OUT_OF_RANGE", "cursor exceeds the bounded track count.", plan.Path, plan.Guid);

            var end = Math.Min(nodes.Count, plan.Cursor + plan.PageSize);
            var tracks = new List<TimelineTrackInfo>();
            for (var i = plan.Cursor; i < end; i++)
            {
                ct.ThrowIfCancellationRequested();
                tracks.Add(ReadTrack(nodes[i], plan.MaxClipsPerTrack, ct));
            }
            var timelineIdentity = RequirePersistentIdentity(plan.Asset, "Timeline asset");
            return new TimelineInfoResult
            {
                Timeline = new TimelineAssetInfo
                {
                    Path = plan.Path,
                    Guid = plan.Guid,
                    Name = plan.Asset.name,
                    Type = plan.Asset.GetType().FullName,
                    FrameRate = RequireFinite(ReadFrameRate(plan.Asset), "Timeline frameRate"),
                    Duration = RequireFinite(ReadDouble(plan.Asset, "duration"), "Timeline duration"),
                    TrackCount = nodes.Count,
                    SizeBytes = new System.IO.FileInfo(AssetToolSupport.AssetFullPath(plan.Path)).Length
                },
                Identity = timelineIdentity,
                Tracks = tracks,
                PageInfo = new PageInfo
                {
                    Cursor = plan.Cursor,
                    PageSize = plan.PageSize,
                    Total = nodes.Count,
                    NextCursor = end < nodes.Count ? (long?)end : null
                },
                MaxDepth = plan.MaxDepth,
                MaxClipsPerTrack = plan.MaxClipsPerTrack,
                DepthTruncated = depthTruncated
            };
        }

        private static void WalkTrack(UnityEngine.Object track, UnityEngine.Object parent, int depth, int maxDepth,
            List<TrackNode> nodes, HashSet<int> visited, ref bool depthTruncated, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(track.GetInstanceID()))
                throw Error("E_TIMELINE_STRUCTURE_INVALID", "Timeline track hierarchy contains a duplicate or cycle.", AssetDatabase.GetAssetPath(track), null);
            if (nodes.Count >= MaxScannedTracks)
                throw Error("E_TIMELINE_STRUCTURE_LIMIT_EXCEEDED", "Timeline contains more than 10000 tracks within maxDepth.", AssetDatabase.GetAssetPath(track), null);
            nodes.Add(new TrackNode { Track = track, Parent = parent, Depth = depth, Index = nodes.Count });

            var children = InvokeEnumerable(track, track.GetType(), "GetChildTracks");
            foreach (var value in children)
            {
                var child = value as UnityEngine.Object;
                if (child == null) continue;
                if (depth >= maxDepth)
                {
                    depthTruncated = true;
                    break;
                }
                WalkTrack(child, track, depth + 1, maxDepth, nodes, visited, ref depthTruncated, ct);
            }
        }

        private static TimelineTrackInfo ReadTrack(TrackNode node, int maxClips, CancellationToken ct)
        {
            var track = node.Track;
            var type = track.GetType();
            var allClips = new List<object>();
            foreach (var value in InvokeEnumerable(track, type, "GetClips"))
            {
                ct.ThrowIfCancellationRequested();
                if (value == null) continue;
                if (allClips.Count >= MaxScannedClipsPerTrack)
                    throw Error("E_TIMELINE_STRUCTURE_LIMIT_EXCEEDED", "A Timeline track contains more than 10000 clips.", AssetDatabase.GetAssetPath(track), null);
                allClips.Add(value);
            }
            var clips = new List<TimelineClipInfo>();
            var returnedClipCount = Math.Min(allClips.Count, maxClips);
            for (var i = 0; i < returnedClipCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                clips.Add(ReadClip(allClips[i], i));
            }
            return new TimelineTrackInfo
            {
                Index = node.Index,
                Identity = RequirePersistentIdentity(track, "Timeline track"),
                Parent = node.Parent == null ? null : RequirePersistentIdentity(node.Parent, "Timeline parent track"),
                Depth = node.Depth,
                Name = track.name,
                TrackType = FriendlyTrackType(type),
                TypeName = type.FullName,
                Muted = ReadBool(track, "muted"),
                MutedInHierarchy = ReadBool(track, "mutedInHierarchy"),
                Locked = ReadBool(track, "locked"),
                LockedInHierarchy = ReadBool(track, "lockedInHierarchy"),
                Start = RequireFinite(ReadDouble(track, "start"), "Track start"),
                Duration = RequireFinite(ReadDouble(track, "duration"), "Track duration"),
                ClipCount = allClips.Count,
                ClipsTruncated = allClips.Count > returnedClipCount,
                Clips = clips,
                Bindings = ReadBindings(track, ct)
            };
        }

        private static TimelineClipInfo ReadClip(object clip, int index)
        {
            var asset = ReadRequiredMember(clip, "asset") as UnityEngine.Object;
            var start = RequireFinite(ReadDouble(clip, "start"), "Clip start");
            var duration = RequireFinite(ReadDouble(clip, "duration"), "Clip duration");
            return new TimelineClipInfo
            {
                Index = index,
                DisplayName = ReadString(clip, "displayName"),
                Start = start,
                Duration = duration,
                End = RequireFinite(start + duration, "Clip end"),
                ClipIn = RequireFinite(ReadDouble(clip, "clipIn"), "Clip clipIn"),
                TimeScale = RequireFinite(ReadDouble(clip, "timeScale"), "Clip timeScale"),
                EaseInDuration = RequireFinite(ReadDouble(clip, "easeInDuration"), "Clip easeInDuration"),
                EaseOutDuration = RequireFinite(ReadDouble(clip, "easeOutDuration"), "Clip easeOutDuration"),
                PlayableAsset = asset == null ? null : RequirePersistentIdentity(asset, "Timeline clip playable asset"),
                SourceAsset = ResolveSourceAsset(asset)
            };
        }

        private static List<TimelineTrackBindingInfo> ReadBindings(UnityEngine.Object track, CancellationToken ct)
        {
            var result = new List<TimelineTrackBindingInfo>();
            var outputs = ReadRequiredMember(track, "outputs") as IEnumerable;
            if (outputs == null)
                throw Error("E_TIMELINE_API_UNAVAILABLE", "Timeline track outputs is not enumerable.", AssetDatabase.GetAssetPath(track), null);
            foreach (var value in outputs)
            {
                ct.ThrowIfCancellationRequested();
                if (value == null) continue;
                if (result.Count >= MaxBindingsPerTrack)
                    throw Error("E_TIMELINE_STRUCTURE_LIMIT_EXCEEDED", "A Timeline track exposes more than 64 output bindings.", AssetDatabase.GetAssetPath(track), null);
                var source = ReadRequiredMember(value, "sourceObject") as UnityEngine.Object;
                var outputType = ReadRequiredMember(value, "outputTargetType") as Type;
                result.Add(new TimelineTrackBindingInfo
                {
                    StreamName = ReadString(value, "streamName"),
                    Source = source == null ? null : RequirePersistentIdentity(source, "Timeline output binding source"),
                    OutputTargetType = outputType == null ? null : outputType.FullName
                });
            }
            return result;
        }

        private static TimelineObjectIdentity ResolveSourceAsset(UnityEngine.Object playableAsset)
        {
            if (playableAsset == null) return null;
            var candidates = new[] { "clip", "prefabGameObject" };
            foreach (var name in candidates)
            {
                var value = ReadMember(playableAsset, name) as UnityEngine.Object;
                if (value != null && value != playableAsset) return RequirePersistentIdentity(value, "Timeline clip source asset");
            }
            return null;
        }

        private static TimelineObjectIdentity RequirePersistentIdentity(UnityEngine.Object value, string label)
        {
            string guid = null;
            long localId = 0L;
            if (value == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out guid, out localId) || string.IsNullOrEmpty(guid))
                throw Error("E_TIMELINE_IDENTITY_UNAVAILABLE", label + " has no stable asset GUID/local file id.", value == null ? null : AssetDatabase.GetAssetPath(value), guid);
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
                throw Error("E_TIMELINE_IDENTITY_UNAVAILABLE", label + " GUID does not resolve to an asset path.", null, guid);
            return new TimelineObjectIdentity
            {
                Path = path,
                Guid = guid,
                LocalId = localId.ToString(CultureInfo.InvariantCulture),
                Name = value.name,
                Type = value.GetType().FullName
            };
        }

        private static IEnumerable InvokeEnumerable(object target, Type type, string methodName)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var enumerable = method == null ? null : method.Invoke(target, null) as IEnumerable;
            if (enumerable == null)
                throw Error("E_TIMELINE_API_UNAVAILABLE", "The installed Timeline package does not expose " + methodName + "().", target is UnityEngine.Object ? AssetDatabase.GetAssetPath((UnityEngine.Object)target) : null, null);
            return enumerable;
        }

        private static object ReadMember(object target, string name)
        {
            if (target == null) return null;
            object value;
            return TryReadMember(target, name, out value) ? value : null;
        }

        private static object ReadRequiredMember(object target, string name)
        {
            object value;
            if (target == null || !TryReadMember(target, name, out value))
                throw Error("E_TIMELINE_API_UNAVAILABLE", "The installed Timeline package does not expose required member " + name + ".", target is UnityEngine.Object ? AssetDatabase.GetAssetPath((UnityEngine.Object)target) : null, null);
            return value;
        }

        private static bool TryReadMember(object target, string name, out object value)
        {
            value = null;
            if (target == null) return false;
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanRead)
            {
                value = property.GetValue(target, null);
                return true;
            }
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return false;
            value = field.GetValue(target);
            return true;
        }

        private static double ReadFrameRate(UnityEngine.Object timeline)
        {
            var settings = ReadRequiredMember(timeline, "editorSettings");
            return Convert.ToDouble(ReadRequiredMember(settings, "frameRate"), CultureInfo.InvariantCulture);
        }

        private static double ReadDouble(object target, string name)
        {
            return Convert.ToDouble(ReadRequiredMember(target, name), CultureInfo.InvariantCulture);
        }

        private static bool ReadBool(object target, string name)
        {
            return Convert.ToBoolean(ReadRequiredMember(target, name), CultureInfo.InvariantCulture);
        }

        private static string ReadString(object target, string name)
        {
            var value = ReadRequiredMember(target, name);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double RequireFinite(double value, string label)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw Error("E_TIMELINE_STRUCTURE_INVALID", label + " is not finite.", null, null);
            return value;
        }

        private static string FriendlyTrackType(Type type)
        {
            var name = type == null ? "Unknown" : type.Name;
            return name.EndsWith("Track", StringComparison.Ordinal) ? name.Substring(0, name.Length - 5) : name;
        }

        private static string ResolveTimelinePath(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw Error("E_TIMELINE_REFERENCE_REQUIRED", "timeline path or GUID is required.", null, null);
            var value = reference.Trim();
            if (value.StartsWith("guid:", StringComparison.OrdinalIgnoreCase)) value = value.Substring(5);
            string path;
            if (IsGuid(value))
            {
                path = AssetDatabase.GUIDToAssetPath(value.ToLowerInvariant());
                if (string.IsNullOrEmpty(path))
                    throw Error("E_TIMELINE_NOT_FOUND", "Timeline GUID did not resolve to an asset.", null, value);
            }
            else
            {
                path = AssetToolSupport.RequireAssetPath(value);
            }
            if (!path.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
                throw Error("E_TIMELINE_PATH_INVALID", "Timeline asset path must end with .playable.", path, null);
            return path;
        }

        private static bool IsGuid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32) return false;
            for (var i = 0; i < value.Length; i++) if (!Uri.IsHexDigit(value[i])) return false;
            return true;
        }

        private static UnityCliBridge.BridgeCommandException Error(string code, string message, string path, string guid)
        {
            return new UnityCliBridge.BridgeCommandException(
                code,
                message,
                new Dictionary<string, object> { { "path", path }, { "guid", guid }, { "requiredPackage", TimelineToolSupport.PackageId } },
                new[] { "Run tools describe timeline.info and use asset.search to obtain a current Timeline path or GUID." });
        }
    }

    public sealed class TimelineInfoResult
    {
        public TimelineAssetInfo Timeline { get; set; }
        public TimelineObjectIdentity Identity { get; set; }
        public List<TimelineTrackInfo> Tracks { get; set; }
        public PageInfo PageInfo { get; set; }
        public long MaxDepth { get; set; }
        public long MaxClipsPerTrack { get; set; }
        public bool DepthTruncated { get; set; }
    }

    public sealed class TimelineObjectIdentity
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string LocalId { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
    }

    public sealed class TimelineTrackInfo
    {
        public long Index { get; set; }
        public TimelineObjectIdentity Identity { get; set; }
        public TimelineObjectIdentity Parent { get; set; }
        public long Depth { get; set; }
        public string Name { get; set; }
        public string TrackType { get; set; }
        public string TypeName { get; set; }
        public bool Muted { get; set; }
        public bool MutedInHierarchy { get; set; }
        public bool Locked { get; set; }
        public bool LockedInHierarchy { get; set; }
        public double Start { get; set; }
        public double Duration { get; set; }
        public long ClipCount { get; set; }
        public bool ClipsTruncated { get; set; }
        public List<TimelineClipInfo> Clips { get; set; }
        public List<TimelineTrackBindingInfo> Bindings { get; set; }
    }

    public sealed class TimelineClipInfo
    {
        public long Index { get; set; }
        public string DisplayName { get; set; }
        public double Start { get; set; }
        public double Duration { get; set; }
        public double End { get; set; }
        public double ClipIn { get; set; }
        public double TimeScale { get; set; }
        public double EaseInDuration { get; set; }
        public double EaseOutDuration { get; set; }
        public TimelineObjectIdentity PlayableAsset { get; set; }
        public TimelineObjectIdentity SourceAsset { get; set; }
    }

    public sealed class TimelineTrackBindingInfo
    {
        public string StreamName { get; set; }
        public TimelineObjectIdentity Source { get; set; }
        public string OutputTargetType { get; set; }
    }
}
#endif
