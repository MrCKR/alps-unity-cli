import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

const mutationTools = [
  ["object.modify", "Tools/object/modify.cs"],
  ["physics.assign-material", "Tools/physics/assign-material.cs"],
  ["physics.collision-matrix", "Tools/physics/collision-matrix.cs"],
  ["physics.force", "Tools/physics/force.cs"],
  ["physics.joint.add", "Tools/physics/joint/add.cs"],
  ["physics.joint.remove", "Tools/physics/joint/remove.cs"],
  ["physics.material", "Tools/physics/material.cs"],
  ["physics.rigidbody", "Tools/physics/rigidbody.cs"],
  ["physics.settings", "Tools/physics/settings.cs"],
  ["physics.simulation", "Tools/physics/simulation.cs"],
  ["prefab.create", "Tools/prefab/create.cs"],
  ["prefab.instantiate", "Tools/prefab/instantiate.cs"],
  ["prefab.modify", "Tools/prefab/modify.cs"],
  ["prefab.stage-close", "Tools/prefab/stage-close.cs"],
  ["prefab.stage-open", "Tools/prefab/stage-open.cs"],
  ["prefab.stage-save", "Tools/prefab/stage-save.cs"],
  ["prefab.unpack", "Tools/prefab/unpack.cs"],
  ["profiler.capture", "Tools/profiler/capture.cs"],
  ["profiler.clear", "Tools/profiler/clear.cs"],
  ["profiler.deep", "Tools/profiler/deep.cs"],
  ["profiler.export", "Tools/profiler/export.cs"],
  ["profiler.frame", "Tools/profiler/frame.cs"],
  ["profiler.frame-debugger.disable", "Tools/profiler/frame-debugger/disable.cs"],
  ["profiler.frame-debugger.enable", "Tools/profiler/frame-debugger/enable.cs"],
  ["profiler.load", "Tools/profiler/load.cs"],
  ["profiler.memory-snapshot.take", "Tools/profiler/memory-snapshot/take.cs"],
  ["profiler.module-enable", "Tools/profiler/module-enable.cs"],
  ["profiler.pause", "Tools/profiler/pause.cs"],
  ["profiler.start", "Tools/profiler/start.cs"],
  ["profiler.stop", "Tools/profiler/stop.cs"],
  ["project.layer.add", "Tools/project/ProjectTagLayerToolSupport.cs"],
  ["project.layer.remove", "Tools/project/ProjectTagLayerToolSupport.cs"],
  ["project.tag.add", "Tools/project/ProjectTagLayerToolSupport.cs"],
  ["project.tag.remove", "Tools/project/ProjectTagLayerToolSupport.cs"],
  ["reflection.method.call", "Tools/reflection/method/call.cs"],
  ["scene.create", "Tools/scene/create.cs"],
  ["scene.load", "Tools/scene/load.cs"],
  ["scene.move-object", "Tools/scene/move-object.cs"],
  ["scene.save", "Tools/scene/save.cs"],
  ["scene.set-active", "Tools/scene/set-active.cs"],
  ["scene.unload", "Tools/scene/unload.cs"],
  ["scene.view-frame", "Tools/scene/view-frame.cs"],
  ["screenshot.game-view", "Tools/screenshot/game-view.cs"],
  ["screenshot.isolated-object", "Tools/screenshot/isolated-object.cs"],
  ["screenshot.orbit", "Tools/screenshot/orbit.cs"],
  ["screenshot.scene-view", "Tools/screenshot/scene-view.cs"],
  ["screenshot.surround", "Tools/screenshot/surround.cs"],
  ["scriptable-object.create", "Tools/scriptable-object/create.cs"],
  ["scriptable-object.modify", "Tools/scriptable-object/modify.cs"],
  ["shader.create", "Tools/shader/create.cs"],
  ["shader.delete", "Tools/shader/delete.cs"],
  ["shader.update", "Tools/shader/update.cs"],
  ["texture.create", "Tools/texture/create.cs"],
  ["texture.delete", "Tools/texture/delete.cs"],
  ["texture.generate", "Tools/texture/generate.cs"],
  ["texture.import-settings", "Tools/texture/import-settings.cs"],
  ["texture.modify", "Tools/texture/modify.cs"],
  ["texture.sprite", "Tools/texture/sprite.cs"],
  ["ui.attach-document", "Tools/ui/attach-document.cs"],
  ["ui.button.create", "Tools/ui/button/create.cs"],
  ["ui.canvas.create", "Tools/ui/canvas/create.cs"],
  ["ui.create-panel-settings", "Tools/ui/create-panel-settings.cs"],
  ["ui.detach-document", "Tools/ui/detach-document.cs"],
  ["ui.document.create", "Tools/ui/document/create.cs"],
  ["ui.document.delete", "Tools/ui/document/delete.cs"],
  ["ui.document.update", "Tools/ui/document/update.cs"],
  ["ui.image.create", "Tools/ui/image/create.cs"],
  ["ui.link-stylesheet", "Tools/ui/link-stylesheet.cs"],
  ["ui.modify-visual-element", "Tools/ui/modify-visual-element.cs"],
  ["ui.render-preview", "Tools/ui/render-preview.cs"],
  ["ui.text.create", "Tools/ui/text/create.cs"],
  ["ui.update-panel-settings", "Tools/ui/update-panel-settings.cs"],
  ["package.embed", "Tools/package/embed.cs"]
] as const;

test("core B mutation tools commit exactly once before their effect", async () => {
  for (const [tool, path] of mutationTools) {
    const source = await readFile(new URL(path, editorUrl), "utf8");
    const commits = source.match(/ctx\.CommitEffect\(\)/g) ?? [];
    assert.ok(commits.length >= 1, `${tool} must commit before its first real effect`);
  }
});

test("reflection and profiler load commit at their first irreversible effect", async () => {
  const [reflection, profilerLoad] = await Promise.all([
    readFile(new URL("Tools/reflection/method/call.cs", editorUrl), "utf8"),
    readFile(new URL("Tools/profiler/load.cs", editorUrl), "utf8")
  ]);
  assert.match(reflection, /ctx\.CommitEffect\(\);\s*var data = UnityCliBridge\.ReflectionInvoker\.CallMethod/s);
  assert.match(profilerLoad, /ctx\.CommitEffect\(\);\s*ProfilerToolSupport\.SetProfilerSessionEnabled\(false\)/s);
});
