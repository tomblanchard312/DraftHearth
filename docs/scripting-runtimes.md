# Scripting runtime packaging

VBA is bundled with its own .NET runtime and compiler. The installed GUI checks its adjacent
`runtimes/vba` first, then the app-specific FHS directory under `lib`. A development SDK is
not required on the user's machine. Run `python3 packaging/stage-runtimes.py --help` for the
stager used by every native packaging script. It includes runtime/compiler licences and
third-party notices, writes a target/component manifest, and compiles a synthetic macro on native
builds with external SDK lookup disabled.

Native Linux staging example:

```
python3 packaging/stage-runtimes.py --rid linux-x64 --out target/runtime-stage/runtimes
```

For a local developer install, copy the complete `runtimes` tree beside the versioned GUI
binary. Do not copy only the executable. Run the stager's `--verify-only` check against the
installed runtime directory. The source Flatpak manifest consumes this pre-staged directory;
tar/Flatpak/AppImage repackaging must preserve the application's `lib` tree.

Cross-builds are not native validation. FreeBSD and universal macOS builds require a tested,
self-contained prebuilt VBA directory with matching `runtime-info.json`, supplied through
`NISABA_VBA_RUNTIME_DIR`. The stager fails rather than publishing a partial package. macOS
JIT entitlements apply to the helper, not the GUI.
