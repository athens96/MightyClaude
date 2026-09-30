import Foundation

/// Where a transcript's viewport sits in its (top-down) document. A reader at
/// or near the bottom follows the latest line; anyone else stays where they
/// scrolled, whatever the text or the viewport does.
public enum TranscriptViewport {
    /// How far above the true bottom still counts as following.
    public static let followSlack: Double = 36

    public static func isFollowing(originY: Double, visibleHeight: Double, documentHeight: Double) -> Bool {
        originY + visibleHeight >= documentHeight - followSlack
    }

    /// The origin that shows the end of the document, including its bottom inset.
    public static func bottomOriginY(visibleHeight: Double, documentHeight: Double) -> Double {
        max(0, documentHeight - visibleHeight)
    }

    /// The origin after the viewport changed size: the new bottom for a reader
    /// who was following, nil (leave the origin alone) for one who scrolled up
    /// or when there was no real viewport before.
    public static func originAfterResize(previousOriginY: Double, previousVisibleHeight: Double,
                                         previousDocumentHeight: Double,
                                         visibleHeight: Double, documentHeight: Double) -> Double? {
        guard previousVisibleHeight > 1, visibleHeight > 1,
              isFollowing(originY: previousOriginY, visibleHeight: previousVisibleHeight,
                          documentHeight: previousDocumentHeight) else { return nil }
        return bottomOriginY(visibleHeight: visibleHeight, documentHeight: documentHeight)
    }
}
