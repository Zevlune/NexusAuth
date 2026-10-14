# NexusAuth Mobile Demo

This is a native iOS/Android Expo development build for the NexusAuth mobile login flow. It is not intended for Expo Go or the web. The app uses the `nexusauth-demo://auth/callback` scheme and OAuth 2.0 Authorization Code with PKCE.

## Configure and run

```bash
cp .env.example .env
npm install
npx expo prebuild
npm run ios
# or
npm run android
```

Use a development build (`npm run ios` or `npm run android`) so the native callback scheme and SecureStore plugin are installed. For a physical device, set `EXPO_PUBLIC_MOBILE_API_URL` in `.env` to the development computer's LAN URL, for example `http://192.168.1.20:5401`, and keep the device and API server on the same network. The API server must use the same issuer configuration as the identity provider used by the web flow.

Run the TypeScript check with `npm run typecheck`.

The app never collects a password or contains a client secret. The PKCE verifier remains in memory during the browser round trip, and only the session token is stored in SecureStore.
