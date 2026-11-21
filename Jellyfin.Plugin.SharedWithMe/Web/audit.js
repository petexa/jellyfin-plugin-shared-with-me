// Shared With Me: audit page (administrators). Loaded by audit.html through data-controller="__plugin/...".
// Every piece of server data is inserted with textContent; titles and names are untrusted.
// Every action button is disabled while its request is in flight.
const PLUGIN_ID = '4069397e-cf0d-460a-9a00-e5175c137516';

// One call to the plugin API through the web client's authenticated ApiClient. Never throws for an HTTP error:
// returns { ok, status, data } so refusals can be shown verbatim.
async function api(method, path, body, query) {
    const request = { type: method, url: ApiClient.getUrl(path, query) };
    if (body !== undefined) {
        request.data = JSON.stringify(body);
        request.contentType = 'application/json';
    }

    let response;
    try {
        response = await ApiClient.fetch(request);
    } catch (err) {
        response = err; // ApiClient rejects with the Response on a non-2xx status
    }

    if (!response || typeof response.status !== 'number') {
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

function say(node, text) {
    node.textContent = text;
}

function titleWithYear(title, year) {
    return year ? title + ' (' + year + ')' : title;
}

function day(utc) {
    const d = new Date(utc);
    return isNaN(d.getTime()) ? '' : d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
}

function stateText(share) {
    return share.StateReason ? share.State + ': ' + share.StateReason : share.State;
}

function visibleText(share) {
    if (share.Pending) {
        return 'pending (not scanned yet)';
    }

    if (share.NotFound) {
        return 'not found';
    }

    return share.VisibleNow ? 'yes' : 'no';
}

export default function (view) {
    const $ = (id) => view.querySelector('#' + id);
    let audit = null;
    let picked = null;

    function renderProblem(problem) {
        $('swm-problem').classList.toggle('hide', !problem);
        say($('swm-problem-text'), problem || '');
    }

    function renderProfiles(profiles) {
        const host = $('swm-profiles');
        host.replaceChildren();
        for (const profile of profiles) {
            const block = el('div');
            block.className = 'verticalSection';
            block.appendChild(el('h3', profile.User.Name + ', age limit: ' + profile.CapLabel));
            if (!profile.Shares.length) {
                block.appendChild(el('p', 'Nothing shared.'));
                host.appendChild(block);
                continue;
            }

            const table = el('table');
            const head = el('tr');
            for (const h of ['Title', 'True rating', 'Shared by', 'Date', 'State', 'Visible now', '']) {
                const th = el('th', h);
                th.scope = 'col';
                head.appendChild(th);
            }

            table.appendChild(el('thead')).appendChild(head);
            const body = table.appendChild(el('tbody'));
            for (const share of profile.Shares) {
                const row = el('tr');
                row.appendChild(el('td', titleWithYear(share.Title, share.Year)));
                row.appendChild(el('td', share.OfficialRating || 'unknown'));
                row.appendChild(el('td', share.SharerExists ? share.SharerName : share.SharerName + ' (deleted)'));
                row.appendChild(el('td', day(share.CreatedUtc)));
                row.appendChild(el('td', stateText(share)));
                row.appendChild(el('td', visibleText(share)));
                const cell = el('td');
                const remove = el('button', 'Remove');
                remove.type = 'button';
                remove.setAttribute('is', 'emby-button');
                remove.className = 'raised';
                remove.addEventListener('click', () => removeShare(share, profile.User.Name, remove));
                cell.appendChild(remove);
                row.appendChild(cell);
                body.appendChild(row);
            }

            block.appendChild(table);
            host.appendChild(block);
        }
    }

    function renderSwitched(report) {
        const list = $('swm-switched');
        list.replaceChildren();
        const narrowed = new Set((report.NarrowedUsers || []).map((u) => u.Id));
        for (const user of report.SwitchedUsers) {
            list.appendChild(el('li', narrowed.has(user.Id)
                ? user.Name + ' (their library list was changed after the plugin moved them, so Restore will leave them on it)'
                : user.Name));
        }

        const active = report.Profiles.reduce((n, p) => n + p.Shares.filter((s) => s.State === 'Active').length, 0);
        const button = $('swm-restore');
        if (!report.SwitchedUsers.length && !report.InCharge) {
            list.appendChild(el('li', 'None. The plugin has not changed any profile.'));
            button.disabled = true;
            say($('swm-restore-why'), 'There is nothing to restore.');
        } else if (active > 0) {
            button.disabled = true;
            say($('swm-restore-why'), 'Restore is not available while ' + active + (active === 1 ? ' share is' : ' shares are') + ' active. Remove every share first, then restore.');
        } else {
            button.disabled = false;
            say($('swm-restore-why'), '');
        }

        $('swm-root').disabled = report.ShareRootLocked;
        $('swm-root-locked').classList.toggle('hide', !report.ShareRootLocked);
    }

    function renderDiagnostics(d) {
        const last = d.LastSync ? new Date(d.LastSync).toLocaleString() : 'not yet';
        say($('swm-diagnostics'), 'Since the server started: ' + d.SyncRuns + ' full checks, ' + d.PolicyWrites + ' profile changes and ' + d.PlaylistWrites + ' playlist changes by the plugin. Last check: ' + last + '.');
    }

    async function load() {
        say($('swm-status'), 'Loading...');
        const res = await api('GET', 'SharedWithMe/Audit');
        if (!res.ok) {
            say($('swm-status'), 'Could not load the audit. ' + failure(res));
            return;
        }

        audit = res.data;
        renderProblem(audit.SetupProblem);
        renderProfiles(audit.Profiles);
        renderSwitched(audit);
        renderDiagnostics(audit.Diagnostics);
        say($('swm-status'), '');
    }

    async function loadNotes() {
        const table = $('swm-notes');
        const body = table.querySelector('tbody');
        body.replaceChildren();
        const res = await api('GET', 'SharedWithMe/Notes', undefined, { all: 'true' });
        if (!res.ok) {
            say($('swm-notes-empty'), 'Could not load the list. ' + failure(res));
            return;
        }

        const notes = res.data.Notes;
        $('swm-notes-empty').classList.toggle('hide', notes.length > 0);
        table.classList.toggle('hide', notes.length === 0);
        for (const n of notes) {
            const row = el('tr');
            row.appendChild(el('td', n.SharerName || ''));
            row.appendChild(el('td', n.Title || '(not named)'));
            row.appendChild(el('td', n.RecipientName));
            row.appendChild(el('td', n.Message));
            row.appendChild(el('td', day(n.AtUtc)));
            body.appendChild(row);
        }
    }

    async function loadRecipients() {
        const select = $('swm-add-profile');
        select.replaceChildren();
        const res = await api('GET', 'SharedWithMe/Recipients');
        if (!res.ok) {
            say($('swm-add-status'), 'Could not load the profiles. ' + failure(res));
            return;
        }

        if (!res.data.length) {
            say($('swm-add-status'), 'There is no profile you can share with.');
        }

        for (const user of res.data) {
            const option = el('option', user.Name);
            option.value = user.Id;
            select.appendChild(option);
        }
    }

    async function loadSettings() {
        try {
            const config = await ApiClient.getPluginConfiguration(PLUGIN_ID);
            $('swm-who').value = config.WhoMayShare || 'Everyone';
            $('swm-root').value = config.ShareRoot || '';
            $('swm-tv').checked = config.TvSharingPlaylists !== false;
        } catch (e) {
            say($('swm-settings-status'), 'Could not load the settings.');
        }
    }

    async function removeShare(share, profileName, button) {
        const title = titleWithYear(share.Title, share.Year);
        if (!window.confirm('Remove "' + title + '" from ' + profileName + '? The shared copy is deleted; the original is not touched.')) {
            return;
        }

        button.disabled = true;
        const res = await api('DELETE', 'SharedWithMe/Shares/' + share.Id);
        button.disabled = false;
        await load();
        say($('swm-status'), res.ok ? 'Removed "' + title + '" from ' + profileName + '.' : 'Could not remove "' + title + '". ' + failure(res));
    }

    async function search(e) {
        e.preventDefault();
        const term = $('swm-add-term').value.trim();
        const table = $('swm-add-results');
        const body = table.querySelector('tbody');
        body.replaceChildren();
        picked = null;
        table.classList.add('hide');
        say($('swm-add-status'), '');
        if (term.length < 2) {
            say($('swm-add-results-note'), 'Type at least 2 letters.');
            return;
        }

        const res = await api('GET', 'SharedWithMe/Search', undefined, { term: term });
        if (!res.ok) {
            say($('swm-add-results-note'), 'Search failed. ' + failure(res));
            return;
        }

        say($('swm-add-results-note'), res.data.length ? res.data.length + ' found. Pick one, choose a profile, then Share.' : 'Nothing found that you can see.');
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
        table.classList.toggle('hide', !res.data.length);
    }

    async function share() {
        const status = $('swm-add-status');
        const select = $('swm-add-profile');
        if (!picked) {
            say(status, 'Pick a title first.');
            return;
        }

        if (!select.value) {
            say(status, 'Choose a profile first.');
            return;
        }

        const who = select.options[select.selectedIndex].textContent;
        const title = titleWithYear(picked.Title, picked.Year);
        say(status, 'Sharing...');
        const button = $('swm-add-share');
        button.disabled = true;
        const res = await api('POST', 'SharedWithMe/Shares', { ItemId: picked.Id, RecipientUserId: select.value });
        button.disabled = false;
        if (res.ok) {
            await load();
        }

        say(status, res.ok ? 'Shared "' + title + '" with ' + who + '.' : failure(res));
    }

    async function checkNow() {
        const status = $('swm-check-status');
        const button = $('swm-check');
        button.disabled = true;
        say(status, 'Checking every share...');
        const res = await api('POST', 'SharedWithMe/Audit/Sync');
        button.disabled = false;
        if (!res.ok) {
            say(status, 'The check did not finish. ' + failure(res));
            return;
        }

        const r = res.data;
        const text = r.SetupProblem
            ? 'Only profile access was checked: ' + r.SetupProblem
            : 'Checked ' + r.Checked + (r.Checked === 1 ? ' share' : ' shares') + ': ' + r.Relinked + ' followed a changed file, ' + r.Closed + ' closed, '
                + r.Broken + ' broken, ' + r.Reapplied + ' re-applied, ' + r.Removed + ' removed.';
        await load();
        say(status, text);
    }

    async function saveSettings(e) {
        e.preventDefault();
        const status = $('swm-settings-status');
        const button = view.querySelector('#swm-settings-form button[type="submit"]');
        button.disabled = true;
        try {
            const config = await ApiClient.getPluginConfiguration(PLUGIN_ID);
            config.WhoMayShare = $('swm-who').value;
            config.TvSharingPlaylists = $('swm-tv').checked;
            if (!$('swm-root').disabled) {
                config.ShareRoot = $('swm-root').value.trim();
            }

            await ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
            await load();
            await loadSettings();
            await loadNotes();
            say(status, 'Settings saved. Sharing from TV apps: ' + ($('swm-tv').checked ? 'on' : 'off') + '.' + (audit && audit.SetupProblem ? ' There is still a setup problem, shown at the top of the page.' : ''));
        } catch (err) {
            say(status, 'Settings were not saved. ' + (err && err.status ? 'The server answered ' + err.status + '.' : ''));
        } finally {
            button.disabled = false;
        }
    }

    async function restore() {
        const status = $('swm-restore-status');
        if (!window.confirm('Put every profile listed back on "all libraries", remove the share libraries and the "Share with" playlists, and turn sharing from TV apps off?')) {
            return;
        }

        say(status, 'Restoring...');
        const button = $('swm-restore');
        button.disabled = true;
        const res = await api('POST', 'SharedWithMe/Audit/Restore');
        await load(); // sets the button's state for what is left
        await loadSettings(); // a restore turns sharing from TV apps off
        if (!res.ok) {
            say(status, failure(res));
            return;
        }

        const names = res.data.RestoredUsers.map((u) => u.Name);
        const kept = (res.data.LeftOnOwnList || []).map((u) => u.Name);
        say(status, (names.length
            ? 'Restored ' + names.length + (names.length === 1 ? ' profile' : ' profiles') + ' to all libraries: ' + names.join(', ') + '.'
            : 'Done. No profile needed to be put back on all libraries.')
            + (kept.length ? ' Left on their own library list because it was changed after the plugin moved them: ' + kept.join(', ') + '.' : ''));
    }

    // The address everyone else uses (Jellyfin lists plugin pages to administrators only), built the same way as the
    // plugin's own calls so a server behind a reverse proxy or a base path gets the right one.
    function showShareLink() {
        $('swm-link').value = ApiClient.getUrl('SharedWithMe/Page');
    }

    async function copyShareLink() {
        const status = $('swm-link-status');
        const input = $('swm-link');
        let copied = false;
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(input.value);
                copied = true;
            }
        } catch (e) {
            copied = false;
        }

        if (!copied) {
            // A plain-http address (the usual home server) is not a secure context, so the clipboard API is refused.
            input.focus();
            input.select();
            try {
                copied = document.execCommand('copy');
            } catch (e) {
                copied = false;
            }
        }

        status.textContent = copied ? 'Copied.' : 'Press Ctrl+C (or hold and copy) to copy the selected address.';
    }

    $('swm-link-copy').addEventListener('click', copyShareLink);
    $('swm-add-form').addEventListener('submit', search);
    $('swm-add-share').addEventListener('click', share);
    $('swm-check').addEventListener('click', checkNow);
    $('swm-settings-form').addEventListener('submit', saveSettings);
    $('swm-restore').addEventListener('click', restore);

    view.addEventListener('viewshow', () => {
        showShareLink();
        load();
        loadRecipients();
        loadSettings();
        loadNotes();
    });
}
