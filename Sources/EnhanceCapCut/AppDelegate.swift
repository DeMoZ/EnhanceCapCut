import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var window: NSWindow?
    private let model = ExportModel()
    private let services = ServiceProvider()

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.mainMenu = makeMenu()
        services.onInvoke = { [weak self] in self?.showExportWindow() }
        NSApp.servicesProvider = services
        NSUpdateDynamicServices()
        showExportWindow()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showExportWindow()
        return true
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard model.isExporting else { return .terminateNow }
        let alert = NSAlert()
        alert.messageText = "An export is in progress"
        alert.informativeText = "Quitting cancels it."
        alert.addButton(withTitle: "Keep Exporting")
        alert.addButton(withTitle: "Quit")
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
            window.title = "Export with EnhanceCapCut"
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: ExportView(model: model))
            window.setFrameAutosaveName("ExportWindow")
            window.center()
            self.window = window
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    private func makeMenu() -> NSMenu {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About EnhanceCapCut", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        let servicesItem = NSMenuItem(title: "Services", action: nil, keyEquivalent: "")
        let servicesMenu = NSMenu()
        servicesItem.submenu = servicesMenu
        NSApp.servicesMenu = servicesMenu
        appMenu.addItem(servicesItem)
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Hide EnhanceCapCut", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quit EnhanceCapCut", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)

        let windowItem = NSMenuItem()
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Export Window", action: #selector(showWindowAction), keyEquivalent: "e").target = self
        windowMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowMenu.addItem(withTitle: "Minimize", action: #selector(NSWindow.miniaturize(_:)), keyEquivalent: "m")
        windowItem.submenu = windowMenu
        NSApp.windowsMenu = windowMenu
        main.addItem(windowItem)
        return main
    }

    @objc private func showWindowAction() { showExportWindow() }
}

/// Handles the "Export with EnhanceCapCut" entry of the system Services menu (declared in Info.plist).
final class ServiceProvider: NSObject {
    var onInvoke: (() -> Void)?

    @objc func exportWithEnhanceCapCut(_ pasteboard: NSPasteboard, userData: String?,
                                       error: AutoreleasingUnsafeMutablePointer<NSString?>) {
        DispatchQueue.main.async { self.onInvoke?() }
    }
}
