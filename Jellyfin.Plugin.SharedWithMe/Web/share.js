// Shared With Me: the share page for every signed-in user. Jellyfin 12.4 lists plugin pages to administrators only,
// so this page is served by the plugin at /SharedWithMe/Page and signs its calls with the session the Jellyfin web
// client stored in this browser (localStorage "jellyfin_credentials"). Every piece of server data is inserted with
// textContent; titles and names are untrusted.
(function () {
    'use strict';

    // The server's base URL from this page's own path: any leading run of slashes collapsed to one, then only plain
    // path segments accepted. Anything else (for example "//other.host/...") is refused before any request is made.
    const rawBase = window.location.pathname.replace(/\/SharedWithMe\/Page\/?$/i, '').replace(/^\/+/, '/');
    const basePath = rawBase === '/' ? '' : rawBase;
    const baseOk = /^(\/[A-Za-z0-9._~-]+)*$/.test(basePath);
    const $ = (id) => document.getElementById(id);
    let token = null;
    let myId = null;
    let picked = null;
    let names = {};
    let tvSharing = false;
    let tvRecipients = [];

    function storedSessions() {
        let stored;
        try {
            stored = JSON.parse(window.localStorage.getItem('jellyfin_credentials') || '{}');
        } catch (e) {
            stored = {};
        }

        return (stored.Servers || [])
            .filter((s) => s && s.AccessToken && s.UserId)
            .sort((a, b) => (b.DateLastAccessed || 0) - (a.DateLastAccessed || 0));
    }

    // Every address is built on this page's own origin, and checked to still be on it before a token is attached.
    function sameOrigin(path, query) {
        const url = new URL(window.location.origin + basePath + path);
        if (query) {
            url.search = new URLSearchParams(query).toString();
        }

        return url.origin === window.location.origin ? url : null;
    }

    function unexpectedAddress() {
        $('swm-content').hidden = true;
        $('swm-signin').hidden = true;
        $('swm-who').textContent = 'This page was opened from an unexpected address.';
    }

    // Token only: the server finds the session (and its device) from it, so the web client's session is not changed.
    async function api(method, path, body, query) {
        const url = sameOrigin(path, query);
        if (!url || !token) {
            return { ok: false, status: 0, data: null };
        }

        const headers = { 'Authorization': 'MediaBrowser Token="' + token + '"' };
        const init = { method: method, headers: headers, credentials: 'omit' };
        if (body !== undefined) {
            headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(body);
        }

        let response;
        try {
            response = await window.fetch(url, init);
        } catch (e) {
            return { ok: false, status: 0, data: null };
        }

        let data = null;
        try {
            const text = await response.text();
            data = text ? JSON.parse(text) : null;
        } catch (e) {
            data = null;
        }

        return { ok: response.ok, status: response.status, data: data };
    }

    function failure(res) {
        if (res.data && typeof res.data.Message === 'string' && res.data.Message) {
            return res.data.Message;
        }

        return res.status ? 'The server answered ' + res.status + '.' : 'The server did not answer.';
    }

    function el(tag, text) {
        const node = document.createElement(tag);
        if (text !== undefined && text !== null) {
            node.textContent = String(text);
        }

        return node;
    }

    function titleWithYear(title, year) {
        return year ? title + ' (' + year + ')' : title;
    }

    function day(utc) {
        const d = new Date(utc);
        return isNaN(d.getTime()) ? '' : d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
    }

    // When a share ends, as the person reads it: a date and a time, or "until removed".
    function until(utc) {
        if (!utc) {
            return 'until removed';
        }

        const d = new Date(utc);
        return isNaN(d.getTime()) ? '' : d.toLocaleString(undefined, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
    }

    // The check-shares run ends an expired share within seconds; until it has, the page already says "expired".
    function expired(s) {
        return (s.State === 'Closed' && s.StateReason === 'expired')
            || (s.State !== 'Closed' && !!s.ExpiresUtc && new Date(s.ExpiresUtc).getTime() <= Date.now());
    }

    function localDay(date) {
        const pad = (n) => String(n).padStart(2, '0');
        return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate());
    }

    // The "Keep it" choice as the end time to send (UTC), null for "until I remove it". A date means the end of that day
    // in this browser's time zone. The server refuses a time that has passed or is more than 366 days ahead.
    function keepUntil() {
        const keep = $('swm-keep').value;
        if (keep === 'day' || keep === 'week') {
            return { ok: true, value: new Date(Date.now() + (keep === 'day' ? 1 : 7) * 86400000).toISOString() };
        }

        if (keep !== 'date') {
            return { ok: true, value: null };
        }

        const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec($('swm-until').value);
        if (!m) {
            return { ok: false, message: 'Choose the last day of the share first.' };
        }

        const end = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]) + 1);
        if (end.getTime() <= Date.now()) {
            return { ok: false, message: 'That day has already passed. Choose today or a later day.' };
        }

        return { ok: true, value: end.toISOString() };
    }

    function renderKeep() {
        $('swm-until-area').hidden = $('swm-keep').value !== 'date';
    }

    function signedOut(message) {
        $('swm-content').hidden = true;
        $('swm-signin').hidden = false;
        $('swm-who').textContent = message;
    }

    async function loadMine() {
        const res = await api('GET', '/SharedWithMe/Shares', undefined, { withSummary: 'true' });
        const table = $('swm-mine');
        const body = table.querySelector('tbody');
        body.replaceChildren();
        if (!res.ok) {
            $('swm-mine-status').textContent = 'Could not load what you shared. ' + failure(res);
            return;
        }

        $('swm-mine-hidden').hidden = !(res.data.HiddenCount > 0);
        // Only the caller's own shares (an administrator's list holds everyone's); a closed one only when it expired.
        const shares = res.data.Shares.filter((s) => s.SharerUserId === myId && (s.State !== 'Closed' || expired(s)));
        $('swm-mine-empty').hidden = shares.length > 0;
        table.hidden = shares.length === 0;
        for (const s of shares) {
            const row = el('tr');
            const title = titleWithYear(s.Title, s.Year);
            const who = names[s.RecipientUserId] || 'a profile you can no longer pick';
            row.appendChild(el('td', title));
            row.appendChild(el('td', who));
            row.appendChild(el('td', day(s.CreatedUtc)));
            row.appendChild(el('td', until(s.ExpiresUtc)));
            row.appendChild(el('td', expired(s) ? 'expired' : s.State === 'Active' ? 'shared' : s.State + (s.StateReason ? ': ' + s.StateReason : '')));
            const cell = el('td');
            if (s.State !== 'Closed') {
                const undo = el('button', 'Undo');
                undo.type = 'button';
                undo.addEventListener('click', () => undoShare(s, title, who, undo));
                cell.appendChild(undo);
            }

            row.appendChild(cell);
            body.appendChild(row);
        }
    }

    async function loadReceived() {
        const res = await api('GET', '/SharedWithMe/Received');
        const table = $('swm-received');
        const body = table.querySelector('tbody');
        body.replaceChildren();
        if (!res.ok) {
            $('swm-received-empty').textContent = 'Could not load what was shared with you. ' + failure(res);
            return;
        }

        $('swm-received-empty').hidden = res.data.length > 0;
        table.hidden = res.data.length === 0;
        for (const s of res.data) {
            const row = el('tr');
            row.appendChild(el('td', titleWithYear(s.Title, s.Year)));
            row.appendChild(el('td', s.SharerName));
            row.appendChild(el('td', day(s.CreatedUtc)));
            row.appendChild(el('td', until(s.ExpiresUtc)));
            body.appendChild(row);
        }
    }

    // The sentence about the TV playlists names the profile picked in "Share with", only when the caller has a playlist
    // for that profile (profiles with no limits get none); hidden when the setting is off.
    function renderTv() {
        const select = $('swm-recipient');
        const line = $('swm-tv');
        const picked = select.selectedIndex >= 0 ? select.options[select.selectedIndex] : null;
        const who = picked ? picked.textContent : '';
        line.hidden = !tvSharing || !picked || !tvRecipients.includes(picked.value);
        line.textContent = 'On a TV app, add a film or any episode of a series to your playlist "Share with ' + who + '" to share it. Remove it there to stop sharing.';
    }

    async function loadNotes() {
        const res = await api('GET', '/SharedWithMe/Notes');
        const table = $('swm-notes');
        const body = table.querySelector('tbody');
        body.replaceChildren();
        if (!res.ok) {
            $('swm-notes-empty').textContent = 'Could not load what was not shared. ' + failure(res);
            return;
        }

        tvSharing = !!res.data.TvSharing;
        tvRecipients = Array.isArray(res.data.TvRecipients) ? res.data.TvRecipients : [];
        renderTv();
        const notes = res.data.Notes;
        $('swm-notes-empty').hidden = notes.length > 0;
        table.hidden = notes.length === 0;
        $('swm-notes-clear').hidden = notes.length === 0;
        for (const n of notes) {
            const row = el('tr');
            row.appendChild(el('td', n.Title || '(a title you cannot see)'));
            row.appendChild(el('td', n.RecipientName));
            row.appendChild(el('td', n.Message));
            row.appendChild(el('td', day(n.AtUtc)));
            body.appendChild(row);
        }
    }

    async function clearNotes() {
        const button = $('swm-notes-clear');
        button.disabled = true;
        const res = await api('DELETE', '/SharedWithMe/Notes');
        button.disabled = false;
        await loadNotes();
        $('swm-notes-status').textContent = res.ok ? 'Cleared.' : 'Could not clear the list. ' + failure(res);
    }

    async function undoShare(share, title, who, button) {
        if (!window.confirm('Stop sharing "' + title + '" with ' + who + '?')) {
            return;
        }

        button.disabled = true;
        const res = await api('DELETE', '/SharedWithMe/Shares/' + share.Id);
        button.disabled = false;
        await loadMine();
        $('swm-mine-status').textContent = res.ok
            ? 'Stopped sharing "' + title + '" with ' + who + '.'
            : 'Could not undo "' + title + '". ' + failure(res);
    }

    async function search(e) {
        e.preventDefault();
        const term = $('swm-term').value.trim();
        const table = $('swm-results');
        const body = table.querySelector('tbody');
        body.replaceChildren();
        table.hidden = true;
        picked = null;
        $('swm-share-status').textContent = '';
        if (term.length < 2) {
            $('swm-results-note').textContent = 'Type at least 2 letters.';
            return;
        }

        const res = await api('GET', '/SharedWithMe/Search', undefined, { term: term });
        if (!res.ok) {
            $('swm-results-note').textContent = 'Search failed. ' + failure(res);
            return;
        }

        $('swm-results-note').textContent = res.data.length
            ? res.data.length + ' found. Pick one, choose who to share it with, then Share.'
            : 'Nothing found that you can see.';
        res.data.forEach((hit, i) => {
            const row = el('tr');
            const cell = el('td');
            const radio = el('input');
            radio.type = 'radio';
            radio.name = 'swm-pick';
            radio.id = 'swm-pick-' + i;
            radio.setAttribute('aria-label', 'Pick ' + titleWithYear(hit.Title, hit.Year));
            radio.addEventListener('change', () => { picked = hit; });
            const label = el('label', 'Pick');
            label.htmlFor = radio.id;
            cell.appendChild(radio);
            cell.appendChild(label);
            row.appendChild(cell);
            row.appendChild(el('td', titleWithYear(hit.Title, hit.Year)));
            row.appendChild(el('td', hit.Kind === 'Series' ? 'Series' : 'Movie'));
            row.appendChild(el('td', hit.OfficialRating || 'unrated'));
            body.appendChild(row);
        });
        table.hidden = res.data.length === 0;
    }

    async function share() {
        const status = $('swm-share-status');
        const select = $('swm-recipient');
        if (!picked) {
            status.textContent = 'Pick a title first.';
            return;
        }

        if (!select.value) {
            status.textContent = 'Choose who to share it with first.';
            return;
        }

        const keep = keepUntil();
        if (!keep.ok) {
            status.textContent = keep.message;
            return;
        }

        const who = select.options[select.selectedIndex].textContent;
        const title = titleWithYear(picked.Title, picked.Year);
        status.textContent = 'Sharing...';
        const button = $('swm-share');
        button.disabled = true;
        const res = await api('POST', '/SharedWithMe/Shares', { ItemId: picked.Id, RecipientUserId: select.value, ExpiresUtc: keep.value });
        button.disabled = false;
        if (res.ok) {
            await loadMine();
        }

        status.textContent = res.ok
            ? 'Shared "' + title + '" with ' + who + (res.data && res.data.ExpiresUtc ? ' until ' + until(res.data.ExpiresUtc) : '') + '.'
            : failure(res);
    }

    async function start() {
        if (!baseOk || !sameOrigin('/System/Info/Public')) {
            unexpectedAddress();
            return;
        }

        // Only the session stored for THIS server is ever used; a session of another server is never sent here.
        let serverId = null;
        try {
            const info = await (await window.fetch(sameOrigin('/System/Info/Public'), { credentials: 'omit' })).json();
            serverId = info && info.Id;
        } catch (e) {
            serverId = null;
        }

        const session = serverId ? storedSessions().find((s) => s.Id === serverId) : null;
        if (!session) {
            signedOut('You are not signed in to this Jellyfin server in this browser.');
            return;
        }

        token = session.AccessToken;
        const me = await api('GET', '/Users/Me');
        if (!me.ok) {
            signedOut(me.status === 401 ? 'Your Jellyfin sign-in has expired.' : 'Could not check who you are. ' + failure(me));
            return;
        }

        myId = me.data.Id;
        $('swm-who').textContent = 'Signed in as ' + me.data.Name + '.';
        $('swm-content').hidden = false;

        const recipients = await api('GET', '/SharedWithMe/Recipients');
        const list = recipients.ok ? recipients.data : [];
        names = {};
        const select = $('swm-recipient');
        select.replaceChildren();
        for (const user of list) {
            names[user.Id] = user.Name;
            const option = el('option', user.Name);
            option.value = user.Id;
            select.appendChild(option);
        }

        $('swm-share-form-area').hidden = list.length === 0;
        $('swm-cannot-share').hidden = list.length > 0;
        if (!recipients.ok) {
            $('swm-cannot-share').textContent = 'Could not load who you can share with. ' + failure(recipients);
        }

        await Promise.all([loadMine(), loadReceived(), loadNotes()]);
    }

    $('swm-search-form').addEventListener('submit', search);
    $('swm-recipient').addEventListener('change', renderTv);
    $('swm-keep').addEventListener('change', renderKeep);
    $('swm-until').min = localDay(new Date());
    $('swm-until').max = localDay(new Date(Date.now() + 365 * 86400000));
    $('swm-notes-clear').addEventListener('click', clearNotes);
    $('swm-share').addEventListener('click', share);
    start();
}());
