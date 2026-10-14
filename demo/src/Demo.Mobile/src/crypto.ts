import * as Crypto from "expo-crypto";

const BASE64_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

function bytesToBase64Url(bytes: Uint8Array): string {
  let encoded = "";
  for (let index = 0; index < bytes.length; index += 3) {
    const first = bytes[index];
    const second = index + 1 < bytes.length ? bytes[index + 1] : 0;
    const third = index + 2 < bytes.length ? bytes[index + 2] : 0;
    const block = (first << 16) | (second << 8) | third;
    encoded += BASE64_CHARS[(block >>> 18) & 63];
    encoded += BASE64_CHARS[(block >>> 12) & 63];
    encoded += index + 1 < bytes.length ? BASE64_CHARS[(block >>> 6) & 63] : "=";
    encoded += index + 2 < bytes.length ? BASE64_CHARS[block & 63] : "=";
  }

  return encoded.replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function base64ToBase64Url(value: string): string {
  return value.replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

export async function createPkcePair(): Promise<{ verifier: string; challenge: string; state: string }> {
  const verifierBytes = await Crypto.getRandomBytesAsync(32);
  const stateBytes = await Crypto.getRandomBytesAsync(32);
  const verifier = bytesToBase64Url(verifierBytes);
  const state = bytesToBase64Url(stateBytes);
  const digest = await Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, verifier, {
    encoding: Crypto.CryptoEncoding.BASE64,
  });

  return { verifier, challenge: base64ToBase64Url(digest), state };
}
