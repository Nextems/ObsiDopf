# Windows installer

Build: `powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1 -Version 1.0.1`

Output: `dist/ObsiDopf-1.0.1-Setup-x64.exe`, SHA-256 file and payload manifest. Publish the EXE and its SHA-256 as GitHub Release assets; do not commit the staging directory or installer binaries to Git.

The NSIS 3.13 compiler archive is downloaded from the official SourceForge project and verified against its published SHA-256. Build inputs include .NET 10 SDK, Rust worker build and cached EN/RU OCR models. LibreOffice is prepared with setup-docx.ps1 from a signed official MSI. Native VC++ libraries come from that same verified distribution.

The payload includes a self-contained .NET 10 Windows x64 app, Rust OCR worker, EN/RU models, app-local VC++ and DirectML dependencies, and the full local LibreOffice runtime (excluding its original MSI). Third-party licenses/notices are included. No user documents, author profiles or API credentials are packaged.

Installation is per-user in LocalAppData/Programs/ObsiDopf, without elevation. A protected location such as Program Files requires starting the installer as administrator. The file-association option registers PDF/DOCX under the current user for Open With; it does not override the user's default application. Start menu shortcuts and an optional desktop shortcut are provided. Uninstall removes only exact packaged files; extra user files and author settings remain.

Validation: `powershell -NoProfile -ExecutionPolicy Bypass -File test-installer.ps1`. This uses the installer's internal /TESTMODE option and a new temporary directory to avoid registering a test copy or touching shortcuts. It verifies every installed file hash, runs the installed executable (OCR EN/RU, DOCX, comments, Ctrl+S, text highlights, continuous viewer), then uninstalls and verifies user-file preservation. Results: dist/installer-validation.json. The test leaves a small diagnostics folder in Temp.

The resulting EXE is unsigned. No code-signing key was configured.

The app icon source is src/ObsidianDocs/Assets/ObsidianDocs.svg. Regenerate its PNG and multi-size Windows ICO with: dotnet run --project packaging/IconGenerator -- src/ObsidianDocs/Assets. The ICO is embedded in the app EXE and used by the installer/uninstaller; the PNG is embedded for the app's sidebar and welcome screen. Installer payloads use solid zlib compression for faster rebuilds.


