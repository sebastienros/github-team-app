import assert from "node:assert/strict";
import vm from "node:vm";
import test from "node:test";

import { APP_JS, STYLES, HTML, THEME_JS } from "./render.mjs";

test("renderer uses Clawpilot theme variables and CSP-safe external scripts", () => {
  assert.match(STYLES, /--cp-bg: #f7f4ef/);
  assert.match(STYLES, /html\[data-theme="dark"\][\s\S]*?--cp-bg: #3d3b3a/);
  assert.match(STYLES, /background: var\(--cp-bg\)/);
  assert.match(STYLES, /color: var\(--cp-text\)/);
  assert.match(STYLES, /--font: "Segoe UI", Aptos, Calibri/);
  assert.doesNotMatch(STYLES.split("* { box-sizing: border-box; }")[1], /#[\da-f]{3,8}\b|rgba?\(/i);
  assert.match(HTML, /<script src="theme.js"><\/script>\s*<script src="standalone.js"><\/script>/);
  assert.deepEqual([...HTML.matchAll(/<script src="([^"]+)"/g)].map(match => match[1]),
    ["theme.js", "standalone.js", "app.js"]);
  assert.doesNotMatch(HTML + APP_JS, /<script>|style=|onerror=|Aspire|aspireTeamStandalone/);
  assert.match(HTML, /GitHub Team App/);
  assert.match(APP_JS, /window.githubTeamStandalone/);
  // Deterministic load bar replaced the looping indeterminate one (no glow, no paintfill).
  assert.match(STYLES, /\.loadbar \{[\s\S]*?transition: width/);
  assert.doesNotMatch(STYLES, /animation: paintfill/);
  assert.doesNotMatch(STYLES, /box-shadow: 0 0 8px/);
  assert.doesNotMatch(STYLES, /var\(--n-/);
  assert.match(STYLES, /\.refresh-pref\.active \{/);
  assert.match(STYLES, /\.update-ready\[hidden\] \{ display: none; \}/);
  assert.match(STYLES, /\.live-tooltip::after \{[\s\S]*?content: attr\(data-tooltip\)/);
  assert.match(STYLES, /\.live-tooltip:hover::after, \.live-tooltip:focus-visible::after/);
});

test("theme honors explicit light/dark selection and falls back to the system", () => {
  for (const [search, dark, expected] of [
    ["?clawpilotTheme=light", true, "light"],
    ["?clawpilotTheme=dark", false, "dark"],
    ["", true, "dark"], ["", false, "light"], ["?clawpilotTheme=invalid", false, "light"],
  ]) {
    let actual;
    vm.runInNewContext(THEME_JS, {
      URLSearchParams,
      window: { location: { search }, matchMedia: () => ({ matches: dark }) },
      document: { documentElement: { setAttribute: (key, value) => { assert.equal(key, "data-theme"); actual = value; } } },
    });
    assert.equal(actual, expected);
  }
});

test("render keeps the current dashboard visible and surfaces later load errors", () => {
  const { app, api } = createRendererHarness();

  api.setState({
    authenticated: true,
    accounts: [],
    activeAccounts: [],
    notifications: [],
  });
  api.setView("accounts");
  api.setLoadError("GitHub API 500 unavailable");
  api.render();

  assert.match(app.innerHTML, /GitHub API 500 unavailable/);
  assert.match(app.innerHTML, /GitHub accounts/);
});

test("forYouCardActions maps pick labels (and layered signals) to actions", () => {
  const { api } = createRendererHarness();

  const resolve = api.forYouCardActions({ action: "Resolve conflicts" });
  assert.equal(resolve.length, 1);
  assert.equal(resolve[0].kind, "resolve-conflicts");

  const review = api.forYouCardActions({ action: "Review this" });
  assert.equal(review.length, 1);
  assert.equal(review[0].kind, "review");
  assert.equal(review[0].label, "Review");

  const fixCi = api.forYouCardActions({ action: "Fix CI" });
  assert.equal(fixCi.length, 1);
  assert.equal(fixCi[0].kind, "fix-ci");
  assert.equal(fixCi[0].label, "Evaluate CI failures");

  // "Respond here" (your PR has feedback waiting) now offers Address feedback + Discuss review.
  const respond = api.forYouCardActions({ action: "Respond here" });
  assert.equal(respond.map((a) => a.kind).join(","), "address-feedback,discuss-review");
  assert.equal(respond[0].label, "Address feedback");

  // A pick that also carries a problem signal surfaces the matching action, deduped by kind:
  // "Resolve conflicts" pick + "merge conflicts" signal is still a single resolve-conflicts button.
  const deduped = api.forYouCardActions({ action: "Resolve conflicts", signals: [{ label: "merge conflicts" }] });
  assert.equal(deduped.map((a) => a.kind).join(","), "resolve-conflicts");

  assert.equal(api.forYouCardActions({ action: "Needs your attention" }), null);
  assert.equal(api.forYouCardActions(null), null);
});

test("signalActions surfaces conflict / CI / unresolved actions from a card's signal pills", () => {
  const { api } = createRendererHarness();

  assert.equal(api.signalActions({ signals: [{ label: "merge conflicts" }] }).map((a) => a.kind).join(","), "resolve-conflicts");
  assert.equal(api.signalActions({ signals: [{ label: "CI failing \u00b7 2 checks" }] }).map((a) => a.kind).join(","), "fix-ci");

  // Every unresolved-feedback pill maps to the address-feedback agent action, surfaced as "Resolve".
  // The pill text varies by surface: "{n} unresolved" (createAttentionSignals), the "Unresolved
  // feedback" bucket / focus-exclusion reason label, "{n} unresolved thread" (reviewSignal), and
  // the "resolve feedback" action pill all mean the same open-threads state.
  for (const label of ["3 unresolved", "Unresolved feedback", "2 unresolved threads", "resolve feedback"]) {
    const resolve = api.signalActions({ signals: [{ label }] });
    assert.equal(resolve.length, 1, label);
    assert.equal(resolve[0].kind, "address-feedback", label);
    assert.equal(resolve[0].label, "Resolve", label);
  }

  // A "review debt" pill (aged without an approving review) offers Address review + Discuss review,
  // and a "re-review" pill (author pushed after a review) offers Review. These surface wherever the
  // pill appears — including your own PRs — so a labelled card never renders without its button.
  const debt = api.signalActions({ pr: { isMine: false }, signals: [{ label: "review debt" }] });
  assert.equal(debt.map((a) => a.kind).join(","), "review-debt,discuss-review");
  const reReview = api.signalActions({ pr: { isMine: false }, signals: [{ label: "re-review" }] });
  assert.equal(reReview.map((a) => a.kind).join(","), "review");
  assert.equal(api.signalActions({ pr: { isMine: true }, signals: [{ label: "review debt" }] }).map((a) => a.kind).join(","), "review-debt,discuss-review");
  assert.equal(api.signalActions({ pr: { isMine: true }, signals: [{ label: "re-review" }] }).map((a) => a.kind).join(","), "review");
  assert.equal(api.signalActions({}).length, 0);
});

test("focusCardActions layers review-debt / review context with signal actions", () => {
  const { api } = createRendererHarness();

  // Review-debt card: Address review + Discuss review.
  const debt = api.focusCardActions({ reviewDebt: true, pr: { isMine: false } });
  assert.equal(debt.map((a) => a.kind).join(","), "review-debt,discuss-review");

  // Your OWN review-debt PR now offers Address review + Discuss review too. Focus cards carry a
  // reviewDebt flag (the "review debt" pill is often truncated off the displayed signals), and the
  // user asked that a review-debt card always carry its action — a self-review before others weigh
  // in is still useful.
  const mineDebt = api.focusCardActions({ reviewDebt: true, pr: { isMine: true } });
  assert.equal(mineDebt.map((a) => a.kind).join(","), "review-debt,discuss-review");

  // ...with a signal-driven fix layered on for your own review-debt PR.
  const mineDebtConflict = api.focusCardActions({ reviewDebt: true, pr: { isMine: true }, signals: [{ label: "merge conflicts" }] });
  assert.equal(mineDebtConflict.map((a) => a.kind).join(","), "review-debt,discuss-review,resolve-conflicts");

  // Changes requested takes precedence over review debt on your own PR: the ball is in your court.
  const mineDebtChanges = api.focusCardActions({ reviewDebt: true, pr: { isMine: true, review: { state: "changes_requested" } } });
  assert.equal(mineDebtChanges.map((a) => a.kind).join(","), "address-feedback,discuss-review");

  // Someone else's PR: Test + Review, plus a layered conflict action from its signal.
  const other = api.focusCardActions({ pr: { isMine: false }, signals: [{ label: "merge conflicts" }] });
  assert.equal(other.map((a) => a.kind).join(","), "test,review,resolve-conflicts");

  // Your own PR with no problem signal and no requested changes: no buttons (you don't review your
  // own work, and there's no feedback waiting on you).
  assert.equal(api.focusCardActions({ pr: { isMine: true } }), null);

  // Your own PR that is failing CI still offers the signal-driven fix action.
  const mineCi = api.focusCardActions({ pr: { isMine: true }, signals: [{ label: "CI failing" }] });
  assert.equal(mineCi.map((a) => a.kind).join(","), "fix-ci");

  // Your own PR with changes requested is waiting on you to respond, so it offers Address feedback
  // + Discuss review (mirroring the "Respond here" For You pick) instead of rendering actionless.
  const mineChanges = api.focusCardActions({ pr: { isMine: true, review: { state: "changes_requested" } } });
  assert.equal(mineChanges.map((a) => a.kind).join(","), "address-feedback,discuss-review");
  assert.equal(mineChanges[0].label, "Address feedback");

  // The "Your PRs outside Needs attention" lane tags the same case with an "Author response" pill,
  // which also qualifies even without review state on the card.
  const mineAuthorPill = api.focusCardActions({ pr: { isMine: true }, signals: [{ label: "Author response" }] });
  assert.equal(mineAuthorPill.map((a) => a.kind).join(","), "address-feedback,discuss-review");
});

test("laneCardActions keys breakdown-lane actions off the lane label", () => {
  const { api } = createRendererHarness();
  const other = { pr: { isMine: false } };
  const mine = { pr: { isMine: true } };

  assert.equal(api.laneCardActions({ label: "Needs review" }, other).map((a) => a.kind).join(","), "test,review");
  assert.equal(api.laneCardActions({ label: "Re-review needed" }, other).map((a) => a.kind).join(","), "review");
  assert.equal(api.laneCardActions({ label: "Unresolved feedback" }, other).map((a) => a.kind).join(","), "address-feedback,discuss-review");

  // Test/Review are withheld on your own PRs.
  assert.equal(api.laneCardActions({ label: "Needs review" }, mine), null);

  // Conflict lane carries the hoisted "merge conflicts" pill, so the button comes from signalActions.
  assert.equal(
    api.laneCardActions({ label: "Merge conflicts" }, { pr: { isMine: false }, signals: [{ label: "merge conflicts" }] }).map((a) => a.kind).join(","),
    "resolve-conflicts",
  );

  // The CI-failing pill is not hoisted and can be truncated off a stacked PR's card, so the CI lane
  // is mapped explicitly: it offers "Evaluate CI failures" even when no "CI failing" signal survives.
  assert.equal(
    api.laneCardActions({ label: "CI failing" }, { pr: { isMine: false }, signals: [{ label: "release 9.0" }, { label: "regression" }] }).map((a) => a.kind).join(","),
    "fix-ci",
  );
  // Fixing CI is the author's job, so the CI lane offers the action on your own PR too.
  assert.equal(
    api.laneCardActions({ label: "CI failing" }, { pr: { isMine: true }, signals: [] }).map((a) => a.kind).join(","),
    "fix-ci",
  );

  // Unresolved-feedback lane card that also carries the "N unresolved" pill stays a single
  // address-feedback button (Address feedback wins over the signal's "Resolve" by dedup).
  const unresolved = api.laneCardActions(
    { label: "Unresolved feedback" },
    { pr: { isMine: false }, signals: [{ label: "2 unresolved" }] },
  );
  assert.equal(unresolved.map((a) => a.kind).join(","), "address-feedback,discuss-review");
  assert.equal(unresolved[0].label, "Address feedback");
});

test("queuePanel reports an honest 'N shown' metric for a mixed (non-prefix) selection", () => {
  const { api } = createRendererHarness();
  const items = [1, 2, 3, 4].map((n) => ({ pr: { url: "", title: "t" + n, author: "a", repository: "o/r", number: n } }));

  // A genuine prefix (top N of a larger sorted list) keeps the "top N of total" claim.
  assert.match(api.queuePanel({ id: "q", title: "Q", items, cappedTotal: 9 }), /top 4 of 9/);

  // A mixed selection (review-debt cards spilled past the cap, so `items` is not a prefix of the
  // sorted list) must NOT claim "top N of total" — non-debt cards between retained debt cards were
  // skipped, so that would be false. It reports the honest shown count instead.
  const mixed = api.queuePanel({ id: "q", title: "Q", items, cappedTotal: 9, exactCount: true });
  assert.doesNotMatch(mixed, /top 4 of 9/);
  assert.match(mixed, /4 shown/);
});

test("cardActionBtn re-renders a disabled button while its action's POST is still in flight", () => {
  const { api } = createRendererHarness();
  const pr = { url: "https://github.com/o/r/pull/1", number: 1, repository: "o/r", title: "t", author: "a" };
  const action = { kind: "review", label: "Review", done: "Review requested", icon: "" };

  // Default render: the split button is enabled so the user can click it.
  const enabled = api.cardActionBtn(pr, action);
  assert.match(enabled, /data-target="new-session"/);
  assert.doesNotMatch(enabled, /disabled/);

  // Mark this exact (kind, PR) action as in flight, then re-render the card the way a streamed
  // 'state' event would. The replacement main button and caret must come back disabled so a click
  // can't re-queue the same agent action mid-request.
  api.inflightActions.add(api.actionKey(action.kind, pr.url, pr.repository, pr.number));
  const busy = api.cardActionBtn(pr, action);
  assert.match(busy, /class="card-btn cb-main busy" data-target="new-session" aria-live="polite" disabled/);
  assert.match(busy, /class="card-btn cb-caret"[^>]*disabled/);

  // A different action on the same PR is unaffected — only the in-flight split is locked.
  const other = api.cardActionBtn(pr, { kind: "test", label: "Test", done: "Testing requested", icon: "" });
  assert.doesNotMatch(other, /disabled/);
});

test("cardActionBtn greys out and spins while refresh finalization is in progress", () => {
  const { api } = createRendererHarness();
  const pr = { url: "https://github.com/o/r/pull/1", number: 1, repository: "o/r", title: "t", author: "a" };
  const action = { kind: "review", label: "Review", done: "Review requested", icon: "" };

  api.setRefreshing(true);
  const html = api.cardActionBtn(pr, action);
  assert.match(html, /class="card-btn cb-main busy spin" data-target="new-session" aria-live="polite" disabled/);
  assert.match(html, /Finalizing…/);
  assert.match(html, /class="card-btn cb-caret"[^>]*disabled/);
});

test("cardActionBtn defaults a GHES/EMU card to the current session with no new-session option", () => {
  const { api } = createRendererHarness();
  const action = { kind: "review", label: "Review", done: "Review requested", icon: "" };

  // A github.com PR gets the full split: the main button opens a new session and the caret menu
  // offers both new- and current-session targets.
  const dotcom = api.cardActionBtn({ url: "https://github.com/o/r/pull/1", number: 1, repository: "o/r", title: "t", author: "a" }, action);
  assert.match(dotcom, /data-target="new-session"/);
  assert.match(dotcom, /cb-caret/);
  assert.match(dotcom, /Open in new session/);

  // A GHES/EMU PR can't open a sub-session (open_pr_session targets github.com), so the server
  // degrades new-session to current-session. Render a single current-session button up front —
  // no caret and no misleading "Open in new session" item the user would only discover on click.
  const ghes = api.cardActionBtn({ url: "https://ghe.example.com:8443/o/r/pull/1", number: 1, repository: "o/r", title: "t", author: "a" }, action);
  assert.match(ghes, /data-target="current-session"/);
  assert.doesNotMatch(ghes, /new-session/);
  assert.doesNotMatch(ghes, /cb-caret/);
  assert.doesNotMatch(ghes, /Open in new session/);
});

test("withRefresh ignores a late older response so overlapping refreshes can't roll state back", async () => {
  // The module-init load() calls fetch("api/state"); a never-resolving fetch keeps it pending so it
  // can't clobber `state` mid-test. withRefresh takes its data from the fn argument, not fetch.
  const { api } = createRendererHarness({ fetch: () => new Promise(() => {}) });
  // authenticated:false keeps render() on the safe authPicker path; seq/marker are what we assert on.
  const dash = (seq, marker) => ({ dashboard: { seq, marker, authenticated: false, accounts: [], message: "" }, prefs: {} });

  // A newer refresh applies and advances lastAppliedSeq.
  await api.withRefresh(async () => dash(5, "new"));
  assert.equal(api.getState().seq, 5);
  assert.equal(api.getState().marker, "new");

  // An older forced load that resolves after the newer one must NOT overwrite the newer state:
  // applying it would roll state and lastAppliedSeq backward and show stale data.
  await api.withRefresh(async () => dash(3, "old"));
  assert.equal(api.getState().seq, 5);
  assert.equal(api.getState().marker, "new");

  // A strictly newer refresh still applies.
  await api.withRefresh(async () => dash(7, "newest"));
  assert.equal(api.getState().seq, 7);

  // Legacy payloads without a seq still apply (back-compat with pre-seq servers).
  await api.withRefresh(async () => ({ dashboard: { marker: "legacy", authenticated: false, accounts: [], message: "" }, prefs: {} }));
  assert.equal(api.getState().marker, "legacy");
});

test("withRefresh suppresses a stale older failure so it can't clobber newer valid state", async () => {
  // The module-init load() calls fetch("api/state"); a never-resolving fetch keeps it pending so it
  // can't clobber `state` mid-test. withRefresh takes its data from the fn argument, not fetch.
  const { api } = createRendererHarness({ fetch: () => new Promise(() => {}) });
  api.setLoadError(null);

  // Two overlapping refreshes. The older one (started first) rejects; the newer one succeeds first.
  // A rejection carries no seq, so without a generation gate the older catch would set loadError and
  // paint a failure banner over the newer valid state.
  let rejectOld;
  const oldRefresh = api.withRefresh(() => new Promise((_, reject) => { rejectOld = reject; }));
  const newRefresh = api.withRefresh(async () => ({ dashboard: { seq: 9, marker: "fresh", authenticated: false, accounts: [], message: "" }, prefs: {} }));
  await newRefresh;
  assert.equal(api.getState().marker, "fresh");
  assert.equal(api.getLoadError(), null);

  // The older refresh rejects late. Its failure must be suppressed because a newer refresh started
  // after it, leaving the newer valid state and a null error banner intact.
  rejectOld(new Error("stale network blip"));
  await oldRefresh;
  assert.equal(api.getLoadError(), null);
  assert.equal(api.getState().marker, "fresh");

  // A rejection from the latest-started refresh still surfaces (the gate only drops superseded ones).
  await api.withRefresh(async () => { throw new Error("current failure"); });
  assert.equal(api.getLoadError(), "current failure");
});


test("load ignores a stale GET /api/state response so it can't rewind lastAppliedSeq", async () => {
  // GET /api/state may be served stale-while-revalidate: the cached payload (seq 3) can settle after
  // the background stream already delivered a newer snapshot (seq 5). fetch always returns the stale
  // seq-3 payload here to model that race.
  const stale = { dashboard: { seq: 3, marker: "stale", authenticated: false, accounts: [], message: "" }, prefs: {} };
  const { api } = createRendererHarness({ fetch: async () => jsonResponse(stale) });

  // Establish a newer applied revision (seq 5). withRefresh takes its data from fn, not fetch.
  await api.withRefresh(async () => ({ dashboard: { seq: 5, marker: "fresh", authenticated: false, accounts: [], message: "" }, prefs: {} }));
  assert.equal(api.getState().seq, 5);
  assert.equal(api.getAppliedSeq(), 5);

  // A stale forced load must be gated out — applying it would roll state and lastAppliedSeq backward.
  await api.load();
  assert.equal(api.getState().seq, 5);
  assert.equal(api.getState().marker, "fresh");
  assert.equal(api.getAppliedSeq(), 5);
});

test("load suppresses its failure when a newer revision was applied while the GET was pending", async () => {
  // A GET served stale-while-revalidate can still be in flight when an SSE 'state' event applies a
  // newer snapshot (advancing lastAppliedSeq). Model that: this load()'s fetch stays pending until we
  // reject it, and in between a newer snapshot lands via withRefresh. The late GET failure must not
  // paint an error banner over the newer valid state.
  let rejectFetch;
  const { api } = createRendererHarness({ fetch: () => new Promise((_, reject) => { rejectFetch = reject; }) });
  api.setLoadError(null);

  // Start the GET; its fetch stays pending (rejectFetch now targets this call, not the module-init one).
  const pending = api.load();

  // A newer snapshot lands while the GET is pending, advancing lastAppliedSeq past load()'s start seq.
  await api.withRefresh(async () => ({ dashboard: { seq: 12, marker: "fresh", authenticated: false, accounts: [], message: "" }, prefs: {} }));
  assert.equal(api.getAppliedSeq(), 12);

  // The GET now fails, but its error is suppressed because a newer revision was applied since it began.
  rejectFetch(new Error("stale GET blip"));
  await pending;
  assert.equal(api.getLoadError(), null);
  assert.equal(api.getState().marker, "fresh");
});

test("rescanAccounts discovers credential metadata without replacing dashboard or preferences", async () => {
  const account = { id: "acct:github.com/octo", login: "octo", host: "github.com", status: "ok" };
  const staleRescan = { accounts: [account] };
  const { api } = createRendererHarness({
    fetch: async (url) => String(url) === "api/accounts" ? jsonResponse(staleRescan) : new Promise(() => {}),
  });

  // Establish a newer applied revision (seq 9).
  await api.withRefresh(async () => ({ dashboard: { seq: 9, marker: "fresh", authenticated: false, accounts: [], message: "" }, prefs: {} }));
  assert.equal(api.getAppliedSeq(), 9);

  // Discovery is separate from dashboard state and never changes its revision.
  await api.rescanAccounts();
  assert.equal(api.currentAccounts()[0].login, "octo");
  assert.equal(api.getState().marker, "fresh");
  assert.equal(api.getAppliedSeq(), 9);
});

test("onCardAction recovers a detached card by re-rendering, but only while the queue is showing", async () => {
  // The action POST resolves; api/state stays pending so the module-init load() can't render mid-test
  // and pollute the assertions below.
  const fetchMock = async (path) =>
    String(path).includes("api/agent/action")
      ? jsonResponse({ queued: false, target: "new-session" })
      : new Promise(() => {});
  const { app, api } = createRendererHarness({ fetch: fetchMock });
  // A queue-renderable state so render() produces output (render() early-returns when state is null).
  // Empty lanes/counts render the "All clear" queue, which is enough for a non-empty innerHTML.
  const queueState = () => ({
    authenticated: true,
    viewer: "octo",
    mode: "review",
    attention: null,
    lanes: [],
    accounts: [],
    activeAccounts: [],
    notifications: [],
    repos: [],
    counts: { prs: 0, drafts: 0, needsReview: 0, readyToMerge: 0, ciFailing: 0 },
    fetchedAt: Date.now(),
    showDrafts: true,
    errors: [],
  });

  const makeBtn = () => {
    const cls = new Set();
    return {
      disabled: false,
      innerHTML: "",
      classList: { add: (c) => cls.add(c), remove: (c) => cls.delete(c), contains: (c) => cls.has(c) },
    };
  };
  const makeSplit = (connected) => {
    const main = makeBtn();
    const caret = makeBtn();
    return {
      isConnected: connected,
      dataset: { kind: "review", prUrl: "https://github.com/o/r/pull/1", prRepo: "o/r", prNumber: "1" },
      querySelector: (sel) => (sel === ".cb-main" ? main : sel === ".cb-caret" ? caret : null),
    };
  };

  // Detached split while the queue is showing: a streamed 'state' event replaced the card while the
  // POST was pending, so the visible replacement was rendered disabled. Settling must re-render the
  // queue so the button reflects the cleared inflight key instead of staying stuck disabled.
  api.setState(queueState());
  api.setView("queue");
  app.innerHTML = "";
  await api.onCardAction(makeSplit(false), "new-session");
  assert.notEqual(app.innerHTML, "");

  // Detached split while a non-queue form is open (Accounts/Settings/Filters): the recovery render is
  // suppressed so it can't rebuild the open form and discard text the user hasn't committed yet.
  // goView() re-renders the queue when they navigate back, so the card is never left stuck.
  api.setState(queueState());
  api.setView("accounts");
  app.innerHTML = "";
  await api.onCardAction(makeSplit(false), "new-session");
  assert.equal(app.innerHTML, "");

  // Still-connected split in the queue: keep the deliberate no-re-render behavior so the inline
  // confirmation stays.
  api.setState(queueState());
  api.setView("queue");
  app.innerHTML = "";
  await api.onCardAction(makeSplit(true), "new-session");
  assert.equal(app.innerHTML, "");
});

test("onCardAction retry inside the failure-restore window starts clean and isn't clobbered by the stale timer", async () => {
  // Controllable timers: the harness default runs setTimeout synchronously, which would close the
  // ~3.2s failure-restore window instantly. Capture callbacks so we fire them on demand instead.
  const timers = new Map();
  let nextId = 1;
  const setTimeoutMock = (handler) => { const id = nextId++; timers.set(id, handler); return id; };
  const clearTimeoutMock = (id) => { timers.delete(id); };

  // First action POST fails (500 -> readJson throws); the retry succeeds. api/state stays pending so
  // the module-init load() can't render mid-test and pollute the assertions.
  let actionCalls = 0;
  const fetchMock = async (path) => {
    if (!String(path).includes("api/agent/action")) return new Promise(() => {});
    actionCalls += 1;
    return actionCalls === 1
      ? jsonResponse({ error: "boom" }, { ok: false, status: 500 })
      : jsonResponse({ queued: false, target: "new-session" });
  };

  const { api } = createRendererHarness({ fetch: fetchMock, setTimeout: setTimeoutMock, clearTimeout: clearTimeoutMock });

  // One stable split/button reused across both clicks (the same still-connected card being retried).
  const cls = new Set();
  const defaultLabel = '<span class="cb-label">Start review</span>';
  const main = {
    disabled: false,
    innerHTML: defaultLabel,
    classList: { add: (c) => cls.add(c), remove: (c) => cls.delete(c), contains: (c) => cls.has(c) },
  };
  const split = {
    isConnected: true,
    dataset: { kind: "review", prUrl: "https://github.com/o/r/pull/1", prRepo: "o/r", prNumber: "1" },
    querySelector: (sel) => (sel === ".cb-main" ? main : null),
  };
  // Drop any timers scheduled during module init so `timers` holds only what the actions schedule.
  timers.clear();

  // First attempt fails: the button shows the error, is re-enabled, and schedules a restore timer.
  await api.onCardAction(split, "new-session");
  assert.ok(cls.has("failed"), "first failure should mark the button .failed");
  assert.equal(main.disabled, false, "failed button is re-enabled so it can be retried");
  assert.match(main.innerHTML, /boom/);
  assert.equal(timers.size, 1, "a restore timer should be pending after the failure");

  // Retry inside the window succeeds. It must start from a clean slate: no inherited .failed styling.
  await api.onCardAction(split, "new-session");
  assert.ok(cls.has("done"), "retry success should mark the button .done");
  assert.ok(!cls.has("failed"), "retry must not inherit the prior attempt's failure styling");
  assert.match(main.innerHTML, /Requested/);

  // The stale first timer must have been cancelled; firing whatever remains must not revert the
  // retry's success label back to the default.
  for (const cb of timers.values()) { cb(); }
  assert.match(main.innerHTML, /Requested/, "a stale restore timer must not overwrite the retry's label");
  assert.ok(!cls.has("failed"));
});

test("setProgress doesn't fade the bar from a terminal SSE tick while another refresh is still in flight", () => {
  // The harness runs setTimeout synchronously, so endProgress()'s fade + reset run inline: a faded
  // bar ends with "active" removed and width "0". A non-faded bar stays "active" at width "100%".
  const cls = new Set();
  const loadbar = {
    style: { width: "" },
    classList: { add: (c) => cls.add(c), remove: (c) => cls.delete(c), contains: (c) => cls.has(c) },
  };
  const { api } = createRendererHarness({ loadbar });
  cls.clear();
  loadbar.style.width = "";

  // Two overlapping withRefresh() calls in flight: the first compute's terminal tick must NOT fade
  // the bar while the second is still fetching (the counter's "last operation settles" invariant).
  api.setRefreshInFlight(2);
  api.setProgress(1, 1);
  assert.ok(cls.has("active"), "bar must stay active while a second refresh is still in flight");
  assert.equal(loadbar.style.width, "100%");

  // Once only the last refresh remains, its terminal tick completes and fades the bar.
  cls.clear();
  loadbar.style.width = "";
  api.setRefreshInFlight(1);
  api.setProgress(1, 1);
  assert.ok(!cls.has("active"), "the last refresh's terminal tick should fade the bar");
  assert.equal(loadbar.style.width, "0");
});

test("an available background update leaves the board unchanged until it is applied", async () => {
  let stateReads = 0;
  const next = {
    dashboard: { seq: 8, marker: "complete", authenticated: false, accounts: [], message: "" },
    prefs: { autoApplyUpdates: false },
  };
  const { api } = createRendererHarness({
    fetch: async (url) => {
      if (String(url) !== "api/state") return new Promise(() => {});
      stateReads++;
      return stateReads === 1 ? new Promise(() => {}) : jsonResponse(next);
    },
  });
  api.setState({ seq: 4, marker: "visible", authenticated: false, accounts: [], message: "" });
  api.setPrefs({ autoApplyUpdates: false });
  api.onUpdateAvailable({ seq: 8, fetchedAt: "2026-08-06T00:00:00Z" });

  assert.equal(api.getState().marker, "visible");
  assert.equal(api.getUpdateAvailable().seq, 8);

  await api.applyAvailableUpdate();

  assert.equal(api.getState().marker, "complete");
  assert.equal(api.getAppliedSeq(), 8);
  assert.equal(api.getUpdateAvailable(), null);
});

test("the toolbar Auto switch persists without refreshing the board", async () => {
  let posted;
  const { api } = createRendererHarness({
    fetch: async (url, options) => {
      if (String(url) === "api/auto-apply") {
        posted = JSON.parse(options.body);
        return jsonResponse({ prefs: { autoApplyUpdates: false } });
      }
      return new Promise(() => {});
    },
  });
  api.setState({ seq: 3, marker: "visible", authenticated: false, accounts: [], message: "" });
  api.setPrefs({ autoApplyUpdates: true });

  await api.toggleAutoApply();

  assert.deepEqual(posted, { enabled: false });
  assert.equal(api.autoApplyEnabled(), false);
  assert.equal(api.getState().marker, "visible");
});

test("enabling Auto applies an update that was already waiting", async () => {
  let stateReads = 0;
  const { api } = createRendererHarness({
    fetch: async (url) => {
      if (String(url) === "api/auto-apply") {
        return jsonResponse({ prefs: { autoApplyUpdates: true } });
      }
      if (String(url) === "api/state") {
        stateReads++;
        if (stateReads === 1) return new Promise(() => {});
        return jsonResponse({
          dashboard: { seq: 9, marker: "applied", authenticated: false, accounts: [], message: "" },
          prefs: { autoApplyUpdates: true },
        });
      }
      return new Promise(() => {});
    },
  });
  api.setState({ seq: 5, marker: "visible", authenticated: false, accounts: [], message: "" });
  api.setPrefs({ autoApplyUpdates: false });
  api.onUpdateAvailable({ seq: 9 });

  await api.toggleAutoApply();

  assert.equal(api.autoApplyEnabled(), true);
  assert.equal(api.getState().marker, "applied");
  assert.equal(api.getUpdateAvailable(), null);
});

test("snapshot replay restores the pending indicator without replacing the board when Auto is off", () => {
  const { api } = createRendererHarness({ fetch: () => new Promise(() => {}) });
  api.setState({ seq: 5, marker: "visible", authenticated: false, accounts: [], message: "" });
  api.setPrefs({ autoApplyUpdates: true });

  api.onSnapshot({ seq: 9, prefs: { autoApplyUpdates: false } });

  assert.equal(api.getState().marker, "visible");
  assert.equal(api.autoApplyEnabled(), false);
  assert.equal(api.getUpdateAvailable().seq, 9);
});

test("refresh tooltip counts down to the server's next background poll", () => {
  const attributes = {};
  const refreshButton = {
    dataset: {},
    classList: classList(),
    setAttribute(name, value) { attributes[name] = value; },
  };
  let intervalMs;
  const { api } = createRendererHarness({
    elements: { "refresh-btn": refreshButton },
    setInterval(_handler, milliseconds) { intervalMs = milliseconds; return 1; },
  });

  api.onPollSchedule({ nextPollAt: Date.now() + 34_000 });

  assert.match(refreshButton.dataset.tooltip, /^Refresh now \(data will auto-update in 3[34]s\)$/);
  assert.equal(attributes["aria-label"], refreshButton.dataset.tooltip);
  assert.equal(intervalMs, 1000);
});

test("issueCard renders linked pull requests as separate safe new-tab links below the pills", () => {
  const { api } = createRendererHarness();
  const html = api.issueCard({
    issue: {
      repository: "microsoft/aspire",
      number: 42,
      title: "Issue title",
      url: "https://github.com/microsoft/aspire/issues/42",
      author: "octo",
      authorAvatarUrl: null,
      linkedPullRequests: [{
        repository: "microsoft/aspire",
        number: 99,
        title: "Cover single-file AppHost re-search fallback",
        url: "https://github.com/microsoft/aspire/pull/99",
        state: "OPEN",
      }, {
        repository: "microsoft/aspire",
        number: 100,
        title: "Merged implementation",
        url: "https://github.com/microsoft/aspire/pull/100",
        state: "MERGED",
      }, {
        repository: "microsoft/aspire",
        number: 101,
        title: "Closed implementation",
        url: "https://github.com/microsoft/aspire/pull/101",
        state: "CLOSED",
      }],
    },
    signals: [{ label: "Regression", tone: "danger" }],
  });

  assert.match(html, /class="card-main" href="https:\/\/github\.com\/microsoft\/aspire\/issues\/42" target="_blank" rel="noopener noreferrer"/);
  assert.match(html, /class="card-main linked-pr" href="https:\/\/github\.com\/microsoft\/aspire\/pull\/99" target="_blank" rel="noopener noreferrer"/);
  assert.match(html, /aria-label="Open pull request: Cover single-file AppHost re-search fallback"[\s\S]*linked-pr-icon open/);
  assert.match(html, /href="https:\/\/github\.com\/microsoft\/aspire\/pull\/100"[\s\S]*aria-label="Merged pull request: Merged implementation"[\s\S]*linked-pr-icon merged/);
  assert.doesNotMatch(html, /pull\/101|Closed implementation/);
  assert.doesNotMatch(html, /class="linked-prs"/);
  assert.ok(html.indexOf('class="pills"') < html.indexOf('class="card-main linked-pr"'));
});

test("openLinkedPr routes the canonical link through the in-app browser endpoint", async () => {
  let request;
  const { api } = createRendererHarness({
    fetch: async (url, options) => {
      if (String(url) === "api/open-pr") {
        request = { url: String(url), body: JSON.parse(options.body) };
        return jsonResponse({ ok: true, instanceId: "aspire-team-app-pr-microsoft-aspire-99" });
      }
      return new Promise(() => {});
    },
  });
  const link = {
    href: "https://github.com/microsoft/aspire/pull/99",
    classList: classList(),
  };

  await api.openLinkedPr(link);

  assert.deepEqual(request, {
    url: "api/open-pr",
    body: { url: "https://github.com/microsoft/aspire/pull/99" },
  });
  assert.equal(link.classList.contains("busy"), false);
});

test("signalActions detects review debt from the serialized flag when the pill is truncated", () => {
  const { api } = createRendererHarness();

  // A stacked card whose "review debt" pill was dropped by signalsFor's 4-pill cap still carries the
  // reviewDebt flag, so Address review + Discuss review must still surface (even on your own PRs).
  assert.equal(
    api.signalActions({ reviewDebt: true, pr: { isMine: true }, signals: [{ label: "released" }, { label: "regression" }] }).map((a) => a.kind).join(","),
    "review-debt,discuss-review",
  );
  // No flag and no pill -> no review-debt actions.
  assert.equal(api.signalActions({ pr: { isMine: false }, signals: [{ label: "released" }] }).length, 0);
});

test("signalActions ignores raw GitHub label pills so a repo label can't spoof a destructive action", () => {
  const { api } = createRendererHarness();

  // A repo label literally named like an action signal (kind "repo-label", set in model.mjs) must NOT
  // authorize the action; only app-computed semantic signals do.
  assert.equal(api.signalActions({ signals: [{ label: "merge conflicts", kind: "repo-label" }] }).length, 0);
  assert.equal(api.signalActions({ signals: [{ label: "CI failing", kind: "repo-label" }] }).length, 0);
  assert.equal(api.signalActions({ signals: [{ label: "3 unresolved", kind: "repo-label" }] }).length, 0);
  assert.equal(api.signalActions({ pr: { isMine: false }, signals: [{ label: "re-review", kind: "repo-label" }] }).length, 0);
  // A "review debt" label pill must not spoof Address review / Discuss review through isReviewDebtItem.
  assert.equal(api.signalActions({ pr: { isMine: false }, signals: [{ label: "review debt", kind: "repo-label" }] }).length, 0);
  assert.equal(
    api.focusCardActions({ pr: { isMine: false }, signals: [{ label: "review debt", kind: "repo-label" }] }).map((a) => a.kind).join(","),
    "test,review",
  );

  // The same labels as app-computed signals (no kind) still authorize their actions.
  assert.equal(api.signalActions({ signals: [{ label: "merge conflicts" }] }).map((a) => a.kind).join(","), "resolve-conflicts");
});

test("Health mode renders provider evidence and remains available without GitHub authentication", () => {
  const { app, api } = createRendererHarness();
  const github = {
    id: "github:github.com:microsoft/aspire-samples",
    provider: "github",
    name: "microsoft/aspire-samples",
    repository: "microsoft/aspire-samples",
    branch: "main",
    url: "https://github.com/microsoft/aspire-samples",
    state: "failing",
    latest: {
      id: "abcdef0123456789",
      at: "2026-01-08T00:00:00Z",
      actor: "dependabot[bot]",
      message: "Bump package <unsafe>",
    },
    daysSinceSuccess: 9,
    failureStreak: 7,
    canOpenRepoSession: true,
    reasons: [{
      tone: "danger",
      summary: "The failing head is a likely regression source <unsafe>.",
      url: "javascript:alert(1)",
    }],
    evidence: [{ label: "CI / build", detail: "failure & timeout", url: "https://github.com/microsoft/aspire-samples/actions" }],
  };
  const azure = {
    id: "azdo:dnceng:internal:1602",
    provider: "azure-devops",
    name: "microsoft-aspire",
    branch: "refs/heads/main",
    url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602",
    state: "degraded",
    latest: { id: 42, number: "20260108.1", at: "2026-01-08T01:00:00Z", result: "partiallySucceeded" },
    daysSinceSuccess: 1,
    failureStreak: 50,
    failureStreakLowerBound: true,
    canOpenRepoSession: false,
    reasons: [{ tone: "warning", summary: "Deployment is likely blocked upstream." }],
    evidence: [],
  };

  api.setState(healthDashboard([github, azure], {
    total: 2,
    healthy: 0,
    running: 0,
    degraded: 1,
    failing: 1,
    unavailable: 0,
    unknown: 0,
  }, false));
  api.setPrefs(rendererPrefs());
  api.render();

  assert.match(app.innerHTML, /data-mode="health"/);
  assert.match(app.innerHTML, /Repository &amp; delivery health/);
  assert.match(app.innerHTML, /microsoft\/aspire-samples/);
  assert.match(app.innerHTML, /Failure streak<\/span><span class="v">7<\/span>/);
  assert.match(app.innerHTML, /Failure streak<\/span><span class="v">50\+<\/span>/);
  assert.match(app.innerHTML, /class="health-reason-banner"/);
  assert.match(app.innerHTML, /likely regression source &lt;unsafe&gt;/);
  assert.doesNotMatch(app.innerHTML, /<unsafe>/);
  assert.match(app.innerHTML, /href="#"[^>]*>The failing head/);
  assert.match(app.innerHTML, /data-kind="diagnose-health"[^>]*data-source-id="github:github\.com:microsoft\/aspire-samples"/);
  assert.match(app.innerHTML, /Fix in repo/);
  assert.match(app.innerHTML, /Work fix here/);
  assert.match(app.innerHTML, /class="health-drag"/);
  assert.match(app.innerHTML, /draggable="true"/);
  assert.match(app.innerHTML, /aria-label="Reorder microsoft\/aspire-samples\. Position 1 of 2\."/);
  assert.match(app.innerHTML, /<details class="health-details"/);
  assert.match(app.innerHTML, /class="health-details-chevron" aria-hidden="true"/);
  assert.match(app.innerHTML, /class="health-metrics"/);
  assert.doesNotMatch(app.innerHTML, /No GitHub credentials detected/);
  assert.match(STYLES, /\.health-card:hover \{[\s\S]*?translateY\(-1px\)/);
  assert.match(STYLES, /\.health-unit\.dragging \{[\s\S]*?rotate\(\.35deg\)/);
  assert.match(STYLES, /\.health-drag-ghost \{[\s\S]*?var\(--cp-shadow\)/);
  assert.match(STYLES, /\.health-details\[open\] \.health-details-chevron \{ transform: rotate\(180deg\); \}/);
  assert.match(STYLES, /prefers-reduced-motion: reduce[\s\S]*?\.health-unit\.dragging/);
  assert.match(STYLES, /@media \(max-width: 470px\)[\s\S]*?button\.brand \{ display: none; \}[\s\S]*?#filters-btn \{ display: none; \}/);
});

test("Health mode groups related provider sources under one repository", () => {
  const groupId = "repository:github.com/microsoft/aspire";
  const github = {
    id: "github:github.com/microsoft/aspire",
    provider: "github",
    name: "microsoft/aspire",
    repository: "microsoft/aspire",
    host: "github.com",
    groupId,
    groupName: "microsoft/aspire",
    groupMatch: "canonical",
    branch: "main",
    url: "https://github.com/microsoft/aspire",
    state: "healthy",
    reasons: [],
    evidence: [],
  };
  const azure = {
    id: "azdo:dnceng/internal/1602",
    provider: "azure-devops",
    name: "microsoft-aspire",
    organizationName: "dnceng",
    project: "internal",
    groupId,
    groupName: "microsoft/aspire",
    groupMatch: "name",
    branch: "refs/heads/main",
    url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602",
    discovered: true,
    discovery: { kind: "azure-cli-default" },
    state: "failing",
    reasons: [],
    evidence: [],
  };
  const { app, api } = createRendererHarness();
  api.setState(healthDashboard([github, azure], {
    total: 2,
    healthy: 1,
    running: 0,
    degraded: 0,
    failing: 1,
    unavailable: 0,
    unknown: 0,
  }, true));
  api.setPrefs(rendererPrefs());

  api.render();

  assert.match(app.innerHTML, /class="health-unit health-source-group"/);
  assert.match(app.innerHTML, /microsoft\/aspire/);
  assert.match(app.innerHTML, /2 delivery sources/);
  assert.match(app.innerHTML, /Repository name match/);
  assert.match(app.innerHTML, /Default branch/);
  assert.match(app.innerHTML, /Azure DevOps \u00b7 dnceng\/internal/);
  assert.match(app.innerHTML, /Auto\u2011discovered/);
  assert.match(
    api.healthCard({ ...azure, discovery: { kind: "official-default" } }, 0, 1),
    /Official default/,
  );
  assert.match(app.innerHTML, /1 repository group across 2 sources\. Drag groups to prioritize\./);
  assert.equal((app.innerHTML.match(/data-health-drag=/g) || []).length, 1);
});

test("Health order saves optimistically and keeps the server-confirmed order", async () => {
  let request = null;
  const motionView = { classList: classList() };
  motionView.classList.add("no-motion");
  const { api } = createRendererHarness({
    querySelector: (selector) => selector === ".view" ? motionView : null,
    fetch: async (path, options) => {
      request = { path: String(path), body: JSON.parse(options.body) };
      return jsonResponse({
        dashboard: {
          ...healthDashboard([second, first], {
            total: 2,
            healthy: 1,
            running: 0,
            degraded: 0,
            failing: 1,
            unavailable: 0,
            unknown: 0,
          }, true),
          seq: 2,
        },
        prefs: { ...rendererPrefs(), healthOrder: [second.id, first.id] },
      });
    },
  });
  const first = {
    id: "github:github.com:microsoft/aspire",
    provider: "github",
    name: "microsoft/aspire",
    state: "healthy",
    reasons: [],
    evidence: [],
  };
  const second = {
    id: "azdo:dnceng:internal:1602",
    provider: "azure-devops",
    name: "Internal deployment",
    state: "failing",
    reasons: [],
    evidence: [],
  };
  api.setState({
    ...healthDashboard([first, second], {
      total: 2,
      healthy: 1,
      running: 0,
      degraded: 0,
      failing: 1,
      unavailable: 0,
      unknown: 0,
    }, true),
    seq: 1,
  });
  api.setPrefs(rendererPrefs());

  await api.commitHealthOrder([second, first], [first, second], second.id);

  assert.deepEqual(request, {
    path: "api/health/order",
    body: { order: [second.id, first.id] },
  });
  assert.deepEqual(JSON.parse(JSON.stringify(api.getState().health.items)), [second, first]);
  assert.deepEqual(JSON.parse(JSON.stringify(api.getPrefs().healthOrder)), [second.id, first.id]);
  assert.equal(motionView.classList.contains("no-motion"), false);
});

test("Health order rolls back when persistence fails", async () => {
  const first = {
    id: "github:github.com/microsoft/aspire",
    provider: "github",
    name: "microsoft/aspire",
    state: "healthy",
    reasons: [],
    evidence: [],
  };
  const second = {
    id: "azdo:dnceng:internal:1602",
    provider: "azure-devops",
    name: "Internal deployment",
    state: "failing",
    reasons: [],
    evidence: [],
  };
  const { app, api } = createRendererHarness({
    fetch: async () => jsonResponse({ error: "Preferences are read-only" }, { ok: false, status: 500 }),
  });
  api.setState({
    ...healthDashboard([first, second], {
      total: 2,
      healthy: 1,
      running: 0,
      degraded: 0,
      failing: 1,
      unavailable: 0,
      unknown: 0,
    }, true),
    seq: 1,
  });
  api.setPrefs(rendererPrefs());

  await api.commitHealthOrder([second, first], [first, second], second.id);

  assert.deepEqual(JSON.parse(JSON.stringify(api.getState().health.items)), [first, second]);
  assert.match(app.innerHTML, /Preferences are read-only/);
});

test("Health group reordering persists every related source as one contiguous unit", async () => {
  let request = null;
  const aspireGroup = "repository:github.com/microsoft/aspire";
  const docsGroup = "repository:github.com/microsoft/aspire.dev";
  const github = {
    id: "github:github.com/microsoft/aspire",
    provider: "github",
    name: "microsoft/aspire",
    groupId: aspireGroup,
    groupName: "microsoft/aspire",
    state: "healthy",
    reasons: [],
    evidence: [],
  };
  const azure = {
    id: "azdo:dnceng/internal/1602",
    provider: "azure-devops",
    name: "microsoft-aspire",
    groupId: aspireGroup,
    groupName: "microsoft/aspire",
    state: "failing",
    reasons: [],
    evidence: [],
  };
  const docs = {
    id: "github:github.com/microsoft/aspire.dev",
    provider: "github",
    name: "microsoft/aspire.dev",
    groupId: docsGroup,
    groupName: "microsoft/aspire.dev",
    state: "healthy",
    reasons: [],
    evidence: [],
  };
  const { app, api } = createRendererHarness({
    fetch: async (path, options) => {
      request = { path: String(path), body: JSON.parse(options.body) };
      return jsonResponse({ prefs: { ...rendererPrefs(), healthOrder: request.body.order } });
    },
  });
  api.setState({
    ...healthDashboard([github, azure, docs], {
      total: 3,
      healthy: 2,
      running: 0,
      degraded: 0,
      failing: 1,
      unavailable: 0,
      unknown: 0,
    }, true),
    seq: 1,
  });
  api.setPrefs(rendererPrefs());

  await api.dropHealthSource(docsGroup, aspireGroup, false);

  assert.deepEqual(request.body.order, [docs.id, github.id, azure.id]);
  assert.deepEqual(
    JSON.parse(JSON.stringify(api.getState().health.items.map((item) => item.id))),
    [docs.id, github.id, azure.id],
  );
  assert.match(app.innerHTML, /Moved microsoft\/aspire\.dev to position 1 of 2\./);
});

test("Health dragover keeps its marker stable while the pointer remains in the same drop zone", () => {
  const first = dragCard();
  const second = dragCard();
  first.dataset.healthGroupId = "first";
  second.dataset.healthGroupId = "second";
  const handle = dragHandle("first", first);
  const { api } = createRendererHarness({
    querySelectorAll: (selector) => {
      if (selector === ".health-unit" || selector === ".health-unit[data-health-group-id]") return [first, second];
      if (selector === "[data-health-drag]") return [handle];
      return [];
    },
  });

  api.wireHealthOrdering();
  handle.listeners.dragstart({
    dataTransfer: {
      effectAllowed: "",
      setData() {},
    },
  });
  const dragover = {
    clientX: 75,
    clientY: 50,
    preventDefault() {},
    dataTransfer: { dropEffect: "" },
  };
  second.listeners.dragover(dragover);
  const mutations = second.mutations;

  second.listeners.dragover(dragover);
  assert.equal(second.mutations, mutations);
  assert.equal(second.classList.has("drag-after"), true);

  second.listeners.dragover({ ...dragover, clientX: 50, clientY: 25 });
  assert.equal(second.classList.has("drag-after"), false);
  assert.equal(second.classList.has("drag-before"), true);
});

test("an equivalent order confirmation advances state without repainting the grid", () => {
  const first = {
    id: "github:github.com/microsoft/aspire",
    provider: "github",
    name: "microsoft/aspire",
    state: "healthy",
    reasons: [],
    evidence: [],
  };
  const initial = { ...healthDashboard([first], {
    total: 1,
    healthy: 1,
    running: 0,
    degraded: 0,
    failing: 0,
    unavailable: 0,
    unknown: 0,
  }, true), seq: 1 };
  const { app, api } = createRendererHarness();
  api.setState(initial);
  api.setPrefs(rendererPrefs());
  api.setHealthOrderSaving(true);
  app.innerHTML = "stable grid";

  api.applyPushedState({
    dashboard: { ...initial, seq: 2 },
    prefs: { ...rendererPrefs(), healthOrder: [first.id] },
  });

  assert.equal(app.innerHTML, "stable grid");
  assert.equal(api.getState().seq, 2);
  assert.equal(api.getAppliedSeq(), 2);
});

test("health actions post only the canonical source id to the dedicated endpoint", async () => {
  let request = null;
  const { api } = createRendererHarness({
    fetch: async (path, options) => {
      if (String(path) !== "api/health/action") return new Promise(() => {});
      request = { path: String(path), body: JSON.parse(options.body) };
      return jsonResponse({ queued: false, target: "current-session" });
    },
  });
  const classes = new Set();
  const main = {
    disabled: false,
    innerHTML: "Diagnose here",
    classList: {
      add(value) { classes.add(value); },
      remove(value) { classes.delete(value); },
      contains(value) { return classes.has(value); },
    },
  };
  const split = {
    isConnected: true,
    dataset: { kind: "diagnose-health", sourceId: "github:github.com:microsoft/aspire", doneLabel: "Requested" },
    querySelector(selector) { return selector === ".cb-main" ? main : null; },
  };

  await api.onCardAction(split, "current-session");

  assert.deepEqual(request, {
    path: "api/health/action",
    body: {
      kind: "diagnose-health",
      target: "current-session",
      source: { id: "github:github.com:microsoft/aspire" },
    },
  });
  assert.equal(classes.has("done"), true);
  assert.match(main.innerHTML, /Running in this session/);
});

test("Azure pipeline settings add and remove normalized sources without persisting credentials", async () => {
  const calls = [];
  const added = {
    id: "azdo:dnceng:internal:1602",
    name: "microsoft-aspire",
    url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602",
    branch: "refs/heads/release/13.1",
    definitionId: 1602,
  };
  const { app, api } = createRendererHarness({
    fetch: async (path, options) => {
      const endpoint = String(path);
      if (!endpoint.startsWith("api/health/pipeline/")) return new Promise(() => {});
      calls.push({ endpoint, body: JSON.parse(options.body) });
      const pipelines = endpoint.endsWith("/add") ? [added] : [];
      return jsonResponse({
        dashboard: { ...healthDashboard([], emptyHealthCounts(), true), seq: calls.length + 1 },
        prefs: { ...rendererPrefs(), azurePipelines: pipelines },
      });
    },
  });
  api.setState({ ...healthDashboard([], emptyHealthCounts(), true), seq: 1 });
  api.setPrefs(rendererPrefs());
  api.setView("settings");
  api.setPipelineDrafts(added.url, "release/13.1");

  await api.addAzurePipeline();

  assert.deepEqual(calls[0], {
    endpoint: "api/health/pipeline/add",
    body: { url: added.url, branch: "release/13.1" },
  });
  assert.equal(api.getPrefs().azurePipelines.length, 1);
  assert.equal(api.getPipelineDrafts().url, "");
  assert.equal(api.getPipelineDrafts().branch, "");
  assert.equal(api.getPipelineDrafts().error, "");
  assert.match(app.innerHTML, /microsoft-aspire/);
  assert.doesNotMatch(JSON.stringify(api.getPrefs()), /AZURE_DEVOPS_EXT_PAT|token|credential/i);

  await api.removeAzurePipeline(added.id);

  assert.deepEqual(calls[1], { endpoint: "api/health/pipeline/remove", body: { id: added.id } });
  assert.equal(api.getPrefs().azurePipelines.length, 0);
});

test("invalid Azure pipeline settings keep the draft and show the provider validation error", async () => {
  const { app, api } = createRendererHarness({
    fetch: async (path) => String(path) === "api/health/pipeline/add"
      ? jsonResponse({ error: "Only Azure DevOps pipeline or build URLs are supported" }, { ok: false, status: 400 })
      : new Promise(() => {}),
  });
  api.setState({ ...healthDashboard([], emptyHealthCounts(), true), seq: 1 });
  api.setPrefs(rendererPrefs());
  api.setView("settings");
  api.setPipelineDrafts("https://example.com/build/1", "");

  await api.addAzurePipeline();

  assert.equal(api.getPipelineDrafts().url, "https://example.com/build/1");
  assert.equal(api.getPipelineDrafts().branch, "");
  assert.equal(api.getPipelineDrafts().error, "Only Azure DevOps pipeline or build URLs are supported");
  assert.match(app.innerHTML, /Only Azure DevOps pipeline or build URLs are supported/);
  assert.match(app.innerHTML, /value="https:\/\/example\.com\/build\/1"/);
});

test("Azure pipeline mutations preserve unsaved settings fields across renders", async () => {
  const elements = {
    "release-input": { value: "13.4-preview" },
    "s-drafts": { checked: true },
    "n-review": { checked: false },
    "n-ready": { checked: true },
    "n-changes": { checked: false },
    "n-ci": { checked: false },
  };
  const resetToPersistedValues = () => {
    elements["release-input"].value = "13.1";
    elements["s-drafts"].checked = false;
    elements["n-review"].checked = true;
    elements["n-ready"].checked = false;
    elements["n-changes"].checked = true;
    elements["n-ci"].checked = true;
  };
  const app = {
    html: "",
    get innerHTML() { return this.html; },
    set innerHTML(value) {
      this.html = value;
      if (value.includes("<h2>Settings</h2>")) resetToPersistedValues();
    },
    removeAttribute() {},
    classList: classList(),
  };
  const added = {
    id: "azdo:dnceng:internal:1602",
    name: "microsoft-aspire",
    url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602",
    branch: "refs/heads/release/13.1",
    definitionId: 1602,
  };
  const { api } = createRendererHarness({
    app,
    elements,
    fetch: async (path) => String(path) === "api/health/pipeline/add"
      ? jsonResponse({
          dashboard: { ...healthDashboard([], emptyHealthCounts(), true), seq: 2 },
          prefs: { ...rendererPrefs(), azurePipelines: [added] },
        })
      : new Promise(() => {}),
  });
  api.setState({ ...healthDashboard([], emptyHealthCounts(), true), seq: 1 });
  api.setPrefs(rendererPrefs());
  api.setView("settings");
  api.setPipelineDrafts(added.url, "");

  await api.addAzurePipeline();

  assert.equal(elements["release-input"].value, "13.4-preview");
  assert.equal(elements["s-drafts"].checked, true);
  assert.equal(elements["n-review"].checked, false);
  assert.equal(elements["n-ready"].checked, true);
  assert.equal(elements["n-changes"].checked, false);
  assert.equal(elements["n-ci"].checked, false);
});

test("settings Enter shortcut leaves interactive controls to their native actions", () => {
  const source = APP_JS.match(/function isSettingsSaveShortcut\(event\) \{[\s\S]*?\n\}/)?.[0];
  assert.ok(source);
  const isSettingsSaveShortcut = vm.runInNewContext(`(${source})`);

  assert.equal(isSettingsSaveShortcut({ key: "Enter", target: { tagName: "BUTTON", id: "pipeline-add-btn" } }), false);
  assert.equal(isSettingsSaveShortcut({ key: "Enter", target: { tagName: "BUTTON", className: "pipeline-remove" } }), false);
  assert.equal(isSettingsSaveShortcut({ key: "Enter", target: { tagName: "INPUT", id: "release-input" } }), true);
});

function rendererPrefs() {
  return {
    mode: "health",
    release: "13.1",
    showDrafts: false,
    azurePipelines: [],
    notifications: {
      assigned: true,
      reviewRequested: true,
      mention: true,
      changesRequested: true,
      ciFailing: true,
    },
  };
}

const repositoryA = { id: "github.com/octo/alpha", host: "github.com", repository: "Octo/Alpha", accountId: "acct:github.com/octo" };
const repositoryB = { id: "ghe.example.com/team/beta", host: "ghe.example.com", repository: "Team/Beta", accountId: "acct:ghe.example.com/octo" };
function repositoryPayload(repo = repositoryA, seq = 1, overrides = {}) {
  return {
    prefs: { ...rendererPrefs(), repositories: [repositoryA, repositoryB], selectedRepository: repo?.id || "", release: "", teamMembers: [] },
    dashboard: {
      ...healthDashboard([], emptyHealthCounts(), true),
      repositoryId: repo?.id || "", seq, marker: repo?.repository || "empty",
      cacheStatus: "cached", refreshing: true, ...overrides,
    },
  };
}
function seedRepository(api, repo = repositoryA) {
  const data = repositoryPayload(repo);
  api.setPrefs(data.prefs);
  api.setState(data.dashboard);
}
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function formElement(value = "", checked = false) {
  return { value, checked, addEventListener() {}, classList: classList(), setAttribute() {} };
}

test("project picker and repository management remain available before load, unauthenticated, and after failure", () => {
  const { app, api } = createRendererHarness({ fetch: () => new Promise(() => {}) });
  for (const state of [null, { authenticated: false, accounts: [], message: "" }]) {
    api.setState(state);
    api.render();
    assert.match(app.innerHTML, /id="repository-picker"/);
    assert.match(app.innerHTML, /id="repositories-btn"/);
    assert.match(app.innerHTML, /No repository selected/);
  }
  api.setState(null);
  api.setLoadError("Offline");
  api.render();
  assert.match(app.innerHTML, /Could not load/);
  assert.match(app.innerHTML, /id="repositories-btn"/);
  api.goView("repositories");
  assert.match(app.innerHTML, /<h2>Repositories/);
  assert.match(app.innerHTML, /Manage accounts/);
});

test("accounts no longer edit repositories, and the repositories page preserves canonical host identity", () => {
  const { app, api } = createRendererHarness();
  seedRepository(api);
  api.setView("accounts");
  api.render();
  assert.doesNotMatch(app.innerHTML, /repo-add-input|data-addinput/);
  assert.doesNotMatch(APP_JS, /api\/account\/repos/);
  api.setView("repositories");
  api.render();
  assert.match(app.innerHTML, /value="ghe.example.com\/team\/beta"/);
  assert.match(app.innerHTML, /Team\/Beta \(ghe.example.com\)/);
  assert.match(app.innerHTML, /acct:ghe.example.com\/octo/);
  assert.match(app.innerHTML, /data-repository-remove="github.com\/octo\/alpha"/);
  assert.match(app.innerHTML, /<select id="repository-account">/);
  assert.doesNotMatch(app.innerHTML, /<input[^>]*(?:id|name)="[^"]*host/i);
});

test("empty selection cannot clear the picker or contradict an existing repository dashboard", async () => {
  const requests = [];
  const handlers = {};
  const picker = {
    ...formElement(repositoryA.id),
    addEventListener(event, handler) { handlers[event] = handler; },
  };
  const { app, api } = createRendererHarness({
    elements: { "repository-picker": picker },
    fetch: path => { requests.push(path); return new Promise(() => {}); },
  });
  seedRepository(api);
  api.render();
  assert.match(app.innerHTML, /<option value="" disabled>No repository selected<\/option>/);
  const dashboard = api.getState();
  const preferences = api.getPrefs();
  picker.value = "";
  await handlers.change();
  assert.equal(picker.value, repositoryA.id);
  assert.equal(api.getState(), dashboard);
  assert.equal(api.getPrefs(), preferences);
  assert.match(app.innerHTML, /value="github.com\/octo\/alpha" selected/);
  assert.match(app.innerHTML, /Choose a repository from the list/);
  assert.ok(!requests.includes("api/repositories/select"));
});

test("empty startup keeps its placeholder selected without posting an empty selection", async () => {
  const requests = [];
  const { app, api } = createRendererHarness({
    fetch: path => { requests.push(path); return new Promise(() => {}); },
  });
  seedRepository(api, null);
  api.render();
  assert.match(app.innerHTML, /<option value="" selected>No repository selected<\/option>/);
  await api.selectRepository("");
  assert.equal(api.getPrefs().selectedRepository, "");
  assert.equal(api.getLoadError(), null);
  assert.ok(!requests.includes("api/repositories/select"));
});

test("repository switching clears old content immediately, applies cache, then accepts only matching SSE", async () => {
  const request = deferred();
  const { app, api } = createRendererHarness({
    fetch: (path) => path === "api/repositories/select" ? request.promise : new Promise(() => {}),
  });
  seedRepository(api);
  const selecting = api.selectRepository(repositoryB.id);
  assert.equal(api.getState().repositoryId, repositoryB.id);
  assert.equal(api.getState().health, undefined);
  assert.match(app.innerHTML, /Loading repository/);
  api.applyPushedState(repositoryPayload(repositoryA, 999));
  assert.equal(api.getState().repositoryId, repositoryB.id);
  request.resolve(jsonResponse(repositoryPayload(repositoryB, 2)));
  await selecting;
  assert.equal(api.getState().marker, repositoryB.repository);
  assert.match(app.innerHTML, /Cached data/);
  api.applyPushedState(repositoryPayload(repositoryB, 3, { cacheStatus: "live", refreshing: false }));
  assert.match(app.innerHTML, /Live data/);
  assert.doesNotMatch(app.innerHTML, /Refreshing\.\.\./);
});

test("A then B selection serializes server writes and ignores late A responses and errors", async () => {
  for (const failA of [false, true]) {
    const first = deferred(), second = deferred();
    const calls = [];
    const { api } = createRendererHarness({
      fetch: (path, options) => {
        if (path !== "api/repositories/select") return new Promise(() => {});
        calls.push(JSON.parse(options.body).id);
        return calls.length === 1 ? first.promise : second.promise;
      },
    });
    seedRepository(api);
    const a = api.selectRepository(repositoryA.id);
    await Promise.resolve();
    const b = api.selectRepository(repositoryB.id);
    assert.deepEqual(calls, [repositoryA.id]);
    if (failA) first.reject(new Error("Old A failure"));
    else first.resolve(jsonResponse(repositoryPayload(repositoryA, 800)));
    await a;
    await Promise.resolve();
    assert.deepEqual(calls, [repositoryA.id, repositoryB.id]);
    assert.equal(api.getState().repositoryId, repositoryB.id);
    assert.equal(api.getLoadError(), null);
    second.resolve(jsonResponse(repositoryPayload(repositoryB, 2)));
    await b;
    assert.equal(api.getPrefs().selectedRepository, repositoryB.id);
    assert.equal(api.getState().seq, 2);
  }
});

test("old loads, refresh responses, and pending form snapshots cannot overwrite a new repository", async () => {
  const oldLoad = deferred(), oldRefresh = deferred();
  const { api } = createRendererHarness({
    fetch: (path) => path === "api/repositories/select"
      ? Promise.resolve(jsonResponse(repositoryPayload(repositoryB, 2))) : oldLoad.promise,
  });
  seedRepository(api);
  const loading = api.load();
  const refreshing = api.withRefresh(() => oldRefresh.promise);
  api.setView("settings");
  api.applyPushedState(repositoryPayload(repositoryA, 999));
  await api.selectRepository(repositoryB.id);
  oldLoad.resolve(jsonResponse(repositoryPayload(repositoryA, 1000)));
  oldRefresh.resolve(repositoryPayload(repositoryA, 1001));
  await Promise.all([loading, refreshing]);
  api.goView("queue");
  assert.equal(api.getState().repositoryId, repositoryB.id);
  assert.equal(api.getState().seq, 2);
  api.onPreferences(repositoryPayload(repositoryA).prefs);
  api.onSnapshot({ seq: 2000, repositoryId: repositoryA.id, prefs: repositoryPayload(repositoryA).prefs });
  api.onUpdateAvailable({ seq: 2000, repositoryId: repositoryA.id });
  assert.equal(api.getPrefs().selectedRepository, repositoryB.id);
  assert.equal(api.getUpdateAvailable(), null);
});

test("pending SSE snapshots retain the newest matching revision while editing", () => {
  const { api } = createRendererHarness();
  seedRepository(api);
  api.setView("repositories");
  api.applyPushedState(repositoryPayload(repositoryA, 5));
  api.applyPushedState(repositoryPayload(repositoryA, 3));
  api.applyPushedState(repositoryPayload(repositoryB, 100));
  api.goView("queue");
  assert.equal(api.getState().seq, 5);
  assert.equal(api.getState().repositoryId, repositoryA.id);
});

test("failed selection surfaces an error without restoring the previous repository's dashboard", async () => {
  const { app, api } = createRendererHarness({
    fetch: (path) => path === "api/repositories/select"
      ? Promise.resolve(jsonResponse({ error: "Access denied" }, { ok: false, status: 403 })) : new Promise(() => {}),
  });
  seedRepository(api);
  await api.selectRepository(repositoryB.id);
  assert.match(app.innerHTML, /Could not select repository: Access denied/);
  assert.equal(api.getState().repositoryId, repositoryB.id);
  assert.equal(api.getState().refreshing, false);
  assert.equal(api.getState().marker, undefined);
});

test("cached content remains visible alongside freshness and refresh errors", () => {
  const { app, api } = createRendererHarness();
  const data = repositoryPayload(repositoryA, 1, {
    refreshError: "GitHub rate limit <exceeded>",
    health: { items: [{ id: "health-a", repository: repositoryA.repository, name: "Retained result", state: "healthy", provider: "github" }], counts: {} },
  });
  api.setState(data.dashboard);
  api.setPrefs(data.prefs);
  api.render();
  assert.match(app.innerHTML, /Retained result/);
  assert.match(app.innerHTML, /Cached data.*Updated.*Refreshing/);
  assert.match(app.innerHTML, /Refresh failed: GitHub rate limit &lt;exceeded&gt;/);
});

test("empty startup reports no repository selected instead of loading, including unauthenticated views", () => {
  const { app, api } = createRendererHarness();
  for (const authenticated of [false, true]) {
    for (const cacheStatus of ["empty", ""]) {
      for (const mode of ["review", "health"]) {
        const data = repositoryPayload(null, 1, {
          authenticated, mode, cacheStatus, loading: false, refreshing: false, fetchedAt: null,
        });
        api.setState(data.dashboard);
        api.setPrefs(data.prefs);
        api.render();
        assert.match(app.innerHTML, /class="freshness" role="status">No repository selected<\/div>/);
        assert.doesNotMatch(app.innerHTML, /Loading data|Loading repository|Refreshing\.\.\.|Updated null/);
        assert.match(app.innerHTML, /id="repositories-btn"/);
      }
    }
  }
});

test("selected repository with no cache and no active request does not claim to be loading", () => {
  const { app, api } = createRendererHarness();
  const data = repositoryPayload(repositoryA, 1, {
    cacheStatus: "empty", loading: false, refreshing: false, fetchedAt: null,
  });
  api.setState(data.dashboard);
  api.setPrefs(data.prefs);
  api.render();
  assert.match(app.innerHTML, /class="freshness" role="status">No data available<\/div>/);
  assert.doesNotMatch(app.innerHTML, /Loading data/);
});

test("search debounce cancels superseded queries and ignores late results after account or view changes", async () => {
  const timers = new Map();
  let timerId = 0;
  const requests = [];
  const { api } = createRendererHarness({
    setTimeout: (callback) => { timers.set(++timerId, callback); return timerId; },
    clearTimeout: id => timers.delete(id),
    fetch: (path, options) => {
      if (!path.startsWith("api/repositories/search")) return new Promise(() => {});
      const response = deferred();
      requests.push({ path, options, response });
      return response.promise;
    },
  });
  seedRepository(api);
  api.setView("repositories");
  api.scheduleRepositorySearch(repositoryA.accountId, "old");
  api.scheduleRepositorySearch(repositoryA.accountId, "new & query");
  assert.equal(timers.size, 1);
  const searchingA = [...timers.values()][0]();
  assert.match(requests[0].path, /q=new%20%26%20query/);
  assert.deepEqual(Object.fromEntries(new URL(requests[0].path, "http://localhost/").searchParams),
    { accountId: repositoryA.accountId, q: "new & query" });
  api.scheduleRepositorySearch(repositoryB.accountId, "beta");
  assert.equal(requests[0].options.signal.aborted, true);
  const searchingB = [...timers.values()].at(-1)();
  assert.deepEqual(Object.fromEntries(new URL(requests[1].path, "http://localhost/").searchParams),
    { accountId: repositoryB.accountId, q: "beta" });
  requests[1].response.resolve(jsonResponse({ items: [{ repository: "Team/Beta", description: "<script>", private: true }] }));
  await searchingB;
  requests[0].response.resolve(jsonResponse({ items: [{ repository: "Old/Result" }] }));
  await searchingA;
  assert.match(api.repositoriesView(), /Team\/Beta/);
  assert.match(api.repositoriesView(), /&lt;script&gt;/);
  assert.doesNotMatch(api.repositoriesView(), /Old\/Result/);
  api.scheduleRepositorySearch(repositoryB.accountId, "leaving");
  const leaving = [...timers.values()].at(-1)();
  api.goView("accounts");
  requests[2].response.resolve(jsonResponse({ items: [{ repository: "Late/Result" }] }));
  await leaving;
  assert.doesNotMatch(api.repositoriesView(), /Late\/Result/);
});

test("search errors and empty results are explicit and do not remove saved repositories", async () => {
  for (const response of [{ items: [], error: "Search unavailable" }, { items: [] }]) {
    const { api } = createRendererHarness({
      fetch: path => path.startsWith("api/repositories/search")
        ? Promise.resolve(jsonResponse(response)) : new Promise(() => {}),
    });
    seedRepository(api);
    api.scheduleRepositorySearch(repositoryA.accountId, "missing");
    await new Promise(resolve => setImmediate(resolve));
    assert.match(api.repositoriesView(), response.error ? /Search unavailable/ : /No matching repositories/);
    assert.equal(api.getPrefs().repositories.length, 2);
  }
});

test("repository add/remove use their dedicated API contracts and preserve state on failure", async () => {
  const calls = [];
  let fail = true;
  const { api } = createRendererHarness({
    setTimeout: () => 1,
    fetch: (path, options) => {
      if (!path.startsWith("api/repositories/")) return new Promise(() => {});
      calls.push({ path, body: JSON.parse(options.body) });
      return Promise.resolve(fail
        ? jsonResponse({ error: "Repository not accessible" }, { ok: false, status: 400 })
        : jsonResponse(repositoryPayload(null, 5)));
    },
  });
  seedRepository(api);
  api.setView("repositories");
  api.scheduleRepositorySearch(repositoryA.accountId, "Octo/New");
  await api.mutateRepository("add", "Octo/New");
  assert.deepEqual(calls[0], { path: "api/repositories/add", body: { accountId: repositoryA.accountId, repository: "Octo/New" } });
  assert.match(api.repositoriesView(), /Repository not accessible/);
  assert.equal(api.getPrefs().selectedRepository, repositoryA.id);
  fail = false;
  await api.mutateRepository("remove", repositoryA.id);
  assert.deepEqual(calls[1], { path: "api/repositories/remove", body: { id: repositoryA.id } });
  assert.equal(api.getPrefs().selectedRepository, "");
  assert.equal(api.getState().repositoryId, "");
});

test("settings send an empty release and configurable team text and preserve both across auxiliary forms", async () => {
  const elements = {
    "release-input": formElement(""),
    "team-members-input": formElement("octo\nhubot"),
    "s-drafts": formElement("", true),
    "n-review": formElement("", true), "n-ready": formElement(), "n-changes": formElement(), "n-ci": formElement(),
  };
  let body;
  const { api } = createRendererHarness({
    elements,
    fetch: (path, options) => {
      if (path !== "api/prefs") return new Promise(() => {});
      body = JSON.parse(options.body);
      return Promise.resolve(jsonResponse(repositoryPayload(repositoryA, 5)));
    },
  });
  seedRepository(api);
  const draft = api.captureSettingsDraft();
  elements["team-members-input"].value = "discarded";
  api.restoreSettingsDraft(draft);
  assert.equal(elements["team-members-input"].value, "octo\nhubot");
  assert.match(api.settingsView(), /Leave empty for no release filter/);
  assert.doesNotMatch(api.settingsView(), /13\.5|microsoft\/aspire/);
  await api.saveSettings();
  assert.equal(body.release, "");
  assert.equal(body.teamMembers, "octo\nhubot");
  assert.equal(body.showDrafts, true);
});

test("standalone doctor and session configuration retain their endpoints and generic defaults", async () => {
  const elements = {
    "release-input": formElement(""), "team-members-input": formElement("octo"),
    "s-drafts": formElement(), "n-review": formElement(), "n-ready": formElement(), "n-changes": formElement(), "n-ci": formElement(),
    "session-project": formElement(""), "session-project-name": formElement("Alpha"),
    "session-project-url": formElement("https://github.com/Octo/Alpha"),
  };
  const calls = [];
  const { app, api } = createRendererHarness({
    standalone: true, elements,
    fetch: (path, options) => {
      calls.push({ path, body: options?.body ? JSON.parse(options.body) : null });
      if (path === "api/doctor") return Promise.resolve(jsonResponse({ checks: [{ status: "ok", label: "GitHub CLI", message: "Available" }] }));
      if (path === "api/session/configuration") return Promise.resolve(jsonResponse({ ok: true }));
      return Promise.resolve(jsonResponse(repositoryPayload(repositoryA, 2)));
    },
  });
  seedRepository(api);
  api.setView("settings");
  await api.runDoctor();
  assert.match(app.innerHTML, /GitHub CLI/);
  assert.equal(elements["team-members-input"].value, "octo");
  await api.saveSessionProject("add");
  assert.deepEqual(calls.find(c => c.path === "api/session/configuration").body, {
    projects: [{ name: "Alpha", repositoryUrl: "https://github.com/Octo/Alpha" }], selectedRepositoryUrl: "",
  });
});

test("credential discovery runs on first management visit even when the cached dashboard has no accounts", async () => {
  const calls = [];
  const { app, api } = createRendererHarness({
    fetch: path => {
      calls.push(path);
      if (path === "api/accounts") return Promise.resolve(jsonResponse({ accounts: [
        { id: repositoryA.accountId, login: "octo", host: "github.com", status: "ok" },
      ] }));
      return new Promise(() => {});
    },
  });
  await api.withRefresh(async () => repositoryPayload(repositoryA));
  api.goView("repositories");
  await new Promise(resolve => setImmediate(resolve));
  assert.ok(calls.includes("api/accounts"));
  assert.match(app.innerHTML, /octo \(github.com\)/);
  assert.doesNotMatch(app.innerHTML, /undefined\/undefined/);
});

test("out-of-order credential scans only publish the newest metadata", async () => {
  const scans = [];
  const { api } = createRendererHarness({
    fetch: path => {
      if (path !== "api/accounts") return new Promise(() => {});
      const scan = deferred();
      scans.push(scan);
      return scan.promise;
    },
  });
  seedRepository(api);
  const first = api.rescanAccounts();
  const second = api.rescanAccounts();
  scans[1].resolve(jsonResponse({ accounts: [{ id: repositoryB.accountId, login: "new" }] }));
  await second;
  scans[0].resolve(jsonResponse({ accounts: [{ id: repositoryA.accountId, login: "old" }] }));
  await first;
  assert.equal(api.currentAccounts()[0].login, "new");
  assert.equal(api.getState().repositoryId, repositoryA.id);
});

test("adding the first repository adopts the server selection and duplicate clicks send one request", async () => {
  const addition = deferred();
  let additions = 0;
  const { api } = createRendererHarness({
    setTimeout: () => 1,
    fetch: path => {
      if (path !== "api/repositories/add") return new Promise(() => {});
      additions++;
      return addition.promise;
    },
  });
  seedRepository(api, null);
  api.setView("repositories");
  api.scheduleRepositorySearch(repositoryA.accountId, repositoryA.repository);
  const first = api.mutateRepository("add", repositoryA.repository);
  await api.mutateRepository("add", repositoryA.repository);
  assert.equal(additions, 1);
  addition.resolve(jsonResponse(repositoryPayload(repositoryA)));
  await first;
  assert.equal(api.getPrefs().selectedRepository, repositoryA.id);
  assert.equal(api.getState().repositoryId, repositoryA.id);
});

test("a late Apply update response is ignored after repository selection", async () => {
  const update = deferred();
  const { api } = createRendererHarness({
    fetch: path => path === "api/repositories/select"
      ? Promise.resolve(jsonResponse(repositoryPayload(repositoryB, 2))) : update.promise,
  });
  seedRepository(api);
  api.onUpdateAvailable({ repositoryId: repositoryA.id, seq: 10 });
  const applying = api.applyAvailableUpdate();
  await api.selectRepository(repositoryB.id);
  update.resolve(jsonResponse(repositoryPayload(repositoryA, 10)));
  await applying;
  assert.equal(api.getState().repositoryId, repositoryB.id);
  assert.equal(api.getPrefs().selectedRepository, repositoryB.id);
});

test("all dashboard modes preserve the same selected repository and header management", () => {
  const { app, api } = createRendererHarness();
  for (const mode of ["review", "issues", "ship", "health"]) {
    const data = repositoryPayload(repositoryB, 1, { mode });
    api.setState(data.dashboard);
    api.setPrefs(data.prefs);
    api.render();
    assert.match(app.innerHTML, /value="ghe.example.com\/team\/beta" selected/);
    assert.match(app.innerHTML, /id="repositories-btn"/);
    assert.equal(api.getState().repositoryId, repositoryB.id);
  }
});

test("pipeline settings list only the selected repository's configured sources", () => {
  const { api } = createRendererHarness();
  const pipelines = [
    { id: "pipeline-a", name: "Alpha delivery", repositoryId: repositoryA.id, url: "https://dev.azure.com/org/a/_build?definitionId=1" },
    { id: "pipeline-b", name: "Beta delivery", repositoryId: repositoryB.id, url: "https://dev.azure.com/org/b/_build?definitionId=2" },
    { id: "unassigned", name: "Unassigned delivery", url: "https://dev.azure.com/org/c/_build?definitionId=3" },
  ];
  for (const [repo, shown, hidden] of [
    [repositoryA, "Alpha delivery", "Beta delivery"],
    [repositoryB, "Beta delivery", "Alpha delivery"],
  ]) {
    api.setPrefs({ ...repositoryPayload(repo).prefs, azurePipelines: pipelines });
    const html = api.pipelineEditorHtml();
    assert.ok(html.includes(shown));
    assert.ok(!html.includes(hidden));
    assert.doesNotMatch(html, /Unassigned delivery|auto-discovered|CLI default project|additional definitions/);
    assert.match(html, /Pipelines are assigned to the selected repository/);
  }
  api.setPrefs({ ...repositoryPayload(null).prefs, azurePipelines: pipelines });
  const empty = api.pipelineEditorHtml();
  assert.match(empty, /No pipelines configured for this repository/);
  assert.match(empty, /id="pipeline-add-btn"[^>]*disabled/);
  assert.doesNotMatch(empty, /Alpha delivery|Beta delivery|Unassigned delivery/);
});

test("pipeline mutations without a repository show a validation error instead of sending a global change", async () => {
  const requests = [];
  const { api } = createRendererHarness({
    fetch: path => { requests.push(path); return new Promise(() => {}); },
  });
  seedRepository(api, null);
  api.setView("settings");
  api.setPipelineDrafts("https://dev.azure.com/org/project/_build?definitionId=1", "");
  await api.addAzurePipeline();
  assert.equal(requests.filter(path => path.startsWith("api/health/pipeline/")).length, 0);
  assert.equal(api.getPipelineDrafts().error, "Select a repository before configuring pipelines.");
});

function emptyHealthCounts() {
  return { total: 0, healthy: 0, running: 0, degraded: 0, failing: 0, unavailable: 0, unknown: 0 };
}

function healthDashboard(items, counts, authenticated) {
  return {
    authenticated,
    viewer: authenticated ? "octo" : null,
    mode: "health",
    accounts: [],
    activeAccounts: [],
    notifications: [],
    repos: [],
    lanes: [],
    health: { items, counts },
    counts,
    errors: [],
    fetchedAt: "2026-01-08T02:00:00Z",
  };
}

function createRendererHarness(overrides = {}) {
  const app = overrides.app ?? {
    innerHTML: "",
    removeAttribute() {},
    classList: classList(),
  };
  const document = {
    getElementById(id) {
      if (id === "app") return app;
      if (id === "loadbar") return overrides.loadbar ?? null;
      return overrides.elements?.[id] ?? null;
    },
    querySelector: overrides.querySelector ?? (() => null),
    querySelectorAll: overrides.querySelectorAll ?? (() => []),
    createElement: overrides.createElement ?? (() => ({})),
    addEventListener() {},
  };
  const sandbox = {
    document,
    window: { CSS: { escape: cssEscape }, githubTeamStandalone: !!overrides.standalone },
    crypto: { randomUUID: () => "b3a61b14-b22c-426e-9a4b-495606e2bc3a" },
    CSS: { escape: cssEscape },
    EventSource: function () { throw new Error("disabled"); },
    ResizeObserver: undefined,
    requestAnimationFrame(handler) { handler(); },
    fetch: overrides.fetch ?? (async () => jsonResponse({ dashboard: null, prefs: null })),
    setTimeout: overrides.setTimeout ?? ((handler) => { handler(); return 1; }),
    clearTimeout: overrides.clearTimeout ?? (() => {}),
    URL,
    AbortController,
    setInterval: overrides.setInterval ?? (() => 1),
    clearInterval: overrides.clearInterval ?? (() => {}),
    console,
  };

  vm.runInNewContext(`${APP_JS}\n;globalThis.__test = {\n  render,\n  withRefresh,\n  load,\n  rescanAccounts,\n  onCardAction,\n  applyPushedState,\n  onUpdateAvailable,\n  onPreferences,\n  onSnapshot,\n  onPollSchedule,\n  applyAvailableUpdate,\n  toggleAutoApply,\n  autoApplyEnabled,\n  openLinkedPr,\n  selectRepository,\n  mutateRepository,\n  scheduleRepositorySearch,\n  searchRepositories,\n  repositoriesView,\n  currentAccounts,\n  goView,\n  saveSettings,\n  runDoctor,\n  saveSessionProject,\n  settingsView,\n  captureSettingsDraft,\n  restoreSettingsDraft,\n  forYouCardActions,\n  focusCardActions,\n  laneCardActions,\n  signalActions,\n  mergeActions,\n  queuePanel,\n  cardActionBtn,\n  issueCard,\n  healthCard,\n  healthView,\n  healthRepositoryGroups,\n  pipelineEditorHtml,\n  addAzurePipeline,\n  removeAzurePipeline,\n  commitHealthOrder,\n  moveHealthSource,\n  dropHealthSource,\n  setHealthDropMarker,\n  wireHealthOrdering,\n  actionKey,\n  inflightActions,\n  setProgress,\n  setState(value) { state = value; },\n  getState() { return state; },\n  getAppliedSeq() { return lastAppliedSeq; },\n  getUpdateAvailable() { return updateAvailable; },\n  setPrefs(value) { prefs = value; },\n  getPrefs() { return prefs; },\n  setHealthOrderSaving(value) { healthOrderSaving = !!value; },\n  setPipelineDrafts(url, branch) { pipelineUrlDraft = url; pipelineBranchDraft = branch; },\n  getPipelineDrafts() { return { url: pipelineUrlDraft, branch: pipelineBranchDraft, error: pipelineError }; },\n  setView(value) { view = value; },\n  setRefreshing(value) { refreshing = !!value; },\n  setRefreshInFlight(value) { refreshInFlight = value; },\n  setLoadError(value) { loadError = value; },\n  getLoadError() { return loadError; },\n};`, sandbox);

  return { app, api: sandbox.__test };
}

function dragCard() {
  const classes = new Set();
  const card = {
    dataset: {},
    listeners: {},
    mutations: 0,
    addEventListener(event, handler) { this.listeners[event] = handler; },
    getBoundingClientRect() { return { left: 0, top: 0, width: 100, height: 100 }; },
    classList: {
      add(...values) {
        for (const value of values) classes.add(value);
        this.owner.mutations++;
      },
      remove(...values) {
        for (const value of values) classes.delete(value);
        this.owner.mutations++;
      },
      contains(value) { return classes.has(value); },
      has(value) { return classes.has(value); },
      owner: null,
    },
  };
  card.classList.owner = card;
  return card;
}

function dragHandle(id, card) {
  return {
    dataset: { healthDrag: id },
    listeners: {},
    addEventListener(event, handler) { this.listeners[event] = handler; },
    setAttribute() {},
    removeAttribute() {},
    closest() { return card; },
  };
}

test("cb-menu keyboard model lets Tab traverse out of the menu instead of trapping focus", () => {
  // Escape still cancels the default and returns focus to the caret (menu-button pattern).
  assert.match(APP_JS, /e\.key === "Escape"\)\s*\{\s*e\.preventDefault\(\);\s*closeCbMenus\(\);\s*caret\.focus\(\);/);
  // Tab has its own branch that closes the menu and re-anchors on the caret, but must NOT call
  // preventDefault so the browser's native Tab moves focus to the next element rather than
  // trapping the keyboard user inside the portaled menu.
  const tabBranch = APP_JS.match(/e\.key === "Tab"\)\s*\{([^}]*)\}/);
  assert.ok(tabBranch, "expected a dedicated Tab keydown branch");
  assert.match(tabBranch[1], /closeCbMenus\(\)/);
  assert.doesNotMatch(tabBranch[1], /preventDefault/);
  // The old combined branch that trapped Tab alongside Escape is gone.
  assert.doesNotMatch(APP_JS, /"Escape" \|\| e\.key === "Tab"/);
});

function jsonResponse(body, options = {}) {
  return {
    ok: options.ok ?? true,
    status: options.status ?? 200,
    statusText: options.statusText ?? "OK",
    json: async () => body,
  };
}

function errorElement() {
  const classes = new Set();
  return {
    textContent: "",
    classList: {
      add(name) { classes.add(name); },
      remove(name) { classes.delete(name); },
      has(name) { return classes.has(name); },
    },
  };
}

function classList() {
  const classes = new Set();
  return {
    add(name) { classes.add(name); },
    remove(name) { classes.delete(name); },
    toggle(name, on) { if (on) classes.add(name); else classes.delete(name); },
    contains(name) { return classes.has(name); },
  };
}

function cssEscape(value) {
  return String(value).replace(/[^a-zA-Z0-9_-]/g, (ch) => `\\${ch}`);
}

test("standalone cards expose only a new-session action", () => {
  const { api } = createRendererHarness({ standalone: true, fetch: () => new Promise(() => {}) });
  const html = api.cardActionBtn(
    { repository: "microsoft/aspire", number: 123, url: "https://github.com/microsoft/aspire/pull/123" },
    { kind: "review", label: "Review" });
  assert.deepEqual([...html.matchAll(/data-target="([^"]+)"/g)].map(match => match[1]), ["new-session"]);
  assert.equal(api.cardActionBtn(
    { repository: "owner/repo", number: 123, url: "https://enterprise.example/owner/repo/pull/123" },
    { kind: "review", label: "Review" }),
    '<span class="hint">GitHub App session links require github.com.</span>');
});

test("standalone requests identify their browser tab", async () => {
  const requests = [];
  const { api } = createRendererHarness({
    standalone: true,
    fetch: async (path, options) => {
      requests.push({ path, options });
      return jsonResponse({ dashboard: healthDashboard([], emptyHealthCounts(), false), prefs: rendererPrefs() });
    },
  });
  await api.load();
  assert.ok(requests.length > 0);
  assert.deepEqual(requests.map(request => request.options.headers["X-Team-App-Client"]),
    requests.map(() => "b3a61b14-b22c-426e-9a4b-495606e2bc3a"));
});

test("standalone actions prepare an explicit local app link without launching a session", async () => {
    const appUrl = "ghapp://session/new?repo=microsoft%2Faspire&pr=123&mode=interactive";
    let replacement;
    const button = {
      classList: classList(),
      innerHTML: "Review",
      replaceWith(link) { replacement = link; },
    };
    const split = {
      isConnected: true,
      dataset: { kind: "review", prRepo: "microsoft/aspire", prNumber: "123", prUrl: "https://github.com/microsoft/aspire/pull/123" },
      querySelector(selector) { return selector === ".cb-main" ? button : null; },
    };
    const { api } = createRendererHarness({
      standalone: true,
      fetch: path => path === "api/agent/action"
        ? Promise.resolve(jsonResponse({ appUrl, target: "new-session", confirmationRequired: true }))
        : new Promise(() => {}),
    });
    await api.onCardAction(split, "new-session");
    assert.equal(replacement.href, appUrl);
    assert.equal(replacement.textContent, "Open in GitHub App");
    assert.equal(replacement.title, "Review and confirm the new session in GitHub App");
    assert.equal(api.inflightActions.size, 0);
});
