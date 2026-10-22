const assert = require('node:assert/strict');
const test = require('node:test');

const AUTH_PATH = require.resolve('../utils/auth');
const TOKEN_URL = '/connect/token';
const USERINFO_URL = '/connect/userinfo';
const APP_SCOPE = 'openid profile offline_access';

function createSession(overrides) {
  return Object.assign({
    accessToken: 'access-old',
    refreshToken: 'refresh-old',
    scope: APP_SCOPE,
    expiresAt: Date.now() + 5 * 60 * 1000,
    user: {
      sub: 'user-old',
      name: '已有昵称'
    }
  }, overrides || {});
}

function parseForm(data) {
  return Object.fromEntries(new URLSearchParams(data));
}

async function within(promise, label) {
  let timeout;
  try {
    return await Promise.race([
      promise,
      new Promise((resolve, reject) => {
        timeout = setTimeout(() => {
          reject(new Error(`${label} timed out`));
        }, 250);
      })
    ]);
  } finally {
    clearTimeout(timeout);
  }
}

function loadAuth(options) {
  const settings = options || {};
  let storedSession = settings.session === undefined ? null : settings.session;
  let loginCalls = 0;
  const requests = [];
  const tokenResponse = settings.tokenResponse || {
    access_token: 'access-new',
    refresh_token: 'refresh-new',
    expires_in: 300,
    scope: APP_SCOPE,
    user: {
      sub: 'user-1',
      name: '服务端昵称'
    }
  };

  global.wx = {
    getStorageSync() {
      return storedSession;
    },
    setStorageSync(key, value) {
      storedSession = value;
    },
    removeStorageSync() {
      storedSession = null;
    },
    login(loginOptions) {
      loginCalls += 1;
      loginOptions.success({ code: settings.loginCode || 'wx-code' });
    },
    request(requestOptions) {
      requests.push(requestOptions);

      if (settings.onRequest) {
        const handled = settings.onRequest(requestOptions, requests);
        if (handled) {
          return;
        }
      }

      if (requestOptions.url.endsWith(TOKEN_URL)) {
        requestOptions.success({
          statusCode: 200,
          data: tokenResponse
        });
        return;
      }

      if (requestOptions.url.endsWith(USERINFO_URL)) {
        requestOptions.success({
          statusCode: 200,
          data: {
            sub: 'user-info',
            name: 'UserInfo 昵称'
          }
        });
        return;
      }

      assert.fail(`Unexpected request: ${requestOptions.method} ${requestOptions.url}`);
    }
  };

  delete require.cache[AUTH_PATH];
  const auth = require(AUTH_PATH);

  return {
    auth,
    requests,
    get loginCalls() {
      return loginCalls;
    },
    getStoredSession() {
      return storedSession;
    }
  };
}

test.afterEach(() => {
  delete global.wx;
  delete require.cache[AUTH_PATH];
});

test('login sends one token request with the explicit nickname and caches the response user', async () => {
  const harness = loadAuth();
  const { auth, requests, getStoredSession } = harness;

  const session = await auth.login('  新昵称  ');
  const form = parseForm(requests[0].data);

  assert.equal(harness.loginCalls, 1);
  assert.equal(requests.length, 1);
  assert.match(requests[0].url, /\/connect\/token$/);
  assert.equal(requests[0].method, 'POST');
  assert.equal(requests[0].header['content-type'], 'application/x-www-form-urlencoded');
  assert.equal(form.grant_type, 'urn:nexusauth:grant-type:wechat_miniprogram');
  assert.equal(form.client_id, 'wechat-miniapp');
  assert.equal(form.code, 'wx-code');
  assert.equal(form.nickname, '新昵称');
  assert.equal(form.scope, APP_SCOPE);
  assert.equal(Object.hasOwn(form, 'refresh_token'), false);
  assert.equal(requests[0].header.Authorization, undefined);
  assert.deepEqual(session.user, {
    sub: 'user-1',
    name: '服务端昵称'
  });
  assert.deepEqual(getStoredSession().user, session.user);
});

test('concurrent confirmations share one wx.login and one token request', async () => {
  const harness = loadAuth();

  const first = authLogin(harness.auth, '并发昵称');
  const second = authLogin(harness.auth, '重复昵称');
  await Promise.all([first, second]);

  assert.equal(harness.loginCalls, 1);
  assert.equal(harness.requests.length, 1);
  assert.equal(parseForm(harness.requests[0].data).nickname, '并发昵称');
});

test('login treats a blank nickname as optional and omits it from the token form', async () => {
  const { auth, requests } = loadAuth();

  await auth.login('   ');
  const form = parseForm(requests[0].data);

  assert.equal(requests.length, 1);
  assert.equal(Object.hasOwn(form, 'nickname'), false);
});

test('login rejects an over-100-character nickname before network requests', async () => {
  const harness = loadAuth();
  const { auth, requests } = harness;

  await assert.rejects(
    auth.login('名'.repeat(101)),
    (error) => error.code === 'invalid_nickname'
  );

  assert.equal(harness.loginCalls, 0);
  assert.equal(requests.length, 0);
});

test('refresh sends no nickname and keeps the previous session user when user is absent', async () => {
  const previousSession = createSession();
  const tokenResponse = {
    access_token: 'access-refreshed',
    refresh_token: 'refresh-refreshed',
    expires_in: 300,
    scope: APP_SCOPE
  };
  const { auth, requests, getStoredSession } = loadAuth({
    session: previousSession,
    tokenResponse
  });

  const refreshed = await auth.refresh();
  const form = parseForm(requests[0].data);

  assert.equal(requests.length, 1);
  assert.equal(form.grant_type, 'refresh_token');
  assert.equal(form.refresh_token, 'refresh-old');
  assert.equal(Object.hasOwn(form, 'nickname'), false);
  assert.deepEqual(refreshed.user, previousSession.user);
  assert.deepEqual(getStoredSession().user, previousSession.user);
});

test('expired refresh token falls back to wx.login without reusing the cached nickname', async () => {
  const previousSession = createSession({ refreshToken: 'expired-refresh' });
  let tokenCalls = 0;
  const harness = loadAuth({
    session: previousSession,
    onRequest(requestOptions) {
      if (!requestOptions.url.endsWith(TOKEN_URL)) {
        return false;
      }

      tokenCalls += 1;
      if (tokenCalls === 1) {
        requestOptions.success({
          statusCode: 400,
          data: {
            error: 'invalid_grant',
            error_description: 'refresh token expired'
          }
        });
        return true;
      }

      requestOptions.success({
        statusCode: 200,
        data: {
          access_token: 'access-login',
          refresh_token: 'refresh-login',
          expires_in: 300,
          scope: APP_SCOPE,
          user: {
            sub: 'user-login',
            name: '自动登录用户'
          }
        }
      });
      return true;
    }
  });
  const { auth, requests, getStoredSession } = harness;

  const session = await within(auth.refresh(), 'automatic fallback login');
  const firstForm = parseForm(requests[0].data);
  const secondForm = parseForm(requests[1].data);

  assert.equal(harness.loginCalls, 1);
  assert.equal(requests.length, 2);
  assert.equal(firstForm.grant_type, 'refresh_token');
  assert.equal(Object.hasOwn(firstForm, 'nickname'), false);
  assert.equal(secondForm.grant_type, 'urn:nexusauth:grant-type:wechat_miniprogram');
  assert.equal(Object.hasOwn(secondForm, 'nickname'), false);
  assert.deepEqual(session.user, {
    sub: 'user-login',
    name: '自动登录用户'
  });
  assert.deepEqual(getStoredSession().user, session.user);
});

test('missing refresh token falls back to wx.login without hanging or sending a nickname', async () => {
  const harness = loadAuth({
    session: createSession({ refreshToken: null })
  });
  const { auth, requests, getStoredSession } = harness;

  const session = await within(auth.refresh(), 'missing-refresh-token login');
  const form = parseForm(requests[0].data);

  assert.equal(harness.loginCalls, 1);
  assert.equal(requests.length, 1);
  assert.equal(form.grant_type, 'urn:nexusauth:grant-type:wechat_miniprogram');
  assert.equal(form.code, 'wx-code');
  assert.equal(Object.hasOwn(form, 'nickname'), false);
  assert.equal(Object.hasOwn(form, 'refresh_token'), false);
  assert.deepEqual(session.user, {
    sub: 'user-1',
    name: '服务端昵称'
  });
  assert.deepEqual(getStoredSession().user, session.user);
});

test('getUserInfo refreshes once after unauthorized and excludes nickname from refresh', async () => {
  let userInfoCalls = 0;
  const { auth, requests } = loadAuth({
    session: createSession(),
    onRequest(requestOptions) {
      if (!requestOptions.url.endsWith(USERINFO_URL)) {
        return false;
      }

      userInfoCalls += 1;
      if (userInfoCalls === 1) {
        requestOptions.success({
          statusCode: 401,
          data: {
            error: 'invalid_token',
            error_description: 'expired'
          }
        });
        return true;
      }

      requestOptions.success({
        statusCode: 200,
        data: {
          sub: 'user-info',
          name: '恢复后的服务端昵称'
        }
      });
      return true;
    }
  });

  const user = await auth.getUserInfo();
  const refreshForm = parseForm(requests[1].data);

  assert.deepEqual(user, {
    sub: 'user-info',
    name: '恢复后的服务端昵称'
  });
  assert.deepEqual(
    requests.map((requestOptions) => {
      if (requestOptions.url.endsWith(USERINFO_URL)) {
        return 'userinfo';
      }
      if (requestOptions.url.endsWith(TOKEN_URL)) {
        return 'token';
      }
      return 'unexpected';
    }),
    ['userinfo', 'token', 'userinfo']
  );
  assert.equal(refreshForm.grant_type, 'refresh_token');
  assert.equal(Object.hasOwn(refreshForm, 'nickname'), false);
});

test('repeated 401 from getUserInfo clears the invalid session without retrying again', async () => {
  const { auth, requests, getStoredSession } = loadAuth({
    session: createSession(),
    onRequest(requestOptions) {
      if (!requestOptions.url.endsWith(USERINFO_URL)) {
        return false;
      }

      requestOptions.success({
        statusCode: 401,
        data: {
          error: 'invalid_token',
          error_description: 'invalid'
        }
      });
      return true;
    }
  });

  await assert.rejects(
    auth.getUserInfo(),
    (error) => error.statusCode === 401 && error.code === 'invalid_token'
  );

  assert.equal(
    requests.filter((requestOptions) => requestOptions.url.endsWith(USERINFO_URL)).length,
    2
  );
  assert.equal(getStoredSession(), null);
});

function authLogin(auth, nickname) {
  return auth.login(nickname);
}
