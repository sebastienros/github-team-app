// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import Cocoa
import WebKit

func loopbackURL(_ text: String) -> URL? {
    guard let url = URL(string: text), url.scheme == "http", url.host == "127.0.0.1",
          let port = url.port, (1...65535).contains(port),
          url.user == nil, url.password == nil, url.query == nil, url.fragment == nil,
          url.path == "" || url.path == "/" else { return nil }
    return url
}

func sameOrigin(_ url: URL, _ root: URL) -> Bool {
    url.scheme == root.scheme && url.host == root.host && url.port == root.port &&
        url.user == nil && url.password == nil
}

func externalURL(_ url: URL) -> Bool {
    guard url.user == nil && url.password == nil else { return false }
    if url.scheme == "https" { return url.host != nil }
    return url.scheme == "ghapp" && url.host == "session" && url.path == "/new"
}

func appearanceMessage(_ text: String) -> (preference: String, dark: Bool)? {
    let parts = text.split(separator: ":").map(String.init)
    guard parts.count == 3, parts[0] == "appearance",
          ["system", "light", "dark"].contains(parts[1]),
          ["light", "dark"].contains(parts[2]),
          parts[1] == "system" || parts[1] == parts[2] else { return nil }
    return (parts[1], parts[2] == "dark")
}

func writeIcons(from source: String, to directory: String) throws {
    guard let image = NSImage(contentsOfFile: source),
          let original = image.cgImage(forProposedRect: nil, context: nil, hints: nil),
          original.width == 1024, original.height == 1024 else {
        throw NSError(domain: "GitHubTeamApp", code: 1,
                      userInfo: [NSLocalizedDescriptionKey: "The approved 1024 x 1024 Octo PNG is missing or invalid."])
    }
    for size in [16, 32, 128, 256, 512] {
        for scale in [1, 2] {
            let pixels = size * scale
            guard let context = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8,
                                          bytesPerRow: pixels * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
                throw NSError(domain: "GitHubTeamApp", code: 1,
                              userInfo: [NSLocalizedDescriptionKey: "Could not render the application icon."])
            }
            context.interpolationQuality = .high
            context.draw(original, in: CGRect(x: 0, y: 0, width: pixels, height: pixels))
            guard let resized = context.makeImage(),
                  let png = NSBitmapImageRep(cgImage: resized).representation(using: .png, properties: [:]) else {
                throw NSError(domain: "GitHubTeamApp", code: 1,
                              userInfo: [NSLocalizedDescriptionKey: "Could not encode the application icon."])
            }
            let suffix = scale == 2 ? "@2x" : ""
            try png.write(to: URL(fileURLWithPath: directory).appendingPathComponent("icon_\(size)x\(size)\(suffix).png"))
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, WKNavigationDelegate, WKUIDelegate, WKScriptMessageHandler {
    var window: NSWindow!
    var webView: WKWebView!
    var root: URL?
    var backend: Process?
    var output = ""
    var diagnostics = ""
    var quitting = false
    var windowShown = false
    var exitCode: Int32 = 0
    var startupTimer: Timer?
    var parentTimer: Timer?
    var appearanceObservation: NSKeyValueObservation?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        installMenu()
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .default()
        configuration.userContentController.add(self, name: "appearance")
        webView = WKWebView(frame: .zero, configuration: configuration)
        webView.navigationDelegate = self
        webView.uiDelegate = self
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 820),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        window.title = "GitHub Team App"
        window.minSize = NSSize(width: 640, height: 480)
        window.contentView = webView
        applyAppearance(preference: "system", dark: systemIsDark)
        updateSystemAppearance()
        appearanceObservation = NSApp.observe(\.effectiveAppearance, options: [.new]) { [weak self] _, _ in
            DispatchQueue.main.async { self?.updateSystemAppearance() }
        }
        window.setFrameAutosaveName("GitHubTeamApp")
        window.center()
        let arguments = Array(CommandLine.arguments.dropFirst())
        if arguments.count == 4, arguments[0] == "--url", arguments[2] == "--parent-pid",
           let url = loopbackURL(arguments[1]), let pid = Int32(arguments[3]), pid > 1 {
            load(url)
            parentTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { _ in
                if kill(pid, 0) != 0 && errno == ESRCH { NSApp.terminate(nil) }
            }
        } else if arguments.isEmpty || (arguments.count == 2 && arguments[0] == "--data-dir") {
            startBackend(arguments)
        } else {
            fail("Invalid desktop arguments. Launch the application normally, or use dotnet run.")
        }
    }

    var systemIsDark: Bool {
        NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
    }

    func updateSystemAppearance() {
        let theme = systemIsDark ? "dark" : "light"
        let content = webView.configuration.userContentController
        content.removeAllUserScripts()
        content.addUserScript(WKUserScript(source: "window.githubTeamSystemTheme = '\(theme)';",
                                          injectionTime: .atDocumentStart, forMainFrameOnly: true))
        guard let url = webView.url, let root, sameOrigin(url, root) else { return }
        webView.evaluateJavaScript("window.githubTeamTheme?.setSystemTheme('\(theme)');") { _, error in
            if let error { NSLog("Could not synchronize system appearance: %@", error.localizedDescription) }
        }
    }

    func applyAppearance(preference: String, dark: Bool) {
        window.appearance = preference == "system" ? nil : NSAppearance(named: dark ? .darkAqua : .aqua)
        let background = dark
            ? NSColor(srgbRed: 61 / 255.0, green: 59 / 255.0, blue: 58 / 255.0, alpha: 1)
            : NSColor(srgbRed: 247 / 255.0, green: 244 / 255.0, blue: 239 / 255.0, alpha: 1)
        window.backgroundColor = background
        webView.underPageBackgroundColor = background
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.name == "appearance", message.frameInfo.isMainFrame,
              let url = message.frameInfo.request.url, let root, sameOrigin(url, root),
              let text = message.body as? String, let appearance = appearanceMessage(text) else { return }
        applyAppearance(preference: appearance.preference, dark: appearance.dark)
    }

    func installMenu() {
        let main = NSMenu()
        let application = NSMenuItem()
        let menu = NSMenu()
        menu.addItem(withTitle: "About GitHub Team App", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "Hide GitHub Team App", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        menu.addItem(withTitle: "Quit GitHub Team App", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        application.submenu = menu
        main.addItem(application)
        let edit = NSMenuItem(title: "Edit", action: nil, keyEquivalent: "")
        edit.submenu = NSMenu(title: "Edit")
        for (title, selector, key) in [
            ("Undo", "undo:", "z"), ("Cut", "cut:", "x"), ("Copy", "copy:", "c"),
            ("Paste", "paste:", "v"), ("Select All", "selectAll:", "a")
        ] {
            edit.submenu?.addItem(withTitle: title, action: Selector(selector), keyEquivalent: key)
        }
        main.addItem(edit)
        let view = NSMenuItem(title: "View", action: nil, keyEquivalent: "")
        view.submenu = NSMenu(title: "View")
        let reload = NSMenuItem(title: "Reload", action: #selector(reloadPage), keyEquivalent: "r")
        reload.target = self
        view.submenu?.addItem(reload)
        main.addItem(view)
        let windows = NSMenuItem(title: "Window", action: nil, keyEquivalent: "")
        windows.submenu = NSMenu(title: "Window")
        windows.submenu?.addItem(withTitle: "Close Window", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windows.submenu?.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windows.submenu?.addItem(withTitle: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        main.addItem(windows)
        NSApp.windowsMenu = windows.submenu
        NSApp.mainMenu = main
    }

    @objc func reloadPage() { webView.reload() }

    func startBackend(_ arguments: [String]) {
        let executable = Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/github-team")
        guard FileManager.default.isExecutableFile(atPath: executable.path) else {
            fail("This development shell has no bundled server. Use dotnet run, or publish the Native AOT .app.")
            return
        }
        let process = Process()
        process.executableURL = executable
        process.arguments = ["--no-browser"] + arguments
        var environment = ProcessInfo.processInfo.environment
        // Finder's PATH omits common CLI installation directories.
        environment["PATH"] = (environment["PATH"] ?? "/usr/bin:/bin") + ":/opt/homebrew/bin:/usr/local/bin"
        process.environment = environment
        let stdout = Pipe()
        let stderr = Pipe()
        process.standardOutput = stdout
        process.standardError = stderr
        stdout.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if data.isEmpty { handle.readabilityHandler = nil; return }
            let text = String(decoding: data, as: UTF8.self)
            DispatchQueue.main.async { self.receive(text) }
        }
        stderr.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if data.isEmpty { handle.readabilityHandler = nil; return }
            let text = String(decoding: data, as: UTF8.self)
            DispatchQueue.main.async { self.diagnostics = String((self.diagnostics + text).suffix(8192)) }
        }
        process.terminationHandler = { process in
            DispatchQueue.main.async {
                if !self.quitting {
                    self.fail("The local server stopped (exit \(process.terminationStatus)).\n\(self.diagnostics)")
                }
            }
        }
        backend = process
        do {
            try process.run()
            startupTimer = Timer.scheduledTimer(withTimeInterval: 30, repeats: false) { _ in
                self.fail("The local server did not start within 30 seconds.\n\(self.diagnostics)")
            }
        } catch {
            fail("Could not start the local server: \(error.localizedDescription)")
        }
    }

    func receive(_ text: String) {
        output += text
        while let newline = output.firstIndex(of: "\n") {
            let line = String(output[..<newline]).trimmingCharacters(in: .whitespacesAndNewlines)
            output.removeSubrange(...newline)
            let prefix = "GitHub Team App: "
            if root == nil && line.hasPrefix(prefix),
               let url = loopbackURL(String(line.dropFirst(prefix.count))) {
                startupTimer?.invalidate()
                load(url)
            }
        }
        output = String(output.suffix(8192))
    }

    func load(_ url: URL) {
        root = url
        webView.load(URLRequest(url: url))
    }

    func fail(_ message: String) {
        guard !quitting else { return }
        exitCode = 1
        startupTimer?.invalidate()
        let alert = NSAlert()
        alert.messageText = "GitHub Team App"
        alert.informativeText = message
        alert.alertStyle = .critical
        alert.runModal()
        NSApp.terminate(nil)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        quitting = true
        startupTimer?.invalidate()
        parentTimer?.invalidate()
        guard let process = backend, process.isRunning else { return .terminateNow }
        process.terminationHandler = { _ in
            DispatchQueue.main.async { sender.reply(toApplicationShouldTerminate: true) }
        }
        process.terminate()
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) {
            if process.isRunning {
                kill(process.processIdentifier, SIGKILL)
            }
        }
        return .terminateLater
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = navigationAction.request.url, let root else {
            decisionHandler(.cancel)
            return
        }
        if sameOrigin(url, root) {
            decisionHandler(.allow)
        } else {
            decisionHandler(.cancel)
            if navigationAction.targetFrame?.isMainFrame != false && externalURL(url) {
                NSWorkspace.shared.open(url)
            }
        }
    }

    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        guard let url = navigationAction.request.url, let root else { return nil }
        if sameOrigin(url, root) { webView.load(navigationAction.request) }
        else if externalURL(url) { NSWorkspace.shared.open(url) }
        return nil
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        if (error as NSError).code != NSURLErrorCancelled {
            fail("Could not load the local dashboard: \(error.localizedDescription)")
        }
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        if !windowShown {
            windowShown = true
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void) {
        let alert = NSAlert()
        alert.messageText = "GitHub Team App"
        alert.informativeText = message
        alert.beginSheetModal(for: window) { _ in completionHandler() }
    }

    func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) {
        let alert = NSAlert()
        alert.messageText = "GitHub Team App"
        alert.informativeText = message
        alert.addButton(withTitle: "Continue")
        alert.addButton(withTitle: "Cancel")
        alert.beginSheetModal(for: window) { completionHandler($0 == .alertFirstButtonReturn) }
    }
}

let arguments = Array(CommandLine.arguments.dropFirst())
if arguments.count == 3 && arguments[0] == "--write-icons" {
    do { try writeIcons(from: arguments[1], to: arguments[2]) }
    catch { fputs("Icon generation failed: \(error)\n", stderr); exit(1) }
} else if arguments == ["--self-test"] {
    precondition(appearanceMessage("appearance:system:dark")?.dark == true)
    precondition(appearanceMessage("appearance:system:light")?.dark == false)
    precondition(appearanceMessage("appearance:dark:dark")?.preference == "dark")
    precondition(appearanceMessage("appearance:light:light")?.preference == "light")
    for invalid in ["appearance:light:dark", "appearance:dark:light", "appearance:auto:dark",
                    "appearance:system:invalid", "appearance:dark", "appearance:dark:dark:extra"] {
        precondition(appearanceMessage(invalid) == nil)
    }
    precondition(loopbackURL("http://127.0.0.1:5143") != nil)
    for invalid in ["https://127.0.0.1:5143", "http://evil.test:5143", "http://127.0.0.1:0",
                    "http://user@127.0.0.1:5143", "http://127.0.0.1:5143/?x=1"] {
        precondition(loopbackURL(invalid) == nil)
    }
    let root = loopbackURL("http://127.0.0.1:5143")!
    precondition(sameOrigin(URL(string: "http://127.0.0.1:5143/api/state")!, root))
    precondition(!sameOrigin(URL(string: "http://127.0.0.1:9999")!, root))
    precondition(externalURL(URL(string: "https://github.com/owner/repo")!))
    precondition(externalURL(URL(string: "ghapp://session/new?repo=owner/repo")!))
    for invalid in ["file:///etc/passwd", "javascript:alert(1)", "ghapp://other/action", "http://evil.test"] {
        precondition(!externalURL(URL(string: invalid)!))
    }
    print("Native shell URL policy tests passed.")
} else {
    let application = NSApplication.shared
    let delegate = AppDelegate()
    application.delegate = delegate
    application.run()
    exit(delegate.exitCode)
}
