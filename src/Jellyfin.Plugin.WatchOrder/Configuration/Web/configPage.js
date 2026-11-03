const pluginId = '760c9732-e114-481f-b6c9-5a2a169111c1';

// Only per-item ids are kept. Collection ids (e.g. TmdbCollection) are shared by every film
// in a collection, so matching on them would pull in the wrong film.
const KEPT_PROVIDERS = ['Tmdb', 'Imdb', 'Tvdb'];
const TYPE_NAMES = { movie: 'Film', season: 'Season', episodes: 'Episodes', episode: 'Episode' };
const ID_PATTERN = /^[a-z0-9][a-z0-9-]{0,63}$/;

export default function (view) {
    const $ = (sel) => view.querySelector(sel);
    const settingsFields = ['ShowInWebApp', 'ButtonLocation', 'LibraryBarPosition', 'ShowHomeRow', 'AutoplayFollow', 'PlaylistSync', 'AutoRefreshOnLibraryChange', 'RefreshDebounceSeconds', 'AllowTitleMatching'];

    // ------------------------------------------------------------------ state

    let draft = null;          // { id, name, description, author, entries: [{ _uid, type, ... }] }
    let editingId = null;      // id of the saved order being edited; null for a new order
    let idTouched = false;     // stop deriving the id from the name once the user edits it
    let dirty = false;
    let knownIds = [];
    let statusByUid = new Map(); // _uid -> { total, matched, byTitle }
    let previewTimer = 0;
    let previewSeq = 0;
    let uidSeq = 0;
    let dragFrom = -1;
    let collectionsLoaded = false;

    // ---------------------------------------------------------------- helpers

    const nextUid = () => ++uidSeq;
    const pad = (n) => String(n ?? '?').padStart(2, '0');
    const num = (v) => (v === '' || v === null || v === undefined || Number.isNaN(Number(v)) ? undefined : Number(v));
    // Jellyfin's API returns PascalCase; order files use camelCase. Read either.
    const pick = (o, k) => o?.[k] ?? o?.[k[0].toUpperCase() + k.slice(1)];

    // Escapes for both element text and attribute values (titles can contain quotes).
    const ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', '\'': '&#39;' };
    const esc = (text) => String(text ?? '').replace(/[&<>"']/g, (c) => ESCAPES[c]);

    const apiUrl = (path, params) => ApiClient.getUrl(path, params);
    const getJson = (path, params) => ApiClient.getJSON(apiUrl(path, params));

    function sendJson(type, path, body) {
        return sendJsonTo(type, path, body);
    }

    function sendJsonTo(type, path, body, params) {
        return ApiClient.ajax({
            type,
            url: apiUrl(path, params),
            data: JSON.stringify(body),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    async function errorMessages(response) {
        try {
            const payload = await response.json();
            return payload.Errors || payload.errors || [payload.title || 'The request failed.'];
        } catch {
            return ['The request failed.'];
        }
    }

    function keptIds(ids) {
        const out = {};
        for (const [key, value] of Object.entries(ids || {})) {
            const canonical = KEPT_PROVIDERS.find((p) => p.toLowerCase() === key.toLowerCase());
            if (canonical && value) {
                out[canonical] = String(value);
            }
        }
        return out;
    }

    function slugify(text) {
        return (text || '')
            .toLowerCase()
            .normalize('NFKD').replace(/[̀-ͯ]/g, '')
            .replace(/&/g, ' and ')
            .replace(/[^a-z0-9]+/g, '-')
            .replace(/^-+|-+$/g, '')
            .slice(0, 64)
            .replace(/-+$/, '');
    }

    function cleanEntry(entry) {
        const out = { ...(entry._extra || {}) };
        for (const [key, value] of Object.entries(entry)) {
            if (key.startsWith('_') || value === null || value === undefined || value === '' || (key === 'optional' && value !== true) || (key === 'recommended' && (value !== true || entry.optional !== true))) {
                continue;
            }
            if (key === 'providerIds' && Object.keys(value).length === 0) {
                continue;
            }
            if (key === 'section') {
                // An empty heading is no section.
                if (String(value).trim()) {
                    out.section = String(value).trim();
                }
                continue;
            }
            out[key] = value;
        }
        return out;
    }

    // Fields the builder edits. Anything else in an order (from a newer plugin, or another tool) is kept as it is.
    const KNOWN_ORDER_FIELDS = ['schemaversion', 'id', 'name', 'description', 'author', 'version', 'source', 'image', 'entries'];
    const KNOWN_ENTRY_FIELDS = ['type', 'title', 'year', 'series', 'season', 'episode', 'from', 'to', 'providerids', 'note', 'optional', 'recommended', 'section'];
    const KNOWN_IMAGE_FIELDS = ['type', 'title', 'year', 'providerids', 'url'];

    function otherFields(obj, known) {
        const out = {};
        for (const [key, value] of Object.entries(obj || {})) {
            if (!known.includes(key.toLowerCase()) && value !== null && value !== undefined) {
                out[key] = value;
            }
        }
        return out;
    }

    function toDraft(def) {
        return {
            id: pick(def, 'id') || '',
            name: pick(def, 'name') || '',
            description: pick(def, 'description') || '',
            author: pick(def, 'author') || '',
            version: pick(def, 'version') ?? '',
            source: pick(def, 'source') || '',
            image: toDraftImage(pick(def, 'image')),
            _extra: otherFields(def, KNOWN_ORDER_FIELDS),
            entries: (pick(def, 'entries') || []).map((e) => ({
                _uid: nextUid(),
                _extra: otherFields(e, KNOWN_ENTRY_FIELDS),
                ...cleanEntry({
                    type: String(pick(e, 'type') || 'movie').toLowerCase(),
                    title: pick(e, 'title'),
                    year: pick(e, 'year'),
                    series: pick(e, 'series'),
                    season: pick(e, 'season'),
                    episode: pick(e, 'episode'),
                    from: pick(e, 'from'),
                    to: pick(e, 'to'),
                    providerIds: { ...(pick(e, 'providerIds') || {}) },
                    note: pick(e, 'note'),
                    optional: pick(e, 'optional') === true,
                    recommended: pick(e, 'optional') === true && pick(e, 'recommended') === true,
                    section: pick(e, 'section')
                })
            }))
        };
    }

    function toPayload(d) {
        return {
            ...(d._extra || {}),
            schemaVersion: 1,
            id: d.id,
            name: d.name.trim(),
            description: d.description.trim() || undefined,
            author: d.author.trim() || undefined,
            version: num(d.version),
            source: String(d.source || '').trim() || undefined,
            image: toPayloadImage(d.image),
            entries: d.entries.map(cleanEntry)
        };
    }

    // The order's image: a film or show (type with title, year, providerIds) and/or a link (url).
    function toDraftImage(image) {
        if (!image) {
            return { _extra: {} };
        }
        const type = String(pick(image, 'type') || '').toLowerCase() || undefined;
        return {
            _extra: otherFields(image, KNOWN_IMAGE_FIELDS),
            type,
            title: pick(image, 'title') || undefined,
            year: pick(image, 'year') ?? undefined,
            providerIds: { ...(pick(image, 'providerIds') || {}) },
            url: pick(image, 'url') || ''
        };
    }

    function toPayloadImage(image) {
        if (!image) {
            return undefined;
        }
        const out = { ...(image._extra || {}) };
        if (image.type && (image.title || Object.keys(image.providerIds || {}).length)) {
            out.type = image.type;
            if (image.title) {
                out.title = image.title;
            }
            if (image.year !== undefined && image.year !== null && image.year !== '') {
                out.year = image.year;
            }
            if (Object.keys(image.providerIds || {}).length) {
                out.providerIds = image.providerIds;
            }
        }
        const link = String(image.url || '').trim();
        if (link) {
            out.url = link;
        }
        return Object.keys(out).length ? out : undefined;
    }

    const imageKey = (c) => `${c.type}|${Object.entries(c.providerIds || {}).sort().map(([k, v]) => `${k}=${v}`).join(',') || `${(c.title || '').toLowerCase()}|${c.year ?? ''}`}`;

    /** The films and shows in the order, for "A film or show from this order". */
    function imageChoices() {
        const choices = [];
        const seen = new Set();
        const add = (c) => {
            const key = imageKey(c);
            if (!seen.has(key)) {
                seen.add(key);
                choices.push({ ...c, key });
            }
        };
        if (draft.image?.type) {
            add({ type: draft.image.type, title: draft.image.title, year: draft.image.year, providerIds: draft.image.providerIds });
        }
        for (const e of draft.entries) {
            if (e.type === 'movie') {
                add({ type: 'movie', title: e.title, year: e.year, providerIds: e.providerIds || {} });
            } else if (e.series || Object.keys(e.providerIds || {}).length) {
                add({ type: 'series', title: e.series, year: e.year, providerIds: e.providerIds || {} });
            }
        }
        return choices;
    }

    function renderImageChoices() {
        const select = $('#woImageItem');
        const choices = imageChoices();
        const current = draft.image?.type ? imageKey(draft.image) : '';
        select.innerHTML = '<option value="">None</option>' + choices.map((c) => `<option value="${esc(c.key)}"${c.key === current ? ' selected' : ''}>${esc(`${c.title || 'Untitled'}${c.year ? ` (${c.year})` : ''} · ${c.type === 'movie' ? 'film' : 'show'}`)}</option>`).join('');
        select._choices = choices;
    }

    function onImageItemChange() {
        const select = $('#woImageItem');
        const choice = (select._choices || []).find((c) => c.key === select.value);
        draft.image = draft.image || { _extra: {} };
        draft.image.type = choice?.type;
        draft.image.title = choice?.title;
        draft.image.year = choice?.year;
        draft.image.providerIds = choice ? { ...(choice.providerIds || {}) } : {};
        markChanged(false);
    }

    /** For a saved order: whether it has an upload, and how the link's download went. */
    async function refreshImageStatus() {
        const box = $('#woImageUpload');
        box.hidden = !editingId;
        $('#woImageUploadNote').hidden = !!editingId;
        if (!editingId) {
            return;
        }
        let image = null;
        try {
            image = (await getJson('WatchOrder/Orders')).find((o) => o.Id === editingId)?.Image || null;
        } catch {
            // Not essential.
        }
        showImageStatus(image);
    }

    function showImageStatus(image) {
        const status = $('#woImageStatus');
        const parts = [];
        if (image?.Uploaded) {
            parts.push('Using a picture uploaded here, in place of the image above.');
        } else if (image?.HasItem && image.ItemFound) {
            parts.push('Using the backdrop of the film or show above.');
        } else if (image?.Downloaded) {
            parts.push(`Using the picture from the link${image.HasItem ? ' (the film or show above isn\'t in the library)' : ''}.`);
        } else if (image?.HasItem && !image.Url) {
            parts.push('The film or show above isn\'t in the library, so the first item\'s backdrop is used.');
        }
        if (image?.Error) {
            parts.push(`The picture couldn't be downloaded: ${image.Error}`);
        }
        status.textContent = parts.join(' ');
        status.classList.toggle('wo-bad', !!image?.Error);
        $('#woImageRemove').hidden = !image?.Uploaded;
        $('#woImageRefresh').hidden = !image?.Url || !!image?.Uploaded;
    }

    async function uploadImage(file) {
        if (!file || !editingId) {
            return;
        }
        if (file.size > 10 * 1024 * 1024) {
            showImageStatus(null);
            $('#woImageStatus').textContent = 'That picture is over 10 MB.';
            $('#woImageStatus').classList.add('wo-bad');
            return;
        }
        Dashboard.showLoadingMsg();
        try {
            // Sent as the raw file (ApiClient.ajax only sends text and form fields).
            const response = await fetch(apiUrl(`WatchOrder/Orders/${encodeURIComponent(editingId)}/Image`), {
                method: 'POST',
                headers: { 'Content-Type': file.type || 'application/octet-stream', Authorization: `MediaBrowser Token="${ApiClient.accessToken()}"` },
                body: file
            });
            if (!response.ok) {
                throw response;
            }
            showImageStatus(await response.json());
        } catch (response) {
            $('#woImageStatus').textContent = (await errorMessages(response)).join(' ');
            $('#woImageStatus').classList.add('wo-bad');
        } finally {
            Dashboard.hideLoadingMsg();
            $('#woImageFile').value = '';
        }
    }

    async function imageAction(type, path) {
        Dashboard.showLoadingMsg();
        try {
            showImageStatus(await ApiClient.ajax({ type, url: apiUrl(`WatchOrder/Orders/${encodeURIComponent(editingId)}/${path}`), dataType: 'json' }));
        } catch (response) {
            $('#woImageStatus').textContent = (await errorMessages(response)).join(' ');
        } finally {
            Dashboard.hideLoadingMsg();
        }
    }

    function entryLabel(e) {
        const name = (e.type === 'movie' ? e.title : e.series)
            || Object.entries(e.providerIds || {}).map(([k, v]) => `${k} ${v}`).join(', ')
            || 'Untitled';
        const year = e.year ? ` (${e.year})` : '';
        switch (e.type) {
            case 'movie': return name + year;
            case 'season': return `${name}${year} · Season ${e.season}`;
            case 'episodes': return `${name}${year} · S${pad(e.season)}E${pad(e.from)}–E${pad(e.to)}`;
            default: return `${name}${year} · S${pad(e.season)}E${pad(e.episode)}`;
        }
    }

    function entryStatus(e) {
        const s = statusByUid.get(e._uid);
        if (!s) {
            return { cls: 'wo-pending', text: 'Checking library…' };
        }
        if (s.matched === 0) {
            return { cls: 'wo-missing', text: 'Not in library' };
        }

        const byTitle = s.byTitle > 0 ? ' · matched by title, worth checking' : '';
        if (e.type === 'season') {
            return { cls: s.byTitle ? 'wo-title' : 'wo-ok', text: `${s.matched} episode${s.matched === 1 ? '' : 's'} in library${byTitle}` };
        }
        if (e.type === 'episodes') {
            const partial = s.matched < s.total;
            return { cls: partial || s.byTitle ? 'wo-title' : 'wo-ok', text: `${s.matched} of ${s.total} in library${byTitle}` };
        }
        return { cls: s.byTitle ? 'wo-title' : 'wo-ok', text: `In library${byTitle}` };
    }

    function setStatus(message) {
        $('#woAddStatus').textContent = message;
    }

    // ------------------------------------------------------------- order list

    async function loadOrders() {
        const orders = await getJson('WatchOrder/Orders');
        knownIds = orders.map((o) => o.Id);
        const list = $('#woOrders');
        list.innerHTML = '';
        $('#woEmpty').hidden = orders.length > 0;
        $('#woOrderCount').textContent = orders.length ? `(${orders.length})` : '';
        // A filter box once the list gets long.
        $('#woFilterBox').hidden = orders.length < 8;

        for (const o of orders) {
            const li = document.createElement('li');
            li.dataset.search = `${o.Name} ${o.Id} ${o.Description || ''}`.toLowerCase();
            const missing = o.MissingCount
                ? `<span class="wo-status wo-missing">${o.MissingCount} missing from library</span>`
                : '<span class="wo-status wo-ok">All in library</span>';
            li.innerHTML = `
                <div class="wo-meta">
                    <div class="wo-name">${esc(o.Name)}${originTags(o)}</div>
                    <div class="wo-sub">${o.EntryCount} entr${o.EntryCount === 1 ? 'y' : 'ies'} · ${missing}<span class="wo-stats-summary" data-summary="${esc(o.Id)}"></span></div>
                </div>
                <button is="emby-button" type="button" class="raised button-alt" data-viewers="${esc(o.Id)}" aria-expanded="false" hidden><span>Viewers</span></button>
                <button is="emby-button" type="button" class="raised button-submit" data-edit="${esc(o.Id)}"><span>Edit</span></button>
                <button is="emby-button" type="button" class="raised button-alt" data-export="${esc(o.Id)}"><span>Export</span></button>
                <button is="emby-button" type="button" class="raised button-delete" data-delete="${esc(o.Id)}"><span>Delete</span></button>
                <div class="wo-stats" data-stats="${esc(o.Id)}" hidden></div>`;
            list.appendChild(li);
        }
        applyFilter();
        loadStats();
        loadReadyMade(); // states change as orders are added, edited and deleted
    }

    // ------------------------------------------------------------ order stats

    let statsById = new Map();

    /** Adds "2 watching · 1 finished" to each order, and fills the Viewers table. Not essential, so failures are quiet. */
    async function loadStats() {
        let stats;
        try {
            stats = await getJson('WatchOrder/Stats');
        } catch {
            return;
        }
        statsById = new Map(stats.map((s) => [s.OrderId, s]));
        for (const s of stats) {
            const summary = view.querySelector(`[data-summary="${CSS.escape(s.OrderId)}"]`);
            const button = view.querySelector(`[data-viewers="${CSS.escape(s.OrderId)}"]`);
            if (!summary || !button) {
                continue;
            }
            summary.textContent = s.Users.length ? ' · ' + statsSummary(s) : ' · No one has started it';
            button.hidden = s.Users.length === 0;
            button.querySelector('span').textContent = `Viewers (${s.Users.length})`;
            const panel = view.querySelector(`[data-stats="${CSS.escape(s.OrderId)}"]`);
            if (panel && !panel.hidden) {
                panel.innerHTML = statsTable(s);
            }
        }
    }

    function statsSummary(s) {
        const parts = [];
        if (s.InProgress) {
            parts.push(`${s.InProgress} watching${s.Stalled ? ` (${s.Stalled} stalled)` : ''}`);
        }
        if (s.Finished) {
            parts.push(`${s.Finished} finished`);
        }
        if (s.NotStarted) {
            parts.push(`${s.NotStarted} not started`);
        }
        return parts.join(' · ');
    }

    const STATUS_TEXT = { InProgress: 'Watching', Stalled: 'Stalled', NotStarted: 'Not started', Finished: 'Finished' };

    function ago(date) {
        if (!date) {
            return 'Never';
        }
        const days = Math.floor((Date.now() - new Date(date).getTime()) / 86400000);
        if (days < 1) {
            return 'Today';
        }
        if (days < 2) {
            return 'Yesterday';
        }
        if (days < 14) {
            return `${days} days ago`;
        }
        if (days < 60) {
            return `${Math.round(days / 7)} weeks ago`;
        }
        return days < 730 ? `${Math.round(days / 30)} months ago` : `${Math.round(days / 365)} years ago`;
    }

    function statsTable(s) {
        const rows = s.Users.map((u) => {
            const pct = u.AvailableCount ? Math.round(100 * u.WatchedCount / u.AvailableCount) : 0;
            const through = u.WatchThrough > 1 ? `${u.WatchThrough}` : '1';
            const finished = u.TimesFinished ? ` (finished ${u.TimesFinished === 1 ? 'once' : u.TimesFinished === 2 ? 'twice' : u.TimesFinished + ' times'})` : '';
            return `<tr>
                <th scope="row">${esc(u.UserName)}</th>
                <td><span class="wo-stat wo-stat-${esc(u.Status)}">${STATUS_TEXT[u.Status] || esc(u.Status)}</span></td>
                <td><div class="wo-bar" role="progressbar" aria-valuemin="0" aria-valuemax="${u.AvailableCount}" aria-valuenow="${u.WatchedCount}" aria-label="${esc(u.UserName)}'s progress"><div style="width:${pct}%"></div></div>
                    <span class="wo-muted">${u.WatchedCount} of ${u.AvailableCount}${u.OptionalMode === 'KeepRecommended' ? ', skipping optional (keeping recommended)' : u.SkipOptional ? ', skipping optional' : ''}</span></td>
                <td>${through}${finished}</td>
                <td>${u.Next ? esc(u.Next.DisplayName || u.Next.Label) : '<span class="wo-muted">—</span>'}</td>
                <td>${esc(ago(u.LastWatched))}</td>
            </tr>`;
        }).join('');
        return `<div class="wo-table-scroll"><table class="wo-table">
            <caption class="wo-muted">"Stalled" means part-way through with nothing watched in it for a month.</caption>
            <thead><tr><th scope="col">User</th><th scope="col">Status</th><th scope="col">Progress</th><th scope="col">Watch-through</th><th scope="col">Up next</th><th scope="col">Last watched</th></tr></thead>
            <tbody>${rows}</tbody></table></div>`;
    }

    function toggleViewers(button) {
        const id = button.dataset.viewers;
        const panel = view.querySelector(`[data-stats="${CSS.escape(id)}"]`);
        const stats = statsById.get(id);
        if (!panel || !stats) {
            return;
        }
        const open = panel.hidden;
        panel.innerHTML = open ? statsTable(stats) : '';
        panel.hidden = !open;
        button.setAttribute('aria-expanded', String(open));
    }

    function originTags(o) {
        const tags = [];
        if (o.Origin === 'ReadyMade') {
            tags.push('<span class="wo-tag">Ready-made</span>');
        } else if (o.Origin === 'Link') {
            tags.push(`<span class="wo-tag" title="${esc(o.OriginUrl || '')}">From a link</span>`);
        }
        if (o.Edited) {
            tags.push('<span class="wo-tag">Edited</span>');
        }
        if (o.UpdateAvailable) {
            tags.push('<span class="wo-tag wo-tag-update">Update available</span>');
        }
        return tags.join('');
    }

    // ------------------------------------------------------- ready-made orders

    const READY_STATE = {
        NotAdded: { text: '', button: 'Add', cls: 'button-submit' },
        Added: { text: 'Added', button: '' },
        UpdateAvailable: { text: 'Update available', button: 'Update', cls: 'button-submit' },
        Edited: { text: 'Added, and edited here', button: '' },
        EditedUpdateAvailable: { text: 'Update available (you\'ve edited yours)', button: 'Update', cls: 'button-submit' },
        IdTaken: { text: 'You have a different order with this ID', button: 'Replace yours', cls: '' }
    };

    async function loadReadyMade() {
        const box = $('#woReadyMade');
        let groups;
        try {
            groups = await getJson('WatchOrder/ReadyMade');
        } catch {
            box.innerHTML = '<p class="wo-errors">The ready-made orders couldn\'t be loaded.</p>';
            return;
        }
        box.innerHTML = groups.map((g) => `
            <h3>${esc(g.Group)}</h3>
            <ul class="wo-list">${g.Orders.map(readyMadeRow).join('')}</ul>`).join('');
    }

    function readyMadeRow(o) {
        const state = READY_STATE[o.State] || READY_STATE.NotAdded;
        const library = o.ItemCount
            ? (o.InLibrary === 0 ? '<span class="wo-status wo-missing">None in your library</span>'
                : o.InLibrary >= o.ItemCount ? '<span class="wo-status wo-ok">All in your library</span>'
                : `<span class="wo-status wo-title">${o.InLibrary} of ${o.ItemCount} in your library</span>`)
            : '';
        const version = o.State === 'UpdateAvailable' || o.State === 'EditedUpdateAvailable'
            ? ` · version ${o.AddedVersion} → ${o.Version}` : '';
        return `<li data-search="">
            <div class="wo-meta">
                <div class="wo-name">${esc(o.Name)}${state.text ? `<span class="wo-tag${o.State.includes('Update') ? ' wo-tag-update' : ''}">${esc(state.text)}</span>` : ''}</div>
                <div class="wo-sub">${o.EntryCount} entr${o.EntryCount === 1 ? 'y' : 'ies'} · ${library}${version}</div>
                ${o.Description ? `<div class="wo-sub wo-desc" title="${esc(o.Description)}">${esc(o.Description)}</div>` : ''}
            </div>
            ${state.button ? `<button is="emby-button" type="button" class="raised ${state.cls || ''}" data-ready="${esc(o.Id)}" data-state="${esc(o.State)}" data-name="${esc(o.Name)}"><span>${state.button}</span></button>` : ''}
        </li>`;
    }

    async function onReadyMadeClick(e) {
        const button = e.target.closest('[data-ready]');
        if (!button) {
            return;
        }
        const { ready: id, state, name } = button.dataset;
        if (state === 'IdTaken' && !confirm(`You already have a different order with the ID "${id}". Replace it with the ready-made "${name}"? This can't be undone.`)) {
            return;
        }
        if (state === 'EditedUpdateAvailable' && !confirm(`You've edited your copy of "${name}". Updating replaces your changes with the new version. Continue?`)) {
            return;
        }
        button.disabled = true;
        Dashboard.showLoadingMsg();
        try {
            await ApiClient.ajax({ type: 'POST', url: apiUrl(`WatchOrder/ReadyMade/${encodeURIComponent(id)}`, { replace: state === 'IdTaken' || state === 'EditedUpdateAvailable' }) });
            await loadOrders();
        } catch {
            button.disabled = false;
            Dashboard.alert(`"${name}" couldn't be added.`);
        } finally {
            Dashboard.hideLoadingMsg();
        }
    }

    function applyFilter() {
        const words = ($('#woFilter').value || '').toLowerCase().split(/\s+/).filter(Boolean);
        let shown = 0;
        for (const li of $('#woOrders').children) {
            li.hidden = !words.every((w) => li.dataset.search.includes(w));
            shown += li.hidden ? 0 : 1;
        }
        $('#woNoMatch').hidden = shown > 0 || !$('#woOrders').children.length;
    }

    async function onOrderListClick(e) {
        const editId = e.target.closest('[data-edit]')?.dataset.edit;
        const exportId = e.target.closest('[data-export]')?.dataset.export;
        const deleteId = e.target.closest('[data-delete]')?.dataset.delete;
        const viewers = e.target.closest('[data-viewers]');
        if (viewers) {
            toggleViewers(viewers);
            return;
        }

        if (editId) {
            Dashboard.showLoadingMsg();
            try {
                openBuilder(await getJson(`WatchOrder/Orders/${encodeURIComponent(editId)}/Definition`));
            } finally {
                Dashboard.hideLoadingMsg();
            }
        }

        if (exportId) {
            const def = await getJson(`WatchOrder/Orders/${encodeURIComponent(exportId)}/Definition`);
            const json = JSON.stringify(toPayload(toDraft(def)), null, 2) + '\n';
            const link = document.createElement('a');
            link.href = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
            link.download = `${exportId}.json`;
            link.click();
            setTimeout(() => URL.revokeObjectURL(link.href), 1000);
        }

        if (deleteId && confirm(`Delete the order "${deleteId}"? This can't be undone.`)) {
            await ApiClient.ajax({ type: 'DELETE', url: apiUrl(`WatchOrder/Orders/${encodeURIComponent(deleteId)}`) });
            await loadOrders();
        }
    }

    async function importOrder() {
        const text = $('#woImportJson').value;
        if (!text.trim()) {
            $('#woImportErrors').textContent = 'Paste an order, or choose a file above.';
            return;
        }
        if (await importOrders([{ name: null, text }])) {
            $('#woImportJson').value = '';
        }
    }

    /** Imports each { name, text }; reports what worked and what didn't. Returns true when all succeeded. */
    async function importOrders(files) {
        const errorsEl = $('#woImportErrors');
        errorsEl.textContent = '';
        const imported = [];
        const problems = [];
        Dashboard.showLoadingMsg();
        try {
            for (const file of files) {
                const prefix = file.name ? `${file.name}: ` : '';
                let body;
                try {
                    body = JSON.parse(file.text);
                } catch (err) {
                    problems.push(`${prefix}that isn't valid JSON (${err.message}).`);
                    continue;
                }
                const id = pick(body, 'id');
                if (id && knownIds.includes(id) && !confirm(`You already have an order with the ID "${id}". Replace it with ${file.name ? `"${file.name}"` : 'this one'}?`)) {
                    problems.push(`${prefix}kept your existing "${id}".`);
                    continue;
                }
                try {
                    await sendJsonTo('POST', 'WatchOrder/Orders', body, file.url ? { fromUrl: file.url } : undefined);
                    imported.push(body.name || body.id || file.name);
                    if (id && !knownIds.includes(id)) {
                        knownIds.push(id);
                    }
                } catch (response) {
                    problems.push(prefix + (await errorMessages(response)).join('; '));
                }
            }
        } finally {
            Dashboard.hideLoadingMsg();
        }

        errorsEl.textContent = problems.join('\n');
        if (imported.length) {
            await loadOrders();
            Dashboard.alert(imported.length === 1 ? `Imported "${imported[0]}".` : `Imported ${imported.length} orders.`);
        }
        return problems.length === 0;
    }

    async function importLink() {
        const link = $('#woImportUrl').value.trim();
        const errorsEl = $('#woImportErrors');
        if (!link) {
            errorsEl.textContent = 'Paste a link to an order file.';
            return;
        }
        errorsEl.textContent = '';
        let fetched;
        Dashboard.showLoadingMsg();
        try {
            fetched = await getJson('WatchOrder/Fetch', { url: link });
        } catch (response) {
            errorsEl.textContent = (await errorMessages(response)).join('\n');
            return;
        } finally {
            Dashboard.hideLoadingMsg();
        }
        if (await importOrders([{ name: link, text: fetched.Text, url: fetched.Url }])) {
            $('#woImportUrl').value = '';
        }
    }

    async function importFiles(fileList) {
        const files = [...fileList].filter((f) => /\.json$/i.test(f.name) || f.type === 'application/json');
        if (!files.length) {
            $('#woImportErrors').textContent = 'Choose .json order files.';
            return;
        }
        await importOrders(await Promise.all(files.map(async (f) => ({ name: f.name, text: await f.text() }))));
    }

    function wireDropZone() {
        const zone = $('#woDropZone');
        const input = $('#woImportFile');
        $('#woChooseFiles').addEventListener('click', () => input.click());
        input.addEventListener('change', async () => {
            await importFiles(input.files);
            input.value = '';
        });
        zone.addEventListener('dragover', (e) => {
            if ([...(e.dataTransfer?.types || [])].includes('Files')) {
                e.preventDefault();
                zone.classList.add('wo-dragover');
            }
        });
        zone.addEventListener('dragleave', () => zone.classList.remove('wo-dragover'));
        zone.addEventListener('drop', (e) => {
            e.preventDefault();
            zone.classList.remove('wo-dragover');
            if (e.dataTransfer?.files?.length) {
                importFiles(e.dataTransfer.files);
            }
        });
    }

    /** Sections remember whether they were left open or closed, in this browser. */
    function rememberSections() {
        const key = 'watchorder-admin-sections';
        let saved = {};
        try {
            saved = JSON.parse(localStorage.getItem(key) || '{}') || {};
        } catch {
            saved = {};
        }
        for (const section of view.querySelectorAll('details[data-section]')) {
            const name = section.dataset.section;
            if (typeof saved[name] === 'boolean') {
                section.open = saved[name];
            }
            section.addEventListener('toggle', () => {
                saved[name] = section.open;
                try {
                    localStorage.setItem(key, JSON.stringify(saved));
                } catch {
                    // Storage unavailable: sections just won't be remembered.
                }
            });
        }
    }

    async function refreshAll() {
        Dashboard.showLoadingMsg();
        try {
            await ApiClient.ajax({ type: 'POST', url: apiUrl('WatchOrder/Refresh') });
            await loadOrders();
        } finally {
            Dashboard.hideLoadingMsg();
        }
    }

    // ------------------------------------------------------- web app status

    async function loadWebClientStatus() {
        const notice = $('#woWebClientNotice');
        try {
            const status = await getJson('WatchOrder/WebClientStatus');
            if (status.Enabled && !status.FileTransformationInstalled) {
                notice.innerHTML = 'To show the <strong>Watch orders</strong> page to your users in the web app, install the '
                    + '<strong>File Transformation</strong> plugin, then restart Jellyfin. '
                    + 'Add the repository <code>https://www.iamparadox.dev/jellyfin/plugins/manifest.json</code> under Plugins → Repositories to find it.';
                notice.hidden = false;
            } else {
                notice.hidden = true;
            }
        } catch {
            notice.hidden = true;
        }
    }

    // --------------------------------------------------------------- settings

    async function loadSettings() {
        const config = await ApiClient.getPluginConfiguration(pluginId);
        for (const key of settingsFields) {
            const el = $('#' + key);
            if (el.type === 'checkbox') {
                el.checked = !!config[key];
            } else {
                el.value = config[key] ?? el.value;
            }
        }
    }

    async function saveSettings(e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        const config = await ApiClient.getPluginConfiguration(pluginId);
        for (const key of settingsFields) {
            const el = $('#' + key);
            config[key] = el.type === 'checkbox' ? el.checked : el.type === 'number' ? parseInt(el.value, 10) : el.value;
        }
        const result = await ApiClient.updatePluginConfiguration(pluginId, config);
        Dashboard.processPluginConfigurationUpdateResult(result);
        loadWebClientStatus();
    }

    // ---------------------------------------------------------------- builder

    function showView(builder) {
        $('#woListView').hidden = builder;
        $('#woBuilderView').hidden = !builder;
        window.scrollTo(0, 0);
    }

    function openBuilder(def) {
        draft = def ? toDraft(def) : { id: '', name: '', description: '', author: '', version: '', source: '', image: { _extra: {} }, entries: [] };
        editingId = def ? draft.id : null;
        idTouched = !!def;
        dirty = false;
        statusByUid = new Map();

        $('#woBuilderTitle').textContent = def ? `Edit "${draft.name}"` : 'New order';
        $('#woName').value = draft.name;
        $('#woId').value = draft.id;
        $('#woId').readOnly = !!def;
        $('#woDescription').value = draft.description;
        $('#woAuthor').value = draft.author;
        $('#woVersion').value = draft.version;
        $('#woSource').value = draft.source;
        $('#woImageUrl').value = draft.image?.url || '';
        $('#woImageStatus').textContent = '';
        refreshImageStatus();
        $('#woBuilderErrors').textContent = '';
        $('#woLibrarySearch').value = '';
        $('#woLibraryResults').innerHTML = '';
        $('#woLibraryPicker').innerHTML = '';
        $('#woProviderResults').innerHTML = '';
        $('#woProviderPicker').innerHTML = '';
        setStatus('');
        selectTab('library');

        closeSectionForm();
        renderEntries();
        $('#woEntriesScroll').scrollTop = 0;
        schedulePreview(0);
        showView(true);
    }

    function closeBuilder() {
        if (dirty && !confirm('Discard your unsaved changes to this order?')) {
            return;
        }
        draft = null;
        showView(false);
        loadOrders();
    }

    function markChanged(needsPreview) {
        dirty = true;
        if (needsPreview) {
            schedulePreview();
        }
    }

    function addEntries(entries) {
        for (const entry of entries) {
            draft.entries.push({ _uid: nextUid(), ...cleanEntry(entry) });
        }
        renderEntries();
        markChanged(true);

        // Show what was just added at the bottom of the list.
        const scroller = $('#woEntriesScroll');
        scroller.scrollTop = scroller.scrollHeight;

        const first = draft.entries.length - entries.length + 1;
        setStatus(entries.length === 1
            ? `Added "${entryLabel(entries[0])}" as entry ${first}.`
            : `Added ${entries.length} entries (${first}–${draft.entries.length}).`);
    }

    function moveEntry(from, to) {
        if (from === to || to < 0 || to >= draft.entries.length) {
            return;
        }
        const [entry] = draft.entries.splice(from, 1);
        draft.entries.splice(to, 0, entry);
        renderEntries();
        markChanged(false); // statuses are keyed by entry, so reordering doesn't need a re-check
    }

    function renderEntries() {
        const list = $('#woEntries');
        const count = draft.entries.length;
        $('#woNoEntries').hidden = count > 0;
        $('#woEntriesScroll').hidden = count === 0;

        list.innerHTML = draft.entries.map((e, i) => {
            const status = entryStatus(e);
            const ids = Object.entries(e.providerIds || {}).map(([k, v]) => `${k} ${v}`).join(' · ');
            const section = e.section !== undefined && e.section !== null ? `
                <li class="wo-section-row">
                    <span class="material-icons segment" aria-hidden="true"></span>
                    <input class="wo-section-input" data-section-input="${i}" value="${esc(e.section)}" placeholder="Section heading, e.g. Phase Two" aria-label="Heading of the section starting at entry ${i + 1}" />
                    <button type="button" class="wo-icon-btn" data-section-remove="${i}" title="Remove this section heading" aria-label="Remove the section heading before entry ${i + 1}"><span class="material-icons close" aria-hidden="true"></span></button>
                </li>` : '';
            return `${section}
                <li class="wo-entry${e.optional ? ' wo-entry-optional' : ''}${e.optional && e.recommended ? ' wo-entry-recommended' : ''}" data-index="${i}">
                    <span class="wo-handle material-icons drag_indicator" title="Drag to reorder" aria-hidden="true"></span>
                    <span class="wo-pos">${i + 1}</span>
                    <div class="wo-entry-main">
                        <div class="wo-entry-label">${esc(entryLabel(e))}</div>
                        <div class="wo-entry-sub">
                            <span class="wo-chip">${TYPE_NAMES[e.type] || e.type}</span>
                            <span class="wo-status ${status.cls}">${esc(status.text)}</span>
                            ${ids ? `<span class="wo-muted">${esc(ids)}</span>` : ''}
                            <label class="wo-optional" title="Filler or a side story. Each user chooses whether to skip optional entries.">
                                <input type="checkbox" data-optional="${i}" ${e.optional ? 'checked' : ''} aria-label="Entry ${i + 1} is optional" /> Optional
                            </label>
                            <label class="wo-optional wo-recommended" title="Optional, but worth watching. Viewers can skip optional entries but keep the recommended ones." ${e.optional ? '' : 'hidden'}>
                                <input type="checkbox" data-recommended="${i}" ${e.optional && e.recommended ? 'checked' : ''} aria-label="Entry ${i + 1} is recommended" /> Recommended
                            </label>
                        </div>
                        <input class="wo-note" data-note="${i}" value="${esc(e.note || '')}" placeholder="Add a note (optional)" aria-label="Note for entry ${i + 1}" />
                    </div>
                    <div class="wo-entry-actions">
                        <button type="button" class="wo-icon-btn" data-section-add="${i}" title="Start a section here" aria-label="Start a section at entry ${i + 1}" ${section ? 'disabled' : ''}><span class="material-icons segment" aria-hidden="true"></span></button>
                        <button type="button" class="wo-icon-btn" data-move="-1" data-index="${i}" title="Move up" aria-label="Move entry ${i + 1} up" ${i === 0 ? 'disabled' : ''}><span class="material-icons arrow_upward" aria-hidden="true"></span></button>
                        <button type="button" class="wo-icon-btn" data-move="1" data-index="${i}" title="Move down" aria-label="Move entry ${i + 1} down" ${i === count - 1 ? 'disabled' : ''}><span class="material-icons arrow_downward" aria-hidden="true"></span></button>
                        <button type="button" class="wo-icon-btn" data-remove="${i}" title="Remove" aria-label="Remove entry ${i + 1}"><span class="material-icons delete" aria-hidden="true"></span></button>
                    </div>
                </li>`;
        }).join('');

        updateSummary();
        renderImageChoices();
        renderSectionsBar();
    }

    // ------------------------------------------------------- sections

    const hasSection = (e) => e.section !== undefined && e.section !== null;

    /** The order's sections as buttons that jump to them, and "Add a section". */
    function renderSectionsBar() {
        const entries = draft.entries;
        $('#woSectionsBar').hidden = entries.length === 0;
        if (!entries.length) {
            closeSectionForm();
        }
        const starts = entries.map((e, i) => (hasSection(e) ? i : -1)).filter((i) => i >= 0);
        $('#woSectionChips').innerHTML = starts.length
            ? starts.map((start, n) => {
                const end = (starts[n + 1] ?? entries.length) - 1;
                const name = String(entries[start].section).trim() || 'Untitled section';
                const range = start === end ? `entry ${start + 1}` : `entries ${start + 1}–${end + 1}`;
                return `<span role="listitem"><button type="button" class="wo-section-chip" data-section-go="${start}" title="Show this section in the list">${esc(name)} <span class="wo-muted">${range}</span></button></span>`;
            }).join('')
            : '<span class="wo-muted">None yet. Long orders are easier to follow split into parts, such as phases or arcs.</span>';
        if (!$('#woSectionForm').hidden) {
            fillSectionStarts();
        }
    }

    /** "Starts at": every entry, with the ones that already start a section marked and unavailable. */
    function fillSectionStarts() {
        const select = $('#woSectionStart');
        const previous = select.value;
        const entries = draft.entries;
        select.innerHTML = entries.map((e, i) => `<option value="${i}"${hasSection(e) ? ' disabled' : ''}>${esc(`${i + 1}. ${entryLabel(e)}${hasSection(e) ? ` (starts "${String(e.section).trim() || 'Untitled'}")` : ''}`)}</option>`).join('');
        if (previous !== '' && entries[Number(previous)] && !hasSection(entries[Number(previous)])) {
            select.value = previous;
            return;
        }
        // Suggest the first free entry after the last section, else the first free one.
        const last = entries.reduce((at, e, i) => (hasSection(e) ? i : at), -1);
        let pick = entries.findIndex((e, i) => i > last && !hasSection(e));
        if (pick < 0) {
            pick = entries.findIndex((e) => !hasSection(e));
        }
        select.value = String(Math.max(pick, 0));
    }

    function openSectionForm() {
        const form = $('#woSectionForm');
        form.hidden = false;
        $('#woAddSection').setAttribute('aria-expanded', 'true');
        $('#woSectionError').textContent = '';
        $('#woSectionName').value = '';
        $('#woSectionStart').value = '';
        fillSectionStarts();
        $('#woSectionName').focus();
    }

    function closeSectionForm(focusButton) {
        $('#woSectionForm').hidden = true;
        $('#woAddSection').setAttribute('aria-expanded', 'false');
        if (focusButton) {
            $('#woAddSection').focus();
        }
    }

    function addSection(e) {
        e.preventDefault();
        const name = $('#woSectionName').value.trim();
        const index = Number($('#woSectionStart').value);
        const entry = draft.entries[index];
        if (!name) {
            $('#woSectionError').textContent = 'Give the section a heading.';
            $('#woSectionName').focus();
            return;
        }
        if (!entry || hasSection(entry)) {
            $('#woSectionError').textContent = 'Choose an entry that doesn\'t already start a section.';
            $('#woSectionStart').focus();
            return;
        }
        entry.section = name;
        renderEntries();
        markChanged(false);
        closeSectionForm(true);
        showSectionInList(index, false);
        setStatus(`Added the section "${name}", starting at entry ${index + 1}.`);
    }

    /** Scrolls the list to a section's heading and briefly highlights it; optionally puts focus in it. */
    function showSectionInList(index, focus) {
        const row = $(`#woEntries [data-section-input="${index}"]`)?.closest('.wo-section-row');
        const scroller = $('#woEntriesScroll');
        if (!row) {
            return;
        }
        scroller.scrollTop += row.getBoundingClientRect().top - scroller.getBoundingClientRect().top - 8;
        row.classList.add('wo-flash');
        setTimeout(() => row.classList.remove('wo-flash'), 1200);
        if (focus) {
            row.querySelector('input').focus({ preventScroll: true });
        }
    }

    function updateSummary() {
        const entries = draft.entries;
        if (!entries.length) {
            $('#woSummary').textContent = '';
            return;
        }
        const known = entries.map((e) => statusByUid.get(e._uid)).filter(Boolean);
        const missing = known.reduce((n, s) => n + (s.total - s.matched), 0);
        const checked = known.length === entries.length;
        $('#woSummary').textContent = `${entries.length} entr${entries.length === 1 ? 'y' : 'ies'}`
            + (checked ? (missing ? ` · ${missing} item${missing === 1 ? '' : 's'} missing from library` : ' · everything is in your library') : '');
    }

    function onEntriesClick(e) {
        const sectionAdd = e.target.closest('[data-section-add]');
        const sectionRemove = e.target.closest('[data-section-remove]');
        if (sectionAdd && !sectionAdd.disabled) {
            const index = Number(sectionAdd.dataset.sectionAdd);
            draft.entries[index].section = '';
            renderEntries();
            markChanged(false);
            $(`#woEntries [data-section-input="${index}"]`)?.focus();
            return;
        }
        if (sectionRemove) {
            const index = Number(sectionRemove.dataset.sectionRemove);
            delete draft.entries[index].section;
            renderEntries();
            markChanged(false);
            setStatus(`Removed the section heading before "${entryLabel(draft.entries[index])}".`);
            $(`#woEntries [data-section-add="${index}"]`)?.focus();
            return;
        }

        const moveBtn = e.target.closest('[data-move]');
        const removeBtn = e.target.closest('[data-remove]');

        if (moveBtn && !moveBtn.disabled) {
            const from = Number(moveBtn.dataset.index);
            const dir = Number(moveBtn.dataset.move);
            moveEntry(from, from + dir);
            // Keep keyboard focus on the same button of the moved row.
            const again = $(`#woEntries [data-move="${dir}"][data-index="${from + dir}"]`);
            (again && !again.disabled ? again : $(`#woEntries [data-move="${-dir}"][data-index="${from + dir}"]`))?.focus();
        }

        if (removeBtn) {
            const index = Number(removeBtn.dataset.remove);
            const [removed] = draft.entries.splice(index, 1);
            statusByUid.delete(removed._uid);
            renderEntries();
            markChanged(false);
            setStatus(`Removed "${entryLabel(removed)}".`);
        }
    }

    function onOptionalChange(e) {
        const index = e.target.dataset.optional;
        if (index !== undefined) {
            const entry = draft.entries[Number(index)];
            entry.optional = e.target.checked;
            const row = e.target.closest('.wo-entry');
            row?.classList.toggle('wo-entry-optional', entry.optional);
            const recommended = row?.querySelector('.wo-recommended');
            if (recommended) {
                recommended.hidden = !entry.optional;
            }
            if (!entry.optional && entry.recommended) {
                // Recommended only means something for optional entries.
                entry.recommended = false;
                recommended.querySelector('input').checked = false;
                row.classList.remove('wo-entry-recommended');
            }
            markChanged(false);
            setStatus(`"${entryLabel(entry)}" is ${entry.optional ? 'now optional' : 'no longer optional'}.`);
            return;
        }

        const recIndex = e.target.dataset.recommended;
        if (recIndex !== undefined) {
            const entry = draft.entries[Number(recIndex)];
            entry.recommended = e.target.checked;
            e.target.closest('.wo-entry')?.classList.toggle('wo-entry-recommended', entry.recommended);
            markChanged(false);
            setStatus(`"${entryLabel(entry)}" is ${entry.recommended ? 'now optional but recommended' : 'no longer recommended'}.`);
        }
    }

    function onNoteInput(e) {
        const sectionIndex = e.target.dataset.sectionInput;
        if (sectionIndex !== undefined) {
            draft.entries[Number(sectionIndex)].section = e.target.value;
            markChanged(false);
            renderSectionsBar(); // keep the section buttons' names in step
            return;
        }
        const index = e.target.dataset.note;
        if (index !== undefined) {
            draft.entries[Number(index)].note = e.target.value;
            markChanged(false);
        }
    }

    // Drag and drop: rows only become draggable while the handle is held,
    // so text in the note field can still be selected normally.
    function onPointerDown(e) {
        const handle = e.target.closest('.wo-handle');
        if (handle) {
            handle.closest('.wo-entry').draggable = true;
        }
    }

    function clearDropMarkers() {
        view.querySelectorAll('.wo-drop-before, .wo-drop-after').forEach((el) => el.classList.remove('wo-drop-before', 'wo-drop-after'));
    }

    function dropPosition(e) {
        const row = e.target.closest('.wo-entry');
        if (!row) {
            return null;
        }
        const rect = row.getBoundingClientRect();
        return { row, index: Number(row.dataset.index), after: e.clientY > rect.top + rect.height / 2 };
    }

    function onDragStart(e) {
        const row = e.target.closest('.wo-entry');
        if (!row || !row.draggable) {
            e.preventDefault();
            return;
        }
        dragFrom = Number(row.dataset.index);
        row.classList.add('wo-dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', String(dragFrom));
    }

    function onDragOver(e) {
        if (dragFrom < 0) {
            return;
        }
        // Near the top or bottom edge of the list, scroll it, so a row can be dragged anywhere in a long order.
        const scroller = $('#woEntriesScroll');
        const box = scroller.getBoundingClientRect();
        const edge = Math.min(56, box.height / 4);
        if (e.clientY < box.top + edge) {
            scroller.scrollTop -= Math.ceil((box.top + edge - e.clientY) / 3);
        } else if (e.clientY > box.bottom - edge) {
            scroller.scrollTop += Math.ceil((e.clientY - (box.bottom - edge)) / 3);
        }
        const pos = dropPosition(e);
        if (!pos) {
            return;
        }
        e.preventDefault();
        clearDropMarkers();
        pos.row.classList.add(pos.after ? 'wo-drop-after' : 'wo-drop-before');
    }

    function onDrop(e) {
        const pos = dropPosition(e);
        if (dragFrom < 0 || !pos) {
            return;
        }
        e.preventDefault();
        let to = pos.index + (pos.after ? 1 : 0);
        if (to > dragFrom) {
            to--;
        }
        const from = dragFrom;
        dragFrom = -1;
        moveEntry(from, to);
    }

    function onDragEnd() {
        dragFrom = -1;
        clearDropMarkers();
        view.querySelectorAll('.wo-entry').forEach((row) => {
            row.draggable = false;
            row.classList.remove('wo-dragging');
        });
    }

    // Live "in library / missing" check. Debounced, and stale responses are ignored.
    function schedulePreview(delay = 500) {
        clearTimeout(previewTimer);
        if (!draft || draft.entries.length === 0) {
            updateSummary();
            return;
        }
        previewTimer = setTimeout(runPreview, delay);
    }

    async function runPreview() {
        const seq = ++previewSeq;
        const sent = draft.entries.slice();
        try {
            const result = await sendJson('POST', 'WatchOrder/Preview', { name: draft.name, entries: sent.map(cleanEntry) });
            if (seq !== previewSeq || !draft) {
                return;
            }

            const errors = result.Errors || [];
            $('#woBuilderErrors').textContent = errors.join('\n');
            if (errors.length || !result.Order) {
                return;
            }

            const next = new Map();
            for (const step of result.Order.Steps) {
                const entry = sent[step.EntryIndex];
                if (!entry) {
                    continue;
                }
                const s = next.get(entry._uid) || { total: 0, matched: 0, byTitle: 0 };
                s.total++;
                if (step.Status !== 'Missing') {
                    s.matched++;
                }
                if (step.Status === 'MatchedByTitle') {
                    s.byTitle++;
                }
                next.set(entry._uid, s);
            }
            statusByUid = next;
            renderEntries();
        } catch {
            if (seq === previewSeq) {
                $('#woBuilderErrors').textContent = 'Couldn\'t check the order against your library. Your changes are still here.';
            }
        }
    }

    function readHeaderFields() {
        draft.name = $('#woName').value;
        draft.id = $('#woId').value.trim();
        draft.description = $('#woDescription').value;
        draft.author = $('#woAuthor').value;
        draft.version = $('#woVersion').value;
        draft.source = $('#woSource').value;
        draft.image = draft.image || { _extra: {} };
        draft.image.url = $('#woImageUrl').value;
    }

    function onNameInput() {
        markChanged(false);
        if (!idTouched && !editingId) {
            $('#woId').value = slugify($('#woName').value);
        }
    }

    async function saveOrder() {
        readHeaderFields();
        const errors = [];
        if (!draft.name.trim()) {
            errors.push('Give the order a name.');
        }
        if (!ID_PATTERN.test(draft.id)) {
            errors.push('The ID must be 1–64 characters: lowercase letters, numbers and hyphens, starting with a letter or number.');
        }
        if (!draft.entries.length) {
            errors.push('Add at least one entry.');
        }
        if (errors.length) {
            $('#woBuilderErrors').textContent = errors.join('\n');
            return;
        }
        if (!editingId && knownIds.includes(draft.id)
            && !confirm(`An order with the ID "${draft.id}" already exists. Replace it?`)) {
            return;
        }

        Dashboard.showLoadingMsg();
        try {
            await sendJson('POST', 'WatchOrder/Orders', toPayload(draft));
            dirty = false;
            draft = null;
            showView(false);
            await loadOrders();
            Dashboard.alert('Order saved.');
        } catch (response) {
            $('#woBuilderErrors').textContent = (await errorMessages(response)).join('\n');
        } finally {
            Dashboard.hideLoadingMsg();
        }
    }

    // ------------------------------------------------------------------- tabs

    function selectTab(name) {
        view.querySelectorAll('.wo-tab').forEach((tab) => tab.setAttribute('aria-selected', String(tab.dataset.tab === name)));
        view.querySelectorAll('[data-panel]').forEach((panel) => {
            panel.hidden = panel.dataset.panel !== name;
        });
        if (name === 'collection' && !collectionsLoaded) {
            loadCollections();
        }
    }

    // ---------------------------------------------------- series/season picker

    /**
     * Renders the "which episodes?" picker for a series.
     * @param {HTMLElement} container Where to render.
     * @param {{name: string, year?: number, providerIds: object}} series The series.
     * @param {{seasons: {number: number, label: string}[], loadEpisodes: (season: number) => Promise<{number: number, label: string}[]>}|null} library
     *        Season and episode lists when the series is in the library; null to type numbers instead.
     */
    function renderSeriesPicker(container, series, library) {
        const numberInput = (attr, label, min = 0) =>
            `<div class="inputContainer wo-narrow" ${attr}><input is="emby-input" type="number" min="${min}" label="${label}" /></div>`;
        const selectInput = (attr, label) =>
            `<div class="selectContainer wo-narrow" ${attr}><select is="emby-select" label="${label}"></select></div>`;

        const seasonField = library
            ? `<div class="selectContainer wo-narrow" data-pick="season"><select is="emby-select" label="Season">${
                library.seasons.map((s) => `<option value="${s.number}">${esc(s.label)}</option>`).join('')}</select></div>`
            : numberInput('data-pick="season"', 'Season');
        const episodeField = (attr, label) => (library ? selectInput(attr, label) : numberInput(attr, label));

        container.innerHTML = `
            <div class="wo-picker">
                <h4>Add from ${esc(series.name)}${series.year ? ` (${series.year})` : ''}</h4>
                <div class="wo-row wo-row-end">
                    ${seasonField}
                    <div class="selectContainer" data-pick="mode">
                        <select is="emby-select" label="Add">
                            <option value="season">Whole season</option>
                            <option value="episodes">Range of episodes</option>
                            <option value="episode">Single episode</option>
                        </select>
                    </div>
                    ${episodeField('data-pick="from" data-for="episodes"', 'From')}
                    ${episodeField('data-pick="to" data-for="episodes"', 'To')}
                    ${episodeField('data-pick="episode" data-for="episode"', 'Episode')}
                </div>
                <div class="wo-errors" data-pick="errors" role="alert"></div>
                <div class="wo-row">
                    <button is="emby-button" type="button" class="raised button-submit" data-pick="add"><span>Add to order</span></button>
                    <button is="emby-button" type="button" class="raised button-cancel" data-pick="close"><span>Close</span></button>
                </div>
            </div>`;

        const field = (name) => container.querySelector(`[data-pick="${name}"] input, [data-pick="${name}"] select`);
        const errorsEl = container.querySelector('[data-pick="errors"]');

        const showFields = () => {
            const mode = field('mode').value;
            container.querySelectorAll('[data-for]').forEach((el) => {
                el.hidden = el.dataset.for !== mode;
            });
        };

        const loadEpisodes = async () => {
            if (!library) {
                return;
            }
            const episodes = await library.loadEpisodes(Number(field('season').value));
            const options = episodes.map((ep) => `<option value="${ep.number}">${esc(ep.label)}</option>`).join('');
            for (const name of ['from', 'to', 'episode']) {
                field(name).innerHTML = options;
            }
            if (episodes.length) {
                field('to').value = String(episodes[episodes.length - 1].number);
            }
        };

        field('mode').addEventListener('change', showFields);
        field('season')?.addEventListener('change', loadEpisodes);
        container.querySelector('[data-pick="close"]').addEventListener('click', () => {
            container.innerHTML = '';
        });
        container.querySelector('[data-pick="add"]').addEventListener('click', () => {
            const mode = field('mode').value;
            const season = num(field('season').value);
            const base = { series: series.name, year: series.year, providerIds: keptIds(series.providerIds), season };
            errorsEl.textContent = '';

            if (season === undefined) {
                errorsEl.textContent = 'Choose a season.';
                return;
            }
            if (mode === 'season') {
                addEntries([{ type: 'season', ...base }]);
            } else if (mode === 'episodes') {
                const from = num(field('from').value);
                const to = num(field('to').value);
                if (from === undefined || to === undefined) {
                    errorsEl.textContent = 'Choose the first and last episode.';
                    return;
                }
                if (from > to) {
                    errorsEl.textContent = 'The first episode comes after the last one.';
                    return;
                }
                addEntries([from === to ? { type: 'episode', ...base, episode: from } : { type: 'episodes', ...base, from, to }]);
            } else {
                const episode = num(field('episode').value);
                if (episode === undefined) {
                    errorsEl.textContent = 'Choose an episode.';
                    return;
                }
                addEntries([{ type: 'episode', ...base, episode }]);
            }
        });

        showFields();
        loadEpisodes();
        container.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }

    // ---------------------------------------------------------------- library

    let librarySearchTimer = 0;
    let librarySearchSeq = 0;
    let libraryItems = [];

    function onLibrarySearchInput() {
        clearTimeout(librarySearchTimer);
        librarySearchTimer = setTimeout(searchLibrary, 300);
    }

    async function searchLibrary() {
        const term = $('#woLibrarySearch').value.trim();
        const results = $('#woLibraryResults');
        const seq = ++librarySearchSeq;
        if (term.length < 2) {
            results.innerHTML = '';
            return;
        }

        const response = await getJson('Items', {
            searchTerm: term,
            IncludeItemTypes: 'Movie,Series',
            Recursive: true,
            Fields: 'ProviderIds,ProductionYear',
            EnableTotalRecordCount: false,
            Limit: 25
        });
        if (seq !== librarySearchSeq) {
            return;
        }

        libraryItems = response.Items || [];
        results.innerHTML = libraryItems.length
            ? libraryItems.map((item, i) => {
                const isSeries = item.Type === 'Series';
                const img = item.ImageTags?.Primary
                    ? `<img loading="lazy" alt="" src="${ApiClient.getScaledImageUrl(item.Id, { type: 'Primary', maxHeight: 120, tag: item.ImageTags.Primary })}">`
                    : '<span class="wo-noimg"></span>';
                return `
                    <li class="wo-result">
                        ${img}
                        <div class="wo-result-main">
                            <div>${esc(item.Name)}</div>
                            <div class="wo-result-sub">${isSeries ? 'Series' : 'Film'}${item.ProductionYear ? ' · ' + item.ProductionYear : ''}</div>
                        </div>
                        <button is="emby-button" type="button" class="raised" data-library="${i}">
                            <span>${isSeries ? 'Choose episodes' : 'Add'}</span>
                        </button>
                    </li>`;
            }).join('')
            : '<li class="wo-hint">Nothing in your library matches that. Try the Metadata providers tab.</li>';
    }

    async function onLibraryResultClick(e) {
        const button = e.target.closest('[data-library]');
        if (!button) {
            return;
        }
        const item = libraryItems[Number(button.dataset.library)];

        if (item.Type !== 'Series') {
            addEntries([{ type: 'movie', title: item.Name, year: item.ProductionYear, providerIds: keptIds(item.ProviderIds) }]);
            return;
        }

        const seasons = ((await getJson(`Shows/${item.Id}/Seasons`)).Items || [])
            .filter((s) => s.IndexNumber !== null && s.IndexNumber !== undefined)
            .map((s) => ({ number: s.IndexNumber, label: s.Name || `Season ${s.IndexNumber}`, id: s.Id }));

        if (!seasons.length) {
            $('#woLibraryPicker').innerHTML = '<p class="wo-hint">This series has no seasons in your library yet.</p>';
            return;
        }

        renderSeriesPicker($('#woLibraryPicker'), {
            name: item.Name,
            year: item.ProductionYear,
            providerIds: item.ProviderIds
        }, {
            seasons,
            loadEpisodes: async (number) => {
                const season = seasons.find((s) => s.number === number);
                const episodes = (await getJson(`Shows/${item.Id}/Episodes`, { seasonId: season.id })).Items || [];
                return episodes
                    .filter((ep) => ep.IndexNumber !== null && ep.IndexNumber !== undefined)
                    .map((ep) => ({ number: ep.IndexNumber, label: `E${pad(ep.IndexNumber)} · ${ep.Name || ''}` }));
            }
        });
    }

    // ----------------------------------------------------- metadata providers

    let providerResults = [];

    async function searchProviders(e) {
        e.preventDefault();
        const type = $('#woProviderType').value;
        const name = $('#woProviderName').value.trim();
        const results = $('#woProviderResults');
        $('#woProviderPicker').innerHTML = '';
        if (!name) {
            return;
        }

        results.innerHTML = '<li class="wo-hint">Searching…</li>';
        try {
            providerResults = await sendJson('POST', `Items/RemoteSearch/${type}`, {
                SearchInfo: { Name: name, Year: num($('#woProviderYear').value) ?? null },
                IncludeDisabledProviders: false
            }) || [];
        } catch {
            results.innerHTML = '<li class="wo-errors">The metadata providers couldn\'t be searched. Check your server can reach them.</li>';
            return;
        }

        results.innerHTML = providerResults.length
            ? providerResults.map((r, i) => {
                const img = r.ImageUrl ? `<img loading="lazy" alt="" referrerpolicy="no-referrer" src="${esc(r.ImageUrl)}">` : '<span class="wo-noimg"></span>';
                const ids = Object.entries(keptIds(r.ProviderIds)).map(([k, v]) => `${k} ${v}`).join(' · ');
                return `
                    <li class="wo-result">
                        ${img}
                        <div class="wo-result-main">
                            <div>${esc(r.Name)}${r.ProductionYear ? ` (${r.ProductionYear})` : ''}</div>
                            <div class="wo-result-sub">${esc(r.SearchProviderName || '')}${ids ? ' · ' + esc(ids) : ''}</div>
                        </div>
                        <button is="emby-button" type="button" class="raised" data-provider="${i}">
                            <span>${type === 'Series' ? 'Choose episodes' : 'Add'}</span>
                        </button>
                    </li>`;
            }).join('')
            : '<li class="wo-hint">No results. Try a different spelling, or leave the year empty.</li>';
        results.dataset.type = type;
    }

    function onProviderResultClick(e) {
        const button = e.target.closest('[data-provider]');
        if (!button) {
            return;
        }
        const r = providerResults[Number(button.dataset.provider)];
        if ($('#woProviderResults').dataset.type === 'Series') {
            // Episode lists aren't available for series outside the library, so numbers are typed in.
            renderSeriesPicker($('#woProviderPicker'), { name: r.Name, year: r.ProductionYear, providerIds: r.ProviderIds }, null);
        } else {
            addEntries([{ type: 'movie', title: r.Name, year: r.ProductionYear, providerIds: keptIds(r.ProviderIds) }]);
        }
    }

    // ------------------------------------------------------------ collections

    async function loadCollections() {
        collectionsLoaded = true;
        const select = $('#woCollection');
        const response = await getJson('Items', { IncludeItemTypes: 'BoxSet', Recursive: true, SortBy: 'SortName', EnableTotalRecordCount: false });
        const items = response.Items || [];
        select.innerHTML = items.map((c) => `<option value="${c.Id}">${esc(c.Name)}</option>`).join('');
        $('#woAddCollection').disabled = items.length === 0;
        $('#woCollectionInfo').textContent = items.length ? '' : 'You don\'t have any collections yet.';
    }

    async function addCollection() {
        const id = $('#woCollection').value;
        if (!id) {
            return;
        }
        const response = await getJson('Items', {
            ParentId: id,
            SortBy: 'PremiereDate,ProductionYear,SortName',
            SortOrder: 'Ascending',
            Fields: 'ProviderIds,ProductionYear'
        });
        const items = response.Items || [];
        const films = items.filter((i) => i.Type === 'Movie');
        const skipped = items.length - films.length;

        if (films.length) {
            addEntries(films.map((f) => ({ type: 'movie', title: f.Name, year: f.ProductionYear, providerIds: keptIds(f.ProviderIds) })));
        }
        $('#woCollectionInfo').textContent = films.length === 0
            ? 'That collection has no films in it.'
            : skipped ? `Skipped ${skipped} show${skipped === 1 ? '' : 's'}; add those from Your library to choose seasons.` : '';
    }

    // ----------------------------------------------------------------- manual

    function showManualFields() {
        const type = $('#woManualType').value;
        view.querySelectorAll('#woManualForm [data-for]').forEach((el) => {
            el.hidden = !el.dataset.for.split(' ').includes(type);
        });
        $('#woManualTitle').setAttribute('label', type === 'movie' ? 'Film title' : 'Series name');
        const label = $('#woManualTitle').closest('.inputContainer')?.querySelector('label');
        if (label) {
            label.textContent = type === 'movie' ? 'Film title' : 'Series name';
        }
    }

    function addManual(e) {
        e.preventDefault();
        const type = $('#woManualType').value;
        const title = $('#woManualTitle').value.trim();
        const providerIds = keptIds({ Tmdb: $('#woManualTmdb').value.trim(), Imdb: $('#woManualImdb').value.trim(), Tvdb: $('#woManualTvdb').value.trim() });
        const entry = { type, year: num($('#woManualYear').value), providerIds };
        const errors = [];

        if (!title && !Object.keys(providerIds).length) {
            errors.push(type === 'movie' ? 'Enter a film title or an ID.' : 'Enter a series name or an ID.');
        }
        if (type === 'movie') {
            entry.title = title;
        } else {
            entry.series = title;
            entry.season = num($('#woManualSeason').value);
            if (entry.season === undefined) {
                errors.push('Enter a season number.');
            }
        }
        if (type === 'episodes') {
            entry.from = num($('#woManualFrom').value);
            entry.to = num($('#woManualTo').value);
            if (entry.from === undefined || entry.to === undefined) {
                errors.push('Enter the first and last episode.');
            } else if (entry.from > entry.to) {
                errors.push('The first episode comes after the last one.');
            }
        }
        if (type === 'episode') {
            entry.episode = num($('#woManualEpisode').value);
            if (entry.episode === undefined) {
                errors.push('Enter an episode number.');
            }
        }

        $('#woManualErrors').textContent = errors.join('\n');
        if (errors.length) {
            return;
        }
        addEntries([entry]);
        for (const id of ['#woManualEpisode', '#woManualFrom', '#woManualTo']) {
            $(id).value = '';
        }
    }

    // ----------------------------------------------------------------- wiring

    $('#woSettingsForm').addEventListener('submit', saveSettings);
    $('#woImport').addEventListener('click', importOrder);
    $('#woImportLink').addEventListener('click', importLink);
    $('#woImportUrl').addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            e.preventDefault();
            importLink();
        }
    });
    $('#woReadyMade').addEventListener('click', onReadyMadeClick);
    $('#woFilter').addEventListener('input', applyFilter);
    wireDropZone();
    rememberSections();
    $('#woRefresh').addEventListener('click', refreshAll);
    $('#woOrders').addEventListener('click', onOrderListClick);
    $('#woNew').addEventListener('click', () => openBuilder(null));

    $('#woBack').addEventListener('click', closeBuilder);
    $('#woCancel').addEventListener('click', closeBuilder);
    $('#woSave').addEventListener('click', saveOrder);
    $('#woName').addEventListener('input', onNameInput);
    $('#woId').addEventListener('input', () => {
        idTouched = true;
        markChanged(false);
    });
    $('#woDescription').addEventListener('input', () => markChanged(false));
    $('#woAuthor').addEventListener('input', () => markChanged(false));
    $('#woVersion').addEventListener('input', () => markChanged(false));
    $('#woSource').addEventListener('input', () => markChanged(false));
    $('#woImageItem').addEventListener('change', onImageItemChange);
    $('#woAddSection').addEventListener('click', () => ($('#woSectionForm').hidden ? openSectionForm() : closeSectionForm(true)));
    $('#woSectionCancel').addEventListener('click', () => closeSectionForm(true));
    $('#woSectionForm').addEventListener('submit', addSection);
    $('#woSectionForm').addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            e.preventDefault();
            closeSectionForm(true);
        }
    });
    $('#woSectionChips').addEventListener('click', (e) => {
        const chip = e.target.closest('[data-section-go]');
        if (chip) {
            showSectionInList(Number(chip.dataset.sectionGo), true);
        }
    });
    $('#woImageUrl').addEventListener('input', () => markChanged(false));
    $('#woImageUploadButton').addEventListener('click', () => $('#woImageFile').click());
    $('#woImageFile').addEventListener('change', () => uploadImage($('#woImageFile').files[0]));
    $('#woImageRemove').addEventListener('click', () => imageAction('DELETE', 'Image'));
    $('#woImageRefresh').addEventListener('click', () => imageAction('POST', 'Image/Refresh'));

    const entries = $('#woEntries');
    entries.addEventListener('click', onEntriesClick);
    entries.addEventListener('input', onNoteInput);
    entries.addEventListener('change', onOptionalChange);
    entries.addEventListener('mousedown', onPointerDown);
    entries.addEventListener('touchstart', onPointerDown, { passive: true });
    entries.addEventListener('dragstart', onDragStart);
    entries.addEventListener('dragover', onDragOver);
    entries.addEventListener('drop', onDrop);
    entries.addEventListener('dragend', onDragEnd);

    view.querySelectorAll('.wo-tab').forEach((tab) => tab.addEventListener('click', () => selectTab(tab.dataset.tab)));
    $('#woLibrarySearch').addEventListener('input', onLibrarySearchInput);
    $('#woLibraryResults').addEventListener('click', onLibraryResultClick);
    $('#woProviderForm').addEventListener('submit', searchProviders);
    $('#woProviderResults').addEventListener('click', onProviderResultClick);
    $('#woAddCollection').addEventListener('click', addCollection);
    $('#woManualType').addEventListener('change', showManualFields);
    $('#woManualForm').addEventListener('submit', addManual);
    showManualFields();

    view.addEventListener('viewshow', async () => {
        showView(false);
        Dashboard.showLoadingMsg();
        try {
            await Promise.all([loadOrders(), loadSettings(), loadWebClientStatus()]);
        } finally {
            Dashboard.hideLoadingMsg();
        }
    });
}
