export type OidcUser = {
  sub: string;
  name?: string;
  preferred_username?: string;
  email?: string;
};

export type LoginResponse = {
  authorizationUrl: string;
};

export type ExchangeResponse = {
  sessionToken: string;
  expiresAt: string;
  user: OidcUser;
};

export type SessionResponse = {
  user: OidcUser;
  expiresAt: string;
  refreshed?: boolean;
};

export type LogoutResponse = {
  revoked: boolean;
};

export type ApiErrorBody = {
  error?: string;
  message?: string;
};
