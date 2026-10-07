# Jellyfin+ for Samsung Tizen TVs (hosted)

A small Samsung TV app that shows **your server's own Jellyfin web client**, instead of a copy
bundled into the app. Server plugins (Home Screen Sections, Watched Together, …) therefore
appear on the TV, and Jellyfin updates reach the TV without reinstalling anything.

- First launch asks for your server address and remembers it. To change it later, press ▲
  while it says "Connecting…", or use Jellyfin's own **Select server** menu.
- Remote media keys, Back and Exit work as in the regular Tizen app (the exit, media-key and
  server-switching parts need the Watched Together plugin on the server, which supplies the
  small bridge the page uses to talk to the app).
- Installs alongside any other Jellyfin build (different app id: `WtJfHost01.JellyfinHosted`).

**Install:** download `Jellyfin-Plus-Tizen.wgt` from the latest release and install it with the
[Samsung Jellyfin Installer](https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer)
("custom .wgt"), with Developer Mode on. The installer signs it with your Samsung certificate.

The app icon is Jellyfin's, from [jellyfin-tizen](https://github.com/jellyfin/jellyfin-tizen).
