import { API_BASE_URL } from "./config";
import type {
  ApiErrorBody,
  ExchangeResponse,
  LoginResponse,
  LogoutResponse,
  SessionResponse,
} from "./types";

export class ApiError extends Error {
  public readonly status: number;

  public constructor(status: number, body: ApiErrorBody | null) {
    super(body?.error || body?.message || `Request failed with status ${status}`);
    this.name = "ApiError";
    this.status = status;
  }
}

async function request<T>(path: string, init: RequestInit, token?: string): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set("Accept", "application/json");
  headers.set("Content-Type", "application/json");
  if (token) {
    headers.set("Authorization", `Bearer ${token}`);
  }

  const response = await fetch(`${API_BASE_URL}${path}`, { ...init, headers });
  let body: unknown = null;
  try {
    body = await response.json();
  } catch {
    body = null;
  }

  if (!response.ok) {
    throw new ApiError(response.status, body && typeof body === "object" ? (body as ApiErrorBody) : null);
  }

  return body as T;
}

export function login(challenge: string, state: string): Promise<LoginResponse> {
  return request<LoginResponse>("/api/mobile/login", {
    method: "POST",
    body: JSON.stringify({ challenge, state }),
  });
}

export function exchange(ticket: string, verifier: string): Promise<ExchangeResponse> {
  return request<ExchangeResponse>("/api/mobile/exchange", {
    method: "POST",
    body: JSON.stringify({ ticket, verifier }),
  });
}

export function profile(token: string): Promise<SessionResponse> {
  return request<SessionResponse>("/api/mobile/profile", { method: "GET" }, token);
}

export function refresh(token: string): Promise<SessionResponse> {
  return request<SessionResponse>("/api/mobile/refresh", { method: "POST" }, token);
}

export function logout(token: string): Promise<LogoutResponse> {
  return request<LogoutResponse>("/api/mobile/logout", { method: "POST" }, token);
}

export function isUnauthorized(error: unknown): boolean {
  return error instanceof ApiError && error.status === 401;
}

export function friendlyError(error: unknown): string {
  if (isUnauthorized(error)) return "登录状态已失效，请重新登录。";
  if (error instanceof ApiError) {
    if (error.status === 400) return "请求无效，请重试。";
    if (error.status === 429) return "操作过于频繁，请稍后再试。";
    if (error.status >= 500) return "服务暂时不可用，请稍后再试。";
  }
  if (error instanceof Error && error.message === "LOGIN_CANCELLED") return "登录已取消。";
  if (error instanceof Error && error.message === "LOGIN_FAILED") return "身份验证失败，请重新登录。";
  if (error instanceof Error && error.message === "CALLBACK_INVALID") return "登录回调无效，请重新登录。";
  return "网络连接失败，请检查设备与服务端是否在同一网络。";
}
