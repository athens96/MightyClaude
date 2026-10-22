package dev.mightyclaude.devicelock

import android.app.KeyguardManager
import android.content.Context
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition

/**
 * Whether the phone is locked right now. A notification button that answers a permission
 * request checks this first and does nothing while the phone is locked, because no notification
 * library exposes Android's own "authentication required" flag for actions.
 * Anything unknown counts as locked.
 */
class DeviceLockModule : Module() {
  override fun definition() = ModuleDefinition {
    Name("MightyDeviceLock")

    Function("isDeviceLocked") {
      val context = appContext.reactContext ?: return@Function true
      val keyguard = context.getSystemService(Context.KEYGUARD_SERVICE) as? KeyguardManager ?: return@Function true
      keyguard.isDeviceLocked
    }
  }
}
