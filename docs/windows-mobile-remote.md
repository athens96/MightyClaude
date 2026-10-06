# Windows mobile remote

The Windows app now hosts the same encrypted relay v1 and `/m1` protocol as the macOS app (`docs/relay.md`, `docs/mobile-remote.md`). Enable **Settings → Mobile remote**, apply a relay address, then scan the pairing QR or copy the pairing link into the phone app. The default relay is the same as macOS. No inbound PC port is opened.

The Windows host supports state and transcript history, queued or steering submissions, one-call tool approvals, questionnaires, Claude plan approvals (`plan` in the permission, `POST …/plan`), pane creation/rename/close, model/permission/effort settings, approved guided styles, commands, chunked attachments, and workspace file browsing. Terminal and browser panes remain desktop-only for execution. A capability is advertised only when its routing is installed; screen sharing additionally requires the Windows capture/WebRTC engine.

Implementation:

- `native/windows/MightyClaude.Core/Remote/RelayCrypto.cs`: X25519, HKDF-SHA256, directional counters, authenticated ChaCha20-Poly1305 frames. BouncyCastle.Cryptography provides the cryptographic primitives. The tests consume the same `native/contracts/relay-vectors.json` bytes as Mac/mobile.
- `MobileIdentity.cs`: stable host identity and device token hashes. A single atomically replaced `mobile-identity.json` stores the host private key, host token, pairing key, and device registry together. This differs from the separate Mac files so a failed write cannot commit a new pairing key while retaining old trusted tokens. The containing directory has a protected current-user Windows ACL; token values never enter the ordinary app snapshot or diagnostics.
- `MobileRelayHost.cs`: outbound control/data WebSockets, authenticated pairing, handshake and idle deadlines, reconnect, replay refusal, bounded concurrent connections and requests. Pairing key regeneration or revoking a device removes all device credentials and disconnects every phone, matching the latest host policy. The UI states this before confirmation.
- `MobileUploads.cs`: device and pane ownership, exact sequential chunk sizes, bounded staging handles, and single-use claims spent only after accepted submission. Failed submissions release claims. Staging data is deleted on consumption or shutdown.
- `MobileDesktopRouter.cs`: m1 routing, ephemeral permission state, bounded JSON replies, read-only file access through `WorkspaceFiles`, and desktop callbacks.
- `MainWindow.MobileRemote.cs`: UI dispatcher boundary, QR/settings/device controls, queue/style-aware composer routing, status and execution graph projection. `MainWindow.MobileImages.cs` supplies bounded Windows image/PDF previews.

Protocol/authentication/ownership checks run cross-platform. Actual Windows capture, ACL behavior, QR rendering, clipboard, and phone integration must also be validated on a Windows machine; a macOS compiler cannot validate those native runtime behaviors. No real provider credentials or external relay are required by the tests. The encrypted relay test uses a local WebSocket fixture and generated test keys.

## Screen view and control

**Settings → Mobile remote → Screen view & control** grants each paired phone **None**, **View only**, or **Control**. New phones have no access. A control phone registers its P-256 public key, which requires matching-fingerprint confirmation on the PC; every control session then consumes a fresh, two-minute signed challenge. Legacy key-only clients cannot receive screen grants. Removing a key, downgrading a grant, rotating pairing identity, locking Windows, or focusing a password field ends the affected sharing session. The in-app red stop control and physical **Ctrl+Alt+Esc** stop every session.

- `ScreenShareHub.cs` owns local grants, enrollment, single-use challenges, one controller/two viewers, owner-bound signaling/input, local-input pauses, background/idle expiry, and teardown. Peer teardown precedes relay notification, including when a phone stops reading its relay socket.
- `WindowsScreenCapture.cs` uses Windows Graphics Capture and Win2D on the selected monitor, retaining the visible OS capture border. GPU crop/resize and bounded JPEG frames feed a bundled WebView2 page; frames never touch disk. Display IDs include virtual-desktop origins for multi-monitor input.
- `Assets/ScreenShare/screen.js` creates two send-only WebRTC video streams (`screen`, `overview`) and the reliable `screen-control` data channel used by the existing Android client. It negotiates H.264, applies individual phone resolution/fps/bitrate ceilings, and caps TURN paths to 2 Mbps. Relay-minted TURN credentials renew before their advertised TTL, with ICE restart.
- `WindowsRemoteInput.cs` observes local mouse/keyboard activity and protected-focus state without reading field contents. It injects only validated mouse actions, a closed shortcut vocabulary, and committed UTF-16 Unicode via `SendInput`. Windows UIPI still blocks elevated windows and secure desktops. Unknown or stale privacy state fails closed.
- `ScreenClipboard.cs` performs only explicitly requested text transfers. It bounds compressed and decoded data to 1 MiB, validates chunk ownership/order/metadata, and decodes zstd with a bounded window. Concealed clipboard formats never produce plaintext replies.
- `ScreenShareTapMarkerOverlay.cs` supplies the captured, click-through, non-activating half-second tap marker. The measurement scene runs in its own window, announces its phases over the data channel, and leaves other windows/documents untouched.

The current Windows path uses H.264 and SDR BGRA8 capture. Optional Mac cellular AV1/VP9 adaptation and HDR tone mapping are not established Windows parity. Hardware capture, Windows privacy/UIPI behavior, monitor changes, and Android end-to-end delivery still need Windows/device validation.

Verification has separate scopes:

- Core tests exercise shared encryption vectors, encrypted local WebSockets, grants/challenges/replay, revocation races, clipboard bounds and privacy teardown under a broken relay.
- `scripts/tests/test-windows-screen-webrtc.cjs` runs actual Chromium WebRTC with synthetic pixels and a local peer. It checks the two stream IDs, H.264, Korean/emoji data, decoded frames, per-phone resolution, and channel shutdown. Set `MIGHTY_PLAYWRIGHT_MODULE` to an installed `playwright-core` module and `MIGHTY_CHROMIUM_EXECUTABLE` if Chrome is not in the default macOS location. This does not capture a desktop or prove the Windows native backend.
- Windows UI smoke uses synthetic Win2D pixels and a native non-activating marker; it does not record the unattended runner's desktop.

The latest macOS implementation clears all device tokens when a pairing key is regenerated or a phone is revoked. Windows follows that source behavior. Some older relay documentation and translated helper text describe preserving other phones; those descriptions do not match the current host implementation.
