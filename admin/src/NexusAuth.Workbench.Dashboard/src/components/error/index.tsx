import { Button, Empty } from 'tdesign-react';
import { useNavigate } from 'react-router-dom';

const ErrorPage = () => {
  const navigate = useNavigate();
  return <Empty type="fail" title="404" description="对不起，你访问的页面不存在。"
    style={{ padding: '120px 24px' }}
    action={<Button theme="primary" onClick={() => navigate('/')}>首页</Button>} />;
};

export default ErrorPage;
