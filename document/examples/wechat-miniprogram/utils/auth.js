const config = require('../config');

const STORAGE_KEY = 'nexusauth.wechat.miniprogram.session';
const EXPIRY_SKEW_MS = 60 * 1000;
const WECHAT_GRANT_TYPE = 'urn:nexusauth:grant-type:wechat_miniprogram';

let loginPromise = null;
let refreshPromise = null;

function createError(code, message, statusCode) {
  const error = new Error(message || '认证请求失败。');
  error.code = code || 'auth_error';
  if (statusCode) {
    error.statusCode = statusCode;
  }
  return error;
}

function isClearableError(error) {
  if (!error) {
    return false;
  }

  return error.statusCode === 401
    || error.code === 'invalid_grant'
    || error.code === 'invalid_token'
    || error.code === 'invalid_client';
}

function isUnauthorizedError(error) {
  if (!error || error.statusCode === 400) {
    return false;
  }

  return error.statusCode === 401 || error.code === 'invalid_token';
}

function encodeForm(data) {
  return Object.keys(data)
    .filter((key) => data[key] !== undefined && data[key] !== null)
    .map((key) => `${encodeURIComponent(key)}=${encodeURIComponent(data[key])}`)
    .join('&');
}

function request(options) {
  return new Promise((resolve, reject) => {
    wx.request({
      url: options.url,
      method: options.method,
      header: options.header,
      data: options.data,
      timeout: config.REQUEST_TIMEOUT_MS,
      success: resolve,
      fail: () => reject(createError('network_error', '网络请求失败，请稍后重试。'))
    });
  });
}

function parseBody(body) {
  if (typeof body !== 'string') {
    return body;
  }

  try {
    return JSON.parse(body);
  } catch (error) {
    return null;
  }
}

function responseError(response, fallbackMessage) {
  const body = parseBody(response.data);
  const code = body && typeof body.error === 'string' ? body.error : null;
  return createError(code || 'http_error', fallbackMessage, response.statusCode);
}

function normalizeUser(user, previousUser) {
  if (!user || typeof user !== 'object') {
    return previousUser ? normalizeUser(previousUser, null) : null;
  }

  const sub = typeof user.sub === 'string' ? user.sub : '';
  const name = typeof user.name === 'string' ? user.name : '';
  if (!sub || typeof user.name !== 'string') {
    return previousUser ? normalizeUser(previousUser, null) : null;
  }

  return { sub, name };
}

function readSession() {
  try {
    const saved = wx.getStorageSync(STORAGE_KEY);
    if (!saved || typeof saved !== 'object' || typeof saved.accessToken !== 'string') {
      return null;
    }
    return saved;
  } catch (error) {
    return null;
  }
}

function saveSession(tokenResponse, previous, requireRefreshToken) {
  const expiresIn = Number(tokenResponse.expires_in);
  if (typeof tokenResponse.access_token !== 'string' || !tokenResponse.access_token) {
    throw createError('invalid_response', 'Token 响应缺少 access_token。');
  }
  if (!Number.isFinite(expiresIn) || expiresIn <= 0) {
    throw createError('invalid_response', 'Token 响应缺少有效的 expires_in。');
  }

  const previousSession = previous || {};
  const refreshToken = typeof tokenResponse.refresh_token === 'string'
    ? tokenResponse.refresh_token
    : previousSession.refreshToken || null;
  if (requireRefreshToken && !refreshToken) {
    throw createError('invalid_response', '刷新响应缺少 refresh_token。');
  }

  const session = {
    accessToken: tokenResponse.access_token,
    refreshToken,
    scope: typeof tokenResponse.scope === 'string'
      ? tokenResponse.scope
      : previousSession.scope || config.SCOPE,
    expiresAt: Date.now() + expiresIn * 1000,
    user: normalizeUser(tokenResponse.user, previousSession.user)
  };

  wx.setStorageSync(STORAGE_KEY, session);
  return session;
}

function clearSession() {
  wx.removeStorageSync(STORAGE_KEY);
}

function hasSession() {
  return Boolean(readSession());
}

function getWeChatCode() {
  return new Promise((resolve, reject) => {
    wx.login({
      success: (result) => {
        if (typeof result.code === 'string' && result.code) {
          resolve(result.code);
          return;
        }
        reject(createError('wechat_login_failed', '微信登录未返回 code，请重试。'));
      },
      fail: () => reject(createError('wechat_login_failed', '微信登录失败，请重试。'))
    });
  });
}

async function postToken(form) {
  const response = await request({
    url: `${config.API_BASE.replace(/\/+$/, '')}/connect/token`,
    method: 'POST',
    header: {
      'content-type': 'application/x-www-form-urlencoded'
    },
    data: encodeForm(form)
  });

  if (response.statusCode >= 200 && response.statusCode < 300) {
    return parseBody(response.data) || {};
  }

  throw responseError(response, '换取 Token 失败，请稍后重试。');
}

function normalizeLoginNickname(nickname) {
  if (nickname === undefined || nickname === null) {
    return null;
  }
  if (typeof nickname !== 'string') {
    throw createError('invalid_nickname', '昵称格式不正确。');
  }

  const normalized = nickname.trim();
  if (!normalized) {
    return null;
  }
  if (Array.from(normalized).length > 100) {
    throw createError('invalid_nickname', '昵称去除首尾空格后不能超过 100 个字。');
  }

  return normalized;
}

async function executeLogin(nickname) {
  const code = await getWeChatCode();
  const form = {
    grant_type: WECHAT_GRANT_TYPE,
    client_id: config.CLIENT_ID,
    code,
    scope: config.SCOPE
  };
  if (nickname) {
    form.nickname = nickname;
  }

  const tokenResponse = await postToken(form);

  return saveSession(tokenResponse, null, false);
}

function createLoginPromise(nickname) {
  loginPromise = executeLogin(nickname)
    .catch((error) => {
      if (isClearableError(error)) {
        clearSession();
      }
      throw error;
    })
    .finally(() => {
      loginPromise = null;
    });

  return loginPromise;
}

function startLogin(nickname) {
  if (loginPromise) {
    return loginPromise;
  }
  if (refreshPromise) {
    return refreshPromise;
  }

  return createLoginPromise(nickname);
}

async function login(nickname) {
  return startLogin(normalizeLoginNickname(nickname));
}

function startLoginDuringRefresh() {
  if (loginPromise) {
    return loginPromise;
  }

  return createLoginPromise(null);
}

async function executeRefresh() {
  const current = readSession();
  if (!current || !current.refreshToken) {
    return startLoginDuringRefresh();
  }

  try {
    const tokenResponse = await postToken({
      grant_type: 'refresh_token',
      client_id: config.CLIENT_ID,
      refresh_token: current.refreshToken
    });

    return saveSession(tokenResponse, current, true);
  } catch (error) {
    if (isClearableError(error)) {
      clearSession();
      return startLoginDuringRefresh();
    }
    throw error;
  }
}

function refresh() {
  if (refreshPromise) {
    return refreshPromise;
  }
  if (loginPromise) {
    return loginPromise;
  }

  refreshPromise = executeRefresh().finally(() => {
    refreshPromise = null;
  });

  return refreshPromise;
}

async function ensureFreshSession() {
  const current = readSession();
  if (!current) {
    return login();
  }

  if (current.expiresAt - Date.now() <= EXPIRY_SKEW_MS) {
    return refresh();
  }

  return current;
}

async function withAuthenticatedRequest(action) {
  let session = await ensureFreshSession();

  try {
    return await action(session);
  } catch (error) {
    if (!isUnauthorizedError(error)) {
      throw error;
    }

    try {
      session = await refresh();
      return await action(session);
    } catch (retryError) {
      if (isUnauthorizedError(retryError)) {
        clearSession();
      }
      throw retryError;
    }
  }
}

async function requestUserInfo(session) {
  const response = await request({
    url: `${config.API_BASE.replace(/\/+$/, '')}/connect/userinfo`,
    method: 'GET',
    header: {
      Authorization: `Bearer ${session.accessToken}`
    }
  });

  if (response.statusCode >= 200 && response.statusCode < 300) {
    return parseBody(response.data) || {};
  }

  throw responseError(response, '读取用户信息失败，请稍后重试。');
}

function toDisplayUser(userInfo) {
  return {
    sub: typeof userInfo.sub === 'string' ? userInfo.sub : '',
    name: typeof userInfo.name === 'string' ? userInfo.name : ''
  };
}

async function getUserInfo() {
  const userInfo = await withAuthenticatedRequest(requestUserInfo);
  return toDisplayUser(userInfo);
}

module.exports = {
  login,
  refresh,
  getUserInfo,
  clearSession,
  hasSession
};
