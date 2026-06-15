# ProBuilder Offline Validation Stub

This package provides the minimal `UnityEngine.ProBuilder.ProBuilderMesh` and `UnityEngine.ProBuilder.ShapeGenerator` types used by Unity CLI Bridge feature detection.

Use it only when the Windows validation runtime cannot download `com.unity.probuilder` from Unity Package Manager.

## Offline validation setup

Copy this directory into the Unity project as:

```text
<project>/Packages/com.unity.probuilder
```

Then reopen Unity or run package resolve. The package includes `Runtime/Unity.ProBuilder.asmdef` so Unity 2022.3 compiles `Runtime/ProBuilderOfflineValidation.cs` into the auto-referenced `Unity.ProBuilder` assembly. The Unity CLI Bridge `probuilder shape/create/edit/csg` commands can detect the package and exercise the CLI/Bridge positive path.

## Limits

This is not a replacement for Unity ProBuilder. It does not implement real ProBuilder geometry APIs or boolean CSG. ALP-50 validation should record that this package validates offline CLI/Bridge wiring, scene object creation, query visibility and structured envelopes; real ProBuilder geometry behavior still requires the official Unity package when available.
