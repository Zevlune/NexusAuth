const auth = require('../../utils/auth');

const INVALID_NICKNAME_MESSAGE = '昵称去除首尾空格后不能超过 100 个字。';

function normalizedUser(user) {
  return {
    sub: user && typeof user.sub === 'string' ? user.sub : '',
    name: user && typeof user.name === 'string' ? user.name : ''
  };
}

function normalizedNickname(value) {
  return typeof value === 'string' ? value.trim() : '';
}

function isNicknameTooLong(value) {
  return Array.from(normalizedNickname(value)).length > 100;
}

function sessionUser(session) {
  const user = session && session.user;
  if (
    !user
    || typeof user.sub !== 'string'
    || !user.sub
    || typeof user.name !== 'string'
  ) {
    return null;
  }

  return normalizedUser(user);
}

Page({
  data: {
    busy: false,
    status: '正在检查登录状态',
    user: null,
    loginDialogVisible: false,
    nicknameValue: '',
    nicknameError: '',
    nicknameReviewStatus: 'idle',
    nicknameInvalid: false
  },

  onLoad() {
    this.setData({
      user: null,
      loginDialogVisible: false,
      nicknameValue: '',
      nicknameError: '',
      nicknameReviewStatus: 'idle',
      nicknameInvalid: false
    });

    if (!auth.hasSession()) {
      this.setData({ status: '未登录' });
      return;
    }

    return this.run(async () => {
      const user = await auth.getUserInfo();
      this.showUser(user, '已恢复登录并读取用户信息');
    }, '正在恢复登录状态');
  },

  onLoginTap() {
    if (this.data.busy) {
      return;
    }

    const currentName = this.data.user && typeof this.data.user.name === 'string'
      ? this.data.user.name
      : '';
    const invalid = isNicknameTooLong(currentName);
    this.setData({
      loginDialogVisible: true,
      nicknameValue: currentName,
      nicknameError: invalid ? INVALID_NICKNAME_MESSAGE : '',
      nicknameReviewStatus: 'idle',
      nicknameInvalid: invalid
    });
  },

  onLoginCancel() {
    if (this.data.busy) {
      return;
    }

    this.setData({
      loginDialogVisible: false,
      nicknameError: '',
      nicknameReviewStatus: 'idle',
      nicknameInvalid: false
    });
  },

  onLoginDialogMaskTap() {
    this.onLoginCancel();
  },

  stopPropagation() {},

  onLogin(event) {
    if (this.data.busy || !this.data.loginDialogVisible) {
      return;
    }

    const formValue = event
      && event.detail
      && event.detail.value
      && event.detail.value.nickname;
    const submittedNickname = typeof formValue === 'string' ? formValue : this.data.nicknameValue;
    const nickname = this.validateNicknameForLogin(submittedNickname);
    if (nickname === null) {
      return;
    }

    return this.run(async () => {
      await this.finishLogin(nickname);
    }, '正在登录', {
      formError: true,
      startData: {
        user: null,
        nicknameValue: submittedNickname,
        nicknameError: '',
        nicknameInvalid: false
      }
    });
  },

  async finishLogin(nickname) {
    const session = await auth.login(nickname || undefined);
    let user = sessionUser(session);
    if (!user) {
      user = await auth.getUserInfo();
    }
    this.showUser(user, '微信登录成功');
  },

  validateNicknameForLogin(value) {
    if (this.data.nicknameReviewStatus === 'failed') {
      this.setData({
        nicknameError: '该微信昵称未通过审核，请重新选择或手动填写昵称。'
      });
      return null;
    }
    if (this.data.nicknameReviewStatus === 'timeout') {
      this.setData({
        nicknameError: '微信昵称审核超时，请点击输入框重试，或手动填写昵称。'
      });
      return null;
    }
    if (isNicknameTooLong(value)) {
      this.setData({
        nicknameValue: typeof value === 'string' ? value : '',
        nicknameError: INVALID_NICKNAME_MESSAGE,
        nicknameInvalid: true
      });
      return null;
    }

    return normalizedNickname(value);
  },

  onRefresh() {
    return this.run(async () => {
      await auth.refresh();
      const user = await auth.getUserInfo();
      this.showUser(user, '登录状态与用户信息已刷新');
    }, '正在刷新登录状态');
  },

  onClearSession() {
    if (this.data.busy) {
      return;
    }

    auth.clearSession();
    this.setData({
      status: '已清除本机会话',
      user: null,
      loginDialogVisible: false,
      nicknameValue: '',
      nicknameError: '',
      nicknameReviewStatus: 'idle',
      nicknameInvalid: false
    });
  },

  onNicknameInput(event) {
    const value = event && event.detail && typeof event.detail.value === 'string'
      ? event.detail.value
      : '';
    const invalid = isNicknameTooLong(value);
    this.setData({
      nicknameValue: value,
      nicknameError: invalid ? INVALID_NICKNAME_MESSAGE : '',
      nicknameReviewStatus: 'idle',
      nicknameInvalid: invalid
    });
  },

  onNicknameBlur(event) {
    const reviewStatus = this.data.nicknameReviewStatus;
    if (
      reviewStatus !== 'failed'
      && reviewStatus !== 'timeout'
      && event
      && event.detail
      && typeof event.detail.value === 'string'
    ) {
      const invalid = isNicknameTooLong(event.detail.value);
      this.setData({
        nicknameValue: event.detail.value,
        nicknameError: invalid ? INVALID_NICKNAME_MESSAGE : '',
        nicknameInvalid: invalid
      });
    }
  },

  onNicknameReview(event) {
    const review = event && event.detail ? event.detail : {};
    if (review.pass === true) {
      const invalid = isNicknameTooLong(this.data.nicknameValue);
      this.setData({
        nicknameError: invalid ? INVALID_NICKNAME_MESSAGE : '',
        nicknameReviewStatus: 'passed',
        nicknameInvalid: invalid
      });
      return;
    }

    if (review.timeout === true) {
      this.setData({
        nicknameValue: '',
        nicknameError: '微信昵称审核超时，请点击输入框重试，或手动填写昵称。',
        nicknameReviewStatus: 'timeout',
        nicknameInvalid: false
      });
      return;
    }

    this.setData({
      nicknameValue: '',
      nicknameError: '该微信昵称未通过审核，请重新选择或手动填写昵称。',
      nicknameReviewStatus: 'failed',
      nicknameInvalid: false
    });
  },

  showUser(user, status) {
    const displayUser = normalizedUser(user);
    this.setData({
      user: displayUser,
      status,
      loginDialogVisible: false,
      nicknameValue: displayUser.name,
      nicknameError: '',
      nicknameReviewStatus: 'idle',
      nicknameInvalid: false
    });
  },

  clearUserDisplay(status) {
    this.setData({
      status,
      user: null
    });
  },

  async run(action, workingStatus, options) {
    if (this.data.busy) {
      return;
    }

    const settings = options || {};
    this.setData(Object.assign({
      busy: true,
      status: workingStatus || '处理中'
    }, settings.startData || {}));

    try {
      await action();
    } catch (error) {
      const message = error && error.message ? error.message : '操作失败，请重试。';
      if (!auth.hasSession()) {
        this.clearUserDisplay(message);
      } else {
        this.setData({ status: message });
      }

      if (settings.formError) {
        this.setData({ nicknameError: message });
      }
    } finally {
      this.setData({ busy: false });
    }
  }
});
