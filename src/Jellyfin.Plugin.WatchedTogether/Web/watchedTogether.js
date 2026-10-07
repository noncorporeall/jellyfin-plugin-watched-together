/*
 * Watched Together — client add-on.
 * Injected into jellyfin-web by File Transformation. Finds the Watched Together shelf that
 * Home Screen Sections renders, and decorates each card with the profile pictures (and,
 * optionally, a caption) of the people who watched it.
 */
(function () {
    'use strict';

    if (window.__watchedTogetherLoaded) {
        return;
    }
    window.__watchedTogetherLoaded = true;

    var DEFAULT_SECTION_ID = 'WatchedTogether';
    var CACHE_MS = 30 * 1000;
    var DECORATED_ATTR = 'data-wt-decorated';

    var state = {
        sectionId: DEFAULT_SECTION_ID,
        data: null,
        fetchedAt: 0,
        userId: null,
        pending: null,
        scheduled: false,
        liveRefreshMs: 0
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
        var days = Math.round(hours / 24);
        return days + 'd ago';
    }

    function joinNames(names) {
        if (names.length === 0) { return ''; }
        if (names.length === 1) { return names[0]; }
        if (names.length === 2) { return names[0] + ' & ' + names[1]; }
        return names[0] + ', ' + names[1] + ' +' + (names.length - 2);
    }

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

    function avatarUrl(watcher) {
        var client = apiClient();
        var tag = pick(watcher, 'ImageTag');
        if (!client || !tag) { return null; }
        return client.getUrl('UserImage', {
            userId: pick(watcher, 'UserId'),
            tag: tag,
            maxWidth: 192,
            quality: 90
        });
    }

    // ---------- data ----------

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
                state.sectionId = pick(response, 'SectionId') || DEFAULT_SECTION_ID;
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

    // ---------- rendering ----------

    function percent(p) {
        return (typeof p === 'number' && isFinite(p)) ? Math.round(p * 100) + '%' : null;
    }

    function buildAvatarsHtml(watchers, max) {
        var html = '';
        var shown = watchers.slice(0, max);
        shown.forEach(function (w, index) {
            var name = pick(w, 'Name');
            var url = avatarUrl(w);
            var live = pick(w, 'IsLive') ? ' wt-avatar-live' : '';
            var style = 'z-index:' + (max - index + 1) + ';background-color:' + colourFor(name) + ';';
            html += '<span class="wt-avatar' + live + '" style="' + style + '" data-initials="' + escapeHtml(initials(name)) + '">';
            if (url) {
                html += '<img src="' + escapeHtml(url) + '" alt="' + escapeHtml(name) + '" loading="lazy" />';
            } else {
                html += '<span class="wt-initials">' + escapeHtml(initials(name)) + '</span>';
            }
            html += '</span>';
        });

        if (watchers.length > max) {
            html += '<span class="wt-avatar wt-more"><span class="wt-initials">+' + (watchers.length - max) + '</span></span>';
        }

        return html;
    }

    function tooltipFor(watchers) {
        return watchers.map(function (w) {
            var line = pick(w, 'Name');
            var detail = pick(w, 'Detail');
            if (detail) { line += ' — ' + detail; }
            if (pick(w, 'IsLive')) {
                var pct = percent(pick(w, 'Progress'));
                line += pick(w, 'IsPaused') ? ' (paused' : ' (watching now';
                line += pct ? ', ' + pct + ')' : ')';
                return line;
            }
            line += pick(w, 'Finished') ? ' (finished' : ' (watched';
            var ago = timeAgo(pick(w, 'LastPlayed'));
            line += ago ? ', ' + ago + ')' : ')';
            return line;
        }).join('\n');
    }

    function captionFor(watchers) {
        var live = watchers.filter(function (w) { return pick(w, 'IsLive'); });
        if (live.length === 0) {
            return { live: false, text: joinNames(watchers.map(function (w) { return pick(w, 'Name'); })) };
        }

        if (live.length === 1) {
            var w = live[0];
            var pct = percent(pick(w, 'Progress'));
            var verb = pick(w, 'IsPaused') ? ' paused' : ' is watching';
            return { live: true, text: pick(w, 'Name') + verb + (pct ? ' · ' + pct : '') };
        }

        return { live: true, text: joinNames(live.map(function (x) { return pick(x, 'Name'); })) + ' are watching' };
    }

    function removeDecorations(card) {
        Array.prototype.forEach.call(card.querySelectorAll('.wt-avatars, .wt-caption, .wt-live-progress'), function (el) {
            el.parentNode.removeChild(el);
        });
    }

    function decorateCard(card, watchers, settings) {
        // Remove an older decoration (cards are redrawn on every refresh).
        removeDecorations(card);

        if (!watchers || watchers.length === 0) {
            card.setAttribute(DECORATED_ATTR, String(state.fetchedAt));
            return;
        }

        var tooltip = tooltipFor(watchers);
        var names = watchers.map(function (w) { return pick(w, 'Name'); });
        var firstLive = watchers.filter(function (w) { return pick(w, 'IsLive'); })[0];
        var host = card.querySelector('.cardScalable') || card.querySelector('.cardBox') || card;

        // Sizes are relative to the card's width (container query units), so they scale with the screen.
        host.classList.add('wt-host');
        host.style.setProperty('--wt-pct', String(settings.sizePct));

        if (settings.showAvatars || firstLive) {
            var wrap = document.createElement('div');
            wrap.className = 'wt-avatars';
            wrap.setAttribute('title', tooltip);
            wrap.setAttribute('aria-label', (firstLive ? 'Watching now: ' : 'Watched by ') + names.join(', '));
            var html = settings.showAvatars ? buildAvatarsHtml(watchers, settings.maxAvatars) : '';
            if (firstLive) {
                html += '<span class="wt-live-pill' + (pick(firstLive, 'IsPaused') ? ' wt-paused' : '') + '">' +
                    '<span class="wt-live-dot"></span>' + (pick(firstLive, 'IsPaused') ? 'PAUSED' : 'LIVE') + '</span>';
            }
            wrap.innerHTML = html;
            host.appendChild(wrap);

            Array.prototype.forEach.call(wrap.querySelectorAll('img'), function (img) {
                img.addEventListener('error', function () {
                    var parent = img.parentNode;
                    if (parent) {
                        parent.innerHTML = '<span class="wt-initials">' + escapeHtml(parent.getAttribute('data-initials') || '?') + '</span>';
                    }
                }, { once: true });
            });
        }

        if (firstLive) {
            var progress = pick(firstLive, 'Progress');
            if (typeof progress === 'number') {
                var bar = document.createElement('div');
                bar.className = 'wt-live-progress';
                bar.setAttribute('title', pick(firstLive, 'Name') + ' is ' + percent(progress) + ' through');
                bar.innerHTML = '<div class="wt-live-progress-fill" style="width:' + (progress * 100).toFixed(1) + '%"></div>';
                host.appendChild(bar);
            }
        }

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

        card.setAttribute(DECORATED_ATTR, String(state.fetchedAt));
    }

    function findSections() {
        var selector = '.verticalSection.' + (window.CSS && CSS.escape ? CSS.escape(state.sectionId) : state.sectionId);
        return document.querySelectorAll(selector);
    }

    function settingsFrom(response) {
        return {
            showAvatars: pick(response, 'ShowAvatars') !== false,
            showCaption: pick(response, 'ShowNamesCaption') !== false,
            maxAvatars: pick(response, 'MaxAvatarsPerCard') || 3,
            sizePct: pick(response, 'AvatarSizePercent') || 16
        };
    }

    function decorateAll() {
        state.scheduled = false;

        var sections = findSections();
        if (sections.length === 0) {
            return;
        }

        var undecorated = [];
        var hasNewSection = false;
        Array.prototype.forEach.call(sections, function (section) {
            if (!section.hasAttribute('data-wt-seen')) {
                section.setAttribute('data-wt-seen', '1');
                hasNewSection = true;
            }
            Array.prototype.forEach.call(section.querySelectorAll('.card[data-id]'), function (card) {
                // Each card is stamped with the snapshot it was decorated from; anything else is redone.
                if (hasNewSection || card.getAttribute(DECORATED_ATTR) !== String(state.fetchedAt)) {
                    undecorated.push(card);
                }
            });
        });

        if (undecorated.length === 0) {
            return;
        }

        // A freshly rendered home screen should reflect the latest activity.
        fetchWatchers(hasNewSection).then(function (response) {
            if (!response) { return; }
            var items = pick(response, 'Items') || {};
            var lookup = {};
            Object.keys(items).forEach(function (key) {
                lookup[normaliseId(key)] = items[key];
            });

            var settings = settingsFrom(response);
            state.liveRefreshMs = (pick(response, 'LiveRefreshSeconds') || 0) * 1000;

            undecorated.forEach(function (card) {
                if (!card.isConnected) { return; }
                decorateCard(card, lookup[normaliseId(card.getAttribute('data-id'))], settings);
            });
        });
    }

    function schedule() {
        if (state.scheduled) { return; }
        state.scheduled = true;
        setTimeout(decorateAll, 150);
    }

    // Keep LIVE badges and progress current while the shelf is on screen.
    function liveTick() {
        var delay = state.liveRefreshMs || 15000;
        setTimeout(liveTick, delay);

        if (!state.liveRefreshMs || document.hidden || findSections().length === 0) {
            return;
        }

        fetchWatchers(true).then(function (response) {
            if (response) { schedule(); }
        });
    }

    function start() {
        if (!document.body) {
            setTimeout(start, 100);
            return;
        }

        var Observer = window.MutationObserver || window.WebKitMutationObserver;
        if (!Observer) { return; }

        new Observer(function (records) {
            for (var i = 0; i < records.length; i++) {
                if (records[i].addedNodes && records[i].addedNodes.length > 0) {
                    schedule();
                    return;
                }
            }
        }).observe(document.body, { childList: true, subtree: true });

        schedule();
        setTimeout(liveTick, 15000);
    }

    start();
})();
