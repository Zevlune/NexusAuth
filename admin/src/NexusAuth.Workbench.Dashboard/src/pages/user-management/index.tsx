import { useCallback, useEffect, useRef, useState } from 'react';
import type { AxiosError } from 'axios';
import { Alert, Avatar, Button, Card, Descriptions, Dialog, Drawer, Dropdown, Empty, Form, Input, Loading, MessagePlugin, Pagination, Select, Space, Statistic, Table, Tag, Textarea, Typography, type FormInstanceFunctions, type TableProps } from 'tdesign-react';
import { BrowseIcon, DeleteIcon, EditIcon, EllipsisIcon, KeyIcon, PoweroffIcon, RefreshIcon, SearchIcon, UserAddIcon, UserIcon, UsergroupIcon } from 'tdesign-icons-react';
import { createUser, deleteUser, getUser, getUserPasskeys, getUsers, resetUserPassword, revokeUserPasskey, revokeUserTokens, updateUser, updateUserStatus, type CreateManagedUserRequest, type ManagedUser, type ResetManagedUserPasswordRequest, type UpdateManagedUserRequest, type WebAuthnCredentialSummary } from '../../api/user';
import PageActions from '../../components/page-actions';
import UserForm, { type UserFormHandle, type UserProfileData, type CreateUserData } from './form';
import { normalizePhoneNumber } from './validation';
import './style.less';

type FilterState = {
  keyword: string;
  isActive: '' | boolean;
};

type UserFormData = UserProfileData;

type ResetPasswordFormData = {
  newPassword: string;
  confirmPassword: string;
};

type CreateUserFormData = CreateUserData;

const PASSWORD_MAX_BYTES = 72;

const defaultFilters: FilterState = {
  keyword: '',
  isActive: '',
};

const defaultFormData: UserFormData = {
  nickname: '',
  email: '',
  phoneNumber: '',
  givenName: '',
  familyName: '',
  title: '',
  userType: '',
  preferredLanguage: '',
  locale: '',
  timezone: '',
};

const defaultResetPasswordFormData: ResetPasswordFormData = {
  newPassword: '',
  confirmPassword: '',
};

const defaultCreateFormData: CreateUserFormData = {
  username: '',
  password: '',
  confirmPassword: '',
  nickname: '',
  email: '',
  phoneNumber: '',
  givenName: '',
  familyName: '',
  title: '',
  userType: '',
  preferredLanguage: '',
  locale: '',
  timezone: '',
  isActive: true,
};

const statusOptions = [
  { label: '全部状态', value: '' },
  { label: '启用', value: true },
  { label: '禁用', value: false },
];

const getRequestErrorMessage = (error: unknown, fallback: string) => {
  const axiosError = error as AxiosError<{ title?: string; detail?: string; message?: string }>;
  return axiosError.response?.data?.detail
    || axiosError.response?.data?.message
    || axiosError.response?.data?.title
    || fallback;
};

const formatDateTime = (value?: string | null) => {
  if (!value) {
    return '-';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return date.toLocaleString('zh-CN', { hour12: false });
};

const toFormData = (user: ManagedUser): UserFormData => ({
  nickname: user.nickname ?? '',
  email: user.email ?? '',
  phoneNumber: user.phoneNumber ?? '',
  givenName: user.givenName ?? '',
  familyName: user.familyName ?? '',
  title: user.title ?? '',
  userType: user.userType ?? '',
  preferredLanguage: user.preferredLanguage ?? '',
  locale: user.locale ?? '',
  timezone: user.timezone ?? '',
});

const nullableValue = (value: string) => value.trim() || null;

const utf8ByteLength = (value: string) => new TextEncoder().encode(value).length;

const isPasswordWithinByteLimit = (value: unknown) => (
  typeof value === 'string' && utf8ByteLength(value) <= PASSWORD_MAX_BYTES
);

const profileFieldsFromForm = (formData: UserFormData) => ({
  email: nullableValue(formData.email),
  phoneNumber: nullableValue(normalizePhoneNumber(formData.phoneNumber)),
  givenName: nullableValue(formData.givenName),
  familyName: nullableValue(formData.familyName),
  title: nullableValue(formData.title),
  userType: nullableValue(formData.userType),
  preferredLanguage: nullableValue(formData.preferredLanguage),
  locale: nullableValue(formData.locale),
  timezone: nullableValue(formData.timezone),
});

const UserManagementPage = () => {
  const [filters, setFilters] = useState<FilterState>(defaultFilters);
  const [appliedFilters, setAppliedFilters] = useState<FilterState>(defaultFilters);
  const [current, setCurrent] = useState(1);
  const [pageSize, setPageSize] = useState(10);
  const [sourceData, setSourceData] = useState<ManagedUser[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [summary, setSummary] = useState<{ total: number; enabled: number; disabled: number } | null>(null);
  const submissionLocks = useRef({ create: false, edit: false });
  const [tableMaxHeight, setTableMaxHeight] = useState(() => Math.max(window.innerHeight - 200, 260));
  const [compactTable, setCompactTable] = useState(() => window.innerWidth <= 640);
  const [stackedDetail, setStackedDetail] = useState(() => window.innerWidth <= 560);
  const tableWrapRef = useRef<HTMLDivElement | null>(null);
  const formRef = useRef<UserFormHandle>(null);
  const [editorVisible, setEditorVisible] = useState(false);
  const [editingUser, setEditingUser] = useState<ManagedUser | null>(null);
  const [formData, setFormData] = useState<UserFormData>(defaultFormData);
  const [submitting, setSubmitting] = useState(false);
  const [createVisible, setCreateVisible] = useState(false);
  const [createFormData, setCreateFormData] = useState<CreateUserFormData>(defaultCreateFormData);
  const [creating, setCreating] = useState(false);
  const createFormRef = useRef<UserFormHandle>(null);
  const [togglingId, setTogglingId] = useState<string | null>(null);
  const [resetPasswordVisible, setResetPasswordVisible] = useState(false);
  const [resetPasswordTarget, setResetPasswordTarget] = useState<ManagedUser | null>(null);
  const [resetPasswordFormData, setResetPasswordFormData] = useState<ResetPasswordFormData>(defaultResetPasswordFormData);
  const [resetPasswordSubmitting, setResetPasswordSubmitting] = useState(false);
  const resetPasswordFormRef = useRef<FormInstanceFunctions>(null);
  const [deleteTarget, setDeleteTarget] = useState<ManagedUser | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [revokeTokensTarget, setRevokeTokensTarget] = useState<ManagedUser | null>(null);
  const [revokingTokens, setRevokingTokens] = useState(false);
  const [detailVisible, setDetailVisible] = useState(false);
  const [detailUser, setDetailUser] = useState<ManagedUser | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [passkeys, setPasskeys] = useState<WebAuthnCredentialSummary[]>([]);
  const [revokeTarget, setRevokeTarget] = useState<WebAuthnCredentialSummary | null>(null);
  const [revokeReason, setRevokeReason] = useState('');
  const [revoking, setRevoking] = useState(false);
  const detailRequestId = useRef(0);

  const fetchData = useCallback(async () => {
    try {
      setLoading(true);
      setLoadError(null);
      const filter: { keyword?: string; isActive?: boolean; page: number; pageSize: number } = {
        page: current,
        pageSize,
      };
      if (appliedFilters.keyword.trim()) {
        filter.keyword = appliedFilters.keyword.trim();
      }
      if (appliedFilters.isActive !== '') {
        filter.isActive = appliedFilters.isActive;
      }

      const result = await getUsers(filter);
      setSourceData(result.items);
      setTotal(result.total);
      void Promise.all([getUsers({ page: 1, pageSize: 1 }), getUsers({ isActive: true, page: 1, pageSize: 1 }), getUsers({ isActive: false, page: 1, pageSize: 1 })])
        .then(([all, enabled, disabled]) => setSummary({ total: all.total, enabled: enabled.total, disabled: disabled.total }))
        .catch(() => setSummary(null));
    } catch (error) {
      console.error('Failed to fetch users:', error);
      setLoadError(getRequestErrorMessage(error, '加载用户失败'));
    } finally {
      setLoading(false);
    }
  }, [appliedFilters, current, pageSize]);

  useEffect(() => {
    void fetchData();
  }, [fetchData]);

  useEffect(() => {
    const maxPage = Math.max(1, Math.ceil(total / pageSize));
    if (current > maxPage) {
      setCurrent(maxPage);
    }
  }, [current, pageSize, total]);

  useEffect(() => {
    const updateTableMaxHeight = () => {
      setCompactTable((tableWrapRef.current?.clientWidth ?? window.innerWidth) < 720);
      setStackedDetail(window.innerWidth <= 560);
      const baseHeight = Math.max(window.innerHeight - 200, 260);
      if (!tableWrapRef.current) {
        setTableMaxHeight(baseHeight);
        return;
      }
      const top = tableWrapRef.current.getBoundingClientRect().top;
      const next = Math.max(Math.floor(window.innerHeight - top - 110), 260);
      setTableMaxHeight(next);
    };

    updateTableMaxHeight();
    const frame = window.requestAnimationFrame(updateTableMaxHeight);
    const resizeObserver = new ResizeObserver(updateTableMaxHeight);
    if (tableWrapRef.current) resizeObserver.observe(tableWrapRef.current);
    window.addEventListener('resize', updateTableMaxHeight);

    return () => {
      window.cancelAnimationFrame(frame);
      resizeObserver.disconnect();
      window.removeEventListener('resize', updateTableMaxHeight);
    };
  }, []);

  const handleQuery = () => {
    setAppliedFilters({ ...filters });
    setCurrent(1);
  };

  const handleReset = () => {
    setFilters(defaultFilters);
    setAppliedFilters(defaultFilters);
    setCurrent(1);
  };

  const handleShowCreate = () => {
    setCreateFormData(defaultCreateFormData);
    setCreateVisible(true);
  };

  const handleCloseCreate = () => {
    if (creating) {
      return;
    }
    setCreateVisible(false);
    setCreateFormData(defaultCreateFormData);
  };

  const handleCreate = async () => {
    if (!createFormRef.current || submissionLocks.current.create) {
      return;
    }

    submissionLocks.current.create = true;
    try {
      const validation = await createFormRef.current.validate();
      if (validation !== true) {
        submissionLocks.current.create = false;
        return;
      }

      if (createFormData.password !== createFormData.confirmPassword) {
        submissionLocks.current.create = false;
        MessagePlugin.warning('两次输入的密码不一致');
        return;
      }

      const request: CreateManagedUserRequest = {
        username: createFormData.username.trim(),
        password: createFormData.password,
        nickname: createFormData.nickname.trim(),
        ...profileFieldsFromForm(createFormData),
        isActive: createFormData.isActive,
      };

      setCreating(true);
      await createUser(request);
      setCreateVisible(false);
      setCreateFormData(defaultCreateFormData);
      await fetchData();
      MessagePlugin.success('用户已创建');
    } catch (error) {
      // Do not log the Axios error because its request config may contain the password.
      createFormRef.current?.setServerError(getRequestErrorMessage(error, '创建用户失败'));
    } finally {
      setCreating(false);
      submissionLocks.current.create = false;
    }
  };

  const handleEdit = (user: ManagedUser) => {
    setEditingUser(user);
    setFormData(toFormData(user));
    setEditorVisible(true);
  };

  const handleCloseEditor = () => {
    if (submitting) {
      return;
    }
    setEditorVisible(false);
    setEditingUser(null);
    setFormData(defaultFormData);
  };

  const handleSubmit = async () => {
    if (!editingUser || !formRef.current || submissionLocks.current.edit) {
      return;
    }

    submissionLocks.current.edit = true;
    try {
      const validation = await formRef.current.validate();
      if (validation !== true) {
        submissionLocks.current.edit = false;
        return;
      }

      const request: UpdateManagedUserRequest = {
        nickname: formData.nickname.trim(),
        ...profileFieldsFromForm(formData),
      };

      setSubmitting(true);
      await updateUser(editingUser.id, request);
      setEditorVisible(false);
      setEditingUser(null);
      setFormData(defaultFormData);
      await fetchData();
      MessagePlugin.success('用户资料已更新');
    } catch (error) {
      console.error('Failed to update user:', error);
      formRef.current?.setServerError(getRequestErrorMessage(error, '更新用户资料失败'));
    } finally {
      setSubmitting(false);
      submissionLocks.current.edit = false;
    }
  };

  const handleToggleStatus = async (user: ManagedUser) => {
    try {
      setTogglingId(user.id);
      await updateUserStatus(user.id, !user.isActive);
      await fetchData();
      MessagePlugin.success(user.isActive ? '用户已禁用' : '用户已启用');
    } catch (error) {
      console.error('Failed to update user status:', error);
      MessagePlugin.error(getRequestErrorMessage(error, '更新用户状态失败'));
    } finally {
      setTogglingId(null);
    }
  };

  const handleShowDelete = (user: ManagedUser) => {
    if (user.isSystemAccount) {
      return;
    }
    setDeleteTarget(user);
  };

  const handleDelete = async () => {
    if (!deleteTarget) {
      return;
    }

    try {
      setDeleting(true);
      await deleteUser(deleteTarget.id);
      setDeleteTarget(null);
      await fetchData();
      MessagePlugin.success('用户已删除');
    } catch (error) {
      MessagePlugin.error(getRequestErrorMessage(error, '删除用户失败'));
    } finally {
      setDeleting(false);
    }
  };

  const handleShowRevokeTokens = (user: ManagedUser) => {
    if (user.isSystemAccount) {
      return;
    }
    setRevokeTokensTarget(user);
  };

  const handleRevokeTokens = async () => {
    if (!revokeTokensTarget) {
      return;
    }

    try {
      setRevokingTokens(true);
      await revokeUserTokens(revokeTokensTarget.id);
      setRevokeTokensTarget(null);
      MessagePlugin.success('已撤销该用户的全部令牌');
    } catch (error) {
      MessagePlugin.error(getRequestErrorMessage(error, '撤销用户令牌失败'));
    } finally {
      setRevokingTokens(false);
    }
  };

  const handleShowResetPassword = (user: ManagedUser) => {
    setResetPasswordTarget(user);
    setResetPasswordFormData(defaultResetPasswordFormData);
    setResetPasswordVisible(true);
  };

  const handleCloseResetPassword = () => {
    if (resetPasswordSubmitting) {
      return;
    }
    setResetPasswordVisible(false);
    setResetPasswordTarget(null);
    setResetPasswordFormData(defaultResetPasswordFormData);
  };

  const handleResetPassword = async () => {
    if (!resetPasswordTarget || !resetPasswordFormRef.current) {
      return;
    }

    const validation = await resetPasswordFormRef.current.validate();
    if (validation !== true) {
      return;
    }

    if (resetPasswordFormData.newPassword !== resetPasswordFormData.confirmPassword) {
      MessagePlugin.warning('两次输入的密码不一致');
      return;
    }

    const request: ResetManagedUserPasswordRequest = {
      newPassword: resetPasswordFormData.newPassword,
    };

    try {
      setResetPasswordSubmitting(true);
      await resetUserPassword(resetPasswordTarget.id, request);
      setResetPasswordVisible(false);
      setResetPasswordTarget(null);
      setResetPasswordFormData(defaultResetPasswordFormData);
      MessagePlugin.success('用户密码已重置');
    } catch (error) {
      // Do not log the Axios error because its request config may contain the password.
      MessagePlugin.error(getRequestErrorMessage(error, '重置用户密码失败'));
    } finally {
      setResetPasswordSubmitting(false);
    }
  };

  const handleShowDetails = async (user: ManagedUser) => {
    const requestId = ++detailRequestId.current;
    setDetailVisible(true);
    setDetailUser(null);
    setPasskeys([]);
    setDetailError(null);
    setDetailLoading(true);
    setRevokeTarget(null);
    setRevokeReason('');
    setRevoking(false);
    try {
      const [loadedUser, loadedPasskeys] = await Promise.all([getUser(user.id), getUserPasskeys(user.id)]);
      if (detailRequestId.current !== requestId) return;
      setDetailUser(loadedUser);
      setPasskeys(loadedPasskeys);
    } catch (error) {
      if (detailRequestId.current !== requestId) return;
      setDetailError(getRequestErrorMessage(error, '加载用户详情失败'));
    } finally {
      if (detailRequestId.current === requestId) setDetailLoading(false);
    }
  };

  const handleCloseDetails = () => {
    detailRequestId.current++;
    setDetailVisible(false);
    setDetailUser(null);
    setPasskeys([]);
    setDetailError(null);
    setDetailLoading(false);
    setRevokeTarget(null);
    setRevokeReason('');
    setRevoking(false);
  };

  const handleRevokePasskey = async () => {
    if (!detailUser || !revokeTarget || !revokeReason.trim()) {
      MessagePlugin.warning('请填写撤销原因');
      return;
    }
    const userId = detailUser.id;
    const credentialId = revokeTarget.id;
    const requestId = detailRequestId.current;
    try {
      setRevoking(true);
      await revokeUserPasskey(userId, credentialId, revokeReason.trim());
      if (detailRequestId.current !== requestId) return;
      setPasskeys((previous) => previous.map((item) => item.id === credentialId
        ? { ...item, disabledAt: new Date().toISOString() }
        : item));
      setRevokeTarget(null);
      setRevokeReason('');
      MessagePlugin.success('Passkey 已撤销');
    } catch (error) {
      if (detailRequestId.current === requestId) {
        MessagePlugin.error(getRequestErrorMessage(error, '撤销 Passkey 失败'));
      }
    } finally {
      if (detailRequestId.current === requestId) setRevoking(false);
    }
  };

  const columns: TableProps<ManagedUser>['columns'] = [
    {
      colKey: 'username', title: '用户 / 用户名', width: 205,
      cell: ({ row }) => <div className="user-management-user-cell">
        <Avatar className="user-management-avatar">{(row.nickname || row.username).slice(0, 1).toUpperCase()}</Avatar>
        <div><strong title={row.nickname}>{row.nickname || row.username}</strong><span title={row.username}>{row.username}</span></div>
      </div>,
    },
    {
      colKey: 'contact', title: '联系信息', minWidth: 240,
      cell: ({ row }) => <div className="user-management-contact-cell"><span title={row.email || undefined}>{row.email || '未填写邮箱'}</span><small>{row.phoneNumber || '未填写手机号'}</small></div>,
    },
    { colKey: 'userType', title: '用户类型', width: 110, ellipsis: true, cell: ({ row }) => row.userType || '—' },
    { colKey: 'isActive', title: '状态', width: 108,
      cell: ({ row }) => <Tag theme={row.isActive ? 'success' : 'default'} variant="light">{row.isActive ? '已启用' : '已停用'}</Tag> },
    { colKey: 'updatedAt', title: '更新时间', width: 174, cell: ({ row }) => formatDateTime(row.updatedAt) },
    { colKey: 'createdAt', title: '创建时间', width: 174, cell: ({ row }) => formatDateTime(row.createdAt) },
    {
      colKey: 'operation', title: '操作', width: 280, fixed: compactTable ? undefined : 'right',
      cell: ({ row }) => <div className="user-management-row-actions">
        <Button variant="text" theme="primary" icon={<BrowseIcon />} onClick={() => void handleShowDetails(row)}>查看</Button>
        {row.isSystemAccount ? <Tag theme="default" variant="light-outline">系统账号受保护</Tag> : <>
          <Button variant="text" theme="primary" icon={<EditIcon />} onClick={() => handleEdit(row)}>编辑</Button>
          <Button variant="text" theme="danger" icon={<DeleteIcon />} onClick={() => handleShowDelete(row)}>删除</Button>
          <Dropdown trigger="click" placement="bottom-right" minColumnWidth={150} options={[
            { content: row.isActive ? '停用账号' : '启用账号', value: 'status', prefixIcon: <PoweroffIcon />, disabled: togglingId === row.id, onClick: () => void handleToggleStatus(row) },
            { content: '重置密码', value: 'password', prefixIcon: <KeyIcon />, onClick: () => handleShowResetPassword(row) },
            { content: '撤销全部令牌', value: 'tokens', theme: 'error', prefixIcon: <PoweroffIcon />, onClick: () => handleShowRevokeTokens(row) },
          ]}><Button variant="text" theme="default" aria-label={`更多操作：${row.username}`} icon={<EllipsisIcon />} /></Dropdown>
        </>}
      </div>,
    },
  ];

  return (
    <div className="user-management-page">
      <PageActions><Button theme="primary" icon={<UserAddIcon />} onClick={handleShowCreate}>新增用户</Button></PageActions>
      <header className="user-management-page__header"><Typography.Title level="h1">用户管理</Typography.Title><Typography.Paragraph className="user-management-page__description">维护组织成员、登录权限与联系信息</Typography.Paragraph></header>
      <div className="user-management-stats" aria-label="用户统计">
        {[
          { label: '用户总数', value: summary?.total, icon: <UsergroupIcon />, tone: 'primary' },
          { label: '已启用', value: summary?.enabled, icon: <UserIcon />, tone: 'success' },
          { label: '已停用', value: summary?.disabled, icon: <PoweroffIcon />, tone: 'neutral' },
          { label: '本页 SCIM 用户', value: sourceData.filter((user) => user.externalId).length, icon: <RefreshIcon />, tone: 'warning' },
        ].map((stat) => <Card key={stat.label} className={`user-management-stat-card user-management-stat--${stat.tone}`}><div className="user-management-stat"><div className="user-management-stat__icon">{stat.icon}</div><Statistic title={stat.label} value={stat.value ?? 0} loading={stat.value == null} /></div></Card>)}
      </div>
      <Drawer
        visible={createVisible}
        className="user-management-drawer user-management-create-drawer"
        header={<div className="user-management-drawer__heading"><strong>新增用户</strong><p>填写账号信息，创建后即可用于登录</p></div>}
        onClose={handleCloseCreate}
        footer={<div className="user-management-drawer__footer"><span>* 为必填项</span><Space>
          <Button variant="outline" onClick={handleCloseCreate} disabled={creating}>取消</Button>
          <Button theme="primary" loading={creating} onClick={() => void handleCreate()}>创建用户</Button>
        </Space></div>}
        size="min(660px, 100vw)"
        destroyOnClose
      >
        <UserForm ref={createFormRef} mode="create" value={createFormData} disabled={creating}
          onChange={(field, value) => setCreateFormData((previous) => ({ ...previous, [field]: value }))}
          onSubmit={() => void handleCreate()} />
      </Drawer>

      <Dialog
        visible={Boolean(deleteTarget)}
        header="删除这个用户？"
        width="min(480px, calc(100vw - 32px))"
        theme="danger"
        confirmBtn="确认删除"
        cancelBtn="取消"
        confirmLoading={deleting}
        destroyOnClose
        onClose={() => {
          if (!deleting) {
            setDeleteTarget(null);
          }
        }}
        onConfirm={() => void handleDelete()}
      >
        <div className="user-management-confirm-text">
          <p>将永久删除该用户及其关联凭据。此操作无法撤销。</p>
          <div className="user-management-delete-identity"><Avatar className="user-management-avatar">{(deleteTarget?.nickname || deleteTarget?.username || 'U').slice(0, 1)}</Avatar><div><strong>{deleteTarget?.nickname || deleteTarget?.username}</strong><span>{deleteTarget?.username} · {deleteTarget?.email || '未填写邮箱'}</span></div></div>
          <p>只想暂停访问？可使用用户菜单中的“停用账号”。</p>
        </div>
      </Dialog>

      <Dialog
        visible={Boolean(revokeTokensTarget)}
        header="撤销全部令牌"
        width="min(520px, calc(100vw - 32px))"
        theme="warning"
        confirmBtn="确认撤销"
        cancelBtn="取消"
        confirmLoading={revokingTokens}
        destroyOnClose
        onClose={() => {
          if (!revokingTokens) {
            setRevokeTokensTarget(null);
          }
        }}
        onConfirm={() => void handleRevokeTokens()}
      >
        <div className="user-management-confirm-text">
          <p>
            将撤销用户 <strong>{revokeTokensTarget?.username}</strong> 的全部令牌。该用户当前所有访问令牌、刷新令牌和会话都会立即失效，需要重新登录。若账号仍处于启用状态，该用户可以重新登录。
          </p>
          <p>如需持续禁止该用户访问，请在撤销后禁用账号。</p>
        </div>
      </Dialog>

      <Dialog
        visible={resetPasswordVisible}
        className="user-management-reset-password-dialog"
        header="重置用户密码"
        width="min(440px, calc(100vw - 32px))"
        confirmBtn="重置密码"
        cancelBtn="取消"
        confirmLoading={resetPasswordSubmitting}
        destroyOnClose
        onClose={handleCloseResetPassword}
        onConfirm={() => void handleResetPassword()}
      >
        {resetPasswordTarget && (
          <Form
            className="user-management-password-form"
            key={resetPasswordTarget.id}
            ref={resetPasswordFormRef}
            layout="vertical"
            labelAlign="top"
            colon
            initialData={resetPasswordFormData}
          >
            <div className="user-management-password-form__target">
              为用户 <strong>{resetPasswordTarget.username}</strong> 设置新密码
            </div>
            <Form.FormItem
              label="新密码"
              name="newPassword"
              rules={[
                { required: true, message: '请输入新密码', type: 'error' },
                { whitespace: true, message: '新密码不能只包含空格', type: 'error' },
                {
                  validator: isPasswordWithinByteLimit,
                  message: `密码不能超过 ${PASSWORD_MAX_BYTES} 个 UTF-8 字节`,
                  type: 'error',
                },
              ]}
              help={`密码最多 ${PASSWORD_MAX_BYTES} 字节，中文字符通常占 3 字节`}
            >
              <Input
                type="password"
                autocomplete="new-password"
                value={resetPasswordFormData.newPassword}
                placeholder="请输入新密码"
                onChange={(value) => setResetPasswordFormData((previous) => ({ ...previous, newPassword: value }))}
              />
            </Form.FormItem>
            <Form.FormItem
              label="确认新密码"
              name="confirmPassword"
              rules={[
                { required: true, message: '请再次输入新密码', type: 'error' },
                { whitespace: true, message: '确认密码不能只包含空格', type: 'error' },
                {
                  validator: (value) => value === resetPasswordFormData.newPassword,
                  message: '两次输入的密码不一致',
                  type: 'error',
                },
                {
                  validator: isPasswordWithinByteLimit,
                  message: `密码不能超过 ${PASSWORD_MAX_BYTES} 个 UTF-8 字节`,
                  type: 'error',
                },
              ]}
            >
              <Input
                type="password"
                autocomplete="new-password"
                value={resetPasswordFormData.confirmPassword}
                placeholder="请再次输入新密码"
                onChange={(value) => setResetPasswordFormData((previous) => ({ ...previous, confirmPassword: value }))}
              />
            </Form.FormItem>
          </Form>
        )}
      </Dialog>

      <Dialog
        visible={Boolean(revokeTarget)}
        header="撤销 Passkey"
        width="min(480px, calc(100vw - 32px))"
        confirmBtn="确认撤销"
        cancelBtn="取消"
        confirmLoading={revoking}
        onClose={() => {
          if (!revoking) {
            setRevokeTarget(null);
            setRevokeReason('');
          }
        }}
        onConfirm={() => void handleRevokePasskey()}
      >
        <div className="user-management-revoke__target">
          将撤销 <strong>{revokeTarget?.displayName || 'Passkey'}</strong>，该凭据之后不能再用于登录，并退出该用户所有设备、撤销现有令牌。请填写单行原因。
        </div>
        <Textarea
          value={revokeReason}
          maxlength={500}
          placeholder="请输入撤销原因"
          autosize={{ minRows: 3, maxRows: 6 }}
          onChange={setRevokeReason}
        />
      </Dialog>

      <Drawer
        visible={detailVisible}
        className="user-management-detail-drawer"
        header="用户详情"
        onClose={handleCloseDetails}
        footer={detailUser && !detailLoading && !detailError ? <div className="user-management-drawer__footer"><span>{detailUser.isSystemAccount ? '系统账号受保护' : '账号与安全资料'}</span>{!detailUser.isSystemAccount && <Button theme="primary" icon={<EditIcon />} onClick={() => { const user = detailUser; handleCloseDetails(); handleEdit(user); }}>编辑资料</Button>}</div> : false}
        size="min(660px, 100vw)"
        destroyOnClose
      >
        {detailLoading ? (
          <div className="user-management-detail-state"><Loading text="正在加载用户详情…" /></div>
        ) : detailError ? (
          <Alert className="user-management-detail-error" theme="error" title="加载用户详情失败" message={detailError} />
        ) : detailUser ? (
          <>
            <div className="user-management-detail-hero"><Avatar className="user-management-avatar" size="large">{(detailUser.nickname || detailUser.username).slice(0, 1)}</Avatar><div><strong>{detailUser.nickname || detailUser.username}</strong><span>{detailUser.username}</span></div><Tag theme={detailUser.isActive ? 'success' : 'default'} variant="light">{detailUser.isActive ? '已启用' : '已停用'}</Tag></div>
            <section className="user-management-detail-section" aria-labelledby="user-detail-profile-title">
              <Typography.Title level="h3"><span id="user-detail-profile-title">基本资料</span></Typography.Title>
              <Descriptions className="user-management-detail-descriptions" column={stackedDetail ? 1 : 2} itemLayout="vertical">
                <Descriptions.DescriptionsItem label="用户名" content={detailUser.username} />
                <Descriptions.DescriptionsItem label="昵称" content={detailUser.nickname || '-'} />
                <Descriptions.DescriptionsItem label="邮箱" content={detailUser.email || '-'} />
                <Descriptions.DescriptionsItem label="手机号" content={detailUser.phoneNumber || '-'} />
                <Descriptions.DescriptionsItem label="姓名" content={[detailUser.familyName, detailUser.givenName].filter(Boolean).join(' ') || '-'} />
                <Descriptions.DescriptionsItem label="职务" content={detailUser.title || '-'} />
                <Descriptions.DescriptionsItem label="用户类型" content={detailUser.userType || '-'} />
                <Descriptions.DescriptionsItem label="状态" content={<Tag theme={detailUser.isActive ? 'success' : 'default'}>{detailUser.isActive ? '启用' : '禁用'}</Tag>} />
                <Descriptions.DescriptionsItem label="创建时间" content={formatDateTime(detailUser.createdAt)} />
                <Descriptions.DescriptionsItem label="更新时间" content={formatDateTime(detailUser.updatedAt)} />
                <Descriptions.DescriptionsItem label="SCIM External ID" content={detailUser.externalId || '-'} />
                <Descriptions.DescriptionsItem label="首选语言 / 地区" content={`${detailUser.preferredLanguage || '—'} / ${detailUser.locale || '—'}`} />
                <Descriptions.DescriptionsItem label="时区" content={detailUser.timezone || '-'} />
              </Descriptions>
            </section>
            <section className="user-management-detail-section" aria-labelledby="user-detail-passkeys-title">
              <Typography.Title level="h3"><span id="user-detail-passkeys-title">Passkey 凭据</span></Typography.Title>
              {passkeys.length === 0 ? <Empty description="该用户暂无 Passkey 凭据" /> : (
                <div className="user-management-passkeys">
                  {passkeys.map((credential) => (
                    <Card className="user-management-passkey" key={credential.id} bodyStyle={{ padding: 14 }}>
                      <div className="user-management-passkey__heading">
                        <strong>{credential.displayName || 'Passkey'}</strong>
                        {credential.disabledAt
                          ? <Tag theme="default" variant="outline">已撤销</Tag>
                          : <Tag theme="success" variant="outline">启用</Tag>}
                      </div>
                      <Descriptions className="user-management-detail-descriptions user-management-detail-descriptions--compact" column={stackedDetail ? 1 : 2} itemLayout="vertical" size="small">
                        <Descriptions.DescriptionsItem label="创建时间" content={formatDateTime(credential.createdAt)} />
                        <Descriptions.DescriptionsItem label="最近使用" content={formatDateTime(credential.lastUsedAt)} />
                        <Descriptions.DescriptionsItem label="撤销时间" content={formatDateTime(credential.disabledAt)} />
                        <Descriptions.DescriptionsItem label="备份资格" content={credential.isBackupEligible ? '支持' : '不支持'} />
                        <Descriptions.DescriptionsItem label="已备份" content={credential.isBackedUp ? '是' : '否'} />
                      </Descriptions>
                      {!credential.disabledAt && (
                        <Button
                          size="small"
                          variant="text"
                          theme="danger"
                          onClick={() => {
                            setRevokeTarget(credential);
                            setRevokeReason('');
                          }}
                        >
                          撤销凭据
                        </Button>
                      )}
                    </Card>
                  ))}
                </div>
              )}
            </section>
          </>
        ) : null}
      </Drawer>

      <Drawer
        visible={editorVisible}
        className="user-management-drawer"
        header={<div className="user-management-drawer__heading"><strong>编辑用户资料</strong><p>{editingUser?.nickname} · {editingUser?.username}</p></div>}
        onClose={handleCloseEditor}
        footer={<div className="user-management-drawer__footer"><span>修改将立即生效</span><Space>
          <Button variant="outline" onClick={handleCloseEditor} disabled={submitting}>取消</Button>
          <Button theme="primary" loading={submitting} onClick={() => void handleSubmit()}>保存修改</Button>
        </Space></div>}
        size="min(660px, 100vw)"
        destroyOnClose
      >
        {editingUser && (
          <>
            <div className="user-management-identity">
              <div>
                <span className="user-management-identity__label">用户名</span>
                <strong>{editingUser.username}</strong>
              </div>
              <div>
                <span className="user-management-identity__label">SCIM External ID</span>
                <code>{editingUser.externalId || '-'}</code>
              </div>
            </div>
            <UserForm key={editingUser.id} ref={formRef} mode="edit" value={formData} disabled={submitting}
              onChange={(field, value) => setFormData((previous) => ({ ...previous, [field]: value }))}
              onSubmit={() => void handleSubmit()} />
          </>
        )}
      </Drawer>

      <Card className="page-filter-bar" bodyStyle={{ padding: 0 }}>
        <Form layout="inline" className="user-management-filter-form">
          <Form.FormItem label="关键词">
            <Input
              clearable
              value={filters.keyword}
              prefixIcon={<SearchIcon />}
              placeholder="用户名、昵称、邮箱或手机号"
              style={{ width: 360 }}
              onChange={(value) => setFilters((prev) => ({ ...prev, keyword: value }))}
              onEnter={handleQuery}
            />
          </Form.FormItem>
          <Form.FormItem label="状态">
            <Select
              value={filters.isActive}
              options={statusOptions}
              style={{ width: 140 }}
              onChange={(value) => setFilters((prev) => ({ ...prev, isActive: value === true || value === false ? value : '' }))}
            />
          </Form.FormItem>
          <Form.FormItem>
            <Space>
              <Button theme="primary" icon={<SearchIcon />} onClick={handleQuery}>查询</Button>
              <Button variant="base" icon={<RefreshIcon />} onClick={handleReset}>重置</Button>
            </Space>
          </Form.FormItem>
        </Form>
      </Card>

      <Card className="page-table-section user-management-table-section" bodyStyle={{ padding: 0 }}>
        {loadError && (
          <Alert
            className="user-management-load-error"
            theme="error"
            message={loadError}
            operation={<Button variant="text" theme="primary" onClick={() => void fetchData()}>重新加载</Button>}
          />
        )}
        <div ref={tableWrapRef}>
          <Table
            rowKey="id"
            columns={columns}
            data={sourceData}
            verticalAlign="middle"
            maxHeight={tableMaxHeight}
            tableLayout="fixed"
            empty={<Empty description={appliedFilters.keyword || appliedFilters.isActive !== '' ? '没有找到匹配的用户，请调整关键词或重置筛选' : '还没有用户，点击右上方“新增用户”开始管理组织成员'} />}
            resizable
            loading={loading}
          />
        </div>

        <div className="user-management-pagination"><span>共 {total} 个用户</span>
          <Pagination
            total={total}
            current={current}
            pageSize={pageSize}
            pageSizeOptions={[10, 20, 50]}
            showPageSize
            showJumper
            onCurrentChange={(next) => setCurrent(next)}
            onPageSizeChange={(size) => {
              setPageSize(Number(size));
              setCurrent(1);
            }}
          />
        </div>
      </Card>
    </div>
  );
};

export default UserManagementPage;
