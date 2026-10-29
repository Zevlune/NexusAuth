import './style.less';

import { Alert, Button, Card, MessagePlugin, Tag, Tooltip, Typography } from 'tdesign-react';
import { MoonIcon, SunnyIcon } from 'tdesign-icons-react';
import { applyThemeMode, getThemeMode } from '../../theme';
import { checkAuthenticated, setCachedAuthStatus } from '../../router/auth';
import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';

import BrandComponent from '../../components/brand';
import { startLogin } from '../../api/login';
import { DEFAULT_AUTHENTICATED_ROUTE } from '../../router/defaults';

const loginErrorMessages: Record<string, string> = {
  invalid_callback: '登录回调无效或已过期，请重新登录。',
  token_exchange_failed: '登录凭据交换失败，请稍后重试；如持续失败请联系管理员。',
  session_missing: '未能建立登录会话，请确认浏览器允许 Cookie 后重新登录。',
};

const Login = () => {
  const [searchParams] = useSearchParams();
  const loginErrorCode = searchParams.get('error');
  const loginErrorMessage = loginErrorCode
    ? loginErrorMessages[loginErrorCode] ?? '登录失败，请重新登录。'
    : null;
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
      window.location.href = DEFAULT_AUTHENTICATED_ROUTE;
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
    } catch (error) {
      console.error('Failed to start login:', error);
      MessagePlugin.error('无法发起登录，请稍后重试');
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

          {loginErrorMessage && (
            <Alert className="stitch-login-error" theme="error" message={loginErrorMessage} />
          )}

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
