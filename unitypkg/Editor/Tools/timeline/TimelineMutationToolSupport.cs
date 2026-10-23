#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>Timeline track/clip mutation 的 preflight、唯一 effect 调用与 fresh structure proof owner。</summary>
    internal static class TimelineMutationToolSupport
    {
        private const int MaxScannedTracks = 10000;
        private const int MaxScannedClips = 10000;
        private const string TrackAssetTypeName = "UnityEngine.Timeline.TrackAsset, Unity.Timeline";
        private const string TimelineClipTypeName = "UnityEngine.Timeline.TimelineClip, Unity.Timeline";
        private const string TimelineCreateUtilitiesTypeName = "UnityEngine.Timeline.TimelineCreateUtilities, Unity.Timeline";

        private static readonly Dictionary<string, string> TrackTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Animation", "UnityEngine.Timeline.AnimationTrack, Unity.Timeline" },
            { "Audio", "UnityEngine.Timeline.AudioTrack, Unity.Timeline" },
            { "Activation", "UnityEngine.Timeline.ActivationTrack, Unity.Timeline" },
            { "Control", "UnityEngine.Timeline.ControlTrack, Unity.Timeline" },
            { "Playable", "UnityEngine.Timeline.PlayableTrack, Unity.Timeline" },
            { "Signal", "UnityEngine.Timeline.SignalTrack, Unity.Timeline" },
            { "Marker", "UnityEngine.Timeline.MarkerTrack, Unity.Timeline" }
        };

        internal sealed class AssetPlan
        {
            internal UnityEngine.Object Asset;
            internal Type TimelineType;
            internal string Path;
            internal string Guid;
            internal string HashBefore;
            internal PropertyInfo EditorSettingsProperty;
            internal TimelineToolSupport.FrameRateMember FrameRateMember;
        }

        internal sealed class TrackRecord
        {
            internal UnityEngine.Object Track;
            internal UnityEngine.Object Parent;
            internal TimelineObjectIdentity Identity;
            internal TimelineObjectIdentity ParentIdentity;
            internal int Index;
        }

        internal sealed class TrackAddPlan
        {
            internal AssetPlan Timeline;
            internal Type TrackAssetType;
            internal Type ConcreteTrackType;
            internal string TrackType;
            internal string Name;
            internal TrackRecord Parent;
            internal MethodInfo CreateTrack;
            internal int TrackCountBefore;
        }

        internal sealed class ClipAddPlan
        {
            internal AssetPlan Timeline;
            internal TrackRecord Track;
            internal UnityEngine.Object SourceAsset;
            internal TimelineObjectIdentity SourceIdentity;
            internal MethodInfo CreateClip;
            internal PropertyInfo StartProperty;
            internal PropertyInfo DurationProperty;
            internal double Start;
            internal double Duration;
            internal int ClipCountBefore;
        }

        /// <summary>effect 前冻结 Timeline、built-in track type、parent identity、name uniqueness 与 CreateTrack contract。</summary>
        internal static TrackAddPlan PrepareTrackAdd(string timelineReference, string trackType, string name,
            string parentTrack, string parentLocalId)
        {
            var timeline = ResolveTimeline(timelineReference);
            string fullName;
            if (string.IsNullOrWhiteSpace(trackType) || !TrackTypes.TryGetValue(trackType.Trim(), out fullName))
                throw Error("E_TIMELINE_TRACK_TYPE_INVALID", "trackType must be Animation, Audio, Activation, Control, Playable, Signal, or Marker.", timeline.Path, timeline.Guid);
            var concreteType = Type.GetType(fullName, false);
            var trackAssetType = Type.GetType(TrackAssetTypeName, false);
            if (concreteType == null || trackAssetType == null || !trackAssetType.IsAssignableFrom(concreteType))
                throw Error("E_TIMELINE_API_UNAVAILABLE", "The requested built-in Timeline track type is unavailable.", timeline.Path, timeline.Guid);

            var records = CollectTracks(timeline.Asset, timeline.TimelineType);
            var parent = ResolveTrack(records, parentTrack, parentLocalId, false, timeline);
            if (parent != null && !IsApprovedParentType(parent.Track.GetType()))
                throw Error("E_TIMELINE_PARENT_TYPE_INVALID", "Parent track must be a built-in GroupTrack or supported built-in track.", timeline.Path, timeline.Guid);
            ValidateParentTrack(parent == null ? null : parent.Track, concreteType, timeline);

            var trackName = RequireName(string.IsNullOrWhiteSpace(name) ? CanonicalTrackType(concreteType) : name, "track name", timeline);
            foreach (var record in records)
            {
                if (!SameParent(record, parent)) continue;
                if (string.Equals(record.Track.name, trackName, StringComparison.Ordinal))
                    throw Error("E_TIMELINE_TRACK_EXISTS", "A sibling track already uses the requested name.", timeline.Path, timeline.Guid);
            }

            var createTrack = timeline.TimelineType.GetMethod("CreateTrack", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(Type), trackAssetType, typeof(string) }, null);
            if (createTrack == null)
                throw Error("E_TIMELINE_API_UNAVAILABLE", "TimelineAsset.CreateTrack(Type, TrackAsset, string) is unavailable.", timeline.Path, timeline.Guid);
            return new TrackAddPlan
            {
                Timeline = timeline,
                TrackAssetType = trackAssetType,
                ConcreteTrackType = concreteType,
                TrackType = CanonicalTrackType(concreteType),
                Name = trackName,
                Parent = parent,
                CreateTrack = createTrack,
                TrackCountBefore = records.Count
            };
        }

        /// <summary>唯一 effect boundary 后创建 track，保存并 fresh 证明 GUID/hash/count/type/name/parent/localId。</summary>
        internal static TimelineTrackMutationInfo AddTrack(TrackAddPlan plan)
        {
            var created = plan.CreateTrack.Invoke(plan.Timeline.Asset,
                new object[] { plan.ConcreteTrackType, plan.Parent == null ? null : plan.Parent.Track, plan.Name }) as UnityEngine.Object;
            if (created == null)
                throw Error("E_TIMELINE_TRACK_ADD_PROOF_FAILED", "Timeline did not return the created track.", plan.Timeline.Path, plan.Timeline.Guid);
            EditorUtility.SetDirty(plan.Timeline.Asset);
            AssetDatabase.SaveAssets();
            var createdIdentity = RequireIdentity(created, "created Timeline track", plan.Timeline);
            AssetDatabase.ImportAsset(plan.Timeline.Path, ImportAssetOptions.ForceSynchronousImport);

            var fresh = RequireFreshTimeline(plan.Timeline);
            var records = CollectTracks(fresh, plan.Timeline.TimelineType);
            if (records.Count != plan.TrackCountBefore + 1)
                throw Error("E_TIMELINE_TRACK_ADD_PROOF_FAILED", "Fresh Timeline track count did not increase by one.", plan.Timeline.Path, plan.Timeline.Guid);
            var observed = FindByLocalId(records, createdIdentity.LocalId);
            if (observed == null
                || observed.Track.GetType() != plan.ConcreteTrackType
                || !string.Equals(observed.Track.name, plan.Name, StringComparison.Ordinal)
                || !SameIdentity(observed.ParentIdentity, plan.Parent == null ? null : plan.Parent.Identity)
                || !HashChanged(plan.Timeline))
                throw Error("E_TIMELINE_TRACK_ADD_PROOF_FAILED", "Fresh Timeline track identity, type, name, parent, or serialized artifact did not match the frozen plan.", plan.Timeline.Path, plan.Timeline.Guid);
            return BuildTrackMutationInfo(observed);
        }

        /// <summary>effect 前冻结 target track、source asset、clip overload、timing range 与 serialized baseline。</summary>
        internal static ClipAddPlan PrepareClipAdd(string timelineReference, string track, string trackLocalId,
            double start, double duration, string assetReference)
        {
            var timeline = ResolveTimeline(timelineReference);
            if (double.IsNaN(start) || double.IsInfinity(start) || start < 0d
                || double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0d)
                throw Error("E_TIMELINE_CLIP_TIMING_INVALID", "start must be finite and >= 0; duration must be finite and > 0.", timeline.Path, timeline.Guid);
            var clipType = Type.GetType(TimelineClipTypeName, false);
            if (clipType == null)
                throw Error("E_TIMELINE_API_UNAVAILABLE", "TimelineClip type is unavailable.", timeline.Path, timeline.Guid);
            var maxTime = ReadMaxClipTime(clipType, timeline);
            if (start > maxTime || duration > maxTime || start + duration > maxTime * 2d)
                throw Error("E_TIMELINE_CLIP_TIMING_INVALID", "start or duration exceeds the installed Timeline clip time range.", timeline.Path, timeline.Guid);
            var startProperty = RequireWritableDoubleProperty(clipType, "start", timeline);
            var durationProperty = RequireWritableDoubleProperty(clipType, "duration", timeline);

            var records = CollectTracks(timeline.Asset, timeline.TimelineType);
            var target = ResolveTrack(records, track, trackLocalId, true, timeline);
            if (!IsApprovedTrackType(target.Track.GetType()))
                throw Error("E_TIMELINE_TRACK_TYPE_INVALID", "Clip target must be one of the approved built-in Timeline track types.", timeline.Path, timeline.Guid);

            UnityEngine.Object source = null;
            TimelineObjectIdentity sourceIdentity = null;
            if (!string.IsNullOrWhiteSpace(assetReference))
            {
                source = ResolveAssetReference(assetReference, timeline);
                sourceIdentity = RequireIdentity(source, "Timeline clip source asset", timeline);
            }
            var createClip = ResolveCreateClip(target.Track, source, timeline);
            return new ClipAddPlan
            {
                Timeline = timeline,
                Track = target,
                SourceAsset = source,
                SourceIdentity = sourceIdentity,
                CreateClip = createClip,
                StartProperty = startProperty,
                DurationProperty = durationProperty,
                Start = start,
                Duration = duration,
                ClipCountBefore = CountClips(target.Track, timeline)
            };
        }

        /// <summary>唯一 effect boundary 后创建 clip，保存并 fresh 证明 count/timing/playable/source binding/hash。</summary>
        internal static TimelineClipMutationInfo AddClip(ClipAddPlan plan)
        {
            var clip = plan.CreateClip.GetParameters().Length == 0
                ? plan.CreateClip.Invoke(plan.Track.Track, null)
                : plan.CreateClip.Invoke(plan.Track.Track, new object[] { plan.SourceAsset });
            if (clip == null)
                throw Error("E_TIMELINE_CLIP_ADD_PROOF_FAILED", "Timeline did not return the created clip.", plan.Timeline.Path, plan.Timeline.Guid);
            plan.StartProperty.SetValue(clip, plan.Start, null);
            plan.DurationProperty.SetValue(clip, plan.Duration, null);
            EditorUtility.SetDirty(plan.Timeline.Asset);
            AssetDatabase.SaveAssets();

            var playableAsset = ReadRequiredMember(clip, "asset", plan.Timeline) as UnityEngine.Object;
            if (playableAsset == null)
                throw Error("E_TIMELINE_CLIP_ADD_PROOF_FAILED", "Created clip has no persistent playable asset identity.", plan.Timeline.Path, plan.Timeline.Guid);
            var playableIdentity = RequireIdentity(playableAsset, "created Timeline clip playable asset", plan.Timeline);
            AssetDatabase.ImportAsset(plan.Timeline.Path, ImportAssetOptions.ForceSynchronousImport);

            var fresh = RequireFreshTimeline(plan.Timeline);
            var records = CollectTracks(fresh, plan.Timeline.TimelineType);
            var freshTrack = FindByLocalId(records, plan.Track.Identity.LocalId);
            if (freshTrack == null)
                throw Error("E_TIMELINE_CLIP_ADD_PROOF_FAILED", "Fresh target track identity was not found.", plan.Timeline.Path, plan.Timeline.Guid);
            var clips = CollectClips(freshTrack.Track, plan.Timeline);
            if (clips.Count != plan.ClipCountBefore + 1)
                throw Error("E_TIMELINE_CLIP_ADD_PROOF_FAILED", "Fresh target clip count did not increase by one.", plan.Timeline.Path, plan.Timeline.Guid);
            object observed = null;
            var observedIndex = -1;
            for (var i = 0; i < clips.Count; i++)
            {
                var candidateAsset = ReadRequiredMember(clips[i], "asset", plan.Timeline) as UnityEngine.Object;
                if (candidateAsset == null) continue;
                var identity = RequireIdentity(candidateAsset, "fresh Timeline clip playable asset", plan.Timeline);
                if (!string.Equals(identity.LocalId, playableIdentity.LocalId, StringComparison.Ordinal)) continue;
                observed = clips[i];
                observedIndex = i;
                break;
            }
            if (observed == null)
                throw Error("E_TIMELINE_CLIP_ADD_PROOF_FAILED", "Fresh clip playable asset identity was not found.", plan.Timeline.Path, plan.Timeline.Guid);
            var observedStart = ReadDouble(observed, "start", plan.Timeline);
            var observedDuration = ReadDouble(observed, "duration", plan.Timeline);
            var sourceIdentity = ResolveClipSourceIdentity(observed, plan.Timeline);
            if (Math.Abs(observedStart - plan.Start) > 0.000000001d
                || Math.Abs(observedDuration - plan.Duration) > 0.000000001d
                || !SameIdentity(sourceIdentity, plan.SourceIdentity)
                || !HashChanged(plan.Timeline))
                throw Error("E_TIMELINE_CLIP_ADD_PROOF_FAILED", "Fresh clip timing, source binding, or serialized artifact did not match the frozen plan.", plan.Timeline.Path, plan.Timeline.Guid);
            return BuildClipMutationInfo(observed, observedIndex, freshTrack, plan.Timeline);
        }

        internal static TimelineTrackMutationInfo PreviewTrack(TrackAddPlan plan)
        {
            return new TimelineTrackMutationInfo
            {
                Parent = plan.Parent == null ? null : plan.Parent.Identity,
                Name = plan.Name,
                TrackType = plan.TrackType,
                TypeName = plan.ConcreteTrackType.FullName
            };
        }

        internal static TimelineClipMutationInfo PreviewClip(ClipAddPlan plan)
        {
            return new TimelineClipMutationInfo
            {
                Track = BuildTrackMutationInfo(plan.Track),
                Index = plan.ClipCountBefore,
                Start = plan.Start,
                Duration = plan.Duration,
                End = plan.Start + plan.Duration,
                SourceAsset = plan.SourceIdentity
            };
        }

        internal static TimelineAssetInfo BuildTimelineInfo(AssetPlan plan)
        {
            var fresh = AssetDatabase.LoadMainAssetAtPath(plan.Path);
            var tracks = fresh == null ? new List<TrackRecord>() : CollectTracks(fresh, plan.TimelineType);
            var file = new FileInfo(AssetToolSupport.AssetFullPath(plan.Path));
            return new TimelineAssetInfo
            {
                Path = plan.Path,
                Guid = plan.Guid,
                Name = fresh == null ? null : fresh.name,
                Type = fresh == null ? null : fresh.GetType().FullName,
                FrameRate = fresh == null ? 0d : TimelineToolSupport.ReadFrameRate(fresh, plan.EditorSettingsProperty, plan.FrameRateMember),
                Duration = fresh == null ? 0d : TimelineToolSupport.ReadDoubleProperty(fresh, plan.TimelineType, "duration"),
                TrackCount = tracks.Count,
                SizeBytes = file.Exists ? file.Length : 0L
            };
        }

        /// <summary>effect 前冻结 asset identity 与 frame-rate 成员；成员不可用时在写入前失败。</summary>
        private static AssetPlan ResolveTimeline(string reference)
        {
            var read = TimelineReadToolSupport.Prepare(reference, 0, 1, 32, 1);
            var settings = TimelineToolSupport.RequireEditorSettingsProperty(read.TimelineType, read.Path);
            return new AssetPlan
            {
                Asset = read.Asset,
                TimelineType = read.TimelineType,
                Path = read.Path,
                Guid = read.Guid,
                HashBefore = HashFile(read.Path),
                EditorSettingsProperty = settings,
                FrameRateMember = TimelineToolSupport.RequireFrameRateMember(settings.PropertyType, read.TimelineType, read.Path)
            };
        }

        private static UnityEngine.Object RequireFreshTimeline(AssetPlan plan)
        {
            var fresh = AssetDatabase.LoadMainAssetAtPath(plan.Path);
            var guid = AssetDatabase.AssetPathToGUID(plan.Path);
            if (fresh == null || !plan.TimelineType.IsInstanceOfType(fresh)
                || !string.Equals(guid, plan.Guid, StringComparison.Ordinal)
                || !string.Equals(AssetDatabase.GUIDToAssetPath(guid), plan.Path, StringComparison.Ordinal))
                throw Error("E_TIMELINE_IDENTITY_UNAVAILABLE", "Fresh Timeline path, GUID, or type did not match the frozen identity.", plan.Path, guid);
            return fresh;
        }

        private static List<TrackRecord> CollectTracks(UnityEngine.Object timeline, Type timelineType)
        {
            var result = new List<TrackRecord>();
            var visited = new HashSet<int>();
            foreach (var value in InvokeEnumerable(timeline, timelineType, "GetRootTracks", null))
            {
                var track = value as UnityEngine.Object;
                if (track != null) WalkTrack(track, null, result, visited, new AssetPlan { Path = AssetDatabase.GetAssetPath(timeline), Guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(timeline)) });
            }
            return result;
        }

        private static void WalkTrack(UnityEngine.Object track, UnityEngine.Object parent, List<TrackRecord> result,
            HashSet<int> visited, AssetPlan timeline)
        {
            if (!visited.Add(track.GetInstanceID()))
                throw Error("E_TIMELINE_STRUCTURE_INVALID", "Timeline track hierarchy contains a duplicate or cycle.", timeline.Path, timeline.Guid);
            if (result.Count >= MaxScannedTracks)
                throw Error("E_TIMELINE_STRUCTURE_LIMIT_EXCEEDED", "Timeline contains more than 10000 tracks.", timeline.Path, timeline.Guid);
            result.Add(new TrackRecord
            {
                Track = track,
                Parent = parent,
                Identity = RequireIdentity(track, "Timeline track", timeline),
                ParentIdentity = parent == null ? null : RequireIdentity(parent, "Timeline parent track", timeline),
                Index = result.Count
            });
            foreach (var value in InvokeEnumerable(track, track.GetType(), "GetChildTracks", timeline))
            {
                var child = value as UnityEngine.Object;
                if (child != null) WalkTrack(child, track, result, visited, timeline);
            }
        }

        private static TrackRecord ResolveTrack(List<TrackRecord> records, string name, string localId, bool required, AssetPlan timeline)
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(localId))
                throw Error("E_TIMELINE_TRACK_SELECTOR_CONFLICT", "Pass track name or localId, not both.", timeline.Path, timeline.Guid);
            if (!string.IsNullOrWhiteSpace(localId))
            {
                long parsed;
                if (!long.TryParse(localId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    throw Error("E_TIMELINE_TRACK_SELECTOR_INVALID", "track localId must be a signed decimal Int64 string.", timeline.Path, timeline.Guid);
                var canonical = parsed.ToString(CultureInfo.InvariantCulture);
                var found = FindByLocalId(records, canonical);
                if (found == null)
                    throw Error("E_TIMELINE_TRACK_NOT_FOUND", "Timeline track localId was not found.", timeline.Path, timeline.Guid);
                return found;
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                TrackRecord found = null;
                foreach (var record in records)
                {
                    if (!string.Equals(record.Track.name, name.Trim(), StringComparison.Ordinal)) continue;
                    if (found != null)
                        throw Error("E_TIMELINE_TRACK_AMBIGUOUS", "Timeline track name is ambiguous; use localId from timeline.info.", timeline.Path, timeline.Guid);
                    found = record;
                }
                if (found == null)
                    throw Error("E_TIMELINE_TRACK_NOT_FOUND", "Timeline track name was not found.", timeline.Path, timeline.Guid);
                return found;
            }
            if (required)
                throw Error("E_TIMELINE_TRACK_REQUIRED", "track name or trackLocalId is required.", timeline.Path, timeline.Guid);
            return null;
        }

        private static TrackRecord FindByLocalId(List<TrackRecord> records, string localId)
        {
            foreach (var record in records)
                if (string.Equals(record.Identity.LocalId, localId, StringComparison.Ordinal)) return record;
            return null;
        }

        private static bool SameParent(TrackRecord record, TrackRecord parent)
        {
            return SameIdentity(record.ParentIdentity, parent == null ? null : parent.Identity);
        }

        private static bool SameIdentity(TimelineObjectIdentity left, TimelineObjectIdentity right)
        {
            if (left == null || right == null) return left == null && right == null;
            return string.Equals(left.Guid, right.Guid, StringComparison.Ordinal)
                && string.Equals(left.LocalId, right.LocalId, StringComparison.Ordinal);
        }

        private static void ValidateParentTrack(UnityEngine.Object parent, Type childType, AssetPlan timeline)
        {
            if (parent == null) return;
            var utilities = Type.GetType(TimelineCreateUtilitiesTypeName, false);
            var method = utilities == null ? null : utilities.GetMethod("ValidateParentTrack", BindingFlags.Public | BindingFlags.Static);
            if (method == null || method.ReturnType != typeof(bool))
                throw Error("E_TIMELINE_API_UNAVAILABLE", "Timeline parent validation API is unavailable.", timeline.Path, timeline.Guid);
            var valid = Convert.ToBoolean(method.Invoke(null, new object[] { parent, childType }), CultureInfo.InvariantCulture);
            if (!valid)
                throw Error("E_TIMELINE_PARENT_TYPE_INVALID", "The requested track type cannot be nested under the selected parent.", timeline.Path, timeline.Guid);
        }

        private static MethodInfo ResolveCreateClip(UnityEngine.Object track, UnityEngine.Object source, AssetPlan timeline)
        {
            var type = track.GetType();
            var isAnimation = string.Equals(type.FullName, "UnityEngine.Timeline.AnimationTrack", StringComparison.Ordinal);
            var isAudio = string.Equals(type.FullName, "UnityEngine.Timeline.AudioTrack", StringComparison.Ordinal);
            if ((isAnimation || isAudio) && source == null)
                throw Error("E_TIMELINE_CLIP_ASSET_REQUIRED", "Animation and Audio tracks require a source asset.", timeline.Path, timeline.Guid);
            if (source != null)
            {
                MethodInfo best = null;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!string.Equals(method.Name, "CreateClip", StringComparison.Ordinal) || method.IsGenericMethodDefinition) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1 || !parameters[0].ParameterType.IsAssignableFrom(source.GetType())) continue;
                    if (parameters[0].ParameterType == source.GetType()) return method;
                    if (best == null) best = method;
                }
                if (best != null) return best;
                throw Error("E_TIMELINE_CLIP_ASSET_TYPE_INVALID", "Target track has no CreateClip overload for the source asset type.", timeline.Path, timeline.Guid);
            }
            var createDefault = type.GetMethod("CreateDefaultClip", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (createDefault == null || !HasDefaultClipType(type))
                throw Error("E_TIMELINE_CLIP_UNSUPPORTED", "Target track cannot create a persistent default clip.", timeline.Path, timeline.Guid);
            return createDefault;
        }

        private static bool HasDefaultClipType(Type trackType)
        {
            foreach (var attribute in trackType.GetCustomAttributes(true))
            {
                if (attribute == null || !string.Equals(attribute.GetType().FullName, "UnityEngine.Timeline.TrackClipTypeAttribute", StringComparison.Ordinal)) continue;
                var field = attribute.GetType().GetField("inspectedType", BindingFlags.Public | BindingFlags.Instance);
                var inspectedType = field == null ? null : field.GetValue(attribute) as Type;
                if (inspectedType != null
                    && typeof(ScriptableObject).IsAssignableFrom(inspectedType)
                    && typeof(UnityEngine.Playables.IPlayableAsset).IsAssignableFrom(inspectedType)) return true;
            }
            return false;
        }

        private static UnityEngine.Object ResolveAssetReference(string reference, AssetPlan timeline)
        {
            var value = reference.Trim();
            if (value.StartsWith("guid:", StringComparison.OrdinalIgnoreCase)) value = value.Substring(5);
            string path;
            if (IsGuid(value)) path = AssetDatabase.GUIDToAssetPath(value.ToLowerInvariant());
            else path = AssetToolSupport.RequireAssetPath(value);
            var asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
                throw Error("E_TIMELINE_CLIP_ASSET_NOT_FOUND", "Timeline clip source asset was not found.", path, IsGuid(value) ? value : null);
            return asset;
        }

        private static TimelineClipMutationInfo BuildClipMutationInfo(object clip, int index, TrackRecord track, AssetPlan timeline)
        {
            var playable = ReadRequiredMember(clip, "asset", timeline) as UnityEngine.Object;
            var start = ReadDouble(clip, "start", timeline);
            var duration = ReadDouble(clip, "duration", timeline);
            return new TimelineClipMutationInfo
            {
                Track = BuildTrackMutationInfo(track),
                Index = index,
                DisplayName = Convert.ToString(ReadRequiredMember(clip, "displayName", timeline), CultureInfo.InvariantCulture),
                Start = start,
                Duration = duration,
                End = start + duration,
                PlayableAsset = playable == null ? null : RequireIdentity(playable, "Timeline clip playable asset", timeline),
                SourceAsset = ResolveClipSourceIdentity(clip, timeline)
            };
        }

        private static TimelineTrackMutationInfo BuildTrackMutationInfo(TrackRecord record)
        {
            return new TimelineTrackMutationInfo
            {
                Identity = record.Identity,
                Parent = record.ParentIdentity,
                Name = record.Track.name,
                TrackType = CanonicalTrackType(record.Track.GetType()),
                TypeName = record.Track.GetType().FullName
            };
        }

        private static TimelineObjectIdentity ResolveClipSourceIdentity(object clip, AssetPlan timeline)
        {
            var playable = ReadRequiredMember(clip, "asset", timeline) as UnityEngine.Object;
            if (playable == null) return null;
            foreach (var name in new[] { "clip", "prefabGameObject" })
            {
                object value;
                if (!TryReadMember(playable, name, out value)) continue;
                var source = value as UnityEngine.Object;
                if (source != null && source != playable) return RequireIdentity(source, "Timeline clip source asset", timeline);
            }
            return null;
        }

        private static int CountClips(UnityEngine.Object track, AssetPlan timeline)
        {
            return CollectClips(track, timeline).Count;
        }

        private static List<object> CollectClips(UnityEngine.Object track, AssetPlan timeline)
        {
            var result = new List<object>();
            foreach (var clip in InvokeEnumerable(track, track.GetType(), "GetClips", timeline))
            {
                if (clip == null) continue;
                if (result.Count >= MaxScannedClips)
                    throw Error("E_TIMELINE_STRUCTURE_LIMIT_EXCEEDED", "Timeline track contains more than 10000 clips.", timeline.Path, timeline.Guid);
                result.Add(clip);
            }
            return result;
        }

        private static TimelineObjectIdentity RequireIdentity(UnityEngine.Object value, string label, AssetPlan timeline)
        {
            string guid = null;
            long localId = 0L;
            if (value == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out guid, out localId) || string.IsNullOrEmpty(guid))
                throw Error("E_TIMELINE_IDENTITY_UNAVAILABLE", label + " has no stable GUID/localId.", timeline.Path, guid);
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
                throw Error("E_TIMELINE_IDENTITY_UNAVAILABLE", label + " GUID does not resolve to a path.", timeline.Path, guid);
            return new TimelineObjectIdentity
            {
                Path = path,
                Guid = guid,
                LocalId = localId.ToString(CultureInfo.InvariantCulture),
                Name = value.name,
                Type = value.GetType().FullName
            };
        }

        private static IEnumerable InvokeEnumerable(object target, Type type, string methodName, AssetPlan timeline)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var enumerable = method == null ? null : method.Invoke(target, null) as IEnumerable;
            if (enumerable == null)
                throw Error("E_TIMELINE_API_UNAVAILABLE", "Timeline API does not expose " + methodName + "().", timeline == null ? null : timeline.Path, timeline == null ? null : timeline.Guid);
            return enumerable;
        }

        private static PropertyInfo RequireWritableDoubleProperty(Type type, string name, AssetPlan timeline)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead || !property.CanWrite || property.PropertyType != typeof(double))
                throw Error("E_TIMELINE_API_UNAVAILABLE", "TimelineClip." + name + " is not a writable double property.", timeline.Path, timeline.Guid);
            return property;
        }

        private static double ReadMaxClipTime(Type clipType, AssetPlan timeline)
        {
            var field = clipType.GetField("kMaxTimeValue", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
                throw Error("E_TIMELINE_API_UNAVAILABLE", "TimelineClip time range contract is unavailable.", timeline.Path, timeline.Guid);
            return Convert.ToDouble(field.GetValue(null), CultureInfo.InvariantCulture);
        }

        private static object ReadRequiredMember(object target, string name, AssetPlan timeline)
        {
            object value;
            if (target == null || !TryReadMember(target, name, out value))
                throw Error("E_TIMELINE_API_UNAVAILABLE", "Timeline API does not expose required member " + name + ".", timeline.Path, timeline.Guid);
            return value;
        }

        private static bool TryReadMember(object target, string name, out object value)
        {
            value = null;
            if (target == null) return false;
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanRead)
            {
                value = property.GetValue(target, null);
                return true;
            }
            var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return false;
            value = field.GetValue(target);
            return true;
        }

        private static double ReadDouble(object target, string name, AssetPlan timeline)
        {
            return Convert.ToDouble(ReadRequiredMember(target, name, timeline), CultureInfo.InvariantCulture);
        }

        private static string HashFile(string assetPath)
        {
            try
            {
                using (var stream = File.OpenRead(AssetToolSupport.AssetFullPath(assetPath)))
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
            catch (Exception)
            {
                throw Error("E_TIMELINE_SOURCE_PROOF_UNAVAILABLE", "Timeline serialized artifact could not be hashed.", assetPath, AssetDatabase.AssetPathToGUID(assetPath));
            }
        }

        private static bool HashChanged(AssetPlan timeline)
        {
            return !string.Equals(timeline.HashBefore, HashFile(timeline.Path), StringComparison.Ordinal);
        }

        private static string RequireName(string value, string label, AssetPlan timeline)
        {
            var name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 128)
                throw Error("E_TIMELINE_TRACK_NAME_INVALID", label + " must contain 1 to 128 characters.", timeline.Path, timeline.Guid);
            for (var i = 0; i < name.Length; i++)
                if (char.IsControl(name[i]) || name[i] == '/')
                    throw Error("E_TIMELINE_TRACK_NAME_INVALID", label + " cannot contain control characters or '/'.", timeline.Path, timeline.Guid);
            return name;
        }

        private static bool IsApprovedTrackType(Type type)
        {
            foreach (var value in TrackTypes.Values)
                if (string.Equals(type.AssemblyQualifiedName, value, StringComparison.Ordinal)
                    || string.Equals(type.FullName + ", " + type.Assembly.GetName().Name, value, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsApprovedParentType(Type type)
        {
            return string.Equals(type.FullName, "UnityEngine.Timeline.GroupTrack", StringComparison.Ordinal) || IsApprovedTrackType(type);
        }

        private static string CanonicalTrackType(Type type)
        {
            foreach (var pair in TrackTypes)
                if (pair.Value.StartsWith(type.FullName + ",", StringComparison.Ordinal)) return pair.Key;
            var name = type.Name;
            return name.EndsWith("Track", StringComparison.Ordinal) ? name.Substring(0, name.Length - 5) : name;
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
                new[] { "Run tools describe timeline.track-add or timeline.clip-add, then use timeline.info for current track identities." });
        }
    }

    public sealed class TimelineMutationPlannedEffect
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public string Target { get; set; }
        public string SideEffects { get; set; }
    }

    public sealed class TimelineTrackMutationInfo
    {
        public TimelineObjectIdentity Identity { get; set; }
        public TimelineObjectIdentity Parent { get; set; }
        public string Name { get; set; }
        public string TrackType { get; set; }
        public string TypeName { get; set; }
    }

    public sealed class TimelineClipMutationInfo
    {
        public TimelineTrackMutationInfo Track { get; set; }
        public long Index { get; set; }
        public string DisplayName { get; set; }
        public double Start { get; set; }
        public double Duration { get; set; }
        public double End { get; set; }
        public TimelineObjectIdentity PlayableAsset { get; set; }
        public TimelineObjectIdentity SourceAsset { get; set; }
    }
}
#endif
