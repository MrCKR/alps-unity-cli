import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

async function source(path: string): Promise<string> {
  return readFile(new URL(path, editorUrl), "utf8");
}

test("core asset and texture mutations validate paths and settings before commit", async () => {
  const [asset, importSettings, generate, sprite, textureSupport] = await Promise.all([
    source("Tools/asset/AssetToolSupport.cs"),
    source("Tools/texture/import-settings.cs"),
    source("Tools/texture/generate.cs"),
    source("Tools/texture/sprite.cs"),
    source("Tools/texture/TextureToolSupport.cs")
  ]);

  assert.match(asset, /var path = RequireAssetPath\(assetPath\);[\s\S]*AssetFullPath\(path\)/);
  assert.match(asset, /path = RequireAssetPath\(path\);[\s\S]*var fullPath = AssetFullPath\(path\);[\s\S]*CreateAssetDirectory\(path\)/);
  assert.ok(importSettings.indexOf("ValidateTextureSettings(settings)") < importSettings.indexOf("ctx.CommitEffect()"));
  assert.match(generate, /var path = TextureToolSupport\.RequireTexturePath\(request\.Path\);[\s\S]*path\.EndsWith\("\.png"/);
  assert.ok(generate.indexOf("ValidateGenerateRequest") < generate.indexOf("ctx.CommitEffect()"));
  assert.match(generate, /E_TEXTURE_PATTERN_INVALID/);
  assert.match(generate, /E_TEXTURE_COLOR_INVALID/);
  assert.match(textureSupport, /Enum\.GetNames\(typeof\(T\)\)\.Any/);
  assert.match(sprite, /observedImporter\.spritePivot\.x/);
  assert.match(sprite, /observedSlices\[index\]\.rect\.width/);
  assert.match(sprite, /observedSlices\[index\]\.alignment/);
});

test("ScriptableObject and UI mutations establish all preconditions before their commit", async () => {
  const [scriptableSupport, scriptableHelpers, scriptableCreate, scriptableModify, uiSupport, documentCreate, documentDelete, modify] = await Promise.all([
    source("Tools/scriptable-object/ScriptableObjectToolSupport.cs"),
    source("Infrastructure/UnityCliScriptableObjectHelpers.cs"),
    source("Tools/scriptable-object/create.cs"),
    source("Tools/scriptable-object/modify.cs"),
    source("Tools/ui/UiToolSupport.cs"),
    source("Tools/ui/document-create.cs"),
    source("Tools/ui/document-delete.cs"),
    source("Tools/ui/modify-visual-element.cs")
  ]);

  assert.match(scriptableSupport, /segment == "\." \|\| segment == "\.\."/);
  assert.match(scriptableSupport, /type\.IsAbstract \|\| type\.ContainsGenericParameters/);
  assert.match(scriptableHelpers, /PrepareObjectMemberWrite[\s\S]*ConvertScriptableObjectValue/);
  assert.match(scriptableHelpers, /ObjectMemberValueMatches[\s\S]*Equals\(actual, write\.ExpectedValue\)/);
  assert.ok(scriptableModify.indexOf("PrepareObjectMemberWrite") < scriptableModify.indexOf("ctx.CommitEffect()"));
  assert.match(scriptableModify, /AssetPathToGUID\(path\)[\s\S]*GUIDToAssetPath\(assetGuid\)[\s\S]*ObjectMemberValueMatches/);
  assert.ok(scriptableCreate.indexOf("ResolveScriptableObjectType") < scriptableCreate.indexOf("ctx.CommitEffect()"));
  assert.match(uiSupport, /RequireUssPath/);
  assert.match(uiSupport, /RequireUiDocumentAssetPath/);
  assert.ok(documentCreate.indexOf("RequireUssPath") < documentCreate.indexOf("ctx.CommitEffect()"));
  assert.ok(documentCreate.indexOf("RequireExistingAssetPath") < documentCreate.indexOf("ctx.CommitEffect()"));
  assert.ok(documentDelete.indexOf("RequireUiDocumentAssetPath") < documentDelete.indexOf("ctx.CommitEffect()"));
  assert.ok(documentDelete.indexOf("AssetToolSupport.AssetExists") < documentDelete.indexOf("ctx.CommitEffect()"));
  assert.ok(modify.indexOf("ValidateVisualElementSize") < modify.indexOf("ctx.CommitEffect()"));
  assert.match(modify, /var targetId = resolvedGo\.GetInstanceID\(\);/);
  assert.match(modify, /EditorUtility\.InstanceIDToObject\(targetId\) as GameObject/);
  assert.match(modify, /var observedDocument = observedGo == null/);
});
