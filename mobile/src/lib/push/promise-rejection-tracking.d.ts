// The `promise` library's unhandled-rejection tracker (a React Native dependency without typings),
// as src/lib/push/crash.ts uses it.
declare module 'promise/setimmediate/rejection-tracking' {
  interface RejectionTrackingOptions {
    allRejections?: boolean;
    onUnhandled?: (id: number, error: unknown) => void;
    onHandled?: (id: number, error: unknown) => void;
  }
  const tracking: { enable(options?: RejectionTrackingOptions): void; disable(): void };
  export default tracking;
}
