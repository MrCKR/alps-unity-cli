# ProBuilder Extension

The ProBuilder extension package (`unity-cli-extension-probuilder`) provides 36 single-action tools for ProBuilder mesh editing in Unity.

## Prerequisites

- Unity project with `com.unity.probuilder` package installed
- alps-unity-cli with bridge connection to a running Unity Editor

## Tool Groups

All tools are in the `probuilder` group. Discover with:

```
unity_cli tools list --group probuilder
```

Describe a specific tool:

```
unity_cli tools describe probuilder.mesh-info
```

## Tool Categories

| Category | Tools |
|----------|-------|
| Utility | `probuilder.ping` |
| Shape Creation | `probuilder.create-shape`, `probuilder.create-poly-shape` |
| Face Operations | `probuilder.extrude-faces`, `probuilder.delete-faces`, `probuilder.detach-faces`, `probuilder.merge-faces`, `probuilder.subdivide`, `probuilder.flip-normals`, `probuilder.select-faces`, `probuilder.set-face-material`, `probuilder.set-face-color`, `probuilder.set-face-uvs` |
| Edge Operations | `probuilder.extrude-edges`, `probuilder.bevel-edges`, `probuilder.bridge-edges`, `probuilder.connect-elements` |
| Mesh Combine/Merge | `probuilder.combine-meshes`, `probuilder.merge-objects`, `probuilder.duplicate-and-flip`, `probuilder.create-polygon` |
| Vertex Operations | `probuilder.merge-vertices`, `probuilder.weld-vertices`, `probuilder.split-vertices`, `probuilder.move-vertices`, `probuilder.insert-vertex`, `probuilder.append-vertices-to-edge` |
| Info/Inspection | `probuilder.mesh-info`, `probuilder.validate-mesh` |
| Conversion | `probuilder.convert-to-probuilder` |
| Smoothing | `probuilder.set-smoothing`, `probuilder.auto-smooth` |
| Pivot/Transform | `probuilder.center-pivot`, `probuilder.freeze-transform`, `probuilder.set-pivot` |
| Repair | `probuilder.repair-mesh` |

## Dependency Missing

When `com.unity.probuilder` is not installed, all 36 tools return `E_PROBUILDER_PACKAGE_NOT_FOUND` with next actions to install the package.
