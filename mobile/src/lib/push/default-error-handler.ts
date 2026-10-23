/**
 * React Native's own fatal-error handler, taken before Crashlytics installs its own (index.js
 * imports this first), so the app still fails the normal way after the scrubbed report.
 */
export const defaultErrorHandler = ErrorUtils.getGlobalHandler();
