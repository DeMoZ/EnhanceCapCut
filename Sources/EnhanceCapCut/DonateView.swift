import EnhanceCapCutCore
import SwiftUI

/// The "Support EnhanceCapCut" window (see `DonateModel`).
struct DonateView: View {
    @ObservedObject var model: DonateModel

    var body: some View {
        Form {
            switch model.state {
            case .loading:
                Section {
                    HStack {
                        ProgressView().controlSize(.small)
                        Text("Loading the ways to donate…").foregroundStyle(.secondary)
                    }
                }
            case .failed:
                Section {
                    Text("Couldn't load the ways to donate. Check your internet connection.")
                    Button("Try Again") { model.load() }
                }
            case let .loaded(info):
                if let message = info.message?.resolved(for: model.languages) {
                    Section { Text(message) }
                }
                Section("Ways to donate") {
                    ForEach(info.methods, id: \.id) { method in
                        if let title = method.title.resolved(for: model.languages) {
                            row(method, title: title)
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 400, minHeight: 280)
    }

    private func row(_ method: DonationMethod, title: String) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                if let description = method.description?.resolved(for: model.languages) {
                    Text(description).font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer()
            Button("Donate…") { model.open(method) }
                .help(method.url.absoluteString)
        }
    }
}
