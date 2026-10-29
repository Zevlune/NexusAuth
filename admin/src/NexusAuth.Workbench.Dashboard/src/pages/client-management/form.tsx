import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import type { AxiosError } from 'axios';
import { AddIcon, DeleteIcon, KeyIcon } from 'tdesign-icons-react';
import { Alert, Button, Card, Descriptions, Dialog, Form, Input, Loading, MessagePlugin, Popconfirm, Select, Space, Switch, Tag, Textarea, Tooltip, Typography } from 'tdesign-react';
import type { FormInstanceFunctions } from 'tdesign-react/es/form/type';
import {
  createClient,
  getClient,
  resetClientCredential,
  updateClient,
  type Client,
  type CreateClientRequest,
  type GeneratedClientCredential,
  type TokenEndpointAuthMethod,
  type UpdateClientRequest,
} from '../../api/client';
import { getAllApiResources, type ApiResource } from '../../api/api-resource';
import { getClientMetadata, type ClientMetadata } from '../../api/client-metadata';
import {
  authMethodOptions,
  canResetClientCredential,
  grantTypeOptions,
  mergeOptions,
} from './client-options';
import './style.less';

type ClientFormData = {
  clientId: string;
  clientName: string;
  description: string;
  redirectUris: { id: string; value: string }[];
  postLogoutRedirectUris: { id: string; value: string }[];
  allowedScopes: string[];
  allowedGrantTypes: string[];
  apiResourceIds: string[];
  requirePkce: boolean;
  requireConsent: boolean;
  tokenEndpointAuthMethod: TokenEndpointAuthMethod;
  keyMaterialSource: 'jwks' | 'jwks_uri';
  jwksInputMode: 'auto_generate' | 'manual';
  jwks: string;
  jwksUri: string;
  isActive: boolean;
};

type ClientFormPageProps = {
  mode: 'create' | 'edit';
};

// jwks_uri 不再支持新配置（服务端不会拉取远程 JWKS），仅用于展示存量应用的已有配置。
const keyMaterialSourceOptions = [
  { label: 'JWKS', value: 'jwks' },
  { label: 'JWKS URI（已停用）', value: 'jwks_uri' },
];

const jwksInputModeOptions = [
  { label: '自动生成', value: 'auto_generate' },
  { label: '手动录入', value: 'manual' },
];

const oauthStandardScopeOptions = [
  { label: 'openid', value: 'openid' },
  { label: 'profile', value: 'profile' },
  { label: 'email', value: 'email' },
  { label: 'phone', value: 'phone' },
  { label: 'address', value: 'address' },
  { label: 'offline_access', value: 'offline_access' },
];

const oauthStandardScopeValues = new Set(oauthStandardScopeOptions.map((scope) => scope.value));

const defaultFormData: ClientFormData = {
  clientId: '',
  clientName: '',
  description: '',
  redirectUris: [{ id: '1', value: '' }],
  postLogoutRedirectUris: [{ id: '1', value: '' }],
  allowedScopes: [],
  allowedGrantTypes: [],
  apiResourceIds: [],
  requirePkce: false,
  requireConsent: false,
  tokenEndpointAuthMethod: 'client_secret_basic',
  keyMaterialSource: 'jwks',
  jwksInputMode: 'manual',
  jwks: '',
  jwksUri: '',
  isActive: true,
};

const defaultClientMetadata: ClientMetadata = {
  scopes: [],
  grantTypes: [],
  tokenEndpointAuthMethods: [],
};

const getRequestErrorMessage = (error: unknown, fallback: string) => {
  const axiosError = error as AxiosError<{ title?: string; detail?: string; message?: string }>;
  return axiosError.response?.data?.detail
    || axiosError.response?.data?.message
    || axiosError.response?.data?.title
    || fallback;
};

const formatDateTime = (value?: string) => {
  if (!value) {
    return '-';
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('zh-CN');
};

const toFormData = (client: Client): ClientFormData => ({
  clientId: client.clientId,
  clientName: client.clientName,
  description: client.description ?? '',
  redirectUris: client.redirectUris?.length
    ? client.redirectUris.map((uri, index) => ({ id: String(index + 1), value: uri }))
    : [{ id: '1', value: '' }],
  postLogoutRedirectUris: client.postLogoutRedirectUris?.length
    ? client.postLogoutRedirectUris.map((uri, index) => ({ id: String(index + 1), value: uri }))
    : [{ id: '1', value: '' }],
  allowedScopes: (client.allowedScopes ?? ['openid']).filter((scope) => oauthStandardScopeValues.has(scope)),
  allowedGrantTypes: client.allowedGrantTypes ?? ['authorization_code'],
  apiResourceIds: client.apiResourceIds ?? [],
  requirePkce: client.requirePkce,
  requireConsent: client.requireConsent ?? false,
  tokenEndpointAuthMethod: client.tokenEndpointAuthMethod,
  keyMaterialSource: client.jwksUri ? 'jwks_uri' : 'jwks',
  jwksInputMode: 'manual',
  jwks: client.jwks ?? '',
  jwksUri: client.jwksUri ?? '',
  isActive: client.isActive,
});

const ClientFormPage = ({ mode }: ClientFormPageProps) => {
  const navigate = useNavigate();
  const { id } = useParams();
  const isEditMode = mode === 'edit';
  const [clientMetadata, setClientMetadata] = useState<ClientMetadata>(defaultClientMetadata);
  const [apiResources, setApiResources] = useState<ApiResource[]>([]);
  const [editingClient, setEditingClient] = useState<Client | null>(null);
  const [formData, setFormData] = useState<ClientFormData>(defaultFormData);
  const formRef = useRef<FormInstanceFunctions<ClientFormData> | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [loadingDetail, setLoadingDetail] = useState(false);
  const [loadingResources, setLoadingResources] = useState(false);
  const [generatedCredential, setGeneratedCredential] = useState<GeneratedClientCredential | null>(null);
  const [createdClientId, setCreatedClientId] = useState<string | null>(null);
  const hasLegacyJwksUri = Boolean(editingClient?.jwksUri);

  const authMethodSelectOptions = useMemo(
    () => {
      const canSwitchToPublicClient = !editingClient
        || (editingClient.credentials.length === 0 && !editingClient.jwks && !editingClient.jwksUri);
      const filterPublicClientOption = (option: { value: string }) =>
        option.value !== 'none' || canSwitchToPublicClient || formData.tokenEndpointAuthMethod === 'none';

      return mergeOptions(
        clientMetadata.tokenEndpointAuthMethods.filter(filterPublicClientOption),
        authMethodOptions.filter(filterPublicClientOption),
        [{ label: formData.tokenEndpointAuthMethod, value: formData.tokenEndpointAuthMethod }],
      );
    },
    [clientMetadata.tokenEndpointAuthMethods, editingClient, formData.tokenEndpointAuthMethod],
  );

  const grantTypeSelectOptions = useMemo(
    () => mergeOptions(
      clientMetadata.grantTypes,
      grantTypeOptions,
      formData.allowedGrantTypes.map((value) => ({ label: value, value })),
    ),
    [clientMetadata.grantTypes, formData.allowedGrantTypes],
  );

  const scopeSelectOptions = useMemo(
    () => mergeOptions(
      oauthStandardScopeOptions,
      clientMetadata.scopes,
      formData.allowedScopes.map((value) => ({ label: value, value })),
    ),
    [clientMetadata.scopes, formData.allowedScopes],
  );

  const apiResourceSelectOptions = useMemo(() => {
    const resourceOptions = apiResources.map((resource) => ({
      label: resource.displayName ? `${resource.displayName} (${resource.audience})` : resource.audience,
      value: resource.id,
      description: resource.description,
    }));
    const missingOptions = formData.apiResourceIds
      .filter((resourceId) => !apiResources.some((resource) => resource.id === resourceId))
      .map((resourceId) => ({ label: resourceId, value: resourceId }));
    return mergeOptions(resourceOptions, missingOptions);
  }, [apiResources, formData.apiResourceIds]);

  useEffect(() => {
    let active = true;
    setLoadingResources(true);
    Promise.all([
      getClientMetadata().catch((error) => {
        console.error('Failed to fetch client metadata:', error);
        return defaultClientMetadata;
      }),
      getAllApiResources({ isActive: true }).catch((error) => {
        console.error('Failed to fetch active API resources:', error);
        return [];
      }),
    ]).then(([metadata, resources]) => {
      if (!active) {
        return;
      }
      setClientMetadata(metadata);
      setApiResources(resources);
    }).finally(() => {
      if (active) {
        setLoadingResources(false);
      }
    });

    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    if (!isEditMode) {
      setEditingClient(null);
      setFormData(defaultFormData);
      setGeneratedCredential(null);
      return;
    }

    if (!id) {
      MessagePlugin.error('缺少应用 ID');
      navigate('/oauth/client-management', { replace: true });
      return;
    }

    const fetchDetail = async () => {
      try {
        setLoadingDetail(true);
        setGeneratedCredential(null);
        const detail = await getClient(id);
        setEditingClient(detail);
        setFormData(toFormData(detail));
      } catch (error) {
        console.error('Failed to fetch client detail:', error);
        MessagePlugin.error(getRequestErrorMessage(error, '加载应用详情失败'));
        navigate('/oauth/client-management', { replace: true });
      } finally {
        setLoadingDetail(false);
      }
    };

    fetchDetail();
  }, [id, isEditMode, navigate]);

  useEffect(() => {
    if (loadingDetail) {
      return;
    }

    const form = formRef.current;
    if (!form) {
      return;
    }

    form.setFieldsValue({
      clientId: formData.clientId,
      clientName: formData.clientName,
      description: formData.description,
      tokenEndpointAuthMethod: formData.tokenEndpointAuthMethod,
      keyMaterialSource: formData.keyMaterialSource,
      jwksInputMode: formData.jwksInputMode,
      jwks: formData.jwks,
      jwksUri: formData.jwksUri,
      allowedScopes: formData.allowedScopes,
      allowedGrantTypes: formData.allowedGrantTypes,
      apiResourceIds: formData.apiResourceIds,
      requirePkce: formData.requirePkce,
      requireConsent: formData.requireConsent,
      isActive: formData.isActive,
    });
  }, [loadingDetail, formData]);

  const handleAuthMethodChange = (value: TokenEndpointAuthMethod) => {
    if (value === 'none') {
      setGeneratedCredential(null);
    }

    setFormData((prev) => ({
      ...prev,
      tokenEndpointAuthMethod: value,
      keyMaterialSource: value === 'private_key_jwt' ? prev.keyMaterialSource : 'jwks',
      jwksInputMode: value === 'private_key_jwt' ? prev.jwksInputMode : 'manual',
      jwks: value === 'private_key_jwt' ? prev.jwks : '',
      jwksUri: value === 'private_key_jwt' ? prev.jwksUri : '',
    }));
  };

  const handleKeyMaterialSourceChange = (value: 'jwks' | 'jwks_uri') => {
    setFormData((prev) => ({
      ...prev,
      keyMaterialSource: value,
      jwks: value === 'jwks' ? prev.jwks : '',
      // 切回 jwks_uri 时恢复展示存量值（该值只读，不会被提交）。
      jwksUri: value === 'jwks_uri' ? editingClient?.jwksUri ?? '' : '',
    }));
  };

  const handleJwksInputModeChange = (value: 'auto_generate' | 'manual') => {
    setFormData((prev) => ({
      ...prev,
      jwksInputMode: value,
      jwks: value === 'manual' ? prev.jwks : '',
    }));
  };

  const addRedirectUri = (type: 'redirectUris' | 'postLogoutRedirectUris') => {
    setFormData((prev) => ({
      ...prev,
      [type]: [...prev[type], { id: String(Date.now()), value: '' }],
    }));
  };

  const removeRedirectUri = (type: 'redirectUris' | 'postLogoutRedirectUris', uriId: string) => {
    setFormData((prev) => ({
      ...prev,
      [type]: prev[type].filter((uri) => uri.id !== uriId),
    }));
  };

  const updateRedirectUri = (type: 'redirectUris' | 'postLogoutRedirectUris', uriId: string, value: string) => {
    setFormData((prev) => ({
      ...prev,
      [type]: prev[type].map((uri) => (uri.id === uriId ? { ...uri, value } : uri)),
    }));
  };

  const copyCredentialValue = async (value?: string) => {
    if (!value) {
      return;
    }

    try {
      await navigator.clipboard.writeText(value);
      MessagePlugin.success('已复制');
    } catch (error) {
      console.error('Failed to copy client credential:', error);
      MessagePlugin.error('复制失败，请手动选中内容复制');
    }
  };

  const handleCredentialAcknowledged = () => {
    // 凭据只展示一次：确认后立即从状态中清除，不在页面中长期保留明文。
    setGeneratedCredential(null);
    if (createdClientId) {
      const clientId = createdClientId;
      setCreatedClientId(null);
      navigate(`/oauth/client-management/edit/${clientId}`, { replace: true });
    }
  };

  const handleBackToList = () => {
    navigate('/oauth/client-management');
  };

  const handleResetCredential = async () => {
    if (!editingClient || !canResetClientCredential(editingClient.tokenEndpointAuthMethod)) {
      return;
    }

    try {
      setSubmitting(true);
      const result = await resetClientCredential(editingClient.id, {
        tokenEndpointAuthMethod: editingClient.tokenEndpointAuthMethod,
      });
      setEditingClient(result.client);
      setFormData(toFormData(result.client));
      setGeneratedCredential(result.generatedCredential ?? null);
      MessagePlugin.success('密钥已重置');
    } catch (error) {
      console.error('Failed to reset client credential:', error);
      MessagePlugin.error(getRequestErrorMessage(error, '重置应用密钥失败'));
    } finally {
      setSubmitting(false);
    }
  };

  const handleSubmit = async () => {
    const form = formRef.current;
    if (!form) return;

    const results = await form.validate();
    if (results !== true) {
      return;
    }

    const redirectUris = formData.redirectUris.map((uri) => uri.value.trim()).filter(Boolean);
    const postLogoutRedirectUris = formData.postLogoutRedirectUris.map((uri) => uri.value.trim()).filter(Boolean);
    const requirePkce = Boolean(form.getFieldValue('requirePkce'));
    const requireConsent = Boolean(form.getFieldValue('requireConsent'));
    const isActive = Boolean(form.getFieldValue('isActive'));
    const isPrivateKeyJwt = formData.tokenEndpointAuthMethod === 'private_key_jwt';
    const jwks = isPrivateKeyJwt && formData.keyMaterialSource === 'jwks' && formData.jwksInputMode === 'manual'
      ? formData.jwks.trim() || undefined
      : undefined;

    if (isEditMode && hasLegacyJwksUri && isPrivateKeyJwt && formData.keyMaterialSource === 'jwks' && !jwks) {
      MessagePlugin.warning('请录入 JWKS 以替换已停用的 JWKS URI');
      return;
    }

    try {
      setSubmitting(true);

      if (isEditMode && editingClient) {
        // jwks_uri 已停用，不再提交新值；仅在改用 JWKS 或切换认证方式时清空存量配置。
        const clearLegacyJwksUri = hasLegacyJwksUri && (!isPrivateKeyJwt || Boolean(jwks));
        const clearJwks = !isPrivateKeyJwt && Boolean(editingClient.jwks);
        const request: UpdateClientRequest = {
          clientName: formData.clientName,
          description: formData.description || undefined,
          // 编辑时始终提交完整列表（允许为空），否则后端会保留原有地址，无法删除最后一个回调地址。
          redirectUris,
          postLogoutRedirectUris,
          allowedScopes: formData.allowedScopes,
          allowedGrantTypes: formData.allowedGrantTypes,
          apiResourceIds: formData.apiResourceIds,
          requirePkce,
          requireConsent,
          isActive,
          tokenEndpointAuthMethod: formData.tokenEndpointAuthMethod,
          jwks: clearJwks ? '' : jwks,
          jwksUri: clearLegacyJwksUri ? '' : undefined,
        };
        const updated = await updateClient(editingClient.id, request);
        setEditingClient(updated);
        setFormData(toFormData(updated));
        MessagePlugin.success('应用已保存');
        return;
      }

      const request: CreateClientRequest = {
        clientId: formData.clientId,
        clientName: formData.clientName,
        description: formData.description || undefined,
        redirectUris,
        postLogoutRedirectUris,
        allowedScopes: formData.allowedScopes,
        allowedGrantTypes: formData.allowedGrantTypes,
        apiResourceIds: formData.apiResourceIds,
        requirePkce,
        requireConsent,
        tokenEndpointAuthMethod: formData.tokenEndpointAuthMethod,
        autoGenerateJwks: isPrivateKeyJwt && formData.keyMaterialSource === 'jwks' && formData.jwksInputMode === 'auto_generate',
        jwks,
      };
      const result = await createClient(request);
      MessagePlugin.success('应用已创建');
      if (result.generatedCredential) {
        // 先弹窗展示一次性凭据，用户确认已保存后再跳转到编辑页，避免页面切换导致凭据丢失。
        setCreatedClientId(result.client.id);
        setGeneratedCredential(result.generatedCredential);
        return;
      }
      navigate(`/oauth/client-management/edit/${result.client.id}`, { replace: true });
    } catch (error) {
      console.error('Failed to save client:', error);
      MessagePlugin.error(getRequestErrorMessage(error, isEditMode ? '更新应用失败' : '创建应用失败'));
    } finally {
      setSubmitting(false);
    }
  };

  const renderCredentialDialog = () => {
    const value = generatedCredential?.clientSecret || generatedCredential?.privateKeyPem;
    const title = generatedCredential?.privateKeyPem ? 'Private Key' : 'Client Secret';

    return (
      <Dialog
        visible={Boolean(generatedCredential)}
        header={`${title} 已生成`}
        width="640px"
        closeBtn={false}
        closeOnOverlayClick={false}
        closeOnEscKeydown={false}
        destroyOnClose
        confirmBtn="我已安全保存"
        cancelBtn={null}
        onConfirm={handleCredentialAcknowledged}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Alert
            theme="warning"
            message={`该 ${title} 只会展示这一次，关闭此窗口后将无法再次查看。请立即复制并妥善保存。`}
          />
          {value && (
            <Textarea
              readonly
              value={value}
              autosize={generatedCredential?.privateKeyPem ? { minRows: 10, maxRows: 16 } : { minRows: 2, maxRows: 4 }}
            />
          )}
          <div>
            <Button variant="outline" onClick={() => void copyCredentialValue(value)}>复制 {title}</Button>
          </div>
        </Space>
      </Dialog>
    );
  };

  const renderClientOverview = () => {
    if (!editingClient) {
      return null;
    }

    return (
      <Card className="client-overview" title="应用状态" bordered headerBordered>
        <Descriptions
          colon
          column={1}
          size="small"
          items={[
            {
              label: '状态',
              content: <Tag theme={editingClient.isActive ? 'success' : 'default'}>{editingClient.isActive ? '启用' : '禁用'}</Tag>,
            },
            {
              label: 'Client ID',
              content: <span className="client-overview__value">{editingClient.clientId}</span>,
            },
            {
              label: '认证方式',
              content: <span className="client-overview__value">{editingClient.tokenEndpointAuthMethod}</span>,
            },
            {
              label: '创建时间',
              content: formatDateTime(editingClient.createdAt),
            },
          ]}
        />
      </Card>
    );
  };

  const renderUriRows = (type: 'redirectUris' | 'postLogoutRedirectUris', placeholder: string) => (
    <div className="client-uri-list">
      {formData[type].map((uri, index) => (
        <div className="client-uri-row" key={uri.id}>
          <Input value={uri.value} placeholder={placeholder} onChange={(value) => updateRedirectUri(type, uri.id, value)} />
          <Tooltip content={index === 0 ? '新增地址' : '删除地址'}>
            <Button
              type="button"
              variant="outline"
              shape="square"
              className="client-uri-action"
              aria-label={index === 0 ? '新增地址' : '删除地址'}
              icon={index === 0 ? <AddIcon /> : <DeleteIcon />}
              onClick={() => (index === 0 ? addRedirectUri(type) : removeRedirectUri(type, uri.id))}
            />
          </Tooltip>
        </div>
      ))}
    </div>
  );

  const pageTitle = isEditMode ? '编辑应用' : '新增应用';

  return (
    <div className="client-form-page">
      <div className="client-form-header">
        <div className="client-form-header__content">
          <Typography.Title level="h1" className="client-form-header__title">{pageTitle}</Typography.Title>
          <Typography.Paragraph className="client-form-header__description">配置 OAuth/OIDC 应用的基础信息、授权类型、回调地址和客户端认证方式。</Typography.Paragraph>
        </div>
        <div className="client-form-header__actions">
          {isEditMode && editingClient && canResetClientCredential(editingClient.tokenEndpointAuthMethod) && (
            <Popconfirm
              content="重置后旧密钥将立即失效，新密钥只展示一次。确定继续吗？"
              confirmBtn={{ content: '重置', theme: 'primary', size: 'small' }}
              cancelBtn={{ content: '取消', size: 'small' }}
              onConfirm={() => void handleResetCredential()}
            >
              <Button size="small" variant="outline" icon={<KeyIcon />} loading={submitting}>重置密钥</Button>
            </Popconfirm>
          )}
          <Button variant="outline" onClick={handleBackToList} disabled={submitting}>返回列表</Button>
        </div>
      </div>

      {renderClientOverview()}
      {renderCredentialDialog()}

      <Loading loading={loadingDetail} text="正在加载应用详情..." className="client-form-loading">
        <Form key={editingClient?.id ?? 'new-client'} ref={formRef} layout="vertical" labelAlign="top" initialData={formData}>
          <div className="client-form-grid">
            <Card className="client-form-section" title="基础信息" bordered headerBordered>
              <div className="client-form-fields">
                <Form.FormItem label="Client ID" name="clientId" rules={[{ required: true, message: '请输入 Client ID', type: 'error' }]}>
                  <Input value={formData.clientId} disabled={isEditMode} placeholder="请输入 Client ID" onChange={(value) => setFormData((prev) => ({ ...prev, clientId: value }))} />
                </Form.FormItem>
                <Form.FormItem label="应用名称" name="clientName" rules={[{ required: true, message: '请输入应用名称', type: 'error' }]}>
                  <Input value={formData.clientName} placeholder="请输入应用名称" onChange={(value) => setFormData((prev) => ({ ...prev, clientName: value }))} />
                </Form.FormItem>
              </div>
              <Form.FormItem label="描述" name="description">
                <Textarea value={formData.description} placeholder="请输入应用描述" autosize={{ minRows: 3, maxRows: 6 }} onChange={(value) => setFormData((prev) => ({ ...prev, description: value }))} />
              </Form.FormItem>
            </Card>

            <Card className="client-form-section" title="授权配置" bordered headerBordered>
              <Form.FormItem label="允许的 Scope" name="allowedScopes" rules={[{ required: true, message: '请选择 Scope', type: 'error' }]}>
                <Select value={formData.allowedScopes} multiple placeholder="请选择 Scope" options={scopeSelectOptions} onChange={(value) => setFormData((prev) => ({ ...prev, allowedScopes: value as string[] }))} />
              </Form.FormItem>
              <Form.FormItem label="允许的 Grant Type" name="allowedGrantTypes" rules={[{ required: true, message: '请选择 Grant Type', type: 'error' }]}>
                <Select value={formData.allowedGrantTypes} multiple placeholder="请选择 Grant Type" options={grantTypeSelectOptions} onChange={(value) => setFormData((prev) => ({ ...prev, allowedGrantTypes: value as string[] }))} />
              </Form.FormItem>
              <Form.FormItem label="服务资源" name="apiResourceIds" help="选择应用可以访问的 API 服务资源，资源的 audience 会用于生成访问令牌的 aud。">
                <Select value={formData.apiResourceIds} multiple loading={loadingResources} placeholder="请选择服务资源" options={apiResourceSelectOptions} onChange={(value) => setFormData((prev) => ({ ...prev, apiResourceIds: value as string[] }))} />
              </Form.FormItem>
              <Form.FormItem label="要求用户同意授权" name="requireConsent" help="开启后，用户首次登录该应用时需要确认授予的权限。">
                <Switch value={formData.requireConsent} onChange={(value) => setFormData((prev) => ({ ...prev, requireConsent: value }))} />
              </Form.FormItem>
            </Card>

            <Card className="client-form-section" title="回调地址" bordered headerBordered>
              <Form.FormItem label="登录回调地址" help="授权完成后，NexusAuth 将把授权响应发送到这些地址。">
                {renderUriRows('redirectUris', '请输入完整的 HTTPS 回调地址')}
              </Form.FormItem>
              <Form.FormItem label="登出回调地址">
                {renderUriRows('postLogoutRedirectUris', '请输入登出后的回调地址')}
              </Form.FormItem>
            </Card>

            <Card className="client-form-section" title="客户端认证" bordered headerBordered>
              <div className="client-form-fields">
                <Form.FormItem label="认证方式" name="tokenEndpointAuthMethod" rules={[{ required: true, message: '请选择认证方式', type: 'error' }]}>
                  <Select value={formData.tokenEndpointAuthMethod} placeholder="请选择认证方式" options={authMethodSelectOptions} onChange={(value) => handleAuthMethodChange(value as TokenEndpointAuthMethod)} />
                </Form.FormItem>
                <div className="client-switch-group">
                  <Form.FormItem label="启用 PKCE" name="requirePkce">
                    <Switch value={formData.requirePkce} onChange={(value) => setFormData((prev) => ({ ...prev, requirePkce: value }))} />
                  </Form.FormItem>
                  <Form.FormItem label="应用状态" name="isActive">
                    <Switch value={formData.isActive} onChange={(value) => setFormData((prev) => ({ ...prev, isActive: value }))} />
                  </Form.FormItem>
                </div>
              </div>

              {formData.tokenEndpointAuthMethod === 'private_key_jwt' && (
                <div className="client-jwt-material">
                  <Typography.Text strong className="client-form-section__subheading">JWT 客户端认证材料</Typography.Text>
                  {(hasLegacyJwksUri || !isEditMode) && (
                    <div className="client-form-fields">
                      {hasLegacyJwksUri && (
                        <Form.FormItem label="公钥来源" name="keyMaterialSource">
                          <Select value={formData.keyMaterialSource} options={keyMaterialSourceOptions} onChange={(value) => handleKeyMaterialSourceChange(value as 'jwks' | 'jwks_uri')} />
                        </Form.FormItem>
                      )}
                      {/* 自动生成密钥对仅在创建时可用；编辑时后端不会生成，只能手动录入公钥 JWKS。 */}
                      {!isEditMode && formData.keyMaterialSource === 'jwks' && (
                        <Form.FormItem label="JWKS 配置方式" name="jwksInputMode">
                          <Select value={formData.jwksInputMode} options={jwksInputModeOptions} onChange={(value) => handleJwksInputModeChange(value as 'auto_generate' | 'manual')} />
                        </Form.FormItem>
                      )}
                    </div>
                  )}
                  {formData.keyMaterialSource === 'jwks' ? (
                    formData.jwksInputMode === 'manual' ? (
                      <Form.FormItem label="JWKS" name="jwks">
                        <Textarea value={formData.jwks} placeholder="请输入客户端公钥 JWKS JSON" autosize={{ minRows: 4, maxRows: 10 }} onChange={(value) => setFormData((prev) => ({ ...prev, jwks: value }))} />
                      </Form.FormItem>
                    ) : <Alert className="client-form-help" theme="info" message="自动生成模式下，创建应用时会由后端生成 RSA 密钥对并登记 JWKS，创建成功后会弹窗一次性展示私钥明文，请务必在关闭前保存。" />
                  ) : (
                    <>
                      <Alert className="client-form-help" theme="warning" message="该应用使用已停用的 JWKS URI 配置：服务端不会拉取远程 JWKS，private_key_jwt 认证无法通过。请将公钥来源切换为 JWKS 并录入客户端公钥。" />
                      <Form.FormItem label="JWKS URI" name="jwksUri">
                        <Input value={formData.jwksUri} readonly disabled />
                      </Form.FormItem>
                    </>
                  )}
                </div>
              )}
            </Card>
          </div>

          <div className="client-form-actions">
            <Button theme="primary" loading={submitting} disabled={loadingDetail} onClick={handleSubmit}>{isEditMode ? '保存' : '创建'}</Button>
          </div>
        </Form>
      </Loading>
    </div>
  );
};

export default ClientFormPage;
