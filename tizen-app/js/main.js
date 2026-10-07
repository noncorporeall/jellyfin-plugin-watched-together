/*
 * Jellyfin+ for Samsung Tizen TVs — app shell.
 * Shows the server's own Jellyfin web client full-screen (so server plugins work on the TV),
 * remembers the server, and provides what a TV app needs: remote media keys, exit, a
 * "change server" screen and a friendly error when the server can't be reached.
 * ES5 only: this runs on TV browser engines from 2016 onwards.
 */
(function () {
    'use strict';

    var STORE_SERVER = 'jfplus.server';
    var STORE_DEVICE = 'jfplus.deviceId';
    var PING_TIMEOUT_MS = 8000;
    var FRAME_TIMEOUT_MS = 25000;
    var SPLASH_MIN_MS = 1500;

    var KEY = {
        LEFT: 37, UP: 38, RIGHT: 39, DOWN: 40, ENTER: 13,
        BACK: 10009, ESC: 27, IME_DONE: 65376, IME_CANCEL: 65385
    };

    var screens = ['connecting', 'setup', 'failed'];
    var frame = document.getElementById('frame');
    var input = document.getElementById('serverInput');
    var state = {
        screen: null,
        server: null,
        frameLoaded: false,
        cancelConnect: false,
        frameTimer: null,
        screenInfo: { width: 0, height: 0 }
    };

    // ---------- Tizen helpers (all optional, so the shell also runs in a desktop browser) ----------

    function tizenApp() {
        try { return window.tizen && tizen.application.getCurrentApplication(); } catch (e) { return null; }
    }

    function appVersion() {
        var app = tizenApp();
        try { return app ? app.appInfo.version : '1.0.0'; } catch (e) { return '1.0.0'; }
    }

    function exitApp() {
        var app = tizenApp();
        if (app) {
            try { app.exit(); return; } catch (e) { /* fall through */ }
        }
        window.close();
    }

    function registerKey(name, on) {
        try {
            if (on) { tizen.tvinputdevice.registerKey(name); } else { tizen.tvinputdevice.unregisterKey(name); }
        } catch (e) { /* not on a TV, or key not supported */ }
    }

    function registerMediaKeys() {
        ['MediaPlay', 'MediaPause', 'MediaStop', 'MediaTrackPrevious', 'MediaTrackNext', 'MediaRewind', 'MediaFastForward']
            .forEach(function (k) { registerKey(k, true); });
    }

    function readScreenInfo(done) {
        try {
            tizen.systeminfo.getPropertyValue('DISPLAY', function (result) {
                var ratio = 1;
                try {
                    if (webapis.productinfo.is8KPanelSupported && webapis.productinfo.is8KPanelSupported()) { ratio = 4; }
                    else if (webapis.productinfo.isUdPanelSupported && webapis.productinfo.isUdPanelSupported()) { ratio = 2; }
                } catch (e) { /* ignore */ }
                state.screenInfo = {
                    width: Math.floor(result.resolutionWidth * ratio),
                    height: Math.floor(result.resolutionHeight * ratio)
                };
                done();
            }, function () { done(); });
        } catch (e) {
            done();
        }
    }

    function deviceId() {
        var id = null;
        try { id = localStorage.getItem(STORE_DEVICE); } catch (e) { /* ignore */ }
        if (!id) {
            // Same shape as jellyfin-web's own device ids.
            id = btoa([navigator.userAgent, new Date().getTime(), Math.random()].join('|')).replace(/=/g, '1');
            try { localStorage.setItem(STORE_DEVICE, id); } catch (e) { /* ignore */ }
        }
        return id;
    }

    // ---------- screens & focus (remote-friendly) ----------

    function show(name) {
        state.screen = name;
        screens.forEach(function (s) {
            document.getElementById(s).hidden = s !== name;
        });
        frame.hidden = name !== 'frame';

        if (name === 'setup') {
            focusEl(input.value ? document.getElementById('connectButton') : input);
        } else if (name === 'failed') {
            focusEl(document.getElementById('retryButton'));
        } else if (name === 'frame') {
            focusFrame();
        }
    }

    function focusEl(el) {
        setTimeout(function () { try { el.focus(); } catch (e) { /* ignore */ } }, 0);
    }

    function focusFrame() {
        setTimeout(function () {
            try { frame.focus(); } catch (e) { /* ignore */ }
            try { frame.contentWindow.focus(); } catch (e) { /* ignore */ }
        }, 0);
    }

    function focusables() {
        var current = document.getElementById(state.screen);
        return current ? Array.prototype.slice.call(current.querySelectorAll('.focusable')) : [];
    }

    function moveFocus(step) {
        var list = focusables();
        if (!list.length) { return; }
        var i = list.indexOf(document.activeElement);
        var next = i < 0 ? 0 : Math.max(0, Math.min(list.length - 1, i + step));
        list[next].focus();
    }

    // ---------- server address ----------

    function normalise(raw) {
        var url = String(raw || '').trim();
        if (!url) { return null; }
        if (!/^https?:\/\//i.test(url)) { url = 'http://' + url; }
        url = url.replace(/\/+$/, '').replace(/\/web(\/index\.html)?$/i, '');
        // Bare host or IP without a port over plain http: assume Jellyfin's default port.
        if (/^http:\/\/[^\/:]+$/i.test(url)) { url += ':8096'; }
        return url;
    }

    function ping(server, done) {
        var xhr = new XMLHttpRequest();
        var finished = false;
        function finish(ok, message) {
            if (finished) { return; }
            finished = true;
            done(ok, message);
        }
        try {
            xhr.open('GET', server + '/System/Info/Public', true);
            xhr.timeout = PING_TIMEOUT_MS;
            xhr.onload = function () {
                if (xhr.status >= 200 && xhr.status < 300) {
                    try {
                        var info = JSON.parse(xhr.responseText);
                        if (info && info.Id) { finish(true, info.ServerName || 'Jellyfin'); return; }
                    } catch (e) { /* fall through */ }
                    finish(false, 'That address answered, but it isn\'t a Jellyfin server.');
                } else {
                    finish(false, 'The server answered with an error (' + xhr.status + ').');
                }
            };
            xhr.onerror = function () { finish(false, 'No answer from ' + server + '. Is the server on, and is the address right?'); };
            xhr.ontimeout = function () { finish(false, 'No answer from ' + server + ' after ' + (PING_TIMEOUT_MS / 1000) + ' seconds.'); };
            xhr.send();
        } catch (e) {
            finish(false, 'That address doesn\'t look right.');
        }
    }

    function saveServer(server) {
        try { localStorage.setItem(STORE_SERVER, server); } catch (e) { /* ignore */ }
    }

    function savedServer() {
        try { return localStorage.getItem(STORE_SERVER); } catch (e) { return null; }
    }

    // ---------- connect & hand off to the server's web client ----------

    function connect(server, fromSetup) {
        state.server = server;
        state.cancelConnect = false;
        document.getElementById('connectingText').textContent = 'Connecting to ' + server.replace(/^https?:\/\//, '') + '…';
        show('connecting');

        var started = new Date().getTime();
        ping(server, function (ok, message) {
            var wait = Math.max(0, SPLASH_MIN_MS - (new Date().getTime() - started));
            setTimeout(function () {
                if (state.cancelConnect) { return; }
                if (ok) {
                    saveServer(server);
                    loadFrame(server);
                } else if (fromSetup) {
                    document.getElementById('setupError').textContent = message;
                    show('setup');
                } else {
                    document.getElementById('failedText').textContent = message;
                    show('failed');
                }
            }, fromSetup ? 0 : wait);
        });
    }

    function loadFrame(server) {
        var info = state.screenInfo;
        var url = server + '/web/index.html' +
            '?wtTizenHost=1' +
            '&wtDeviceId=' + encodeURIComponent(deviceId()) +
            '&wtAppVersion=' + encodeURIComponent(appVersion()) +
            '&wtW=' + (info.width || 0) +
            '&wtH=' + (info.height || 0);

        state.frameLoaded = false;
        clearTimeout(state.frameTimer);
        state.frameTimer = setTimeout(function () {
            if (!state.frameLoaded) {
                document.getElementById('failedText').textContent = 'Jellyfin took too long to load from ' + server + '.';
                show('failed');
            }
        }, FRAME_TIMEOUT_MS);

        frame.onload = function () {
            state.frameLoaded = true;
            clearTimeout(state.frameTimer);
            if (state.screen === 'connecting') { show('frame'); }
            focusFrame();
        };
        frame.src = url;
    }

    function unloadFrame() {
        clearTimeout(state.frameTimer);
        state.frameLoaded = false;
        frame.onload = null;
        frame.src = 'about:blank';
    }

    function openSetup(message) {
        state.cancelConnect = true;
        unloadFrame();
        input.value = state.server || savedServer() || '';
        document.getElementById('setupError').textContent = message || '';
        show('setup');
    }

    // ---------- messages from the server page (via the Watched Together plugin's bridge) ----------

    window.addEventListener('message', function (e) {
        var msg = e.data;
        if (!msg || msg.source !== 'wt-tizen' || e.source !== frame.contentWindow) { return; }

        switch (msg.type) {
            case 'ready':
                state.frameLoaded = true;
                clearTimeout(state.frameTimer);
                if (state.screen === 'connecting') { show('frame'); }
                break;
            case 'exit':
                exitApp();
                break;
            case 'selectServer':
                openSetup();
                break;
            case 'view':
                // Like the stock app: the remote's Play/Pause key belongs to the player only on playback screens.
                var hash = String(msg.data || '');
                registerKey('MediaPlayPause', hash.indexOf('/video') !== -1 || hash.indexOf('/queue') !== -1);
                break;
        }
    });

    // ---------- keys for the shell's own screens ----------

    document.addEventListener('keydown', function (e) {
        var code = e.keyCode;

        if (state.screen === 'frame') {
            // Keys only land here if focus slipped out of the server page: hand it back.
            focusFrame();
            return;
        }

        if (state.screen === 'connecting') {
            if (code === KEY.UP) {
                e.preventDefault();
                openSetup();
            } else if (code === KEY.BACK || code === KEY.ESC) {
                e.preventDefault();
                exitApp();
            }
            return;
        }

        if (document.activeElement === input) {
            // OK opens the TV's on-screen keyboard (default behaviour); its "Done" key moves on to Connect.
            if (code === KEY.IME_DONE) {
                e.preventDefault();
                input.blur();
                focusEl(document.getElementById('connectButton'));
                return;
            }
            if (code === KEY.IME_CANCEL) {
                e.preventDefault();
                input.blur();
                focusEl(input);
                return;
            }
            if (code === KEY.DOWN) {
                e.preventDefault();
                input.blur();
                moveFocus(1);
                focusEl(document.getElementById('connectButton'));
                return;
            }
            if (code === KEY.BACK || code === KEY.ESC) {
                e.preventDefault();
                input.blur();
                return;
            }
            return; // typing
        }

        switch (code) {
            case KEY.UP:
            case KEY.LEFT:
                e.preventDefault();
                moveFocus(-1);
                break;
            case KEY.DOWN:
            case KEY.RIGHT:
                e.preventDefault();
                moveFocus(1);
                break;
            case KEY.BACK:
            case KEY.ESC:
                e.preventDefault();
                if (state.screen === 'setup' && savedServer()) {
                    connect(savedServer(), false);
                } else {
                    exitApp();
                }
                break;
        }
    });

    document.getElementById('connectButton').addEventListener('click', function () {
        var server = normalise(input.value);
        if (!server) {
            document.getElementById('setupError').textContent = 'Enter your server\'s address first.';
            focusEl(input);
            return;
        }
        input.value = server;
        connect(server, true);
    });

    document.getElementById('retryButton').addEventListener('click', function () {
        connect(state.server || savedServer(), false);
    });

    document.getElementById('changeButton').addEventListener('click', function () {
        openSetup();
    });

    // ---------- start ----------

    registerMediaKeys();
    readScreenInfo(function () {
        var server = savedServer();
        if (server) {
            connect(server, false);
        } else {
            openSetup();
        }
    });
})();
