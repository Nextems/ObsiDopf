# Component notices and sources

Original ObsiDopf code: Copyright (c) 2026 Maxim_Dembitsky (Nextems), AGPL-3.0-only.
Source and releases: https://github.com/Nextems/ObsiDopf

- SuperDoc 1.47.2: `WordAssets/SUPERDOC-LICENSE.txt`, `WordAssets/THIRD-PARTY-NOTICES.txt`, and the `npm` notice directory. Full corresponding engine sources, local modifications and build instructions accompany the release as `ObsiDopf-1.1.0-SuperDoc-1.47.2-Source.zip`; application sources are in the separate `ObsiDopf-1.1.0-Source.zip`. The engine archive preserves the original repository and npm/source-map versions, including embedded dependency sources.
- npm notices: exact package versions identified by `package-lock.json` and the engine source map. `npm/manifest.json` records package versions, archive integrity and pinned upstream notices. Registry archives are verified before their license texts are collected.
- Rust crates: original published license/notice files and Cargo metadata are in `rust`. Supplemental repository-level license files for crates omitting them from their archive are in `rust-supplement`; source revisions come from the published crate's `.cargo_vcs_info.json`. The small build-time `simd_helpers` macro crate only publishes an MIT declaration in Cargo.toml; that declaration, author metadata, exact source reference and the standard MIT template are preserved without inventing an upstream copyright statement.
- .NET runtime: the license and third-party notices from the runtime NuGet packs used in this build are in `dotnet`.
- ONNX Runtime 1.28.0: `onnxruntime`; source: https://github.com/microsoft/onnxruntime/tree/v1.28.0
- DirectML 1.15.4: the Microsoft license terms in `directml` apply separately to this Windows component. Original distribution: https://www.nuget.org/packages/Microsoft.AI.DirectML/1.15.4
- PDFium build 7961: distribution and bundled dependency notices in `pdfium`. Original distribution: https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium%2F7961
- LibreOffice 26.8.1: original `license.txt`, `LICENSE.html` and `NOTICE` are preserved in `native/libreoffice/runtime`. LibreOffice is redistributed without local source modifications and runs as a separate conversion process. Source downloads: https://www.libreoffice.org/download/download-libreoffice/ ; historical source archives: https://downloadarchive.documentfoundation.org/libreoffice/old/
- NuGet packages, including Open XML SDK, PDFsharp, PdfPig, PDFtoImage and SkiaSharp: original package metadata/licenses/notices in `nuget`, with additional upstream license files at the root of `ThirdPartyNotices`.
- PaddleOCR/PP-OCRv5 models: `PaddleOCR-LICENSE.txt`; upstream: https://github.com/PaddlePaddle/PaddleOCR

`native-notice-sources.json` records the sources and SHA-256 hashes of supplemental native notices. Redistribution of third-party components retains their original licenses; the ObsiDopf license does not replace them.
