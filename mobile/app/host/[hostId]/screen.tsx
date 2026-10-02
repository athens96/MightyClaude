import { useCallback, useEffect, useMemo, useState } from 'react';
import { Platform, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import * as ScreenOrientation from 'expo-screen-orientation';
import { Gesture, GestureDetector } from 'react-native-gesture-handler';
import { RTCView } from 'react-native-webrtc';
import { BetaBadge, Button, Card, Chip, EmptyState, ErrorBanner } from '@/components/ui';
import { useCapabilities } from '@/hooks/use-capabilities';
import { useScreenShare } from '@/hooks/use-screen-share';
import { t } from '@/lib/i18n';
import { screenShareRefusal, screenShareVisibility } from '@/lib/screen-share/availability';
import {
  candidatePathText,
  clipboardMovedText,
  clipboardRefusalText,
  controlFailureText,
  screenHiddenText,
  screenHoldDetail,
  screenHoldTitle,
  screenModeLabel,
  screenRefusalText,
  screenStartLabel,
  screenStoppedText,
} from '@/lib/screen-share/strings';
import { isScreenSessionActive } from '@/lib/screen-share/session';
import { pointInRegion } from '@/lib/screen-share/zoom';
import { useHostsStore } from '@/store/hosts';
import { useHostClient } from '@/store/live';
import { spacing, useStyles, type Palette } from '@/theme';

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

  const [size, setSize] = useState({ width: 1, height: 1 });
  const [draft, setDraft] = useState('');

  // Landscape belongs to this screen alone; every other screen stays portrait.
  useEffect(() => {
    if (!visibility.visible) return undefined;
    void ScreenOrientation.unlockAsync();
    return () => {
      void ScreenOrientation.lockAsync(ScreenOrientation.OrientationLock.PORTRAIT_UP);
    };
  }, [visibility.visible]);

  const session = snapshot?.session;
  const live = session?.phase === 'live';
  const controlling = live && session?.mode === 'control';
  const zoom = snapshot?.zoom;

  const toDisplayPoint = useCallback(
    (x: number, y: number) => {
      const local = { x: x / Math.max(1, size.width), y: y / Math.max(1, size.height) };
      return zoom ? pointInRegion(zoom, local) : local;
    },
    [size.height, size.width, zoom],
  );

  const gesture = useMemo(() => {
    if (!controller || !controlling) return undefined;
    const tap = Gesture.Tap()
      .runOnJS(true)
      .onEnd((event) => controller.tap(toDisplayPoint(event.x, event.y)));
    const longPress = Gesture.LongPress()
      .runOnJS(true)
      .onStart((event) => controller.rightClick(toDisplayPoint(event.x, event.y)));
    const drag = Gesture.Pan()
      .runOnJS(true)
      .maxPointers(1)
      .onBegin((event) => controller.drag(toDisplayPoint(event.x, event.y), 'begin'))
      .onUpdate((event) => controller.drag(toDisplayPoint(event.x, event.y), 'move'))
      .onEnd((event) => controller.drag(toDisplayPoint(event.x, event.y), 'end'));
    // Two fingers scroll. The deltas go out normalized, so the Mac scrolls by the same
    // fraction of the display whatever the phone's pixel density is.
    let lastX = 0;
    let lastY = 0;
    const scroll = Gesture.Pan()
      .runOnJS(true)
      .minPointers(2)
      .onBegin(() => {
        lastX = 0;
        lastY = 0;
      })
      .onUpdate((event) => {
        const dx = (event.translationX - lastX) / Math.max(1, size.width);
        const dy = (event.translationY - lastY) / Math.max(1, size.height);
        lastX = event.translationX;
        lastY = event.translationY;
        controller.scroll(toDisplayPoint(event.x, event.y), { dx, dy });
      });
    const pinch = Gesture.Pinch()
      .runOnJS(true)
      .onUpdate((event) =>
        controller.setZoom(event.scale, toDisplayPoint(event.focalX, event.focalY)),
      );
    return Gesture.Race(pinch, scroll, drag, longPress, tap);
  }, [controller, controlling, size.height, size.width, toDisplayPoint]);

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

  const refusalNow = session
    ? screenShareRefusal({
        allowed: session.allowed,
        grant: session.grant,
        mode: 'view',
        ...(clientId === undefined ? {} : { clientId }),
      })
    : undefined;

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

  return (
    <View style={styles.screen}>
      <Stack.Screen options={{ title: t('phone.screenShare.title') }} />

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
        {/* The overview layer: the whole display at low resolution, under the zoomed
            region the Mac streams sharp. Zooming never blows up a shrunken frame. */}
        {snapshot?.streamUrl ? (
          <>
            <RTCView
              objectFit="contain"
              streamURL={snapshot.streamUrl}
              style={styles.overview}
              zOrder={0}
            />
            {gesture ? (
              <GestureDetector gesture={gesture}>
                <View style={styles.sharp}>
                  <RTCView
                    objectFit="contain"
                    streamURL={snapshot.streamUrl}
                    style={styles.sharp}
                    zOrder={1}
                  />
                </View>
              </GestureDetector>
            ) : (
              <RTCView
                objectFit="contain"
                streamURL={snapshot.streamUrl}
                style={styles.sharp}
                zOrder={1}
              />
            )}
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
        )}
      </View>

      <ScrollView contentContainerStyle={styles.panel} keyboardShouldPersistTaps="handled">
        <View style={styles.row}>
          <Chip label={t('phone.screenShare.title')} beta />
          {session ? <Chip label={screenModeLabel(session.mode)} selected={live} /> : null}
          {snapshot?.candidateType ? (
            <Chip label={candidatePathText(snapshot.candidateType)} />
          ) : null}
          <BetaBadge />
        </View>

        <Text style={styles.status}>{statusText}</Text>

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
              {session.displays.map((display) => (
                <Chip
                  key={display.displayId}
                  label={
                    display.main
                      ? t('phone.screenShare.display.main')
                      : t('phone.screenShare.display.item', { id: display.displayId })
                  }
                  selected={display.displayId === session.displayId}
                  onPress={
                    live
                      ? () => controller?.switchDisplay(display.displayId)
                      : () => controller?.start(session.mode, display.displayId)
                  }
                />
              ))}
            </View>
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
                disabled={
                  !session ||
                  screenShareRefusal({
                    allowed: session.allowed,
                    grant: session.grant,
                    mode: 'view',
                    ...(clientId === undefined ? {} : { clientId }),
                  }) !== undefined
                }
                onPress={() => void controller?.start('view')}
              />
              <Button
                label={screenStartLabel('control')}
                tone="primary"
                busy={snapshot?.busy === true}
                disabled={
                  !session ||
                  screenShareRefusal({
                    allowed: session.allowed,
                    grant: session.grant,
                    mode: 'control',
                    ...(clientId === undefined ? {} : { clientId }),
                  }) !== undefined
                }
                onPress={() => void controller?.start('control')}
              />
            </>
          )}
        </View>

        {controlling ? (
          <Card style={styles.card}>
            {/* Korean goes out as a committed string: the IME finishes the syllable in
                this field and the whole text is sent, never a jamo at a time. */}
            <TextInput
              style={styles.input}
              value={draft}
              placeholder={t('phone.screenShare.keyboard.placeholder')}
              onChangeText={setDraft}
              onSubmitEditing={() => {
                controller?.sendText(draft);
                setDraft('');
              }}
              returnKeyType="send"
            />
            <View style={styles.row}>
              <Button
                label={t('phone.screenShare.keyboard.send')}
                compact
                disabled={draft.length === 0}
                onPress={() => {
                  controller?.sendText(draft);
                  setDraft('');
                }}
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
    overview: { bottom: 0, left: 0, opacity: 0.35, position: 'absolute', right: 0, top: 0 },
    sharp: { flex: 1 },
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
