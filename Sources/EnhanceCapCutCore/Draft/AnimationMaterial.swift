import Foundation

/// `materials.material_animations[]`: the intro / outro / loop animations of a text or sticker clip
/// (`type` "sticker_animation" for both), linked from the segment's `extra_material_refs`.
public struct AnimationMaterial: Decodable {
    public var id: String
    public var animations: [AnimationItem]

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        id = c.str("id") ?? ""
        animations = c.list("animations")
    }
}

/// One animation of a `material_animations` entry.
public struct AnimationItem: Decodable {
    /// "in", "out" or "loop".
    public var type: String
    /// Display name in the language CapCut used when it was applied ("Fade In", "渐显", …).
    public var name: String
    /// The preset's id in CapCut's library.
    public var resourceID: String
    /// µs. For a loop: the length of one cycle.
    public var duration: Int64

    public init(type: String, name: String, resourceID: String = "", duration: Int64) {
        self.type = type
        self.name = name
        self.resourceID = resourceID
        self.duration = duration
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        type = c.str("type") ?? ""
        name = c.str("name") ?? ""
        resourceID = c.str("resource_id") ?? c.str("third_resource_id") ?? c.str("id") ?? ""
        duration = Int64(c.num("duration") ?? 0)
    }
}
