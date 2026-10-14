import { useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import type { AxiosError } from 'axios';
import {
  AddIcon,
  ApplicationIcon,
  DeleteIcon,
  EditIcon,
  RefreshIcon,
  SearchIcon,
  ViewListIcon,
} from 'tdesign-icons-react';
import {
  Button,
  Card,
  Descriptions,
  Drawer,
  Empty,
  Form,
  Input,
  Loading,
  MessagePlugin,
  Pagination,
  Popconfirm,
  Select,
  Tag,
  Avatar,
  Typography,
  Space,
} from 'tdesign-react';
import { deleteClient, getAllClients, getClient, getClients, type Client } from '../../api/client';
import { getAllApiResources } from '../../api/api-resource';
import PageActions from '../../components/page-actions';
import './style.less';
import '../management-card.less';

type FilterState = {
  keyword: string;
  isActive: '' | boolean;
  authMethod: string;
};

const defaultFilters: FilterState = { keyword: '', isActive: '', authMethod: '' };
const statusOptions = [
  { label: '全部状态', value: '' },
  { label: '启用', value: true },
  { label: '停用', value: false },
];
const authMethodOptions = [
  { label: '全部认证方式', value: '' },
  { label: 'client_secret_basic', value: 'client_secret_basic' },
  { label: 'client_secret_post', value: 'client_secret_post' },
  { label: 'private_key_jwt', value: 'private_key_jwt' },
  { label: 'none (public client)', value: 'none' },
];

const getRequestErrorMessage = (error: unknown, fallback: string) => {
  const axiosError = error as AxiosError<{ title?: string; detail?: string; message?: string }>;
  return axiosError.response?.data?.detail
    || axiosError.response?.data?.message
    || axiosError.response?.data?.title
    || fallback;
};

const ClientManagementPage = () => {
  const navigate = useNavigate();
  const [filters, setFilters] = useState<FilterState>(defaultFilters);
  const [appliedFilters, setAppliedFilters] = useState<FilterState>(defaultFilters);
  const [current, setCurrent] = useState(1);
  const [pageSize, setPageSize] = useState(6);
  const [loading, setLoading] = useState(false);
  const [detailVisible, setDetailVisible] = useState(false);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailClient, setDetailClient] = useState<Client | null>(null);
  const [sourceData, setSourceData] = useState<Client[]>([]);
  const [total, setTotal] = useState(0);
  const [resourceNamesById, setResourceNamesById] = useState<Record<string, string>>({});

  const fetchData = useCallback(async () => {
    try {
      setLoading(true);
      const sharedFilter: { keyword?: string; isActive?: boolean } = {};
      if (appliedFilters.keyword) sharedFilter.keyword = appliedFilters.keyword;
      if (appliedFilters.isActive !== '') sharedFilter.isActive = appliedFilters.isActive;

      if (appliedFilters.authMethod) {
        const clients = await getAllClients(sharedFilter);
        const filteredClients = clients.filter((client) => client.tokenEndpointAuthMethod === appliedFilters.authMethod);
        const start = (current - 1) * pageSize;
        setSourceData(filteredClients.slice(start, start + pageSize));
        setTotal(filteredClients.length);
        return;
      }

      const result = await getClients({ ...sharedFilter, page: current, pageSize });
      setSourceData(result.items);
      setTotal(result.total);
    } catch (error) {
      console.error('Failed to fetch clients:', error);
      MessagePlugin.error(getRequestErrorMessage(error, '加载客户端失败'));
    } finally {
      setLoading(false);
    }
  }, [appliedFilters, current, pageSize]);

  useEffect(() => { void fetchData(); }, [fetchData]);

  useEffect(() => {
    void getAllApiResources()
      .then((resources) => setResourceNamesById(Object.fromEntries(resources.map((resource) => [resource.id, resource.name]))))
      .catch((error) => console.error('Failed to fetch API resources for client cards:', error));
  }, []);

  const handleQuery = () => {
    setAppliedFilters(filters);
    setCurrent(1);
  };

  const applySelectFilter = (patch: Partial<FilterState>) => {
    setFilters((previous) => {
      const next = { ...previous, ...patch };
      setAppliedFilters(next);
      return next;
    });
    setCurrent(1);
  };

  const handleReset = () => {
    setFilters(defaultFilters);
    setAppliedFilters(defaultFilters);
    setCurrent(1);
  };

  const handleAdd = () => navigate('/oauth/client-management/create');
  const handleEdit = (client: Client) => navigate(`/oauth/client-management/edit/${client.id}`);

  const handleView = async (client: Client) => {
    setDetailClient(client);
    setDetailVisible(true);
    try {
      setDetailLoading(true);
      setDetailClient(await getClient(client.id));
    } catch (error) {
      console.error('Failed to fetch client detail:', error);
      MessagePlugin.error(getRequestErrorMessage(error, '加载应用详情失败'));
    } finally {
      setDetailLoading(false);
    }
  };

  const handleDelete = async (client: Client) => {
    try {
      await deleteClient(client.id);
      MessagePlugin.success('删除成功');
      await fetchData();
    } catch (error) {
      console.error('Failed to delete client:', error);
      MessagePlugin.error(getRequestErrorMessage(error, '删除客户端失败'));
    }
  };

  const renderTextList = (values?: string[], emptyText = '未配置') => (
    values?.length
      ? <code className="client-management-card__list" title={values.join(' · ')}>{values.join(' · ')}</code>
      : <span className="management-card__empty">{emptyText}</span>
  );

  return (
    <div className="management-card-page client-management-page">
      <div className="client-management-page__header">
        <div>
          <Typography.Title level="h1" className="management-page-title">应用管理</Typography.Title>
          <Typography.Paragraph className="management-page-description">集中管理 OAuth / OIDC 客户端及其访问边界</Typography.Paragraph>
        </div>
      </div>

      <PageActions>
        <Button theme="primary" icon={<AddIcon />} onClick={handleAdd}>新增应用</Button>
      </PageActions>

      <Card className="management-card-page__toolbar client-management-page__toolbar" bodyStyle={{ padding: 0 }}>
        <Form layout="inline" onSubmit={(context) => { context.e?.preventDefault(); handleQuery(); }}>
          <Form.FormItem className="client-management-page__search">
            <Input
              size="small"
              clearable
              prefixIcon={<SearchIcon />}
              value={filters.keyword}
              placeholder="搜索应用名称、Client ID 或服务资源 Key"
              onChange={(value) => setFilters((previous) => ({ ...previous, keyword: value }))}
              onEnter={handleQuery}
            />
          </Form.FormItem>
          <Form.FormItem>
            <Select
              size="small"
              value={filters.isActive}
              placeholder="全部状态"
              options={statusOptions}
              onChange={(value) => applySelectFilter({ isActive: value === true || value === false ? value : '' })}
            />
          </Form.FormItem>
          <Form.FormItem className="client-management-page__auth-filter">
            <Select
              size="small"
              value={filters.authMethod}
              placeholder="全部认证方式"
              options={authMethodOptions}
              onChange={(value) => applySelectFilter({ authMethod: String(value || '') })}
            />
          </Form.FormItem>
          <Form.FormItem className="client-management-page__filter-actions">
            <Button size="small" variant="text" theme="primary" icon={<RefreshIcon />} onClick={handleReset}>重置</Button>
          </Form.FormItem>
        </Form>
      </Card>

      <Loading loading={loading} className="management-card-page__loading">
        {sourceData.length ? (
          <div className="management-card-grid management-card-grid--clients">
            {sourceData.map((client) => {
              const resourceKeys = client.apiResourceIds?.map((resourceId) => resourceNamesById[resourceId] || resourceId);
              return (
                <Card key={client.id} className="management-card management-card--client" bordered>
                  <div className="management-card__header client-management-card__header">
                    <div className="client-management-card__identity">
                      <Avatar className="client-management-card__icon" shape="round" icon={<ApplicationIcon />} />
                      <div className="management-card__heading">
                        <span className="management-card__title" title={client.clientName}>{client.clientName}</span>
                        <code className="management-card__identifier" title={client.clientId}>{client.clientId}</code>
                      </div>
                    </div>
                    <Space direction="vertical" size={6} className="management-card__status">
                      <Tag theme={client.isActive ? 'success' : 'default'} variant="light" size="small">
                        {client.isActive ? '启用' : '停用'}
                      </Tag>
                      <Tag theme={client.requirePkce ? 'primary' : 'default'} variant="light" size="small">
                        {client.requirePkce ? 'PKCE' : '无 PKCE'}
                      </Tag>
                    </Space>
                  </div>

                  <Descriptions className="management-card__descriptions" column={1} size="small" items={[
                    { label: '认证方式', content: <code title={client.tokenEndpointAuthMethod}>{client.tokenEndpointAuthMethod || '未配置'}</code> },
                    { label: '授权方式', content: renderTextList(client.allowedGrantTypes) },
                    { label: '授权 Scope', content: renderTextList(client.allowedScopes) },
                    { label: '服务资源 Key', content: renderTextList(resourceKeys) },
                    { label: '主回调地址', content: client.redirectUris?.length ? <code className="client-management-card__callback" title={client.redirectUris[0]}>{client.redirectUris[0]}</code> : '未配置' },
                  ]} />

                  <div className="management-card__footer client-management-card__footer">
                    <Button size="small" variant="outline" icon={<EditIcon />} onClick={() => handleEdit(client)}>编辑</Button>
                    <Button size="small" variant="outline" icon={<ViewListIcon />} onClick={() => void handleView(client)}>查看详情</Button>
                    <Popconfirm
                      theme="danger"
                      content={`确定删除应用“${client.clientName}”吗？`}
                      confirmBtn={{ content: '删除', theme: 'danger', size: 'small' }}
                      cancelBtn={{ content: '取消', size: 'small' }}
                      onConfirm={() => void handleDelete(client)}
                    >
                      <Button size="small" variant="outline" theme="danger" icon={<DeleteIcon />}>删除</Button>
                    </Popconfirm>
                  </div>
                </Card>
              );
            })}
          </div>
        ) : <Empty description="暂无应用" />}
      </Loading>

      <div className="management-card-page__pagination client-management-page__pagination">
        <Pagination
          size="small"
          total={total}
          current={current}
          pageSize={pageSize}
          pageSizeOptions={[6, 12, 24]}
          showPageSize
          showJumper
          onCurrentChange={(next) => setCurrent(next)}
          onPageSizeChange={(size) => {
            setPageSize(Number(size));
            setCurrent(1);
          }}
        />
      </div>

      <Drawer
        className="client-detail-drawer"
        visible={detailVisible}
        header="应用详情"
        size="680px"
        footer={false}
        destroyOnClose
        onClose={() => setDetailVisible(false)}
      >
        <Loading loading={detailLoading} className="client-detail-drawer__loading">
          {detailClient && (
            <Descriptions
              bordered
              colon
              column={1}
              layout="vertical"
              itemLayout="horizontal"
              size="small"
              items={[
                { label: '应用名称', content: detailClient.clientName },
                { label: 'Client ID', content: <code>{detailClient.clientId}</code> },
                { label: '状态', content: <Tag size="small" theme={detailClient.isActive ? 'success' : 'default'} variant="light">{detailClient.isActive ? '启用' : '停用'}</Tag> },
                { label: 'PKCE', content: <Tag size="small" theme={detailClient.requirePkce ? 'primary' : 'default'} variant="light">{detailClient.requirePkce ? 'PKCE' : '无 PKCE'}</Tag> },
                { label: '认证方式', content: <code>{detailClient.tokenEndpointAuthMethod || '未配置'}</code> },
                { label: '授权方式', content: renderTextList(detailClient.allowedGrantTypes) },
                { label: '授权 Scope', content: renderTextList(detailClient.allowedScopes) },
                { label: '服务资源 Key', content: renderTextList(detailClient.apiResourceIds?.map((resourceId) => resourceNamesById[resourceId] || resourceId)) },
                { label: '登录回调地址', content: renderTextList(detailClient.redirectUris) },
                { label: '登出回调地址', content: renderTextList(detailClient.postLogoutRedirectUris) },
                { label: '描述', content: detailClient.description || '未填写描述' },
                { label: '创建时间', content: new Date(detailClient.createdAt).toLocaleString('zh-CN') },
              ]}
            />
          )}
        </Loading>
      </Drawer>
    </div>
  );
};

export default ClientManagementPage;
