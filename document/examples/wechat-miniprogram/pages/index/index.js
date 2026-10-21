const auth = require('../../utils/auth');

Page({
  data: {
    busy: false,
    status: '未登录',
    user: null
  },

  onLoad() {
    if (auth.hasSession()) {
      this.setData({ status: '本地已有会话' });
    }
  },

  onLogin() {
    return this.run(async () => {
      await auth.login();
      this.setData({
        status: '登录成功',
        user: null
      });
    });
  },

  onReadUser() {
    return this.run(async () => {
      const user = await auth.getUserInfo();
      this.setData({
        status: '已读取用户信息',
        user
      });
    });
  },

  onRefresh() {
    return this.run(async () => {
      await auth.refresh();
      this.setData({ status: 'Token 已刷新' });
    });
  },

  onClearSession() {
    auth.clearSession();
    this.setData({
      status: '已清除本机会话',
      user: null
    });
  },

  async run(action) {
    if (this.data.busy) {
      return;
    }

    this.setData({
      busy: true,
      status: '处理中'
    });

    try {
      await action();
    } catch (error) {
      this.setData({
        status: error && error.message ? error.message : '操作失败，请重试。'
      });
    } finally {
      this.setData({ busy: false });
    }
  }
});
