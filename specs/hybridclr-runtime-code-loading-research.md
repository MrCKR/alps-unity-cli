# HybridCLR Runtime Code Loading Feasibility

## Scope and conclusion

This note evaluates one narrow architecture for `alps-unity-cli`: generate complete C# source, compile it after an Android Player has been built, transfer the resulting managed DLL (and optionally symbols) to that Player, load it with `Assembly.Load`, and invoke a fixed entry point. It uses only HybridCLR and Unity primary sources. HybridCLR source evidence is pinned to `hybridclr` commit [`210cbe0`](https://github.com/focus-creative-games/hybridclr/tree/210cbe0ecc878a6ab7e392b1ce191a46d509ba25) and `hybridclr_unity` commit [`fd32708`](https://github.com/focus-creative-games/hybridclr_unity/tree/fd32708e7f5106f0ffa375bcfe43342ee594c1e5).

**Conclusion: conditionally feasible for a bounded internal Android Development-Player runner.** Post-Player-build DLL compilation and byte-array loading are core HybridCLR workflows. A full Unity project is not required on the device and is not intrinsically required by an external compiler. However, the Player is not an open-ended CLR: its HybridCLR runtime, preserved AOT API surface, bridge/native-call wrappers, and supplemental-metadata snapshots must be prepared at Player build time. Community-edition assemblies are not unloadable, so repeated per-request loads consume process-lifetime metadata/static state and eventually require Player restart. This is unsuitable as an indefinitely reusable same-process worker without commercial Hot Reload or an equivalent process-recycling boundary.

## What is directly supported

### Load bytes and invoke a fixed entry point

HybridCLR's official loading documentation says:

> "directly call `Assembly.Load(byte[] assemblyData)`" and, since v6.4.0, `Assembly.Load(byte[] assemblyData, byte[] pdbSymbolData)`.

It then demonstrates `ass.GetType(...).GetMethod(...).Invoke(...)` ([Loading and Running](https://www.hybridclr.cn/en/docs/basic/runhotupdatecodes#loading-update-assemblies)). Multiple DLLs must be loaded in dependency order.

The implementation copies the DLL and optional symbol bytes, parses them, registers an interpreter assembly, runs the module initializer, and returns that exact `Assembly` ([`Assembly.cpp` lines 93-170](https://github.com/focus-creative-games/hybridclr/blob/210cbe0ecc878a6ab7e392b1ce191a46d509ba25/hybridclr/metadata/Assembly.cpp#L93-L170)):

```cpp
Il2CppAssembly* ass = Create(...);
RunModuleInitializer(ass->image);
...
assemblyData = (const byte*)CopyBytes(assemblyData, length);
...
rawSymbolStoreBytes = (const byte*)CopyBytes(...);
err = image->LoadPDB(...);
...
il2cpp::vm::MetadataCache::RegisterInterpreterAssembly(ass);
```

This proves that a runtime tool can supply bytes from ADB, a socket, or another transport; HybridCLR does not require the DLL to have been packaged as a Unity asset. Transport behavior itself is outside the cited sources.

### Compile after the Player build

The official quick start builds a Player, then changes source, runs `HybridCLR/CompileDll/ActiveBuildTarget`, replaces the packaged DLL, and reruns the program ([Test hot update](https://www.hybridclr.cn/en/docs/beginner/quickstart#test-hot-update)). That directly proves the DLL can be produced after the Player build.

HybridCLR's supported compiler path calls Unity's `PlayerBuildInterface.CompilePlayerScripts` with an explicit `BuildTarget` and Development/Release option ([`CompileDllCommand.cs` lines 15-24](https://github.com/focus-creative-games/hybridclr_unity/blob/fd32708e7f5106f0ffa375bcfe43342ee594c1e5/Editor/Commands/CompileDllCommand.cs#L15-L24)). Its manual warns:

> "For each target, you must use hot update dlls compiled under the target platform compilation switches."

([HybridCLR package manual, Compile Dll](https://www.hybridclr.cn/en/docs/basic/com.code-philosophy.hybridclr#compile-dll)). For the proposed target, compile with Android symbols/options and the same Development setting as the Player.

HybridCLR supports all IL2CPP platforms, explicitly including Android and iOS ([Supported Versions and Platforms](https://www.hybridclr.cn/en/docs/intro#supported-versions-and-platforms)); the package build check enforces IL2CPP and an installed matching HybridCLR runtime ([`CheckSettings.cs` lines 48-75](https://github.com/focus-creative-games/hybridclr_unity/blob/fd32708e7f5106f0ffa375bcfe43342ee594c1e5/Editor/BuildProcessors/CheckSettings.cs#L48-L75)). Nothing in the cited sources requires a non-Development Player.

## Mandatory Player-build preparation

`HybridCLR/Generate/All` is documented as essential. Its implementation compiles the current hot-update DLLs, generates `link.xml`, produces post-strip AOT DLLs, generates method/reverse-PInvoke bridges, and emits AOT generic references in that order ([`PrebuildCommand.cs` lines 16-37](https://github.com/focus-creative-games/hybridclr_unity/blob/fd32708e7f5106f0ffa375bcfe43342ee594c1e5/Editor/Commands/PrebuildCommand.cs#L16-L37); [Build Workflow](https://www.hybridclr.cn/en/docs/basic/buildpipeline#build-process)). The Android Player must therefore be built with:

1. HybridCLR installed into the exact Unity/IL2CPP version and enabled for Android.
2. The fixed AOT loader/runtime entry contract included in the Player.
3. Generated code for the same Android target and Development setting.
4. A deliberately preserved future-callable AOT surface.
5. Saved post-strip AOT DLLs from that exact Player build for validation and supplemental metadata.

### Stripping is the main open-ended-code boundary

HybridCLR states that its generated `link.xml` scans only AOT references in the current hot-update DLLs, and:

> "it cannot predict the types you will use in the future. Therefore, you still need to plan ahead and reserve types you may use in the future."

It also warns that an entirely unreferenced AOT assembly can still be removed even if named in `link.xml` ([Code Stripping Principles and Solutions](https://www.hybridclr.cn/en/docs/basic/codestriping#aot-type-and-function-reservation)). Unity independently says preservation annotations are required for runtime code that does not exist during linker analysis ([Preserving code using annotations](https://docs.unity3d.com/Manual/managed-code-stripping-preserving.html)).

Therefore, compiling successfully against a reference DLL does **not** prove the member exists in the Player. The compile service should check each generated DLL against the saved post-strip AOT snapshot (HybridCLR provides `MissingMetadataChecker` on the same page), and the Player build must preserve an intentionally bounded API allowlist. A DLL that reaches outside that surface requires a new Player build.

### Bridges are mostly fixed, with native-call exceptions

HybridCLR says ordinary AOT/interpreter bridge signatures are determined by the fixed AOT side, so later hot updates do not introduce new ordinary bridges ([Bridge Functions](https://www.hybridclr.cn/en/docs/basic/methodbridge)). This supports post-build managed code generation.

However, the generator separately scans the configured hot-update DLLs for reverse P/Invoke, `calli`, and P/Invoke signatures before emitting `MethodBridge.cpp` ([`MethodBridgeGeneratorCommand.cs` lines 77-93](https://github.com/focus-creative-games/hybridclr_unity/blob/fd32708e7f5106f0ffa375bcfe43342ee594c1e5/Editor/Commands/MethodBridgeGeneratorCommand.cs#L77-L93)). **Engineering constraint:** either prohibit these features in per-request code or pre-generate every allowed signature/native target. Arbitrary new native interop is not justified by the ordinary-bridge guarantee.

## AOT generics and supplemental metadata

Generics defined wholly in hot-update code are unrestricted. Generics whose implementation is in an AOT assembly require an existing IL2CPP generic-sharing instantiation or supplemental metadata; async-generated state machines can trigger this even when source looks ordinary ([AOT Generics](https://www.hybridclr.cn/en/docs/basic/aotgeneric#aot-generic-problems)).

`RuntimeApi.LoadMetadataForAOTAssembly` can be called before generic use. `Consistent` requires the exact post-strip DLL; `SuperSet` accepts a superset/original DLL. The official guidance says post-strip DLLs are target-specific, must be saved from the Player build, and must not be replaced with newly generated AOT DLLs for each update ([Metadata Mode](https://www.hybridclr.cn/en/docs/basic/aotgeneric#metadata-mode-homologousimagemode)). Source lookup is by the already-loaded AOT assembly name, and a second metadata load for the same AOT assembly returns `HOMOLOGOUS_ASSEMBLY_HAS_BEEN_LOADED` ([`Assembly.cpp` lines 173-215](https://github.com/focus-creative-games/hybridclr/blob/210cbe0ecc878a6ab7e392b1ce191a46d509ba25/hybridclr/metadata/Assembly.cpp#L173-L215)).

Supplemental metadata is retained for process lifetime. The source contains `// FIXME. not free memory`; the official page also calls the memory unreclaimable, although its stated multiplier varies between sections. Treat memory as measured per pinned HybridCLR/Unity version, not as a stable ratio.

## Symbols and diagnostics

`Assembly.Load(dllBytes, pdbBytes)` is supported from HybridCLR v6.4.0; both arrays are copied ([Loading and Running](https://www.hybridclr.cn/en/docs/basic/runhotupdatecodes#loading-update-assemblies); [`Assembly.cpp` lines 116-134](https://github.com/focus-creative-games/hybridclr/blob/210cbe0ecc878a6ab7e392b1ce191a46d509ba25/hybridclr/metadata/Assembly.cpp#L116-L134)). The PDB reader explicitly follows the .NET Portable PDB metadata specification ([`PDBImage.cpp` line 169](https://github.com/focus-creative-games/hybridclr/blob/210cbe0ecc878a6ab7e392b1ce191a46d509ba25/hybridclr/metadata/PDBImage.cpp#L169)). Generate and transfer the Portable PDB paired with that DLL.

PDB loading must not be represented as interactive debugging support: HybridCLR's unsupported-feature list says "C#-level debugging of interpreted code is not supported" ([Unsupported Features](https://www.hybridclr.cn/en/docs/basic/notsupportedfeatures)).

## Identity, unloading, and repeated requests

### Duplicate identity

HybridCLR permits duplicate assembly instances in some paths, but documents that same-named types from two loads have different type identity and can make `GetComponent<T>` return null ([Common Errors](https://www.hybridclr.cn/en/docs/help/commonerrors#gameobjectgetcomponentt-or-gameobjectgetcomponenttype-type-returns-null)). For configured placeholder assemblies, source is stricter: a second load throws `"reloading placeholder assembly is not supported!"` ([`Assembly.cpp` lines 137-156](https://github.com/focus-creative-games/hybridclr/blob/210cbe0ecc878a6ab7e392b1ce191a46d509ba25/hybridclr/metadata/Assembly.cpp#L137-L156)).

Use the `Assembly` returned by the current load to resolve the fixed entry point. Do not rediscover it with `AppDomain.GetAssemblies().First(name == ...)`. For multiple requests in one process, use a unique simple assembly name per request and prevent cross-request type references. This is an engineering policy derived from the documented duplicate-instance behavior, not an official promise that every dependency-resolution scenario with duplicate names is safe.

An eventual unique assembly name need not be known merely for raw reflection loading: source creates a new assembly when no placeholder exists ([`Assembly.cpp` lines 152-169](https://github.com/focus-creative-games/hybridclr/blob/210cbe0ecc878a6ab7e392b1ce191a46d509ba25/hybridclr/metadata/Assembly.cpp#L152-L169)). Conversely, an assembly already present in the Unity project must be listed as hot-update code so it is removed from the AOT build ([`FilterHotFixAssemblies.cs` lines 21-65](https://github.com/focus-creative-games/hybridclr_unity/blob/fd32708e7f5106f0ffa375bcfe43342ee594c1e5/Editor/BuildProcessors/FilterHotFixAssemblies.cs#L21-L65)). Known names are also patched into Unity's scripting assembly list for serialized-resource type lookup ([`PatchScriptingAssemblyList.cs` lines 104-126](https://github.com/focus-creative-games/hybridclr_unity/blob/fd32708e7f5106f0ffa375bcfe43342ee594c1e5/Editor/BuildProcessors/PatchScriptingAssemblyList.cs#L104-L126)). Unknown per-request assemblies should therefore remain reflection/runtime-created code, not types expected in prebuilt serialized assets.

### No community-edition unload

HybridCLR describes complete unload/reload as commercial **Hot Reload Special Edition** functionality ([Hot Reload Technology](https://www.hybridclr.cn/en/docs/business/reload/hotreloadassembly)). Community source uses a monotonically advancing image-index allocator and stores each image in a static table; no free path appears in this load implementation ([`InterpreterImage.cpp` lines 42-100](https://github.com/focus-creative-games/hybridclr/blob/210cbe0ecc878a6ab7e392b1ce191a46d509ba25/hybridclr/metadata/InterpreterImage.cpp#L42-L100)). The metadata index has a finite 10-bit image field and a 1024-entry table ceiling, partitioned by DLL-size class ([`MetadataUtil.h` lines 94-120](https://github.com/focus-creative-games/hybridclr/blob/210cbe0ecc878a6ab7e392b1ce191a46d509ba25/hybridclr/metadata/MetadataUtil.h#L94-L120)). Practical exhaustion can occur below 1024 because the allocator partitions indices by image size.

Every load copies DLL/PDB bytes and allocates metadata. Even malformed images intentionally are not cleaned after parse failure ([`Assembly.cpp` lines 109-134](https://github.com/focus-creative-games/hybridclr/blob/210cbe0ecc878a6ab7e392b1ce191a46d509ba25/hybridclr/metadata/Assembly.cpp#L109-L134)). Old assembly metadata and static roots therefore survive until process exit in the community runtime.

**Engineering consequence, not a vendor lifecycle guarantee:** per-request code can also leave static fields, delegates, tasks, threads, Unity objects, and native registrations alive. A fixed entry point needs a strict completion/cleanup contract, but cleanup cannot reclaim the assembly itself. Run one request per fresh Player process for strongest isolation, or enforce a measured request/memory budget and recycle the Development Player before exhaustion. Commercial Hot Reload changes this conclusion but has its own unload restrictions.

## Project versus extracted compile kit

### Proven facts

- Runtime execution needs only the prepared Player, DLL/PDB bytes, and any required supplemental AOT metadata; `Assembly.Load(byte[])` is the runtime boundary.
- Unity officially supports managed DLLs "create[d] and compile[d] outside of Unity" and documents compiling with `csc` against Unity managed references ([Managed plug-ins](https://docs.unity3d.com/Manual/plug-ins-managed.html)).
- HybridCLR accepts regular precompiled DLLs and supports external hot-update assembly search directories ([Assembly Configuration](https://www.hybridclr.cn/en/docs/basic/hotupdateassemblysetting#regular-dll-assemblies); [package manual, external directories](https://www.hybridclr.cn/en/docs/basic/com.code-philosophy.hybridclr#externalhotupdateassemblydirs)).
- HybridCLR's own supported compiler path uses the Unity project/Editor to reproduce target-specific compilation exactly.

### Required compile-kit contents (engineering inference)

A clean extracted compiler environment is feasible, but neither HybridCLR nor Unity documents a canonical "compile kit" artifact. To be equivalent, snapshot per Player build:

- compiler/toolchain and language version;
- Android and Development-build define symbols and compiler options;
- API compatibility profile/reference assemblies;
- exact Unity, package/plugin, and project AOT assemblies exposed to generated code;
- assembly-reference graph and nullable/unsafe settings;
- the exact post-strip AOT DLL set for `MissingMetadataChecker` and supplemental metadata;
- a manifest of intentionally preserved callable members and allowed native-call signatures;
- the Player build identifier and HybridCLR package/runtime commit.

The full Unity project is therefore **not required at execution time** and can be avoided at compile time, but using the exact Unity project with `CompilePlayerScripts` is the source-proven high-fidelity path. An extracted kit is an alps-unity-cli design that must be validated against the resulting Player; official sources prove the ingredients are possible, not that an arbitrary reference-folder snapshot is sufficient.

## Recommended feasibility boundary for alps-unity-cli

Proceed only as an Android internal-development capability with these boundaries:

1. Build a dedicated HybridCLR IL2CPP Development Player with a narrow AOT loader and an explicitly preserved API surface.
2. Capture a versioned compile kit and exact post-strip AOT snapshot from that Player build.
3. Compile each request as a uniquely named, pure managed/interpreted assembly; reject unsupported references and unprepared native interop before transfer.
4. Load supplemental metadata once per required AOT assembly, then load DLL plus Portable PDB and invoke the entry point from the returned `Assembly` object.
5. Treat generated code as full-trust code. The cited community workflow provides loading, not sandboxing, cancellation, or resource cleanup.
6. Prefer one Player process per request; otherwise impose hard request-count/memory/time limits and recycle the Player.
7. Require a new Player build whenever generated code needs an unpreserved AOT member, a new native-call wrapper/signature, or a changed Unity/package/API environment.

Within those limits the architecture is real, not speculative. The unrestricted version, arbitrary C# against any Unity API indefinitely loaded into one already-built Player, is not supported by the cited implementation.