# Windows installer

Build: `powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1 -Version 1.1.0`

Output: `dist/ObsiDopf-1.1.0-Setup-x64.exe`, SHA-256 file and payload manifest. Each build creates a fresh staging directory so stale output cannot enter a new installer. Do not commit staging or installer binaries to Git.

Prepare source and release assets after the installer finishes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging/package-release-source.ps1 -Version 1.1.0
```

This archives the actual working tree, copies release notes, and creates SHA-256 sums and release metadata. Publish from a clean commit so BaseCommit identifies the source used for both the installer and archives. It does not commit, tag, push, publish, run the application, install anything, or call Gemini. Original ObsiDopf code is licensed under AGPL-3.0-only (root LICENSE and COPYRIGHT.md); third-party licenses are preserved. Both root notices are included in application builds. Publish both source archives alongside the matching installer; see RELEASE-NOTES.md and web/word/README.md.

`collect-npm-notices.mjs` collects exact-version npm license texts from both the lock file and SuperDoc source map, verifying npm archive integrity. `collect-native-notices.mjs` supplements the existing native notices with pinned crate repository licenses, .NET runtime pack notices, ONNX Runtime, DirectML and PDFium distribution notices. Their generated files are committed under `packaging/notices` and automatically included in the installer. Regenerate them when changing dependencies, then commit the results before building release artifacts. Collection scripts download/read build inputs only and never execute the application or tests.

The source packager also prepares `ObsiDopf-1.1.0-SuperDoc-1.47.2-Source.zip` using
`superdoc/prepare-source.ps1` when the engine archive is missing or stale. This includes
the complete pinned upstream snapshot, original npm tarball, all published source-map
contents, preferred-source modifications and build instructions. See superdoc/SOURCE-INFO.md.
Publish both source ZIPs alongside the corresponding installer. `-SourcesOnly` creates
source archives/metadata without including an older installer in the new checksum set;
it does not assert that the existing installer matches the current source snapshot.

For a build using unchanged native components from an earlier installed release, pass `-ComponentsRoot 'C:\absolute\installation\folder'` to build-installer.ps1. Only the OCR worker, DirectML and LibreOffice runtime are reused; previous app DLLs and user data are not copied. The current C#/Word assets are always published from source. Use this option only when the native worker sources are unchanged from that installed build.

The NSIS 3.13 compiler archive is downloaded from the official SourceForge project and verified against its published SHA-256. Build inputs include .NET 10 SDK, Rust worker build and cached EN/RU OCR models. LibreOffice is prepared with setup-docx.ps1 from a signed official MSI. Native VC++ libraries come from that same verified distribution.

The payload includes a self-contained .NET 10 Windows x64 app, local SuperDoc 1.47.2 assets, Rust OCR worker, EN/RU models, app-local VC++ and DirectML dependencies, and the full local LibreOffice runtime (excluding its original MSI). Third-party licenses/notices are included. No user documents, author profiles or API credentials are packaged. Microsoft Edge WebView2 Runtime is required for DOC/DOCX and is not bundled. Gemini requires an internet connection and the user's own API key.

Installation is per-user in LocalAppData/Programs/ObsiDopf, without elevation. A protected location such as Program Files requires starting the installer as administrator. The file-association option registers PDF/DOCX under the current user for Open With; it does not override the user's default application. Start menu shortcuts and an optional desktop shortcut are provided. Uninstall removes only exact packaged files; extra user files and author settings remain.

Validation: `powershell -NoProfile -ExecutionPolicy Bypass -File test-installer.ps1`. This uses the installer's internal /TESTMODE option and a new temporary directory to avoid registering a test copy or touching shortcuts. It verifies every installed file hash, runs the installed executable (OCR EN/RU, DOCX, comments, Ctrl+S, text highlights, continuous viewer), then uninstalls and verifies user-file preservation. Results: dist/installer-validation.json. The test leaves a small diagnostics folder in Temp.

The runner also compiles `validation/Validation.csproj` against the installed application DLLs and checks the actual WebView2/SuperDoc bridge, editable DOCX, search, text/marker colors, export round-trips, undo, export-failure handling/retry, legacy minimal-DOCX normalization, eyedropper cancellation and material picker. A test-only accessor is injected before editor initialization; installed application assets are not modified. No live Gemini API call is made; exact screen-pixel sampling is not asserted. The packager includes a sanitized versioned validation report only when its installer SHA-256 and source commit match the current release. Failed or stale reports cannot mark a release validated.

The resulting EXE is unsigned. No code-signing key was configured.

The app icon source is src/ObsidianDocs/Assets/ObsidianDocs.svg. Regenerate its PNG and multi-size Windows ICO with: dotnet run --project packaging/IconGenerator -- src/ObsidianDocs/Assets. The ICO is embedded in the app EXE and used by the installer/uninstaller; the PNG is embedded for the app's sidebar and welcome screen. Installer payloads use solid LZMA compression with a 32 MiB dictionary to reduce download size.


