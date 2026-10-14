export const API_BASE_URL = (process.env.EXPO_PUBLIC_MOBILE_API_URL || "http://localhost:5401")
  .trim()
  .replace(/\/$/, "");

export const CALLBACK_URL = "nexusauth-demo://auth/callback";
