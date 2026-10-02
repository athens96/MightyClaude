import { useEffect, useMemo, useRef, useState, type ComponentType } from 'react';
import { Platform, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import * as ScreenOrientation from 'expo-screen-orientation';
import { Gesture, GestureDetector } from 'react-native-gesture-handler';
import type { RTCVideoViewProps } from 'react-native-webrtc/lib/typescript/RTCView';
import { BetaBadge, Button, Card, Chip, EmptyState, ErrorBanner } from '@/components/ui';
import { useCapabilities } from '@/hooks/use-capabilities';
import { useScreenMeasurement } from '@/hooks/use-screen-measurement';
import { useScreenShare } from '@/hooks/use-screen-share';
import { t } from '@/lib/i18n';
import { screenShareRefusal, screenShareVisibility } from '@/lib/screen-share/availability';
import { mayEnrolControlKey } from '@/lib/screen-share/control-key';
import { createDragTracker } from '@/lib/screen-share/drag';
import {
  containRect,
  contentAspect,
  overviewRect,
  stagePointToDisplay,
  type Rect,
  type Size,
} from '@/lib/screen-share/geometry';
import type { NormalizedPoint } from '@/lib/screen-share/input';
import { isScreenSessionActive, mayRequestSession } from '@/lib/screen-share/session';
import {
  candidatePathText,
  clipboardMovedText,
  clipboardRefusalText,
  controlFailureText,
  controlKeyEnrolFailureText,
  controlKeyStatusText,
  measurementLines,
  measurementMarkerNote,
  screenHiddenText,
  screenHoldDetail,
  screenHoldTitle,
  screenModeLabel,
  screenRefusalText,
  screenStartLabel,
  screenStoppedText,
} from '@/lib/screen-share/strings';
import { isZoomed, type ZoomRegion } from '@/lib/screen-share/zoom';
import { useHostsStore } from '@/store/hosts';
import { useHostClient } from '@/store/live';
import { spacing, useStyles, type Palette } from '@/theme';

/** How often a pinch in progress asks the Mac for a new region. */
const ZOOM_SEND_INTERVAL_MS = 150;

/**
 * `RTCView`, loaded only on Android: the iOS build leaves `react-native-webrtc` out of
 * autolinking, and importing it there would throw before this screen could say why it is
 * not available.
 */
function rtcView(): ComponentType<RTCVideoViewProps> | undefined {
  if (Platform.OS !== 'android') return undefined;
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  return (require('react-native-webrtc') as { RTCView: ComponentType<RTCVideoViewProps> }).RTCView;
}

interface StageGeometry {
  content: Rect;
  zoom: ZoomRegion;
}

/** A point inside the picture, pulled in from a bar: drags never end off the display. */
function clampToContent(point: { x: number; y: number }, content: Rect) {
  return {
    x: Math.min(content.x + content.width, Math.max(content.x, point.x)),
    y: Math.min(content.y + content.height, Math.max(content.y, point.y)),
  };
}

/**
 * 화면 보기 — the one screen that may turn sideways. The app is portrait everywhere else,
 * so landscape is unlocked on the way in and locked back on the way out.
 *
 * Everything that decides what may happen here is on the Mac: the allow-list, the view and
 * control grants, the signature it demands before control, the idle ceilings, the lock
 * screen, the kill switch. This screen asks, shows what came back, and says plainly when
 * the answer was no.
 */
export default function RemoteScreenScreen() {
  const styles = useStyles(makeStyles);
  const { hostId } = useLocalSearchParams<{ hostId: string }>();
  const host = useHostsStore((state) => state.hosts.find((entry) => entry.id === hostId));
  const client = useHostClient(hostId);
  const capabilities = useCapabilities(hostId, client);
  const visibility = screenShareVisibility({ platform: Platform.OS, capabilities });
  const clientId = useHostsStore((state) =>
    hostId ? (state.clientIds[hostId] ?? state.clientId) : state.clientId,
  );
  const { controller, snapshot } = useScreenShare(
    visibility.visible ? hostId : undefined,
    client,
  );
  const RTCView = useMemo(() => (visibility.visible ? rtcView() : undefined), [visibility.visible]);

  const [size, setSize] = useState<Size>({ width: 1, height: 1 });
  const [videoSize, setVideoSize] = useState<Size | undefined>(undefined);
  const [draft, setDraft] = useState('');
  const [measuring, setMeasuring] = useState(false);

  // Landscape belongs to this screen alone; every other screen stays portrait.
  useEffect(() => {
    if (!visibility.visible) return undefined;
    void ScreenOrientation.unlockAsync();
    return () => {
      void ScreenOrientation.lockAsync(ScreenOrientation.OrientationLock.PORTRAIT_UP);
    };
  }, [visibility.visible]);

  const session = snapshot?.session;
  const streamUrl = snapshot?.streamUrl;
  const live = session?.phase === 'live';
  const controlling = live && session?.mode === 'control';
  const zoom = snapshot?.zoom;

  const measure = useScreenMeasurement(controller, {
    enabled: measuring,
    live: live === true,
    exportTitle: t('phone.screenShare.measure.exportTitle'),
  });
  // The tap gesture is built once per session; it reaches the latest probe through here.
  const markTap = useRef(measure.markTap);
  markTap.current = measure.markTap;

  // A new stream starts with no known frame size; the view reports it with the first frame.
  useEffect(() => setVideoSize(undefined), [streamUrl]);

  const display = session?.displays.find((entry) => entry.displayId === session.displayId);
  const content = useMemo(() => {
    const aspect = contentAspect({ video: videoSize, display, region: zoom, stage: size });
    return containRect(size, aspect);
  }, [display, size, videoSize, zoom]);

  // The gesture callbacks read the latest picture position from here, so the gestures do
  // not have to be rebuilt (and lose a drag in progress) whenever a frame changes size.
  const geometry = useRef<StageGeometry | undefined>(undefined);
  geometry.current = zoom ? { content, zoom } : undefined;

  const gesture = useMemo(() => {
    if (!controller || !live) return undefined;
    const toDisplay = (x: number, y: number): NormalizedPoint | undefined => {
      const current = geometry.current;
      return current ? stagePointToDisplay({ x, y }, current.content, current.zoom) : undefined;
    };
    const toDisplayClamped = (x: number, y: number): NormalizedPoint | undefined => {
      const current = geometry.current;
      if (!current) return undefined;
      return stagePointToDisplay(clampToContent({ x, y }, current.content), current.content, current.zoom);
    };

    // Pinch works watching too: the Mac follows it when nobody else is driving. The scale
    // carries on from the last pinch instead of starting over at 1×.
    let pinchBase = 1;
    let pinchCentre: NormalizedPoint = { x: 0.5, y: 0.5 };
    let lastZoomSentAt = 0;
    const pinch = Gesture.Pinch()
      .runOnJS(true)
      .onStart((event) => {
        pinchBase = controller.snapshot().zoomScale;
        pinchCentre = toDisplayClamped(event.focalX, event.focalY) ?? { x: 0.5, y: 0.5 };
        lastZoomSentAt = 0;
      })
      .onUpdate((event) => {
        const now = Date.now();
        if (now - lastZoomSentAt < ZOOM_SEND_INTERVAL_MS) return;
        lastZoomSentAt = now;
        controller.setZoom(pinchBase * event.scale, pinchCentre);
      })
      .onEnd((event) => controller.setZoom(pinchBase * event.scale, pinchCentre));

    if (!controlling) return pinch;

    const tap = Gesture.Tap()
      .runOnJS(true)
      .onEnd((event) => {
        const point = toDisplay(event.x, event.y);
        if (point && controller.tap(point)) markTap.current();
      });
    const longPress = Gesture.LongPress()
      .runOnJS(true)
      .onStart((event) => {
        const point = toDisplay(event.x, event.y);
        if (point) controller.rightClick(point);
      });

    // A drag goes out as begin → move… → end, and only once the pan has really started
    // (onStart, not onBegin): a tap must never press the mouse button. onFinalize runs
    // however the gesture ends, so every `begin` gets its `end`.
    const tracker = createDragTracker((point, phase) => controller.drag(point, phase));
    const drag = Gesture.Pan()
      .runOnJS(true)
      .maxPointers(1)
      .onStart((event) =>
        // Where the finger first landed, not where the pan was recognised.
        tracker.begin(toDisplay(event.x - event.translationX, event.y - event.translationY)),
      )
      .onUpdate((event) => tracker.move(toDisplayClamped(event.x, event.y)))
      .onFinalize((event) => tracker.finish(toDisplayClamped(event.x, event.y)));

    // Two fingers scroll. The deltas go out as a fraction of the picture, so the Mac
    // scrolls by the same share of the display whatever the phone's pixel density is.
    let lastX = 0;
    let lastY = 0;
    const scroll = Gesture.Pan()
      .runOnJS(true)
      .minPointers(2)
      .onStart(() => {
        lastX = 0;
        lastY = 0;
      })
      .onUpdate((event) => {
        const current = geometry.current;
        if (!current) return;
        const point = toDisplayClamped(event.x, event.y);
        if (!point) return;
        const dx = ((event.translationX - lastX) / Math.max(1, current.content.width)) * current.zoom.width;
        const dy = ((event.translationY - lastY) / Math.max(1, current.content.height)) * current.zoom.height;
        lastX = event.translationX;
        lastY = event.translationY;
        controller.scroll(point, { dx, dy });
      });

    return Gesture.Race(pinch, scroll, drag, longPress, tap);
  }, [controller, controlling, live]);

  if (!host) {
    return (
      <View style={styles.screen}>
        <Stack.Screen options={{ title: t('phone.screenShare.title') }} />
        <EmptyState
          title={t('phone.workspaces.hostNotFound.title')}
          description={t('phone.workspaces.hostNotFound.description')}
        />
      </View>
    );
  }

  if (!visibility.visible) {
    return (
      <View style={styles.screen}>
        <Stack.Screen options={{ title: t('phone.screenShare.title') }} />
        <EmptyState
          title={t('phone.screenShare.title')}
          description={screenHiddenText(visibility.hiddenBecause ?? 'capability')}
        />
      </View>
    );
  }

  const gate = (mode: 'view' | 'control') =>
    session
      ? screenShareRefusal({
          allowed: session.allowed,
          grant: session.grant,
          mode,
          ...(clientId === undefined ? {} : { clientId }),
        })
      : undefined;
  const refusalNow = gate('view');
  const atRest = session ? mayRequestSession(session) : false;
  const key = snapshot?.controlKey;
  const keyText = key ? controlKeyStatusText(key.status) : undefined;
  const showKey = session?.allowed === true && session.grant === 'control' && key !== undefined;
  const zoomed = zoom ? isZoomed(zoom) : false;

  const statusText = (() => {
    if (!session) return t('phone.screenShare.status.idle');
    switch (session.phase) {
      case 'starting':
        return t('phone.screenShare.status.starting');
      case 'connecting':
        return t('phone.screenShare.status.connecting');
      case 'live':
        return t('phone.screenShare.status.live');
      case 'ended':
        return session.stopReason
          ? screenStoppedText(session.stopReason)
          : t('phone.screenShare.status.idle');
      default:
        return t('phone.screenShare.status.idle');
    }
  })();

  const overview = zoom && zoomed ? overviewRect(content, zoom) : undefined;
  const send = () => {
    controller?.sendText(draft);
    setDraft('');
  };

  const picture =
    streamUrl && RTCView ? (
      <>
        {/* The overview: the whole display at low resolution, scaled so the zoomed
            region on it lies exactly under the sharp picture. Zooming never blows up a
            shrunken frame; the sharp track carries the region at full quality. */}
        {overview && snapshot?.overviewUrl ? (
          <View
            pointerEvents="none"
            style={[
              styles.overview,
              { height: overview.height, left: overview.x, top: overview.y, width: overview.width },
            ]}
          >
            <RTCView
              objectFit="contain"
              streamURL={snapshot.overviewUrl}
              style={styles.fill}
              zOrder={0}
            />
          </View>
        ) : null}
        <RTCView
          objectFit="contain"
          streamURL={streamUrl}
          style={styles.fill}
          zOrder={1}
          onDimensionsChange={(event) =>
            setVideoSize({
              width: event.nativeEvent.width,
              height: event.nativeEvent.height,
            })
          }
        />
      </>
    ) : (
      <View style={styles.placeholder}>
        <Text style={styles.placeholderText}>
          {session?.hold ? screenHoldTitle(session.hold) : statusText}
        </Text>
        {session?.hold && screenHoldDetail(session.hold) ? (
          <Text style={styles.placeholderDetail}>{screenHoldDetail(session.hold)}</Text>
        ) : null}
      </View>
    );

  return (
    <View style={styles.screen}>
      <Stack.Screen options={{ title: t('phone.screenShare.title') }} />

      {snapshot?.loadFailed ? <ErrorBanner message={t('phone.screenShare.loadFailed')} /> : null}
      {session?.refusal ? <ErrorBanner message={screenRefusalText(session.refusal)} /> : null}
      {snapshot?.controlFailure ? (
        <ErrorBanner message={controlFailureText(snapshot.controlFailure)} />
      ) : null}
      {snapshot?.clipboardRefusal ? (
        <ErrorBanner message={clipboardRefusalText(snapshot.clipboardRefusal)} />
      ) : null}

      <View
        style={styles.stage}
        onLayout={(event) =>
          setSize({
            width: event.nativeEvent.layout.width,
            height: event.nativeEvent.layout.height,
          })
        }
      >
        {gesture ? (
          <GestureDetector gesture={gesture}>
            <View style={styles.fill}>{picture}</View>
          </GestureDetector>
        ) : (
          picture
        )}
        {measuring && live ? (
          <View pointerEvents="none" style={styles.measure}>
            {measurementLines(measure.summary).map((line) => (
              <Text key={line} style={styles.measureText}>
                {line}
              </Text>
            ))}
          </View>
        ) : null}
      </View>

      <ScrollView contentContainerStyle={styles.panel} keyboardShouldPersistTaps="handled">
        <View style={styles.row}>
          <Chip label={t('phone.screenShare.title')} beta />
          {session ? <Chip label={screenModeLabel(session.mode)} selected={live} /> : null}
          {snapshot?.candidateType ? (
            <Chip label={candidatePathText(snapshot.candidateType)} />
          ) : null}
          {live ? (
            <Chip
              label={t('phone.screenShare.measure.toggle')}
              selected={measuring}
              onPress={() => setMeasuring((on) => !on)}
            />
          ) : null}
          {live && zoomed ? (
            <Chip label={t('phone.screenShare.zoom.reset')} onPress={() => controller?.resetZoom()} />
          ) : null}
          <BetaBadge />
        </View>

        <Text style={styles.status}>{statusText}</Text>

        {measuring ? (
          <View style={styles.card}>
            <Text style={styles.detail}>{measurementMarkerNote()}</Text>
            <Button
              label={t('phone.screenShare.measure.export')}
              compact
              onPress={() => void measure.exportReport()}
            />
          </View>
        ) : null}

        {session?.quality ? (
          <Text style={styles.detail}>
            {t('phone.screenShare.quality', {
              width: session.quality.width,
              height: session.quality.height,
              fps: session.quality.fps,
              kbps: session.quality.maxBitrateKbps,
            })}
          </Text>
        ) : null}

        {refusalNow ? <Text style={styles.detail}>{screenRefusalText(refusalNow)}</Text> : null}
        {snapshot?.clipboardMoved ? (
          <Text style={styles.detail}>{clipboardMovedText(snapshot.clipboardMoved)}</Text>
        ) : null}

        {session && session.displays.length > 0 ? (
          <Card style={styles.card}>
            <Text style={styles.cardTitle}>{t('phone.screenShare.display.title')}</Text>
            <View style={styles.row}>
              {session.displays.map((entry) => {
                const chosen = live
                  ? session.displayId
                  : (snapshot?.selectedDisplayId ??
                    session.displays.find((item) => item.main)?.displayId);
                return (
                  <Chip
                    key={entry.displayId}
                    label={
                      entry.main
                        ? t('phone.screenShare.display.main')
                        : t('phone.screenShare.display.item', { id: entry.displayId })
                    }
                    selected={entry.displayId === chosen}
                    // Live switches the capture; at rest it picks where the next session
                    // starts. Nothing happens while a session is still starting.
                    disabled={!live && !atRest}
                    onPress={() => controller?.selectDisplay(entry.displayId)}
                  />
                );
              })}
            </View>
          </Card>
        ) : null}

        {showKey && key ? (
          <Card style={styles.card}>
            <Text style={styles.cardTitle}>{t('phone.screenShare.key.title')}</Text>
            {keyText ? <Text style={styles.detail}>{keyText}</Text> : null}
            {key.phoneFingerprint ? (
              <Text style={styles.fingerprint}>
                {t('phone.screenShare.key.fingerprint', { fingerprint: key.phoneFingerprint })}
              </Text>
            ) : null}
            {key.enrolling ? (
              <>
                <Text style={styles.detail}>{t('phone.screenShare.key.enrolling')}</Text>
                <Text style={styles.detail}>{t('phone.screenShare.key.confirmHint')}</Text>
              </>
            ) : null}
            {key.enrolFailure ? (
              <Text style={styles.warning}>{controlKeyEnrolFailureText(key.enrolFailure)}</Text>
            ) : null}
            {mayEnrolControlKey(key.status) ? (
              <Button
                label={t('phone.screenShare.key.enrol')}
                compact
                busy={key.enrolling}
                disabled={key.enrolling}
                onPress={() => void controller?.enrolControlKey()}
              />
            ) : null}
          </Card>
        ) : null}

        <View style={styles.row}>
          {session && isScreenSessionActive(session) ? (
            <Button
              label={t('phone.screenShare.stop')}
              tone="danger"
              onPress={() => controller?.stop()}
            />
          ) : (
            <>
              <Button
                label={screenStartLabel('view')}
                busy={snapshot?.busy === true}
                disabled={!session || !atRest || gate('view') !== undefined}
                onPress={() => void controller?.start('view')}
              />
              <Button
                label={screenStartLabel('control')}
                tone="primary"
                busy={snapshot?.busy === true}
                disabled={
                  !session ||
                  !atRest ||
                  gate('control') !== undefined ||
                  key?.status !== 'ready'
                }
                onPress={() => void controller?.start('control')}
              />
            </>
          )}
        </View>

        {controlling ? (
          <Card style={styles.card}>
            {/* Korean goes out as a committed string: the IME finishes the syllable in
                this field and the whole text is sent, never a jamo at a time. Nothing typed
                here may be remembered, suggested or offered to an autofill service. */}
            <TextInput
              style={styles.input}
              value={draft}
              placeholder={t('phone.screenShare.keyboard.placeholder')}
              onChangeText={setDraft}
              onSubmitEditing={send}
              returnKeyType="send"
              autoCorrect={false}
              autoComplete="off"
              autoCapitalize="none"
              importantForAutofill="no"
              spellCheck={false}
            />
            <View style={styles.row}>
              <Button
                label={t('phone.screenShare.keyboard.send')}
                compact
                disabled={draft.length === 0}
                onPress={send}
              />
              <Button
                label={t('phone.screenShare.shortcut.copy')}
                compact
                onPress={() => controller?.shortcut('cmd+c')}
              />
              <Button
                label={t('phone.screenShare.shortcut.paste')}
                compact
                onPress={() => controller?.shortcut('cmd+v')}
              />
            </View>
            <View style={styles.row}>
              <Chip label={t('phone.screenShare.keys.escape')} onPress={() => controller?.shortcut('escape')} />
              <Chip label={t('phone.screenShare.keys.tab')} onPress={() => controller?.shortcut('tab')} />
              <Chip label={t('phone.screenShare.keys.return')} onPress={() => controller?.shortcut('return')} />
              <Chip label={t('phone.screenShare.keys.backspace')} onPress={() => controller?.shortcut('backspace')} />
              <Chip label={t('phone.screenShare.keys.left')} onPress={() => controller?.shortcut('left')} />
              <Chip label={t('phone.screenShare.keys.up')} onPress={() => controller?.shortcut('up')} />
              <Chip label={t('phone.screenShare.keys.down')} onPress={() => controller?.shortcut('down')} />
              <Chip label={t('phone.screenShare.keys.right')} onPress={() => controller?.shortcut('right')} />
              <Chip label={t('phone.screenShare.keys.interrupt')} onPress={() => controller?.shortcut('ctrl+c')} />
            </View>
            <View style={styles.row}>
              {/* Manual only: nothing is synced in the background, either way. */}
              <Button
                label={t('phone.screenShare.clipboard.toMac')}
                compact
                onPress={() => void controller?.pasteToMac()}
              />
              <Button
                label={t('phone.screenShare.clipboard.toPhone')}
                compact
                onPress={() => controller?.copyFromMac()}
              />
            </View>
            <Text style={styles.detail}>{t('phone.screenShare.hint.control')}</Text>
          </Card>
        ) : null}

        <Text style={styles.detail}>{t('phone.screenShare.hint.zoom')}</Text>
      </ScrollView>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    stage: { backgroundColor: '#000', flex: 1, minHeight: 180, overflow: 'hidden' },
    fill: { bottom: 0, left: 0, position: 'absolute', right: 0, top: 0 },
    overview: { opacity: 0.45, position: 'absolute' },
    placeholder: { alignItems: 'center', flex: 1, justifyContent: 'center', padding: spacing.md },
    placeholderText: { color: palette.textMuted, textAlign: 'center' },
    placeholderDetail: {
      color: palette.textMuted,
      fontSize: 12,
      marginTop: spacing.xs,
      textAlign: 'center',
    },
    panel: { gap: spacing.sm, padding: spacing.md },
    row: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    status: { color: palette.text, fontWeight: '600' },
    detail: { color: palette.textMuted, fontSize: 12 },
    warning: { color: palette.danger, fontSize: 12 },
    fingerprint: { color: palette.text, fontFamily: 'monospace', fontSize: 13 },
    measure: {
      backgroundColor: 'rgba(0,0,0,0.6)',
      borderRadius: 6,
      left: spacing.xs,
      padding: spacing.xs,
      position: 'absolute',
      top: spacing.xs,
    },
    measureText: { color: '#fff', fontFamily: 'monospace', fontSize: 11 },
    card: { gap: spacing.xs },
    cardTitle: { color: palette.text, fontWeight: '600' },
    input: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: 8,
      borderWidth: StyleSheet.hairlineWidth,
      color: palette.text,
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.xs,
    },
  });
