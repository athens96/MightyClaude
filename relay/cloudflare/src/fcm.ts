/** FCM HTTP v1 on Workers: ../src/fcm-core.ts with a WebCrypto RS256 signer. */

import { base64urlBytes, FcmHttpV1Core, parseServiceAccount, type PushSender, type Rs256Signer } from '../../src/fcm-core.ts';

/** The DER bytes of a PKCS#8 PEM ("-----BEGIN PRIVATE KEY-----", what Google's service account keys are). */
function pkcs8Der(pem: string): ArrayBuffer {
  const body = pem.replace(/-----(BEGIN|END) PRIVATE KEY-----/g, '').replace(/\s+/g, '');
  const binary = atob(body);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer;
}

/** A signer that imports the key once. */
export function webCryptoRs256(): Rs256Signer {
  let imported: { pem: string; key: Promise<CryptoKey> } | null = null;
  return async (input, privateKeyPem) => {
    if (imported === null || imported.pem !== privateKeyPem) {
      imported = {
        pem: privateKeyPem,
        key: crypto.subtle.importKey('pkcs8', pkcs8Der(privateKeyPem), { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' }, false, ['sign']),
      };
    }
    const signature = await crypto.subtle.sign('RSASSA-PKCS1-v1_5', await imported.key, new TextEncoder().encode(input));
    return base64urlBytes(new Uint8Array(signature));
  };
}

/** The sender the secret names; none (sending answers 503) when it is absent or does not parse. */
export function workerFcmSender(secret: string | undefined, log: (line: string) => void): PushSender | null {
  if (secret === undefined || secret.trim() === '') {
    log('[push] FCM disabled: no FCM_SERVICE_ACCOUNT');
    return null;
  }
  try {
    return new FcmHttpV1Core(parseServiceAccount(secret), webCryptoRs256(), undefined, undefined, log);
  } catch {
    log('[push] FCM disabled: service account unreadable');
    return null;
  }
}
