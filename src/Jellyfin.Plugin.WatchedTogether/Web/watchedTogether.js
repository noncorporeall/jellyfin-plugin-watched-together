/*
 * Watched Together — client add-on.
 * Injected into jellyfin-web by File Transformation. It:
 *  - decorates the "recently watched" shelf with watchers' profile pictures, LIVE badges and progress,
 *  - refreshes that shelf in place when someone starts watching something new,
 *  - when someone clicks a card another person is watching live, offers "watch together"
 *    (a SyncPlay invite) or "watch on my own", and shows incoming invites,
 *  - puts gold / silver / bronze / rank badges on the Most Popular shelves.
 */
(function () {
    'use strict';

    if (window.__watchedTogetherLoaded) {
        return;
    }
    window.__watchedTogetherLoaded = true;

    var SHELF_ID = 'WatchedTogether';
    var POPULAR_IDS = ['WatchedTogetherPopularMovies', 'WatchedTogetherPopularShows'];
    var CACHE_MS = 30 * 1000;
    var STAMP = 'data-wt-decorated';
    var MEDAL_STAMP = 'data-wt-medal';

    var state = {
        sectionId: SHELF_ID,
        data: null,
        fetchedAt: 0,
        userId: null,
        pending: null,
        scheduled: false,
        liveRefreshMs: 0,
        lastShelfRefreshSig: '',
        popular: null,
        popularAt: 0,
        popularPending: null,
        incomingShown: {},
        incomingDelayMs: 5000,
        lookup: {},
        settings: null,
        dialogOpen: false
    };

    // ---------- helpers ----------

    function pick(obj, name) {
        // Jellyfin serialises PascalCase; tolerate camelCase too.
        if (!obj) { return undefined; }
        if (obj[name] !== undefined) { return obj[name]; }
        var camel = name.charAt(0).toLowerCase() + name.slice(1);
        return obj[camel];
    }

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function normaliseId(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function initials(name) {
        var parts = String(name || '?').trim().split(/\s+/);
        var text = parts[0].charAt(0) + (parts.length > 1 ? parts[parts.length - 1].charAt(0) : '');
        return text.toUpperCase();
    }

    function colourFor(name) {
        var hash = 0;
        var s = String(name || '');
        for (var i = 0; i < s.length; i++) {
            hash = ((hash << 5) - hash + s.charCodeAt(i)) | 0;
        }
        return 'hsl(' + (Math.abs(hash) % 360) + ', 55%, 42%)';
    }

    function timeAgo(iso) {
        var then = new Date(iso).getTime();
        if (isNaN(then)) { return ''; }
        var mins = Math.max(0, Math.round((Date.now() - then) / 60000));
        if (mins < 1) { return 'just now'; }
        if (mins < 60) { return mins + 'm ago'; }
        var hours = Math.round(mins / 60);
        if (hours < 24) { return hours + 'h ago'; }
        return Math.round(hours / 24) + 'd ago';
    }

    function joinNames(names) {
        if (names.length === 0) { return ''; }
        if (names.length === 1) { return names[0]; }
        if (names.length === 2) { return names[0] + ' & ' + names[1]; }
        return names[0] + ', ' + names[1] + ' +' + (names.length - 2);
    }

    function percent(p) {
        return (typeof p === 'number' && isFinite(p)) ? Math.round(p * 100) + '%' : null;
    }

    function isAttached(el) {
        // Node.isConnected is missing on older TV browser engines.
        return !!el && (el.isConnected === undefined ? document.documentElement.contains(el) : el.isConnected);
    }

    /**
     * Badge size in plain pixels, measured from the card: "Profile picture size" is a % of the
     * card's width. Pixels work on every engine (Samsung TVs run Chrome 56–76 engines).
     */
    function sizeHost(host, pct) {
        host.classList.add('wt-host');
        host.setAttribute('data-wt-pct', String(pct));
        var width = host.clientWidth || host.offsetWidth || 0;
        if (width > 0) {
            var size = Math.max(18, Math.round(width * pct / 100));
            host.style.setProperty('--wt-size', size + 'px');
        } else {
            // Not laid out yet (e.g. off-screen): measure again shortly.
            setTimeout(function () {
                if (isAttached(host) && (host.clientWidth || host.offsetWidth)) { sizeHost(host, pct); }
            }, 500);
        }
    }

    var resizeTimer = null;
    window.addEventListener('resize', function () {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(function () {
            Array.prototype.forEach.call(document.querySelectorAll('.wt-host[data-wt-pct]'), function (host) {
                sizeHost(host, parseFloat(host.getAttribute('data-wt-pct')) || 16);
            });
        }, 200);
    });

    function apiClient() {
        return window.ApiClient || null;
    }

    function currentUserId() {
        var client = apiClient();
        try {
            return client && client.getCurrentUserId ? client.getCurrentUserId() : null;
        } catch (e) {
            return null;
        }
    }

    function userImageUrl(userId, tag) {
        var client = apiClient();
        if (!client || !tag) { return null; }
        return client.getUrl('UserImage', { userId: userId, tag: tag, maxWidth: 192, quality: 90 });
    }

    function postJson(path, body) {
        var client = apiClient();
        return client.ajax({
            type: 'POST',
            url: client.getUrl(path),
            data: JSON.stringify(body || {}),
            contentType: 'application/json; charset=utf-8',
            dataType: 'json'
        });
    }

    function postNoContent(path, body) {
        var client = apiClient();
        return client.ajax({
            type: 'POST',
            url: client.getUrl(path),
            data: JSON.stringify(body || {}),
            contentType: 'application/json; charset=utf-8'
        });
    }

    function sectionSelector(id) {
        return '.verticalSection.' + (window.CSS && CSS.escape ? CSS.escape(id) : id);
    }

    function avatarHtml(userId, name, tag, extraClass, style) {
        var url = userImageUrl(userId, tag);
        var html = '<span class="wt-avatar' + (extraClass || '') + '" style="' + (style || '') + 'background-color:' + colourFor(name) + ';" data-initials="' + escapeHtml(initials(name)) + '">';
        html += url
            ? '<img src="' + escapeHtml(url) + '" alt="' + escapeHtml(name) + '" loading="lazy" />'
            : '<span class="wt-initials">' + escapeHtml(initials(name)) + '</span>';
        return html + '</span>';
    }

    function wireImageFallbacks(root) {
        Array.prototype.forEach.call(root.querySelectorAll('.wt-avatar img'), function (img) {
            img.addEventListener('error', function () {
                var parent = img.parentNode;
                if (parent) {
                    parent.innerHTML = '<span class="wt-initials">' + escapeHtml(parent.getAttribute('data-initials') || '?') + '</span>';
                }
            }, { once: true });
        });
    }

    // ---------- toasts & dialogs ----------

    var toastTimer = null;

    function toast(html, opts) {
        opts = opts || {};
        var el = document.querySelector('.wt-toast');
        if (!el) {
            el = document.createElement('div');
            el.className = 'wt-toast';
            el.setAttribute('role', 'status');
            document.body.appendChild(el);
        }
        el.innerHTML = '<div class="wt-toast-body">' + html + '</div>' +
            (opts.action ? '<button type="button" class="wt-toast-action">' + escapeHtml(opts.action) + '</button>' : '');
        el.classList.add('wt-show');
        var btn = el.querySelector('.wt-toast-action');
        if (btn && opts.onAction) {
            btn.addEventListener('click', function () { opts.onAction(); hideToast(); });
        }
        clearTimeout(toastTimer);
        if (!opts.sticky) {
            toastTimer = setTimeout(hideToast, opts.duration || 5000);
        }
    }

    function hideToast() {
        var el = document.querySelector('.wt-toast');
        if (el) { el.classList.remove('wt-show'); }
    }

    // ---------- shelf data ----------

    function fetchWatchers(force) {
        var client = apiClient();
        var uid = currentUserId();
        if (!client || !uid) {
            return Promise.resolve(null);
        }

        var fresh = state.data && state.userId === uid && (Date.now() - state.fetchedAt) < CACHE_MS;
        if (fresh && !force) {
            return Promise.resolve(state.data);
        }

        if (state.pending) {
            return state.pending;
        }

        state.pending = client.getJSON(client.getUrl('WatchedTogether/Watchers'))
            .then(function (response) {
                state.data = response;
                state.userId = uid;
                state.fetchedAt = Date.now();
                state.sectionId = pick(response, 'SectionId') || SHELF_ID;
                state.liveRefreshMs = (pick(response, 'LiveRefreshSeconds') || 0) * 1000;
                return response;
            }, function (err) {
                console.warn('[WatchedTogether] Could not load watchers', err);
                return null;
            })
            .then(function (result) {
                state.pending = null;
                return result;
            });

        return state.pending;
    }

    function lookupFrom(response) {
        var items = pick(response, 'Items') || {};
        var lookup = {};
        Object.keys(items).forEach(function (key) {
            lookup[normaliseId(key)] = items[key];
        });
        return lookup;
    }

    function settingsFrom(response) {
        return {
            showAvatars: pick(response, 'ShowAvatars') !== false,
            showCaption: pick(response, 'ShowNamesCaption') !== false,
            maxAvatars: pick(response, 'MaxAvatarsPerCard') || 3,
            sizePct: pick(response, 'AvatarSizePercent') || 16,
            watchTogether: pick(response, 'WatchTogether') === true,
            viewerId: normaliseId(pick(response, 'ViewerId')),
            refreshSec: (pick(response, 'LiveRefreshSeconds') || 15)
        };
    }

    // ---------- shelf rendering ----------

    function tooltipFor(watchers) {
        return watchers.map(function (w) {
            var line = pick(w, 'Name');
            var detail = pick(w, 'Detail');
            if (detail) { line += ' — ' + detail; }
            if (pick(w, 'IsLive')) {
                var pct = percent(pick(w, 'Progress'));
                line += pick(w, 'IsPaused') ? ' (paused' : ' (watching now';
                if (pick(w, 'SyncPlayGroupId')) { line += ' together'; }
                return line + (pct ? ', ' + pct + ')' : ')');
            }
            line += pick(w, 'Finished') ? ' (finished' : ' (watched';
            var ago = timeAgo(pick(w, 'LastPlayed'));
            return line + (ago ? ', ' + ago + ')' : ')');
        }).join('\n');
    }

    function captionFor(watchers) {
        var live = watchers.filter(function (w) { return pick(w, 'IsLive'); });
        if (live.length === 0) {
            return { live: false, text: joinNames(watchers.map(function (w) { return pick(w, 'Name'); })) };
        }
        var lead = live[0];
        var pct = percent(pick(lead, 'Progress'));
        if (live.length === 1) {
            var verb = pick(lead, 'IsPaused') ? ' paused' : ' is watching';
            return { live: true, text: pick(lead, 'Name') + verb + (pct ? ' · ' + pct : '') };
        }
        var party = partyOf(live);
        var names = joinNames(live.map(function (x) { return pick(x, 'Name'); }));
        return { live: true, text: names + (party ? ' are watching together' : ' are watching') + (party && pct ? ' · ' + pct : '') };
    }

    /** The SyncPlay group shared by all of these live watchers, if there is one. */
    function partyOf(live) {
        var first = live.length > 1 ? pick(live[0], 'SyncPlayGroupId') : null;
        if (!first) { return null; }
        return live.every(function (w) { return pick(w, 'SyncPlayGroupId') === first; }) ? first : null;
    }

    function buildAvatarsHtml(watchers, max) {
        var html = '';
        var openParty = null;
        watchers.slice(0, max).forEach(function (w, index) {
            var group = pick(w, 'IsLive') ? pick(w, 'SyncPlayGroupId') : null;
            var groupSize = group ? watchers.filter(function (x) { return pick(x, 'SyncPlayGroupId') === group; }).length : 0;
            if (openParty && openParty !== group) { html += '</span>'; openParty = null; }
            if (group && groupSize > 1 && openParty !== group) {
                html += '<span class="wt-party" title="Watching together (SyncPlay)">';
                openParty = group;
            }
            html += avatarHtml(
                pick(w, 'UserId'),
                pick(w, 'Name'),
                pick(w, 'ImageTag'),
                pick(w, 'IsLive') ? ' wt-avatar-live' : '',
                'z-index:' + (max - index + 1) + ';');
        });
        if (openParty) { html += '</span>'; }
        if (watchers.length > max) {
            html += '<span class="wt-avatar wt-more"><span class="wt-initials">+' + (watchers.length - max) + '</span></span>';
        }
        return html;
    }

    function updateProgressBar(host, live, settings) {
        var bar = host.querySelector('.wt-live-progress');
        var progress = live ? pick(live, 'Progress') : null;
        if (typeof progress !== 'number') {
            if (bar) { bar.parentNode.removeChild(bar); }
            return;
        }

        if (!bar) {
            bar = document.createElement('div');
            bar.className = 'wt-live-progress';
            bar.innerHTML = '<div class="wt-live-progress-fill"><span class="wt-live-progress-head"></span></div>';
            host.appendChild(bar);
        }

        var fill = bar.querySelector('.wt-live-progress-fill');
        var paused = !!pick(live, 'IsPaused');
        bar.classList.toggle('wt-paused', paused);
        bar.setAttribute('title', pick(live, 'Name') + (paused ? ' paused at ' : ' is ') + percent(progress) + (paused ? '' : ' through'));

        // Snap to the real position, then glide toward where they'll be at the next refresh.
        fill.style.transition = 'width 0.6s ease-out';
        fill.style.width = (progress * 100).toFixed(2) + '%';

        clearTimeout(bar._wtTimer);
        var runtime = pick(live, 'RuntimeSeconds');
        if (!paused && typeof runtime === 'number' && runtime > 0) {
            var seconds = settings.refreshSec;
            var predicted = Math.min(1, progress + seconds / runtime);
            bar._wtTimer = setTimeout(function () {
                fill.style.transition = 'width ' + (seconds - 0.6) + 's linear';
                fill.style.width = (predicted * 100).toFixed(2) + '%';
            }, 650);
        }
    }

    function removeDecorations(card) {
        Array.prototype.forEach.call(card.querySelectorAll('.wt-avatars, .wt-caption'), function (el) {
            el.parentNode.removeChild(el);
        });
    }

    function decorateCard(card, watchers, settings) {
        removeDecorations(card);
        var host = card.querySelector('.cardScalable') || card.querySelector('.cardBox') || card;

        if (!watchers || watchers.length === 0) {
            updateProgressBar(host, null, settings);
            card.setAttribute(STAMP, String(state.fetchedAt));
            return;
        }

        var tooltip = tooltipFor(watchers);
        var names = watchers.map(function (w) { return pick(w, 'Name'); });
        var firstLive = watchers.filter(function (w) { return pick(w, 'IsLive'); })[0];

        // Sizes are relative to the card's width (container query units), so they scale with the screen.
        sizeHost(host, settings.sizePct);

        if (settings.showAvatars) {
            var wrap = document.createElement('div');
            wrap.className = 'wt-avatars';
            wrap.setAttribute('title', tooltip);
            wrap.setAttribute('aria-label', (firstLive ? 'Watching now: ' : 'Watched by ') + names.join(', '));
            wrap.innerHTML = settings.showAvatars ? buildAvatarsHtml(watchers, settings.maxAvatars) : '';
            host.appendChild(wrap);
            wireImageFallbacks(wrap);
        }

        updateProgressBar(host, firstLive, settings);

        if (settings.showCaption) {
            var footerHost = card.querySelector('.cardFooter') || card.querySelector('.cardBox') || card;
            var info = captionFor(watchers);
            var caption = document.createElement('div');
            caption.className = 'cardText cardTextCentered cardText-secondary wt-caption' + (info.live ? ' wt-caption-live' : '');
            caption.setAttribute('title', tooltip);
            caption.innerHTML = (info.live
                ? '<span class="wt-live-dot" aria-hidden="true"></span>'
                : '<span class="material-icons wt-caption-icon" aria-hidden="true">visibility</span>') +
                '<span class="wt-caption-text">' + escapeHtml(info.text) + '</span>';
            footerHost.appendChild(caption);
        }

        card.setAttribute(STAMP, String(state.fetchedAt));
    }

    function decorateShelf() {
        var sections = document.querySelectorAll(sectionSelector(state.sectionId));
        if (sections.length === 0) {
            return;
        }

        var cards = [];
        var hasNewSection = false;
        Array.prototype.forEach.call(sections, function (section) {
            if (!section.hasAttribute('data-wt-seen')) {
                section.setAttribute('data-wt-seen', '1');
                hasNewSection = true;
            }
            Array.prototype.forEach.call(section.querySelectorAll('.card[data-id]'), function (card) {
                // Each card is stamped with the snapshot it was decorated from; anything else is redone.
                if (hasNewSection || card.getAttribute(STAMP) !== String(state.fetchedAt)) {
                    cards.push(card);
                }
            });
        });

        if (cards.length === 0) {
            return;
        }

        // A freshly rendered home screen should reflect the latest activity.
        fetchWatchers(hasNewSection).then(function (response) {
            if (!response) { return; }
            var lookup = lookupFrom(response);
            var settings = settingsFrom(response);
            state.lookup = lookup;
            state.settings = settings;
            cards.forEach(function (card) {
                if (!isAttached(card)) { return; }
                decorateCard(card, lookup[normaliseId(card.getAttribute('data-id'))], settings);
            });
        });
    }

    /**
     * Someone started watching something that isn't on the shelf yet: ask the shelf's
     * items container (set up by Home Screen Sections) to fetch and redraw itself.
     */
    function refreshShelfIfLiveMissing(response) {
        var lookup = lookupFrom(response);
        var liveIds = Object.keys(lookup).filter(function (id) {
            return (lookup[id] || []).some(function (w) { return pick(w, 'IsLive'); });
        }).sort();

        var sig = liveIds.join(',');
        if (!sig || sig === state.lastShelfRefreshSig) {
            return;
        }

        Array.prototype.forEach.call(document.querySelectorAll(sectionSelector(state.sectionId)), function (section) {
            var onShelf = {};
            Array.prototype.forEach.call(section.querySelectorAll('.card[data-id]'), function (card) {
                onShelf[normaliseId(card.getAttribute('data-id'))] = true;
            });

            var missing = liveIds.some(function (id) { return !onShelf[id]; });
            var container = section.querySelector('.itemsContainer');
            if (missing && container) {
                state.lastShelfRefreshSig = sig;
                if (typeof container.refreshItems === 'function') {
                    container.refreshItems();
                } else if (typeof container.notifyRefreshNeeded === 'function') {
                    container.notifyRefreshNeeded(true);
                }
            }
        });
    }

    // ---------- dialogs (mouse, touch, keyboard, TV remote and gamepad) ----------

    var BACK_KEYS = { Escape: 1, Back: 1, GoBack: 1, BrowserBack: 1, GamepadB: 1 };
    var BACK_CODES = { 27: 1, 461: 1, 10009: 1, 166: 1 };   // Esc, webOS Back, Tizen Return, BrowserBack
    var PREV_KEYS = { ArrowLeft: 1, ArrowUp: 1, GamepadDPadLeft: 1, GamepadDPadUp: 1, GamepadLeftThumbstickLeft: 1, GamepadLeftThumbstickUp: 1 };
    var NEXT_KEYS = { ArrowRight: 1, ArrowDown: 1, GamepadDPadRight: 1, GamepadDPadDown: 1, GamepadLeftThumbstickRight: 1, GamepadLeftThumbstickDown: 1 };
    var SELECT_KEYS = { GamepadA: 1 };

    /**
     * A small modal that works everywhere Jellyfin's web client runs. Buttons are real
     * <button>s, focus starts on a safe default and is kept inside the dialog; arrows / D-pad
     * move between buttons, OK / Enter / A activates, Back / Esc / B cancels. Keys are
     * handled first and marked as handled, so Jellyfin's own navigation and the video
     * player's shortcuts ignore them while the dialog is open.
     */
    function openDialog(opts) {
        if (state.dialogOpen) { return null; }
        state.dialogOpen = true;

        var previousFocus = document.activeElement;
        var backdrop = document.createElement('div');
        backdrop.className = 'wt-dialog-backdrop';
        var panel = document.createElement('div');
        panel.className = 'wt-dialog';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-modal', 'true');
        panel.setAttribute('aria-label', opts.label || 'Watch together');
        panel.innerHTML =
            '<div class="wt-dialog-head">' + (opts.avatarsHtml ? '<div class="wt-dialog-avatars">' + opts.avatarsHtml + '</div>' : '') +
            '<div class="wt-dialog-text"><div class="wt-dialog-title">' + opts.titleHtml + '</div>' +
            (opts.subtitle ? '<div class="wt-dialog-subtitle">' + escapeHtml(opts.subtitle) + '</div>' : '') + '</div></div>' +
            (opts.note ? '<div class="wt-dialog-note">' + escapeHtml(opts.note) + '</div>' : '') +
            '<div class="wt-dialog-actions"></div>' +
            (opts.timeoutSeconds ? '<div class="wt-dialog-timer"><div class="wt-dialog-timer-fill"></div></div>' : '');
        backdrop.appendChild(panel);
        document.body.appendChild(backdrop);
        wireImageFallbacks(panel);

        var actions = panel.querySelector('.wt-dialog-actions');
        var buttons = opts.buttons.map(function (b) {
            var el = document.createElement('button');
            el.type = 'button';
            el.className = 'wt-btn ' + (b.primary ? 'wt-btn-go' : 'wt-btn-quiet');
            el.textContent = b.label;
            el.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                close();
                if (b.onClick) { b.onClick(); }
            });
            actions.appendChild(el);
            return el;
        });

        var expiry = null;
        if (opts.timeoutSeconds) {
            var fill = panel.querySelector('.wt-dialog-timer-fill');
            requestAnimationFrame(function () {
                requestAnimationFrame(function () {
                    fill.style.transition = 'width ' + opts.timeoutSeconds + 's linear';
                    fill.style.width = '0%';
                });
            });
            expiry = setTimeout(cancel, opts.timeoutSeconds * 1000);
        }

        function focusIndex(i) {
            var n = buttons.length;
            buttons[((i % n) + n) % n].focus();
        }

        function currentIndex() {
            return buttons.indexOf(document.activeElement);
        }

        function onKey(e) {
            var key = e.key;
            var handled = true;
            if (BACK_KEYS[key] || BACK_CODES[e.keyCode]) {
                cancel();
            } else if (PREV_KEYS[key]) {
                focusIndex(currentIndex() < 0 ? 0 : currentIndex() - 1);
            } else if (NEXT_KEYS[key]) {
                focusIndex(currentIndex() < 0 ? 0 : currentIndex() + 1);
            } else if (key === 'Tab') {
                focusIndex((currentIndex() < 0 ? 0 : currentIndex()) + (e.shiftKey ? -1 : 1));
            } else if (SELECT_KEYS[key]) {
                var i = currentIndex();
                (i < 0 ? buttons[opts.defaultIndex || 0] : buttons[i]).click();
            } else if (key === 'Enter' || key === ' ' || key === 'Spacebar') {
                handled = false; // let the focused button activate natively
                if (currentIndex() < 0) { buttons[opts.defaultIndex || 0].click(); handled = true; }
            }
            // Keep every key away from Jellyfin's navigation / the player while we're open.
            if (handled) { e.preventDefault(); }
            e.stopPropagation();
        }

        function onFocusIn(e) {
            if (!panel.contains(e.target)) { focusIndex(opts.defaultIndex || 0); }
        }

        function close() {
            if (!state.dialogOpen) { return; }
            state.dialogOpen = false;
            clearTimeout(expiry);
            window.removeEventListener('keydown', onKey, true);
            document.removeEventListener('focusin', onFocusIn, true);
            if (backdrop.parentNode) { backdrop.parentNode.removeChild(backdrop); }
            if (previousFocus && previousFocus.focus && isAttached(previousFocus)) {
                try { previousFocus.focus(); } catch (err) { /* ignore */ }
            }
        }

        function cancel() {
            close();
            if (opts.onCancel) { opts.onCancel(); }
        }

        backdrop.addEventListener('click', function (e) {
            if (e.target === backdrop) { cancel(); }
        });
        window.addEventListener('keydown', onKey, true);
        document.addEventListener('focusin', onFocusIn, true);
        setTimeout(function () { focusIndex(opts.defaultIndex || 0); }, 0);

        return { close: close };
    }

    // ---------- clicking a card someone is watching live ----------

    function liveOthersOn(card) {
        var settings = state.settings;
        if (!settings || !settings.watchTogether) { return []; }
        var watchers = state.lookup[normaliseId(card.getAttribute('data-id'))] || [];
        return watchers.filter(function (w) {
            return pick(w, 'IsLive') && normaliseId(pick(w, 'UserId')) !== settings.viewerId;
        });
    }

    /**
     * Capture-phase click handler: runs before Jellyfin's own card handler. A remote's OK or a
     * gamepad's A button becomes a click on the focused card, so this covers every input.
     */
    function onCardActivate(e) {
        var target = e.target;
        if (!target || !target.closest || state.dialogOpen) { return; }

        var card = target.closest('.card[data-id]');
        if (!card || !card.closest(sectionSelector(state.sectionId))) { return; }

        if (card._wtBypass) {
            card._wtBypass = false;
            return;
        }

        // Leave the "more" menu and similar secondary actions alone.
        var actionEl = target.closest('[data-action]');
        var action = actionEl ? actionEl.getAttribute('data-action') : '';
        if (/^(menu|multiselect|none|playmenu|addtoplaylist|edit)$/.test(action)) { return; }

        var live = liveOthersOn(card);
        if (live.length === 0) { return; }

        e.preventDefault();
        e.stopPropagation();
        if (e.stopImmediatePropagation) { e.stopImmediatePropagation(); }

        showWatchChoice(card, actionEl || card, live);
    }

    function continueOnMyOwn(card, actionEl) {
        card._wtBypass = true;
        try {
            actionEl.click();
        } finally {
            setTimeout(function () { card._wtBypass = false; }, 500);
        }
    }

    function showWatchChoice(card, actionEl, live) {
        var lead = live[0];
        var party = partyOf(live);
        var names = joinNames(live.map(function (w) { return pick(w, 'Name'); }));
        var detail = pick(lead, 'Detail');
        var pct = percent(pick(lead, 'Progress'));
        var title = card.querySelector('.cardText-first') ? card.querySelector('.cardText-first').textContent.trim() : '';

        var subtitle = [title, detail, pct ? pct + ' in' : null].filter(Boolean).join(' · ');
        var avatars = live.slice(0, 4).map(function (w) {
            return avatarHtml(pick(w, 'UserId'), pick(w, 'Name'), pick(w, 'ImageTag'), ' wt-avatar-live', '');
        }).join('');

        openDialog({
            label: names + ' watching now',
            avatarsHtml: avatars,
            titleHtml: '<b>' + escapeHtml(names) + '</b> ' + (live.length > 1 ? (party ? 'are watching this together' : 'are watching this') : (pick(lead, 'IsPaused') ? 'paused this' : 'is watching this')) + ' right now',
            subtitle: subtitle,
            note: party
                ? 'Join their SyncPlay group to watch in sync with them.'
                : 'Ask to start SyncPlay from where ' + pick(lead, 'Name') + ' is, or start it yourself.',
            defaultIndex: 0,
            buttons: [
                { label: party ? 'Join their watch party' : 'Ask to watch together', primary: true, onClick: function () { askToWatchTogether({ id: pick(lead, 'UserId'), name: party ? names : pick(lead, 'Name') }); } },
                { label: 'Watch on my own', onClick: function () { continueOnMyOwn(card, actionEl); } }
            ]
        });
    }

    // ---------- watch together (SyncPlay invites) ----------

    function askToWatchTogether(target) {
        postJson('WatchedTogether/Invites', { UserId: target.id }).then(function (result) {
            var outcome = pick(result, 'Outcome');
            if (outcome === 'Joined') {
                toast('Joining <b>' + escapeHtml(target.name) + '</b>\'s watch party…');
                return;
            }
            if (outcome !== 'Invited') {
                toast(escapeHtml(pick(result, 'Message') || 'Couldn\'t send the invite.'));
                return;
            }
            waitForAnswer(pick(pick(result, 'Invite'), 'Id'), target);
        }, function () {
            toast('Couldn\'t send the invite.');
        });
    }

    function waitForAnswer(inviteId, target) {
        var started = Date.now();
        toast('<span class="wt-spinner"></span>Asking <b>' + escapeHtml(target.name) + '</b> to watch together…', { sticky: true });

        function poll() {
            var client = apiClient();
            client.getJSON(client.getUrl('WatchedTogether/Invites/' + inviteId)).then(function (invite) {
                var status = pick(invite, 'Status');
                if (status === 'Pending') {
                    if (Date.now() - started < 100000) { setTimeout(poll, 2000); } else { hideToast(); }
                    return;
                }
                if (status === 'Accepted') {
                    toast('<b>' + escapeHtml(target.name) + '</b> said yes! Starting SyncPlay…');
                    if (!pick(invite, 'RequesterJoined') && pick(invite, 'GroupId')) {
                        postNoContent('SyncPlay/Join', { GroupId: pick(invite, 'GroupId') }).catch(function () {
                            toast('Couldn\'t join the SyncPlay group. Try joining it from the SyncPlay menu.');
                        });
                    }
                    return;
                }
                if (status === 'Declined') {
                    toast('<b>' + escapeHtml(target.name) + '</b> can\'t right now.');
                } else if (status === 'Expired') {
                    toast('No answer from <b>' + escapeHtml(target.name) + '</b>. They may be on an app that can\'t show invites.');
                } else {
                    toast(escapeHtml(pick(invite, 'Message') || 'Couldn\'t start watching together.'));
                }
            }, function () {
                setTimeout(poll, 4000);
            });
        }

        setTimeout(poll, 1500);
    }

    function showInvite(invite) {
        var id = pick(invite, 'Id');
        if (state.incomingShown[id] || state.dialogOpen) { return; }
        state.incomingShown[id] = true;

        var from = pick(invite, 'FromName');
        function answer(accept) {
            postJson('WatchedTogether/Invites/' + id + '/Respond', { Accept: accept }).then(function (result) {
                if (!accept) { return; }
                if (pick(result, 'Status') === 'Accepted') {
                    toast('Watching together with <b>' + escapeHtml(from) + '</b> — SyncPlay is on.');
                } else {
                    toast(escapeHtml(pick(result, 'Message') || 'Couldn\'t start watching together.'));
                }
            }, function () {
                toast('Couldn\'t answer the invite.');
            });
        }

        // "Not now" has focus by default, so a stray OK press on a remote never accepts by accident.
        openDialog({
            label: from + ' wants to watch together',
            avatarsHtml: avatarHtml(pick(invite, 'FromUserId'), from, pick(invite, 'FromImageTag'), '', ''),
            titleHtml: '<b>' + escapeHtml(from) + '</b> wants to watch with you',
            subtitle: pick(invite, 'ItemName'),
            note: 'Starts SyncPlay from where you are now. Pausing and seeking will happen for both of you.',
            timeoutSeconds: Math.max(5, pick(invite, 'SecondsLeft') || 60),
            defaultIndex: 0,
            buttons: [
                { label: 'Not now', onClick: function () { answer(false); } },
                { label: 'Watch together', primary: true, onClick: function () { answer(true); } }
            ],
            onCancel: function () { answer(false); }
        });
    }

    function pollIncoming() {
        var client = apiClient();
        if (!client || !currentUserId() || document.hidden) {
            setTimeout(pollIncoming, state.incomingDelayMs);
            return;
        }

        client.getJSON(client.getUrl('WatchedTogether/Invites/Incoming')).then(function (response) {
            state.incomingDelayMs = pick(response, 'Enabled') === false ? 60000 : 5000;
            (pick(response, 'Invites') || []).forEach(showInvite);
        }, function () {
            state.incomingDelayMs = 30000;
        }).then(function () {
            setTimeout(pollIncoming, state.incomingDelayMs);
        });
    }

    // ---------- Most Popular medals ----------

    function fetchPopular(force) {
        var client = apiClient();
        if (!client || !currentUserId()) { return Promise.resolve(null); }
        if (state.popular && !force && Date.now() - state.popularAt < 60000) {
            return Promise.resolve(state.popular);
        }
        if (state.popularPending) { return state.popularPending; }

        state.popularPending = client.getJSON(client.getUrl('WatchedTogether/Popular')).then(function (response) {
            state.popular = response;
            state.popularAt = Date.now();
            return response;
        }, function () {
            return null;
        }).then(function (r) {
            state.popularPending = null;
            return r;
        });
        return state.popularPending;
    }

    function windowText(days) {
        if (!days) { return 'of all time'; }
        if (days === 7) { return 'this week'; }
        if (days === 365) { return 'this year'; }
        return 'in the last ' + days + ' days';
    }

    function decorateMedal(card, index, info, response) {
        var old = card.querySelector('.wt-medal');
        if (old) { old.parentNode.removeChild(old); }

        var rank = info ? pick(info, 'Rank') : index + 1;
        var host = card.querySelector('.cardScalable') || card.querySelector('.cardBox') || card;
        sizeHost(host, pick(response, 'AvatarSizePercent') || 16);

        var tier = rank === 1 ? 'wt-gold' : rank === 2 ? 'wt-silver' : rank === 3 ? 'wt-bronze' : 'wt-plain';
        var medal = document.createElement('div');
        medal.className = 'wt-medal ' + tier;
        var title = '#' + rank + ' most watched ' + windowText(pick(response, 'Days'));
        if (info) {
            title += ' — ' + pick(info, 'Hours') + ' hours over ' + pick(info, 'Plays') + ' plays';
        }
        medal.setAttribute('title', title);
        medal.setAttribute('aria-label', title);
        medal.innerHTML = (rank <= 3 ? '<span class="wt-medal-ribbon" aria-hidden="true"></span>' : '') +
            '<span class="wt-medal-disc"><span class="wt-medal-num">' + rank + '</span></span>';
        host.appendChild(medal);
        card.setAttribute(MEDAL_STAMP, String(state.popularAt));
    }

    function decoratePopular() {
        var pendingCards = [];
        var newSection = false;
        POPULAR_IDS.forEach(function (sectionId) {
            Array.prototype.forEach.call(document.querySelectorAll(sectionSelector(sectionId)), function (section) {
                if (!section.hasAttribute('data-wt-seen')) {
                    section.setAttribute('data-wt-seen', '1');
                    newSection = true;
                }
                Array.prototype.forEach.call(section.querySelectorAll('.card[data-id]'), function (card, index) {
                    if (newSection || card.getAttribute(MEDAL_STAMP) !== String(state.popularAt)) {
                        pendingCards.push({ sectionId: sectionId, card: card, index: index });
                    }
                });
            });
        });

        if (pendingCards.length === 0) { return; }

        fetchPopular(newSection).then(function (response) {
            if (!response) { return; }
            var sections = pick(response, 'Sections') || {};
            pendingCards.forEach(function (entry) {
                if (!isAttached(entry.card)) { return; }
                var map = sections[entry.sectionId] || {};
                var info = null;
                var id = normaliseId(entry.card.getAttribute('data-id'));
                Object.keys(map).forEach(function (key) {
                    if (normaliseId(key) === id) { info = map[key]; }
                });
                decorateMedal(entry.card, entry.index, info, response);
            });
        });
    }

    // ---------- scheduling ----------

    function decorateAll() {
        state.scheduled = false;
        decorateShelf();
        decoratePopular();
    }

    function schedule() {
        if (state.scheduled) { return; }
        state.scheduled = true;
        setTimeout(decorateAll, 150);
    }

    // Keep LIVE badges, progress and the shelf itself current while it's on screen.
    function liveTick() {
        setTimeout(liveTick, state.liveRefreshMs || 15000);

        if (!state.liveRefreshMs || document.hidden || document.querySelectorAll(sectionSelector(state.sectionId)).length === 0) {
            return;
        }

        fetchWatchers(true).then(function (response) {
            if (!response) { return; }
            refreshShelfIfLiveMissing(response);
            schedule();
        });
    }

    function start() {
        if (!document.body) {
            setTimeout(start, 100);
            return;
        }

        var Observer = window.MutationObserver || window.WebKitMutationObserver;
        if (Observer) {
            new Observer(function (records) {
                for (var i = 0; i < records.length; i++) {
                    if (records[i].addedNodes && records[i].addedNodes.length > 0) {
                        schedule();
                        return;
                    }
                }
            }).observe(document.body, { childList: true, subtree: true });
        }

        document.addEventListener('click', onCardActivate, true);

        schedule();
        setTimeout(liveTick, 15000);
        setTimeout(pollIncoming, 3000);
    }

    start();
})();
