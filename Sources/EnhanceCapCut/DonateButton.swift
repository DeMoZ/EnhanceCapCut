import SwiftUI

/// "♥ Donate" next to the Export button. Now and then it plays one short effect to catch the eye — a double heartbeat,
/// the label swinging like a swing, or a pink glow — and stays still the rest of the time. With Reduce Motion on, only
/// the glow plays. Same schedule and effects as `DonateButton` in the Windows version.
struct DonateButton: View {
    let action: () -> Void

    enum Effect: CaseIterable { case pulse, swing, glow }

    /// The first effect plays a little after the window appears…
    static let firstDelay: Duration = .seconds(6)
    /// …then one effect every 10 s, in turn: each lasts under 2 s, so the button is still ~85% of the time.
    static let interval: Duration = .seconds(10)
    /// Pulse: the button grows by 12%, then by 7% (lub-dub).
    static let pulseScales: (CGFloat, CGFloat) = (1.12, 1.07)
    /// Swing: the label hangs from its top edge and swings out to 12°, each swing ~70% of the one before.
    static let swingAngles: [Double] = [12, -8.5, 6, -4, 2.5, -1]
    /// Glow: the pink halo's blur radius at full strength.
    static let glowRadius: CGFloat = 9

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var effect = Effect.pulse
    @State private var trigger = 0

    private struct Values {
        var scale: CGFloat = 1
        var angle: Double = 0
        var glow: CGFloat = 0
    }

    var body: some View {
        KeyframeAnimator(initialValue: Values(), trigger: trigger) { values in
            Button(action: action) {
                Label {
                    Text("Donate")
                } icon: {
                    Image(systemName: "heart.fill").foregroundStyle(.pink)
                }
                .rotationEffect(.degrees(values.angle), anchor: .top)
            }
            .help("Support EnhanceCapCut")
            .shadow(color: .pink.opacity(0.9 * values.glow), radius: Self.glowRadius * values.glow)
            .scaleEffect(values.scale)
        } keyframes: { _ in
            let pulse: CGFloat = effect == .pulse ? 1 : 0
            let swing = effect == .swing ? 1.0 : 0
            let glow: CGFloat = effect == .glow ? 1 : 0
            KeyframeTrack(\.scale) {
                SpringKeyframe(1 + (Self.pulseScales.0 - 1) * pulse, duration: 0.14)
                SpringKeyframe(1, duration: 0.16)
                SpringKeyframe(1 + (Self.pulseScales.1 - 1) * pulse, duration: 0.14)
                SpringKeyframe(1, duration: 0.5)
            }
            KeyframeTrack(\.angle) {
                let a = Self.swingAngles.map { $0 * swing }
                CubicKeyframe(a[0], duration: 0.24)
                CubicKeyframe(a[1], duration: 0.24)
                CubicKeyframe(a[2], duration: 0.24)
                CubicKeyframe(a[3], duration: 0.24)
                CubicKeyframe(a[4], duration: 0.24)
                CubicKeyframe(a[5], duration: 0.24)
                CubicKeyframe(0, duration: 0.24)
            }
            KeyframeTrack(\.glow) {
                CubicKeyframe(glow, duration: 0.5)
                CubicKeyframe(glow, duration: 0.4)
                CubicKeyframe(0, duration: 0.9)
            }
        }
        .task {
            try? await Task.sleep(for: Self.firstDelay)
            var next = 0
            while !Task.isCancelled {
                effect = reduceMotion ? .glow : Effect.allCases[next % Effect.allCases.count]
                next += 1
                trigger += 1
                try? await Task.sleep(for: Self.interval)
            }
        }
    }
}
