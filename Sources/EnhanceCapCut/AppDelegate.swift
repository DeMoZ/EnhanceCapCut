import AppKit
import EnhanceCapCutCore
import Sparkle
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var window: NSWindow?
    private var subtitlesWindow: NSWindow?
    /// A `SubtitlesModel` (macOS 26+), kept for the life of the app so running jobs survive closing the window.
    private var subtitlesModel: AnyObject?
    private var donateWindow: NSWindow?
    private let donateModel = DonateModel()
    private var guideWindow: NSWindow?
    private let model = ExportModel()
    private let services = ServiceProvider()
    /// Sparkle, only in the app bundle (a `swift run` build has no Info.plist feed to check).
    private let updater: SPUStandardUpdaterController? = Bundle.main.bundleURL.pathExtension == "app"
        ? SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: nil, userDriverDelegate: nil) : nil

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let running = deferToRunningInstance() {
            running.activate()
            NSApp.terminate(nil)
            return
        }
        Localization.use(Localization.choice ?? .system())
        NSApp.mainMenu = makeMenu()
        services.onInvoke = { [weak self] in self?.showExportWindow() }
        model.openSubtitles = { [weak self] in self?.showSubtitlesWindow() }
        model.openDonate = { [weak self] in self?.showDonateWindow() }
        model.openGuide = { [weak self] in self?.showGuideWindow() }
        model.setLanguage = { [weak self] in self?.setLanguage($0) }
        model.exportFinished = { [weak self] in self?.offerDonationOncePerDay() }
        if let updater {
            model.checkForUpdates = { updater.checkForUpdates(nil) }
        }
        NSApp.servicesProvider = services
        NSUpdateDynamicServices()
        showExportWindow()
        // `--args -subtitles YES` opens the subtitles window too (as `--subtitles` on Windows; for screenshots).
        if UserDefaults.standard.bool(forKey: "subtitles") { showSubtitlesWindow() }
        showGuideOnFirstLaunch()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showExportWindow()
        return true
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if #available(macOS 26, *), let subtitles = subtitlesModel as? SubtitlesModel, subtitles.isRunning {
            let alert = NSAlert()
            alert.messageText = L("Subtitles are being created")
            alert.informativeText = L("Quitting cancels them.")
            alert.addButton(withTitle: L("Keep Working"))
            alert.addButton(withTitle: L("Quit"))
            guard alert.runModal() == .alertSecondButtonReturn else { return .terminateCancel }
            subtitles.cancel()
        }
        guard model.isExporting else { return .terminateNow }
        let alert = NSAlert()
        alert.messageText = L("An export is in progress")
        alert.informativeText = L("Quitting cancels it.")
        alert.addButton(withTitle: L("Keep Exporting"))
        alert.addButton(withTitle: L("Quit"))
        if alert.runModal() == .alertSecondButtonReturn {
            model.cancel()
            return .terminateNow
        }
        return .terminateCancel
    }

    /// Shows the export window and re-detects the project open in CapCut.
    func showExportWindow() {
        if !model.isExporting {
            model.refreshProjects(selectOpen: true)
            // `open EnhanceCapCut.app --args -project <name>` preselects a project (handy for testing).
            if let name = UserDefaults.standard.string(forKey: "project"),
               let project = model.projects.first(where: { $0.name == name }) {
                model.selectedProjectURL = project.url
            }
        }
        if window == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 760),
                                  styleMask: [.titled, .closable, .miniaturizable, .resizable],
                                  backing: .buffered, defer: false)
            window.title = L("Export with EnhanceCapCut")
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: ExportView(model: model))
            window.setFrameAutosaveName("ExportWindow")
            window.center()
            self.window = window
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    /// Shows the "Subtitles & Voice-over" window for the project selected in the export window.
    func showSubtitlesWindow() {
        guard #available(macOS 26, *) else { return }
        let subtitles = subtitlesModel as? SubtitlesModel ?? SubtitlesModel(export: model)
        subtitlesModel = subtitles
        if subtitlesWindow == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 720),
                                  styleMask: [.titled, .closable, .miniaturizable, .resizable],
                                  backing: .buffered, defer: false)
            window.title = L("Subtitles & Voice-over")
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: SubtitlesView(model: subtitles))
            window.setFrameAutosaveName("SubtitlesWindow")
            window.center()
            subtitlesWindow = window
        }
        subtitles.refreshProjectState()
        NSApp.activate(ignoringOtherApps: true)
        subtitlesWindow?.makeKeyAndOrderFront(nil)
    }

    /// Shows the "Support EnhanceCapCut" window (app info and the ways to donate, downloaded again); it's also the
    /// About window.
    func showDonateWindow(title: String = L("Support EnhanceCapCut")) {
        if donateWindow == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 440, height: 440),
                                  styleMask: [.titled, .closable, .miniaturizable, .resizable],
                                  backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: DonateView(model: donateModel))
            window.setFrameAutosaveName("DonateWindow")
            window.center()
            donateWindow = window
        }
        donateWindow?.title = title
        donateModel.load()
        NSApp.activate(ignoringOtherApps: true)
        donateWindow?.makeKeyAndOrderFront(nil)
    }

    /// Shows the "How to Use EnhanceCapCut" window (`GuideView`).
    func showGuideWindow() {
        if guideWindow == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 640),
                                  styleMask: [.titled, .closable, .miniaturizable, .resizable],
                                  backing: .buffered, defer: false)
            window.title = L("How to Use EnhanceCapCut")
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: GuideView())
            window.setFrameAutosaveName("GuideWindow")
            window.center()
            guideWindow = window
        }
        NSApp.activate(ignoringOtherApps: true)
        guideWindow?.makeKeyAndOrderFront(nil)
    }

    /// The guide opens by itself once, at the first launch (as on Windows); not for `-autoExport` runs.
    private func showGuideOnFirstLaunch() {
        let defaults = UserDefaults.standard
        guard !defaults.bool(forKey: "guideShown"), !defaults.bool(forKey: "autoExport") else { return }
        defaults.set(true, forKey: "guideShown")
        showGuideWindow()
    }

    /// Switches the interface to `choice` (nil: the system's language) right away: the menu and the open windows are
    /// built again in the new language. Texts a window already worked out (a finished run's summary) stay as they were.
    func setLanguage(_ choice: Localization.Language?) {
        Localization.choice = choice
        let language = choice ?? .system()
        guard language != Localization.language else { return }
        Localization.use(language)
        NSApp.mainMenu = makeMenu()
        window?.title = L("Export with EnhanceCapCut")
        window?.contentView = NSHostingView(rootView: ExportView(model: model))
        if #available(macOS 26, *), let subtitles = subtitlesModel as? SubtitlesModel {
            subtitlesWindow?.title = L("Subtitles & Voice-over")
            subtitlesWindow?.contentView = NSHostingView(rootView: SubtitlesView(model: subtitles))
        }
        donateWindow?.title = L("Support EnhanceCapCut")
        donateWindow?.contentView = NSHostingView(rootView: DonateView(model: donateModel))
        guideWindow?.title = L("How to Use EnhanceCapCut")
        guideWindow?.contentView = NSHostingView(rootView: GuideView())
        // The "Not rendered yet" list is worked out with the timeline: read it again in the new language.
        if !model.isExporting { model.refreshProjects(selectOpen: false) }
    }

    /// After the first export of each day, offers to donate (as on Windows).
    private func offerDonationOncePerDay() {
        let today = Date().formatted(Date.ISO8601FormatStyle(timeZone: .current).year().month().day())
        guard UserDefaults.standard.string(forKey: "donateOfferDay") != today else { return }
        UserDefaults.standard.set(today, forKey: "donateOfferDay")
        showDonateWindow()
    }

    private func makeMenu() -> NSMenu {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: L("About EnhanceCapCut"), action: #selector(showAboutAction), keyEquivalent: "").target = self
        if let updater {
            appMenu.addItem(withTitle: L("Check for Updates…"), action: #selector(SPUStandardUpdaterController.checkForUpdates(_:)),
                            keyEquivalent: "").target = updater
        }
        appMenu.addItem(withTitle: L("Donate…"), action: #selector(showDonateAction), keyEquivalent: "").target = self
        appMenu.addItem(.separator())
        let servicesItem = NSMenuItem(title: L("Services"), action: nil, keyEquivalent: "")
        let servicesMenu = NSMenu()
        servicesItem.submenu = servicesMenu
        NSApp.servicesMenu = servicesMenu
        appMenu.addItem(servicesItem)
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: L("Hide EnhanceCapCut"), action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: L("Quit EnhanceCapCut"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: L("Edit"))
        edit.addItem(withTitle: L("Undo"), action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: L("Redo"), action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: L("Cut"), action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: L("Copy"), action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: L("Paste"), action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: L("Select All"), action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)

        let windowItem = NSMenuItem()
        let windowMenu = NSMenu(title: L("Window"))
        windowMenu.addItem(withTitle: L("Export Window"), action: #selector(showWindowAction), keyEquivalent: "e").target = self
        if #available(macOS 26, *) {
            windowMenu.addItem(withTitle: L("Subtitles & Voice-over"), action: #selector(showSubtitlesAction), keyEquivalent: "u").target = self
        }
        windowMenu.addItem(withTitle: L("Close"), action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowMenu.addItem(withTitle: L("Minimize"), action: #selector(NSWindow.miniaturize(_:)), keyEquivalent: "m")
        windowItem.submenu = windowMenu
        NSApp.windowsMenu = windowMenu
        main.addItem(windowItem)

        let helpItem = NSMenuItem()
        let helpMenu = NSMenu(title: L("Help"))
        helpMenu.addItem(withTitle: L("EnhanceCapCut Help"), action: #selector(showGuideAction), keyEquivalent: "?").target = self
        helpItem.submenu = helpMenu
        NSApp.helpMenu = helpMenu
        main.addItem(helpItem)
        return main
    }

    @objc private func showWindowAction() { showExportWindow() }
    @objc private func showSubtitlesAction() { showSubtitlesWindow() }
    @objc private func showGuideAction() { showGuideWindow() }
    @objc private func showDonateAction() { showDonateWindow() }
    @objc private func showAboutAction() { showDonateWindow(title: L("About EnhanceCapCut")) }

    /// Keeps a single instance when two copies are installed (e.g. ~/Applications and /Applications):
    /// both can answer the Services entry and show their own export window. Older copies are asked to
    /// quit (one that is exporting asks the user first); if an equal or newer one runs, this one exits.
    private func deferToRunningInstance() -> NSRunningApplication? {
        guard let id = Bundle.main.bundleIdentifier else { return nil }
        let own = Self.buildNumber(Bundle.main)
        var keep: NSRunningApplication?
        for other in NSRunningApplication.runningApplications(withBundleIdentifier: id) where other != .current {
            let theirs = other.bundleURL.flatMap(Bundle.init(url:)).map(Self.buildNumber) ?? ""
            if theirs.compare(own, options: .numeric) == .orderedAscending {
                other.terminate()  // an exporting instance refuses via applicationShouldTerminate
            } else {
                keep = other
            }
        }
        return keep
    }

    private static func buildNumber(_ bundle: Bundle) -> String {
        bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? ""
    }
}

/// Handles the "Export with EnhanceCapCut" entry of the system Services menu (declared in Info.plist).
final class ServiceProvider: NSObject {
    var onInvoke: (() -> Void)?

    @objc func exportWithEnhanceCapCut(_ pasteboard: NSPasteboard, userData: String?,
                                       error: AutoreleasingUnsafeMutablePointer<NSString?>) {
        DispatchQueue.main.async { self.onInvoke?() }
    }
}
