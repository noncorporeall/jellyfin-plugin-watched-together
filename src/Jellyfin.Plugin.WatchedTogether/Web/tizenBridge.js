/* Watched Together — bridge for the hosted Samsung (Tizen) app.
 * Inlined at the top of <head> so it runs before jellyfin-web. It does nothing unless this page
 * was opened by the hosted TV app (?wtTizenHost=1 inside a frame). It then provides the
 * window.NativeShell object jellyfin-web expects from a TV app, relaying exit, server
 * selection and media-key changes to the app shell with postMessage. ES5 only: old TVs.
 */
(function () {
    'use strict';

    var KEY = 'wtTizenHost';
    var params = {};
    (location.search || '').replace(/^\?/, '').split('&').forEach(function (pair) {
        var i = pair.indexOf('=');
        if (i > 0) {
            try { params[decodeURIComponent(pair.slice(0, i))] = decodeURIComponent(pair.slice(i + 1)); } catch (e) { /* ignore */ }
        }
    });

    var info = null;
    try {
        if (params.wtTizenHost === '1') {
            info = {
                deviceId: params.wtDeviceId || '',
                appVersion: params.wtAppVersion || '1.0.0',
                width: parseInt(params.wtW, 10) || 0,
                height: parseInt(params.wtH, 10) || 0
            };
            sessionStorage.setItem(KEY, JSON.stringify(info));
        } else {
            info = JSON.parse(sessionStorage.getItem(KEY) || 'null');
        }
    } catch (e) {
        info = null;
    }

    if (!info || window.parent === window) {
        return;
    }

    function send(type, data) {
        try {
            window.parent.postMessage({ source: 'wt-tizen', type: type, data: data === undefined ? null : data }, '*');
        } catch (e) { /* ignore */ }
    }

    var AppInfo = {
        deviceId: info.deviceId,
        deviceName: 'Samsung Smart TV',
        appName: 'Jellyfin for Tizen',
        appVersion: info.appVersion
    };

    var SupportedFeatures = [
        'exit',
        'exitmenu',
        'externallinkdisplay',
        'htmlaudioautoplay',
        'htmlvideoautoplay',
        'physicalvolumecontrol',
        'displaylanguage',
        'screensaver',
        'multiserver',
        'subtitleappearancesettings',
        'subtitleburnsettings'
    ];

    window.NativeShell = {
        AppHost: {
            init: function () { return Promise.resolve(AppInfo); },
            appName: function () { return AppInfo.appName; },
            appVersion: function () { return AppInfo.appVersion; },
            deviceId: function () { return AppInfo.deviceId; },
            deviceName: function () { return AppInfo.deviceName; },
            exit: function () { send('exit'); },
            getDefaultLayout: function () { return 'tv'; },
            getDeviceProfile: function (profileBuilder) {
                return profileBuilder({ enableMkvProgressive: false, enableSsaRender: true });
            },
            getSyncProfile: function (profileBuilder) {
                return profileBuilder({ enableMkvProgressive: false });
            },
            screen: function () {
                return info.width && info.height ? { width: info.width, height: info.height } : null;
            },
            supports: function (command) {
                return !!command && SupportedFeatures.indexOf(String(command).toLowerCase()) !== -1;
            }
        },
        selectServer: function () { send('selectServer'); },
        downloadFile: function () { },
        enableFullscreen: function () { },
        disableFullscreen: function () { },
        getPlugins: function () { return []; },
        openUrl: function (url) { send('openUrl', { url: url }); },
        updateMediaSession: function () { },
        hideMediaSession: function () { }
    };

    // The app shell registers the remote's Play/Pause key only on playback screens, like the stock Tizen app.
    window.addEventListener('viewshow', function () { send('view', location.hash); });
    window.addEventListener('hashchange', function () { send('view', location.hash); });
    send('ready', location.hash);
})();
