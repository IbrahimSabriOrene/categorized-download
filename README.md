# CategorizedDownload

Personal Windows desktop utility by Ibrahim Sabri Örene. A WPF/DevExpress application and Chrome extension hand downloads to a loopback bridge, then move completed files into category folders or the Downloads directory.

## Implemented scope

- Download queue and recent-completion history.
- Built-in categories and custom file-extension folder templates.
- Temporary download handling before the final move.
- Chrome extension handoff to the desktop application.

```powershell
dotnet run -c Release
```

Requires Windows, .NET 8 and access to the referenced DevExpress packages under your own license. In Chrome's extensions page, enable Developer mode and load the `chrome-extension` directory unpacked. Run the desktop app before handing off downloads.

The current handoff uses a public download URL. Requests depending on browser-only cookies, POST bodies or temporary headers may fail; this is not a general authenticated-download bridge.

Fresh source-only import: no personal download history, downloaded files, browser profiles, credentials or third-party binaries are included. Original local files are unchanged. No new license grant has been selected for this source release.
