(() => {
  const param = new URLSearchParams(window.location.search).get("clawpilotTheme");
  const media = window.matchMedia("(prefers-color-scheme: dark)");
  const valid = value => ["system", "light", "dark"].includes(value);
  // The server supplies the durable preference before this script, even on a new port.
  let preference = valid(window.githubTeamAppearance) ? window.githubTeamAppearance
    : param === "light" || param === "dark" ? param : "system";
  let nativeSystemTheme = window.githubTeamSystemTheme;
  let effectiveTheme;
  let lastMessage;

  function apply() {
    const systemTheme = nativeSystemTheme === "light" || nativeSystemTheme === "dark"
      ? nativeSystemTheme : media.matches ? "dark" : "light";
    const effective = preference === "system" ? systemTheme : preference;
    effectiveTheme = effective;
    document.documentElement.setAttribute("data-theme", effective);
    const message = "appearance:" + preference + ":" + effective;
    if (message !== lastMessage) {
      lastMessage = message;
      if (window.webkit?.messageHandlers?.appearance) window.webkit.messageHandlers.appearance.postMessage(message);
      else if (window.chrome?.webview) window.chrome.webview.postMessage(message);
      window.dispatchEvent(new Event("appearancechange"));
    }
  }

  window.githubTeamTheme = {
    get preference() { return preference; },
    get effectiveTheme() { return effectiveTheme; },
    setPreference(value) {
      if (!valid(value)) throw new Error("Appearance must be system, light, or dark.");
      preference = value;
      apply();
    },
    // WKWebView's media query follows window appearance; the shell reports the OS separately.
    setSystemTheme(value) {
      if (value !== "light" && value !== "dark") throw new Error("Invalid system appearance.");
      nativeSystemTheme = value;
      if (preference === "system") apply();
    },
  };
  media.addEventListener("change", () => { if (preference === "system") apply(); });
  apply();
})();
