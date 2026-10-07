/*
 * Watched Together — client add-on.
 * Injected into jellyfin-web by File Transformation. It:
 *  - decorates the "recently watched" shelf with watchers' profile pictures, LIVE badges and progress,
 *  - refreshes that shelf in place when someone starts watching something new,
 *  - offers "Join" on live cards and shows incoming "watch together" invites (SyncPlay),
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
        incomingDelayMs: 5000
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
        if (live.length === 1) {
            var w = live[0];
            var pct = percent(pick(w, 'Progress'));
            var verb = pick(w, 'IsPaused') ? ' paused' : ' is watching';
            return { live: true, text: pick(w, 'Name') + verb + (pct ? ' · ' + pct : '') };
        }
        return { live: true, text: joinNames(live.map(function (x) { return pick(x, 'Name'); })) + ' are watching' };
    }

    function buildAvatarsHtml(watchers, max) {
        var html = '';
        watchers.slice(0, max).forEach(function (w, index) {
            html += avatarHtml(
                pick(w, 'UserId'),
                pick(w, 'Name'),
                pick(w, 'ImageTag'),
                pick(w, 'IsLive') ? ' wt-avatar-live' : '',
                'z-index:' + (max - index + 1) + ';');
        });
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
        host.classList.add('wt-host');
        host.style.setProperty('--wt-pct', String(settings.sizePct));

        if (settings.showAvatars || firstLive) {
            var wrap = document.createElement('div');
            wrap.className = 'wt-avatars';
            wrap.setAttribute('title', tooltip);
            wrap.setAttribute('aria-label', (firstLive ? 'Watching now: ' : 'Watched by ') + names.join(', '));
            var html = settings.showAvatars ? buildAvatarsHtml(watchers, settings.maxAvatars) : '';
            if (firstLive) {
                var paused = !!pick(firstLive, 'IsPaused');
                html += '<span class="wt-live-pill' + (paused ? ' wt-paused' : '') + '">' +
                    '<span class="wt-live-dot"></span>' + (paused ? 'PAUSED' : 'LIVE') + '</span>';

                if (settings.watchTogether && normaliseId(pick(firstLive, 'UserId')) !== settings.viewerId) {
                    html += '<button type="button" class="wt-join" title="Ask ' + escapeHtml(pick(firstLive, 'Name')) + ' to watch together">' +
                        '<span class="material-icons" aria-hidden="true">group_add</span><span class="wt-join-text">Join</span></button>';
                }
            }
            wrap.innerHTML = html;
            host.appendChild(wrap);
            wireImageFallbacks(wrap);

            var joinBtn = wrap.querySelector('.wt-join');
            if (joinBtn) {
                var target = { id: pick(firstLive, 'UserId'), name: pick(firstLive, 'Name') };
                joinBtn.addEventListener('click', function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    askToWatchTogether(target, joinBtn);
                });
            }
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
            cards.forEach(function (card) {
                if (!card.isConnected) { return; }
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

    // ---------- watch together (SyncPlay invites) ----------

    function askToWatchTogether(target, button) {
        if (button) { button.disabled = true; }
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
            var invite = pick(result, 'Invite');
            waitForAnswer(pick(invite, 'Id'), target);
        }, function () {
            toast('Couldn\'t send the invite.');
        }).then(function () {
            if (button) { setTimeout(function () { button.disabled = false; }, 3000); }
        });
    }

    function waitForAnswer(inviteId, target) {
        var cancelled = false;
        var started = Date.now();
        toast('<span class="wt-spinner"></span>Asking <b>' + escapeHtml(target.name) + '</b> to watch together…', {
            sticky: true,
            action: 'Cancel',
            onAction: function () { cancelled = true; }
        });

        function poll() {
            if (cancelled) { return; }
            var client = apiClient();
            client.getJSON(client.getUrl('WatchedTogether/Invites/' + inviteId)).then(function (invite) {
                var status = pick(invite, 'Status');
                if (status === 'Pending') {
                    if (Date.now() - started < 100000) { setTimeout(poll, 2000); }
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
        if (state.incomingShown[id] || document.querySelector('.wt-invite')) { return; }
        state.incomingShown[id] = true;

        var from = pick(invite, 'FromName');
        var panel = document.createElement('div');
        panel.className = 'wt-invite';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-label', from + ' wants to watch together');
        panel.innerHTML =
            '<div class="wt-invite-head">' +
            avatarHtml(pick(invite, 'FromUserId'), from, pick(invite, 'FromImageTag'), ' wt-invite-avatar', '') +
            '<div class="wt-invite-text"><div class="wt-invite-title"><b>' + escapeHtml(from) + '</b> wants to watch with you</div>' +
            '<div class="wt-invite-item">' + escapeHtml(pick(invite, 'ItemName')) + '</div></div></div>' +
            '<div class="wt-invite-note">Starts SyncPlay from where you are now. Pausing and seeking will happen for both of you.</div>' +
            '<div class="wt-invite-actions"><button type="button" class="wt-btn wt-btn-quiet" data-answer="no">Not now</button>' +
            '<button type="button" class="wt-btn wt-btn-go" data-answer="yes">Watch together</button></div>' +
            '<div class="wt-invite-timer"><div class="wt-invite-timer-fill"></div></div>';
        document.body.appendChild(panel);
        wireImageFallbacks(panel);

        var seconds = Math.max(5, pick(invite, 'SecondsLeft') || 60);
        var timerFill = panel.querySelector('.wt-invite-timer-fill');
        requestAnimationFrame(function () {
            requestAnimationFrame(function () {
                timerFill.style.transition = 'width ' + seconds + 's linear';
                timerFill.style.width = '0%';
            });
        });
        var expiry = setTimeout(close, seconds * 1000);

        function close() {
            clearTimeout(expiry);
            if (panel.parentNode) { panel.parentNode.removeChild(panel); }
        }

        Array.prototype.forEach.call(panel.querySelectorAll('[data-answer]'), function (btn) {
            btn.addEventListener('click', function () {
                var accept = btn.getAttribute('data-answer') === 'yes';
                close();
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
            });
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
        host.classList.add('wt-host');
        host.style.setProperty('--wt-pct', String(pick(response, 'AvatarSizePercent') || 16));

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
                if (!entry.card.isConnected) { return; }
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

        schedule();
        setTimeout(liveTick, 15000);
        setTimeout(pollIncoming, 3000);
    }

    start();
})();
