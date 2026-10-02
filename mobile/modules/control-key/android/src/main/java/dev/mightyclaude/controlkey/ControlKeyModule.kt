package dev.mightyclaude.controlkey

import android.app.KeyguardManager
import android.content.Context
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import expo.modules.kotlin.Promise
import expo.modules.kotlin.exception.CodedException
import expo.modules.kotlin.functions.Queues
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec

/**
 * The screen-share control key: an EC P-256 signing key that lives in the Android
 * Keystore and never leaves it. JavaScript only ever sees the public key (an ANSI X9.62
 * uncompressed point, which is what the Mac stores) and finished signatures.
 *
 * Every signature needs the phone's owner: the key is created with
 * `setUserAuthenticationRequired(true)` and per-use authentication, so the Keystore only
 * signs through a `BiometricPrompt` that carries the `Signature` as its `CryptoObject`.
 * On Android 11 and later the device PIN, pattern or password is accepted as well as a
 * strong biometric.
 *
 * The signature is `SHA256withECDSA` over the challenge bytes, DER encoded — ECDSA over
 * SHA-256(challenge), which is what the Mac's `ScreenSharePolicy.verifyControlSignature`
 * checks with CryptoKit.
 */
class ControlKeyModule : Module() {
  override fun definition() = ModuleDefinition {
    Name("MightyControlKey")

    Function("isSupported") { true }

    // The public half needs no authentication, so this never raises a prompt.
    AsyncFunction("publicKey") { alias: String ->
      val key = keyStore().getCertificate(alias)?.publicKey as? ECPublicKey
      key?.let { Base64.encodeToString(x962(it), Base64.NO_WRAP) }
    }

    // Creating the key does not prompt either; only using it does.
    AsyncFunction("generate") { alias: String ->
      requireAuthenticator()
      val spec = KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
        .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
        .setDigests(KeyProperties.DIGEST_SHA256)
        .setUserAuthenticationRequired(true)
        .setInvalidatedByBiometricEnrollment(true)
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
        spec.setUserAuthenticationParameters(
          0,
          KeyProperties.AUTH_BIOMETRIC_STRONG or KeyProperties.AUTH_DEVICE_CREDENTIAL,
        )
      } else {
        // -1 is "every use": before Android 11 only a strong biometric can unlock a
        // per-use key through a CryptoObject.
        @Suppress("DEPRECATION")
        spec.setUserAuthenticationValidityDurationSeconds(-1)
      }
      val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, KEYSTORE)
      try {
        generator.initialize(spec.build())
        val pair = generator.generateKeyPair()
        Base64.encodeToString(x962(pair.public as ECPublicKey), Base64.NO_WRAP)
      } catch (error: Exception) {
        throw CodedException("E_KEYSTORE", "The Keystore did not create the key.", error)
      }
    }

    AsyncFunction("remove") { alias: String ->
      val store = keyStore()
      if (store.containsAlias(alias)) store.deleteEntry(alias)
    }

    AsyncFunction("sign") { alias: String, challengeB64: String, title: String, cancel: String, promise: Promise ->
      sign(alias, challengeB64, title, cancel, promise)
    }.runOnQueue(Queues.MAIN)
  }

  private fun sign(alias: String, challengeB64: String, title: String, cancel: String, promise: Promise) {
    val activity = appContext.currentActivity as? FragmentActivity
      ?: return promise.reject("E_NO_ACTIVITY", "No activity to show the prompt over.", null)
    val challenge = try {
      Base64.decode(challengeB64, Base64.DEFAULT)
    } catch (error: IllegalArgumentException) {
      return promise.reject("E_BAD_CHALLENGE", "The challenge is not base64.", error)
    }
    val key = keyStore().getKey(alias, null) as? PrivateKey
      ?: return promise.reject("E_NO_KEY", "No control key for this host.", null)
    val signature = Signature.getInstance("SHA256withECDSA")
    try {
      signature.initSign(key)
    } catch (error: KeyPermanentlyInvalidatedException) {
      // A new fingerprint was enrolled or the screen lock was removed: the Keystore
      // has thrown this key away for good.
      return promise.reject("E_KEY_INVALIDATED", "The control key is no longer usable.", error)
    } catch (error: Exception) {
      return promise.reject("E_KEYSTORE", "The Keystore refused the key.", error)
    }

    val prompt = BiometricPrompt.PromptInfo.Builder()
      .setTitle(title)
      .setConfirmationRequired(false)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
      prompt.setAllowedAuthenticators(
        BiometricManager.Authenticators.BIOMETRIC_STRONG or
          BiometricManager.Authenticators.DEVICE_CREDENTIAL,
      )
    } else {
      prompt.setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG)
      prompt.setNegativeButtonText(cancel)
    }

    val callback = object : BiometricPrompt.AuthenticationCallback() {
      override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
        val unlocked = result.cryptoObject?.signature
          ?: return promise.reject("E_KEYSTORE", "The prompt returned no signature.", null)
        try {
          unlocked.update(challenge)
          promise.resolve(Base64.encodeToString(unlocked.sign(), Base64.NO_WRAP))
        } catch (error: Exception) {
          promise.reject("E_KEYSTORE", "The Keystore did not sign.", error)
        }
      }

      override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
        val code = when (errorCode) {
          BiometricPrompt.ERROR_USER_CANCELED,
          BiometricPrompt.ERROR_NEGATIVE_BUTTON,
          BiometricPrompt.ERROR_CANCELED -> "E_AUTH_CANCELLED"
          BiometricPrompt.ERROR_NO_BIOMETRICS,
          BiometricPrompt.ERROR_NO_DEVICE_CREDENTIAL,
          BiometricPrompt.ERROR_HW_NOT_PRESENT,
          BiometricPrompt.ERROR_HW_UNAVAILABLE -> "E_NO_AUTHENTICATOR"
          else -> "E_AUTH_FAILED"
        }
        promise.reject(code, errString.toString(), null)
      }
      // A finger that did not match keeps the prompt open; only the outcomes above end it.
    }

    try {
      BiometricPrompt(activity, ContextCompat.getMainExecutor(activity), callback)
        .authenticate(prompt.build(), BiometricPrompt.CryptoObject(signature))
    } catch (error: Exception) {
      promise.reject("E_AUTH_FAILED", "The prompt could not be shown.", error)
    }
  }

  /** A per-use key cannot exist without something to unlock it with. */
  private fun requireAuthenticator() {
    val context = appContext.reactContext ?: throw CodedException("E_NO_ACTIVITY", "No context.", null)
    val keyguard = context.getSystemService(Context.KEYGUARD_SERVICE) as KeyguardManager
    if (!keyguard.isDeviceSecure) {
      throw CodedException("E_NO_AUTHENTICATOR", "The phone has no screen lock.", null)
    }
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
      val status = BiometricManager.from(context)
        .canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG)
      if (status != BiometricManager.BIOMETRIC_SUCCESS) {
        throw CodedException("E_NO_AUTHENTICATOR", "No strong biometric is enrolled.", null)
      }
    }
  }

  private fun keyStore(): KeyStore = KeyStore.getInstance(KEYSTORE).apply { load(null) }

  /** `0x04 ‖ X ‖ Y`, each coordinate left-padded to 32 bytes. */
  private fun x962(key: ECPublicKey): ByteArray {
    val out = ByteArray(65)
    out[0] = 0x04
    writeCoordinate(key.w.affineX, out, 1)
    writeCoordinate(key.w.affineY, out, 33)
    return out
  }

  private fun writeCoordinate(value: BigInteger, out: ByteArray, offset: Int) {
    val bytes = value.toByteArray()
    val start = maxOf(0, bytes.size - 32)
    val length = bytes.size - start
    System.arraycopy(bytes, start, out, offset + 32 - length, length)
  }

  private companion object {
    const val KEYSTORE = "AndroidKeyStore"
  }
}
