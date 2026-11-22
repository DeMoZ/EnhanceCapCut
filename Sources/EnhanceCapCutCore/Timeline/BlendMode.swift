import Foundation

/// An overlay clip's blend mode (CapCut's "Blend" panel). The draft keeps it as a `mix_mode` material in
/// `materials.effects`, referenced from the segment's `extra_material_refs` (see FORMAT.md). The opacity slider of
/// the same panel is the clip's ordinary opacity (`clip.alpha`).
///
/// The modes use the standard separable blend formulas (W3C Compositing / PDF), applied to gamma-encoded sRGB values
/// and then composited source-over, like the rest of the overlay.
public enum BlendMode: String, CaseIterable {
    case normal, screen, darken, lighten, overlay, hardLight, softLight, linearBurn, colorBurn, colorDodge, multiply

    /// The ids CapCut uses for each mode: its effect id, resource id, internal name and English UI name. The
    /// draft's `effect_id` / `resource_id` are matched first, then `name`.
    static let capCutIDs: [BlendMode: [String]] = [
        .normal: ["normal"],
        .screen: ["871339", "6758325170760323597", "color_filter", "screen"],
        .darken: ["871342", "6758324839670354445", "dark_en", "darken"],
        .lighten: ["871341", "6758324919789949453", "bright_en", "brighten", "lighten"],
        .overlay: ["871340", "6758324989931295240", "over_lay", "overlay"],
        .hardLight: ["871338", "6758325264670790152", "glare_pc", "hard light"],
        .softLight: ["871337", "6758325439212556814", "soft_light", "soft light"],
        .linearBurn: ["871336", "6758325619253056013", "linear_deepening", "linear burn"],
        .colorBurn: ["871335", "6758325724848853518", "darken_color", "color burn"],
        .colorDodge: ["871334", "6758325800031752712", "color_dodge", "color dodge"],
        .multiply: ["871333", "6758325895519277582", "multiply_blend_mode", "multiply"],
    ]

    /// The mode of a `mix_mode` material, nil when it isn't one of the known modes.
    public init?(material: GenericMaterial) {
        let keys = [material.effectID, material.resourceID, material.name.lowercased()].filter { !$0.isEmpty }
        for key in keys {
            if let mode = BlendMode.allCases.first(where: { BlendMode.capCutIDs[$0]?.contains(key) == true }) {
                self = mode
                return
            }
        }
        return nil
    }

    /// The Core Image compositing filter; nil for `normal` (plain source-over).
    var coreImageFilter: String? {
        switch self {
        case .normal: nil
        case .screen: "CIScreenBlendMode"
        case .darken: "CIDarkenBlendMode"
        case .lighten: "CILightenBlendMode"
        case .overlay: "CIOverlayBlendMode"
        case .hardLight: "CIHardLightBlendMode"
        case .softLight: "CISoftLightBlendMode"
        case .linearBurn: "CILinearBurnBlendMode"
        case .colorBurn: "CIColorBurnBlendMode"
        case .colorDodge: "CIColorDodgeBlendMode"
        case .multiply: "CIMultiplyBlendMode"
        }
    }
}
