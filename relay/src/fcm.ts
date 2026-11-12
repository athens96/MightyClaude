import { createSign } from 'node:crypto';
import { readFile } from 'node:fs/promises';

import { FcmHttpV1Core, parseServiceAccount, type FetchLike, type Rs256Signer, type ServiceAccount } from './fcm-core.ts';

export { parseServiceAccount, type FetchLike, type PushSender, type PushSendResult, type ServiceAccount } from './fcm-core.ts';

/** Node's RS256 signer. */
export const nodeRs256: Rs256Signer = async (input, privateKeyPem) => {
  const signer = createSign('RSA-SHA256');
  signer.update(input);
  return signer.sign(privateKeyPem).toString('base64url');
};

/** FCM HTTP v1 on Node (fcm-core.ts with Node's signer). */
export class FcmHttpV1Sender extends FcmHttpV1Core {
  constructor(account: ServiceAccount, fetchImpl?: FetchLike, now?: () => number, log?: (line: string) => void) {
    super(account, nodeRs256, fetchImpl, now, log);
  }

  static async fromFile(path: string, fetchImpl?: FetchLike): Promise<FcmHttpV1Sender> {
    return new FcmHttpV1Sender(parseServiceAccount(await readFile(path, 'utf8')), fetchImpl);
  }
}
