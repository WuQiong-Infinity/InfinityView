# Third-party notices

InfinityView's own source code is MIT licensed. The components below retain their original copyright notices and licenses; the root license does not relicense them.

InfinityView logo/icon artwork is separately reserved by WuQiong-Infinity under
`LICENSE-BRANDING.md`, outside the MIT code license.

## Little CMS 2.19.1 — MIT

- Repository: https://github.com/mm2/Little-CMS
- Tag `lcms2.19.1`, commit `21c582a594fe5279f90c0b93437c398f93bf62b0`.
- Unmodified upstream C source and headers: `native/lcms2/src` and `include`.
- Full license: `native/lcms2/LICENSE`; provenance: `native/lcms2/source.json`.
- `src/v0.4/LightView.App/runtimes/win-x64/native/lcms2.dll` is built from that source with MinGW-w64 GCC. `native/lcms2/build.ps1` is the project's build integration.

## libwebp 1.6.0 — BSD-3-Clause and PATENTS grant

- Repository: https://chromium.googlesource.com/webm/libwebp
- Commit: `4fa21912338357f89e4fd51cf2368325b59e9bd9`.
- Unmodified upstream decoder, demux and dependent source/headers/Makefile.am: `native/libwebp/src`; provenance: `native/libwebp/source.json`.
- Full terms: `native/libwebp/COPYING`, `PATENTS` and `AUTHORS`.
- `ivwebp.dll` is a locally built decoder/demux library. Animated WebP displays its composited first frame, with no animation playback.
- Project integration: `native/libwebp/build.ps1`, selecting decoder-only sources.

## Microsoft .NET packages and pipe ACL adaptation — MIT

- Locked dependencies: application/core `packages.lock.json` files.
- Full package licenses and provenance: `src/v0.4/Compatibility/licenses/`.
- The small pipe-owner ACL adaptation comes from dotnet/runtime tag `v10.0.13`, commit `6aa72daa24f66c01c3d7b6a32519e9d21396deec`. Provenance: `src/v0.4/Compatibility/pipe-source.json`.
- Original copyright: .NET Foundation and Contributors. MIT text retained as `src/v0.4/Compatibility/licenses/dotnet-runtime-LICENSE.txt`.
- Windows .NET Framework/WPF/WIC are OS components, not redistributed as a self-contained .NET runtime in this source version.

## Inno Setup 6.7.3 and Simplified Chinese translation

- Separate, unmodified build tool/installer engine: https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3
- Full license: `installer/licenses/Inno-Setup-LICENSE.txt`.
- The repository contains the original InfinityView integration script, not the Inno compiler or engine source.
- `installer/languages/ChineseSimplified.isl`: upstream commit `c7af86b4b2fd03371185df2b09a1dca8d472ab70`, original path `Files/Languages/Unofficial/ChineseSimplified.isl`.
- Translation is unmodified and retains its original Zhenghan Yang attribution and upstream license. Generated installers retain Inno notices.

## Test fixtures

`tests/fixtures/metadata` contains artificial metadata fixtures. Tests generate additional images. Some color tests require external ICC profiles.
