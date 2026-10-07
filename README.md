# Watched Together — a Jellyfin plugin

Adds a **Recently Watched on This Server** row to everyone's Jellyfin home screen, with the profile
pictures of the people who watched each item stacked on its card (and an optional "Alice & Bob" caption).

It plugs into the community **Home Screen Sections** plugin (which draws the row) and uses
**File Transformation** to inject a small script that draws the avatars. No forks, no custom frontend.
Play history comes from Jellyfin's own per-user data, so Playback Reporting is not required.

## Requirements

- Jellyfin 10.11.x or 12.1+
- From the `https://www.iamparadox.dev/jellyfin/plugins/manifest.json` repository:
  **File Transformation**, **Plugin Pages**, **Home Screen Sections**
- Web client or a web-based app (native Android TV / Swiftfin apps don't run the web client)

## Install (short version)

1. Install the three plugins above, restart.
2. Add this repository's manifest to Jellyfin and install **Watched Together**, restart:
   `https://raw.githubusercontent.com/<your-github-username>/jellyfin-plugin-watched-together/main/manifest.json`
3. Dashboard → Plugins → Home Screen Sections: tick **Enabled by Default**, untick **Allow User Override**,
   **Add Section** → *Recently Watched on This Server*, Save.
4. Dashboard → Plugins → Watched Together: adjust look-back, privacy exclusions, avatars. Save.
5. Hard-refresh the browser.

## Releasing

Push a tag like `v1.0.0`. The `Build & Release` workflow compiles a 10.11 build (`1.0.0.10`) and a
12.x build (`1.0.0.12`), attaches both zips to a GitHub release, and updates `manifest.json`.
Requires *Settings → Actions → General → Workflow permissions → Read and write*.

Manual build:

```bash
dotnet build src/Jellyfin.Plugin.WatchedTogether -c Release -p:JellyfinVersion=12.1.0 -o out
python3 scripts/package.py zip --dll out/Jellyfin.Plugin.WatchedTogether.dll \
  --version 1.0.0.12 --target-abi 12.1.0.0 --out dist
```

## How it works

| Piece | File |
| --- | --- |
| Registers the shelf with Home Screen Sections and the script with File Transformation (startup task, retries) | `Services/StartupService.cs`, `Services/IntegrationRegistrar.cs` |
| Builds the server-wide "who watched what" snapshot, filters it per viewer by library access and parental rating | `Services/RecentActivityService.cs` |
| Fills the row when Home Screen Sections asks | `Services/ResultsHandler.cs` |
| `GET /WatchedTogether/Watchers` (viewer taken from the access token), plus the injected JS/CSS | `Controllers/WatchedTogetherController.cs` |
| Adds the `<script>`/`<link>` tags to `index.html` | `Helpers/IndexTransformation.cs` |
| Finds `.verticalSection.WatchedTogether .card[data-id]` and draws avatars | `Web/watchedTogether.js`, `Web/watchedTogether.css` |
| Admin settings page | `Configuration/configPage.html` |
