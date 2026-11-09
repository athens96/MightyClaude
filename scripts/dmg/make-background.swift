// Draws the dmg install window's background at 1x and 2x.
//
//   swift scripts/dmg/make-background.swift <output folder>
//
// Writes background.png (660×400) and background@2x.png (1320×800); the caller
// joins them into one Retina TIFF (tiffutil -cathidpicheck). The layout matches
// scripts/dmg/settings.py: the app icon centred at (165, 180), Applications at
// (495, 180), both 128 pt, so the arrow runs between them. Colours are the light
// tokens of native/contracts/fixtures/design-tokens.json (page, card, accent,
// ink2): Finder draws the icon names over this picture in the system's label
// colour, and a light picture keeps them legible in both appearances (Finder
// gives them a backdrop in dark mode).
import AppKit

let width: CGFloat = 660, height: CGFloat = 400
let appCentre = CGPoint(x: 165, y: 180), applicationsCentre = CGPoint(x: 495, y: 180)
let page = NSColor(srgbRed: 0xEC / 255, green: 0xEE / 255, blue: 0xF3 / 255, alpha: 1)
let card = NSColor(srgbRed: 1, green: 1, blue: 1, alpha: 1)
let accent = NSColor(srgbRed: 0x24 / 255, green: 0x59 / 255, blue: 0xE6 / 255, alpha: 1)
let ink2 = NSColor(srgbRed: 0x5A / 255, green: 0x63 / 255, blue: 0x77 / 255, alpha: 1)

func render(scale: CGFloat) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(width * scale), pixelsHigh: Int(height * scale),
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: width, height: height)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    // Finder's coordinates run from the top-left; AppKit's from the bottom-left.
    func flip(_ point: CGPoint) -> CGPoint { CGPoint(x: point.x, y: height - point.y) }

    NSGradient(starting: card, ending: page)!.draw(in: NSRect(x: 0, y: 0, width: width, height: height), angle: -90)

    // A soft well under each icon, so the two ends of the drag read as places.
    for centre in [appCentre, applicationsCentre] {
        let c = flip(centre)
        let well = NSBezierPath(roundedRect: NSRect(x: c.x - 88, y: c.y - 88, width: 176, height: 176), xRadius: 28, yRadius: 28)
        page.withAlphaComponent(0.9).setFill()
        well.fill()
        NSColor(srgbRed: 0xDE / 255, green: 0xE2 / 255, blue: 0xEA / 255, alpha: 1).setStroke()
        well.lineWidth = 1
        well.stroke()
    }

    // The arrow: a short shaft and an open head, between the wells.
    let from = flip(CGPoint(x: appCentre.x + 100, y: appCentre.y)), to = flip(CGPoint(x: applicationsCentre.x - 100, y: applicationsCentre.y))
    let shaft = NSBezierPath()
    shaft.move(to: from)
    shaft.line(to: CGPoint(x: to.x - 4, y: to.y))
    shaft.lineWidth = 5
    shaft.lineCapStyle = .round
    accent.setStroke()
    shaft.stroke()
    let head = NSBezierPath()
    head.move(to: CGPoint(x: to.x - 18, y: to.y + 16))
    head.line(to: to)
    head.line(to: CGPoint(x: to.x - 18, y: to.y - 16))
    head.lineWidth = 5
    head.lineCapStyle = .round
    head.lineJoinStyle = .round
    head.stroke()

    // The instruction, in Korean and English, under the icons' names.
    let paragraph = NSMutableParagraphStyle()
    paragraph.alignment = .center
    func line(_ text: String, size: CGFloat, weight: NSFont.Weight, colour: NSColor, top: CGFloat) {
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: size, weight: weight),
                                                          .foregroundColor: colour, .paragraphStyle: paragraph]
        let string = NSAttributedString(string: text, attributes: attributes)
        let bounds = string.boundingRect(with: NSSize(width: width, height: 100), options: .usesLineFragmentOrigin)
        string.draw(in: NSRect(x: 0, y: height - top - bounds.height, width: width, height: bounds.height))
    }
    line("Mighty Code를 응용 프로그램 폴더로 끌어다 놓으세요", size: 15, weight: .semibold, colour: ink2, top: 318)
    line("Drag Mighty Code into Applications", size: 12, weight: .regular, colour: ink2.withAlphaComponent(0.8), top: 344)

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let output = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : ".")
try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
try render(scale: 1).write(to: output.appendingPathComponent("background.png"))
try render(scale: 2).write(to: output.appendingPathComponent("background@2x.png"))
