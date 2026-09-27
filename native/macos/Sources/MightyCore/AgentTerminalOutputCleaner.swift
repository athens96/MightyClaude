import Foundation

/// Turns raw PTY output into plain text for the agent.
///
/// The terminal pane runs under TERM=xterm-256color, so its bytes carry colour
/// and cursor escape sequences, and the tty ends every line with CR LF. The
/// agent reads plain lines instead: CSI, OSC and every other ESC sequence
/// (DCS, SOS, PM and APC strings included) are removed, CR LF becomes LF, and
/// a bare CR — a progress bar redrawing its line — becomes LF too.
///
/// Only the text handed to the agent is cleaned; the visible pane and the
/// per-handle buffers stay raw. One cleaner follows one handle's reads, so a
/// sequence or a CR LF split across two 64 KB reads is still handled whole.
public struct AgentTerminalOutputCleaner: Sendable, Equatable {
    private enum State: Sendable, Equatable {
        case text
        case escape       // after ESC
        case csi          // ESC [ … final byte
        case string       // OSC / DCS / SOS / PM / APC body, until BEL or ST
        case stringEscape // ESC inside a string: ST if `\` follows
    }

    private var state = State.text
    /// A CR was seen; it becomes one LF, merged with an LF that follows.
    private var pendingCR = false

    public init() {}

    /// Clean the next piece of one handle's output. `final` says no more
    /// output follows, so a trailing CR is written out rather than held for
    /// a possible LF.
    public mutating func clean(_ raw: String, final: Bool) -> String {
        var out = String.UnicodeScalarView()
        for scalar in raw.unicodeScalars { feed(scalar, into: &out) }
        if final {
            if pendingCR { out.append("\n") }
            pendingCR = false
            state = .text
        }
        return String(out)
    }

    private mutating func feed(_ scalar: Unicode.Scalar, into out: inout String.UnicodeScalarView) {
        switch state {
        case .text:
            switch scalar.value {
            case 0x1B: state = .escape
            case 0x9B: state = .csi                          // C1 CSI
            case 0x90, 0x98, 0x9D, 0x9E, 0x9F: state = .string // C1 DCS, SOS, OSC, PM, APC
            case 0x0D: pendingCR = true
            case 0x0A:
                pendingCR = false
                out.append("\n")
            default:
                if pendingCR { out.append("\n"); pendingCR = false }
                out.append(scalar)
            }
        case .escape:
            switch scalar.value {
            case 0x5B: state = .csi                               // [
            case 0x5D, 0x50, 0x58, 0x5E, 0x5F: state = .string    // ] P X ^ _
            case 0x1B, 0x20 ... 0x2F: break                       // restart, or intermediate bytes
            default: state = .text                                // the final byte ends it
            }
        case .csi:
            switch scalar.value {
            case 0x40 ... 0x7E: state = .text   // final byte
            case 0x20 ... 0x3F: break           // parameter and intermediate bytes
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
}
