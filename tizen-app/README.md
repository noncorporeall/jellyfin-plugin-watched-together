# Jellyfin+ for Samsung Tizen TVs (hosted)

A small Samsung TV app that shows **your server's own Jellyfin web client**, instead of a copy
bundled into the app. Server plugins (Home Screen Sections, Watched Together, …) therefore
appear on the TV, and Jellyfin updates reach the TV without reinstalling anything.

- First launch asks for your server address and remembers it. To change it later, press ▲
  while it says "Connecting…", or use Jellyfin's own **Select server** menu.
- Remote media keys, Back and Exit work as in the regular Tizen app. Exiting from Jellyfin's
  menu, the Play/Pause key on playback screens and **Select server** rely on a small bridge
  that the Watched Together plugin adds to the server's page.
- Installs alongside any other Jellyfin build (different app id: `WtJfHost01.JellyfinHosted`).

**Install:** download `Jellyfin-Plus-Tizen.wgt` from the latest release and install it with the
[Samsung Jellyfin Installer](https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer)
("custom .wgt"), with Developer Mode on. The installer signs it with your Samsung certificate.

The app icon is Jellyfin's, from [jellyfin-tizen](https://github.com/jellyfin/jellyfin-tizen).
