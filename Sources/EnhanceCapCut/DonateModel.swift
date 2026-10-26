import AppKit
import EnhanceCapCutCore

/// The "Support EnhanceCapCut" window (also the About window): app info, then ways to donate, downloaded from the
/// `DeMoZ/donations` repository each time the window opens, so links and texts change without a release. The last copy
/// shows while (or if) the download fails.
@MainActor
final class DonateModel: ObservableObject {
    enum State {
        case loading
        case loaded(DonationInfo)
        case failed
    }

    @Published private(set) var state: State = .loading
    private var loading = false

    /// The UI language order of the system, for the texts of the file.
    let languages = Locale.preferredLanguages
    /// "0.6.0" from the bundle; empty for a `swift run` build.
    let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    static let projectPage = URL(string: "https://github.com/DeMoZ/EnhanceCapCut")!

    func load() {
        guard !loading else { return }
        loading = true
        if case .loaded = state {} else {
            state = Donations.cached().map(State.loaded) ?? .loading
        }
        Task {
            let fresh = await Donations.download()
            loading = false
            if let fresh {
                state = .loaded(fresh)
            } else if case .loading = state {
                state = .failed
            }
        }
    }

    func open(_ method: DonationMethod) {
        NSWorkspace.shared.open(method.url)
    }
}
