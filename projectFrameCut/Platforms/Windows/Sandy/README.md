Sandy v0.9996 (`c14ac771708a003df84cdf1fb978235dcab40cc3`), MIT:
https://github.com/ahrvoje/sandy_cli

`win-x64/sandy.exe` is the upstream release, SHA-256:
`40c1ad5b6236f7b0d3d76103d665f96cf992772b84ecfc7533aff33a4b442549`.

`win-arm64/sandy.exe` is built from the same source with native ARM64 project
configurations and toml11 v4.4.0. Both executables embed their matching pipe hook
DLL; projectFrameCut uses explicit pipe names and does not enable the hook.

The bundled ARM64 executable was built with MSVC 14.52.37025, Windows SDK
10.0.26100.0 and `/utf-8`, SHA-256:
`beeac83b3ee5b4e005184c5f8815b66a2151aa1690b39f3c2befa2084f8bded0`.

To prepare both runtimes, run `./tools/Prepare-Sandy.ps1` from PowerShell with
Visual Studio C++ ARM64 build tools and the Windows SDK installed. For a different
Visual Studio installation, pass `-MSBuildPath` and `-PlatformToolset`.

The package includes the runtime matching its Windows RID, plus both licenses.
There are no runtime downloads.

Each package family uses one persistent `pjfc-<SHA256(PFN)[0..32]>` profile.
The host owns directory grants; Sandy owns the profile and run bookkeeping.
Worker shutdown retains the profile. Network settings select `internetClient`
or `internetClientServer` in `[capabilities]`; loopback access also adds
`privateNetworkClientServer` and uses the elevated CLI for its Windows exemption.
Capability changes take effect after restarting projectFrameCut. The next launch
updates `profile.toml` and the saved profile's capability list, keeping its SID
and grants. The saved capability fields follow the bundled Sandy v0.9996 format.
