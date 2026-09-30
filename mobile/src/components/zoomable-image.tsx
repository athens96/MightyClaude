import { useMemo, useRef, useState } from 'react';
import { Animated, Image, PixelRatio, StyleSheet, View, type LayoutChangeEvent } from 'react-native';
import { Gesture, GestureDetector } from 'react-native-gesture-handler';

/** How far a pinch may magnify the fitted image. */
const MAX_ZOOM = 8;
const DOUBLE_TAP_ZOOM = 2.5;

function clamp(value: number, low: number, high: number): number {
  return Math.min(high, Math.max(low, value));
}

/**
 * An image fitted to the space it is given, never drawn larger than its own pixels
 * (as the Mac pane fits), with pinch to zoom, drag while zoomed, and a double tap to
 * zoom in or back out. Gestures run on the JS thread through core `Animated`, like the
 * app's other gesture, so nothing here needs worklets.
 */
export function ZoomableImage({
  uri,
  pixelWidth,
  pixelHeight,
  accessibilityLabel,
}: {
  uri: string;
  pixelWidth: number;
  pixelHeight: number;
  accessibilityLabel: string;
}) {
  const [box, setBox] = useState({ width: 0, height: 0 });
  const scale = useRef(new Animated.Value(1)).current;
  const translateX = useRef(new Animated.Value(0)).current;
  const translateY = useRef(new Animated.Value(0)).current;
  const current = useRef({ scale: 1, x: 0, y: 0 });
  const start = useRef({ scale: 1, x: 0, y: 0 });

  // The thumbnail's own size in points: fitting never enlarges past it.
  const natural = {
    width: pixelWidth / PixelRatio.get(),
    height: pixelHeight / PixelRatio.get(),
  };
  const fit =
    box.width > 0 && box.height > 0 && natural.width > 0 && natural.height > 0
      ? Math.min(1, box.width / natural.width, box.height / natural.height)
      : 0;
  const shown = { width: natural.width * fit, height: natural.height * fit };
  const sizes = useRef({ box, shown });
  sizes.current = { box, shown };

  const gesture = useMemo(() => {
    const limit = (zoom: number, x: number, y: number) => {
      const { box: frame, shown: size } = sizes.current;
      const spareX = Math.max(0, (size.width * zoom - frame.width) / 2);
      const spareY = Math.max(0, (size.height * zoom - frame.height) / 2);
      return { x: clamp(x, -spareX, spareX), y: clamp(y, -spareY, spareY) };
    };
    const apply = (zoom: number, x: number, y: number, animated: boolean) => {
      const bounded = limit(zoom, x, y);
      current.current = { scale: zoom, ...bounded };
      if (!animated) {
        scale.setValue(zoom);
        translateX.setValue(bounded.x);
        translateY.setValue(bounded.y);
        return;
      }
      Animated.parallel([
        Animated.timing(scale, { toValue: zoom, duration: 180, useNativeDriver: true }),
        Animated.timing(translateX, { toValue: bounded.x, duration: 180, useNativeDriver: true }),
        Animated.timing(translateY, { toValue: bounded.y, duration: 180, useNativeDriver: true }),
      ]).start();
    };
    const begin = () => {
      start.current = { ...current.current };
    };
    const pinch = Gesture.Pinch()
      .runOnJS(true)
      .onStart(begin)
      .onUpdate((event) => {
        const zoom = clamp(start.current.scale * event.scale, 1, MAX_ZOOM);
        apply(zoom, start.current.x, start.current.y, false);
      });
    // Only a zoomed image is dragged; otherwise the touch is left to the screen
    // (the back swipe), the way the chat's pull gesture steps aside.
    const pan = Gesture.Pan()
      .runOnJS(true)
      .averageTouches(true)
      .manualActivation(true)
      .onTouchesMove((_event, state) => {
        if (current.current.scale > 1) state.activate();
        else state.fail();
      })
      .onStart(begin)
      .onUpdate((event) => {
        apply(
          current.current.scale,
          start.current.x + event.translationX,
          start.current.y + event.translationY,
          false,
        );
      });
    const doubleTap = Gesture.Tap()
      .runOnJS(true)
      .numberOfTaps(2)
      .onEnd(() => {
        if (current.current.scale > 1) apply(1, 0, 0, true);
        else apply(DOUBLE_TAP_ZOOM, 0, 0, true);
      });
    return Gesture.Race(doubleTap, Gesture.Simultaneous(pinch, pan));
  }, [scale, translateX, translateY]);

  const onLayout = (event: LayoutChangeEvent) => {
    const { width, height } = event.nativeEvent.layout;
    setBox({ width, height });
  };

  return (
    <GestureDetector gesture={gesture}>
      <View
        style={styles.frame}
        onLayout={onLayout}
        accessible
        accessibilityRole="image"
        accessibilityLabel={accessibilityLabel}
      >
        {fit > 0 ? (
          <Animated.View
            style={{
              height: shown.height,
              transform: [{ translateX }, { translateY }, { scale }],
              width: shown.width,
            }}
          >
            <Image source={{ uri }} style={styles.image} resizeMode="contain" />
          </Animated.View>
        ) : null}
      </View>
    </GestureDetector>
  );
}

const styles = StyleSheet.create({
  frame: { alignItems: 'center', flex: 1, justifyContent: 'center', overflow: 'hidden' },
  image: { height: '100%', width: '100%' },
});
