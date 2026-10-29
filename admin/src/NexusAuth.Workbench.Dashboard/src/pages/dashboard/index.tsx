import './style.less';

import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Alert,
  Button,
  Card,
  Empty,
  List,
  Tag,
  Typography,
} from 'tdesign-react';
import {
  AppIcon,
  ChartLineDataIcon,
  CheckCircleFilledIcon,
  ErrorCircleFilledIcon,
  PercentIcon,
  RefreshIcon,
  ServerIcon,
  UserIcon,
} from 'tdesign-icons-react';

import { getApiResources } from '../../api/api-resource';
import { getClients } from '../../api/client';
import { getLoginAudits, type LoginAuditLog } from '../../api/login-audit';
import { getUsers } from '../../api/user';

type DashboardData = {
  loginRecords: number | null;
  successfulLogins: number | null;
  failedLogins: number | null;
  users: number | null;
  applications: number | null;
  apiResources: number | null;
  recentAudits: LoginAuditLog[] | null;
};

type MetricKey = 'loginRecords' | 'successfulLogins' | 'failedLogins' | 'successRate';

type MetricDefinition = {
  key: MetricKey;
  title: string;
  helper: string;
  icon: React.ReactNode;
  tone: 'primary' | 'success' | 'danger' | 'warning';
};

const emptyDashboardData: DashboardData = {
  loginRecords: null,
  successfulLogins: null,
  failedLogins: null,
  users: null,
  applications: null,
  apiResources: null,
  recentAudits: null,
};

const metricDefinitions: MetricDefinition[] = [
  {
    key: 'loginRecords',
    title: '登录记录',
    helper: '全部保留记录',
    icon: <ChartLineDataIcon />,
    tone: 'primary',
  },
  {
    key: 'successfulLogins',
    title: '成功登录',
    helper: '审计结果成功',
    icon: <CheckCircleFilledIcon />,
    tone: 'success',
  },
  {
    key: 'failedLogins',
    title: '失败登录',
    helper: '审计结果失败',
    icon: <ErrorCircleFilledIcon />,
    tone: 'danger',
  },
  {
    key: 'successRate',
    title: '登录成功率',
    helper: '成功记录 / 全部记录',
    icon: <PercentIcon />,
    tone: 'warning',
  },
];

const formatNumber = (value: number) => value.toLocaleString('zh-CN');

const formatDateTime = (value: string) => {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return date.toLocaleString('zh-CN', { hour12: false });
};

const formatUpdatedAt = (value: Date) => value.toLocaleTimeString('zh-CN', {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
});

const Dashboard = () => {
  const [data, setData] = useState<DashboardData>(emptyDashboardData);
  const [loading, setLoading] = useState(true);
  const [failedRequestCount, setFailedRequestCount] = useState(0);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  const loadDashboard = useCallback(async () => {
    setLoading(true);

    const results = await Promise.allSettled([
      getLoginAudits({ page: 1, pageSize: 6 }),
      getLoginAudits({ isSuccessful: true, page: 1, pageSize: 1 }),
      getLoginAudits({ isSuccessful: false, page: 1, pageSize: 1 }),
      getUsers({ page: 1, pageSize: 1 }),
      getClients({ page: 1, pageSize: 1 }),
      getApiResources({ page: 1, pageSize: 1 }),
    ]);

    setData((previous) => ({
      loginRecords: results[0].status === 'fulfilled'
        ? results[0].value.total
        : previous.loginRecords,
      recentAudits: results[0].status === 'fulfilled'
        ? results[0].value.items
        : previous.recentAudits,
      successfulLogins: results[1].status === 'fulfilled'
        ? results[1].value.total
        : previous.successfulLogins,
      failedLogins: results[2].status === 'fulfilled'
        ? results[2].value.total
        : previous.failedLogins,
      users: results[3].status === 'fulfilled'
        ? results[3].value.total
        : previous.users,
      applications: results[4].status === 'fulfilled'
        ? results[4].value.total
        : previous.applications,
      apiResources: results[5].status === 'fulfilled'
        ? results[5].value.total
        : previous.apiResources,
    }));

    const failures = results.filter((result) => result.status === 'rejected').length;
    setFailedRequestCount(failures);
    if (failures < results.length) {
      setLastUpdated(new Date());
    }
    setLoading(false);
  }, []);

  useEffect(() => {
    void loadDashboard();
  }, [loadDashboard]);

  const successRate = useMemo(() => {
    if (data.successfulLogins === null || data.loginRecords === null || data.loginRecords === 0) {
      return null;
    }

    return (data.successfulLogins / data.loginRecords) * 100;
  }, [data.loginRecords, data.successfulLogins]);

  const getMetricValue = (key: MetricKey) => {
    if (key === 'successRate') {
      return successRate === null ? null : `${successRate.toFixed(2)}%`;
    }

    const value = data[key];
    return value === null ? null : formatNumber(value);
  };

  const renderRecentAudits = () => {
    if (data.recentAudits === null) {
      return (
        <Empty
          className="dashboard-empty"
          description={loading ? '正在加载登录记录' : '登录记录暂时不可用'}
        />
      );
    }

    if (data.recentAudits.length === 0) {
      return <Empty className="dashboard-empty" description="暂无登录记录" />;
    }

    return (
      <List className="dashboard-audit-list" split>
        {data.recentAudits.map((audit) => (
          <List.ListItem key={audit.id} className="dashboard-audit-item">
            <div className="dashboard-audit-main">
              <div className="dashboard-audit-identity">
                <span className="dashboard-audit-avatar" aria-hidden="true">
                  {(audit.username || 'U').slice(0, 1).toUpperCase()}
                </span>
                <div>
                  <strong>{audit.username}</strong>
                  <span>{audit.clientId || '直接登录'}</span>
                </div>
              </div>
              <div className="dashboard-audit-result">
                <Tag
                  theme={audit.isSuccessful ? 'success' : 'danger'}
                  variant="light-outline"
                  size="small"
                >
                  {audit.isSuccessful ? '成功' : '失败'}
                </Tag>
                <span>{formatDateTime(audit.occurredAt)}</span>
              </div>
            </div>
            <div className="dashboard-audit-meta">
              <span>{audit.ipAddress || '未记录 IP'}</span>
              {!audit.isSuccessful && audit.failureReason && <span>{audit.failureReason}</span>}
            </div>
          </List.ListItem>
        ))}
      </List>
    );
  };

  return (
    <div className="dashboard-page">
      <header className="dashboard-header">
        <div className="dashboard-heading">
          <Typography.Title level="h1" className="dashboard-title">仪表盘</Typography.Title>
          <Typography.Paragraph className="dashboard-subtitle">
            NexusAuth 身份与访问管理概览
          </Typography.Paragraph>
        </div>
        <div className="dashboard-controls">
          <Button
            variant="outline"
            icon={<RefreshIcon />}
            loading={loading}
            onClick={() => void loadDashboard()}
          >
            刷新
          </Button>
          <span className="dashboard-updated">
            {lastUpdated ? `更新于 ${formatUpdatedAt(lastUpdated)}` : '尚未取得数据'}
          </span>
        </div>
      </header>

      {failedRequestCount > 0 && (
        <Alert
          className="dashboard-load-alert"
          theme="warning"
          title="部分数据暂时无法加载"
          message="已保留此前成功获取的数据。请稍后刷新；持续失败时请检查当前账号权限或 BFF 服务状态。"
        />
      )}

      <section className="dashboard-metrics" aria-label="登录审计统计">
        {metricDefinitions.map((metric) => {
          const value = getMetricValue(metric.key);

          return (
            <Card
              key={metric.key}
              className={`dashboard-metric-card dashboard-metric-card--${metric.tone}`}
              bordered={false}
            >
              <div className="dashboard-metric-card__top">
                <span>{metric.title}</span>
                <span className="dashboard-metric-card__icon" aria-hidden="true">{metric.icon}</span>
              </div>
              <strong className={`dashboard-metric-card__value${value === null ? ' is-empty' : ''}`}>
                {value ?? (loading ? '加载中' : '—')}
              </strong>
              <span className="dashboard-metric-card__helper">
                {value === null && !loading ? '暂不可用' : metric.helper}
              </span>
            </Card>
          );
        })}
      </section>

      <section className="dashboard-resources" aria-label="身份资源统计">
        <Card className="dashboard-resource-card" bordered={false}>
          <span className="dashboard-resource-card__icon"><UserIcon /></span>
          <div>
            <span>用户</span>
            <strong>{data.users === null ? '—' : formatNumber(data.users)}</strong>
          </div>
          <small>{data.users === null ? '暂不可用' : '当前账号总数'}</small>
        </Card>
        <Card className="dashboard-resource-card" bordered={false}>
          <span className="dashboard-resource-card__icon"><AppIcon /></span>
          <div>
            <span>应用</span>
            <strong>{data.applications === null ? '—' : formatNumber(data.applications)}</strong>
          </div>
          <small>{data.applications === null ? '暂不可用' : '已注册 OAuth 应用'}</small>
        </Card>
        <Card className="dashboard-resource-card" bordered={false}>
          <span className="dashboard-resource-card__icon"><ServerIcon /></span>
          <div>
            <span>服务资源</span>
            <strong>{data.apiResources === null ? '—' : formatNumber(data.apiResources)}</strong>
          </div>
          <small>{data.apiResources === null ? '暂不可用' : '已注册 API 资源'}</small>
        </Card>
      </section>

      <section className="dashboard-main-grid">
        <Card className="dashboard-panel dashboard-recent-panel" bordered={false}>
          <div className="dashboard-panel-header">
            <div>
              <Typography.Title level="h2" className="dashboard-panel-title">近期登录记录</Typography.Title>
              <Typography.Paragraph className="dashboard-panel-caption">
                来自登录审计接口的最新记录
              </Typography.Paragraph>
            </div>
            <Tag theme="primary" variant="light-outline">{data.loginRecords === null ? '—' : `${formatNumber(data.loginRecords)} 条`}</Tag>
          </div>
          {renderRecentAudits()}
        </Card>

        <div className="dashboard-insights">
          <Card className="dashboard-panel dashboard-empty-panel" bordered={false}>
            <div className="dashboard-panel-header">
              <div>
                <Typography.Title level="h2" className="dashboard-panel-title">认证趋势</Typography.Title>
                <Typography.Paragraph className="dashboard-panel-caption">按日期聚合的登录结果</Typography.Paragraph>
              </div>
            </div>
            <Empty description="当前 API 未提供按日期聚合的认证数据" />
          </Card>
          <Card className="dashboard-panel dashboard-empty-panel" bordered={false}>
            <div className="dashboard-panel-header">
              <div>
                <Typography.Title level="h2" className="dashboard-panel-title">应用调用排行</Typography.Title>
                <Typography.Paragraph className="dashboard-panel-caption">按应用聚合的认证请求量</Typography.Paragraph>
              </div>
            </div>
            <Empty description="当前 API 未提供按应用聚合的调用数据" />
          </Card>
        </div>
      </section>
    </div>
  );
};

export default Dashboard;
