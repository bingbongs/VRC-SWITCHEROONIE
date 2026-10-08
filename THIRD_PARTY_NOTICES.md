# Third-party components

The original VRC-SWITCHEROONIE implementation is MIT licensed. It is not affiliated with Valve, VRChat, PICO, Virtual Desktop, or the reference video creator.

- Valve OpenVR SDK: BSD 3-clause license, revision `0924064316de3effbcd1acf1e309182a2deb1c05`. Headers, x64 import library and redistributable API DLL are vendored under `third_party/openvr`; license is included.
- TsudaKageyu MinHook: BSD 2-clause license, v1.3.4 revision `c3fcafdc10146beb5919319d0683e44e3c30d537`. Vendored under `third_party/minhook`; license is included. Additional decoder notices appear in that license.
- .NET and Windows Desktop runtime: Microsoft .NET redistributables included by `dotnet publish --self-contained`. Their generated license and third-party-notice files accompany the package.
- GitHub Releases and GitHub Actions host source, build checks and signed portable update assets. No GitHub credential is shipped with the program.
- OpenAI Codex and collaborating coding agents produced the implementation, directed by bingbongs. Entirely vibe coded using 6.1 sol.
- The included mascot atlas is user-supplied artwork. OpenAI image generation was used during mascot development. The mascot is rendered locally; the app has no image-generation API dependency.

No source or binary from VRDesktopShifter is included. The supplied document is a requirements/research reference, not proof of a working backend or vendor approval.
