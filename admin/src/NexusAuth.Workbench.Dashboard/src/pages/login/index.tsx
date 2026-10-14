import './style.less';

import { Button, Card, Tag, Tooltip, Typography } from 'tdesign-react';
import { MoonIcon, SunnyIcon } from 'tdesign-icons-react';
import { applyThemeMode, getThemeMode } from '../../theme';
import { checkAuthenticated, setCachedAuthStatus } from '../../router/auth';
import { useEffect, useState } from 'react';

import BrandComponent from '../../components/brand';
import { startLogin } from '../../api/login';

const Login = () => {
  const [loading, setLoading] = useState(false);
  const [themeMode, setThemeMode] = useState<'light' | 'dark'>(() => getThemeMode());

  useEffect(() => {
    void checkAuthStatus();
  }, []);

  async function checkAuthStatus() {
    setCachedAuthStatus(false);
    const authenticated = await checkAuthenticated();
    setCachedAuthStatus(authenticated);
    if (authenticated) {
      window.location.href = '/dashboard';
    }
  }

  const handleToggleTheme = () => {
    const nextTheme = themeMode === 'light' ? 'dark' : 'light';
    setThemeMode(nextTheme);
    applyThemeMode(nextTheme);
  };

  const handleLogin = async () => {
    setLoading(true);
    try {
      const { authorizeUrl } = await startLogin();
      window.location.assign(authorizeUrl);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className={`stitch-login-page stitch-login-page--${themeMode}`}>
      <div className="stitch-login-bg" />

      <header className="stitch-login-header">
        <BrandComponent className="stitch-login-brand" />
        <Tooltip content={themeMode === 'light' ? '切换到暗色主题' : '切换到浅色主题'}>
          <Button
            className="stitch-login-theme-button"
            shape="circle"
            variant="text"
            aria-label={themeMode === 'light' ? '切换到暗色主题' : '切换到浅色主题'}
            icon={themeMode === 'light' ? <MoonIcon /> : <SunnyIcon />}
            onClick={handleToggleTheme}
          />
        </Tooltip>
      </header>

      <main className="stitch-login-main">
        <Card className="stitch-login-card" bordered bodyClassName="stitch-login-card__body">
          <div className="stitch-login-identity">
            <Typography.Title level="h1" className="stitch-login-title">欢迎回来</Typography.Title>
            <Typography.Paragraph className="stitch-login-subtitle">点击下方按钮使用 NexusAuth 账号登录</Typography.Paragraph>
          </div>

          <Button className="stitch-login-submit" theme="primary" size="large" loading={loading} onClick={handleLogin} block>
            使用 NexusAuth 登录
          </Button>
        </Card>

        <div className="stitch-login-status">
          <Tag theme="success" variant="light">系统在线</Tag>
        </div>
      </main>

      <footer className="stitch-login-footer">
        <div>© 2024 NexusAuth. 保留所有权利。</div>
      </footer>

      <div className="stitch-login-side-visual" />
    </div>
  );
};

export default Login;
