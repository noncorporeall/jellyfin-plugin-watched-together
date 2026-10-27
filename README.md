# Watched Together

A Jellyfin plugin that adds social shelves to the home screen:

- **Recently Watched on This Server** shows what other people on the server have watched lately.
  The watchers' profile pictures are stacked on each card, with an "Alice & Bob" caption.
- **Live first.** Anyone watching right now comes first, with a green ring and a progress bar
  that moves along the artwork.
- **Watch together.** Opening a card someone is watching live offers *Ask to watch together*.
  That sends a polite invite which starts a SyncPlay session only if they accept, or joins
  their watch party if they already have one. It works with mouse, touch, keyboard, TV remotes
  and gamepads.
- **Most Popular Movies / Shows** rank items by total watch time over a window you choose,
  with gold, silver and bronze medals.

It works through the community plugin ecosystem — no forked or custom web client.
[Home Screen Sections](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) draws
the rows, and [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
adds a small script to the web client for the pictures, live progress, medals and invites.

## Requirements

- Jellyfin **10.11.x** or **12.1+**
- From the `https://www.iamparadox.dev/jellyfin/plugins/manifest.json` repository:
  **File Transformation**, **Plugin Pages**, **Home Screen Sections**
- **Playback Reporting** (Jellyfin's default catalog), only for the Most Popular shelves

| Client | Shelves | Pictures, live progress, medals, invites |
| --- | --- | --- |
| Web browser, Jellyfin Media Player, Android/iOS app, webOS app | Yes | Yes |
| Samsung (Tizen) TV | With the hosted [Jellyfin+ app](tizen-app/) | With Jellyfin+ |
| Streamyfin | Through its custom home sections ([below](#streamyfin)) | No |
| Native apps (Android TV, Swiftfin, Roku, Kodi) | No | No |

## Install

1. Install File Transformation, Plugin Pages and Home Screen Sections, then restart Jellyfin.
2. Add this plugin repository in **Dashboard → Plugins → Repositories**:

   ```
   https://raw.githubusercontent.com/noncorporeall/jellyfin-plugin-watched-together/main/manifest.json
   ```

   Install **Watched Together** from the catalog and restart.
3. **Dashboard → Plugins → Home Screen Sections**: tick **Enabled by Default** and save. In
   **Section Settings**, tick **Enabled** on the Watched Together rows and untick **User Override**.
   Existing users only get new rows when User Override is unticked.
4. **Dashboard → Plugins → Watched Together**: adjust the settings if you like, then save.
5. Hard-refresh the browser (Ctrl/Cmd+Shift+R).

Zips for manual installs are on the [Releases](https://github.com/noncorporeall/jellyfin-plugin-watched-together/releases)
page: `…10.zip` for Jellyfin 10.11, `…12.zip` for 12.x.

## How the recently watched shelf is chosen

Each person adds cards for their latest few movies and latest few shows (2 each by default).
Every card then shows everyone who watched that item within the look-back period (14 days by
default), not just whoever added it. People only ever see items they have access to,
parental ratings included, and by default not their own activity.

Key settings: **Look back (days)**, **Maximum cards on the shelf**, **Movies / Shows each
person adds**, plus privacy lists of users whose viewing is never shared and libraries that
never appear.

## Streamyfin

Streamyfin draws its own home screen, so it can't show the pictures, live progress, medals
or invites. It can show the shelves' items through the
[Streamyfin plugin](https://github.com/streamyfin/jellyfin-plugin-streamyfin)'s custom home sections.
These endpoints return the same items in the same order as the web shelves:

```yaml
settings:
  home:
    locked: false
    value:
      sections:
        - title: Recently Watched on This Server
          orientation: horizontal
          custom:
            endpoint: /WatchedTogether/Shelves/Recent
        - title: Most Popular Movies
          orientation: vertical
          custom:
            endpoint: /WatchedTogether/Shelves/PopularMovies
        - title: Most Popular Shows
          orientation: vertical
          custom:
            endpoint: /WatchedTogether/Shelves/PopularShows
```

A custom section list replaces Streamyfin's default home screen, so list the stock rows
(Continue Watching, Next Up, …) as well.

## Samsung TVs

The regular Tizen app bundles its own copy of the web client, so server plugins never reach it.
[`tizen-app/`](tizen-app/) is a small hosted app, **Jellyfin+**, that shows the server's own
web client instead. Each release includes `WatchedTogether-TV.wgt`; see its
[README](tizen-app/README.md) for installation.

## Building and releasing

Pushing a tag like `v1.2.0` runs the **Build & Release** workflow. It builds a Jellyfin 10.11
version (`1.2.0.10`) and a 12.x version (`1.2.0.12`), packages the TV app, attaches everything
to a GitHub release, and updates `manifest.json`. On a fork, set
*Settings → Actions → General → Workflow permissions* to *Read and write*.

To build by hand (.NET 9 SDK for 10.11, .NET 10 SDK for 12.x):

```bash
dotnet build src/Jellyfin.Plugin.WatchedTogether -c Release -p:JellyfinVersion=12.1.0 -o out
python3 scripts/package.py zip --dll out/Jellyfin.Plugin.WatchedTogether.dll \
  --version 1.0.0.12 --target-abi 12.1.0.0 --out dist
```

For 10.11 use `JellyfinVersion=10.11.11`, `--version 1.0.0.10` and `--target-abi 10.11.0.0`.

## How it works

| Piece | Files |
| --- | --- |
| Registers the shelves with Home Screen Sections and the script with File Transformation (startup task, with retries) | `Services/StartupService.cs`, `Services/IntegrationRegistrar.cs` |
| Builds the server-wide "who watched what" snapshot, overlays live sessions and SyncPlay groups, then filters it per viewer | `Services/RecentActivityService.cs` |
| Ranks movies and shows by watch time from Playback Reporting's database | `Services/PopularityService.cs` |
| Fills the rows when Home Screen Sections asks | `Services/ResultsHandler.cs` |
| Watch-together invites and the SyncPlay hand-off | `Services/WatchTogetherService.cs`, `Controllers/WatchTogetherController.cs` |
| `GET /WatchedTogether/Watchers` (the viewer always comes from the access token) and the injected JS/CSS | `Controllers/WatchedTogetherController.cs` |
| Plain item lists for other apps (`/WatchedTogether/Shelves/…`) | `Controllers/ShelvesController.cs` |
| Adds the script, stylesheet and Tizen bridge to `index.html` | `Helpers/IndexTransformation.cs`, `Web/tizenBridge.js` |
| Draws pictures, live progress, medals and dialogs on the cards | `Web/watchedTogether.js`, `Web/watchedTogether.css` |
| Admin settings page | `Configuration/configPage.html` |
