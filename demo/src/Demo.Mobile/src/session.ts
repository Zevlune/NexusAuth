import * as SecureStore from "expo-secure-store";

const SESSION_TOKEN_KEY = "nexusauth.sessionToken";

export function readSessionToken(): Promise<string | null> {
  return SecureStore.getItemAsync(SESSION_TOKEN_KEY);
}

export function writeSessionToken(token: string): Promise<void> {
  return SecureStore.setItemAsync(SESSION_TOKEN_KEY, token);
}

export function deleteSessionToken(): Promise<void> {
  return SecureStore.deleteItemAsync(SESSION_TOKEN_KEY);
}
