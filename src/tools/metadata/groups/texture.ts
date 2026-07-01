import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

// TextureImporter 枚举值，对齐 CoplayDev manage_texture import_settings 的可读枚举文档
const TEXTURE_TYPE_ENUM = ["Default", "NormalMap", "GUI", "Sprite", "Cursor", "Cookie", "Lightmap", "SingleChannel"];
const WRAP_MODE_ENUM = ["Repeat", "Clamp", "Mirror", "MirrorOnce"];
const FILTER_MODE_ENUM = ["Point", "Bilinear", "Trilinear"];
const COMPRESSION_ENUM = ["Uncompressed", "Compressed", "CompressedLQ", "CompressedHQ"];

export const TEXTURE_TOOLS: ToolMetadata[] = [
  {
    name: "texture.info",
    summary: "Read dimensions, format, mip/stream flags and TextureImporter settings of a texture asset.",
    description: "Read dimensions, format, mip/stream flags and TextureImporter settings of a texture asset. Use this before texture.import-settings to inspect current values, or before texture.sprite to confirm the texture type. Use asset.search with filter t:Texture2D to discover paths when unknown.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/* texture path (.png/.jpg/.jpeg/.tga/.psd/.exr)." } }, ["path"]),
    outputSchema: objSchema({
      texture: objSchema({
        path: { type: "string" },
        guid: { type: "string" },
        name: { type: "string" },
        width: { type: "integer" },
        height: { type: "integer" },
        format: { type: "string" },
        mipCount: { type: "integer" },
        isReadable: { type: "boolean" }
      }),
      importSettings: objSchema({
        textureType: { type: "string" },
        textureShape: { type: "string" },
        wrapMode: { type: "string" },
        filterMode: { type: "string" },
        maxTextureSize: { type: "integer" },
        textureCompression: { type: "string" },
        mipmapEnabled: { type: "boolean" },
        sRGBTexture: { type: "boolean" }
      })
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Inspect a texture and its importer settings", argv: ["tools", "run", "texture.info", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\"}"] }
    ]
  },
  {
    name: "texture.create",
    summary: "Create a PNG/JPG texture asset under Assets/.",
    description: "Create a texture asset. With a source path it copies an existing image file; otherwise it generates a solid-color .png/.jpg of the given width/height. Refuses to overwrite an existing asset unless overwrite is true. Use texture.generate for patterns/gradients/noise, or texture.import-settings to tune the importer afterward.",
    group: "texture",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/* texture path. Supported: .png/.jpg/.jpeg/.tga/.psd/.exr (generated output is .png/.jpg only)." },
      source: { type: "string", description: "Optional source image file or Assets/* texture to copy. Omit to generate a solid-color texture." },
      width: { type: "integer", default: 1, description: "Generated texture width (pixels). Ignored when source is supplied." },
      height: { type: "integer", default: 1, description: "Generated texture height (pixels). Ignored when source is supplied." },
      color: { type: "string", description: "Solid fill color for generated textures, e.g. ffffff or #ff00ffff. Defaults to white." },
      overwrite: { type: "boolean", description: "Allow replacing an existing texture asset." }
    }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Create a solid color texture", argv: ["tools", "run", "texture.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedSolid.png\",\"width\":64,\"height\":64,\"color\":\"#ff0000\"}"] }
    ]
  },
  {
    name: "texture.sprite",
    summary: "Convert a texture asset's importer to Sprite type and optionally set sprite mode, pixels-per-unit and pivot.",
    description: "Convert a texture asset's importer to Sprite type and optionally set sprite mode, pixels-per-unit and pivot. This reimports the texture. Use texture.info first to confirm the current type, and asset.info to find sprite sub-assets afterward.",
    group: "texture",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/* texture path." },
      spriteMode: { type: "string", enum: ["Single", "Multiple"], description: "Sprite import mode. Omit to keep the importer default." },
      pixelsPerUnit: { type: "integer", description: "Sprite pixels-per-unit. Must be > 0." },
      pivotX: { type: "string", description: "Sprite pivot X (0..1 normalized)." },
      pivotY: { type: "string", description: "Sprite pivot Y (0..1 normalized)." },
      slices: { type: "array", items: objSchema({ name: { type: "string" }, x: { type: "number" }, y: { type: "number" }, width: { type: "number" }, height: { type: "number" }, pivotX: { type: "number" }, pivotY: { type: "number" } }), description: "Optional sprite sheet rectangles for Multiple mode. Coordinates are pixel-space x/y/width/height with optional normalized pivotX/pivotY." }
    }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Convert a texture to a Single sprite", argv: ["tools", "run", "texture.sprite", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\",\"spriteMode\":\"Single\",\"pixelsPerUnit\":100}"] },
      { description: "Slice a 64x32 spritesheet into two sprites", argv: ["tools", "run", "texture.sprite", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/Sheet.png\",\"spriteMode\":\"Multiple\",\"slices\":[{\"name\":\"left\",\"x\":0,\"y\":0,\"width\":32,\"height\":32},{\"name\":\"right\",\"x\":32,\"y\":0,\"width\":32,\"height\":32}]}"] }
    ]
  },
  {
    name: "texture.modify",
    summary: "Modify an existing PNG/JPG texture by filling a pixel region with one color.",
    description: "Modify an existing PNG/JPG texture asset by filling a pixel region with one color, then reimport it. Use this for Unity-side texture mutations; for arbitrary image editing, write the file directly and call asset.refresh. Supports --dry-run to preview the affected region.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.png/.jpg/.jpeg texture path." }, color: { type: "string", description: "Fill color as #RRGGBB, #RRGGBBAA, or hex without #." }, fillColor: { type: "string", description: "Alias of color." }, x: { type: "integer", default: 0, description: "Region origin X in pixels." }, y: { type: "integer", default: 0, description: "Region origin Y in pixels." }, width: { type: "integer", description: "Region width in pixels. Defaults to remaining texture width." }, height: { type: "integer", description: "Region height in pixels. Defaults to remaining texture height." } }, ["path", "color"]),
    outputSchema: objSchema({ texture: { type: "object" }, importSettings: { type: "object" }, changedPixels: { type: "integer" }, region: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: true,
    sideEffects: ["asset-write", "asset-import"],
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview filling the full texture", argv: ["tools", "run", "texture.modify", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\",\"color\":\"#ff0000\"}", "--dry-run"] },
      { description: "Fill a 16x16 region after confirmation", argv: ["tools", "run", "texture.modify", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\",\"color\":\"#00ff00\",\"x\":0,\"y\":0,\"width\":16,\"height\":16}", "--yes"] }
    ]
  },
  {
    name: "texture.delete",
    summary: "Delete an existing texture asset through a texture-specific destructive tool.",
    description: "Delete an existing texture asset through a texture-specific destructive tool. This is equivalent in effect to asset.delete for texture files, but is discoverable from the texture group. Use --dry-run before --yes when the path was discovered programmatically.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/* texture path to delete." } }, ["path"]),
    outputSchema: objSchema({ texture: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: true,
    sideEffects: ["asset-delete", "asset-import"],
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview deleting a generated texture", argv: ["tools", "run", "texture.delete", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedSolid.png\"}", "--dry-run"] },
      { description: "Delete a generated texture after confirmation", argv: ["tools", "run", "texture.delete", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedSolid.png\"}", "--yes"] }
    ]
  },
  {
    name: "texture.import-settings",
    summary: "Read or apply TextureImporter settings on a texture asset.",
    description: "Read and/or apply TextureImporter settings on a texture asset. With no setting fields it is a read returning current importSettings; with any setting field it reimports. Enum values mirror Unity's TextureImporter. Use texture.info for a pure read that never reimports.",
    group: "texture",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/* texture path." },
      textureType: { type: "string", enum: TEXTURE_TYPE_ENUM, description: "Importer texture type." },
      readable: { type: "boolean", description: "isReadable: CPU-accessible pixel data." },
      mipmap: { type: "boolean", description: "Generate mipmaps." },
      sRGB: { type: "boolean", description: "sRGB color texture (disable for data/normal maps)." },
      alphaIsTransparency: { type: "boolean", description: "Treat alpha as transparency." },
      wrapMode: { type: "string", enum: WRAP_MODE_ENUM, description: "Texture wrap mode." },
      filterMode: { type: "string", enum: FILTER_MODE_ENUM, description: "Sampling filter mode." },
      compression: { type: "string", enum: COMPRESSION_ENUM, description: "Texture compression." },
      maxTextureSize: { type: "integer", description: "Max texture size (32/64/128/.../8192/16384)." }
    }, ["path"]),
    outputSchema: objSchema({
      texture: objSchema({ path: { type: "string" }, guid: { type: "string" } }),
      importSettings: objSchema({ textureType: { type: "string" }, wrapMode: { type: "string" }, filterMode: { type: "string" }, maxTextureSize: { type: "integer" }, textureCompression: { type: "string" } }),
      changed: { type: "boolean", description: "True when settings were applied and the asset reimported." }
    }),
    destructiveHint: false,
    sideEffects: ["asset-import"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Read current import settings (no mutation)", argv: ["tools", "run", "texture.import-settings", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\"}"] },
      { description: "Set compression and max size", argv: ["tools", "run", "texture.import-settings", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\",\"compression\":\"Compressed\",\"maxTextureSize\":512}"] }
    ]
  },
  {
    name: "texture.generate",
    summary: "Generate a procedural texture pattern and save it as PNG under Assets/.",
    description: "Generate a procedural texture and save it as PNG under Assets/. Patterns: solid, gradient (linear), radial, noise (Perlin), checkerboard, stripes, dots, grid, brick. color/color2 accept #hex; patternSize controls cell size. Supports --dry-run to preview planned effects without writing. Prefer this over texture.create for test/debug textures.",
    group: "texture",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/*.png path." },
      width: { type: "integer", default: 256, description: "Texture width, clamped 1..4096." },
      height: { type: "integer", default: 256, description: "Texture height, clamped 1..4096." },
      pattern: { type: "string", enum: ["solid", "gradient", "radial", "noise", "checkerboard", "stripes", "dots", "grid", "brick"], default: "solid", description: "Procedural pattern." },
      color: { type: "string", description: "Primary color (#hex). Defaults to white." },
      color2: { type: "string", description: "Secondary color (#hex). Defaults to black." },
      patternSize: { type: "integer", default: 8, description: "Cell size in pixels for pattern types." }
    }, ["path"]),
    destructiveHint: true,
    sideEffects: ["asset-write", "asset-import"],
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview a checkerboard (dry-run)", argv: ["tools", "run", "texture.generate", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedChecker.png\",\"pattern\":\"checkerboard\",\"patternSize\":32}", "--dry-run"] },
      { description: "Generate a gradient texture", argv: ["tools", "run", "texture.generate", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedGradient.png\",\"pattern\":\"gradient\",\"color\":\"#ff0000\",\"color2\":\"#0000ff\"}"] }
    ]
  }
];
