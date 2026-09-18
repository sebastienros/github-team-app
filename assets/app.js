
const app = document.getElementById("app");
const standalone = typeof window !== "undefined" && window.aspireTeamStandalone === true;
const dashboardClient = standalone ? crypto.randomUUID() : null;
function apiFetch(path, options = {}) {
  return fetch(path, standalone
    ? { ...options, headers: { ...options.headers, "X-Team-App-Client": dashboardClient } }
    : options);
}
// Deterministic top progress bar element (lives outside #app so re-renders don't drop it).
const loadbar = document.getElementById("loadbar");
let state = null;
let prefs = null;
let view = "queue";       // queue | settings | accounts | notifications | filters
let keysBound = false;
let cbMenuBound = false;
let prevRank = 0;
let refreshing = false;
// Count of overlapping withRefresh() calls in flight. The refresh button stays clickable and
// mode/account mutations also route through withRefresh, so several can run at once over one
// shared refreshing flag and one progress bar. We wind the shared UI down only when the LAST
// one settles (count returns to 0), never when whichever finishes first does.
let refreshInFlight = 0;
// Monotonic id assigned to each withRefresh() call in start order. Overlapping refreshes can
// reject out of order, and unlike the success path (gated by the server-assigned seq) a rejection
// carries no seq to order it. The catch gates on this so only the latest-started refresh may
// publish its failure: an older refresh that rejects after a newer one started must not paint a
// failure banner over the newer valid state (or over the newer refresh still settling).
let refreshGen = 0;
let rescanning = false;
let loadError = null;
// A dashboard pushed over SSE while the user is on a form-bearing view (accounts editor,
// settings, etc.) is stashed here and applied when they return to the queue, so a
// background refresh never clobbers an in-progress edit.
let pendingState = null;
// Completed background snapshots wait here when automatic application is disabled. The SSE event
// carries metadata only; clicking Apply reads the already-computed complete snapshot from the server.
let updateAvailable = null;
let applyingUpdate = false;
let savingAutoApply = false;
let nextPollAt = null;
let pollCountdownTimer = null;
// Monotonic revision of the snapshot currently applied. fetchedAt is a wall-clock display
// timestamp and is unsafe as a stream key because overlapping requests can settle out of order.
// The server stamps a strictly increasing seq whenever semantic content changes; we apply only
// strictly-newer snapshots. -1 lets the first real snapshot (0+) always apply.
let lastAppliedSeq = -1;
// Record the revision of whatever snapshot was just assigned to state. Call at every point
// that adopts a server snapshot so later SSE pushes are ordered against it.
function adoptAppliedRev() {
  if (state && typeof state.seq === "number") lastAppliedSeq = state.seq;
}
function adoptState(payload) {
  state = payload.dashboard;
  prefs = payload.prefs;
  loadError = null;
  adoptAppliedRev();
  const appliedSeq = state && state.seq;
  if (!updateAvailable || typeof appliedSeq !== "number" || appliedSeq >= updateAvailable.seq) {
    updateAvailable = null;
  }
}
function autoApplyEnabled() {
  return !prefs || prefs.autoApplyUpdates !== false;
}
function refreshTooltip() {
  if (typeof nextPollAt !== "number") return "Refresh now";
  const seconds = Math.max(0, Math.ceil((nextPollAt - Date.now()) / 1000));
  return "Refresh now (data will auto-update in " + seconds + "s)";
}
const expanded = new Set(); // account ids whose detail (sources + repos) is expanded
const collapsedLanes = new Set(); // lane ids the user collapsed (survives re-render + SSE)
const draftReposByAcct = {}; // account id -> working copy of that account's watched repos
const editingByAcct = {};    // account id -> index of the repo row being inline-edited, or -1
const repoSaveSeqByAcct = {}; // account id -> latest repository save request number
let pipelineUrlDraft = "";
let pipelineBranchDraft = "";
let pipelineError = "";
let pipelineSaving = false;
let draggedHealthId = null;
let healthOrderSaving = false;
let healthOrderAnnouncement = "";
let sessionSettingsError = "";
let sessionSettingsSaving = false;
let doctorResult = null;
let doctorRunning = false;

// Agent actions still in flight, keyed by action kind and canonical PR/source identity,
// so a split can be tracked independently of its owning card's DOM node. The in-DOM busy state
// on the button is not enough on its own: a streamed 'state' event re-renders the card and hands
// back a fresh, enabled button mid-request, which a second click would use to re-queue the same
// agent action. cardActionBtn consults this set so the replacement split re-renders already
// disabled, and onCardAction clears the key once the request settles (matching the existing
// design where a later SSE refresh restores the default label).
const inflightActions = new Set();
function actionKey(kind, prUrl, prRepo, prNumber) {
  return String(kind) + "@" + (prUrl || (String(prRepo) + "#" + String(prNumber)));
}

const RANK = { queue: 0, notifications: 1, accounts: 1, settings: 1, filters: 1 };

const ICONS = {
  refresh: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-2.64-6.36"/><path d="M21 3v6h-6"/></svg>',
  gear: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>',
  bell: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg>',
  bellBig: '<svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg>',
  chev: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M6 9l6 6 6-6"/></svg>',
  back: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M19 12H5"/><path d="M12 19l-7-7 7-7"/></svg>',
  check: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6L9 17l-5-5"/></svg>',
  x: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 6L6 18"/><path d="M6 6l12 12"/></svg>',
  plus: '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 5v14"/><path d="M5 12h14"/></svg>',
  pencil: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z"/></svg>',
  trash: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg>',
  users: '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg>',
  alert: '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>',
  eye: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></svg>',
  merge: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="18" cy="18" r="3"/><circle cx="6" cy="6" r="3"/><path d="M6 21V9a9 9 0 0 0 9 9"/></svg>',
  pulse: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/></svg>',
  xcircle: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M15 9l-6 6"/><path d="M9 9l6 6"/></svg>',
  chat: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>',
  pr: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="18" cy="18" r="3"/><circle cx="6" cy="6" r="3"/><path d="M13 6h3a2 2 0 0 1 2 2v7"/><line x1="6" y1="9" x2="6" y2="21"/></svg>',
  tag: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><line x1="7" y1="7" x2="7.01" y2="7"/></svg>',
  clock: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg>',
  dot2: '<svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="12" cy="12" r="4"/></svg>',
  building: '<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="2" width="16" height="20" rx="2"/><path d="M9 22v-4h6v4"/><path d="M8 6h.01M16 6h.01M12 6h.01M12 10h.01M12 14h.01M16 10h.01M16 14h.01M8 10h.01M8 14h.01"/></svg>',
  sparkle: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 3l1.9 5.1L18 10l-5.1 1.9L11 17l-1.9-5.1L4 10l5.1-1.9z"/><path d="M19 14l.7 1.9 1.9.7-1.9.7-.7 1.9-.7-1.9-1.9-.7 1.9-.7z"/></svg>',
  alertSm: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>',
  globe: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>',
  usersSm: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg>',
  layers: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 17 12 22 22 17"/><polyline points="2 12 12 17 22 12"/></svg>',
  funnel: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3"/></svg>',
};

const LOGO = '<svg viewBox="0 0 32 32" fill="none" aria-hidden="true"><path d="M3.5 30C1.57 30 0 28.43 0 26.5C0 25.871 0.166 25.259 0.48 24.729L8.818 10.287L8.852 10.236L12.968 3.099C13.593 2.019 14.754 1.349 16 1.349C17.246 1.349 18.407 2.019 19.031 3.098L31.531 24.749C31.833 25.258 31.999 25.87 31.999 26.499C31.999 28.429 30.429 29.999 28.499 29.999L3.5 30Z" fill="#512BD4"/><path d="M25.33 18H16.99L16 16.28L13.13 11.31C13 11.09 12.82 10.9 12.58 10.77C11.87 10.35 10.95 10.6 10.53 11.32L14.7 4.10001C14.96 3.65001 15.44 3.35001 16 3.35001C16.56 3.35001 17.04 3.65001 17.3 4.10001L21.45 11.29L21.46 11.31L21.48 11.34L25.33 18Z" fill="#7455DD"/><path d="M30 26.5C30 27.33 29.33 28 28.5 28H20.17C21 28 21.67 27.33 21.67 26.5C21.67 26.23 21.59 25.97 21.47 25.75L17.3 18.53L16.99 18H25.33L29.8 25.75C29.93 25.97 30 26.23 30 26.5Z" fill="#9780E5"/><path d="M21.67 26.5C21.67 27.33 21 28 20.17 28H11.83C12.66 28 13.33 27.33 13.33 26.5C13.33 26.23 13.26 25.97 13.13 25.75C13.13 25.74 13.12 25.73 13.11 25.72L11.79 23.57L8.82004 18.72C8.55004 18.28 8.07004 18 7.54004 18H16.99L17.3 18.53L17.427 18.75L21.47 25.75C21.59 25.97 21.67 26.23 21.67 26.5Z" fill="#B9AAEE"/><path d="M13.33 26.5C13.33 27.33 12.66 28 11.83 28H3.5C2.67 28 2 27.33 2 26.5C2 26.23 2.07 25.97 2.2 25.75L6.24 18.75C6.51 18.29 7.01 18 7.54 18C8.07 18 8.55 18.28 8.82 18.72L11.79 23.57L13.11 25.72C13.12 25.73 13.13 25.74 13.13 25.75C13.26 25.97 13.33 26.23 13.33 26.5Z" fill="#DCD5F6"/><path d="M16.99 18H7.53999C7.00999 18 6.50999 18.29 6.23999 18.75L6.66999 18L10.49 11.39L10.53 11.33V11.32C10.95 10.6 11.87 10.35 12.58 10.77C12.82 10.9 13 11.09 13.13 11.31L16 16.28L16.99 18Z" fill="#9780E5"/></svg>';

const ACCT_STATUS = {
  ok: { tone: "success", label: "Full access" },
  partial: { tone: "warning", label: "Partial access" },
  limited: { tone: "warning", label: "No repo access" },
  failed: { tone: "danger", label: "Sign-in failed" },
};
const SRC_LABEL = { gh: "GitHub CLI", env: "Environment", copilot: "Copilot" };
function acctTone(a) { return (ACCT_STATUS[a && a.status] || ACCT_STATUS.failed).tone; }
function srcLabel(s) { return SRC_LABEL[s] || s; }

function esc(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function safeHref(value) {
  try {
    const url = new URL(String(value || ""));
    return url.protocol === "https:" || url.protocol === "http:" ? esc(url.href) : "#";
  } catch {
    return "#";
  }
}
function timeAgo(iso) {
  const s = Math.floor((Date.now() - new Date(iso)) / 1000);
  if (s < 60) return s + "s ago";
  const m = Math.floor(s / 60); if (m < 60) return m + "m ago";
  const h = Math.floor(m / 60); if (h < 24) return h + "h ago";
  return Math.floor(h / 24) + "d ago";
}
function shortRepo(r) { const p = String(r).split("/"); return p[p.length - 1]; }
function cssEsc(s) { return (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/[^\w-]/g, "\\$&"); }

// Neutral gray silhouette used when an avatar URL 404s (renamed users, bots,
// enterprise hosts the browser can't reach). Base64 keeps it safe inside both the
// double-quoted attribute and the single-quoted onerror JS string.
const FALLBACK_AVATAR = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0MCIgaGVpZ2h0PSI0MCIgdmlld0JveD0iMCAwIDQwIDQwIj48cmVjdCB3aWR0aD0iNDAiIGhlaWdodD0iNDAiIHJ4PSIyMCIgZmlsbD0iIzMwMzYzZCIvPjxjaXJjbGUgY3g9IjIwIiBjeT0iMTUuNSIgcj0iNi41IiBmaWxsPSIjOGI5NDllIi8+PHBhdGggZD0iTTguNSAzMy41YzAtNi40IDUuMi0xMC41IDExLjUtMTAuNXMxMS41IDQuMSAxMS41IDEwLjV6IiBmaWxsPSIjOGI5NDllIi8+PC9zdmc+";
if (standalone) document.addEventListener("error", (event) => {
  const image = event.target;
  if (image && image.tagName === "IMG" && image.hasAttribute("data-fallback-avatar")) {
    image.removeAttribute("data-fallback-avatar");
    image.src = FALLBACK_AVATAR;
  }
}, true);

// Build an <img> for an avatar, preferring the real avatarUrl from the API and
// degrading to github.com/<login>.png, then to the inline fallback on error.
function avatarTag(url, login, cls, size) {
  const px = size || 40;
  const src = url || ("https://github.com/" + encodeURIComponent(login || "github") + ".png?size=" + px);
  return '<img class="' + (cls || "avatar") + '" src="' + esc(src) + '" alt="" referrerpolicy="no-referrer" loading="lazy" ' +
    (standalone ? 'data-fallback-avatar />' : 'onerror="this.onerror=null;this.src=\'' + FALLBACK_AVATAR + '\'" />');
}
function acctAvatar(a, size) {
  const url = a && a.avatarUrl ? a.avatarUrl : null;
  const login = a && a.login ? a.login : (typeof a === "string" ? a : "github");
  return avatarTag(url, login, "acct-av", size);
}

/* ---- data ---- */

// Parse a JSON response, surfacing the server's { error } message on any non-2xx
// (origin-guard 403, 500, etc.) instead of treating the error body as a successful
// payload. The loopback server always replies with JSON, but tolerate a missing or
// unparseable body so a raw failure still yields a useful message.
async function readJson(res) {
  const data = await res.json().catch(() => null);
  if (!res.ok) throw new Error((data && data.error) || ("Request failed (" + res.status + ")"));
  return data;
}

async function load() {
  // Capture the applied revision at request start. GET /api/state may be served stale-while-
  // revalidate and can still be in flight when an SSE 'state' event (applyPushedState) applies a
  // newer snapshot. If the GET then fails, publishing its error would paint a failure banner over
  // that newer valid state. Suppress the failure when a newer revision was applied after this
  // request started (the withRefresh catch gates the same class of race with its refreshGen id).
  const startSeq = lastAppliedSeq;
  let changed = false;
  try {
    const res = await apiFetch("api/state");
    const data = await readJson(res);
    // GET /api/state may be served stale-while-revalidate, so a cached seq N can settle after the
    // background stream already delivered seq N+1. Apply this response only when it is legacy (no seq)
    // or strictly newer than what we've already applied, so a late stale load can't roll
    // state/lastAppliedSeq backward. Mirrors the withRefresh/applyPushedState gate.
    const seq = data.dashboard && data.dashboard.seq;
    if (typeof seq !== "number" || seq > lastAppliedSeq) {
      adoptState(data);
      changed = true;
    }
  } catch (e) {
    // Only publish this failure if no newer revision was applied while the GET was pending. A late
    // failure from a superseded request must not clobber the newer valid state (or its banner).
    if (lastAppliedSeq === startSeq) {
      loadError = String((e && e.message) || e);
      changed = true;
    }
  }
  if (changed) render(); else updateRefreshControls();
}

async function withRefresh(fn) {
  const myGen = ++refreshGen;
  let changed = false;
  refreshInFlight++;
  refreshing = true; setLoading(true); beginProgress();
  try {
    const data = await fn();
    if (data && data.dashboard) {
      // Overlapping refreshes can resolve out of order: an older forced load may finish client-side
      // after a newer one. Apply this response only when it is legacy (no seq) or strictly newer than
      // what we've already applied, so a late older response can't roll state/lastAppliedSeq backward
      // (which would show stale data and corrupt later seq gates). Mirrors applyPushedState's gate.
      const seq = data.dashboard.seq;
      if (typeof seq !== "number" || seq > lastAppliedSeq) {
        adoptState(data);
        changed = true;
      }
    }
  } catch (e) {
    // Publish this failure only if no newer refresh has started since. A late rejection from an
    // older overlapping refresh must not clobber the newer operation's state/banner — the success
    // path is seq-gated for the same reason, but rejections carry no seq, so gate on refreshGen.
    if (myGen === refreshGen) {
      loadError = String((e && e.message) || e);
      changed = true;
    }
  } finally {
    refreshInFlight--;
    // Only the last overlapping refresh winds down the shared UI. If an earlier one finished
    // this while a later forced load is still running, we must NOT clear refreshing or fade
    // the bar out from under it — just re-render to show whatever data this call applied.
    // The SSE 'progress' stream is the normal bar driver (setProgress -> endProgress at
    // done>=total); ending here is the backstop for when it never delivers a terminal event
    // (SSE disconnected, or this refresh joined a background compute started with
    // progress:false, which emits no progress events).
    if (refreshInFlight === 0) { refreshing = false; endProgress(); }
    if (changed) render(); else updateRefreshControls();
  }
}

function setLoading(on) {
  const rb = document.getElementById("refresh-btn");
  if (rb) rb.classList.toggle("spin", on);
}

function updateRefreshControls() {
  const rb = document.getElementById("refresh-btn");
  if (rb) {
    rb.classList.toggle("spin", refreshing);
    const tooltip = refreshTooltip();
    rb.dataset.tooltip = tooltip;
    rb.setAttribute("aria-label", tooltip);
  }

  const auto = document.getElementById("auto-apply-btn");
  const enabled = autoApplyEnabled();
  if (auto) {
    auto.classList.toggle("active", enabled);
    auto.setAttribute("aria-checked", enabled ? "true" : "false");
    auto.title = enabled
      ? "Background updates apply automatically"
      : "Background updates wait until you apply them";
    auto.disabled = savingAutoApply;
  }

  const apply = document.getElementById("apply-update-btn");
  if (apply) {
    apply.hidden = !updateAvailable || enabled;
    apply.disabled = applyingUpdate;
    apply.classList.toggle("busy", applyingUpdate);
  }
}

/* ---- deterministic progress bar ----
   Driven by SSE 'progress' events ({ done, total }). beginProgress shows a small sliver
   for instant feedback; setProgress advances the fill; endProgress completes to 100% then
   fades. All are no-ops when #loadbar is absent (e.g. the render test harness). */
let progFadeTimer = null;
let progResetTimer = null;
function beginProgress() {
  if (!loadbar) return;
  if (progFadeTimer) { clearTimeout(progFadeTimer); progFadeTimer = null; }
  if (progResetTimer) { clearTimeout(progResetTimer); progResetTimer = null; }
  loadbar.classList.add("active");
  const w = parseFloat(loadbar.style.width) || 0;
  // Start (or restart from a faded-out state) with a visible sliver.
  if (w <= 0 || w >= 100) loadbar.style.width = "8%";
}
function setProgress(done, total) {
  if (!loadbar) return;
  beginProgress();
  const pct = total > 0 ? Math.max(8, Math.min(100, Math.round((done / total) * 100))) : 8;
  loadbar.style.width = pct + "%";
  // Only fade on a terminal tick when this is the last in-flight refresh. Forced computes are
  // serialized server-side, so when two withRefresh() calls overlap the FIRST compute emits its
  // done>=total tick while the second is still fetching; fading here would bypass the counter's
  // "last operation settles" invariant and flicker the bar back on at the second compute's first
  // tick. withRefresh's finally (endProgress at refreshInFlight === 0) remains the backstop.
  if (total > 0 && done >= total && refreshInFlight <= 1) { endProgress(); }
}
function endProgress() {
  if (!loadbar) return;
  // Clear any in-flight fade/reset timers so a second call (SSE completion followed by the
  // withRefresh finally backstop, or vice versa) can't leave a dangling timer that fires a
  // duplicate fade after the bar has already reset.
  if (progFadeTimer) { clearTimeout(progFadeTimer); progFadeTimer = null; }
  if (progResetTimer) { clearTimeout(progResetTimer); progResetTimer = null; }
  loadbar.style.width = "100%";
  // Fill to 100%, hold briefly, fade out, then reset width so the next cycle grows from
  // the left again rather than snapping back visibly.
  progFadeTimer = setTimeout(() => {
    loadbar.classList.remove("active");
    progResetTimer = setTimeout(() => { loadbar.style.width = "0"; }, 260);
  }, 220);
}

// Apply a dashboard pushed over SSE. Guarded so background updates never disrupt an active
// edit: while off the queue we stash it (applied on return); duplicate final snapshots are
// dropped; and scroll position is preserved across the re-render.
function applyPushedState(payload) {
  if (!payload || !payload.dashboard) return;
  if (view !== "queue") { pendingState = payload; return; }
  const seq = payload.dashboard.seq;
  if (typeof seq === "number" && seq <= lastAppliedSeq) {
    // Stale or duplicate: the request response may have already applied this snapshot, or an older
    // overlapping request settled late. Never overwrite the newer state already on screen.
    return;
  }
  // The order endpoint broadcasts the same card snapshot that the optimistic render already
  // shows. Adopt its revision and preferences without rebuilding the grid, which would cancel
  // the in-progress FLIP animation and appear as a flash.
  if (healthOrderSaving && sameRenderedHealthDashboard(state, payload.dashboard)) {
    state = payload.dashboard; prefs = payload.prefs; loadError = null;
    adoptAppliedRev();
    return;
  }
  applyState(payload);
}

function sameRenderedHealthDashboard(left, right) {
  if (left?.mode !== "health" || right?.mode !== "health") return false;
  return left.loading === right.loading
    && JSON.stringify(left.health ?? null) === JSON.stringify(right.health ?? null)
    && JSON.stringify(left.errors ?? []) === JSON.stringify(right.errors ?? []);
}
function applyState(payload) {
  const scroller = document.scrollingElement;
  const top = scroller ? scroller.scrollTop : 0;
  adoptState(payload);
  render();
  if (top && scroller) scroller.scrollTop = top;
}

async function postJSON(path, body) {
  const res = await apiFetch(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {}) });
  return readJson(res);
}

function onUpdateAvailable(payload) {
  if (!payload || typeof payload.seq !== "number" || payload.seq <= lastAppliedSeq) return;
  if (!updateAvailable || payload.seq > updateAvailable.seq) updateAvailable = payload;
  updateRefreshControls();
}

function onPreferences(nextPrefs) {
  if (!nextPrefs || typeof nextPrefs !== "object") return;
  const wasEnabled = autoApplyEnabled();
  prefs = nextPrefs;
  updateRefreshControls();
  if (!wasEnabled && autoApplyEnabled() && updateAvailable) {
    applyAvailableUpdate();
  }
}

function onSnapshot(payload) {
  onPollSchedule(payload);
  if (!payload || typeof payload.seq !== "number" || payload.seq <= lastAppliedSeq) return;
  if (payload.prefs && typeof payload.prefs === "object") prefs = payload.prefs;
  // Initial load already reads the complete cache. On reconnect, replay either applies the missed
  // state automatically or restores the pending-update affordance without touching the board.
  if (!state) return;
  if (autoApplyEnabled()) load(); else onUpdateAvailable(payload);
}

function onPollSchedule(payload) {
  const value = Number(payload && payload.nextPollAt);
  if (!Number.isFinite(value) || value <= 0) return;
  nextPollAt = value;
  if (!pollCountdownTimer) {
    pollCountdownTimer = setInterval(updateRefreshControls, 1000);
  }
  updateRefreshControls();
}

async function applyAvailableUpdate() {
  if (!updateAvailable || applyingUpdate) return;
  applyingUpdate = true;
  updateRefreshControls();
  try {
    const res = await apiFetch("api/state");
    const data = await readJson(res);
    const seq = data.dashboard && data.dashboard.seq;
    if (view !== "queue") {
      pendingState = data;
      prefs = data.prefs;
      updateAvailable = null;
    } else if (typeof seq !== "number" || seq > lastAppliedSeq) {
      applyState(data);
    } else {
      prefs = data.prefs;
      updateAvailable = null;
    }
  } catch (e) {
    loadError = String((e && e.message) || e);
    if (view === "queue") render();
  } finally {
    applyingUpdate = false;
    updateRefreshControls();
  }
}

async function toggleAutoApply() {
  if (savingAutoApply) return;
  const previous = autoApplyEnabled();
  const enabled = !previous;
  prefs = { ...(prefs || {}), autoApplyUpdates: enabled };
  savingAutoApply = true;
  updateRefreshControls();
  try {
    const data = await postJSON("api/auto-apply", { enabled });
    if (data && data.prefs) prefs = data.prefs;
    if (enabled && updateAvailable) await applyAvailableUpdate();
  } catch (e) {
    prefs = { ...(prefs || {}), autoApplyUpdates: previous };
    loadError = String((e && e.message) || e);
    render();
  } finally {
    savingAutoApply = false;
    updateRefreshControls();
  }
}

async function openLinkedPr(link) {
  if (!link || link.classList.contains("busy")) return;
  link.classList.add("busy");
  try {
    await postJSON("api/open-pr", { url: link.href });
  } catch (e) {
    loadError = String((e && e.message) || e);
    render();
  } finally {
    link.classList.remove("busy");
  }
}

const refresh = () => withRefresh(() => postJSON("api/refresh"));
const setMode = (mode) => { if (state && state.mode === mode) return; goView("queue", false); return withRefresh(() => postJSON("api/mode", { mode })); };
const toggleAccountActive = (id, active) => withRefresh(() => postJSON("api/account/toggle", { id, active }));

function captureSettingsDraft() {
  const release = document.getElementById("release-input");
  const showDrafts = document.getElementById("s-drafts");
  const reviewRequested = document.getElementById("n-review");
  const readyToMerge = document.getElementById("n-ready");
  const changesRequested = document.getElementById("n-changes");
  const ciFailing = document.getElementById("n-ci");
  if (!release || !showDrafts || !reviewRequested || !readyToMerge || !changesRequested || !ciFailing) return null;
  return {
    release: release.value,
    showDrafts: showDrafts.checked,
    sessionFields: standalone ? ["session-project-name", "session-project-url", "session-project"]
      .map(id => ({ id, value: document.getElementById(id)?.value || "" })) : [],
    notifications: {
      reviewRequested: reviewRequested.checked,
      readyToMerge: readyToMerge.checked,
      changesRequested: changesRequested.checked,
      ciFailing: ciFailing.checked,
    },
  };
}

function restoreSettingsDraft(draft) {
  if (!draft) return;
  const release = document.getElementById("release-input");
  const showDrafts = document.getElementById("s-drafts");
  const reviewRequested = document.getElementById("n-review");
  const readyToMerge = document.getElementById("n-ready");
  const changesRequested = document.getElementById("n-changes");
  const ciFailing = document.getElementById("n-ci");
  if (release) release.value = draft.release;
  if (showDrafts) showDrafts.checked = draft.showDrafts;
  if (reviewRequested) reviewRequested.checked = draft.notifications.reviewRequested;
  if (readyToMerge) readyToMerge.checked = draft.notifications.readyToMerge;
  if (changesRequested) changesRequested.checked = draft.notifications.changesRequested;
  if (ciFailing) ciFailing.checked = draft.notifications.ciFailing;
  for (const field of draft.sessionFields || []) {
    const element = document.getElementById(field.id);
    if (element) element.value = field.value;
  }
}

async function mutateAzurePipeline(path, body, clearDraft) {
  if (pipelineSaving) return;
  // Pipeline state requires a full render, which otherwise rebuilds the rest of Settings
  // from persisted preferences and discards edits that have not been saved yet.
  let settingsDraft = captureSettingsDraft();
  pipelineSaving = true;
  pipelineError = "";
  render();
  restoreSettingsDraft(settingsDraft);
  try {
    const data = await postJSON(path, body);
    if (data && data.prefs) prefs = data.prefs;
    if (data && data.dashboard) {
      const seq = data.dashboard.seq;
      if (typeof seq !== "number" || seq > lastAppliedSeq) {
        state = data.dashboard;
        adoptAppliedRev();
      }
    }
    if (clearDraft) {
      pipelineUrlDraft = "";
      pipelineBranchDraft = "";
    }
    loadError = null;
  } catch (error) {
    pipelineError = String((error && error.message) || error || "Pipeline update failed");
  } finally {
    settingsDraft = captureSettingsDraft() || settingsDraft;
    pipelineSaving = false;
    render();
    restoreSettingsDraft(settingsDraft);
  }
}

function addAzurePipeline() {
  const url = pipelineUrlDraft.trim();
  const branch = pipelineBranchDraft.trim();
  if (!url) {
    const settingsDraft = captureSettingsDraft();
    pipelineError = "Enter an Azure DevOps pipeline or build URL.";
    render();
    restoreSettingsDraft(settingsDraft);
    return;
  }
  return mutateAzurePipeline("api/health/pipeline/add", { url, branch: branch || undefined }, true);
}

function removeAzurePipeline(id) {
  if (!id) return;
  return mutateAzurePipeline("api/health/pipeline/remove", { id }, false);
}

function currentHealthItems() {
  return Array.isArray(state?.health?.items) ? state.health.items : [];
}

function healthRepositoryGroups(items = currentHealthItems()) {
  const groups = new Map();
  for (const item of items) {
    const id = item?.groupId || item?.id;
    if (!id) continue;
    let group = groups.get(id);
    if (!group) {
      group = {
        id,
        name: item.groupName || item.repository?.name || item.repository || item.name || "Health source",
        items: [],
        url: null,
        match: null,
      };
      groups.set(id, group);
    }
    group.items.push(item);
    if (item.provider === "github" && item.url) group.url = item.url;
    else if (!group.url && item.url) group.url = item.url;
    if (item.groupMatch === "name") group.match = "name";
    else if (!group.match && item.groupMatch === "provider") group.match = "provider";
  }
  return [...groups.values()];
}

function flattenHealthGroups(groups) {
  return groups.flatMap((group) => group.items);
}

function healthSourceName(item) {
  return item?.name || item?.repository || "Health source";
}

function focusHealthHandle(id) {
  requestAnimationFrame(() => {
    const handle = Array.from(document.querySelectorAll("[data-health-drag]"))
      .find((candidate) => candidate.dataset.healthDrag === id);
    if (handle) handle.focus();
  });
}

function clearHealthDragMarkers() {
  document.querySelectorAll(".health-unit").forEach((unit) => {
    unit.classList.remove("dragging", "drag-before", "drag-after");
  });
}

function clearHealthDropMarkers() {
  document.querySelectorAll(".health-unit").forEach((unit) => {
    unit.classList.remove("drag-before", "drag-after");
    delete unit.dataset.dropAfter;
  });
}

function setHealthDropMarker(card, after) {
  const marker = after ? "drag-after" : "drag-before";
  if (card.classList.contains(marker) && card.dataset.dropAfter === String(after)) return false;
  clearHealthDropMarkers();
  card.classList.add(marker);
  card.dataset.dropAfter = String(after);
  return true;
}

function captureHealthLayout() {
  const positions = new Map();
  document.querySelectorAll(".health-unit[data-health-group-id]").forEach((unit) => {
    positions.set(unit.dataset.healthGroupId, unit.getBoundingClientRect());
  });
  return positions;
}

function animateHealthLayout(previous) {
  if (!previous.size) return;
  if (window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
  document.querySelectorAll(".health-unit[data-health-group-id]").forEach((unit) => {
    const before = previous.get(unit.dataset.healthGroupId);
    if (!before || typeof unit.animate !== "function") return;
    const after = unit.getBoundingClientRect();
    const x = before.left - after.left;
    const y = before.top - after.top;
    if (Math.abs(x) < 1 && Math.abs(y) < 1) return;
    unit.animate(
      [
        { transform: "translate(" + x + "px, " + y + "px)", opacity: .82 },
        { transform: "translate(0, 0)", opacity: 1 },
      ],
      { duration: 280, easing: "cubic-bezier(.2, .8, .2, 1)" },
    );
  });
}

function renderHealthOrderTransition(previous) {
  render();
  animateHealthLayout(previous);
}

function setHealthDragImage(event, card) {
  if (!event.dataTransfer || !card || !document.body) return;
  const rect = card.getBoundingClientRect();
  const ghost = card.cloneNode(true);
  ghost.classList.remove("dragging", "drag-before", "drag-after");
  ghost.classList.add("health-drag-ghost");
  ghost.style.width = rect.width + "px";
  document.body.appendChild(ghost);
  event.dataTransfer.setDragImage(ghost, Math.min(36, rect.width / 2), 24);
  setTimeout(() => ghost.remove(), 0);
}

async function commitHealthOrder(nextItems, previousItems, focusId) {
  if (healthOrderSaving) return;
  const previousLayout = captureHealthLayout();
  healthOrderSaving = true;
  state.health.items = nextItems;
  const nextGroups = healthRepositoryGroups(nextItems);
  const moved = nextGroups.find((group) => group.id === focusId);
  const position = nextGroups.findIndex((group) => group.id === focusId) + 1;
  healthOrderAnnouncement = "Moved " + healthSourceName(moved) + " to position " + position + " of " + nextGroups.length + ".";
  renderHealthOrderTransition(previousLayout);
  focusHealthHandle(focusId);

  try {
    const data = await postJSON("api/health/order", { order: nextItems.map((item) => item.id) });
    if (data?.prefs) prefs = data.prefs;
    if (data?.dashboard) {
      const seq = data.dashboard.seq;
      if (typeof seq !== "number" || seq > lastAppliedSeq) {
        state = data.dashboard;
        adoptAppliedRev();
      }
    }
    loadError = null;
  } catch (error) {
    const failedLayout = captureHealthLayout();
    state.health.items = previousItems;
    healthOrderAnnouncement = "Card order was not saved.";
    loadError = String((error && error.message) || error || "Card order was not saved.");
    renderHealthOrderTransition(failedLayout);
  } finally {
    healthOrderSaving = false;
    document.getElementById("health-grid")?.classList.remove("ordering");
    document.querySelector(".view")?.classList.remove("no-motion");
    focusHealthHandle(focusId);
  }
}

function moveHealthSource(id, delta) {
  if (healthOrderSaving) return;
  const previous = currentHealthItems().slice();
  const previousGroups = healthRepositoryGroups(previous);
  const from = previousGroups.findIndex((group) => group.id === id);
  const to = Math.max(0, Math.min(previousGroups.length - 1, from + delta));
  if (from < 0 || from === to) return;
  const nextGroups = previousGroups.slice();
  const [group] = nextGroups.splice(from, 1);
  nextGroups.splice(to, 0, group);
  return commitHealthOrder(flattenHealthGroups(nextGroups), previous, id);
}

function dropHealthSource(sourceId, targetId, after) {
  if (healthOrderSaving || !sourceId || sourceId === targetId) return;
  const previous = currentHealthItems().slice();
  const previousGroups = healthRepositoryGroups(previous);
  const source = previousGroups.find((group) => group.id === sourceId);
  const nextGroups = previousGroups.filter((group) => group.id !== sourceId);
  let targetIndex = nextGroups.findIndex((group) => group.id === targetId);
  if (!source || targetIndex < 0) return;
  if (after) targetIndex++;
  nextGroups.splice(targetIndex, 0, source);
  return commitHealthOrder(flattenHealthGroups(nextGroups), previous, sourceId);
}

function wireHealthOrdering() {
  const units = Array.from(document.querySelectorAll(".health-unit[data-health-group-id]"));
  const clear = () => {
    draggedHealthId = null;
    document.getElementById("health-grid")?.classList.remove("drag-active");
    clearHealthDragMarkers();
    units.forEach((unit) => { delete unit.dataset.dropAfter; });
  };

  document.querySelectorAll("[data-health-drag]").forEach((handle) => {
    handle.addEventListener("dragstart", (event) => {
      if (healthOrderSaving) {
        event.preventDefault();
        return;
      }
      draggedHealthId = handle.dataset.healthDrag;
      if (event.dataTransfer) {
        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", draggedHealthId);
      }
      handle.setAttribute("aria-grabbed", "true");
      const unit = handle.closest(".health-unit");
      unit?.classList.add("dragging");
      document.getElementById("health-grid")?.classList.add("drag-active");
      setHealthDragImage(event, unit);
    });
    handle.addEventListener("dragend", () => {
      handle.removeAttribute("aria-grabbed");
      clear();
    });
    handle.addEventListener("keydown", (event) => {
      const horizontal = event.key === "ArrowLeft" ? -1 : event.key === "ArrowRight" ? 1 : 0;
      const vertical = event.key === "ArrowUp" ? -1 : event.key === "ArrowDown" ? 1 : 0;
      const delta = horizontal || vertical;
      if (!delta) return;
      event.preventDefault();
      moveHealthSource(handle.dataset.healthDrag, delta);
    });
  });

  units.forEach((unit) => {
    unit.addEventListener("dragover", (event) => {
      if (!draggedHealthId || draggedHealthId === unit.dataset.healthGroupId) return;
      event.preventDefault();
      if (event.dataTransfer) event.dataTransfer.dropEffect = "move";
      const rect = unit.getBoundingClientRect();
      const xDistance = Math.abs(event.clientX - (rect.left + rect.width / 2)) / rect.width;
      const yDistance = Math.abs(event.clientY - (rect.top + rect.height / 2)) / rect.height;
      const after = unit.classList.contains("health-source-group")
        ? event.clientY > rect.top + rect.height / 2
        : xDistance > yDistance
          ? event.clientX > rect.left + rect.width / 2
          : event.clientY > rect.top + rect.height / 2;
      setHealthDropMarker(unit, after);
    });
    unit.addEventListener("drop", (event) => {
      event.preventDefault();
      const sourceId = draggedHealthId || event.dataTransfer?.getData("text/plain");
      const after = unit.dataset.dropAfter === "true";
      const targetId = unit.dataset.healthGroupId;
      clear();
      dropHealthSource(sourceId, targetId, after);
    });
  });
}

// PR and health action buttons post only enough identity to let the loopback server resolve
// the canonical item from its trusted snapshot. 'split' carries the data-* attributes and
// 'target' selects the current session or a mapped repository session. We give inline feedback
// and deliberately do not re-render because the action changes the conversation, not the data.
async function onCardAction(split, target) {
  const mainBtn = split.querySelector(".cb-main") || split;
  const caret = split.querySelector(".cb-caret");
  if (mainBtn.classList.contains("busy") || mainBtn.classList.contains("done")) return;
  const d = split.dataset;
  const t = target || "new-session";
  // Guard across re-renders too: if a streamed 'state' event replaced this card while an
  // earlier click's POST was still pending, the fresh button carries no .busy class, but the
  // pending key still does — so refuse to double-queue the same action (see inflightActions).
  const healthAction = !!d.sourceId;
  if (standalone && healthAction && !prefs?.sessionLauncher?.selectedRepositoryUrl) {
    const source = state?.health?.items?.find(item => item.id === d.sourceId);
    if (source && !source.canOpenRepoSession) {
      sessionSettingsError = "Choose a GitHub App project for this health source, then retry the action.";
      goView("settings");
      document.getElementById("session-project")?.focus();
      return;
    }
  }
  const key = healthAction
    ? actionKey(d.kind, d.sourceId)
    : actionKey(d.kind, d.prUrl, d.prRepo, d.prNumber);
  if (inflightActions.has(key)) return;
  const body = healthAction
    ? { kind: d.kind, target: t, source: { id: d.sourceId } }
    : {
        kind: d.kind,
        target: t,
        pr: {
          url: d.prUrl,
          number: Number(d.prNumber),
          repository: d.prRepo,
          title: d.prTitle,
          author: d.prAuthor,
        },
      };
  // If a prior attempt failed, the button was re-enabled immediately but still shows the failure
  // label under a pending ~3.2s restore timer (see the catch below). A retry that lands inside that
  // window would otherwise (a) inherit the .failed styling, (b) capture the failure HTML as its
  // "original" so a later restore reverts to the wrong text, and (c) have its own outcome label
  // clobbered when the stale timer fires mid-flight. Cancel that timer and restore the default label
  // now so this attempt starts from a clean slate.
  if (mainBtn._cbRestore) {
    clearTimeout(mainBtn._cbRestore.timer);
    mainBtn.classList.remove("failed");
    mainBtn.innerHTML = mainBtn._cbRestore.original;
    mainBtn._cbRestore = null;
  }
  const original = mainBtn.innerHTML;
  mainBtn.classList.add("busy");
  mainBtn.disabled = true;
  if (caret) caret.disabled = true;
  inflightActions.add(key);
  try {
    const res = await postJSON(healthAction ? "api/health/action" : "api/agent/action", body);
    if (standalone) {
      const url = new URL(res.appUrl || res.url);
      if (url.protocol !== "ghapp:" || url.hostname !== "session" || url.pathname !== "/new") {
        throw new Error("The server returned an invalid session link.");
      }
      // A real click avoids popup blockers after the asynchronous request and makes the
      // handoff explicit. The app still asks for confirmation before creating a session.
      const link = document.createElement("a");
      link.className = "card-btn";
      link.href = url.href;
      link.textContent = "Open in GitHub App";
      link.title = "Review and confirm the new session in GitHub App";
      mainBtn.replaceWith(link);
      if (caret) caret.hidden = true;
      return;
    }
    mainBtn.classList.remove("busy");
    mainBtn.classList.add("done");
    if (caret) caret.classList.add("done");
    // Label truthfully: if the agent was mid-task the prompt is queued behind it, so
    // don't claim it already started. Otherwise reflect where it's headed — a new
    // session in the PR's repo, or this very session. Use the server's EFFECTIVE target
    // (res.target), not the requested one: a GHES card degrades new-session to
    // current-session server-side, so the requested 't' can overstate what actually ran.
    const effTarget = (res && res.target) || t;
    const label = res && res.queued
      ? "Queued \u2014 starts after current task"
      : (effTarget === "current-session" ? "Running in this session" : (d.doneLabel || "Requested"));
    mainBtn.innerHTML = '<span class="cb-ico">' + ICONS.check + '</span><span class="cb-label">' + esc(label) + "</span>";
  } catch (e) {
    mainBtn.classList.remove("busy");
    mainBtn.classList.add("failed");
    mainBtn.disabled = false;
    if (caret) caret.disabled = false;
    mainBtn.innerHTML = '<span class="cb-ico">' + ICONS.x + '</span><span class="cb-label">' + esc(String((e && e.message) || "Failed")) + "</span>";
    // Restore the original label after a beat so the user can retry. Track the timer + original on
    // the element so a retry landing inside this window can cancel it (see the top of this function)
    // instead of letting a stale timer overwrite the retry's outcome label.
    mainBtn._cbRestore = {
      original,
      timer: setTimeout(() => { mainBtn.classList.remove("failed"); mainBtn.innerHTML = original; mainBtn._cbRestore = null; }, 3200),
    };
  } finally {
    // Clear the pending key once the request settles. The button keeps its own done/failed
    // state; a subsequent SSE re-render restores the default (now re-enabled) button.
    inflightActions.delete(key);
    // If an SSE 'state' event re-rendered this card mid-request, our done/failed feedback landed on
    // the now-detached old nodes, and the visible replacement was rendered disabled while the key was
    // still pending. Re-render so it reflects the just-cleared inflight state instead of staying stuck
    // disabled until the next SSE refresh. When the split is still connected we keep the deliberate
    // no-re-render behavior above so the inline confirmation survives.
    //
    // Only recover while the queue is showing. A split also goes disconnected when the user opens
    // Accounts/Settings/Filters mid-request; those views have no replacement action button to unlock,
    // and an unconditional render() there would rebuild the open form and discard text the user has
    // not committed yet. goView() re-renders the queue when they navigate back, so nothing stays stuck.
    if (!split.isConnected && view === "queue") { render(); }
  }
}

// Close any open card-action dropdown and reset its caret. Called on outside click, Esc,
// scroll, resize, and whenever an action fires.
function closeCbMenus() {
  document.querySelectorAll(".cb-menu").forEach((m) => {
    // Menus are portaled to <body> while open (see openCbMenu). Return the menu to its
    // owning split so it is torn down with the card on the next re-render instead of
    // leaking as a detached <body> orphan; drop it outright if the split is already gone.
    if (m.parentElement === document.body) {
      const owner = m.__ownerSplit;
      if (owner && owner.isConnected) owner.appendChild(m);
      else m.remove();
    }
    m.hidden = true;
  });
  document.querySelectorAll('.cb-caret[aria-expanded="true"]').forEach((c) => c.setAttribute("aria-expanded", "false"));
}

// Open a split-button dropdown as a viewport-fixed overlay. The menu is portaled to <body>
// before positioning: the .view element runs a forward-filling keyframe animation
// (animation-fill-mode: both) whose frames include a transform, and a filling transform
// animation establishes a containing block for position:fixed descendants in Blink/WebKit.
// Left inside .view, the menu's fixed coordinates resolve against .view (which starts below
// the sticky topbar) rather than the viewport, dropping the menu ~1 topbar-height below the
// button. Anchoring it to <body> (which has no transformed ancestor) restores viewport-
// relative fixed positioning. We measure the caret and menu, left-align the menu under the
// split, then flip up/right when it would spill past the viewport.
function openCbMenu(split, caret, menu) {
  const splitRect = split.getBoundingClientRect();
  const caretRect = caret.getBoundingClientRect();
  // Portal to <body> so no transformed/filtered ancestor governs the fixed menu. Remember
  // the owning split so closeCbMenus can restore it.
  menu.__ownerSplit = split;
  document.body.appendChild(menu);
  // Reveal off-paint so offsetWidth/Height are measurable before we place it.
  menu.hidden = false;
  menu.style.visibility = "hidden";
  const mw = menu.offsetWidth;
  const mh = menu.offsetHeight;
  const pad = 8;
  const vw = document.documentElement.clientWidth || window.innerWidth;
  const vh = document.documentElement.clientHeight || window.innerHeight;

  let left = splitRect.left;
  if (left + mw > vw - pad) left = vw - mw - pad;
  if (left < pad) left = pad;

  // Prefer below the caret; flip above when it would overflow the bottom and there is
  // more room up top.
  let top = caretRect.bottom + 5;
  if (top + mh > vh - pad && caretRect.top - mh - 5 > pad) top = caretRect.top - mh - 5;

  menu.style.left = Math.round(left) + "px";
  menu.style.top = Math.round(top) + "px";
  menu.style.visibility = "";
  caret.setAttribute("aria-expanded", "true");
  // Move focus into the menu so keyboard users land on the choices (the menu was portaled to
  // the end of <body>, so a bare Tab would otherwise skip past it in document order). Remember
  // the caret so Escape/Tab can restore focus to it when the menu closes (see the keydown
  // handler wired in render()).
  menu.__ownerCaret = caret;
  const firstItem = menu.querySelector(".cb-menu-item");
  if (firstItem && typeof firstItem.focus === "function") firstItem.focus();
}

// Persist one account's repos without a full refresh/broadcast (the editor owns
// the DOM and a re-render would interrupt typing). The editor is optimistic, so a
// failed save reverts to the previous draft and shows the API error beside the row.
function persistAccountRepos(id, previousRepos) {
  const repos = (draftReposByAcct[id] || []).slice();
  const seq = (repoSaveSeqByAcct[id] || 0) + 1;
  repoSaveSeqByAcct[id] = seq;
  return postJSON("api/account/repos", { id, repos }).then((data) => {
    if (repoSaveSeqByAcct[id] !== seq) return data;
    // Gate the adoption on seq like every other response path (load/withRefresh/SSE/goView): a save
    // that resolves after a newer refresh or pushed snapshot already applied must not roll state
    // (and lastAppliedSeq via adoptAppliedRev) backward. The repoSaveSeqByAcct guard above only
    // orders saves for this account against each other, not against those lastAppliedSeq-keyed paths.
    if (data && data.dashboard) {
      const dseq = data.dashboard.seq;
      if (typeof dseq !== "number" || dseq > lastAppliedSeq) adoptState(data);
    }
    repoErr(id, "");
    return data;
  }).catch((e) => {
    if (repoSaveSeqByAcct[id] === seq) {
      const msg = "Couldn't save repositories: " + String((e && e.message) || e);
      draftReposByAcct[id] = (Array.isArray(previousRepos) ? previousRepos : accountRepos(id)).slice();
      editingByAcct[id] = -1;
      renderRepoList(id);
      repoErr(id, msg);
    }
    return null;
  });
}

async function saveSettings() {
  const release = document.getElementById("release-input").value;
  const showDrafts = document.getElementById("s-drafts").checked;
  const notifications = {
    reviewRequested: document.getElementById("n-review").checked,
    readyToMerge: document.getElementById("n-ready").checked,
    changesRequested: document.getElementById("n-changes").checked,
    ciFailing: document.getElementById("n-ci").checked,
  };
  goView("queue", true);
  await withRefresh(() => postJSON("api/prefs", { release, showDrafts, notifications }));
}

async function rescanAccounts() {
  rescanning = true;
  const btn = document.getElementById("rescan-btn");
  if (btn) btn.classList.add("spin");
  try {
    const res = await apiFetch("api/accounts");
    const data = await readJson(res);
    // Gate on seq like every other response path: if a newer refresh/SSE snapshot applied while this
    // rescan was in flight, adopting it would roll state (and lastAppliedSeq) backward. When it is the
    // newest, adoptState() also advances the revision so a delayed lower-seq response cannot roll
    // the queue back.
    const dseq = data.dashboard && data.dashboard.seq;
    if (typeof dseq !== "number" || dseq > lastAppliedSeq) {
      adoptState(data);
    }
  } catch (e) {
    loadError = String((e && e.message) || e);
  } finally { rescanning = false; render(); }
}

function dismissNotif(id, cardEl) {
  if (cardEl) cardEl.classList.add("removing");
  const go = () => withRefresh(() => postJSON("api/notifications/dismiss", { id }));
  setTimeout(go, 200);
}
const dismissAll = () => withRefresh(() => postJSON("api/notifications/dismiss-all"));
const restoreNotifs = () => withRefresh(() => postJSON("api/notifications/restore"));

/* ---- navigation ---- */

function goView(next, forward) {
  if (view === "accounts" && next !== "accounts") { for (const k in editingByAcct) editingByAcct[k] = -1; }
  prevRank = RANK[view] || 0;
  view = next;
  // Returning to the queue is the moment to fold in any dashboard that streamed in while
  // the user was editing a form on another view.
  if (next === "queue" && pendingState) {
    const payload = pendingState; pendingState = null;
    // The stash can be older than a POST response (repo save, prefs) that advanced lastAppliedSeq
    // while the user was on the form: fold it in only when it is strictly newer than what we
    // already show, mirroring applyPushedState's gate. (No seq → legacy payload, apply as before.)
    const seq = payload.dashboard && payload.dashboard.seq;
    if (typeof seq !== "number" || seq > lastAppliedSeq) {
      adoptState(payload);
    }
  }
  render(forward === undefined ? undefined : forward);
}

/* ---- cards ---- */

function pill(s) { return '<span class="pill ' + (s.tone || "muted") + '">' + esc(s.label) + "</span>"; }

// Encode the PR descriptor onto the split container as data-* attributes so the click
// handler can post it back to /api/agent/action without another lookup. The whole card
// body is a link, so buttons live in a sibling row (not nested in the <a>, which is
// invalid). The main button opens a new session in the PR's repo; the caret opens a menu
// to run the same action in the current session instead.
function cardActionBtn(pr, a) {
  if (standalone && !/^https:\/\/github\.com\//i.test(pr.url || "")) {
    return '<span class="hint">GitHub App session links require github.com.</span>';
  }
  const data =
    ' data-kind="' + esc(a.kind) + '"' +
    ' data-done-label="' + esc(a.done || "Requested") + '"' +
    ' data-pr-url="' + esc(pr.url || "") + '"' +
    ' data-pr-number="' + esc(pr.number) + '"' +
    ' data-pr-repo="' + esc(pr.repository || "") + '"' +
    ' data-pr-title="' + esc(pr.title || "") + '"' +
    ' data-pr-author="' + esc(pr.author || "") + '"';
  const icon = a.icon ? '<span class="cb-ico">' + a.icon + "</span>" : "";
  // If a click's POST is still in flight when this card re-renders (e.g. from a streamed
  // 'state' event), keep the replacement split disabled so it can't re-queue the same action.
  const inflight = inflightActions.has(actionKey(a.kind, pr.url || "", pr.repository || "", pr.number));
  // While a forced refresh is finalizing, keep actions disabled so a click cannot race a user-driven
  // mode/account change whose replacement snapshot has not committed yet.
  const finalizing = refreshing && !inflight;
  const busyCls = (inflight || finalizing) ? " busy" : "";
  const spinCls = finalizing ? " spin" : "";
  const disabledAttr = (inflight || refreshing) ? " disabled" : "";
  const mainIcon = finalizing ? '<span class="cb-ico">' + ICONS.refresh + "</span>" : icon;
  const mainLabel = finalizing ? "Finalizing\u2026" : a.label;
  // open_pr_session can only target github.com, so the server degrades a new-session action on a
  // GHES/EMU (non-dotcom) PR to the current session. Detect that here from the server-resolved PR
  // url and don't advertise "Open in new session" for those cards: render a single current-session
  // button so enterprise users see the honest behavior up front instead of discovering it on click.
  const isDotcom = /^https:\/\/github\.com\//i.test(pr.url || "");
  const mainTarget = isDotcom ? "new-session" : "current-session";
  // aria-live="polite" turns the main button into a live region: onCardAction rewrites its label
  // in place to "Queued\u2026", "Running\u2026", or an error, but the button is disabled while the
  // request runs so focus may move away. Announcing politely surfaces that async result to screen
  // readers even after the button loses focus. A full re-render swaps in a fresh element with its
  // initial label, which does not announce, so only the in-place status updates are spoken.
  const mainBtn = '<button type="button" class="card-btn cb-main' + busyCls + spinCls + '" data-target="' + mainTarget + '" aria-live="polite"' + disabledAttr + '>' +
    mainIcon + '<span class="cb-label">' + esc(mainLabel) + "</span></button>";
  // A GHES/EMU card has only the current-session target, so render the lone main button with no
  // caret or menu — there is nothing to choose and no unsupported option to mislead with.
  if (!isDotcom || standalone) {
    return '<div class="cb-split"' + data + '>' + mainBtn + "</div>";
  }
  return '<div class="cb-split"' + data + '>' +
    mainBtn +
    '<button type="button" class="card-btn cb-caret" aria-haspopup="true" aria-expanded="false"' +
      ' title="Choose where to run" aria-label="Choose where to run ' + esc(a.label) + '"' + disabledAttr + '>' +
      ICONS.chev + "</button>" +
    '<div class="cb-menu" role="menu" hidden>' +
      cbMenuItem("new-session", ICONS.layers, "Open in new session", "In the PR\u2019s repo") +
      cbMenuItem("current-session", ICONS.chat, "Run in current session", "Here, in this conversation") +
    "</div>" +
  "</div>";
}

function cbMenuItem(target, icon, label, sub) {
  return '<button type="button" class="cb-menu-item" role="menuitem" tabindex="-1" data-target="' + esc(target) + '">' +
    '<span class="cb-mi-ico">' + icon + "</span>" +
    '<span class="cb-mi-text"><span class="cb-mi-label">' + esc(label) + "</span>" +
    '<span class="cb-mi-sub">' + esc(sub) + "</span></span></button>";
}

// Shared action-button descriptors, so a given agent action's label, confirmation text, and
// icon stay identical wherever it is offered. "Resolve" and "Address feedback" are the same
// underlying agent action (address-feedback): both work the unresolved review threads and
// resolve them; they differ only in the surface wording (a signal pill vs. a lane).
var CARD_ACTIONS = {
  test: { kind: "test", label: "Test", done: "Testing requested", icon: ICONS.check },
  review: { kind: "review", label: "Review", done: "Review requested", icon: ICONS.eye },
  resolveConflicts: { kind: "resolve-conflicts", label: "Resolve conflicts", done: "Sent to agent", icon: ICONS.merge },
  reviewDebt: { kind: "review-debt", label: "Address review", done: "Sent to agent", icon: ICONS.eye },
  fixCi: { kind: "fix-ci", label: "Evaluate CI failures", done: "Sent to agent", icon: ICONS.pulse },
  discussReview: { kind: "discuss-review", label: "Discuss review", done: "Sent to agent", icon: ICONS.chat },
  addressFeedback: { kind: "address-feedback", label: "Address feedback", done: "Sent to agent", icon: ICONS.check },
  resolveFeedback: { kind: "address-feedback", label: "Resolve", done: "Sent to agent", icon: ICONS.check },
};

// Combine several action lists into one, first-wins by kind, so a card that qualifies for the
// same underlying action twice (e.g. an "Unresolved feedback" lane card that also carries the
// "N unresolved" signal) shows a single button. Returns null when nothing applies, matching the
// "no actions" contract the card renderers expect.
function mergeActions() {
  var out = [], seen = {};
  for (var i = 0; i < arguments.length; i++) {
    var list = arguments[i];
    if (!list) continue;
    for (var j = 0; j < list.length; j++) {
      var a = list[j];
      if (!a || seen[a.kind]) continue;
      seen[a.kind] = true;
      out.push(a);
    }
  }
  return out.length ? out : null;
}

// Signal-driven actions available on ANY card, keyed off the danger pills the model attaches.
// Wherever a card shows one of these signals it also offers the matching action, so e.g. every
// card with a "merge conflicts" pill gets a Resolve conflicts button and every card with a
// "review debt" pill gets Address review. This is layered onto every surface by mergeActions, so a
// labelled card never renders without its button. Signal labels come from model.mjs and vary by
// surface:
//   * conflicts            -> "merge conflicts"
//   * CI failing           -> "CI failing[ \u00b7 N checks]"
//   * unresolved feedback  -> "{n} unresolved" (createAttentionSignals), "Unresolved feedback"
//                             (the bucket / focus-exclusion reason label), "{n} unresolved thread"
//                             (reviewSignal), or the "resolve feedback" action pill
//   * review debt          -> the serialized reviewDebt flag (isReviewDebtItem), with the
//                             "review debt" pill (constants.mjs reviewDebtSignalLabel) as fallback
//   * re-review            -> "re-review" (actionSignal)
// The review-oriented pills ("review debt" / "re-review") are surfaced on every card regardless of
// ownership: the user asked that a labelled card always carry its action, and a self-review of your
// own aged PR before others weigh in is still useful.
function signalActions(item) {
  // Only app-computed semantic signals authorize actions. Skip raw GitHub label pills (kind
  // "repo-label", set in model.mjs): a repo can define a label literally named "merge conflicts",
  // "re-review", or "3 unresolved", and matching that presentation text would expose a destructive
  // action the PR's structured state does not actually warrant.
  var sigs = ((item && item.signals) || []).filter(function (s) { return s && s.kind !== "repo-label"; });
  function hasSig(re) { return sigs.some(function (s) { return s && re.test(s.label || ""); }); }
  var out = [];
  if (hasSig(/conflict/i)) out.push(CARD_ACTIONS.resolveConflicts);
  if (hasSig(/^CI failing/i)) out.push(CARD_ACTIONS.fixCi);
  if (hasSig(/unresolved|resolve feedback/i)) out.push(CARD_ACTIONS.resolveFeedback);
  // Detect review debt via isReviewDebtItem (the serialized reviewDebt flag OR the pill), not the
  // pill alone: signalsFor caps a card at four pills and oldFirstSignal() emits "review debt" last,
  // so a stacked card (release + regression + CI + ...) can lose the visible pill yet still be debt.
  // Keying off the flag keeps Address review + Discuss review on every review-debt card.
  if (isReviewDebtItem(item)) { out.push(CARD_ACTIONS.reviewDebt); out.push(CARD_ACTIONS.discussReview); }
  if (hasSig(/^re-review$/i)) out.push(CARD_ACTIONS.review);
  return out;
}

// Which action buttons a "Needs attention" or "Your PRs outside" card gets. A review-debt card
// (aged without an approving review) offers Address review (a fresh review) plus Discuss review
// (talk through existing feedback) — on every card carrying the debt, including your own, so a
// review-debt card is never actionless. Focus cards carry a reviewDebt flag (isReviewDebtItem)
// because signalsFor truncates the displayed pills, so the debt is detected even when the "review
// debt" pill is not among the visible signals. For your own PR, changes requested takes precedence:
// the ball is in your court, so those cards offer Address feedback + Discuss review (mirroring the
// "Respond here" For You pick). Someone else's non-debt PR offers Test + Review. Signal-driven
// actions (conflicts, CI, unresolved) are layered on for every card, since fixing those is the
// author's job.
function focusCardActions(item) {
  var pr = (item && item.pr) || {};
  var ctx = [];
  var sig = signalActions(item);
  if (pr.isMine) {
    if (isAuthorResponseItem(item)) {
      ctx = [CARD_ACTIONS.addressFeedback, CARD_ACTIONS.discussReview];
      // Changes requested takes precedence over review debt on your own PR: you respond, you don't
      // start a review of your own aged PR. signalActions now layers review-debt off the serialized
      // flag (an aged, non-approved PR is debt even with changes requested), so drop just that action
      // here to keep the precedence. Conflict / CI / unresolved signals still layer normally.
      sig = sig.filter(function (a) { return a.kind !== CARD_ACTIONS.reviewDebt.kind; });
    } else if (isReviewDebtItem(item)) {
      ctx = [CARD_ACTIONS.reviewDebt, CARD_ACTIONS.discussReview];
    }
  } else if (isReviewDebtItem(item)) {
    ctx = [CARD_ACTIONS.reviewDebt, CARD_ACTIONS.discussReview];
  } else {
    ctx = [CARD_ACTIONS.test, CARD_ACTIONS.review];
  }
  return mergeActions(ctx, sig);
}

// "For you" picks that carry an actionable label get an interactive split button. The pick's
// action label maps to the matching agent action; "Respond here" (your PR has feedback waiting)
// offers Address feedback + Discuss review. Signal-driven actions are layered on, so a pick that
// also carries a problem signal (conflicts/CI/unresolved) still surfaces it, deduped by kind.
function forYouCardActions(item) {
  var action = item && item.action;
  var ctx = [];
  if (action === "Resolve conflicts") ctx = [CARD_ACTIONS.resolveConflicts];
  else if (action === "Fix CI") ctx = [CARD_ACTIONS.fixCi];
  else if (action === "Review this") ctx = [CARD_ACTIONS.review];
  else if (action === "Respond here") ctx = [CARD_ACTIONS.addressFeedback, CARD_ACTIONS.discussReview];
  return mergeActions(ctx, signalActions(item));
}

// Which action buttons a breakdown-lane card gets, keyed on the lane's signal bucket. Review/Test
// only make sense for a PR the viewer would review, so they are withheld on the viewer's own PRs.
// The "Merge conflicts" lane is covered by signalActions alone: createAttentionSignals hoists that
// pill right after the action signal so it always survives signalsFor's top-4 cap. The "CI failing"
// pill is NOT hoisted, so on a stacked PR (release + regression + base) it can be pushed past the
// top 4 and dropped, leaving signalActions with no CI pill to key off. So the CI lane is mapped
// explicitly here (like "Unresolved feedback"); fixing CI is the author's job, so it is offered on
// your own PRs too rather than gated on !isMine.
function laneCardActions(lane, item) {
  var pr = (item && item.pr) || {};
  var label = lane && lane.label;
  var ctx = [];
  if (label === "Needs review") {
    ctx = pr.isMine ? [] : [CARD_ACTIONS.test, CARD_ACTIONS.review];
  } else if (label === "Re-review needed" || label === "Review started" || label === "Quick wins") {
    ctx = pr.isMine ? [] : [CARD_ACTIONS.review];
  } else if (label === "Unresolved feedback") {
    ctx = [CARD_ACTIONS.addressFeedback, CARD_ACTIONS.discussReview];
  } else if (label === "CI failing") {
    ctx = [CARD_ACTIONS.fixCi];
  }
  return mergeActions(ctx, signalActions(item));
}

function isReviewDebtItem(item) {
  if (!item) return false;
  if (item.reviewDebt) return true;
  // Fall back to the pill only for an app-computed signal, never a raw GitHub label named "review
  // debt" (kind "repo-label"), so a label can't spoof the Address review / Discuss review actions.
  return (item.signals || []).some((s) => s && s.kind !== "repo-label" && s.label === "review debt");
}

// Your own PR is "author response" when a reviewer requested changes (pr.review.state), or when the
// "Your PRs outside Needs attention" lane tagged the card with the "Author response" exclusion pill.
// Either way the ball is in your court, so focusCardActions offers Address feedback + Discuss review.
function isAuthorResponseItem(item) {
  var pr = (item && item.pr) || {};
  if (pr.review && pr.review.state === "changes_requested") return true;
  return ((item && item.signals) || []).some((s) => s && /^Author response$/i.test(s.label || ""));
}

function prCard(item, actions) {
  const pr = item.pr;
  const main = '<a class="card-main" href="' + esc(pr.url) + '" target="_blank" rel="noreferrer">' +
    '<div class="card-top"><div class="card-title">' + esc(pr.title) + "</div></div>" +
    '<div class="card-sub">' +
      avatarTag(pr.authorAvatarUrl, pr.author, "avatar", 36) +
      '<span class="repo">' + esc(shortRepo(pr.repository)) + " #" + pr.number + "</span>" +
      "<span>by " + esc(pr.author) + "</span>" +
    "</div>" +
    (item.reason ? '<div class="reason">' + esc(item.reason) + "</div>" : "") +
    ((item.signals && item.signals.length) ? '<div class="pills">' + item.signals.map(pill).join("") + "</div>" : "") +
  "</a>";
  const acts = (actions && actions.length)
    ? '<div class="card-actions">' + actions.map((a) => cardActionBtn(pr, a)).join("") + "</div>"
    : "";
  return '<div class="card">' + main + acts + "</div>";
}

function issueCard(item) {
  const is = item.issue;
  const main = '<a class="card-main" href="' + esc(is.url) + '" target="_blank" rel="noopener noreferrer">' +
    '<div class="card-top"><div class="card-title">' + esc(is.title) + "</div></div>" +
    '<div class="card-sub">' +
      avatarTag(is.authorAvatarUrl, is.author, "avatar", 36) +
      '<span class="repo">' + esc(shortRepo(is.repository)) + " #" + is.number + "</span>" +
      "<span>by " + esc(is.author) + "</span>" +
    "</div>" +
    ((item.signals && item.signals.length) ? '<div class="pills">' + item.signals.map(pill).join("") + "</div>" : "") +
  "</a>";
  const linkedPullRequests = (is.linkedPullRequests || []).filter((pr) => pr.state !== "CLOSED");
  const linked = linkedPullRequests.length
    ? linkedPullRequests.map((pr) => {
        const state = pr.state === "MERGED" ? "merged" : (pr.state === "OPEN" ? "open" : "unknown");
        const stateLabel = state === "merged" ? "Merged" : (state === "open" ? "Open" : "Linked");
        const stateIcon = state === "merged" ? ICONS.merge : ICONS.pr;
        return (
        '<a class="card-main linked-pr" href="' + esc(pr.url) + '" target="_blank" rel="noopener noreferrer" title="' + stateLabel + " pull request " +
          esc(shortRepo(pr.repository)) + " #" + pr.number + '" aria-label="' + stateLabel + " pull request: " + esc(pr.title) + '">' +
          '<span class="linked-pr-icon ' + state + '">' + stateIcon + '</span><span class="linked-pr-title">' + esc(pr.title) + "</span></a>"
        );
      }).join("")
    : "";
  return '<div class="card">' + main + linked + "</div>";
}

function laneIcon(lane) {
  switch (lane.id) {
    case "review-queue": case "needs-review": case "assigned": return ICONS.eye;
    case "ready-to-merge": case "ready": return ICONS.merge;
    case "ci-failing": case "blocked": return ICONS.xcircle;
    case "unresolved": return ICONS.chat;
    case "your-prs": case "yours": case "in-progress": return ICONS.pr;
    case "triage": return ICONS.tag;
    case "active": return ICONS.clock;
    default: return ICONS.dot2;
  }
}

function laneHtml(lane) {
  const items = (lane.items || []).map((it) => (it.pr ? prCard(it, laneCardActions(lane, it)) : issueCard(it))).join("");
  const tone = lane.tone || "muted";
  const repos = new Set((lane.items || []).map((it) => (it.pr || it.issue || {}).repository).filter(Boolean));
  const repoLabel = repos.size + (repos.size === 1 ? " repo" : " repos");
  const capped = typeof lane.cappedTotal === "number" && lane.cappedTotal > lane.items.length;
  const detail = capped
    ? "top " + lane.items.length + " of " + lane.cappedTotal
    : repoLabel;
  const collapsed = collapsedLanes.has(lane.id);
  return '<section class="lane' + (collapsed ? " collapsed" : "") + '" data-lane="' + esc(lane.id) + '">' +
    '<button class="lane-head" data-lane-toggle="' + esc(lane.id) + '" aria-expanded="' + (collapsed ? "false" : "true") + '">' +
      '<span class="lane-ico t-' + tone + '">' + laneIcon(lane) + "</span>" +
      '<span class="lane-title">' + esc(lane.label) + "</span>" +
      '<span class="lane-count">' + lane.items.length + "</span>" +
      '<span class="lane-detail' + (capped ? " capped" : "") + '">' + detail + "</span>" +
      '<span class="lane-caret">' + ICONS.chev + "</span>" +
    "</button>" +
    '<div class="lane-body"><div class="inner"><div class="grid">' + items + "</div></div></div>" +
  "</section>";
}

/* ---- review attention board (full pr-dashboard parity) ---- */

function slugId(s) {
  return "b-" + String(s).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
}

// Buckets and the long Community list start collapsed so the focused queue stays the
// headline. Seeded once; after that the user's own toggles win.
let reviewDefaultsSeeded = false;
function seedReviewCollapse(att) {
  if (reviewDefaultsSeeded) return;
  reviewDefaultsSeeded = true;
  for (const b of att.buckets || []) collapsedLanes.add(slugId(b.label));
  collapsedLanes.add("community");
  collapsedLanes.add("attention-breakdown");
}

function developerCountsHtml(list) {
  if (!list || !list.length) return "";
  const rows = list.map((d) =>
    '<div class="dev-row">' +
      avatarTag(d.avatarUrl || null, d.actor, "dev-av", 36) +
      '<div class="dev-main">' +
        '<span class="dev-name">' + esc(d.actor) + "</span>" +
        (d.latestUpdatedAt ? '<span class="dev-when">updated ' + timeAgo(d.latestUpdatedAt) + "</span>" : "") +
      "</div>" +
      '<span class="dev-count">' + d.openPullRequestCount + "</span>" +
    "</div>"
  ).join("");
  const activeCount = list.length;
  const totalPrs = list.reduce((n, d) => n + (d.openPullRequestCount || 0), 0);
  return collapsibleSect({
    id: "core-team",
    title: "Core team open PRs",
    icon: ICONS.usersSm,
    iconTone: "accent",
    count: totalPrs,
    note: activeCount + " active author" + (activeCount === 1 ? "" : "s"),
    subtitle: "Who is carrying open work across the loaded queue right now.",
    body: '<div class="dev-counts">' + rows + "</div>",
  });
}

// A collapsible secondary reference group (Community / Core team / Attention
// breakdown). Quieter header than the primary queues; collapse state persists.
function collapsibleSect(opts) {
  const id = opts.id;
  const collapsed = collapsedLanes.has(id);
  const count = (opts.count != null)
    ? '<span class="sect-count">' + esc(String(opts.count)) + "</span>" : "";
  return '<section class="sect-group collapsible' + (collapsed ? " collapsed" : "") + '" data-sect="' + esc(id) + '">' +
    '<button class="sect-head" data-collapse="' + esc(id) + '" aria-expanded="' + (collapsed ? "false" : "true") + '">' +
      (opts.icon ? '<span class="sect-icon t-' + (opts.iconTone || "muted") + '">' + opts.icon + "</span>" : "") +
      '<span class="sect-title">' + esc(opts.title) + "</span>" +
      (opts.note ? '<span class="sect-note">' + esc(opts.note) + "</span>" : "") +
      count +
      '<span class="sect-caret t-' + (opts.iconTone || "muted") + '">' + ICONS.chev + "</span>" +
    "</button>" +
    '<div class="collapse-body"><div class="collapse-inner">' +
      (opts.subtitle ? '<p class="sect-sub">' + esc(opts.subtitle) + "</p>" : "") +
      (opts.body || "") +
    "</div></div>" +
  "</section>";
}

// A clean, always-expanded primary queue (For you / Needs attention / Your PRs
// outside). Masonry cards with a title, count metric, and descriptive subtitle.
function queuePanel(opts) {
  const items = opts.items || [];
  const n = items.length;
  // exactCount: the shown items are not a prefix of the source list (e.g. the focus lane keeps
  // review-debt cards that spill past the cap), so "top N of total" would be false — higher-ranked
  // cards were skipped. Report the honest shown count instead; the uncapped total still shows in
  // the header summary badge.
  const capped = !opts.exactCount && typeof opts.cappedTotal === "number" && opts.cappedTotal > n;
  const metric = capped ? "top " + n + " of " + opts.cappedTotal : n + " shown";
  const cardActions = typeof opts.cardActions === "function" ? opts.cardActions : null;
  const body = n
    ? '<div class="grid">' + items.map((it) => (it.pr ? prCard(it, cardActions ? cardActions(it) : null) : issueCard(it))).join("") + "</div>"
    : '<div class="lane-empty">' + esc(opts.emptyText || "Nothing here right now.") + "</div>";
  const collapsed = collapsedLanes.has(opts.id);
  return '<section class="qpanel collapsible' + (collapsed ? " collapsed" : "") + '" data-q="' + esc(opts.id) + '">' +
    '<button class="qhead" data-collapse="' + esc(opts.id) + '" aria-expanded="' + (collapsed ? "false" : "true") + '">' +
      '<span class="qdot t-' + (opts.tone || "muted") + '">' + (opts.icon || ICONS.dot2) + "</span>" +
      '<span class="qtitle">' + esc(opts.title) + "</span>" +
      '<span class="qmetric' + (capped ? " capped" : "") + '">' + metric + "</span>" +
      '<span class="qcaret t-' + (opts.tone || "muted") + '">' + ICONS.chev + "</span>" +
    "</button>" +
    '<div class="collapse-body"><div class="collapse-inner qbody">' +
      (opts.subtitle ? '<p class="qsub">' + esc(opts.subtitle) + "</p>" : "") +
      body +
    "</div></div>" +
  "</section>";
}

function reviewBoardHtml() {
  const att = state.attention;
  seedReviewCollapse(att);
  let html = "";

  // 1. For you — personalized headline of your highest-leverage actions.
  if (att.forMe && att.forMe.length) {
    html += queuePanel({
      id: "for-you", title: "For you", tone: "accent", icon: ICONS.sparkle,
      subtitle: "Your highest-leverage actions across every repo, pulled to the top of the queue.",
      items: att.forMe.slice(0, 6), cappedTotal: att.forMe.length,
      cardActions: forYouCardActions,
    });
  }

  // 2. Needs attention — the focused, team-managed review queue (the headline).
  html += queuePanel({
    id: "needs-attention", title: "Needs attention", tone: "danger", icon: ICONS.alertSm,
    subtitle: "One actionable row per PR with fresh activity, waiting on a review or a merge.",
    items: att.focus || [], cappedTotal: att.focusTotal, exactCount: !!att.focusMixed,
    cardActions: focusCardActions,
    emptyText: "Nothing is waiting on a reviewer right now \u00b7 anything blocked sits in the breakdown below.",
  });

  // 3. Your PRs outside Needs attention — the viewer's own out-of-queue work,
  //    sat directly under the focused queue so it's easy to see what got filtered.
  html += queuePanel({
    id: "outside-focus", title: "Your PRs outside Needs attention", tone: "info", icon: ICONS.pr,
    subtitle: "Open non-draft PRs you authored that do not currently qualify for the focused queue.",
    items: (att.focusExclusions || []).slice(0, 10), cappedTotal: (att.focusExclusions || []).length,
    cardActions: focusCardActions,
    emptyText: "None right now \u00b7 every open PR you authored is already in the queue or still in draft.",
  });

  // 4. Community — external-contributor PRs, collapsible reference group.
  if (att.community && att.community.length) {
    const repos = new Set(att.community.map((c) => (c.pr || {}).repository).filter(Boolean));
    html += collapsibleSect({
      id: "community",
      title: "Community",
      icon: ICONS.globe,
      iconTone: "success",
      count: att.community.length,
      note: "external contributors \u00b7 " + repos.size + (repos.size === 1 ? " repo" : " repos"),
      subtitle: "Recently active external-contributor PRs, tracked apart from the core-team queue.",
      body: '<div class="grid">' + att.community.map((it) => (it.pr ? prCard(it, signalActions(it)) : issueCard(it))).join("") + "</div>",
    });
  }

  // 5. Core team open PRs — who is carrying open work right now.
  html += developerCountsHtml(att.developerCounts);

  // 6. Attention breakdown — every signal lane, collapsed by default, at the bottom.
  const buckets = (att.buckets || []).filter((b) => b.items && b.items.length);
  if (buckets.length) {
    const total = buckets.reduce((n, b) => n + b.items.length, 0);
    html += collapsibleSect({
      id: "attention-breakdown",
      title: "Attention breakdown",
      icon: ICONS.layers,
      iconTone: "warning",
      count: total,
      note: buckets.length + " lane" + (buckets.length === 1 ? "" : "s"),
      subtitle: "Every signal lane behind the queue. Expand one to see the PRs it groups.",
      body: buckets.map((b) => laneHtml({ id: slugId(b.label), label: b.label, tone: b.tone, items: b.items })).join(""),
    });
  }

  return html;
}

/* ---- topbar ---- */

function tabs() {
  const mode = state ? state.mode : "review";
  const defs = [["review", "Review"], ["issues", "Issues"], ["ship", "Ship"], ["health", "Health"]];
  return '<div class="tabs">' + defs.map(([id, label]) =>
    '<button class="tab ' + (mode === id ? "active" : "") + '" data-mode="' + id + '">' + label + "</button>"
  ).join("") + "</div>";
}

function avatarStack(accts, size) {
  if (!accts || !accts.length) return "";
  const shown = accts.slice(0, 3);
  return '<span class="stack">' + shown.map((a) =>
    '<span class="stk-av' + (a.enterprise ? " ent" : "") + '" title="' +
      esc(a.login + (a.enterprise ? " \u00b7 " + (a.host || "Enterprise") : "")) + '">' +
      acctAvatar(a, size) +
      (a.enterprise ? '<span class="ent-dot" title="Enterprise">' + ICONS.building + "</span>" : "") +
    "</span>"
  ).join("") + (accts.length > shown.length ? '<span class="stk-more">+' + (accts.length - shown.length) + "</span>" : "") + "</span>";
}

function accountChip() {
  const active = (state && state.activeAccounts) || [];
  const anyEnt = active.some((a) => a.enterprise);
  let inner;
  if (!active.length) {
    inner = '<span class="sdot bg-muted"></span><span class="name">Accounts</span>';
  } else if (active.length === 1) {
    inner = avatarStack(active, 40) + '<span class="name">' + esc(active[0].login) + "</span>" +
      (anyEnt ? '<span class="ent-badge sm" title="GitHub Enterprise">' + ICONS.building + "Enterprise</span>" : "");
  } else {
    inner = avatarStack(active, 40) + '<span class="name">' + active.length + " accounts</span>" +
      (anyEnt ? '<span class="ent-badge sm" title="Includes a GitHub Enterprise account">' + ICONS.building + "</span>" : "");
  }
  return '<button class="acct-chip ' + (view === "accounts" ? "active" : "") + '" id="acct-btn" title="GitHub accounts">' +
    inner + ICONS.chev + "</button>";
}

function topbarHtml() {
  const notifCount = (state && state.notifications || []).length;
  const autoApply = autoApplyEnabled();
  const showUpdate = !!updateAvailable && !autoApply;
  const left = view === "queue"
    ? tabs()
    : '<button class="backbtn" id="back-btn">' + ICONS.back + "Back</button>";
  const right =
    (state ? accountChip() : "") +
    '<div class="tb-actions">' +
    '<button class="iconbtn live-tooltip ' + (refreshing ? "spin" : "") + '" id="refresh-btn" aria-label="' + refreshTooltip() +
      '" data-tooltip="' + refreshTooltip() + '">' + ICONS.refresh + "</button>" +
    '<button class="update-ready ' + (applyingUpdate ? "busy" : "") + '" id="apply-update-btn" type="button" ' +
      (showUpdate ? "" : "hidden ") + (applyingUpdate ? "disabled " : "") +
      'title="Apply the latest completed background update"><span class="update-dot"></span>Apply<span class="update-detail"> update</span></button>' +
    '<button class="refresh-pref ' + (autoApply ? "active" : "") + '" id="auto-apply-btn" type="button" role="switch" aria-checked="' +
      (autoApply ? "true" : "false") + '" title="' +
      (autoApply ? "Background updates apply automatically" : "Background updates wait until you apply them") +
      '"><span class="status-dot"></span><span class="refresh-label">Auto</span></button>' +
    '<button class="iconbtn ' + (view === "filters" ? "active" : "") + '" id="filters-btn" title="What\u2019s filtered">' + ICONS.funnel + "</button>" +
    '<button class="iconbtn ' + (view === "notifications" ? "active" : "") + '" id="bell-btn" title="Notifications">' + ICONS.bell +
      (notifCount ? '<span class="badge">' + notifCount + "</span>" : "") + "</button>" +
    '<button class="iconbtn ' + (view === "settings" ? "active" : "") + '" id="gear-btn" title="Settings">' + ICONS.gear + "</button>" +
    "</div>";
  return '<div class="topbar" id="topbar">' +
    '<button class="brand" id="brand-home" type="button" title="Back to review queue"><span class="mark">' + LOGO + '</span><span class="brand-text">Aspire Team App</span></button>' +
    left + '<span class="spacer"></span>' + right + "</div>";
}

/* ---- views ---- */

const HEALTH_STATUS = {
  healthy: { label: "Healthy", tone: "success" },
  running: { label: "Running", tone: "warning" },
  degraded: { label: "Degraded", tone: "warning" },
  failing: { label: "Failing", tone: "danger" },
  unavailable: { label: "Unavailable", tone: "muted" },
  unknown: { label: "Unknown", tone: "muted" },
};

function healthMeta(item) {
  return HEALTH_STATUS[item && item.state] || HEALTH_STATUS.unknown;
}

function healthRelativeTime(value) {
  if (!value || !Number.isFinite(new Date(value).getTime())) return "";
  return timeAgo(value);
}

function healthLastSuccess(item) {
  if (typeof item.daysSinceSuccess === "number") {
    return item.daysSinceSuccess === 0 ? "Today" : item.daysSinceSuccess + "d ago";
  }
  return "Not found";
}

function healthActionBtn(item, kind, label, target) {
  if (standalone) {
    target = "new-session";
    label = kind === "diagnose-health" ? "Diagnose in GitHub App" : "Fix in GitHub App";
  }
  const key = actionKey(kind, item.id);
  const busy = inflightActions.has(key);
  const icon = kind === "diagnose-health" ? ICONS.pulse : ICONS.sparkle;
  const doneLabel = target === "new-session" ? "Repo session requested" : "Requested";
  return '<span class="cb-split" data-kind="' + esc(kind) + '" data-source-id="' + esc(item.id) +
    '" data-done-label="' + esc(doneLabel) + '"><button class="card-btn cb-main' + (busy ? ' busy spin' : '') +
    '" type="button" data-target="' + esc(target) + '"' + (busy ? ' disabled aria-busy="true"' : '') +
    '><span class="cb-ico">' + (busy ? ICONS.refresh : icon) + '</span><span class="cb-label">' +
    esc(label) + "</span></button></span>";
}

function healthLatest(item) {
  if (!item.latest) return '<div class="health-latest">No recent validation signal is available.</div>';
  const latest = item.latest;
  const ago = healthRelativeTime(latest.at);
  let primary;
  if (item.provider === "github") {
    const sha = String(latest.id || "").slice(0, 7);
    primary = (sha ? "Commit " + sha : "Default-branch head") + (latest.actor ? " by " + latest.actor : "");
  } else {
    const result = latest.result || latest.status;
    primary = "Build " + (latest.number || latest.id || "") + (result ? " (" + result + ")" : "");
  }
  return '<div class="health-latest"><div class="health-latest-line"><b>' + esc(primary) + '</b>' +
    (ago ? '<time datetime="' + esc(latest.at || "") + '">' + esc(ago) + "</time>" : "") + "</div>" +
    (latest.message ? '<span class="commit-message" title="' + esc(latest.message) + '">' +
      esc(latest.message) + "</span>" : "") + "</div>";
}

function healthReasons(item) {
  const reasons = Array.isArray(item.reasons) ? item.reasons : [];
  const content = (reason) => {
    const summary = esc(reason.summary || "Health evidence is unavailable.");
    return reason.url
      ? '<a href="' + safeHref(reason.url) + '" target="_blank" rel="noreferrer">' + summary + "</a>"
      : "<span>" + summary + "</span>";
  };
  const primary = reasons[0] || {
    summary: item.state === "healthy" ? "Latest validation succeeded." : "No additional diagnostic evidence is available.",
  };
  const secondary = reasons.slice(1, 3);
  const icon = item.state === "healthy"
    ? ICONS.check
    : item.state === "failing"
      ? ICONS.alertSm
      : item.state === "running" || item.state === "degraded"
        ? ICONS.clock
        : ICONS.pulse;
  return '<div class="health-reasons"><div class="health-reason-banner"><span class="health-reason-icon">' +
    icon + '</span><div class="health-reason-content"><p class="health-primary-reason">' + content(primary) + "</p>" +
    (secondary.length
      ? '<ul class="health-secondary-reasons">' + secondary.map((reason) => "<li>" + content(reason) + "</li>").join("") + "</ul>"
      : "") + "</div></div></div>";
}

function healthEvidence(item) {
  const evidence = Array.isArray(item.evidence) ? item.evidence : [];
  if (!evidence.length) return "";
  const count = Math.min(evidence.length, 5);
  return '<details class="health-details"><summary><span>Evidence (' + count +
    ')</span><span class="health-details-chevron" aria-hidden="true">' + ICONS.chev + '</span></summary><div class="health-evidence">' +
    evidence.slice(0, 5).map((entry) =>
    '<a href="' + safeHref(entry.url || item.url) + '" target="_blank" rel="noreferrer"><span class="ev-label">' +
    esc(entry.label || "Evidence") + '</span><span class="ev-detail">' + esc(entry.detail || "") + "</span></a>"
  ).join("") + "</div></details>";
}

function healthCard(item, index, total, options = {}) {
  const meta = healthMeta(item);
  const provider = item.provider === "azure-devops" ? "Azure DevOps" : "GitHub";
  const providerIcon = item.provider === "azure-devops" ? ICONS.building : ICONS.pulse;
  const branch = String(item.branch || "Unknown").replace(/^refs\/heads\//, "");
  const grouped = !!options.grouped;
  const showHandle = options.showHandle !== false;
  const groupId = options.groupId || item.groupId || item.id;
  const orderName = options.groupName || item.groupName || item.name || item.repository || "health source";
  const titleText = grouped && item.provider === "github"
    ? "Default branch"
    : item.name || item.repository || "Health source";
  const title = item.url
    ? '<a href="' + safeHref(item.url) + '" target="_blank" rel="noreferrer">' +
      esc(titleText) + "</a>"
    : "<span>" + esc(titleText) + "</span>";
  let providerContext = item.provider === "azure-devops" && item.organizationName && item.project
    ? provider + " \u00b7 " + item.organizationName + "/" + item.project
    : item.provider === "github" && item.host
      ? provider + " \u00b7 " + item.host
      : provider;
  if (item.provider === "azure-devops" && item.discovered) {
    const discoveryKind = item.discovery?.kind;
    const discoveryLabel = discoveryKind === "official-default"
      ? "Official default"
      : discoveryKind === "azure-cli-default" || !discoveryKind
        ? "Auto\u2011discovered"
        : null;
    if (discoveryLabel) providerContext += " \u00b7 " + discoveryLabel;
  }
  const streak = item.failureStreak > 0
    ? String(item.failureStreak) + (item.failureStreakLowerBound ? "+" : "")
    : "0";
  const actionTarget = item.canOpenRepoSession ? "new-session" : "current-session";
  const fixLabel = item.canOpenRepoSession ? "Fix in repo" : "Work fix here";
  const handle = showHandle
    ? '<button class="health-drag" type="button" draggable="true" data-health-drag="' +
      esc(groupId) + '" aria-label="Reorder ' + esc(orderName) +
      ". Position " + (index + 1) + " of " + total +
      '." aria-describedby="health-order-help" title="Drag to reorder. Arrow keys also move this group."></button>'
    : "";
  const orderStatus = showHandle
    ? '<span class="health-order-status" aria-hidden="true">' + (index + 1) + " of " + total + "</span>"
    : "";

  return '<article class="health-card ' + esc(item.state || "unknown") + (grouped ? " grouped-source" : "") +
    '" data-health-id="' + esc(item.id) + '">' +
    '<div class="health-card-top">' + handle +
    '<span class="health-provider">' + providerIcon + '</span><div class="health-title">' +
    title + '<span class="provider-name">' + esc(providerContext) + '</span></div><span class="health-state"><span class="health-state-dot"></span>' +
    esc(meta.label) + "</span></div><div class=\"health-card-body\">" +
    healthLatest(item) +
    healthReasons(item) +
    '<div class="health-metrics"><div class="health-metric"><span class="k">Last success</span><span class="v">' +
    esc(healthLastSuccess(item)) + '</span></div><div class="health-metric"><span class="k">Failure streak</span><span class="v">' +
    esc(streak) + '</span></div><div class="health-metric"><span class="k">Branch</span><span class="v" title="' +
    esc(branch) + '">' + esc(branch) + "</span></div></div>" +
    healthEvidence(item) + "</div>" +
    '<div class="health-actions">' + healthActionBtn(item, "diagnose-health", "Diagnose here", "current-session") +
    healthActionBtn(item, "fix-health", fixLabel, actionTarget) +
    orderStatus + "</div></article>";
}

function healthGroup(group, index, total) {
  if (group.items.length === 1) {
    return '<div class="health-unit health-unit-single" data-health-group-id="' + esc(group.id) + '">' +
      healthCard(group.items[0], index, total, { groupId: group.id, groupName: group.name }) + "</div>";
  }

  const title = group.url
    ? '<a href="' + safeHref(group.url) + '" target="_blank" rel="noreferrer">' + esc(group.name) + "</a>"
    : "<span>" + esc(group.name) + "</span>";
  const match = group.match === "name"
    ? '<span class="health-group-match" title="Grouped because the Azure DevOps repository name uniquely matches this watched GitHub repository.">Repository name match</span>'
    : group.match === "provider"
      ? '<span class="health-group-match" title="Grouped using repository metadata reported by the provider.">Provider linked</span>'
      : "";
  return '<section class="health-unit health-source-group" data-health-group-id="' + esc(group.id) + '">' +
    '<header class="health-group-head"><button class="health-drag" type="button" draggable="true" data-health-drag="' +
    esc(group.id) + '" aria-label="Reorder ' + esc(group.name) + ". Position " + (index + 1) + " of " + total +
    '." aria-describedby="health-order-help" title="Drag to reorder. Arrow keys also move this group."></button>' +
    '<span class="health-group-icon">' + ICONS.layers + '</span><div class="health-group-title">' + title +
    "<small>" + group.items.length + " delivery sources</small></div>" + match + "</header>" +
    '<div class="health-group-cards">' +
    group.items.map((item) => healthCard(item, index, total, {
      groupId: group.id,
      groupName: group.name,
      grouped: true,
      showHandle: false,
    })).join("") + "</div></section>";
}

function healthView() {
  const health = state.health || {};
  const items = Array.isArray(health.items) ? health.items : [];
  const groups = healthRepositoryGroups(items);
  const counts = health.counts || {};
  const loadingNote = state.loading ? " \u00b7 checking sources\u2026" : "";
  const errors = state.errors && state.errors.length
    ? '<div class="errbar">' + esc(state.errors.join(" \u00b7 ")) + "</div>"
    : "";
  const body = items.length
    ? '<div class="health-grid' + (healthOrderSaving ? " ordering" : "") + '" id="health-grid">' +
      groups.map((group, index) => healthGroup(group, index, groups.length)).join("") + "</div>"
    : '<div class="state"><div class="ico">' + ICONS.pulse + "</div><h2>" +
      (state.loading ? "Checking repository health\u2026" : "No health sources configured") + "</h2><p>" +
      (state.loading ? "Results appear as each source completes." : "Watch a GitHub repository or add an Azure DevOps pipeline in Settings.") +
      "</p></div>";

  return '<div class="subbar"><span class="who">' + ICONS.pulse + " Repository &amp; delivery health</span>" +
    '<span class="meta" id="health-order-help">' + groups.length + " repository group" + (groups.length === 1 ? "" : "s") +
    " across " + items.length + " source" + (items.length === 1 ? "" : "s") + ". Drag groups to prioritize." + loadingNote +
    '</span><div class="stats"><span class="stat"><span class="dot bg-success"></span><b>' + (counts.healthy || 0) +
    '</b> Healthy</span><span class="stat"><span class="dot bg-warning"></span><b>' +
    ((counts.running || 0) + (counts.degraded || 0)) +
    '</b> Active / degraded</span><span class="stat"><span class="dot bg-danger"></span><b>' + (counts.failing || 0) +
    '</b> Failing</span><span class="stat"><span class="dot bg-muted"></span><b>' +
    ((counts.unavailable || 0) + (counts.unknown || 0)) +
    '</b> Unknown</span></div></div>' + errors + '<div class="health-shell">' + body +
    '<p class="health-order-status" id="health-order-announcement" aria-live="polite">' +
    esc(healthOrderAnnouncement) + "</p></div>";
}

function queueView() {
  if (state.mode === "health") return healthView();
  const active = state.activeAccounts || [];
  const anyEnt = active.some((a) => a.enterprise);
  const whoLabel = active.length > 1
    ? esc(state.viewer) + ' <span class="who-more">+' + (active.length - 1) + " more</span>"
    : esc(state.viewer);
  const who = active.length
    ? avatarStack(active, 44) + '<span class="who-name">' + whoLabel + "</span>" +
        (anyEnt ? '<span class="ent-badge" title="GitHub Enterprise account active">' + ICONS.building + "Enterprise</span>" : "")
    : '<span class="who-name">' + esc(state.viewer) + "</span>";
  const isReviewBoard = state.mode === "review" && state.attention;
  const lanesHtml = isReviewBoard
    ? reviewBoardHtml()
    : (state.lanes.length
        ? state.lanes.map(laneHtml).join("")
        : '<div class="state"><div class="ico">' + ICONS.check + "</div><h2>All clear</h2><p>No items in " + esc(state.mode) + " mode for your watched repositories.</p></div>");
  const draftsHidden = !state.showDrafts && state.counts && state.counts.drafts
    ? ' <span class="meta-draft">\u00b7 ' + state.counts.drafts + " draft" + (state.counts.drafts === 1 ? "" : "s") + " hidden</span>"
    : "";
  return '<div class="subbar">' +
      '<span class="who">' + who + "</span>" +
      '<span class="meta">' + state.counts.prs + " open PRs across " + state.repos.length + " repos, updated " + timeAgo(state.fetchedAt) + draftsHidden + "</span>" +
      statsHtml() +
    "</div>" +
    (state.errors && state.errors.length ? '<div class="errbar">' + esc(state.errors.join(" \u00b7 ")) + "</div>" : "") +
    '<div class="lanes">' + lanesHtml + "</div>";
}

function statsHtml() {
  const c = state.counts;
  // In review mode the header mirrors the attention board so the numbers agree.
  if (state.mode === "review" && state.attention) {
    const att = state.attention;
    const bucket = (label) => {
      const b = (att.buckets || []).find((x) => x.label === label);
      return b ? b.items.length : 0;
    };
    return '<div class="stats">' +
      '<span class="stat"><span class="dot bg-danger"></span><b>' + att.focusTotal + "</b> needs attention</span>" +
      '<span class="stat"><span class="dot bg-success"></span><b>' + bucket("Ready to merge") + "</b> ready</span>" +
      '<span class="stat"><span class="dot bg-warning"></span><b>' + bucket("CI failing") + "</b> CI failing</span>" +
      '<span class="stat"><span class="dot bg-accent"></span><b>' + (att.community ? att.community.length : 0) + "</b> community</span>" +
    "</div>";
  }
  return '<div class="stats">' +
    '<span class="stat"><span class="dot bg-danger"></span><b>' + c.needsReview + "</b> needs review</span>" +
    '<span class="stat"><span class="dot bg-success"></span><b>' + c.readyToMerge + "</b> ready</span>" +
    '<span class="stat"><span class="dot bg-warning"></span><b>' + c.ciFailing + "</b> CI failing</span>" +
  "</div>";
}

function toggle(id, title, desc, checked) {
  return '<div class="toggle-row"><span><span class="tl">' + esc(title) + "</span>" +
    (desc ? '<span class="td">' + esc(desc) + "</span>" : "") + "</span>" +
    '<label class="switch"><input type="checkbox" id="' + id + '" ' + (checked ? "checked" : "") + ' /><span class="slider"></span></label></div>';
}

function filtersView() {
  const mode = state.mode;
  const modeName = { review: "Review", issues: "Issues", ship: "Ship", health: "Health" }[mode] || "Review";
  const cur = (m) => (mode === m ? " current" : "");
  const drafts = state.counts && state.counts.drafts;
  const draftLine = state.showDrafts
    ? "Draft PRs are <b>shown</b> right now."
    : "Draft PRs are <b>hidden</b> right now" + (drafts ? " (" + drafts + " hidden)" : "") + ".";
  return '<div class="page">' +
    '<div class="page-head"><h2>What\u2019s filtered</h2><p>This queue is curated, not a raw list. Here is exactly what each mode surfaces, holds back, or routes elsewhere, so a missing PR or issue is never a mystery. You are viewing <b>' + esc(modeName) + '</b> mode.</p></div>' +

    '<div class="section' + cur("review") + '"><h3>' + ICONS.eye + " Review</h3>" +
      '<div class="policy">' +
        '<div class="policy-row">' + ICONS.merge + "<span>A PR reaches the <b>shared review queue</b> only once <b>checks are green</b> and <b>all feedback is resolved</b>. Unfinished work stays in the author\u2019s <b>Your PRs</b> lane.</span></div>" +
        '<div class="policy-row">' + ICONS.pr + "<span><b>Drafts, merge conflicts, and needs-author-action</b> PRs are routed out of the shared lists, so reviewers only see PRs that are genuinely ready.</span></div>" +
        '<div class="policy-row">' + ICONS.xcircle + "<span><b>CI-failing</b> PRs are held out of Needs attention. A failure driven only by informational <b>aspire-1p checks</b> (proof of presence) is not counted as red.</span></div>" +
        '<div class="policy-row">' + ICONS.usersSm + "<span>PRs you authored <b>as yourself or via Copilot</b> both count as yours, so delegated work still lands in your lanes and developer totals.</span></div>" +
      "</div>" +
    "</div>" +

    '<div class="section' + cur("issues") + '"><h3>' + ICONS.tag + " Issues</h3>" +
      '<div class="policy">' +
        '<div class="policy-row">' + ICONS.alertSm + "<span><b>Focus buckets</b> surface the issues that matter first: regressions, CTI team items ([aspiree2e]), afscrome finds, and your own issues.</span></div>" +
        '<div class="policy-row">' + ICONS.dot2 + "<span>Everything else lands in <b>Needs triage</b> (unlabeled and unassigned) or <b>Recently active</b>, so no open issue silently drops off.</span></div>" +
      "</div>" +
    "</div>" +

    '<div class="section' + cur("ship") + '"><h3>' + ICONS.merge + " Ship</h3>" +
      '<div class="policy">' +
        '<div class="policy-row">' + ICONS.clock + "<span>Groups open work for the active milestone (<b>" + esc(prefs.release || state.release || "\u2014") + "</b>) so the release view stays focused on what is landing now.</span></div>" +
      "</div>" +
    "</div>" +

    '<div class="section' + cur("health") + '"><h3>' + ICONS.pulse + " Health</h3>" +
      '<div class="policy">' +
        '<div class="policy-row">' + ICONS.pulse + "<span>Checks the <b>default branch</b> of each watched GitHub repository and every Azure DevOps pipeline explicitly configured in Settings.</span></div>" +
        '<div class="policy-row">' + ICONS.alertSm + "<span>Likely causes appear only when provider evidence supports them. Repository inactivity by itself does <b>not</b> make a source unhealthy.</span></div>" +
      "</div>" +
    "</div>" +

    '<div class="section"><h3>Drafts</h3>' +
      '<p class="hint">' + draftLine + " Drafts are prototypes and experiments, not review work. Change this in Settings.</p>" +
      '<div class="row-actions"><button class="btn ghost" id="filters-to-settings">Open settings</button></div>' +
    "</div>" +
  "</div>";
}

function pipelineEditorHtml() {
  const pipelines = Array.isArray(prefs.azurePipelines) ? prefs.azurePipelines : [];
  const rows = pipelines.length
    ? pipelines.map((pipeline) => {
        const name = pipeline.name || (pipeline.definitionId ? "Pipeline " + pipeline.definitionId : "Azure DevOps pipeline");
        const branch = String(pipeline.branch || "refs/heads/main").replace(/^refs\/heads\//, "");
        return '<li class="pipeline-row"><div class="pipeline-main"><span class="pipeline-name">' + esc(name) +
          '</span><span class="pipeline-meta">' + esc(branch + " \u00b7 " + pipeline.url) +
          '</span></div><button class="repo-ico danger pipeline-remove" type="button" data-pipeline-id="' +
          esc(pipeline.id) + '" title="Remove pipeline" aria-label="Remove ' + esc(name) + '">' + ICONS.trash + "</button></li>";
      }).join("")
    : '<li class="repo-empty">No additional Azure DevOps pipelines configured.</li>';
  return '<div class="section" id="pipeline-settings"><h3>' + ICONS.pulse + " Azure DevOps pipelines</h3>" +
    '<p class="hint">A matching Azure Repo delivery pipeline is auto-discovered from your <b>az</b> CLI default project. Paste a pipeline or build URL to monitor additional definitions. Existing <b>az</b> or <b>AZURE_DEVOPS_EXT_PAT</b> authentication is reused; credentials are never stored.</p>' +
    '<div class="pipeline-add"><input id="pipeline-url-input" type="text" value="' + esc(pipelineUrlDraft) +
    '" placeholder="https://dev.azure.com/org/project/_build?definitionId=123" aria-label="Azure DevOps pipeline URL" />' +
    '<input id="pipeline-branch-input" class="pipeline-branch" type="text" value="' + esc(pipelineBranchDraft) +
    '" placeholder="Branch (default: main)" aria-label="Pipeline branch override" />' +
    '<button class="repo-add-btn" id="pipeline-add-btn" type="button" title="Add pipeline" aria-label="Add pipeline"' +
    (pipelineSaving ? ' disabled aria-busy="true"' : "") + ">" + (pipelineSaving ? ICONS.refresh : ICONS.plus) +
    '</button></div><div class="pipeline-err" role="alert">' + esc(pipelineError) +
    '</div><ul class="pipeline-list">' + rows + "</ul></div>";
}

function settingsView() {
  const n = prefs.notifications;
  const limit = (state.reviewLimit || 10);
  return '<div class="page">' +
    '<div class="page-head"><h2>Settings</h2><p>Tune the shared review queue, delivery health, the ship milestone, and when the canvas speaks up. Watched repositories are configured per account in the Accounts tab.</p></div>' +
    '<div class="section"><h3>Review queue</h3>' +
      '<p class="hint">The shared queue is team-managed, not individually sorted. It shows at most <b>' + limit + '</b> PRs, ranked so the oldest waits surface first.</p>' +
      '<div class="policy">' +
        '<div class="policy-row">' + ICONS.eye + '<span>A PR only enters the shared queue once <b>checks are green</b> and <b>all review feedback is resolved</b>. Unfinished work stays in the author\u2019s <b>Your PRs</b> lane.</span></div>' +
        '<div class="policy-row">' + ICONS.pr + '<span>Draft PRs are hidden by default. They are prototypes and experiments, not review work.</span></div>' +
      "</div>" +
      toggle("s-drafts", "Show draft PRs", "Include drafts in lanes and counts", !!prefs.showDrafts) +
    "</div>" +
    '<div class="section"><h3>Ship milestone</h3>' +
      '<p class="hint">Used by Ship mode to group work for the active release.</p>' +
      '<div class="field"><input type="text" id="release-input" value="' + esc(prefs.release || "") + '" placeholder="13.5" /></div></div>' +
    pipelineEditorHtml() +
    (standalone ? standaloneSettingsView() : "") +
    '<div class="section" id="notif-settings"><h3>Notifications</h3>' +
      '<p class="hint">Live in-session alerts surface in the bell. Choose what counts.</p>' +
      toggle("n-review", "Review requested", "Someone asked you to review a PR", n.reviewRequested) +
      toggle("n-ready", "Your PR is ready to merge", "Approved with passing checks", n.readyToMerge) +
      toggle("n-changes", "Changes requested on your PR", "A reviewer wants edits", n.changesRequested) +
      toggle("n-ci", "CI failing on your PR", "A required check is red", n.ciFailing) +
    "</div>" +
    '<div class="row-actions">' +
      '<button class="btn ghost" id="cancel-settings">Cancel <kbd>Esc</kbd></button>' +
      '<button class="btn" id="save-settings">Save changes <kbd>\u21B5</kbd></button></div>' +
  "</div>";
}

function standaloneSettingsView() {
  const config = prefs.sessionLauncher || { projects: [], selectedRepositoryUrl: "" };
  const projects = config.projects || [];
  const suggestions = [...new Set(["microsoft/aspire", "devdiv-microsoft/aspire-1p", ...(state.repos || [])])];
  return '<div class="section"><h3>GitHub App projects</h3>' +
    '<p class="hint">PR actions automatically use the project with the same GitHub repository. ' +
    'Choose a fallback project for health sources without a mapped repository. GitHub App confirms each new session. ' +
    'These are local routing preferences, not a list read from the app.</p>' +
    '<label for="session-project">Fallback project</label><div class="field"><select id="session-project">' +
    '<option value="">Choose a project</option>' +
    projects.map(p => '<option value="' + esc(p.repositoryUrl) + '"' +
      (p.repositoryUrl === config.selectedRepositoryUrl ? " selected" : "") + '>' + esc(p.name) + " - " + esc(p.repositoryUrl) + "</option>").join("") +
    '</select></div><div class="field"><label for="session-project-name">Project name</label>' +
    '<input id="session-project-name" placeholder="Aspire" /></div>' +
    '<div class="field"><label for="session-project-url">GitHub repository URL or owner/repo</label>' +
    '<input id="session-project-url" list="session-project-suggestions" placeholder="https://github.com/microsoft/aspire" />' +
    '<datalist id="session-project-suggestions">' +
    suggestions.map(repo => '<option value="' + esc(repo) + '"></option>').join("") +
    '</datalist></div><div class="row-actions"><button type="button" class="btn ghost" id="add-session-project">Add project</button>' +
    '<button type="button" class="btn" id="save-session-project">Save selection</button>' +
    '<button type="button" class="btn ghost" id="remove-session-project">Remove selected project</button></div>' +
    '<div class="pipeline-err" id="session-settings-error" role="alert">' + esc(sessionSettingsError) + '</div></div>' +
    '<div class="section"><h3>Prerequisites</h3><p class="hint">Check tools and authentication without installing anything or starting sessions.</p>' +
    '<button type="button" class="btn ghost" id="doctor-btn"' + (doctorRunning ? " disabled" : "") + '>' +
    (doctorRunning ? "Checking..." : "Run doctor") + '</button>' +
    (doctorResult ? '<pre role="status" style="white-space:pre-wrap;overflow-wrap:anywhere">' + esc(doctorResult) + "</pre>" : "") +
    "</div>";
}

async function saveSessionProject(action) {
  if (sessionSettingsSaving) return;
  sessionSettingsSaving = true;
  const settingsDraft = captureSettingsDraft();
  let saved = false;
  for (const id of ["add-session-project", "save-session-project", "remove-session-project"]) {
    const button = document.getElementById(id);
    if (button) button.disabled = true;
  }
  try {
    const config = prefs.sessionLauncher || { projects: [], selectedRepositoryUrl: "" };
    let projects = (config.projects || []).map(p => ({ ...p }));
    let selected = document.getElementById("session-project").value;
    if (action === "add") {
      const name = document.getElementById("session-project-name").value.trim();
      const repositoryUrl = document.getElementById("session-project-url").value.trim();
      if (!name || !repositoryUrl) throw new Error("Enter a project name and repository.");
      projects.push({ name, repositoryUrl });
    } else if (action === "remove") {
      if (!selected) throw new Error("Choose the project to remove.");
      projects = projects.filter(project => project.repositoryUrl !== selected);
      selected = "";
    }
    await postJSON("api/session/configuration", { projects, selectedRepositoryUrl: selected });
    const data = await readJson(await apiFetch("api/state"));
    prefs = data.prefs;
    sessionSettingsError = "";
    saved = true;
  } catch (error) {
    sessionSettingsError = error.message || String(error);
  }
  sessionSettingsSaving = false;
  const latestDraft = captureSettingsDraft() || settingsDraft;
  if (saved && latestDraft) latestDraft.sessionFields = [];
  render();
  restoreSettingsDraft(latestDraft);
}

async function runDoctor() {
  const draft = captureSettingsDraft();
  doctorRunning = true;
  doctorResult = null;
  render();
  restoreSettingsDraft(draft);
  try {
    const report = await readJson(await apiFetch("api/doctor"));
    doctorResult = (report.checks || []).map(check =>
      check.status.toUpperCase() + "  " + check.label + (check.required ? " (required)" : "") +
      "\n" + check.message + (check.remediation ? "\n" + check.remediation : "")).join("\n\n");
  } catch (error) {
    doctorResult = error.message || String(error);
  } finally {
    doctorRunning = false;
    const latestDraft = captureSettingsDraft();
    render();
    restoreSettingsDraft(latestDraft);
  }
}

function srcRow(s) {
  const t = (ACCT_STATUS[s.status] || ACCT_STATUS.failed);
  const meta = [];
  if (s.status !== "failed") meta.push("<span>" + s.accessible + "/" + s.total + " repos</span>");
  if (s.scopes && s.scopes.includes("read:org")) meta.push('<span class="scopes">read:org</span>');
  if (s.reason) meta.push("<span>" + esc(s.reason) + "</span>");
  return '<div class="src-row">' +
    '<span class="dot bg-' + t.tone + '"></span>' +
    '<span class="sname">' + esc(srcLabel(s.source)) +
      (s.enterprise ? '<span class="schip ent">' + esc(s.host || "Enterprise") + "</span>" : "") +
      (s.chosen ? '<span class="schip">IN USE</span>' : "") + "</span>" +
    '<span class="smeta"><span class="t-' + t.tone + '">' + esc(t.label) + "</span>" + meta.join("") + "</span>" +
  "</div>";
}

function repoEditorHtml(a) {
  const id = a.id;
  const count = (draftReposByAcct[id] || a.repos || []).length;
  return '<div class="acct-repos">' +
    '<div class="acct-repos-head"><span>Watched repositories</span><span class="rcount" data-rcount="' + esc(id) + '">' + count + "</span></div>" +
    '<p class="acct-repos-hint">Add a repo as <code>owner/repo</code>, then press Enter or the plus. Changes save to this account immediately.</p>' +
    '<div class="repo-add">' +
      '<input class="repo-add-input" data-addinput="' + esc(id) + '" type="text" spellcheck="false" autocomplete="off" autocapitalize="off" placeholder="owner/repo" />' +
      '<button class="repo-add-btn" data-add="' + esc(id) + '" title="Add repository" aria-label="Add repository">' + ICONS.plus + "</button>" +
    "</div>" +
    '<div class="repo-err" data-err="' + esc(id) + '"></div>' +
    '<ul class="repo-list" data-list="' + esc(id) + '">' + repoRowsHtml(id) + "</ul>" +
  "</div>";
}

function accountCard(a, asPicker) {
  const tone = acctTone(a);
  const st = (ACCT_STATUS[a.status] || ACCT_STATUS.failed);
  const usable = a.status !== "failed";
  // Seed this account's repo draft from the server copy unless mid-edit.
  if (editingByAcct[a.id] == null || editingByAcct[a.id] < 0) draftReposByAcct[a.id] = (a.repos || []).slice();
  if (editingByAcct[a.id] == null) editingByAcct[a.id] = -1;
  const open = expanded.has(a.id) || asPicker;
  const kinds = a.sourceKinds || [...new Set((a.sources || []).map((s) => s.source))];
  const badgeHtml = kinds.map((k) => '<span class="src-badge">' + esc(srcLabel(k)) + "</span>").join("");
  const multi = (a.sources || []).length > 1;
  const entBadge = a.enterprise
    ? '<span class="ent-badge" title="' + esc(a.host || "GitHub Enterprise") + '">' + ICONS.building + "Enterprise</span>"
    : "";
  const meta = [];
  if (usable) meta.push("<span>" + a.accessible + "/" + a.total + " repos</span>");
  if (a.hasReadOrg) meta.push('<span class="scopes">read:org</span>');
  if (a.reason) meta.push("<span>" + esc(a.reason) + "</span>");
  const detail = (a.sources || []).map(srcRow).join("");
  const sw = '<label class="switch" title="' + (a.active ? "Active \u00b7 click to disable" : "Enable this account") + '">' +
    '<input type="checkbox" data-active="' + esc(a.id) + '"' + (a.active ? " checked" : "") + (usable ? "" : " disabled") +
    ' aria-label="Toggle ' + esc(a.login) + '" /><span class="slider"></span></label>';
  return '<div class="acct-card ' + (a.active ? "active " : "") + (open ? "open" : "") + '" data-card="' + esc(a.id) + '">' +
    '<div class="acct-head">' +
      '<button class="acct-main" data-expand="' + esc(a.id) + '">' +
        acctAvatar(a, 72) +
        '<span class="acct-id">' +
          '<span class="acct-name">' + esc(a.login) + entBadge + badgeHtml +
            (multi ? '<span class="count">found in ' + (a.sources || []).length + " places</span>" : "") + "</span>" +
          '<span class="acct-meta"><span class="acct-status"><span class="dot bg-' + tone + '"></span><span class="t-' + tone + '">' + esc(st.label) + "</span></span>" + meta.join("") + "</span>" +
        "</span>" +
      "</button>" +
      '<div class="acct-right">' + sw +
        '<button class="caret" data-expand="' + esc(a.id) + '" title="Configure" aria-label="Configure account">' + ICONS.chev + "</button>" +
      "</div>" +
    "</div>" +
    '<div class="acct-detail"><div class="inner">' +
      repoEditorHtml(a) +
      (detail ? '<div class="src-list">' + detail + "</div>" : "") +
    "</div></div>" +
  "</div>";
}

function accountsView() {
  const accts = (state && state.accounts) || [];
  const activeCount = accts.filter((a) => a.active).length;
  const rows = accts.length
    ? '<div class="acct-list">' + accts.map((a) => accountCard(a, false)).join("") + "</div>"
    : '<p class="acct-intro">No GitHub credentials detected. Run <code>gh auth login</code> and rescan.</p>';
  return '<div class="page">' +
    '<div class="page-head" style="display:flex;align-items:flex-end;gap:10px">' +
      '<div><h2>GitHub accounts</h2><p>Enable any number of accounts. Their results interleave across all tabs, and each account watches its own repositories.</p></div>' +
      '<div class="page-actions"><button class="rescan-btn ' + (rescanning ? "spin" : "") + '" id="rescan-btn">' + ICONS.refresh + "Rescan</button></div>" +
    "</div>" +
    (accts.length ? '<p class="acct-intro">' + activeCount + " of " + accts.length + " account" + (accts.length === 1 ? "" : "s") + " active.</p>" : "") +
    rows +
  "</div>";
}

function notificationsView() {
  const items = state.notifications || [];
  const dismissed = state.dismissedCount || 0;
  let body;
  if (!items.length) {
    body = '<div class="state"><div class="ico">' + ICONS.bellBig + "</div><h2>You're all caught up</h2>" +
      "<p>Nothing needs your attention right now. Adjust what counts as a notification in Settings.</p>" +
      '<div class="state-cta"><button class="btn ghost" id="to-settings">Open settings</button>' +
      (dismissed ? '<button class="btn ghost" id="restore-notifs">Restore ' + dismissed + " dismissed</button>" : "") + "</div></div>";
  } else {
    body = '<div class="notif-list">' + items.map((n) =>
      '<div class="notif-card" data-id="' + esc(n.id) + '">' +
        '<span class="ndot bg-' + (n.tone || "muted") + '"></span>' +
        '<a class="nbody" href="' + esc(n.url) + '" target="_blank" rel="noreferrer">' +
          '<span class="ntitle">' + esc(n.title) + "</span>" +
          '<span class="ndetail">' + esc(n.detail) + ' \u00b7 <span class="repo">' + esc(shortRepo(n.repository)) + " #" + n.number + "</span></span>" +
        "</a>" +
        '<button class="dismiss" data-dismiss="' + esc(n.id) + '" title="Dismiss" aria-label="Dismiss">' + ICONS.x + "</button>" +
      "</div>"
    ).join("") + "</div>";
  }
  return '<div class="page">' +
    '<div class="page-head" style="display:flex;align-items:flex-end;gap:10px">' +
      '<div><h2>Notifications</h2><p>' + (items.length ? items.length + " active" : "Up to date") +
        (dismissed ? ", " + dismissed + " dismissed" : "") + ".</p></div>" +
      '<div class="page-actions">' +
        (items.length ? '<button class="rescan-btn" id="dismiss-all">Clear all</button>' : "") +
        (dismissed && items.length ? '<button class="rescan-btn" id="restore-notifs2">Restore</button>' : "") +
      "</div>" +
    "</div>" + body +
    '<div class="notif-foot"><button class="linklike" id="notif-config" type="button">' + ICONS.gear + ' Configure which notifications count</button></div>' +
  "</div>";
}

function authPicker() {
  const accts = state.accounts || [];
  const picker = accts.length
    ? '<div class="acct-list" style="text-align:left;margin-top:18px">' + accts.map((a) => accountCard(a, true)).join("") + "</div>"
    : '<span class="cmd">gh auth login</span>';
  return '<div class="page" style="max-width:560px">' +
    '<div class="state" style="padding-top:32px"><div class="ico">' + ICONS.users + "</div>" +
    "<h2>Enable a GitHub account</h2><p>" + esc(state.message) + "</p></div>" +
    picker +
  "</div>";
}

/* ---- render ---- */

function render(forward) {
  app.removeAttribute("aria-busy");
  // Drop any split-button menu we portaled to <body> before rebuilding the subtree, so an
  // open menu never survives a re-render as a detached orphan carrying stale click handlers.
  document.querySelectorAll("body > .cb-menu").forEach((m) => m.remove());
  if (loadError && !state) {
    app.innerHTML = topbarShell() +
      '<div class="state"><div class="ico">' + ICONS.alert + '</div><h2>Could not load</h2><p>' + esc(loadError) +
      '</p><div class="state-cta"><button class="btn" id="retry-btn">Try again</button></div></div>';
    const rt = document.getElementById("retry-btn"); if (rt) rt.addEventListener("click", load);
    return;
  }
  if (!state) return; // skeleton (initial HTML) stays until first load resolves

  let inner;
  if (!state.authenticated && view === "queue" && state.mode !== "health") inner = authPicker();
  else if (view === "settings") inner = settingsView();
  else if (view === "filters") inner = filtersView();
  else if (view === "accounts") inner = accountsView();
  else if (view === "notifications") inner = notificationsView();
  else inner = queueView();

  const dir = forward === false || (forward === undefined && (RANK[view] || 0) < prevRank) ? "back" : "";
  // A refresh/rescan that fails after the dashboard already loaded sets loadError
  // but keeps the last-good state. Surface it as a dismissible banner instead of
  // discarding the loaded UI (the full-screen "Could not load" state above only
  // applies to the very first load, when there is no state to preserve).
  const banner = loadError
    ? '<div class="errbar loaderr" role="alert">' + esc(loadError) +
      '<button class="errbar-x" id="load-errbar-dismiss" type="button" title="Dismiss" aria-label="Dismiss">' + ICONS.x + "</button></div>"
    : "";
  const motionClass = healthOrderSaving ? " no-motion" : "";
  app.innerHTML = topbarHtml() + banner + '<div class="viewport"><div class="view ' + dir + motionClass + '">' + inner + "</div></div>";
  if (banner) {
    const bx = document.getElementById("load-errbar-dismiss");
    if (bx) bx.addEventListener("click", function () { loadError = null; render(); });
  }
  wire();
  updateRefreshControls();
  layoutGrids();
}

/* ---- masonry: fill the left column first, top-to-bottom ----
   CSS multi-column balances column heights, which puts a single tall card on
   the left and the rest on the right. For a prioritized queue we want the
   opposite: read straight down the first column, then the next. We rebuild each
   .grid into real flex columns and distribute cards in queue order, only
   reflowing when the responsive column count actually changes. */
var __gridRO = null;

function ensureGridObserver() {
  if (__gridRO || typeof ResizeObserver === "undefined") return;
  __gridRO = new ResizeObserver(function (entries) {
    for (var i = 0; i < entries.length; i++) layoutGrid(entries[i].target);
  });
}

function layoutGrids() {
  ensureGridObserver();
  if (__gridRO) __gridRO.disconnect();
  var grids = document.querySelectorAll(".grid");
  for (var i = 0; i < grids.length; i++) {
    layoutGrid(grids[i]);
    if (__gridRO) __gridRO.observe(grids[i]);
  }
}

function layoutGrid(grid) {
  var cards = grid.__cards;
  if (!cards) {
    cards = [];
    var kids = grid.children;
    for (var i = 0; i < kids.length; i++) {
      if (kids[i].classList && kids[i].classList.contains("card")) cards.push(kids[i]);
    }
    if (!cards.length) return; // skeletons or non-card grids: leave alone
    grid.__cards = cards;
  }
  var w = grid.clientWidth;
  if (!w) return; // not visible yet; the observer fires again when it is
  var COLW = 280, GAP = 12;
  var cols = Math.max(1, Math.floor((w + GAP) / (COLW + GAP)));
  if (cols > cards.length) cols = cards.length || 1;
  if (grid.__cols === cols) return; // responsive column count unchanged
  grid.__cols = cols;

  while (grid.firstChild) grid.removeChild(grid.firstChild);
  if (cols <= 1) {
    grid.classList.remove("mcol");
    for (var j = 0; j < cards.length; j++) grid.appendChild(cards[j]);
    return;
  }
  grid.classList.add("mcol");
  var n = cards.length, base = Math.floor(n / cols), extra = n % cols, idx = 0;
  for (var c = 0; c < cols; c++) {
    var col = document.createElement("div");
    col.className = "mcol-col";
    var cnt = base + (c < extra ? 1 : 0);
    for (var k = 0; k < cnt; k++) col.appendChild(cards[idx++]);
    grid.appendChild(col);
  }
}

function topbarShell() {
  return '<div class="topbar"><span class="brand"><span class="mark">' + LOGO + '</span><span class="brand-text">Aspire Team App</span></span><span class="spacer"></span></div>';
}

/* ---- watched-repository editor ---- */

var REPO_RE = /^[A-Za-z0-9][A-Za-z0-9_.-]*\/[A-Za-z0-9][A-Za-z0-9_.-]*$/;

function normRepo(v) {
  return String(v || "").trim()
    .replace(/^https?:\/\/github\.com\//i, "")
    .replace(/\.git$/i, "")
    .replace(/\/+$/, "");
}

function repoErr(id, msg) {
  var el = document.querySelector('.repo-err[data-err="' + cssEsc(id) + '"]');
  if (!el) return;
  if (!msg) { el.textContent = ""; el.classList.remove("show"); return; }
  el.textContent = msg; el.classList.add("show");
}

function shake(el) {
  if (!el) return;
  el.classList.remove("shake");
  void el.offsetWidth;
  el.classList.add("shake");
}

function repoRowsHtml(id) {
  var repos = draftReposByAcct[id] || [];
  var editing = editingByAcct[id];
  if (!repos.length) {
    return '<li class="repo-empty">No repositories yet. Add one above to start watching it.</li>';
  }
  return repos.map(function (r, i) {
    if (i === editing) {
      return '<li class="repo-row editing" data-acct="' + esc(id) + '" data-i="' + i + '">' +
        '<input class="repo-edit-input" data-editinput="' + esc(id) + '" type="text" spellcheck="false" autocomplete="off" value="' + esc(r) + '" />' +
        '<span class="repo-acts">' +
          '<button class="repo-ico ok" data-save-edit="' + i + '" data-acct="' + esc(id) + '" title="Save" aria-label="Save">' + ICONS.check + "</button>" +
          '<button class="repo-ico" data-cancel-edit="' + i + '" data-acct="' + esc(id) + '" title="Cancel" aria-label="Cancel">' + ICONS.x + "</button>" +
        "</span></li>";
    }
    return '<li class="repo-row" data-acct="' + esc(id) + '" data-i="' + i + '">' +
      '<span class="repo-name">' + esc(r) + "</span>" +
      '<span class="repo-acts">' +
        '<button class="repo-ico" data-edit="' + i + '" data-acct="' + esc(id) + '" title="Edit" aria-label="Edit">' + ICONS.pencil + "</button>" +
        '<button class="repo-ico danger" data-del="' + i + '" data-acct="' + esc(id) + '" title="Remove" aria-label="Remove">' + ICONS.trash + "</button>" +
      "</span></li>";
  }).join("");
}

function updateRepoCount(id) {
  var c = document.querySelector('.rcount[data-rcount="' + cssEsc(id) + '"]');
  if (c) c.textContent = (draftReposByAcct[id] || []).length;
}

function accountRepos(id) {
  const accts = (state && state.accounts) || [];
  const a = accts.find(function (acct) { return acct.id === id; });
  return (a && a.repos) || [];
}

function renderRepoList(id, flagLast) {
  var ul = document.querySelector('.repo-list[data-list="' + cssEsc(id) + '"]');
  if (!ul) return;
  ul.innerHTML = repoRowsHtml(id);
  updateRepoCount(id);
  if (flagLast) {
    var rows = ul.querySelectorAll(".repo-row");
    var last = rows[rows.length - 1];
    if (last) { last.classList.add("added"); last.addEventListener("animationend", function () { last.classList.remove("added"); }, { once: true }); }
  }
  wireRepoRows(id);
}

function addRepoFromInput(id) {
  var inp = document.querySelector('.repo-add-input[data-addinput="' + cssEsc(id) + '"]');
  if (!inp) return;
  var v = normRepo(inp.value);
  if (!v) { return; }
  if (!REPO_RE.test(v)) { repoErr(id, "Use the owner/repo format, like microsoft/aspire."); shake(inp); return; }
  var list = draftReposByAcct[id] || (draftReposByAcct[id] = []);
  if (list.some(function (r) { return r.toLowerCase() === v.toLowerCase(); })) {
    repoErr(id, v + " is already in the list."); shake(inp); return;
  }
  var before = list.slice();
  list.push(v);
  inp.value = "";
  repoErr(id, "");
  renderRepoList(id, true);
  persistAccountRepos(id, before);
  inp.focus();
}

function commitEdit(id, i) {
  var inp = document.querySelector('.repo-edit-input[data-editinput="' + cssEsc(id) + '"]');
  if (!inp) return;
  var v = normRepo(inp.value);
  var list = draftReposByAcct[id] || [];
  if (!REPO_RE.test(v)) { repoErr(id, "Use the owner/repo format, like microsoft/aspire."); shake(inp); return; }
  if (list.some(function (r, j) { return j !== i && r.toLowerCase() === v.toLowerCase(); })) {
    repoErr(id, v + " is already in the list."); shake(inp); return;
  }
  var before = list.slice();
  list[i] = v; editingByAcct[id] = -1; repoErr(id, "");
  renderRepoList(id);
  persistAccountRepos(id, before);
}

function deleteRepo(id, i, row) {
  var before = (draftReposByAcct[id] || []).slice();
  // The row-removal animation is our cue to actually splice, but a missed
  // animationend (reduced motion, a backgrounded tab, an interrupted animation)
  // would strand the row. We keep a fallback timer as a backstop, so both the
  // animationend handler and the timer can fire. A once-only guard makes the splice
  // run exactly once: without it the second call would splice a now-shifted index
  // and silently drop the wrong repository.
  var ran = false;
  var fallback = null;
  var done = function () {
    if (ran) return;
    ran = true;
    if (fallback) clearTimeout(fallback);
    (draftReposByAcct[id] || []).splice(i, 1);
    if (editingByAcct[id] === i) editingByAcct[id] = -1;
    renderRepoList(id);
    persistAccountRepos(id, before);
  };
  if (row) { row.classList.add("removing"); row.addEventListener("animationend", done, { once: true }); fallback = setTimeout(done, 240); }
  else done();
}

function wireRepoRows(id) {
  var ul = document.querySelector('.repo-list[data-list="' + cssEsc(id) + '"]');
  if (!ul) return;
  ul.querySelectorAll("[data-edit]").forEach(function (b) {
    b.addEventListener("click", function () {
      editingByAcct[id] = parseInt(b.dataset.edit, 10); repoErr(id, ""); renderRepoList(id);
      var ei = document.querySelector('.repo-edit-input[data-editinput="' + cssEsc(id) + '"]'); if (ei) { ei.focus(); ei.select(); }
    });
  });
  ul.querySelectorAll("[data-del]").forEach(function (b) {
    var fired = false;
    b.addEventListener("click", function () { if (fired) return; fired = true; deleteRepo(id, parseInt(b.dataset.del, 10), b.closest(".repo-row")); });
  });
  ul.querySelectorAll("[data-save-edit]").forEach(function (b) {
    b.addEventListener("click", function () { commitEdit(id, parseInt(b.dataset.saveEdit, 10)); });
  });
  ul.querySelectorAll("[data-cancel-edit]").forEach(function (b) {
    b.addEventListener("click", function () { editingByAcct[id] = -1; repoErr(id, ""); renderRepoList(id); });
  });
  var ei = ul.querySelector(".repo-edit-input");
  if (ei) ei.addEventListener("keydown", function (e) {
    if (e.key === "Enter") { e.preventDefault(); commitEdit(id, editingByAcct[id]); }
    else if (e.key === "Escape") { e.preventDefault(); editingByAcct[id] = -1; repoErr(id, ""); renderRepoList(id); }
  });
}

function wireRepoEditor(id) {
  var addBtn = document.querySelector('.repo-add-btn[data-add="' + cssEsc(id) + '"]');
  if (addBtn) addBtn.addEventListener("click", function () { addRepoFromInput(id); });
  var inp = document.querySelector('.repo-add-input[data-addinput="' + cssEsc(id) + '"]');
  if (inp) inp.addEventListener("keydown", function (e) {
    if (e.key === "Enter") { e.preventDefault(); addRepoFromInput(id); }
    else { repoErr(id, ""); }
  });
  wireRepoRows(id);
}

function isSettingsSaveShortcut(event) {
  if (event.key !== "Enter" || event.isComposing) return false;
  const target = event.target || {};
  const tagName = String(target.tagName || "").toUpperCase();
  if (tagName === "TEXTAREA" || tagName === "BUTTON" || tagName === "A" || tagName === "SELECT" || target.isContentEditable) return false;
  if (typeof target.closest === "function" && target.closest('[role="button"]')) return false;
  return target.id !== "pipeline-url-input" && target.id !== "pipeline-branch-input";
}

function wire() {
  const addSessionProject = document.getElementById("add-session-project");
  if (addSessionProject) addSessionProject.addEventListener("click", () => saveSessionProject("add"));
  const saveSessionSelection = document.getElementById("save-session-project");
  if (saveSessionSelection) saveSessionSelection.addEventListener("click", () => saveSessionProject("save"));
  const removeSessionProject = document.getElementById("remove-session-project");
  if (removeSessionProject) removeSessionProject.addEventListener("click", () => saveSessionProject("remove"));
  const doctor = document.getElementById("doctor-btn");
  if (doctor) doctor.addEventListener("click", runDoctor);
  document.querySelectorAll(".tab").forEach((b) => b.addEventListener("click", () => setMode(b.dataset.mode)));
  const rb = document.getElementById("refresh-btn"); if (rb) rb.addEventListener("click", refresh);
  const applyUpdate = document.getElementById("apply-update-btn"); if (applyUpdate) applyUpdate.addEventListener("click", applyAvailableUpdate);
  const autoApply = document.getElementById("auto-apply-btn"); if (autoApply) autoApply.addEventListener("click", toggleAutoApply);
  document.querySelectorAll(".linked-pr").forEach((link) =>
    link.addEventListener("click", (e) => {
      if (standalone) return;
      e.preventDefault();
      e.stopPropagation();
      openLinkedPr(link);
    }));
  const back = document.getElementById("back-btn"); if (back) back.addEventListener("click", () => goView("queue", false));
  const bell = document.getElementById("bell-btn"); if (bell) bell.addEventListener("click", () => goView(view === "notifications" ? "queue" : "notifications"));
  const gear = document.getElementById("gear-btn"); if (gear) gear.addEventListener("click", () => goView(view === "settings" ? "queue" : "settings"));
  const filt = document.getElementById("filters-btn"); if (filt) filt.addEventListener("click", () => goView(view === "filters" ? "queue" : "filters"));
  const acct = document.getElementById("acct-btn"); if (acct) acct.addEventListener("click", () => goView(view === "accounts" ? "queue" : "accounts"));

  const save = document.getElementById("save-settings"); if (save) save.addEventListener("click", saveSettings);
  const cancel = document.getElementById("cancel-settings"); if (cancel) cancel.addEventListener("click", () => goView("queue", false));
  const fToSettings = document.getElementById("filters-to-settings"); if (fToSettings) fToSettings.addEventListener("click", () => goView("settings"));
  const pipelineUrl = document.getElementById("pipeline-url-input");
  const pipelineBranch = document.getElementById("pipeline-branch-input");
  const pipelineAdd = document.getElementById("pipeline-add-btn");
  if (pipelineUrl) {
    pipelineUrl.addEventListener("input", (e) => { pipelineUrlDraft = e.target.value; pipelineError = ""; });
    pipelineUrl.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); e.stopPropagation(); addAzurePipeline(); }
    });
  }
  if (pipelineBranch) {
    pipelineBranch.addEventListener("input", (e) => { pipelineBranchDraft = e.target.value; pipelineError = ""; });
    pipelineBranch.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); e.stopPropagation(); addAzurePipeline(); }
    });
  }
  if (pipelineAdd) pipelineAdd.addEventListener("click", addAzurePipeline);
  document.querySelectorAll(".pipeline-remove").forEach((button) => {
    button.addEventListener("click", () => removeAzurePipeline(button.dataset.pipelineId));
  });

  // The Esc/Enter hints on the settings buttons are real shortcuts, like the app's
  // Cancel/Continue. Bound once so re-renders don't stack handlers. Esc also backs
  // out of the filters info page, which has no Enter action of its own.
  if (!keysBound) {
    keysBound = true;
    document.addEventListener("keydown", function (e) {
      if (view === "filters" && e.key === "Escape") { e.preventDefault(); goView("queue", false); return; }
      if (view !== "settings") return;
      if (e.key === "Escape") { e.preventDefault(); goView("queue", false); }
      else if (isSettingsSaveShortcut(e)) { e.preventDefault(); saveSettings(); }
    });
  }

  const rescan = document.getElementById("rescan-btn"); if (rescan) rescan.addEventListener("click", rescanAccounts);

  wireAccounts();
  wireHealthOrdering();

  document.querySelectorAll(".lane-head").forEach((b) =>
    b.addEventListener("click", () => {
      const id = b.dataset.laneToggle;
      const sec = b.closest(".lane");
      if (!sec) return;
      const nowCollapsed = !sec.classList.contains("collapsed");
      sec.classList.toggle("collapsed", nowCollapsed);
      b.setAttribute("aria-expanded", nowCollapsed ? "false" : "true");
      if (nowCollapsed) collapsedLanes.add(id); else collapsedLanes.delete(id);
    }));

  // Generic collapsible sections (primary queues + secondary reference groups).
  document.querySelectorAll("[data-collapse]").forEach((b) =>
    b.addEventListener("click", () => {
      const id = b.dataset.collapse;
      const sec = b.closest(".collapsible");
      if (!sec) return;
      const nowCollapsed = !sec.classList.contains("collapsed");
      sec.classList.toggle("collapsed", nowCollapsed);
      b.setAttribute("aria-expanded", nowCollapsed ? "false" : "true");
      if (nowCollapsed) collapsedLanes.add(id); else collapsedLanes.delete(id);
    }));

  document.querySelectorAll(".dismiss").forEach((b) =>
    b.addEventListener("click", (e) => { e.preventDefault(); e.stopPropagation(); dismissNotif(b.dataset.dismiss, b.closest(".notif-card")); }));

  // Card action split buttons live in a sibling row of the card link, so stop the click
  // from bubbling to any surrounding handler and never navigate. The main button runs the
  // action at its own data-target: "new-session" for a github.com PR, or "current-session"
  // for a GHES/EMU PR that can't open a sub-session (see cardActionBtn). The caret, present
  // only on github.com cards, toggles a menu to pick new vs current session.
  document.querySelectorAll(".cb-split").forEach((split) => {
    const main = split.querySelector(".cb-main");
    const caret = split.querySelector(".cb-caret");
    const menu = split.querySelector(".cb-menu");
    if (main) main.addEventListener("click", (e) => {
      e.preventDefault(); e.stopPropagation();
      closeCbMenus();
      onCardAction(split, main.dataset.target || "new-session");
    });
    if (caret && menu) caret.addEventListener("click", (e) => {
      e.preventDefault(); e.stopPropagation();
      const wasOpen = !menu.hidden;
      closeCbMenus();
      if (!wasOpen) openCbMenu(split, caret, menu);
    });
    // Menu keyboard model (ARIA menu-button pattern): the items use roving tabindex (-1) and are
    // driven from here. Arrow keys move between choices, Home/End jump to the ends. Escape closes
    // the menu and returns focus to the caret. Tab must NOT be trapped: it closes the menu and
    // re-anchors focus on the in-flow caret (so focus never lands in the portaled-away <body>),
    // but does not preventDefault, so the browser's native Tab then advances focus to the next
    // element per the pattern. Enter/Space activate natively (buttons).
    if (caret && menu) menu.addEventListener("keydown", (e) => {
      const items = Array.prototype.slice.call(menu.querySelectorAll(".cb-menu-item"));
      if (!items.length) return;
      const i = items.indexOf(document.activeElement);
      if (e.key === "ArrowDown") { e.preventDefault(); items[(i + 1 + items.length) % items.length].focus(); }
      else if (e.key === "ArrowUp") { e.preventDefault(); items[(i - 1 + items.length) % items.length].focus(); }
      else if (e.key === "Home") { e.preventDefault(); items[0].focus(); }
      else if (e.key === "End") { e.preventDefault(); items[items.length - 1].focus(); }
      else if (e.key === "Escape") { e.preventDefault(); closeCbMenus(); caret.focus(); }
      else if (e.key === "Tab") { closeCbMenus(); caret.focus(); }
    });
    split.querySelectorAll(".cb-menu-item").forEach((mi) =>
      mi.addEventListener("click", (e) => {
        e.preventDefault(); e.stopPropagation();
        closeCbMenus();
        onCardAction(split, mi.dataset.target);
      }));
  });
  // Dismiss any open action menu on an outside click or Escape. Bound once so re-renders
  // don't stack handlers; the caret/main/menu handlers stopPropagation, so this only
  // fires for clicks elsewhere.
  if (!cbMenuBound) {
    cbMenuBound = true;
    document.addEventListener("click", () => closeCbMenus());
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeCbMenus(); });
    // A fixed menu doesn't track the caret once the page scrolls or the window resizes,
    // so dismiss rather than let it drift away from its button. Capture-phase scroll on
    // the document catches scrolling inside any nested container, not just the window.
    document.addEventListener("scroll", () => closeCbMenus(), true);
    if (typeof window !== "undefined" && window.addEventListener) {
      window.addEventListener("resize", () => closeCbMenus());
    }
  }
  const da = document.getElementById("dismiss-all"); if (da) da.addEventListener("click", dismissAll);
  const r1 = document.getElementById("restore-notifs"); if (r1) r1.addEventListener("click", restoreNotifs);
  const r2 = document.getElementById("restore-notifs2"); if (r2) r2.addEventListener("click", restoreNotifs);
  const ts = document.getElementById("to-settings"); if (ts) ts.addEventListener("click", () => goView("settings"));
  const brandHome = document.getElementById("brand-home"); if (brandHome) brandHome.addEventListener("click", () => goView("queue", false));
  const notifCfg = document.getElementById("notif-config");
  if (notifCfg) notifCfg.addEventListener("click", () => {
    goView("settings");
    requestAnimationFrame(() => {
      const sec = document.getElementById("notif-settings");
      if (sec) sec.scrollIntoView({ behavior: "smooth", block: "start" });
    });
  });
}

function wireAccounts() {
  // Active toggles interleave/withdraw an account's results across every tab.
  document.querySelectorAll("input[data-active]").forEach((inp) =>
    inp.addEventListener("change", () => { if (!inp.disabled) toggleAccountActive(inp.dataset.active, inp.checked); }));

  // Expand/collapse the account detail (repo editor + credential sources).
  document.querySelectorAll("[data-expand]").forEach((b) =>
    b.addEventListener("click", (e) => {
      e.stopPropagation();
      const id = b.dataset.expand;
      const card = document.querySelector('.acct-card[data-card="' + cssEsc(id) + '"]');
      if (!card) return;
      if (expanded.has(id)) { expanded.delete(id); card.classList.remove("open"); }
      else { expanded.add(id); card.classList.add("open"); }
    }));

  // Per-account watched-repository editors.
  document.querySelectorAll(".acct-card").forEach((card) => {
    const id = card.dataset.card;
    if (id) wireRepoEditor(id);
  });
}

// Live updates over Server-Sent Events. Progress drives the deterministic top bar. State events
// contain complete dashboards and apply atomically; when auto-apply is disabled, update-available
// changes only the toolbar until the user chooses to swap to the completed snapshot.
try {
  const es = new EventSource(standalone ? "events?client=" + dashboardClient : "events");
  if (standalone) es.addEventListener("refresh-error", (e) => {
    loadError = JSON.parse(e.data).error;
    if (view === "queue") render();
  });
  es.addEventListener("progress", (e) => {
    try { const p = JSON.parse(e.data); setProgress(p.done, p.total); } catch {}
  });
  es.addEventListener("state", (e) => {
    try { applyPushedState(JSON.parse(e.data)); } catch {}
  });
  es.addEventListener("update-available", (e) => {
    try { onUpdateAvailable(JSON.parse(e.data)); } catch {}
  });
  es.addEventListener("preferences", (e) => {
    try { onPreferences(JSON.parse(e.data)); } catch {}
  });
  es.addEventListener("snapshot", (e) => {
    try { onSnapshot(JSON.parse(e.data)); } catch {}
  });
  es.addEventListener("poll-schedule", (e) => {
    try { onPollSchedule(JSON.parse(e.data)); } catch {}
  });
} catch {}

load();
