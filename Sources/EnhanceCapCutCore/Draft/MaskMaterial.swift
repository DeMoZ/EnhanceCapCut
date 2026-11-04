import Foundation

/// A clip mask (`materials.common_mask[]`), see FORMAT.md › Masks.
public struct MaskMaterial: Decodable {
    public var id: String
    /// Mask keyframes point at this id (`common_keyframes[].material_id`).
    public var constantID: String
    /// "Split", "Mirror", "Circle", "Rectangle", "Heart", "Star", "Brush".
    public var name: String
    /// `line`, `mirror`, `circle`, `rectangle`, `geometric_shape`, `custom` (brush).
    public var resourceType: String
    /// The brush strokes' folder, usually `##_draftpath_placeholder_…_##/adjust_mask/<id>`.
    public var loaderWorkSpace: String
    /// Center in half-material units (+y up), size as fractions of the material's width and height.
    public var centerX: Double
    public var centerY: Double
    public var width: Double
    public var height: Double
    /// Degrees, clockwise.
    public var rotation: Double
    /// 0...1.
    public var feather: Double
    public var expansion: Double
    /// 0...1 of the shorter side's half.
    public var roundCorner: Double
    public var invert: Bool

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        guard let id = c.str("id") else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "no id"))
        }
        self.id = id
        constantID = c.str("constant_material_id") ?? ""
        name = c.str("name") ?? ""
        resourceType = c.str("resource_type") ?? ""
        loaderWorkSpace = c.str("loader_work_space") ?? ""
        let config = try? c.nestedContainer(keyedBy: AnyKey.self, forKey: AnyKey("config"))
        centerX = config?.num("centerX") ?? 0
        centerY = config?.num("centerY") ?? 0
        width = config?.num("width") ?? 0.5
        height = config?.num("height") ?? 0.5
        rotation = config?.num("rotation") ?? 0
        feather = config?.num("feather") ?? 0
        expansion = config?.num("expansion") ?? 0
        roundCorner = config?.num("roundCorner") ?? 0
        invert = config?.bool("invert") ?? false
    }
}
