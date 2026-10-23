const assert = require('node:assert/strict');
const test = require('node:test');

const AUTH_PATH = require.resolve('../utils/auth');
const PAGE_PATH = require.resolve('../pages/index/index');

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function loadPage(auth) {
  const previousAuthModule = require.cache[AUTH_PATH];
  require.cache[AUTH_PATH] = {
    id: AUTH_PATH,
    filename: AUTH_PATH,
    loaded: true,
    exports: auth
  };

  let definition = null;
  global.Page = (pageDefinition) => {
    definition = pageDefinition;
  };

  delete require.cache[PAGE_PATH];
  require(PAGE_PATH);

  if (previousAuthModule) {
    require.cache[AUTH_PATH] = previousAuthModule;
  } else {
    delete require.cache[AUTH_PATH];
  }
  delete global.Page;
  delete require.cache[PAGE_PATH];

  assert.ok(definition, 'Page should receive a page definition');

  return Object.assign({}, definition, {
    data: structuredClone(definition.data),
    setData(updates) {
      Object.assign(this.data, updates);
    }
  });
}

function createAuth(overrides) {
  return Object.assign({
    hasSession() {
      return true;
    },
    async login() {
      return {
        user: {
          sub: 'user-1',
          name: '小林'
        }
      };
    },
    async getUserInfo() {
      return { sub: 'user-1', name: '小林' };
    },
    async refresh() {
      return {};
    },
    clearSession() {}
  }, overrides || {});
}

function submitLogin(page, nickname) {
  return page.onLogin({
    detail: {
      value: {
        nickname
      }
    }
  });
}

test.afterEach(() => {
  delete global.Page;
  delete require.cache[PAGE_PATH];
  delete require.cache[AUTH_PATH];
});

test('opening defaults the displayed user name and cancel sends no request', async () => {
  let loginCalls = 0;
  let userInfoReads = 0;
  let refreshCalls = 0;
  const page = loadPage(createAuth({
    async login() {
      loginCalls += 1;
      return {};
    },
    async getUserInfo() {
      userInfoReads += 1;
      return {};
    },
    async refresh() {
      refreshCalls += 1;
      return {};
    }
  }));
  page.data.user = { sub: 'user-1', name: '已有昵称' };

  page.onLoginTap();

  assert.equal(page.data.loginDialogVisible, true);
  assert.equal(page.data.nicknameValue, '已有昵称');
  page.onLoginCancel();
  await Promise.resolve();

  assert.equal(page.data.loginDialogVisible, false);
  assert.equal(loginCalls, 0);
  assert.equal(userInfoReads, 0);
  assert.equal(refreshCalls, 0);
});

test('confirm calls auth.login once, hides the old user, and displays the response user directly', async () => {
  const loginResult = deferred();
  const loginArguments = [];
  let userInfoReads = 0;
  const page = loadPage(createAuth({
    async login(nickname) {
      loginArguments.push(nickname);
      return loginResult.promise;
    },
    async getUserInfo() {
      userInfoReads += 1;
      return { sub: 'unexpected', name: '不应读取' };
    }
  }));
  page.data.user = { sub: 'old-user', name: '旧用户' };
  page.onLoginTap();

  const loginPromise = submitLogin(page, '  新昵称  ');
  const duplicatePromise = submitLogin(page, '重复提交');
  page.onLoginCancel();

  assert.equal(duplicatePromise, undefined);
  assert.deepEqual(loginArguments, ['新昵称']);
  assert.equal(page.data.user, null);
  assert.equal(page.data.busy, true);
  assert.equal(page.data.loginDialogVisible, true);

  loginResult.resolve({
    user: {
      sub: 'new-user',
      name: '服务端返回昵称'
    }
  });
  await loginPromise;

  assert.equal(userInfoReads, 0);
  assert.deepEqual(page.data.user, {
    sub: 'new-user',
    name: '服务端返回昵称'
  });
  assert.equal(page.data.nicknameValue, '服务端返回昵称');
  assert.equal(page.data.status, '微信登录成功');
  assert.equal(page.data.loginDialogVisible, false);
  assert.equal(page.data.busy, false);
});

test('login failure keeps the dialog and submitted nickname draft for retry', async () => {
  let sessionClears = 0;
  const page = loadPage(createAuth({
    async login() {
      const error = new Error('网络请求失败，请稍后重试。');
      error.code = 'network_error';
      throw error;
    },
    clearSession() {
      sessionClears += 1;
    }
  }));
  page.onLoginTap();

  await submitLogin(page, '  待重试昵称  ');

  assert.equal(sessionClears, 0);
  assert.equal(page.data.loginDialogVisible, true);
  assert.equal(page.data.user, null);
  assert.equal(page.data.nicknameValue, '  待重试昵称  ');
  assert.equal(page.data.nicknameError, '网络请求失败，请稍后重试。');
  assert.equal(page.data.status, '网络请求失败，请稍后重试。');
  assert.equal(page.data.busy, false);
});

test('unknown nickname stays blank and login uses the account default', async () => {
  let receivedNickname = 'not-called';
  const page = loadPage(createAuth({
    async login(nickname) {
      receivedNickname = nickname;
      return {
        user: {
          sub: 'user-1',
          name: '默认昵称'
        }
      };
    }
  }));
  page.onLoginTap();

  assert.equal(page.data.nicknameValue, '');
  await submitLogin(page, '   ');

  assert.equal(receivedNickname, undefined);
  assert.equal(page.data.user.name, '默认昵称');
});

test('old Host responses without user retain the UserInfo fallback', async () => {
  let userInfoReads = 0;
  const page = loadPage(createAuth({
    async login() {
      return {
        accessToken: 'access-token',
        user: null
      };
    },
    async getUserInfo() {
      userInfoReads += 1;
      return { sub: 'legacy-user', name: '旧 Host 用户' };
    }
  }));
  page.onLoginTap();

  await submitLogin(page, '');

  assert.equal(userInfoReads, 1);
  assert.deepEqual(page.data.user, {
    sub: 'legacy-user',
    name: '旧 Host 用户'
  });
  assert.equal(page.data.loginDialogVisible, false);
});

test('form submit reads the latest nickname and allows a passed native review', async () => {
  let receivedNickname = null;
  const page = loadPage(createAuth({
    async login(nickname) {
      receivedNickname = nickname;
      return {
        user: {
          sub: 'user-1',
          name: nickname || '未提交昵称'
        }
      };
    }
  }));
  page.onLoginTap();

  page.onNicknameInput({ detail: { value: '输入事件值' } });
  page.onNicknameBlur({ detail: { value: '失焦值' } });
  page.onNicknameReview({
    detail: {
      pass: true,
      timeout: false
    }
  });
  await submitLogin(page, '  表单最终值  ');

  assert.equal(receivedNickname, '表单最终值');
  assert.equal(page.data.nicknameValue, '表单最终值');
});

test('rejected or timed-out WeChat nickname reviews block the stale draft', () => {
  let loginCalls = 0;
  const page = loadPage(createAuth({
    async login() {
      loginCalls += 1;
      return {};
    }
  }));
  page.onLoginTap();

  page.onNicknameInput({ detail: { value: '未通过审核昵称' } });
  page.onNicknameReview({
    detail: {
      pass: false,
      timeout: false
    }
  });

  assert.equal(page.data.nicknameValue, '');
  assert.equal(page.data.nicknameReviewStatus, 'failed');
  assert.equal(page.data.nicknameError, '该微信昵称未通过审核，请重新选择或手动填写昵称。');

  submitLogin(page, '未通过审核昵称');
  assert.equal(loginCalls, 0);

  page.onNicknameInput({ detail: { value: '审核超时昵称' } });
  page.onNicknameReview({
    detail: {
      pass: false,
      timeout: true
    }
  });

  assert.equal(page.data.nicknameValue, '');
  assert.equal(page.data.nicknameReviewStatus, 'timeout');
  assert.equal(page.data.nicknameError, '微信昵称审核超时，请点击输入框重试，或手动填写昵称。');

  submitLogin(page, '审核超时昵称');
  assert.equal(loginCalls, 0);
});

test('over-100-Unicode-code-point nickname does not start login', () => {
  let loginCalls = 0;
  const page = loadPage(createAuth({
    async login() {
      loginCalls += 1;
      return {};
    }
  }));
  page.onLoginTap();
  const tooLong = '😀'.repeat(101);

  submitLogin(page, tooLong);

  assert.equal(loginCalls, 0);
  assert.equal(page.data.nicknameError, '昵称去除首尾空格后不能超过 100 个字。');
  assert.equal(page.data.nicknameValue, tooLong);
  assert.equal(page.data.loginDialogVisible, true);
});

test('onLoad restores an existing session through UserInfo', async () => {
  let loginCalls = 0;
  let reads = 0;
  const page = loadPage(createAuth({
    async login() {
      loginCalls += 1;
      return {};
    },
    async getUserInfo() {
      reads += 1;
      return {
        sub: 'restored-user',
        name: '已登录用户'
      };
    }
  }));
  page.data.user = { sub: 'old-user', name: '旧用户' };
  page.data.loginDialogVisible = true;

  await page.onLoad();

  assert.equal(loginCalls, 0);
  assert.equal(reads, 1);
  assert.deepEqual(page.data.user, {
    sub: 'restored-user',
    name: '已登录用户'
  });
  assert.equal(page.data.nicknameValue, '已登录用户');
  assert.equal(page.data.loginDialogVisible, false);
  assert.equal(page.data.status, '已恢复登录并读取用户信息');
  assert.equal(page.data.busy, false);
});

test('onLoad without a session clears the page and dialog state', () => {
  const page = loadPage(createAuth({
    hasSession() {
      return false;
    }
  }));
  page.data.user = { sub: 'old-user', name: '旧用户' };
  page.data.loginDialogVisible = true;
  page.data.nicknameValue = '旧草稿';

  page.onLoad();

  assert.equal(page.data.user, null);
  assert.equal(page.data.loginDialogVisible, false);
  assert.equal(page.data.nicknameValue, '');
  assert.equal(page.data.status, '未登录');
});

test('refresh rereads UserInfo and never submits the nickname draft', async () => {
  let loginCalls = 0;
  let refreshCalls = 0;
  const page = loadPage(createAuth({
    async login() {
      loginCalls += 1;
      return {};
    },
    async refresh() {
      refreshCalls += 1;
      return {};
    },
    async getUserInfo() {
      return { sub: 'current-user', name: '当前用户' };
    }
  }));
  page.data.user = { sub: 'old-user', name: '旧用户' };
  page.data.nicknameValue = '不应提交的草稿';

  await page.onRefresh();

  assert.equal(loginCalls, 0);
  assert.equal(refreshCalls, 1);
  assert.deepEqual(page.data.user, {
    sub: 'current-user',
    name: '当前用户'
  });
  assert.equal(page.data.nicknameValue, '当前用户');
  assert.equal(page.data.status, '登录状态与用户信息已刷新');
});

test('clear session resets user, nickname, and dialog state', () => {
  let sessionClears = 0;
  const page = loadPage(createAuth({
    clearSession() {
      sessionClears += 1;
    }
  }));
  page.data.user = { sub: 'user-1', name: '当前用户' };
  page.data.loginDialogVisible = true;
  page.data.nicknameValue = '待提交';
  page.data.nicknameError = '错误';
  page.data.nicknameReviewStatus = 'failed';

  page.onClearSession();

  assert.equal(sessionClears, 1);
  assert.equal(page.data.user, null);
  assert.equal(page.data.loginDialogVisible, false);
  assert.equal(page.data.nicknameValue, '');
  assert.equal(page.data.nicknameError, '');
  assert.equal(page.data.nicknameReviewStatus, 'idle');
  assert.equal(page.data.status, '已清除本机会话');
});
