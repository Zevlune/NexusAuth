import * as WebBrowser from "expo-web-browser";
import { useCallback, useEffect, useRef, useState } from "react";
import { AppState, type AppStateStatus } from "react-native";
import {
  ActivityIndicator,
  Pressable,
  SafeAreaView,
  ScrollView,
  StatusBar,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { API_BASE_URL, CALLBACK_URL } from "./src/config";
import { exchange, friendlyError, isUnauthorized, login, logout, profile, refresh } from "./src/api";
import { createPkcePair } from "./src/crypto";
import { deleteSessionToken, readSessionToken, writeSessionToken } from "./src/session";
import type { OidcUser, SessionResponse } from "./src/types";

WebBrowser.maybeCompleteAuthSession();

type ScreenState = "loading" | "loggedOut" | "loggedIn";

function validateCallback(urlString: string, expectedState: string): { ticket: string } {
  let url: URL;
  try {
    url = new URL(urlString);
  } catch {
    throw new Error("CALLBACK_INVALID");
  }

  if (url.protocol !== "nexusauth-demo:" || url.hostname !== "auth" || url.port || url.username || url.password || url.pathname !== "/callback" || url.hash) {
    throw new Error("CALLBACK_INVALID");
  }
  const queryKeys = Array.from(url.searchParams.keys());
  if (new Set(queryKeys).size !== queryKeys.length || queryKeys.some((key) => key !== "state" && key !== "ticket" && key !== "error")) {
    throw new Error("CALLBACK_INVALID");
  }
  if (url.searchParams.get("state") !== expectedState) {
    throw new Error("CALLBACK_INVALID");
  }
  const errorCode = url.searchParams.get("error");
  const ticket = url.searchParams.get("ticket");
  if (errorCode) {
    if (errorCode !== "login_failed" || ticket) throw new Error("CALLBACK_INVALID");
    throw new Error("LOGIN_FAILED");
  }

  if (!ticket) throw new Error("CALLBACK_INVALID");
  return { ticket };
}

function displayName(user: OidcUser): string {
  return user.name || user.preferred_username || user.email || user.sub;
}

function formatExpiry(expiresAt: string): string {
  const date = new Date(expiresAt);
  return Number.isNaN(date.getTime()) ? expiresAt : date.toLocaleString();
}

export default function App() {
  const [screenState, setScreenState] = useState<ScreenState>("loading");
  const [user, setUser] = useState<OidcUser | null>(null);
  const [expiresAt, setExpiresAt] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [restoreFailed, setRestoreFailed] = useState(false);
  const [busy, setBusy] = useState(false);
  const mountedRef = useRef(true);
  const busyRef = useRef(false);

  const setSession = useCallback((session: SessionResponse) => {
    if (!mountedRef.current) return;
    setUser(session.user);
    setExpiresAt(session.expiresAt);
    setScreenState("loggedIn");
  }, []);

  const clearSession = useCallback(async (message?: string) => {
    await deleteSessionToken();
    if (!mountedRef.current) return;
    setUser(null);
    setExpiresAt(null);
    setScreenState("loggedOut");
    if (message !== undefined) setNotice(message);
  }, []);

  const runExclusive = useCallback(async (operation: () => Promise<void>) => {
    if (busyRef.current) return;
    busyRef.current = true;
    if (mountedRef.current) {
      setBusy(true);
      setNotice(null);
    }
    try {
      await operation();
    } catch (error) {
      if (isUnauthorized(error)) {
        await clearSession("登录状态已失效，请重新登录。");
      } else if (mountedRef.current) {
        setNotice(friendlyError(error));
      }
    } finally {
      busyRef.current = false;
      if (mountedRef.current) setBusy(false);
    }
  }, [clearSession]);

  const restoreSession = useCallback(async () => {
    if (busyRef.current) return;
    busyRef.current = true;
    if (mountedRef.current) setBusy(true);
    try {
      const token = await readSessionToken();
      if (!token) {
        if (mountedRef.current) {
          setScreenState("loggedOut");
          setRestoreFailed(false);
        }
        return;
      }
      const session = await profile(token);
      setSession(session);
      if (mountedRef.current) setRestoreFailed(false);
    } catch (error) {
      if (isUnauthorized(error)) {
        await clearSession();
      } else if (mountedRef.current) {
        setNotice(friendlyError(error));
        setScreenState("loggedOut");
        setRestoreFailed(true);
      }
    } finally {
      busyRef.current = false;
      if (mountedRef.current) setBusy(false);
    }
  }, [clearSession, setSession]);

  useEffect(() => {
    mountedRef.current = true;
    void restoreSession();
    return () => {
      mountedRef.current = false;
    };
  }, [restoreSession]);

  useEffect(() => {
    let refreshTimer: ReturnType<typeof setInterval> | undefined;
    const refreshIfActive = (status: AppStateStatus) => {
      if (status !== "active" || busyRef.current || screenState !== "loggedIn") return;
      void runExclusive(async () => {
        const token = await readSessionToken();
        if (!token) {
          await clearSession();
          return;
        }
        setSession(await profile(token));
      });
    };
    const subscription = AppState.addEventListener("change", refreshIfActive);
    if (screenState === "loggedIn") {
      refreshTimer = setInterval(() => refreshIfActive("active"), 5 * 60 * 1000);
    }
    return () => {
      subscription.remove();
      if (refreshTimer) clearInterval(refreshTimer);
    };
  }, [clearSession, runExclusive, screenState, setSession]);

  const handleLogin = useCallback(() => runExclusive(async () => {
    const pkce = await createPkcePair();
    const authorization = await login(pkce.challenge, pkce.state);
    const result = await WebBrowser.openAuthSessionAsync(authorization.authorizationUrl, CALLBACK_URL, {
      preferEphemeralSession: false,
    });
    if (result.type !== "success" || !result.url) throw new Error("LOGIN_CANCELLED");
    const { ticket } = validateCallback(result.url, pkce.state);
    const session = await exchange(ticket, pkce.verifier);
    await writeSessionToken(session.sessionToken);
    const current = await profile(session.sessionToken);
    setSession(current);
  }), [runExclusive, setSession]);

  const handleRefresh = useCallback(() => runExclusive(async () => {
    const token = await readSessionToken();
    if (!token) {
      await clearSession();
      return;
    }
    const session = await refresh(token);
    setSession(session);
  }), [clearSession, runExclusive, setSession]);

  const handleLogout = useCallback(() => runExclusive(async () => {
    const token = await readSessionToken();
    let revocationConfirmed = false;
    try {
      if (token) revocationConfirmed = (await logout(token)).revoked;
    } finally {
      await clearSession(revocationConfirmed ? undefined : "已退出本机登录，但服务端未确认撤销会话。");
    }
  }), [clearSession, runExclusive]);

  const isLoading = screenState === "loading";
  const isLoggedIn = screenState === "loggedIn" && user !== null;

  return (
    <SafeAreaView style={styles.safeArea}>
      <StatusBar barStyle="dark-content" />
      <ScrollView contentContainerStyle={styles.content}>
        <View style={styles.header}>
          <View style={styles.logo}><Text style={styles.logoText}>N</Text></View>
          <View>
            <Text style={styles.eyebrow}>NEXUSAUTH</Text>
            <Text style={styles.title}>移动端登录演示</Text>
          </View>
        </View>

        <View style={styles.hero}>
          <Text style={styles.heroTitle}>把登录交给浏览器</Text>
          <Text style={styles.heroBody}>使用系统浏览器完成身份验证，应用只保存短期会话令牌。</Text>
        </View>

        <View style={styles.statusCard}>
          <View style={styles.statusRow}>
            <Text style={styles.sectionLabel}>当前状态</Text>
            {isLoggedIn && <View style={styles.onlinePill}><View style={styles.onlineDot} /><Text style={styles.onlineText}>已连接</Text></View>}
          </View>
          {isLoading ? (
            <View style={styles.loadingRow}><ActivityIndicator color="#1463DF" /><Text style={styles.statusText}>正在恢复登录状态…</Text></View>
          ) : isLoggedIn ? (
            <>
              <Text style={styles.userName}>{displayName(user)}</Text>
              <Text style={styles.userMeta}>{user.email || user.preferred_username || user.sub}</Text>
              {expiresAt && <Text style={styles.expiry}>会话有效期至 {formatExpiry(expiresAt)}</Text>}
            </>
          ) : (
            <Text style={styles.statusText}>尚未登录，点击下方按钮开始。</Text>
          )}
        </View>

        {notice && <View style={styles.notice}><Text style={styles.noticeText}>{notice}</Text></View>}

        <View style={styles.actions}>
          {!isLoggedIn ? (
            <Pressable disabled={busy || isLoading} onPress={handleLogin} style={({ pressed }) => [styles.primaryButton, (busy || isLoading) && styles.disabledButton, pressed && styles.pressed]}>
              {busy ? <ActivityIndicator color="#FFFFFF" /> : <Text style={styles.primaryButtonText}>使用浏览器登录</Text>}
            </Pressable>
          ) : (
            <>
              <Pressable disabled={busy} onPress={handleRefresh} style={({ pressed }) => [styles.primaryButton, busy && styles.disabledButton, pressed && styles.pressed]}>
                {busy ? <ActivityIndicator color="#FFFFFF" /> : <Text style={styles.primaryButtonText}>刷新会话</Text>}
              </Pressable>
              <Pressable disabled={busy} onPress={handleLogout} style={({ pressed }) => [styles.secondaryButton, busy && styles.disabledSecondary, pressed && styles.pressed]}>
                <Text style={styles.secondaryButtonText}>退出登录</Text>
              </Pressable>
            </>
          )}
          {!isLoggedIn && restoreFailed && <Pressable disabled={busy} onPress={restoreSession} style={({ pressed }) => [styles.secondaryButton, busy && styles.disabledSecondary, pressed && styles.pressed]}><Text style={styles.secondaryButtonText}>重试恢复会话</Text></Pressable>}
        </View>

        <View style={styles.infoSection}>
          <Text style={styles.sectionLabel}>连接配置</Text>
          <View style={styles.infoRow}><Text style={styles.infoKey}>API 地址</Text><Text style={styles.infoValue} numberOfLines={1}>{API_BASE_URL}</Text></View>
          <View style={styles.infoRow}><Text style={styles.infoKey}>回调地址</Text><Text style={styles.infoValue} numberOfLines={1}>{CALLBACK_URL}</Text></View>
          <Text style={styles.infoHint}>真机调试时，请确保设备与服务端处于同一局域网，并把 API 地址配置为电脑的局域网 IP。</Text>
        </View>

        <Text style={styles.footer}>NexusAuth · OAuth 2.0 / PKCE</Text>
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: { flex: 1, backgroundColor: "#F4F7FB" },
  content: { padding: 24, paddingBottom: 36 },
  header: { flexDirection: "row", alignItems: "center", gap: 14, marginTop: 12, marginBottom: 28 },
  logo: { width: 46, height: 46, borderRadius: 14, backgroundColor: "#1463DF", alignItems: "center", justifyContent: "center" },
  logoText: { color: "#FFFFFF", fontSize: 26, fontWeight: "800" },
  eyebrow: { color: "#1463DF", fontSize: 11, fontWeight: "800", letterSpacing: 1.5 },
  title: { color: "#101828", fontSize: 21, fontWeight: "700", marginTop: 3 },
  hero: { backgroundColor: "#102A56", borderRadius: 22, padding: 22, marginBottom: 18 },
  heroTitle: { color: "#FFFFFF", fontSize: 25, fontWeight: "800", marginBottom: 8 },
  heroBody: { color: "#C9D8F0", fontSize: 15, lineHeight: 23 },
  statusCard: { backgroundColor: "#FFFFFF", borderRadius: 18, padding: 20, shadowColor: "#183B68", shadowOpacity: 0.08, shadowRadius: 12, shadowOffset: { width: 0, height: 5 }, elevation: 2 },
  statusRow: { flexDirection: "row", justifyContent: "space-between", alignItems: "center", marginBottom: 14 },
  sectionLabel: { color: "#667085", fontSize: 13, fontWeight: "700", letterSpacing: 0.4 },
  onlinePill: { flexDirection: "row", alignItems: "center", gap: 6, backgroundColor: "#E8F7EE", borderRadius: 20, paddingHorizontal: 10, paddingVertical: 5 },
  onlineDot: { width: 7, height: 7, borderRadius: 4, backgroundColor: "#18A957" },
  onlineText: { color: "#08753A", fontSize: 12, fontWeight: "700" },
  loadingRow: { flexDirection: "row", alignItems: "center", gap: 10 },
  statusText: { color: "#667085", fontSize: 15, lineHeight: 22 },
  userName: { color: "#101828", fontSize: 23, fontWeight: "800", marginBottom: 4 },
  userMeta: { color: "#475467", fontSize: 14 },
  expiry: { color: "#98A2B3", fontSize: 12, marginTop: 14 },
  notice: { backgroundColor: "#FFF4E5", borderRadius: 12, padding: 13, marginTop: 14, borderWidth: 1, borderColor: "#FFD59A" },
  noticeText: { color: "#8A4B08", fontSize: 14, lineHeight: 20 },
  actions: { gap: 11, marginTop: 18 },
  primaryButton: { minHeight: 54, borderRadius: 14, backgroundColor: "#1463DF", alignItems: "center", justifyContent: "center", paddingHorizontal: 18 },
  primaryButtonText: { color: "#FFFFFF", fontSize: 16, fontWeight: "800" },
  secondaryButton: { minHeight: 50, borderRadius: 14, backgroundColor: "#E9EFF9", alignItems: "center", justifyContent: "center", paddingHorizontal: 18 },
  secondaryButtonText: { color: "#184B99", fontSize: 15, fontWeight: "700" },
  disabledButton: { opacity: 0.55 },
  disabledSecondary: { opacity: 0.55 },
  pressed: { opacity: 0.8 },
  infoSection: { marginTop: 30, backgroundColor: "#FFFFFF", borderRadius: 18, padding: 19 },
  infoRow: { flexDirection: "row", alignItems: "center", paddingVertical: 13, borderBottomWidth: 1, borderBottomColor: "#EEF2F6", gap: 14 },
  infoKey: { width: 66, color: "#667085", fontSize: 13 },
  infoValue: { flex: 1, color: "#344054", fontSize: 13, textAlign: "right" },
  infoHint: { color: "#98A2B3", fontSize: 12, lineHeight: 19, marginTop: 14 },
  footer: { color: "#98A2B3", fontSize: 12, textAlign: "center", marginTop: 28 },
});
