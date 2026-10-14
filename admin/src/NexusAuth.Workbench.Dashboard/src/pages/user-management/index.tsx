import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { AxiosError } from 'axios';
import { Button, Dialog, Drawer, Empty, Form, Input, MessagePlugin, Pagination, Select, Space, Switch, Table, Tag, Textarea, type TableProps } from 'tdesign-react';
import { DeleteIcon, EditIcon, KeyIcon, PoweroffIcon, RefreshIcon, SearchIcon, UserAddIcon } from 'tdesign-icons-react';
import { createUser, deleteUser, getUser, getUserPasskeys, getUsers, resetUserPassword, revokeUserPasskey, revokeUserTokens, updateUser, updateUserStatus, type CreateManagedUserRequest, type ManagedUser, type ResetManagedUserPasswordRequest, type UpdateManagedUserRequest, type WebAuthnCredentialSummary } from '../../api/user';
import './style.less';

type FilterState = {
  keyword: string;
  isActive: '' | boolean;
};

type UserFormData = {
  nickname: string;
  email: string;
  phoneNumber: string;
  givenName: string;
  familyName: string;
  title: string;
  userType: string;
  preferredLanguage: string;
  locale: string;
  timezone: string;
};

type ResetPasswordFormData = {
  newPassword: string;
  confirmPassword: string;
};

type CreateUserFormData = UserFormData & {
  username: string;
  password: string;
  confirmPassword: string;
  isActive: boolean;
};

const PASSWORD_MAX_BYTES = 72;

const FIELD_MAX_LENGTH = {
  username: 100,
  nickname: 100,
  email: 256,
  phoneNumber: 20,
  givenName: 100,
  familyName: 100,
  title: 256,
  userType: 100,
  preferredLanguage: 35,
  locale: 35,
  timezone: 100,
} as const;

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
  phoneNumber: nullableValue(formData.phoneNumber),
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
  const [tableMaxHeight, setTableMaxHeight] = useState(() => Math.max(window.innerHeight - 200, 260));
  const tableWrapRef = useRef<HTMLDivElement | null>(null);
  const formRef = useRef<any>(null);
  const [editorVisible, setEditorVisible] = useState(false);
  const [editingUser, setEditingUser] = useState<ManagedUser | null>(null);
  const [formData, setFormData] = useState<UserFormData>(defaultFormData);
  const [submitting, setSubmitting] = useState(false);
  const [createVisible, setCreateVisible] = useState(false);
  const [createFormData, setCreateFormData] = useState<CreateUserFormData>(defaultCreateFormData);
  const [creating, setCreating] = useState(false);
  const createFormRef = useRef<any>(null);
  const [togglingId, setTogglingId] = useState<string | null>(null);
  const [resetPasswordVisible, setResetPasswordVisible] = useState(false);
  const [resetPasswordTarget, setResetPasswordTarget] = useState<ManagedUser | null>(null);
  const [resetPasswordFormData, setResetPasswordFormData] = useState<ResetPasswordFormData>(defaultResetPasswordFormData);
  const [resetPasswordSubmitting, setResetPasswordSubmitting] = useState(false);
  const resetPasswordFormRef = useRef<any>(null);
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
    } catch (error) {
      console.error('Failed to fetch users:', error);
      MessagePlugin.error(getRequestErrorMessage(error, '加载用户失败'));
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
    window.addEventListener('resize', updateTableMaxHeight);

    return () => {
      window.cancelAnimationFrame(frame);
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
    if (!createFormRef.current) {
      return;
    }

    const validation = await createFormRef.current.validate();
    if (validation !== true) {
      return;
    }

    if (createFormData.password !== createFormData.confirmPassword) {
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

    try {
      setCreating(true);
      await createUser(request);
      setCreateVisible(false);
      setCreateFormData(defaultCreateFormData);
      await fetchData();
      MessagePlugin.success('用户已创建');
    } catch (error) {
      // Do not log the Axios error because its request config may contain the password.
      MessagePlugin.error(getRequestErrorMessage(error, '创建用户失败'));
    } finally {
      setCreating(false);
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
    if (!editingUser || !formRef.current) {
      return;
    }

    const validation = await formRef.current.validate();
    if (validation !== true) {
      return;
    }

    const request: UpdateManagedUserRequest = {
      nickname: formData.nickname.trim(),
      ...profileFieldsFromForm(formData),
    };

    try {
      setSubmitting(true);
      await updateUser(editingUser.id, request);
      setEditorVisible(false);
      setEditingUser(null);
      setFormData(defaultFormData);
      await fetchData();
      MessagePlugin.success('用户资料已更新');
    } catch (error) {
      console.error('Failed to update user:', error);
      MessagePlugin.error(getRequestErrorMessage(error, '更新用户资料失败'));
    } finally {
      setSubmitting(false);
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

  const columns: TableProps<ManagedUser>['columns'] = useMemo(
    () => [
      { colKey: 'username', title: '用户名', width: 150, ellipsis: true },
      { colKey: 'nickname', title: '昵称', width: 140, ellipsis: true },
      {
        colKey: 'name',
        title: '姓名',
        width: 140,
        ellipsis: true,
        cell: ({ row }) => [row.familyName, row.givenName].filter(Boolean).join(' ') || '-',
      },
      { colKey: 'email', title: '邮箱', minWidth: 210, ellipsis: true, cell: ({ row }) => row.email || '-' },
      { colKey: 'phoneNumber', title: '手机号', width: 150, ellipsis: true, cell: ({ row }) => row.phoneNumber || '-' },
      { colKey: 'title', title: '职务', width: 140, ellipsis: true, cell: ({ row }) => row.title || '-' },
      { colKey: 'userType', title: '用户类型', width: 130, ellipsis: true, cell: ({ row }) => row.userType || '-' },
      {
        colKey: 'isActive',
        title: '状态',
        width: 115,
        cell: ({ row }) => (
          <Switch
            value={row.isActive}
            loading={togglingId === row.id}
            disabled={row.isSystemAccount}
            label={({ value }) => value ? '启用' : '禁用'}
            onChange={() => void handleToggleStatus(row)}
          />
        ),
      },
      {
        colKey: 'source',
        title: '来源',
        width: 100,
        cell: ({ row }) => row.externalId
          ? <Tag theme="primary" variant="outline">SCIM</Tag>
          : <Tag theme="default" variant="outline">本地</Tag>,
      },
      { colKey: 'updatedAt', title: '更新时间', width: 180, cell: ({ row }) => formatDateTime(row.updatedAt) },
      {
        colKey: 'operation',
        title: '操作',
        width: 190,
        fixed: 'right',
        cell: ({ row }) => (
          <Space direction="vertical" size={4} style={{ alignItems: 'flex-start' }}>
            <Button variant="text" theme="primary" onClick={() => void handleShowDetails(row)}>
              用户详情
            </Button>
            {row.isSystemAccount ? (
              <Tag theme="default" variant="light-outline">系统账号受保护</Tag>
            ) : (
              <>
                <Button variant="text" theme="primary" icon={<EditIcon />} onClick={() => handleEdit(row)}>
                  编辑
                </Button>
                <Button variant="text" theme="danger" icon={<KeyIcon />} onClick={() => handleShowResetPassword(row)}>
                  重置密码
                </Button>
                <Button variant="text" theme="warning" icon={<PoweroffIcon />} onClick={() => handleShowRevokeTokens(row)}>
                  撤销全部令牌
                </Button>
                <Button variant="text" theme="danger" icon={<DeleteIcon />} onClick={() => handleShowDelete(row)}>
                  删除用户
                </Button>
              </>
            )}
          </Space>
        ),
      },
    ],
    [fetchData, togglingId],
  );

  return (
    <div className="user-management-page">
      <Drawer
        visible={createVisible}
        className="user-management-drawer user-management-create-drawer"
        header="新增用户"
        onClose={handleCloseCreate}
        footer={false}
        size="min(680px, 100vw)"
        destroyOnClose
      >
        <Form
          className="user-management-form"
          ref={createFormRef}
          layout="vertical"
          labelAlign="top"
          colon
          initialData={createFormData}
        >
          <div className="user-management-form__grid">
            <Form.FormItem
              label="用户名"
              name="username"
              rules={[
                { required: true, message: '请输入用户名', type: 'error' },
                { whitespace: true, message: '用户名不能只包含空格', type: 'error' },
              ]}
            >
              <Input
                value={createFormData.username}
                maxlength={FIELD_MAX_LENGTH.username}
                autocomplete="username"
                placeholder="请输入用户名"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, username: value }))}
              />
            </Form.FormItem>
            <Form.FormItem
              label="昵称"
              name="nickname"
              rules={[
                { required: true, message: '请输入昵称', type: 'error' },
                { whitespace: true, message: '昵称不能只包含空格', type: 'error' },
              ]}
            >
              <Input
                value={createFormData.nickname}
                maxlength={FIELD_MAX_LENGTH.nickname}
                placeholder="请输入昵称"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, nickname: value }))}
              />
            </Form.FormItem>
            <Form.FormItem
              label="密码"
              name="password"
              rules={[
                { required: true, message: '请输入密码', type: 'error' },
                { whitespace: true, message: '密码不能只包含空格', type: 'error' },
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
                value={createFormData.password}
                placeholder="请输入密码"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, password: value }))}
              />
            </Form.FormItem>
            <Form.FormItem
              label="确认密码"
              name="confirmPassword"
              rules={[
                { required: true, message: '请再次输入密码', type: 'error' },
                { whitespace: true, message: '确认密码不能只包含空格', type: 'error' },
                {
                  validator: (value) => value === createFormData.password,
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
                value={createFormData.confirmPassword}
                placeholder="请再次输入密码"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, confirmPassword: value }))}
              />
            </Form.FormItem>
            <Form.FormItem label="邮箱" name="email">
              <Input
                value={createFormData.email}
                maxlength={FIELD_MAX_LENGTH.email}
                clearable
                type="text"
                placeholder="请输入邮箱"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, email: value }))}
              />
            </Form.FormItem>
            <Form.FormItem label="手机号" name="phoneNumber">
              <Input
                value={createFormData.phoneNumber}
                maxlength={FIELD_MAX_LENGTH.phoneNumber}
                clearable
                type="tel"
                placeholder="请输入手机号"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, phoneNumber: value }))}
              />
            </Form.FormItem>
            <Form.FormItem label="名 (Given name)" name="givenName">
              <Input
                value={createFormData.givenName}
                maxlength={FIELD_MAX_LENGTH.givenName}
                clearable
                placeholder="请输入名"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, givenName: value }))}
              />
            </Form.FormItem>
            <Form.FormItem label="姓 (Family name)" name="familyName">
              <Input
                value={createFormData.familyName}
                maxlength={FIELD_MAX_LENGTH.familyName}
                clearable
                placeholder="请输入姓"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, familyName: value }))}
              />
            </Form.FormItem>
            <Form.FormItem label="职务 (Title)" name="title">
              <Input
                value={createFormData.title}
                maxlength={FIELD_MAX_LENGTH.title}
                clearable
                placeholder="请输入职务"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, title: value }))}
              />
            </Form.FormItem>
            <Form.FormItem label="用户类型 (User type)" name="userType">
              <Input
                value={createFormData.userType}
                maxlength={FIELD_MAX_LENGTH.userType}
                clearable
                placeholder="请输入用户类型"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, userType: value }))}
              />
            </Form.FormItem>
            <Form.FormItem label="首选语言" name="preferredLanguage">
              <Input
                value={createFormData.preferredLanguage}
                maxlength={FIELD_MAX_LENGTH.preferredLanguage}
                clearable
                placeholder="如 zh-CN"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, preferredLanguage: value }))}
              />
            </Form.FormItem>
            <Form.FormItem label="Locale" name="locale">
              <Input
                value={createFormData.locale}
                maxlength={FIELD_MAX_LENGTH.locale}
                clearable
                placeholder="如 zh-CN"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, locale: value }))}
              />
            </Form.FormItem>
            <Form.FormItem label="时区" name="timezone">
              <Input
                value={createFormData.timezone}
                maxlength={FIELD_MAX_LENGTH.timezone}
                clearable
                placeholder="如 Asia/Shanghai"
                onChange={(value) => setCreateFormData((previous) => ({ ...previous, timezone: value }))}
              />
            </Form.FormItem>
          </div>
          <Form.FormItem label="账号状态" name="isActive">
            <Switch
              value={createFormData.isActive}
              label={({ value }) => value ? '启用' : '禁用'}
              onChange={(value) => setCreateFormData((previous) => ({ ...previous, isActive: value }))}
            />
          </Form.FormItem>
        </Form>
        <div className="user-management-drawer__footer">
          <Button variant="base" onClick={handleCloseCreate} disabled={creating}>取消</Button>
          <Button theme="primary" loading={creating} onClick={() => void handleCreate()}>创建</Button>
        </div>
      </Drawer>

      <Dialog
        visible={Boolean(deleteTarget)}
        header="删除用户"
        width="min(480px, calc(100vw - 32px))"
        theme="danger"
        confirmBtn="永久删除"
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
          将永久删除用户 <strong>{deleteTarget?.username}</strong>。该用户及其凭据将被永久删除且无法恢复，现有登录凭据会立即失效。
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
        footer={false}
        size="min(760px, 100vw)"
        destroyOnClose
      >
        {detailLoading ? (
          <div className="user-management-detail-state">正在加载用户详情…</div>
        ) : detailError ? (
          <div className="user-management-detail-state user-management-detail-state--error" role="alert">{detailError}</div>
        ) : detailUser ? (
          <>
            <section className="user-management-detail-section" aria-labelledby="user-detail-profile-title">
              <h3 id="user-detail-profile-title">基本资料</h3>
              <dl className="user-management-detail-grid">
                <div><dt>用户名</dt><dd>{detailUser.username}</dd></div>
                <div><dt>昵称</dt><dd>{detailUser.nickname || '-'}</dd></div>
                <div><dt>邮箱</dt><dd>{detailUser.email || '-'}</dd></div>
                <div><dt>手机号</dt><dd>{detailUser.phoneNumber || '-'}</dd></div>
                <div><dt>姓名</dt><dd>{[detailUser.familyName, detailUser.givenName].filter(Boolean).join(' ') || '-'}</dd></div>
                <div><dt>职务</dt><dd>{detailUser.title || '-'}</dd></div>
                <div><dt>用户类型</dt><dd>{detailUser.userType || '-'}</dd></div>
                <div><dt>状态</dt><dd><Tag theme={detailUser.isActive ? 'success' : 'default'}>{detailUser.isActive ? '启用' : '禁用'}</Tag></dd></div>
                <div><dt>创建时间</dt><dd>{formatDateTime(detailUser.createdAt)}</dd></div>
                <div><dt>更新时间</dt><dd>{formatDateTime(detailUser.updatedAt)}</dd></div>
                <div><dt>SCIM External ID</dt><dd>{detailUser.externalId || '-'}</dd></div>
                <div><dt>时区</dt><dd>{detailUser.timezone || '-'}</dd></div>
              </dl>
            </section>
            <section className="user-management-detail-section" aria-labelledby="user-detail-passkeys-title">
              <h3 id="user-detail-passkeys-title">Passkey 凭据</h3>
              {passkeys.length === 0 ? <Empty description="该用户暂无 Passkey 凭据" /> : (
                <div className="user-management-passkeys">
                  {passkeys.map((credential) => (
                    <article className="user-management-passkey" key={credential.id}>
                      <div className="user-management-passkey__heading">
                        <strong>{credential.displayName || 'Passkey'}</strong>
                        {credential.disabledAt
                          ? <Tag theme="default" variant="outline">已撤销</Tag>
                          : <Tag theme="success" variant="outline">启用</Tag>}
                      </div>
                      <dl className="user-management-detail-grid user-management-detail-grid--compact">
                        <div><dt>创建时间</dt><dd>{formatDateTime(credential.createdAt)}</dd></div>
                        <div><dt>最近使用</dt><dd>{formatDateTime(credential.lastUsedAt)}</dd></div>
                        <div><dt>撤销时间</dt><dd>{formatDateTime(credential.disabledAt)}</dd></div>
                        <div><dt>备份资格</dt><dd>{credential.isBackupEligible ? '支持' : '不支持'}</dd></div>
                        <div><dt>已备份</dt><dd>{credential.isBackedUp ? '是' : '否'}</dd></div>
                      </dl>
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
                    </article>
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
        header="编辑用户资料"
        onClose={handleCloseEditor}
        footer={false}
        size="min(620px, 100vw)"
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
            <Form
              className="user-management-form"
              key={editingUser.id}
              ref={formRef}
              layout="vertical"
              labelAlign="top"
              colon
              initialData={formData}
            >
              <div className="user-management-form__grid">
                <Form.FormItem
                  label="昵称"
                  name="nickname"
                  rules={[
                    { required: true, message: '请输入昵称', type: 'error' },
                    { whitespace: true, message: '昵称不能只包含空格', type: 'error' },
                  ]}
                >
                  <Input
                    value={formData.nickname}
                    maxlength={FIELD_MAX_LENGTH.nickname}
                    placeholder="请输入昵称"
                    onChange={(value) => setFormData((prev) => ({ ...prev, nickname: value }))}
                  />
                </Form.FormItem>
                <Form.FormItem label="邮箱" name="email">
                  <Input value={formData.email} maxlength={FIELD_MAX_LENGTH.email} clearable type="text" placeholder="请输入邮箱" onChange={(value) => setFormData((prev) => ({ ...prev, email: value }))} />
                </Form.FormItem>
                <Form.FormItem label="手机号" name="phoneNumber">
                  <Input value={formData.phoneNumber} maxlength={FIELD_MAX_LENGTH.phoneNumber} clearable type="tel" placeholder="请输入手机号" onChange={(value) => setFormData((prev) => ({ ...prev, phoneNumber: value }))} />
                </Form.FormItem>
                <Form.FormItem label="名 (Given name)" name="givenName">
                  <Input value={formData.givenName} maxlength={FIELD_MAX_LENGTH.givenName} clearable placeholder="请输入名" onChange={(value) => setFormData((prev) => ({ ...prev, givenName: value }))} />
                </Form.FormItem>
                <Form.FormItem label="姓 (Family name)" name="familyName">
                  <Input value={formData.familyName} maxlength={FIELD_MAX_LENGTH.familyName} clearable placeholder="请输入姓" onChange={(value) => setFormData((prev) => ({ ...prev, familyName: value }))} />
                </Form.FormItem>
                <Form.FormItem label="职务 (Title)" name="title">
                  <Input value={formData.title} maxlength={FIELD_MAX_LENGTH.title} clearable placeholder="请输入职务" onChange={(value) => setFormData((prev) => ({ ...prev, title: value }))} />
                </Form.FormItem>
                <Form.FormItem label="用户类型 (User type)" name="userType">
                  <Input value={formData.userType} maxlength={FIELD_MAX_LENGTH.userType} clearable placeholder="请输入用户类型" onChange={(value) => setFormData((prev) => ({ ...prev, userType: value }))} />
                </Form.FormItem>
                <Form.FormItem label="首选语言" name="preferredLanguage">
                  <Input value={formData.preferredLanguage} maxlength={FIELD_MAX_LENGTH.preferredLanguage} clearable placeholder="如 zh-CN" onChange={(value) => setFormData((prev) => ({ ...prev, preferredLanguage: value }))} />
                </Form.FormItem>
                <Form.FormItem label="Locale" name="locale">
                  <Input value={formData.locale} maxlength={FIELD_MAX_LENGTH.locale} clearable placeholder="如 zh-CN" onChange={(value) => setFormData((prev) => ({ ...prev, locale: value }))} />
                </Form.FormItem>
                <Form.FormItem label="时区" name="timezone">
                  <Input value={formData.timezone} maxlength={FIELD_MAX_LENGTH.timezone} clearable placeholder="如 Asia/Shanghai" onChange={(value) => setFormData((prev) => ({ ...prev, timezone: value }))} />
                </Form.FormItem>
              </div>
            </Form>
            <div className="user-management-drawer__footer">
              <Button variant="base" onClick={handleCloseEditor} disabled={submitting}>取消</Button>
              <Button theme="primary" loading={submitting} onClick={() => void handleSubmit()}>保存</Button>
            </div>
          </>
        )}
      </Drawer>

      <div className="page-filter-bar">
        <Form layout="inline" className="user-management-filter-form">
          <Form.FormItem label="关键词">
            <Input
              clearable
              value={filters.keyword}
              prefixIcon={<SearchIcon />}
              placeholder="用户名、昵称、邮箱或手机号"
              style={{ width: 280 }}
              onChange={(value) => setFilters((prev) => ({ ...prev, keyword: value }))}
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
              <Button theme="primary" variant="outline" icon={<UserAddIcon />} onClick={handleShowCreate}>新增用户</Button>
            </Space>
          </Form.FormItem>
        </Form>
      </div>

      <div className="page-table-section">
        <div ref={tableWrapRef}>
          <Table
            rowKey="id"
            columns={columns}
            data={sourceData}
            verticalAlign="middle"
            maxHeight={tableMaxHeight}
            tableLayout="fixed"
            resizable
            loading={loading}
          />
        </div>

        <div style={{ marginTop: 16, display: 'flex', justifyContent: 'flex-end' }}>
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
      </div>
    </div>
  );
};

export default UserManagementPage;
