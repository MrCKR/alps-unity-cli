import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";

const root = process.cwd();
const read = (relative: string): string => readFileSync(path.join(root, relative), "utf8");
const tool = read("unitypkg/Editor/Tools/timeline/create.cs");
const support = read("unitypkg/Editor/Tools/timeline/TimelineToolSupport.cs");
const infoTool = read("unitypkg/Editor/Tools/timeline/info.cs");
const trackAddTool = read("unitypkg/Editor/Tools/timeline/track/add.cs");
const clipAddTool = read("unitypkg/Editor/Tools/timeline/clip/add.cs");
const readSupport = read("unitypkg/Editor/Tools/timeline/TimelineReadToolSupport.cs");
const mutationSupport = read("unitypkg/Editor/Tools/timeline/TimelineMutationToolSupport.cs");
const bridge = read("src/bridge/domains/timeline.ts");

test("timeline.create owns the canonical package-optional create route", () => {
  assert.match(tool, /Name = "timeline\.create"/);
  assert.match(tool, /RequiresPackages = new\[\] \{ TimelineToolSupport\.PackageId \}/);
  assert.match(tool, /PlayModeSupport = "editor-only"/);
  assert.match(tool, /SupportsDryRun = true/);
  assert.match(tool, /DestructiveHint = true/);
  assert.match(bridge, /toolName: "timeline\.create"/);
  assert.match(bridge, /frameRate: floatFlag\(flags, "frame-rate"\)/);
  assert.match(bridge, /overwrite: boolFlag\(flags, "overwrite"\)/);
});

test("timeline.create remains compile-time independent from Unity Timeline", () => {
  assert.doesNotMatch(tool, /using UnityEngine\.Timeline/);
  assert.doesNotMatch(support, /using UnityEngine\.Timeline/);
  assert.match(support, /Type\.GetType\(TimelineAssetTypeName, false\)/);
  assert.match(support, /ScriptableObject\.CreateInstance\(plan\.TimelineType\)/);
  assert.match(support, /GetProperty\("editorSettings"/);
  assert.match(support, /GetProperty\("frameRate"/);
});

test("timeline.create validates the full plan before one effect barrier", () => {
  const prepareIndex = tool.indexOf("TimelineToolSupport.PrepareCreate");
  const dryRunIndex = tool.indexOf("if (ctx.DryRun)");
  const barrierIndex = tool.indexOf("ctx.CommitEffect();");
  const createIndex = tool.indexOf("TimelineToolSupport.Create(plan)");
  const proofIndex = tool.indexOf("TimelineToolSupport.ProveCreate(plan)");
  assert.ok(prepareIndex >= 0 && prepareIndex < dryRunIndex);
  assert.ok(dryRunIndex < barrierIndex && barrierIndex < createIndex && createIndex < proofIndex);
  assert.equal(tool.match(/ctx\.CommitEffect\(\);/g)?.length, 1);
  assert.match(support, /E_TIMELINE_OUTPUT_TYPE_CONFLICT/);
  assert.match(support, /E_TIMELINE_OUTPUT_META_EXISTS/);
  assert.match(support, /E_TIMELINE_OUTPUT_EXISTS/);
  assert.match(support, /frameRate < 0\.000001d \|\| frameRate > 1000d/);
  assert.match(support, /E_TIMELINE_FRAME_RATE_INVALID/);
  assert.match(support, /EndsWith\("\.playable"/);
});

test("timeline.create proves fresh path GUID type frame-rate and serialized file", () => {
  assert.match(support, /LoadMainAssetAtPath\(plan\.Path\)/);
  assert.match(support, /AssetDatabase\.AssetPathToGUID\(plan\.Path\)/);
  assert.match(support, /AssetDatabase\.GUIDToAssetPath\(guid\)/);
  assert.match(support, /plan\.Existing && !string\.Equals\(guid, plan\.ExistingGuid/);
  assert.match(support, /plan\.TimelineType\.IsInstanceOfType\(asset\)/);
  assert.match(support, /Math\.Abs\(frameRate - plan\.FrameRate\)/);
  assert.match(support, /!file\.Exists \|\| file\.Length <= 0L/);
  assert.match(support, /E_TIMELINE_CREATE_PROOF_FAILED/);
});

test("timeline.info is a package-optional read-only bounded Tool", () => {
  assert.match(infoTool, /Name = "timeline\.info"/);
  assert.match(infoTool, /DestructiveHint = false/);
  assert.match(infoTool, /SideEffects = new string\[0\]/);
  assert.match(infoTool, /SupportsDryRun = false/);
  assert.match(infoTool, /PlayModeSupport = "supported"/);
  assert.doesNotMatch(infoTool + readSupport, /using UnityEngine\.Timeline/);
  assert.doesNotMatch(infoTool + readSupport, /CommitEffect|AssetDatabase\.(Refresh|ImportAsset|SaveAssets|CreateAsset|DeleteAsset)|EditorUtility\.SetDirty/);
});

test("timeline.info bounds hierarchy pagination and per-track clip output", () => {
  assert.match(readSupport, /MaxPageSize = 200/);
  assert.match(readSupport, /MaxDepthLimit = 32/);
  assert.match(readSupport, /MaxClipsPerTrackLimit = 500/);
  assert.match(readSupport, /MaxScannedTracks = 10000/);
  assert.match(readSupport, /MaxScannedClipsPerTrack = 10000/);
  assert.match(readSupport, /WalkTrack\(track, null, 0, plan\.MaxDepth/);
  assert.match(readSupport, /Cursor = plan\.Cursor/);
  assert.match(readSupport, /NextCursor = end < nodes\.Count/);
  assert.match(readSupport, /ClipsTruncated = allClips\.Count > returnedClipCount/);
  assert.match(readSupport, /DepthTruncated = depthTruncated/);
});

test("timeline.info returns stable track parent clip and output binding identities", () => {
  assert.match(readSupport, /TryGetGUIDAndLocalFileIdentifier\(value, out guid, out localId\)/);
  assert.match(readSupport, /public string LocalId \{ get; set; \}/);
  assert.match(readSupport, /localId\.ToString\(CultureInfo\.InvariantCulture\)/);
  assert.match(readSupport, /Parent = node\.Parent == null \? null : RequirePersistentIdentity/);
  assert.match(readSupport, /PlayableAsset = asset == null \? null : RequirePersistentIdentity/);
  assert.match(readSupport, /SourceAsset = ResolveSourceAsset\(asset\)/);
  assert.match(readSupport, /OutputTargetType = outputType == null \? null : outputType\.FullName/);
  assert.match(readSupport, /new\[\] \{ "clip", "prefabGameObject" \}/);
});

test("timeline.track.add freezes built-in type parent and sibling identity before one effect", () => {
  assert.match(trackAddTool, /Name = "timeline\.track\.add"/);
  assert.match(trackAddTool, /SupportsDryRun = true/);
  assert.match(trackAddTool, /PlayModeSupport = "editor-only"/);
  assert.equal(trackAddTool.match(/ctx\.CommitEffect\(\);/g)?.length, 1);
  assert.ok(trackAddTool.indexOf("PrepareTrackAdd") < trackAddTool.indexOf("if (ctx.DryRun)"));
  assert.ok(trackAddTool.indexOf("if (ctx.DryRun)") < trackAddTool.indexOf("ctx.CommitEffect();"));
  assert.match(mutationSupport, /TimelineCreateUtilitiesTypeName/);
  assert.match(mutationSupport, /ValidateParentTrack/);
  assert.match(mutationSupport, /E_TIMELINE_TRACK_AMBIGUOUS/);
  assert.match(mutationSupport, /E_TIMELINE_TRACK_EXISTS/);
  assert.match(mutationSupport, /records\.Count != plan\.TrackCountBefore \+ 1/);
  assert.match(mutationSupport, /SameIdentity\(observed\.ParentIdentity/);
});

test("timeline.clip.add freezes track source API and timing before one effect", () => {
  assert.match(clipAddTool, /Name = "timeline\.clip\.add"/);
  assert.match(clipAddTool, /SupportsDryRun = true/);
  assert.match(clipAddTool, /PlayModeSupport = "editor-only"/);
  assert.equal(clipAddTool.match(/ctx\.CommitEffect\(\);/g)?.length, 1);
  assert.ok(clipAddTool.indexOf("PrepareClipAdd") < clipAddTool.indexOf("if (ctx.DryRun)"));
  assert.ok(clipAddTool.indexOf("if (ctx.DryRun)") < clipAddTool.indexOf("ctx.CommitEffect();"));
  assert.match(mutationSupport, /ReadMaxClipTime/);
  assert.match(mutationSupport, /ResolveCreateClip/);
  assert.match(mutationSupport, /HasDefaultClipType/);
  assert.match(mutationSupport, /E_TIMELINE_CLIP_ASSET_REQUIRED/);
  assert.match(mutationSupport, /clips\.Count != plan\.ClipCountBefore \+ 1/);
  assert.match(mutationSupport, /Math\.Abs\(observedStart - plan\.Start\)/);
  assert.match(mutationSupport, /SameIdentity\(sourceIdentity, plan\.SourceIdentity\)/);
});

test("timeline mutations prove fresh stable identities and serialized hash changes", () => {
  assert.match(mutationSupport, /TryGetGUIDAndLocalFileIdentifier\(value, out guid, out localId\)/);
  assert.match(mutationSupport, /localId\.ToString\(CultureInfo\.InvariantCulture\)/);
  assert.match(mutationSupport, /AssetDatabase\.ImportAsset\(plan\.Timeline\.Path, ImportAssetOptions\.ForceSynchronousImport\)/);
  assert.match(mutationSupport, /RequireFreshTimeline\(plan\.Timeline\)/);
  assert.match(mutationSupport, /HashChanged\(plan\.Timeline\)/);
  assert.match(mutationSupport, /E_TIMELINE_TRACK_ADD_PROOF_FAILED/);
  assert.match(mutationSupport, /E_TIMELINE_CLIP_ADD_PROOF_FAILED/);
});

test("timeline bridge exposes exactly the four approved canonical routes", () => {
  assert.match(bridge, /toolName: "timeline\.info"/);
  assert.match(bridge, /toolName: "timeline\.track\.add"/);
  assert.match(bridge, /toolName: "timeline\.clip\.add"/);
  assert.match(bridge, /pageSize: numberFlag\(flags, "page-size"\)/);
  assert.match(bridge, /parentLocalId: stringFlag\(flags, "parent-local-id"\)/);
  assert.match(bridge, /trackLocalId: stringFlag\(flags, "track-local-id"\)/);
  assert.match(bridge, /maxDepth: numberFlag\(flags, "max-depth"\)/);
  assert.match(bridge, /maxClipsPerTrack: numberFlag\(flags, "max-clips-per-track"\)/);
  assert.equal(bridge.match(/toolName: "timeline\./g)?.length, 4);
  assert.doesNotMatch(bridge, /get_timeline|create_timeline|add_timeline/);
});
