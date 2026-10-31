/*
 * Watch Order: user page for jellyfin-web.
 *
 * Added to the web app by the File Transformation plugin (see ScriptInjector.cs). Puts a button in the
 * top bar that opens a "Watch Orders" page inside the app (at #/watchorders, like a library): every order
 * the user can watch, their progress, and play buttons that start the next unwatched item and queue the
 * rest of the order after it.
 *
 * It only uses the public ApiClient global and DOM, so it works with both the modern and legacy layouts.
 */
(function () {
    'use strict';

    if (window.__watchOrderClient) {
        return;
    }
    window.__watchOrderClient = true;

    const PREFIX = 'wo-u';
    const QUEUE_LIMIT = 50;
    const SUMMARY_TTL_MS = 60 * 1000;
    const ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', '\'': '&#39;' };
    const esc = (text) => String(text ?? '').replace(/[&<>"']/g, (c) => ESCAPES[c]);
    const pad = (n) => String(n ?? '?').padStart(2, '0');

    let summaries = null;        // [] once loaded for the current user
    let buttonLocation = 'LibraryBar'; // LibraryBar | TopBarIcon | Both (admin setting)
    let libraryBarPosition = 'End';    // End | AfterFavourites (admin setting)
    let showHomeRow = true;      // admin setting
    let autoplayMode = 'OptOut'; // OptOut | OptIn | Off (admin setting)
    let autoplayFollow = false;  // whether autoplay follows this user's orders
    let homeItems = new Map();   // next item details for the home row, keyed by item id
    let summariesUser = null;
    let summariesAt = 0;
    let loadingSummaries = null;
    let view = null;             // the Watch Orders page content, while it is on screen
    let viewRoute = null;        // the route it is showing ('' for the list, or an order id)
    let currentOrder = null;     // { progress, items: Map(itemId -> item) }
    let hideWatched = false;

    // ------------------------------------------------------------------- api

    const api = () => window.ApiClient;
    const userId = () => {
        try {
            return api()?.accessToken?.() ? api().getCurrentUserId() : null;
        } catch {
            return null;
        }
    };
    const url = (path, params) => api().getUrl(path, params);
    const getJson = (path, params) => api().getJSON(url(path, params));
    const send = (type, path, params) => api().ajax({ type, url: url(path, params) });
    const postJson = (path, params) => api().ajax({ type: 'POST', url: url(path, params), dataType: 'json' });

    async function loadSummaries(force) {
        const user = userId();
        if (!user) {
            summaries = null;
            return null;
        }
        const fresh = summariesUser === user && Date.now() - summariesAt < SUMMARY_TTL_MS;
        if (!force && fresh) {
            return summaries;
        }
        if (loadingSummaries) {
            return loadingSummaries;
        }
        loadingSummaries = Promise.all([
            getJson('WatchOrder/Progress'),
            getJson('WatchOrder/ClientSettings').catch(() => null)
        ])
            .then(async ([list, settings]) => {
                buttonLocation = settings?.ButtonLocation || 'LibraryBar';
                libraryBarPosition = settings?.LibraryBarPosition === 'AfterFavourites' ? 'AfterFavourites' : 'End';
                showHomeRow = settings?.ShowHomeRow !== false;
                autoplayMode = settings?.AutoplayFollowMode || 'OptOut';
                autoplayFollow = !!settings?.AutoplayFollow;
                const loaded = Array.isArray(list) ? list : [];
                const ids = showHomeRow ? loaded.filter((o) => o.OnHomeScreen && o.Next?.ItemId).map((o) => o.Next.ItemId) : [];
                homeItems = ids.length ? await loadItems(ids).catch(() => new Map()) : new Map();
                summaries = loaded;
                summariesUser = user;
                summariesAt = Date.now();
                return summaries;
            })
            .catch(() => {
                // Plugin turned off or the server is unreachable: just don't show the button.
                summaries = [];
                summariesUser = user;
                summariesAt = Date.now();
                return summaries;
            })
            .finally(() => {
                loadingSummaries = null;
                scheduleEnsure();
            });
        return loadingSummaries;
    }

    async function loadItems(ids) {
        const items = new Map();
        const unique = [...new Set(ids)];
        for (let i = 0; i < unique.length; i += 60) {
            const batch = unique.slice(i, i + 60);
            const result = await getJson('Items', {
                userId: userId(),
                ids: batch.join(','),
                EnableUserData: true,
                EnableImageTypes: 'Primary,Thumb,Backdrop',
                ImageTypeLimit: 1,
                Fields: 'PrimaryImageAspectRatio'
            });
            for (const item of result.Items || []) {
                items.set(normaliseId(item.Id), item);
            }
        }
        return items;
    }

    const normaliseId = (id) => (id ? String(id).replace(/-/g, '').toLowerCase() : null);

    // ---------------------------------------------------------------- styles

    function injectStyles() {
        if (document.getElementById(`${PREFIX}-styles`)) {
            return;
        }
        const style = document.createElement('style');
        style.id = `${PREFIX}-styles`;
        style.textContent = `
.${PREFIX}-btn { background: none; border: 0; color: inherit; cursor: pointer; width: 48px; height: 48px; border-radius: 50%;
  display: inline-flex; align-items: center; justify-content: center; padding: 0; flex: none; }
.${PREFIX}-btn:hover, .${PREFIX}-btn:focus-visible { background: rgba(128,128,128,.2); outline: none; }
.${PREFIX}-btn .material-icons { font-size: 24px; }

.${PREFIX}-view [hidden] { display: none !important; }
/* The page lives in the app's own (otherwise "page not found") page for #/watchorders; hide that message. */
html.${PREFIX}-route #fallbackPage > :not(.${PREFIX}-view) { display: none !important; }

/*
 * The page is built from Jellyfin's own elements (emby-button, emby-checkbox, paperList, listItem,
 * itemProgressBar, paper-icon-button-light), so the app's theme and any custom CSS style it like every
 * other page. The rules here only lay things out. The few colours are Jellyfin's theme variables, inside
 * :where() so they carry no weight and any theme rule for the same element wins.
 */
.${PREFIX}-view { padding-bottom: 4em; }
.${PREFIX}-bar { display: flex; align-items: center; gap: .25em; margin: .5em 0 1em; min-height: 42px; }
.${PREFIX}-bar h1 { font-size: 1.6em; margin: 0; flex: 1; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.${PREFIX}-bar .paper-icon-button-light { margin-left: -.4em; text-decoration: none; }
/* The list of orders uses the full width; a single order keeps a readable width, centred, with its heading above it. */
.${PREFIX}-view:not(.${PREFIX}-wide) .${PREFIX}-body,
.${PREFIX}-view:not(.${PREFIX}-wide) .${PREFIX}-bar { max-width: 72em; margin-left: auto; margin-right: auto; }
:where(.${PREFIX}-secondary, .${PREFIX}-desc, .${PREFIX}-count) { color: var(--jf-palette-text-secondary, inherit); }
.${PREFIX}-error { border: 1px solid; border-radius: var(--jf-card-borderRadius, .3em); padding: 1em; }
:where(.${PREFIX}-error) { border-color: var(--jf-palette-error-main, #f0645a); }
.${PREFIX}-empty { padding: 3em 1em; text-align: center; }

.${PREFIX}-autoplay { margin: 0 0 1.5em; max-width: 60em; }
.${PREFIX}-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(18.5em, 100%), 1fr)); gap: 1em; }
.${PREFIX}-card .${PREFIX}-actions .emby-button { padding-left: .9em; padding-right: .9em; }
.${PREFIX}-grid > .${PREFIX}-card { margin: 0; padding: 1em; display: flex; flex-direction: column; gap: .6em; }
.${PREFIX}-card h2 { font-size: 1.1em; margin: 0; }
.${PREFIX}-desc { margin: 0; font-size: .9em; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
.${PREFIX}-progress.itemProgressBar { position: relative; height: 6px; border-radius: 3px; overflow: hidden; }
:where(.${PREFIX}-progress) { background: rgba(128,128,128,.3); }
.${PREFIX}-progress > .itemProgressBarForeground { position: absolute; left: 0; top: 0; bottom: 0; height: 100%; }
.${PREFIX}-count { font-size: .85em; }
.${PREFIX}-next { font-size: .9em; }
.${PREFIX}-next strong { font-weight: 600; }
.${PREFIX}-done .material-icons { font-size: 1.2em; vertical-align: -.25em; margin-right: .2em; }
.${PREFIX}-actions { display: flex; gap: .5em; flex-wrap: wrap; margin-top: auto; }
/* On a card: the two main buttons share the card's width equally, with the home screen toggle underneath at full width. */
.${PREFIX}-card .${PREFIX}-actions { display: grid; grid-template-columns: 1fr 1fr; }
.${PREFIX}-card .${PREFIX}-actions > .emby-button { justify-content: center; min-width: 0; }
.${PREFIX}-card .${PREFIX}-actions > .${PREFIX}-card-home,
.${PREFIX}-card .${PREFIX}-actions > :only-child { grid-column: 1 / -1; }
.${PREFIX}-view .emby-button { margin: 0; text-decoration: none; }
.${PREFIX}-view .emby-button > .material-icons { margin-right: .3em; }

.${PREFIX}-head { display: flex; flex-direction: column; gap: .75em; margin-bottom: 1.25em; }
.${PREFIX}-choice { margin: 0; padding: 1em; display: flex; flex-direction: column; gap: .75em; }
.${PREFIX}-choice p { margin: 0; }
.${PREFIX}-toolbar { display: flex; gap: .75em; align-items: center; flex-wrap: wrap; }
.${PREFIX}-toolbar > .checkboxContainer { margin: 0 0 0 .25em; }
.${PREFIX}-toolbar .emby-checkbox-label { width: auto; }
.${PREFIX}-playlist { display: inline-flex; align-items: center; gap: .75em; flex-wrap: wrap; }
.${PREFIX}-playlist > .checkboxContainer { margin: 0 0 0 .25em; }
.${PREFIX}-playlist-link { text-decoration: underline; }
/* "Part of a watch order" on item pages. */
.${PREFIX}-itemorders-list { max-width: 72em; margin: .5em 0 0; }
.listItem.${PREFIX}-itemorder { flex-wrap: wrap; gap: .5em 1em; align-items: center; padding-right: 1em; }
.${PREFIX}-itemorder .listItemBody { flex: 1 1 22em; min-width: 0; }
.${PREFIX}-itemorder .listItemBodyText a { color: inherit; }
.${PREFIX}-itemorder .listItemBodyText.secondary a { text-decoration: underline; }
.${PREFIX}-itemorder-name { font-weight: 600; text-decoration: none; }
.${PREFIX}-itemorder .${PREFIX}-progress { max-width: 24em; margin-top: .5em; }
.${PREFIX}-itemorder-actions { display: flex; gap: .5em; flex-wrap: wrap; }
.${PREFIX}-itemorder-actions > .emby-button { margin: 0; }
@media (max-width: 40em) { .${PREFIX}-itemorder > .listItemIcon { display: none; } }

.${PREFIX}-steps { list-style: none; margin: 0; padding: 0; }
.${PREFIX}-steps > .${PREFIX}-step { display: flex; align-items: center; gap: .85em; padding: .6em .4em; cursor: default; overflow: visible; }
:where(.${PREFIX}-step.${PREFIX}-is-next) { background: var(--jf-palette-action-selected, rgba(128,128,128,.16));
  box-shadow: inset 3px 0 0 var(--jf-palette-primary-main, #00a4dc); }
.${PREFIX}-step.${PREFIX}-is-missing, .${PREFIX}-step.${PREFIX}-is-skipped { opacity: .55; }
.${PREFIX}-tag { display: inline-block; border: 1px solid currentColor; border-radius: 999px; padding: 0 .5em; font-size: .85em; line-height: 1.4; margin-right: .2em; }
.${PREFIX}-pos { width: 2em; text-align: right; font-variant-numeric: tabular-nums; flex: none; }
.${PREFIX}-thumb { position: relative; width: 8.5em; aspect-ratio: 16 / 9; border-radius: var(--jf-card-borderRadius, 5px); overflow: hidden; flex: none;
  background: rgba(128,128,128,.2) center / cover no-repeat; }
.${PREFIX}-thumb > .itemProgressBar { position: absolute; left: 0; right: 0; bottom: 0; height: 4px; }
.${PREFIX}-thumb > .itemProgressBar > .itemProgressBarForeground { position: absolute; left: 0; top: 0; height: 100%; }
.${PREFIX}-step > .${PREFIX}-step-main { flex: 1; min-width: 0; padding: 0; }
.${PREFIX}-step-title { font-weight: 600; overflow-wrap: anywhere; }
.${PREFIX}-step-title a { color: inherit; text-decoration: none; }
.${PREFIX}-step-title a:hover, .${PREFIX}-step-title a:focus-visible { text-decoration: underline; }
.${PREFIX}-step-sub { font-size: .87em; }
.${PREFIX}-note { font-size: .85em; font-style: italic; margin-top: .15em; }
.${PREFIX}-step-actions { display: flex; gap: .1em; flex: none; }
.${PREFIX}-sr { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }

/* home row */
.${PREFIX}-orderline .material-icons { font-size: 1.1em; vertical-align: -.2em; margin-right: .25em; opacity: .8; }
.${PREFIX}-toast { position: fixed; left: 50%; bottom: 1.5em; transform: translateX(-50%); z-index: 1400; display: flex; gap: 1em; align-items: center;
  max-width: calc(100vw - 2em); box-sizing: border-box; padding: .8em 1em; border-radius: .4em; background: var(--jf-palette-SnackbarContent-bg, #303030); color: var(--jf-palette-SnackbarContent-color, #fff);
  box-shadow: 0 .3em 1em rgba(0,0,0,.4); font-size: .95em; }
.${PREFIX}-toast-action { background: none; border: 0; color: var(--jf-palette-primary-main, #00a4dc); font: inherit; font-weight: 600; cursor: pointer; padding: .3em .5em; border-radius: .3em; }
.${PREFIX}-toast-action:hover, .${PREFIX}-toast-action:focus-visible { background: rgba(255,255,255,.1); outline: none; }
.${PREFIX}-homerow { margin-bottom: 1.2em; }
.${PREFIX}-hrow { display: flex; gap: 1.1em; overflow-x: auto; padding-bottom: .6em; scroll-snap-type: x proximity; scroll-padding-inline: 3.3%; scrollbar-width: thin; }
.${PREFIX}-hcard { position: relative; flex: none; width: clamp(15em, 22vw, 21em); scroll-snap-align: start; }
.${PREFIX}-hcard-main { display: flex; flex-direction: column; gap: .3em; width: 100%; padding: 0; border: 0; background: none; color: inherit;
  font: inherit; text-align: left; cursor: pointer; }
.${PREFIX}-hcard-main:focus-visible { outline: none; }
.${PREFIX}-hcard-main:focus-visible .${PREFIX}-hcard-img { box-shadow: 0 0 0 3px var(--jf-palette-primary-main, #00a4dc); }
.${PREFIX}-hcard-img { position: relative; display: block; width: 100%; aspect-ratio: 16 / 9; border-radius: .3em; overflow: hidden;
  background: rgba(128,128,128,.25) center / cover no-repeat; }
.${PREFIX}-hcard-bar { position: absolute; left: 0; right: 0; bottom: 0; height: 5px; background: rgba(0,0,0,.55); }
.${PREFIX}-hcard-bar > span { display: block; height: 100%; background: var(--jf-palette-primary-main, #00a4dc); }
.${PREFIX}-hcard-title { margin-top: .2em; text-align: center; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.${PREFIX}-hcard-sub { font-size: .9em; opacity: .7; text-align: center; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.${PREFIX}-hcard-play, .${PREFIX}-hcard-remove { position: absolute; border: 0; cursor: pointer; display: inline-flex; align-items: center;
  justify-content: center; color: #fff; background: rgba(0,0,0,.6); opacity: 0; transition: opacity .15s; }
.${PREFIX}-hcard-play { width: 3.2em; height: 3.2em; border-radius: 50%; left: 50%; top: calc((100% - 3.2em) / 2 - 1.3em); transform: translate(-50%, 0);
  background: var(--jf-palette-primary-main, #00a4dc); }
.${PREFIX}-hcard-play .material-icons { font-size: 2em; }
.${PREFIX}-hcard-remove { width: 2.2em; height: 2.2em; border-radius: 50%; top: .4em; right: .4em; }
.${PREFIX}-hcard:hover .${PREFIX}-hcard-play, .${PREFIX}-hcard:hover .${PREFIX}-hcard-remove,
.${PREFIX}-hcard:focus-within .${PREFIX}-hcard-play, .${PREFIX}-hcard:focus-within .${PREFIX}-hcard-remove { opacity: 1; }
@media (hover: none) { .${PREFIX}-hcard-play, .${PREFIX}-hcard-remove { opacity: 1; } }
.${PREFIX}-hcard-removed { display: flex; flex-direction: column; justify-content: center; gap: .6em; padding: 1em;
  border: 1px dashed rgba(128,128,128,.5); border-radius: .3em; aspect-ratio: 16 / 9; box-sizing: border-box; height: auto; }

@media (max-width: 40em) {
  .${PREFIX}-thumb { width: 5.5em; }
  .${PREFIX}-pos { display: none; }
}`;
        document.head.appendChild(style);
    }

    // --------------------------------------------------------- header button

    let ensureScheduled = false;
    function scheduleEnsure() {
        if (ensureScheduled) {
            return;
        }
        ensureScheduled = true;
        requestAnimationFrame(() => {
            ensureScheduled = false;
            ensureButton();
            ensureHomeRow();
            ensureItemOrders();
            syncPage();
        });
    }

    function findButtonSlot() {
        // Modern layout (12.x default): MUI toolbar with the user menu button at the end.
        // Other toolbar buttons (SyncPlay, cast) also open menus, so take the last one, which is the user menu.
        const menuButtons = document.querySelectorAll('.MuiAppBar-root .MuiToolbar-root button[aria-haspopup="true"]');
        const userMenu = menuButtons[menuButtons.length - 1];
        const toolbar = userMenu?.closest('.MuiToolbar-root');
        if (toolbar) {
            let box = userMenu;
            while (box.parentElement && box.parentElement !== toolbar) {
                box = box.parentElement;
            }
            return { parent: toolbar, before: box };
        }

        // Legacy layout: .headerRight with the user button last.
        const headerRight = document.querySelector('.skinHeader .headerRight');
        if (headerRight) {
            const userButton = headerRight.querySelector('.headerUserButton');
            return { parent: headerRight, before: userButton && userButton.parentElement === headerRight ? userButton : null };
        }
        return null;
    }

    const LABEL = 'Watch Orders';
    const ENTRY_ATTR = 'data-wo-entry';

    function removeEntries() {
        document.querySelectorAll(`[${ENTRY_ATTR}]`).forEach((el) => el.remove());
    }

    function onEntryClick(e) {
        if (e.button || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) {
            return; // let the browser open it in a new tab or window
        }
        e.preventDefault();
        // Close the side menu first if the entry was in it.
        if (e.currentTarget.closest('.MuiDrawer-root')) {
            document.querySelector('.MuiDrawer-root .MuiBackdrop-root')?.click();
        }
        openPage();
    }

    /** Shows the entries as selected while the page is open, the way the app marks the current library. */
    function markEntries() {
        const active = parseRoute() !== null;
        document.querySelectorAll(`[${ENTRY_ATTR}]`).forEach((el) => {
            const kind = el.getAttribute(ENTRY_ATTR);
            const link = kind === 'menu' ? el.querySelector('a') || el : el;
            if (link.classList.contains(`${PREFIX}-active`) === active) {
                return;
            }
            link.classList.toggle(`${PREFIX}-active`, active);
            if (active) {
                link.setAttribute('aria-current', 'page');
            } else {
                link.removeAttribute('aria-current');
            }
            // Side menu items have the app's own selected styles; the library bar and icon use the theme colour.
            if (kind === 'menu') {
                link.classList.toggle(link.classList.contains('navMenuOption') ? 'navMenuOption-selected' : 'Mui-selected', active);
            } else {
                link.style.color = active ? 'var(--jf-palette-primary-main, #00a4dc)' : '';
            }
        });
    }

    /** Swaps a cloned element's icon for ours and its label for "Watch Orders". */
    function relabel(clone, iconHost, labelHost) {
        iconHost.innerHTML = '<span class="material-icons format_list_numbered" aria-hidden="true" style="font-size:inherit"></span>';
        labelHost.textContent = LABEL;
        clone.setAttribute(ENTRY_ATTR, '');
        clone.removeAttribute('id');
        clone.removeAttribute('aria-current');
        clone.classList.remove('Mui-selected', 'Mui-focusVisible', 'navMenuOption-selected');
        clone.classList.remove(`${PREFIX}-active`);
        clone.style.color = '';
        clone.setAttribute('href', ROUTE_HASH);
        clone.removeAttribute('role');
        clone.addEventListener('click', onEntryClick);
        return clone;
    }

    /**
     * Library bar (modern layout, wide screens): a copy of one of the library buttons, so it picks up the
     * app's own styling, added after the libraries. Returns false when the library bar isn't on screen.
     */
    function ensureLibraryBarEntry() {
        const favourites = document.querySelector('.MuiAppBar-root .MuiToolbar-root a[href="#/home?tab=1"]');
        const bar = favourites?.parentElement;
        if (!bar) {
            return false;
        }
        const existing = bar.querySelector(`[${ENTRY_ATTR}="bar"]`);
        if (existing) {
            positionLibraryBarEntry(bar, favourites, existing);
            return true;
        }
        // Copy an unselected library link (Favourites or after it), so it matches them. The server name button
        // before Favourites is styled differently (bold), so never copy that one.
        const links = [...bar.children];
        const template = links.slice(links.indexOf(favourites))
            .find((a) => a.matches('a.MuiButton-root.MuiButton-colorInherit')) || favourites;
        const clone = template.cloneNode(true);
        const icon = clone.querySelector('.MuiButton-startIcon');
        if (!icon) {
            return false;
        }
        [...clone.childNodes].filter((n) => n !== icon && !n.classList?.contains('MuiTouchRipple-root')).forEach((n) => n.remove());
        icon.after(document.createTextNode(''));
        relabel(clone, icon, icon.nextSibling).setAttribute(ENTRY_ATTR, 'bar');
        positionLibraryBarEntry(bar, favourites, clone);
        return true;
    }

    /**
     * Straight after Favourites, or after the last library (just before the "More" button when some libraries
     * are tucked into it). Jellyfin adds library links in stages, so this runs on every change, not just once.
     */
    function positionLibraryBarEntry(bar, favourites, entry) {
        if (libraryBarPosition === 'AfterFavourites') {
            if (favourites.nextElementSibling !== entry) {
                favourites.after(entry);
            }
            return;
        }
        const more = bar.querySelector('[aria-controls="user-view-overflow-menu"]');
        const others = [...bar.children].filter((el) => el !== entry);
        const lastLink = others[others.length - 1];
        if (more) {
            if (entry.nextElementSibling !== more) {
                bar.insertBefore(entry, more);
            }
        } else if (lastLink && lastLink.nextElementSibling !== entry) {
            lastLink.after(entry);
        }
    }

    /** Side menu (modern layout on narrow screens, and the legacy layout): an item under Favourites. */
    function ensureSideMenuEntry() {
        // Modern: MUI drawer list item linking to favourites. Legacy: .navMenuOption links in the main drawer.
        const modernFav = document.querySelector('.MuiDrawer-root a[href="#/home?tab=1"]');
        const legacyFav = !modernFav && document.querySelector('.mainDrawer a.navMenuOption[href*="home"][href*="tab=1"]');
        const fav = modernFav || legacyFav;
        if (!fav) {
            return;
        }
        const item = modernFav ? fav.closest('li') : fav;
        if (!item?.parentElement) {
            return;
        }
        // "End": last item of the drawer's Libraries list (modern layout); otherwise straight after Favourites.
        const librariesList = modernFav && libraryBarPosition === 'End' ? document.querySelector('.MuiDrawer-root [aria-labelledby="libraries-subheader"]') : null;
        const existing = document.querySelector(`[${ENTRY_ATTR}="menu"]`);
        if (existing) {
            if (librariesList && librariesList.lastElementChild !== existing) {
                librariesList.appendChild(existing);
            } else if (!librariesList && item.nextElementSibling !== existing) {
                item.after(existing);
            }
            return;
        }
        const clone = item.cloneNode(true);
        const link = modernFav ? clone.querySelector('a') : clone;
        const icon = modernFav ? clone.querySelector('.MuiListItemIcon-root') : clone.querySelector('.navMenuOptionIcon');
        const text = modernFav ? clone.querySelector('.MuiListItemText-primary, .MuiTypography-root') : clone.querySelector('.navMenuOptionText');
        if (!link || !icon || !text) {
            return;
        }
        relabel(link, icon, text);
        clone.setAttribute(ENTRY_ATTR, 'menu');
        if (librariesList) {
            librariesList.appendChild(clone);
        } else {
            item.after(clone);
        }
    }

    /** Top bar: the round icon button next to the user menu. */
    function ensureIconButton() {
        const slot = findButtonSlot();
        if (!slot) {
            return;
        }
        let button = document.getElementById(`${PREFIX}-open`);
        if (button && button.parentElement === slot.parent) {
            return;
        }
        injectStyles();
        if (!button) {
            button = document.createElement('button');
            button.id = `${PREFIX}-open`;
            button.type = 'button';
            button.className = `${PREFIX}-btn`;
            button.title = LABEL;
            button.setAttribute('aria-label', LABEL);
            button.setAttribute(ENTRY_ATTR, 'icon');
            button.innerHTML = '<span class="material-icons format_list_numbered" aria-hidden="true"></span>';
            button.addEventListener('click', () => openPage());
        }
        slot.parent.insertBefore(button, slot.before);
    }

    function ensureButton() {
        const user = userId();
        if (!user) {
            removeEntries();
            return;
        }
        if (summariesUser !== user || Date.now() - summariesAt > SUMMARY_TTL_MS) {
            loadSummaries(false);
        }
        if (!summaries || summaries.length === 0) {
            removeEntries();
            return;
        }

        const wantIcon = buttonLocation === 'TopBarIcon' || buttonLocation === 'Both';
        const wantLibrary = buttonLocation !== 'TopBarIcon';

        let onLibraryBar = false;
        if (wantLibrary) {
            onLibraryBar = ensureLibraryBarEntry();
            ensureSideMenuEntry();
        } else {
            document.querySelectorAll(`[${ENTRY_ATTR}="bar"], [${ENTRY_ATTR}="menu"]`).forEach((el) => el.remove());
        }

        // When the library bar isn't showing (phones, narrow windows, the legacy layout), keep the icon
        // as well, so the page is always one tap away and not only inside the side menu.
        if (wantIcon || !onLibraryBar) {
            ensureIconButton();
        } else {
            document.getElementById(`${PREFIX}-open`)?.remove();
        }
        markEntries();
    }

    // -------------------------------------------------------------- home row

    const HOME_ROW_ID = `${PREFIX}-home`;
    const HOME_TITLE = 'Up Next in Your Watch Orders';
    let homeRowOrders = [];      // orders shown in the row, in card order
    let homeRowItems = [];       // their next items, in the same order

    /** The home sections container of the page currently on screen (old pages stay in the DOM, hidden). */
    function visibleHomeContainer() {
        return [...document.querySelectorAll('.homeSectionsContainer')].find((el) => el.offsetParent !== null) || null;
    }

    function homeRowSignature(list) {
        return list.map((o) => `${o.Id}:${o.Next?.Position}:${o.WatchedCount}`).join('|');
    }

    /**
     * Jellyfin's own card renderer, borrowed from the Next Up row (or Continue Watching if Next Up is turned off),
     * so the cards are identical to native ones: same image choice (including the user's "episode images"
     * setting), same text and links, and the same hover buttons for watched, favourite and the ⋯ menu.
     */
    function findNativeRenderer(container) {
        const containers = [...container.querySelectorAll('.itemsContainer')]
            .filter((el) => typeof el.getItemsHtml === 'function' && !el.closest(`#${HOME_ROW_ID}`));
        const nextUp = containers.find((el) => el.closest('.verticalSection')?.querySelector('a[href*="nextup" i]'));
        const source = nextUp || containers.find((el) => (el.getAttribute('data-monitor') || '').includes('videoplayback'));
        return source ? { getItemsHtml: source.getItemsHtml, overflow: source.classList.contains('scrollSlider') } : null;
    }

    function ensureHomeRow() {
        const existing = document.getElementById(HOME_ROW_ID);
        const container = userId() && showHomeRow && summaries ? visibleHomeContainer() : null;
        const list = (summaries || []).filter((o) => o.OnHomeScreen && o.Next);

        if (!container || list.length === 0) {
            existing?.remove();
            return;
        }

        const signature = homeRowSignature(list);
        if (existing && existing.parentElement === container) {
            placeHomeRow(container, existing); // sections load one by one, so re-check where it belongs
            if (existing.dataset.signature !== signature) {
                existing.dataset.signature = signature;
                refreshHomeRow(existing);
            }
            return;
        }

        injectStyles();
        existing?.remove();
        const row = document.createElement('div');
        row.id = HOME_ROW_ID;
        row.className = `verticalSection ${PREFIX}-homerow`;
        row.dataset.signature = signature;
        // Capture phase, so our actions run before Jellyfin's card click handling (which would open the item).
        row.addEventListener('click', onHomeRowClick, true);

        const renderer = findNativeRenderer(container);
        const title = `
            <div class="sectionTitleContainer sectionTitleContainer-cards padded-left">
                <a is="emby-linkbutton" href="${ROUTE_HASH}" class="button-flat button-flat-mini sectionTitleTextButton" data-wo-home="all" title="Open Watch Orders">
                    <h2 class="sectionTitle sectionTitle-cards">${HOME_TITLE}</h2>
                    <span class="material-icons chevron_right" aria-hidden="true"></span>
                </a>
            </div>`;

        if (renderer) {
            row.dataset.native = 'true';
            const items = '<div is="emby-itemscontainer" class="itemsContainer focuscontainer-x %CLASSES%" data-monitor="videoplayback,markplayed"></div>';
            row.innerHTML = title + (renderer.overflow
                ? `<div is="emby-scroller" class="padded-top-focusscale padded-bottom-focusscale" data-centerfocus="true">${items.replace('%CLASSES%', 'scrollSlider')}</div>`
                : items.replace('%CLASSES%', 'padded-left padded-right vertical-wrap'));
            placeHomeRow(container, row);
            startNativeRow(row, renderer.getItemsHtml);
        } else {
            row.innerHTML = title + `<div class="${PREFIX}-hrow padded-left padded-right" role="list"></div>`;
            placeHomeRow(container, row);
            renderFallbackCards(row, list);
        }
    }

    function refreshHomeRow(row) {
        const itemsContainer = row.querySelector('.itemsContainer');
        if (row.dataset.native && typeof itemsContainer?.notifyRefreshNeeded === 'function') {
            itemsContainer.notifyRefreshNeeded(true);
        } else if (!row.dataset.native) {
            renderFallbackCards(row, (summaries || []).filter((o) => o.OnHomeScreen && o.Next));
        }
    }

    /** Hands the row to Jellyfin's items container once the browser has turned it into one. */
    function startNativeRow(row, getItemsHtml, attempt = 0) {
        const itemsContainer = row.querySelector('.itemsContainer');
        if (!itemsContainer || !row.isConnected) {
            return;
        }
        if (typeof itemsContainer.resume !== 'function') {
            if (attempt < 120) {
                requestAnimationFrame(() => startNativeRow(row, getItemsHtml, attempt + 1));
            }
            return;
        }
        // Whenever an items container is moved in the page (the row moving into place, or Jellyfin's scroller
        // arranging its contents), Jellyfin clears these, which would leave the row empty. Keep ours.
        keep(itemsContainer, 'fetchData', fetchHomeRowItems);
        keep(itemsContainer, 'getItemsHtml', getItemsHtml);
        keep(itemsContainer, 'parentContainer', row);
        itemsContainer.afterRefresh = () => decorateHomeCards(row);
        // Jellyfin calls fetchData again by itself after playback and when items are marked watched.
        itemsContainer.resume({ refresh: true });
    }

    function keep(element, name, value) {
        Object.defineProperty(element, name, {
            configurable: true,
            get: () => value,
            set: (next) => {
                if (next) {
                    value = next;
                }
            }
        });
    }

    /** Next item of every order on the home screen, in the same shape Jellyfin's own rows use. */
    async function fetchHomeRowItems() {
        // Jellyfin calls this after playback or a watched change, so always get fresh progress. (It also runs once
        // when the row is created, straight after a load; loadSummaries shares an in-flight request.)
        await loadSummaries(true);
        const list = (summaries || []).filter((o) => o.OnHomeScreen && o.Next?.ItemId);
        const row = document.getElementById(HOME_ROW_ID);
        if (row) {
            row.dataset.signature = homeRowSignature(list);
        }
        if (!list.length) {
            homeRowOrders = [];
            return { Items: [], TotalRecordCount: 0 };
        }
        const result = await getJson('Items', {
            userId: userId(),
            ids: [...new Set(list.map((o) => o.Next.ItemId))].join(','),
            Fields: 'PrimaryImageAspectRatio,DateCreated,Path,MediaSourceCount',
            EnableImageTypes: 'Primary,Backdrop,Thumb',
            ImageTypeLimit: 1,
            EnableUserData: true,
            EnableTotalRecordCount: false
        });
        const byId = new Map((result.Items || []).map((item) => [normaliseId(item.Id), item]));
        const shown = list.filter((o) => byId.has(normaliseId(o.Next.ItemId)));
        homeRowOrders = shown;
        homeRowItems = shown.map((o) => byId.get(normaliseId(o.Next.ItemId)));
        return { Items: shown.map((o) => byId.get(normaliseId(o.Next.ItemId))), TotalRecordCount: shown.length };
    }

    /** Adds the Watch Order parts to Jellyfin's cards: the order name and progress, and a remove button. */
    function decorateHomeCards(row) {
        const cards = [...row.querySelectorAll('.itemsContainer > .card')];
        cards.forEach((card, i) => {
            const order = homeRowOrders[i];
            if (!order || card.dataset.woOrder) {
                return;
            }
            card.dataset.woOrder = order.Id;

            // Films have no episode line, so Jellyfin leaves the second line empty. Show the year there, as
            // film cards elsewhere in Jellyfin do.
            const item = homeRowItems[i];
            const year = item?.ProductionYear || (item?.PremiereDate ? new Date(item.PremiereDate).getFullYear() : null);
            const emptyLine = [...card.querySelectorAll('.cardText')].find((el) => !el.textContent.replace(/\u00a0/g, '').trim());
            if (item?.Type === 'Movie' && year && emptyLine) {
                emptyLine.innerHTML = `<bdi>${esc(year)}</bdi>`;
            }

            // Text lines sit in .cardFooter for some card layouts and straight in .cardBox for others (home rows).
            const textLines = card.querySelectorAll('.cardText');
            const lastLine = textLines[textLines.length - 1];
            if (lastLine) {
                const line = document.createElement('div');
                line.className = lastLine.className.replace(/\bcardText-first\b/, '').trim();
                line.classList.add('cardText-secondary', `${PREFIX}-orderline`);
                // Same structure as Jellyfin's own lines (<bdi><a class="textActionButton">) so it lines up the same way.
                line.innerHTML = `<bdi><a href="${esc(routeHash(order.Id))}" class="textActionButton" data-wo-home="open" title="Open this watch order">`
                    + `<span class="material-icons format_list_numbered" aria-hidden="true"></span>`
                    + `${esc(order.Name)} · ${order.WatchedCount} of ${order.AvailableCount}</a></bdi>`;
                lastLine.after(line);
            }

            const buttons = card.querySelector('.cardOverlayButton-br');
            const menu = buttons?.querySelector('[data-action="menu"]');
            if (buttons) {
                const remove = document.createElement('button');
                remove.type = 'button';
                remove.className = (menu?.className || 'cardOverlayButton cardOverlayButton-hover paper-icon-button-light').replace(/\bitemAction\b/, '').trim();
                remove.dataset.woHome = 'remove';
                remove.title = 'Remove from home screen';
                remove.setAttribute('aria-label', `Remove ${order.Name} from your home screen`);
                remove.innerHTML = '<span class="material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover playlist_remove" aria-hidden="true"></span>';
                buttons.insertBefore(remove, menu || null);
            }

            // Name the play button for screen readers: it plays this item and queues the rest of the order.
            card.querySelectorAll('[data-action="resume"], [data-action="play"]').forEach((play) => {
                play.setAttribute('aria-label', `Play next in ${order.Name}`);
            });
        });
    }

    // Fallback when the home screen has neither Next Up nor Continue Watching to borrow cards from.
    function renderFallbackCards(row, list) {
        homeRowOrders = list;
        const strip = row.querySelector(`.${PREFIX}-hrow`);
        if (!strip) {
            return;
        }
        // Orders added since the last load (e.g. "Add to home screen") need their next item's details first.
        const missing = list.map((o) => o.Next.ItemId).filter((id) => id && !homeItems.has(normaliseId(id)));
        if (missing.length && !row.dataset.loadingItems) {
            row.dataset.loadingItems = 'true';
            loadItems(missing)
                .then((loaded) => loaded.forEach((item, id) => homeItems.set(id, item)))
                .catch(() => {})
                .finally(() => {
                    delete row.dataset.loadingItems;
                    renderFallbackCards(row, (summaries || []).filter((o) => o.OnHomeScreen && o.Next));
                });
        }
        strip.innerHTML = list.map((o) => {
            const item = homeItems.get(normaliseId(o.Next.ItemId));
            const img = thumbFor(item);
            const nextText = item ? stepText({ Step: o.Next }, item) : { title: o.Next.DisplayName || o.Next.Label, sub: '' };
            return `
                <div class="${PREFIX}-hcard" role="listitem" data-wo-order="${esc(o.Id)}">
                    <button type="button" class="${PREFIX}-hcard-main" data-wo-home="open" aria-label="${esc(o.Name)}: up next, ${esc(nextText.title)}. ${o.WatchedCount} of ${o.AvailableCount} watched.">
                        <span class="${PREFIX}-hcard-img" ${img ? `style="background-image:url('${esc(img)}')"` : ''}>
                            <span class="${PREFIX}-hcard-bar"><span style="width:${percent(o.WatchedCount, o.AvailableCount)}%"></span></span>
                        </span>
                        <span class="${PREFIX}-hcard-title">${esc(item?.Type === 'Episode' ? item.SeriesName : nextText.title)}</span>
                        <span class="${PREFIX}-hcard-sub">${esc(item?.Type === 'Episode' ? `S${item.ParentIndexNumber}:E${item.IndexNumber} - ${item.Name}` : item?.ProductionYear || '')}</span>
                        <span class="${PREFIX}-hcard-sub ${PREFIX}-orderline"><span class="material-icons format_list_numbered" aria-hidden="true"></span>${esc(o.Name)} · ${o.WatchedCount} of ${o.AvailableCount}</span>
                    </button>
                    <button type="button" class="${PREFIX}-hcard-play" data-action="resume" aria-label="Play next in ${esc(o.Name)}" title="Play">
                        <span class="material-icons play_arrow" aria-hidden="true"></span>
                    </button>
                    <button type="button" class="${PREFIX}-hcard-remove" data-wo-home="remove" aria-label="Remove ${esc(o.Name)} from your home screen" title="Remove from home screen">
                        <span class="material-icons playlist_remove" aria-hidden="true"></span>
                    </button>
                </div>`;
        }).join('');
    }

    /** Thumb, like Jellyfin's Next Up: the item's own thumb, else the series' thumb, else a backdrop, else primary. */
    function thumbFor(item) {
        if (!item) {
            return '';
        }
        const options = { maxWidth: 480, quality: 90 };
        if (item.ImageTags?.Thumb) {
            return api().getScaledImageUrl(item.Id, { ...options, type: 'Thumb', tag: item.ImageTags.Thumb });
        }
        if (item.ParentThumbItemId && item.ParentThumbImageTag) {
            return api().getScaledImageUrl(item.ParentThumbItemId, { ...options, type: 'Thumb', tag: item.ParentThumbImageTag });
        }
        if (item.BackdropImageTags?.length) {
            return api().getScaledImageUrl(item.Id, { ...options, type: 'Backdrop', tag: item.BackdropImageTags[0] });
        }
        return imageFor(item);
    }

    /**
     * Puts the row just after Continue Watching (or Next Up when there's nothing to resume); failing that,
     * just after My Media; failing that, at the top. Jellyfin loads sections one by one and keeps empty ones
     * hidden, so this runs again whenever the home screen changes and moves the row once its neighbours appear.
     */
    // Sections are shown by removing their "hide" class, which isn't a content change, so watch for that too.
    const watchedContainers = new WeakSet();
    function watchSectionVisibility(container) {
        if (watchedContainers.has(container)) {
            return;
        }
        watchedContainers.add(container);
        new MutationObserver((mutations) => {
            if (mutations.some((m) => m.target.parentElement === container)) {
                scheduleEnsure();
            }
        }).observe(container, { attributes: true, attributeFilter: ['class'], subtree: true });
    }

    function placeHomeRow(container, row) {
        watchSectionVisibility(container);
        const sections = [...container.children].filter((section) => section !== row
            && !section.classList.contains('hide') && section.offsetParent !== null);
        const isPlayback = (section) => section.querySelector('.itemsContainer[data-monitor*="videoplayback"]');
        const isLibraries = (section) => section.querySelector('.homeLibraryButtonContainer, [data-type="CollectionFolder"], [data-type="UserView"]');

        const anchor = sections.find(isPlayback) || sections.filter(isLibraries).pop();
        if (anchor) {
            if (anchor.nextElementSibling !== row) {
                anchor.after(row);
            }
        } else if (container.firstElementChild !== row) {
            container.prepend(row);
        }
    }

    /** The "Add to home screen" toggle on an order's card, spanning the width of the two buttons above it. */
    function homeButton(orderId, on) {
        return `<button is="emby-button" type="button" class="emby-button raised ${PREFIX}-card-home" data-wo="card-home" data-order="${esc(orderId)}"
            aria-pressed="${!!on}"><span class="material-icons ${on ? 'check' : 'add'}" aria-hidden="true"></span><span>${on ? 'On your home screen' : 'Add to home screen'}</span></button>`;
    }

    /** Adds an order to, or removes it from, the home screen straight from its card. */
    async function toggleCardHome(button) {
        const orderId = button.dataset.order;
        const summary = summaries?.find((o) => o.Id === orderId);
        const on = button.getAttribute('aria-pressed') === 'true';
        button.disabled = true;
        try {
            await setHomeState(orderId, on ? 'Hidden' : 'Shown');
            if (summary) {
                summary.OnHomeScreen = !on;
            }
            summariesAt = 0;
            const replacement = document.createElement('template');
            replacement.innerHTML = homeButton(orderId, !on).trim();
            const fresh = replacement.content.firstElementChild;
            button.replaceWith(fresh);
            fresh.focus();
        } catch {
            button.disabled = false;
        }
    }

    async function setHomeState(orderId, state) {
        await send('POST', `WatchOrder/Orders/${encodeURIComponent(orderId)}/HomeState`, { state });
        const summary = summaries?.find((o) => o.Id === orderId);
        if (summary) {
            summary.HomeState = state;
            summary.OnHomeScreen = state === 'Shown' ? !!summary.Next
                : state === 'Hidden' ? false
                : !!summary.Run?.Chosen && !!summary.Next;
        }
    }

    async function onHomeRowClick(e) {
        const card = e.target.closest('[data-wo-order]');
        const orderId = card?.dataset.woOrder;
        const action = e.target.closest('[data-wo-home]')?.dataset.woHome
            || (e.target.closest('[data-action="resume"], [data-action="play"]') && card ? 'play' : null);
        if (!action) {
            return; // everything else (image, titles, watched, favourite, ⋯) is Jellyfin's own
        }
        e.preventDefault();
        e.stopPropagation();

        if (action === 'all') {
            openPage();
        } else if (action === 'open' && orderId) {
            openPage(orderId);
        } else if (action === 'play' && orderId) {
            // Plays this item and queues the rest of the order, instead of Jellyfin playing just the one item.
            await playOrder(orderId);
        } else if (action === 'remove' && orderId) {
            const summary = summaries?.find((o) => o.Id === orderId);
            const previous = summary?.HomeState || 'Default';
            const name = summary?.Name || 'Watch order';
            await setHomeState(orderId, 'Hidden');
            ensureHomeRow();
            showToast(`Removed ${name} from your home screen.`, 'Undo', async () => {
                await setHomeState(orderId, previous === 'Hidden' ? 'Default' : previous);
                await loadSummaries(true);
                ensureHomeRow();
            });
        }
    }

    /** A small notice at the bottom of the screen, with an optional action button. */
    function showToast(message, actionLabel, onAction) {
        injectStyles();
        document.getElementById(`${PREFIX}-toast`)?.remove();
        const toast = document.createElement('div');
        toast.id = `${PREFIX}-toast`;
        toast.className = `${PREFIX}-toast`;
        toast.setAttribute('role', 'status');
        toast.innerHTML = `<span>${esc(message)}</span>${actionLabel ? `<button type="button" class="${PREFIX}-toast-action">${esc(actionLabel)}</button>` : ''}`;
        document.body.appendChild(toast);
        const timer = setTimeout(() => toast.remove(), 8000);
        toast.querySelector('button')?.addEventListener('click', async () => {
            clearTimeout(timer);
            toast.remove();
            await onAction();
        });
        toast.querySelector('button')?.focus();
    }

    // ------------------------------------------------------------------ page

    // The page has its own address, so the app treats it like any other page: the top bar stays, Back and
    // Forward work, and refreshing keeps you on it. The app has no route for it, so it shows its "page not
    // found" page there; the content goes inside that page in place of the message.
    const ROUTE_HASH = '#/watchorders';
    const ROUTE_PATTERN = /^#\/watchorders\/?(?:\?(.*))?$/i;

    function routeHash(orderId) {
        // Not "id": the app's top bar treats an "id" in the address as a library item and looks it up.
        return orderId ? `${ROUTE_HASH}?order=${encodeURIComponent(orderId)}` : ROUTE_HASH;
    }

    /** null when not on the page; '' for the list of orders; otherwise the order id. */
    function parseRoute() {
        const match = ROUTE_PATTERN.exec(location.hash || '');
        return match ? new URLSearchParams(match[1] || '').get('order') || '' : null;
    }

    function openPage(orderId) {
        const hash = routeHash(orderId);
        if (location.hash === hash) {
            syncPage();
        } else {
            location.hash = hash;
        }
    }

    function leavePage() {
        if (parseRoute() !== null) {
            location.hash = '#/home';
        }
    }

    /** The app's page element for the current address, once it has rendered it. */
    function hostPage() {
        const pages = [...document.querySelectorAll('#fallbackPage')];
        return pages[pages.length - 1] || null;
    }

    /** Mounts, updates or forgets the page to match the address. Runs on navigation and on page changes. */
    function syncPage() {
        const route = userId() ? parseRoute() : null;
        document.documentElement.classList.toggle(`${PREFIX}-route`, route !== null);
        if (view && !view.isConnected) {
            view = null;
            viewRoute = null;
            currentOrder = null;
        }
        if (route === null) {
            return;
        }
        const host = hostPage();
        if (!host) {
            return; // not rendered yet; the page observer calls this again
        }
        if (!view || view.parentElement !== host) {
            injectStyles();
            view?.remove();
            view = document.createElement('div');
            view.className = `${PREFIX}-view padded-left padded-right`;
            view.innerHTML = `
                <div class="${PREFIX}-bar">
                    <a href="${ROUTE_HASH}" is="paper-icon-button-light" class="paper-icon-button-light" data-wo="back" aria-label="All watch orders" title="All watch orders" hidden><span class="material-icons arrow_back" aria-hidden="true"></span></a>
                    <h1 id="${PREFIX}-title" tabindex="-1">Watch Orders</h1>
                </div>
                <div class="${PREFIX}-body" aria-live="polite"></div>`;
            view.addEventListener('click', onViewClick);
            view.addEventListener('change', onViewChange);
            host.appendChild(view);
            viewRoute = null;
        }
        if (viewRoute !== route) {
            const firstView = viewRoute === null;
            viewRoute = route;
            if (!firstView) {
                window.scrollTo(0, 0);
            }
            if (route) {
                showOrder(route);
            } else {
                showList();
            }
        }
    }

    function setTitle(title) {
        document.title = title;
        // Legacy layout: the title in the header.
        const headerTitle = document.querySelector('.skinHeader .pageTitle');
        if (headerTitle) {
            headerTitle.textContent = title;
        }
    }

    function setView(title, html, showBack) {
        const focusWasInside = view.contains(document.activeElement);
        view.querySelector(`#${PREFIX}-title`).textContent = title;
        view.querySelector('[data-wo="back"]').hidden = !showBack;
        view.classList.toggle(`${PREFIX}-wide`, !showBack);
        view.querySelector(`.${PREFIX}-body`).innerHTML = html;
        setTitle(title);
        // Replacing the content can remove the focused element; keep focus on the page.
        if (focusWasInside && !view.contains(document.activeElement)) {
            view.querySelector(`#${PREFIX}-title`).focus();
        }
    }

    function setLoading(title, showBack) {
        setView(title, `<p class="${PREFIX}-secondary">Loading…</p>`, showBack);
    }

    function setError(title, message, retry, showBack) {
        setView(title, `<div class="${PREFIX}-error"><p>${esc(message)}</p>
            <button is="emby-button" type="button" class="emby-button raised" data-wo="${retry}"><span>Try again</span></button></div>`, showBack);
    }

    /** True while the page is still showing `route` (the user may have moved on while something loaded). */
    const stillOn = (route) => view !== null && view.isConnected && viewRoute === route;

    const percent = (done, total) => (total ? Math.round((100 * done) / total) : 0);

    const timesText = (n) => (n === 1 ? 'once' : n === 2 ? 'twice' : `${n} times`);
    function formatDate(iso) {
        const date = new Date(iso);
        return Number.isNaN(date.getTime()) ? '' : date.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
    }

    /** Jellyfin's own progress bar (themes colour its fill), as an accessible progress bar. */
    function progressBar(done, total, label) {
        return `<div class="itemProgressBar ${PREFIX}-progress" role="progressbar" aria-label="${esc(label)}" aria-valuemin="0"`
            + ` aria-valuemax="${total}" aria-valuenow="${done}"><div class="itemProgressBarForeground" style="width:${percent(done, total)}%"></div></div>`;
    }

    // ------------------------------------------------------- item pages

    // "Watch orders" on a film's, episode's, season's or series' page: which orders it's in, where, and what's next.
    const ITEM_SECTION_ID = `${PREFIX}-itemorders`;
    const itemOrdersCache = new Map(); // item id -> { at, list } or { pending }

    function detailsItemId() {
        const match = /^#\/details\?(.*)$/.exec(location.hash || '');
        return match ? new URLSearchParams(match[1]).get('id') : null;
    }

    function ensureItemOrders() {
        const itemId = detailsItemId();
        const page = itemId ? [...document.querySelectorAll('.itemDetailPage')].find((p) => !p.classList.contains('hide')) : null;
        const container = page?.querySelector('.detailPageSecondaryContainer');
        const existing = document.getElementById(ITEM_SECTION_ID);
        if (!container || !userId()) {
            existing?.remove();
            return;
        }

        const key = normaliseId(itemId);
        const cached = itemOrdersCache.get(key);
        if (!cached || (!cached.pending && Date.now() - cached.at > 30000)) {
            itemOrdersCache.set(key, { pending: true });
            getJson(`WatchOrder/Items/${encodeURIComponent(itemId)}/Orders`)
                .then((list) => itemOrdersCache.set(key, { at: Date.now(), list: list || [] }))
                .catch(() => itemOrdersCache.set(key, { at: Date.now(), list: [] }))
                .finally(scheduleEnsure);
            if (existing && existing.dataset.item !== key) {
                existing.remove();
            }
            return;
        }
        if (cached.pending) {
            return;
        }
        if (!cached.list.length) {
            existing?.remove();
            return;
        }

        const signature = key + JSON.stringify(cached.list);
        if (existing && existing.parentElement === container && existing.dataset.signature === signature) {
            placeItemOrders(container, existing);
            return;
        }

        injectStyles();
        existing?.remove();
        const section = document.createElement('div');
        section.id = ITEM_SECTION_ID;
        section.className = `verticalSection detailVerticalSection ${PREFIX}-itemorders`;
        section.dataset.item = key;
        section.dataset.signature = signature;
        section.innerHTML = itemOrdersHtml(cached.list);
        section.addEventListener('click', onItemOrdersClick);
        placeItemOrders(container, section);
    }

    /** Just below Cast & Crew (and Guest Stars, on an episode), above specials, collections and "More like this". */
    function placeItemOrders(container, section) {
        const anchor = container.querySelector(':scope > #guestCastCollapsible') || container.querySelector(':scope > #castCollapsible');
        if (anchor) {
            if (section.previousElementSibling !== anchor) {
                anchor.after(section);
            }
        } else if (section.parentElement !== container) {
            container.appendChild(section);
        }
    }

    function itemOrdersHtml(list) {
        const serverId = encodeURIComponent(api().serverId());
        const link = (step) => `<a class="button-link" href="#/details?id=${encodeURIComponent(normaliseId(step.ItemId))}&serverId=${serverId}">${esc(step.DisplayName || step.Label)}</a>`;
        const rows = list.map((o) => {
            const single = !!o.After || o.Count === 1;
            const place = single
                ? `No. ${o.Position} of ${o.Total}${o.Watched ? ' · watched' : ''}${o.Skipped ? ' · optional, skipped' : ''}`
                : `${o.Count} ${o.Count === 1 ? 'entry' : 'entries'} in this order, from no. ${o.Position} of ${o.Total}`;
            const thenIsNext = o.After && o.Next && normaliseId(o.After.ItemId) === normaliseId(o.Next.ItemId);
            const then = o.After ? ` · Then: ${link(o.After)}${thenIsNext ? ' (your next)' : ''}` : '';
            const yours = !o.Next ? 'You\'ve finished this order'
                : `You've watched ${o.WatchedCount} of ${o.AvailableCount}${thenIsNext ? '' : ` · Your next: ${link(o.Next)}`}`;
            return `
                <div class="listItem listItem-border ${PREFIX}-itemorder">
                    <span class="material-icons listItemIcon listItemIcon-transparent format_list_numbered" aria-hidden="true"></span>
                    <div class="listItemBody">
                        <div class="listItemBodyText"><a class="button-link ${PREFIX}-itemorder-name" href="${routeHash(o.OrderId)}">${esc(o.Name)}</a></div>
                        <div class="listItemBodyText secondary">${place}${then}</div>
                        <div class="listItemBodyText secondary">${yours}</div>
                        ${progressBar(o.WatchedCount, o.AvailableCount, `${o.Name} progress`)}
                    </div>
                    <div class="${PREFIX}-itemorder-actions">
                        ${playButton(o)}
                        <a class="raised emby-button" href="${routeHash(o.OrderId)}"><span>View order</span></a>
                    </div>
                </div>`;
        }).join('');
        return `<h2 class="sectionTitle padded-right">${list.length === 1 ? 'Part of a watch order' : 'Part of these watch orders'}</h2>
            <div class="padded-right"><div class="paperList ${PREFIX}-itemorders-list">${rows}</div></div>`;
    }

    /**
     * Not started, or finished: "Play from the start" (a finished order starts a new watch-through).
     * Part-way through: "Continue where you left off", which plays the user's next item in the order, wherever this
     * item is. Either way the rest of the order is queued after it.
     */
    function playButton(o) {
        const started = o.Next && o.WatchedCount > 0;
        const label = started ? 'Continue where you left off' : 'Play from the start';
        const title = started ? `Plays your next: ${o.Next.DisplayName || o.Next.Label}` : o.Next ? '' : 'Starts a new watch-through';
        return `<button is="emby-button" type="button" class="emby-button raised${started ? ' button-submit' : ''}" data-wo-item="play"
            data-order="${esc(o.OrderId)}" ${o.Next ? '' : 'data-fresh="true"'} ${title ? `title="${esc(title)}"` : ''}>
            <span class="material-icons ${started ? 'play_arrow' : 'replay'}" aria-hidden="true"></span><span>${label}</span></button>`;
    }

    async function onItemOrdersClick(e) {
        const button = e.target.closest('[data-wo-item="play"]');
        if (!button) {
            return; // links do their own thing
        }
        e.preventDefault();
        button.disabled = true;
        const previous = currentOrder;
        try {
            const orderId = button.dataset.order;
            if (button.dataset.fresh) {
                // Finished: watching it again is a new watch-through (which can be undone on the order's page).
                await postJson(`WatchOrder/Orders/${encodeURIComponent(orderId)}/Run`, { mode: 'Fresh' });
                summariesAt = 0;
            }
            currentOrder = await fetchOrder(orderId);
            const next = currentOrder.progress.Next;
            const index = next ? currentOrder.progress.Steps.findIndex((s) => s.Step.Position === next.Step.Position) : -1;
            if (index >= 0) {
                await playFrom(index);
            }
        } catch {
            openPage(button.dataset.order);
        } finally {
            if (!stillOn(button.dataset.order)) {
                currentOrder = previous;
            }
            button.disabled = false;
            itemOrdersCache.clear();
        }
    }

    // ------------------------------------------------------------ list view

    async function showList() {
        currentOrder = null;
        setLoading(LABEL, false);
        let list;
        try {
            list = await loadSummaries(true);
        } catch {
            list = null;
        }
        if (!stillOn('')) {
            return;
        }
        if (!list) {
            setError(LABEL, 'Your watch orders couldn\'t be loaded.', 'retry-list', false);
            return;
        }
        if (list.length === 0) {
            setView(LABEL, `<p class="${PREFIX}-empty ${PREFIX}-secondary">There are no watch orders for anything in your libraries yet.</p>`, false);
            return;
        }

        const cards = list.map((o) => {
            const finished = !o.Next;
            const started = o.WatchedCount > 0;
            const run = o.Run || {};
            const status = finished
                ? `<span class="${PREFIX}-done"><span class="material-icons check playstatebutton-icon-played" aria-hidden="true"></span>Finished${run.TimesCompleted > 1 ? ` ${timesText(run.TimesCompleted)}` : ''}</span>`
                : `${started ? 'Up next' : 'Starts with'}: <strong>${esc(o.Next.DisplayName || o.Next.Label)}</strong>`;
            const playLabel = finished ? '' : started ? 'Continue' : 'Start';
            const runText = run.Number > 1 ? ` · watch-through ${run.Number}` : '';
            return `
                <article class="paperList ${PREFIX}-card">
                    <h2>${esc(o.Name)}</h2>
                    ${o.Description ? `<p class="${PREFIX}-desc">${esc(o.Description)}</p>` : ''}
                    ${progressBar(o.WatchedCount, o.AvailableCount, `Progress through ${o.Name}`)}
                    <div class="${PREFIX}-count">${o.WatchedCount} of ${o.AvailableCount} watched${runText}${o.SkippedCount ? ` · ${o.SkippedCount} optional skipped` : ''}${o.TotalCount > o.AvailableCount ? ` · ${o.TotalCount - o.AvailableCount} not in your library` : ''}</div>
                    <div class="${PREFIX}-next">${status}</div>
                    <div class="${PREFIX}-actions">
                        ${playLabel ? `<button is="emby-button" type="button" class="emby-button raised button-submit" data-wo="play-order" data-order="${esc(o.Id)}"><span class="material-icons play_arrow" aria-hidden="true"></span><span>${playLabel}</span></button>`
                            : `<button is="emby-button" type="button" class="emby-button raised" data-wo="watch-again" data-order="${esc(o.Id)}"><span class="material-icons replay" aria-hidden="true"></span><span>Watch again</span></button>`}
                        <a href="${esc(routeHash(o.Id))}" class="raised emby-button"><span>View order</span></a>
                        ${!finished && showHomeRow ? homeButton(o.Id, o.OnHomeScreen) : ''}
                    </div>
                </article>`;
        }).join('');

        const autoplay = autoplayMode === 'Off' ? '' : `
            <div class="checkboxContainer checkboxContainer-withDescription ${PREFIX}-autoplay">
                <label><input type="checkbox" is="emby-checkbox" data-wo="autoplay-follow" ${autoplayFollow ? 'checked' : ''}><span>Autoplay follows my watch orders</span></label>
                <div class="fieldDescription checkboxFieldDescription">When something from an order you're following starts, the order's next item plays after it, instead of the next episode of the same series.</div>
            </div>`;
        setView(LABEL, `${autoplay}<div class="${PREFIX}-grid">${cards}</div>`, false);
    }

    // ----------------------------------------------------------- order view

    async function fetchOrder(orderId) {
        const progress = await getJson(`WatchOrder/Orders/${encodeURIComponent(orderId)}`);
        const ids = progress.Steps.map((s) => s.Step.ItemId).filter(Boolean);
        const items = await loadItems(ids);
        return { progress, items };
    }

    async function showOrder(orderId) {
        const name = summaries?.find((s) => s.Id === orderId)?.Name || 'Watch order';
        currentOrder = null;
        setLoading(name, true);
        let order;
        try {
            order = await fetchOrder(orderId);
        } catch {
            if (stillOn(orderId)) {
                setError(name, 'This order couldn\'t be loaded.', `retry-order:${orderId}`, true);
            }
            return;
        }
        if (stillOn(orderId)) {
            currentOrder = order;
            renderOrder();
        }
    }

    function itemFor(step) {
        return step.Step.ItemId ? currentOrder.items.get(normaliseId(step.Step.ItemId)) : null;
    }

    function stepText(step, item) {
        if (!item) {
            return { title: step.Step.Label, sub: 'Not in your library' };
        }
        if (item.Type === 'Episode') {
            const code = `S${pad(item.ParentIndexNumber)}E${pad(item.IndexNumber)}`;
            return { title: item.Name || code, sub: `${item.SeriesName || ''} · ${code}` };
        }
        return { title: item.Name, sub: [item.ProductionYear, 'Film'].filter(Boolean).join(' · ') };
    }

    function imageFor(item) {
        if (!item) {
            return '';
        }
        const options = { maxWidth: 320, quality: 85 };
        if (item.Type !== 'Episode' && item.BackdropImageTags?.length) {
            return api().getScaledImageUrl(item.Id, { ...options, type: 'Backdrop', tag: item.BackdropImageTags[0] });
        }
        if (item.ImageTags?.Thumb) {
            return api().getScaledImageUrl(item.Id, { ...options, type: 'Thumb', tag: item.ImageTags.Thumb });
        }
        if (item.ImageTags?.Primary) {
            return api().getScaledImageUrl(item.Id, { ...options, type: 'Primary', tag: item.ImageTags.Primary });
        }
        return '';
    }

    function renderOrder() {
        const { progress } = currentOrder;
        const steps = progress.Steps;
        const nextIndex = steps.findIndex((s) => s === progress.Next || (progress.Next && s.Step.Position === progress.Next.Step.Position));
        const watched = progress.WatchedCount;
        const available = progress.AvailableCount;
        let previousEntry = -1;

        const rows = steps.map((s, i) => {
            const item = itemFor(s);
            const missing = !s.Step.ItemId;
            const skipped = !!s.Skipped;
            const played = !!s.Played;
            const isNext = i === nextIndex;
            const showNote = s.Step.Note && s.Step.EntryIndex !== previousEntry;
            previousEntry = s.Step.EntryIndex;
            if (hideWatched && played && !isNext) {
                return '';
            }

            const text = stepText(s, item);
            const img = imageFor(item);
            const resume = item?.UserData?.PlayedPercentage && !played ? item.UserData.PlayedPercentage : 0;
            const detailsLink = item ? `#/details?id=${encodeURIComponent(item.Id)}&serverId=${encodeURIComponent(api().serverId())}` : null;
            const title = detailsLink
                ? `<a href="${detailsLink}" data-wo="details">${esc(text.title)}</a>`
                : esc(text.title);

            return `
                <li class="listItem listItem-border ${PREFIX}-step${isNext ? ` ${PREFIX}-is-next` : ''}${missing ? ` ${PREFIX}-is-missing` : ''}${skipped ? ` ${PREFIX}-is-skipped` : ''}" data-index="${i}">
                    <span class="${PREFIX}-pos ${PREFIX}-secondary" aria-hidden="true">${s.Step.Position}</span>
                    <div class="${PREFIX}-thumb" ${img ? `style="background-image:url('${esc(img)}')"` : ''}>
                        ${resume ? `<div class="itemProgressBar"><div class="itemProgressBarForeground" style="width:${Math.round(resume)}%"></div></div>` : ''}
                    </div>
                    <div class="listItemBody ${PREFIX}-step-main">
                        <div class="listItemBodyText ${PREFIX}-step-title">${title}</div>
                        <div class="listItemBodyText ${PREFIX}-step-sub ${PREFIX}-secondary">${s.Step.Optional ? `<span class="${PREFIX}-tag">${skipped ? 'Optional · skipped' : 'Optional'}</span> ` : ''}${esc(text.sub)}${isNext ? ' · <strong>Up next</strong>' : ''}${resume ? ` · ${Math.round(resume)}% watched` : ''}</div>
                        ${showNote ? `<div class="${PREFIX}-note ${PREFIX}-secondary">${esc(s.Step.Note)}</div>` : ''}
                    </div>
                    <div class="${PREFIX}-step-actions">
                        <button is="paper-icon-button-light" type="button" class="paper-icon-button-light" data-wo="toggle-played" data-index="${i}"
                            aria-pressed="${played}" aria-label="Watched: ${esc(text.title)}"
                            title="${played ? 'Watched. Select to mark as not watched' : 'Mark as watched'}" ${missing ? 'disabled' : ''}>
                            <span class="material-icons check ${played ? 'playstatebutton-icon-played' : 'playstatebutton-icon-unplayed'}" aria-hidden="true"></span>
                        </button>
                        <button is="paper-icon-button-light" type="button" class="paper-icon-button-light" data-wo="play-from" data-index="${i}"
                            aria-label="Play from ${esc(text.title)}" title="Play from here" ${missing ? 'disabled' : ''}>
                            <span class="material-icons play_arrow" aria-hidden="true"></span>
                        </button>
                    </div>
                </li>`;
        }).join('');

        const nextStep = nextIndex >= 0 ? steps[nextIndex] : null;
        const missingCount = steps.filter((st) => !st.Step.ItemId && !st.Skipped).length;
        const nextText = nextStep ? stepText(nextStep, itemFor(nextStep)) : null;
        const run = progress.Run || {};
        // Watched some of it before choosing how to count it (e.g. seen the films years ago): ask.
        const needsChoice = !run.Chosen && watched > 0;
        const runParts = [];
        if (run.Number > 1 || run.Fresh) {
            runParts.push(`Watch-through ${run.Number}${run.StartedAt ? `, started ${esc(formatDate(run.StartedAt))}` : ''}`);
        }
        if (run.TimesCompleted > 0) {
            runParts.push(`finished ${timesText(run.TimesCompleted)}`);
        }
        const runLine = runParts.length ? `<div class="${PREFIX}-count ${PREFIX}-run">${runParts.join(' · ').replace(/^f/, 'F')}</div>` : '';
        const choice = needsChoice ? `
                <div class="paperList ${PREFIX}-choice" role="group" aria-labelledby="${PREFIX}-choice-text">
                    <p id="${PREFIX}-choice-text">You've already watched ${watched} of ${available} of these. Count them, or start a fresh
                        watch-through from the beginning? Starting fresh doesn't change anything in your library.</p>
                    <div class="${PREFIX}-actions">
                        <button is="emby-button" type="button" class="emby-button raised" data-wo="run-continue"><span>Count them</span></button>
                        <button is="emby-button" type="button" class="emby-button raised" data-wo="run-fresh"><span class="material-icons replay" aria-hidden="true"></span><span>Start fresh</span></button>
                    </div>
                </div>` : '';
        const startOver = !needsChoice && watched > 0
            ? `<button is="emby-button" type="button" class="emby-button raised${nextStep ? '' : ' button-submit'}" data-wo="run-fresh">
                   <span class="material-icons replay" aria-hidden="true"></span><span>${nextStep ? 'Start over' : 'Watch again'}</span></button>`
            : '';
        const head = `
            <div class="${PREFIX}-head">
                ${progress.OrderDescription ? `<p class="${PREFIX}-desc" style="-webkit-line-clamp:unset;display:block">${esc(progress.OrderDescription)}</p>` : ''}
                ${progressBar(watched, available, 'Progress')}
                <div class="${PREFIX}-count">${watched} of ${available} watched${progress.SkippedCount ? ` · ${progress.SkippedCount} optional skipped` : ''}${missingCount ? ` · ${missingCount} not in your library` : ''}</div>
                ${runLine}
                ${choice}
                <div class="${PREFIX}-toolbar">
                    ${nextStep
                        ? `<button is="emby-button" type="button" class="emby-button raised button-submit" data-wo="play-from" data-index="${nextIndex}">
                               <span class="material-icons play_arrow" aria-hidden="true"></span><span>${watched ? 'Continue' : 'Start'}: ${esc(nextText.title)}</span></button>`
                        : `<span class="${PREFIX}-done"><span class="material-icons check playstatebutton-icon-played" aria-hidden="true"></span>You've finished this order.</span>`}
                    ${startOver}
                    ${nextStep && showHomeRow ? `<button is="emby-button" type="button" class="emby-button raised" data-wo="toggle-home" aria-pressed="${progress.OnHomeScreen}">
                        <span class="material-icons ${progress.OnHomeScreen ? 'check' : 'add'}" aria-hidden="true"></span><span>${progress.OnHomeScreen ? 'On your home screen' : 'Add to home screen'}</span></button>` : ''}
                    <label class="checkboxContainer"><input type="checkbox" is="emby-checkbox" data-wo="hide-watched" ${hideWatched ? 'checked' : ''}><span>Hide watched</span></label>
                    ${progress.OptionalCount ? `<label class="checkboxContainer" title="Optional entries are filler or side stories. Skipped ones don't count towards progress and aren't played automatically.">
                        <input type="checkbox" is="emby-checkbox" data-wo="skip-optional" ${progress.SkipOptional ? 'checked' : ''}><span>Skip optional entries (${progress.OptionalCount})</span></label>` : ''}
                    ${playlistToggle(progress, !!nextStep)}
                </div>
            </div>`;

        setView(progress.OrderName, `${head}<ol class="${PREFIX}-steps" aria-label="Viewing order">${rows}</ol>`, true);
    }

    /** "Keep a playlist of what's left", so the order can be carried on from any Jellyfin app, and a link to it. */
    function playlistToggle(progress, anythingLeft) {
        const playlist = progress.Playlist;
        if (!playlist || playlist.Mode === 'Off' || !anythingLeft) {
            return '';
        }
        const link = playlist.On && playlist.Id
            ? `<a class="${PREFIX}-playlist-link button-link" href="#/details?id=${encodeURIComponent(normaliseId(playlist.Id))}&serverId=${encodeURIComponent(api().serverId())}">Open playlist</a>`
            : '';
        return `<span class="${PREFIX}-playlist"><label class="checkboxContainer" title="A playlist of what you have left, in order, kept up to date as you watch. Play it in any Jellyfin app to carry on.">
                <input type="checkbox" is="emby-checkbox" data-wo="playlist" ${playlist.On ? 'checked' : ''}><span>Keep a playlist of what's left</span></label>${link}</span>`;
    }

    // --------------------------------------------------------------- actions

    async function findOwnSession() {
        const deviceId = api().deviceId?.();
        if (!deviceId) {
            return null;
        }
        const sessions = await getJson('Sessions', { deviceId, activeWithinSeconds: 600 });
        return (sessions || [])
            .filter((s) => s.SupportsRemoteControl !== false)
            .sort((a, b) => String(b.LastActivityDate).localeCompare(String(a.LastActivityDate)))[0] || null;
    }

    function openDetails(itemId) {
        location.hash = `#/details?id=${encodeURIComponent(itemId)}&serverId=${encodeURIComponent(api().serverId())}`;
    }

    /** Plays the step at `index`, then queues the following unwatched items in order. */
    async function playFrom(index) {
        const steps = currentOrder.progress.Steps;
        const queue = [];
        for (let i = index; i < steps.length && queue.length < QUEUE_LIMIT; i++) {
            const s = steps[i];
            // Skips what's been watched, and optional entries the user skips (unless that's where they chose to start).
            if (s.Step.ItemId && (i === index || (!s.Played && !s.Skipped))) {
                queue.push(s);
            }
        }
        if (!queue.length) {
            return;
        }

        // Watching from the Watch Orders page means the user is following this order: put it on their home
        // screen, and if they hadn't chosen how to count what they'd already watched, keep counting it.
        if (currentOrder.progress.HomeState !== 'Shown') {
            setHomeState(currentOrder.progress.OrderId, 'Shown').catch(() => {});
        }
        if (!currentOrder.progress.Run?.Chosen) {
            postJson(`WatchOrder/Orders/${encodeURIComponent(currentOrder.progress.OrderId)}/Run`, { mode: 'Continue' }).catch(() => {});
        }

        const first = itemFor(queue[0]);
        const ids = queue.map((s) => s.Step.ItemId);
        const resumeTicks = first && !queue[0].Played ? first.UserData?.PlaybackPositionTicks || 0 : 0;

        try {
            const session = await findOwnSession();
            if (!session) {
                throw new Error('No session');
            }
            const params = { playCommand: 'PlayNow', itemIds: ids.join(',') };
            if (resumeTicks > 0) {
                params.startPositionTicks = resumeTicks;
            }
            // The app opens its player by itself; Back from the player returns to this page.
            await send('POST', `Sessions/${encodeURIComponent(session.Id)}/Playing`, params);
        } catch {
            // Couldn't start playback directly; open the item so it can be played from there.
            openDetails(queue[0].Step.ItemId);
        }
    }

    async function playOrder(orderId) {
        const button = view?.querySelector(`[data-wo="play-order"][data-order="${CSS.escape(orderId)}"]`);
        if (button) {
            button.disabled = true;
        }
        const previous = currentOrder;
        try {
            currentOrder = await fetchOrder(orderId);
            const next = currentOrder.progress.Next;
            const index = next ? currentOrder.progress.Steps.findIndex((s) => s.Step.Position === next.Step.Position) : -1;
            if (index >= 0) {
                await playFrom(index);
            }
        } catch {
            // Show the order instead, so it can be played from there.
            openPage(orderId);
        } finally {
            if (!stillOn(orderId)) {
                currentOrder = previous;
            }
            if (button) {
                button.disabled = false;
            }
        }
    }

    async function toggleHome(button) {
        const progress = currentOrder.progress;
        const state = progress.OnHomeScreen ? 'Hidden' : 'Shown';
        button.disabled = true;
        try {
            await setHomeState(progress.OrderId, state);
            progress.HomeState = state;
            progress.OnHomeScreen = state === 'Shown';
            summariesAt = 0;
            if (stillOn(progress.OrderId)) {
                renderOrder();
                view.querySelector('[data-wo="toggle-home"]')?.focus();
            }
        } catch {
            button.disabled = false;
        }
    }

    /** "Count them", or a fresh watch-through ("Start fresh", "Start over", "Watch again"), with Undo for the latter. */
    async function startRun(orderId, mode, button) {
        if (button) {
            button.disabled = true;
        }
        try {
            const progress = await postJson(`WatchOrder/Orders/${encodeURIComponent(orderId)}/Run`, { mode });
            summariesAt = 0;
            if (stillOn(orderId) && currentOrder) {
                currentOrder.progress = progress;
                renderOrder();
                view.querySelector(`#${PREFIX}-title`)?.focus();
            } else {
                openPage(orderId);
            }
            if (mode === 'Fresh') {
                const name = progress.OrderName || 'this order';
                showToast(`Started watch-through ${progress.Run?.Number || 1} of ${name}. Earlier viewing no longer counts here.`, 'Undo', async () => {
                    const restored = await postJson(`WatchOrder/Orders/${encodeURIComponent(orderId)}/Run/Undo`);
                    summariesAt = 0;
                    if (stillOn(orderId) && currentOrder) {
                        currentOrder.progress = restored;
                        renderOrder();
                    }
                });
            }
        } catch {
            if (button) {
                button.disabled = false;
            }
        }
    }

    async function togglePlayed(index, button) {
        const step = currentOrder.progress.Steps[index];
        const id = step.Step.ItemId;
        if (!id) {
            return;
        }
        button.disabled = true;
        const orderId = currentOrder.progress.OrderId;
        try {
            // The plugin marks it in Jellyfin, except "not watched" during a fresh watch-through, which only
            // applies to that watch-through.
            await send('POST', `WatchOrder/Orders/${encodeURIComponent(orderId)}/Items/${encodeURIComponent(id)}/Watched`, { watched: !step.Played });
            const order = await fetchOrder(orderId);
            summariesAt = 0;
            if (stillOn(orderId)) {
                currentOrder = order;
                renderOrder();
                view.querySelector(`[data-wo="toggle-played"][data-index="${index}"]`)?.focus();
            }
        } catch {
            button.disabled = false;
        }
    }

    function onViewClick(e) {
        const target = e.target.closest('[data-wo]');
        if (!target) {
            return;
        }
        const action = target.dataset.wo;
        const index = Number(target.dataset.index);

        switch (action) {
            case 'retry-list': showList(); break;
            case 'play-order': playOrder(target.dataset.order); break;
            case 'play-from':
                target.disabled = true;
                playFrom(index).finally(() => {
                    target.disabled = false;
                });
                break;
            case 'toggle-played': togglePlayed(index, target); break;
            case 'toggle-home': toggleHome(target); break;
            case 'card-home': toggleCardHome(target); break;
            case 'run-continue': startRun(currentOrder?.progress.OrderId, 'Continue', target); break;
            case 'run-fresh': startRun(currentOrder?.progress.OrderId, 'Fresh', target); break;
            case 'watch-again': startRun(target.dataset.order, 'Fresh', target); break;
            default:
                if (action.startsWith('retry-order:')) {
                    showOrder(action.slice('retry-order:'.length));
                }
                // Links (back, view order, item details) navigate as normal.
        }
    }

    async function setSkipOptional(input) {
        const orderId = currentOrder?.progress.OrderId;
        if (!orderId) {
            return;
        }
        input.disabled = true;
        try {
            const progress = await postJson(`WatchOrder/Orders/${encodeURIComponent(orderId)}/SkipOptional`, { skip: input.checked });
            summariesAt = 0;
            if (stillOn(orderId) && currentOrder) {
                currentOrder.progress = progress;
                renderOrder();
                view.querySelector('[data-wo="skip-optional"]')?.focus();
            }
        } catch {
            input.checked = !input.checked;
            input.disabled = false;
        }
    }

    async function setPlaylist(input) {
        const orderId = currentOrder?.progress.OrderId;
        if (!orderId) {
            return;
        }
        input.disabled = true;
        try {
            const progress = await postJson(`WatchOrder/Orders/${encodeURIComponent(orderId)}/Playlist`, { on: input.checked });
            summariesAt = 0;
            if (stillOn(orderId) && currentOrder) {
                currentOrder.progress = progress;
                renderOrder();
                view.querySelector('[data-wo="playlist"]')?.focus();
            }
            // The server makes or removes the playlist a moment later; pick up its link when it's there.
            setTimeout(() => refreshPlaylist(orderId), 3500);
        } catch {
            input.checked = !input.checked;
            input.disabled = false;
        }
    }

    async function refreshPlaylist(orderId) {
        try {
            const progress = await getJson(`WatchOrder/Orders/${encodeURIComponent(orderId)}`);
            if (stillOn(orderId) && currentOrder?.progress.OrderId === orderId
                && JSON.stringify(progress.Playlist) !== JSON.stringify(currentOrder.progress.Playlist)) {
                const focused = document.activeElement?.dataset?.wo;
                currentOrder.progress.Playlist = progress.Playlist;
                renderOrder();
                if (focused) {
                    view.querySelector(`[data-wo="${focused}"]`)?.focus();
                }
            }
        } catch {
            // Not important: the link appears next time the page is opened.
        }
    }

    async function setAutoplayFollow(input) {
        const wanted = input.checked;
        input.disabled = true;
        try {
            await send('POST', 'WatchOrder/UserSettings', { autoplayFollow: wanted });
            autoplayFollow = wanted;
        } catch {
            input.checked = !wanted;
        } finally {
            input.disabled = false;
        }
    }

    function onViewChange(e) {
        if (e.target.dataset.wo === 'autoplay-follow') {
            setAutoplayFollow(e.target);
            return;
        }
        if (e.target.dataset.wo === 'skip-optional') {
            setSkipOptional(e.target);
            return;
        }
        if (e.target.dataset.wo === 'playlist') {
            setPlaylist(e.target);
            return;
        }
        if (e.target.dataset.wo === 'hide-watched') {
            hideWatched = e.target.checked;
            renderOrder();
            view.querySelector('[data-wo="hide-watched"]')?.focus();
        }
    }

    let lastHomeRefresh = 0;
    function onNavigate() {
        itemOrdersCache.clear(); // progress may have moved on since the last item page
        syncPage();
        const onHome = /^#\/(home)?(\?(?!tab=[1-9]).*)?$/.test(location.hash || '#/home');
        if (onHome && Date.now() - lastHomeRefresh > 1000) {
            lastHomeRefresh = Date.now();
            loadSummaries(true);
        }
        scheduleEnsure();
    }

    // ----------------------------------------------------------------- start

    function start() {
        injectStyles();
        syncPage();
        new MutationObserver(scheduleEnsure).observe(document.body, { childList: true, subtree: true });
        window.addEventListener('hashchange', onNavigate);
        window.addEventListener('popstate', onNavigate);
        scheduleEnsure();
    }

    // Exposed for testing and for other plugins that want to open a specific order.
    window.WatchOrder = { open: openPage, close: leavePage };

    if (document.body) {
        start();
    } else {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    }
})();
