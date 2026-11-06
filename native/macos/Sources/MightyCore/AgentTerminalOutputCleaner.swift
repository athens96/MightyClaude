import Foundation

/// Turns raw PTY output into plain text for the agent.
///
/// The terminal pane runs under TERM=xterm-256color, so its bytes carry colour
/// and cursor escape sequences, and the tty ends every line with CR LF. The
/// agent reads plain lines instead: CSI, OSC and every other ESC sequence
/// (DCS, SOS, PM and APC strings included) are removed and CR LF becomes LF.
/// A bare CR — a progress bar redrawing its line — returns to the line's start
/// as a terminal does, so the next text overwrites it (and `ESC [ K` erases the
/// rest): the agent reads the line as the pane last showed it, not every redraw.
///
/// Only the text handed to the agent is cleaned; the visible pane and the
/// per-handle buffers stay raw. One cleaner follows one handle's reads, so a
/// sequence or a CR LF split across two 64 KB reads is still handled whole. A
/// line still being redrawn when a read ends is written as it stands then; if a
/// later read changes it other than by adding to its end, the new state follows
/// on its own line, so each read shows a progress line once.
public struct AgentTerminalOutputCleaner: Sendable, Equatable {
    private enum State: Sendable, Equatable {
        case text
        case escape       // after ESC
        case csi          // ESC [ … final byte
        case string       // OSC / DCS / SOS / PM / APC body, until BEL or ST
        case stringEscape // ESC inside a string: ST if `\` follows
    }

    private var state = State.text
    /// A CR was seen: an LF next makes it a line end, anything else a return
    /// to the line's start.
    private var pendingCR = false
    /// The current line as the terminal shows it, and the cursor in it.
    private var line: [Unicode.Scalar] = []
    private var column = 0
    /// What of the current line an earlier read already wrote, nil for nothing.
    private var written: [Unicode.Scalar]?
    /// Parameter bytes of the CSI sequence being read (for `ESC [ K`).
    private var csiParameters = ""

    public init() {}

    /// Clean the next piece of one handle's output. `final` says no more
    /// output follows, so a trailing CR is written out rather than held for
    /// a possible LF.
    public mutating func clean(_ raw: String, final: Bool) -> String {
        var out = String.UnicodeScalarView()
        for scalar in raw.unicodeScalars { feed(scalar, into: &out) }
        flushLine(into: &out)
        if final {
            if pendingCR { endLine(into: &out) }
            pendingCR = false
            state = .text
        }
        return String(out)
    }

    /// Write what the current line shows beyond what was written of it before.
    private mutating func flushLine(into out: inout String.UnicodeScalarView) {
        if let written, written == line || line.isEmpty { return }
        if let written, line.starts(with: written) {
            out.append(contentsOf: line[written.count...])
        } else {
            if written != nil { out.append("\n") }
            out.append(contentsOf: line)
        }
        written = line.isEmpty && written == nil ? nil : line
    }

    private mutating func endLine(into out: inout String.UnicodeScalarView) {
        flushLine(into: &out)
        out.append("\n")
        line.removeAll(keepingCapacity: true)
        column = 0
        written = nil
    }

    private mutating func put(_ scalar: Unicode.Scalar) {
        if pendingCR { pendingCR = false; column = 0 }
        if column < line.count { line[column] = scalar } else { line.append(scalar) }
        column += 1
    }

    private mutating func feed(_ scalar: Unicode.Scalar, into out: inout String.UnicodeScalarView) {
        switch state {
        case .text:
            switch scalar.value {
            case 0x1B: state = .escape
            case 0x9B: state = .csi; csiParameters = ""      // C1 CSI
            case 0x90, 0x98, 0x9D, 0x9E, 0x9F: state = .string // C1 DCS, SOS, OSC, PM, APC
            case 0x0D: pendingCR = true
            case 0x0A:
                pendingCR = false
                endLine(into: &out)
            case 0x08:
                if pendingCR { pendingCR = false; column = 0 }
                column = max(0, column - 1)
            default:
                put(scalar)
            }
        case .escape:
            switch scalar.value {
            case 0x5B: state = .csi; csiParameters = ""           // [
            case 0x5D, 0x50, 0x58, 0x5E, 0x5F: state = .string    // ] P X ^ _
            case 0x1B, 0x20 ... 0x2F: break                       // restart, or intermediate bytes
            default: state = .text                                // the final byte ends it
            }
        case .csi:
            switch scalar.value {
            case 0x40 ... 0x7E:                 // final byte
                state = .text
                if scalar == "K" { eraseInLine() }
            case 0x20 ... 0x3F: csiParameters.unicodeScalars.append(scalar) // parameter and intermediate bytes
            case 0x1B: state = .escape
            default:
                // A control or other text cuts the sequence short; keep it as text.
                state = .text
                feed(scalar, into: &out)
            }
        case .string:
            switch scalar.value {
            case 0x07, 0x9C: state = .text      // BEL or C1 ST
            case 0x1B: state = .stringEscape
            default: break
            }
        case .stringEscape:
            if scalar.value == 0x5C {
                state = .text                   // ESC \ is ST
            } else {
                // Any other ESC ends the string and starts a new sequence.
                state = .escape
                feed(scalar, into: &out)
            }
        }
    }

    /// `ESC [ K` (or `0K`) erases from the cursor to the line's end, `1K` to its
    /// start, `2K` the whole line; the cursor stays. A CR before it has already
    /// moved the cursor home.
    private mutating func eraseInLine() {
        if pendingCR { pendingCR = false; column = 0 }
        switch csiParameters {
        case "", "0": if column < line.count { line.removeSubrange(column...) }
        case "1": for index in 0 ..< min(column + 1, line.count) { line[index] = " " }
        case "2": line.removeAll(keepingCapacity: true); line.append(contentsOf: repeatElement(" ", count: column))
        default: break
        }
    }
}
