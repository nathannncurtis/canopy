# Portable mode

Canopy supports a self-contained portable mode without a separate build.

1. Put `Canopy.exe`, `Canopy.Core.dll`, and the rest of the release together in a writable folder.
2. Create an empty file named `portable.flag` beside `Canopy.exe`.
3. Start Canopy. The Updates page reports whether portable mode is active and shows the exact data directory.

In portable mode, Canopy writes application-owned settings, logs, location history, presets, and consent state beneath the `data` directory beside the executable. Saved scans and exports still go wherever you explicitly choose. Remove `portable.flag` and restart to return to installed mode; Canopy does not migrate or delete either data directory.

Portable mode requires a writable application folder. Avoid protected locations such as `Program Files`, and treat the `data` folder as sensitive because it can contain filenames, paths, history, and diagnostic logs.

Update checks remain manual in both modes. Checking makes one HTTPS manifest request with no scan paths or installation identifier. Downloading is a separate action, verifies the package SHA-256, and saves to a location you choose. Opening the verified installer requires another confirmation; Canopy never silently installs updates.
