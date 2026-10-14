import { forwardRef, useImperativeHandle, useRef, useState } from 'react';
import { Alert, Collapse, Form, Input, Switch, Typography, type FormInstanceFunctions, type FormRule } from 'tdesign-react';
import { validateOptionalEmail, validateOptionalPhoneNumber } from './validation';

export type UserProfileData = {
  nickname: string; email: string; phoneNumber: string; givenName: string;
  familyName: string; title: string; userType: string; preferredLanguage: string;
  locale: string; timezone: string;
};

export type CreateUserData = UserProfileData & {
  username: string; password: string; confirmPassword: string; isActive: boolean;
};

type FieldName = keyof CreateUserData;
type UserFormData = UserProfileData & Partial<CreateUserData>;

export interface UserFormHandle {
  validate: () => Promise<boolean>;
  setServerError: (message: string) => void;
}

type UserFormProps = {
  mode: 'create' | 'edit';
  value: UserFormData;
  disabled: boolean;
  onChange: (field: FieldName, value: string | boolean) => void;
  onSubmit: () => void;
};

const MAX_LENGTH: Partial<Record<FieldName, number>> = {
  username: 100, nickname: 100, email: 256, phoneNumber: 20,
  givenName: 100, familyName: 100, title: 256, userType: 100,
  preferredLanguage: 35, locale: 35, timezone: 100,
};

function getErrors(result: unknown): Record<string, string> {
  if (!result || typeof result !== 'object') return {};
  const errors: Record<string, string> = {};
  for (const [field, rules] of Object.entries(result)) {
    if (!Array.isArray(rules)) continue;
    const error = rules.find((rule) => rule.result === false && rule.type !== 'warning');
    if (error?.message) errors[field] = error.message;
  }
  return errors;
}

const UserForm = forwardRef<UserFormHandle, UserFormProps>(({ mode, value, disabled, onChange, onSubmit }, ref) => {
  const formRef = useRef<FormInstanceFunctions>(null);
  const touched = useRef(new Set<string>());
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [serverError, setServerError] = useState('');
  const [submitted, setSubmitted] = useState(false);
  const errorCount = Object.keys(errors).length;

  const focusError = (messages: Record<string, string>) => {
    const field = Object.keys(messages)[0];
    if (!field) return;
    formRef.current?.getCurrentElement?.()?.querySelector<HTMLInputElement>(`input[name="${field}"]`)?.focus();
  };

  useImperativeHandle(ref, () => ({
    validate: async () => {
      setSubmitted(true);
      setServerError('');
      const result = await formRef.current?.validate();
      const messages = getErrors(result);
      setErrors(messages);
      if (result !== true) focusError(messages);
      return result === true;
    },
    setServerError: (message) => {
      setServerError(message);
      const field = /邮箱|email/i.test(message) ? 'email'
        : /手机|phone/i.test(message) ? 'phoneNumber'
          : /用户名|username/i.test(message) ? 'username' : undefined;
      if (field) {
        touched.current.add(field);
        formRef.current?.setValidateMessage({ [field]: [{ type: 'error', message }] });
        setErrors((previous) => ({ ...previous, [field]: message }));
      }
    },
  }));

  const revalidate = (field: string) => {
    touched.current.add(field);
    void formRef.current?.validate({ fields: [field] }).then((result) => {
      const nextErrors = getErrors(result);
      setErrors((previous) => {
        const next = { ...previous };
        delete next[field];
        return { ...next, ...nextErrors };
      });
    });
  };

  const rulesFor = (field: FieldName): FormRule[] => {
    const rules: FormRule[] = [];
    if (['username', 'nickname', 'password', 'confirmPassword'].includes(field)) {
      const labels: Partial<Record<FieldName, string>> = { username: '用户名', nickname: '昵称', password: '密码', confirmPassword: '确认密码' };
      const label = labels[field];
      rules.push({ required: true, message: `请输入${label}`, type: 'error', trigger: 'blur' });
      rules.push({ whitespace: true, message: `${label}不能只包含空格`, type: 'error', trigger: 'blur' });
    }
    if (field === 'password' || field === 'confirmPassword') {
      rules.push({ validator: (input) => typeof input === 'string' && new TextEncoder().encode(input).length <= 72,
        message: '密码不能超过 72 个 UTF-8 字节', type: 'error', trigger: 'blur' });
    }
    if (field === 'confirmPassword') rules.push({ validator: (input) => input === value.password,
      message: '两次输入的密码不一致', type: 'error', trigger: 'blur' });
    if (field === 'email') rules.push({ validator: validateOptionalEmail,
      message: '请输入有效的邮箱地址', type: 'error', trigger: 'blur' });
    if (field === 'phoneNumber') rules.push({ validator: validateOptionalPhoneNumber,
      message: '请输入有效的手机号', type: 'error', trigger: 'blur' });
    return rules;
  };

  const field = (name: FieldName, label: string, placeholder: string, help?: string) => (
    <Form.FormItem key={name} name={name} label={label} rules={rulesFor(name)} help={help}>
      <Input name={name} value={String(value[name] ?? '')} placeholder={placeholder}
        maxlength={MAX_LENGTH[name]} clearable={!name.toLowerCase().includes('password')}
        type={name.toLowerCase().includes('password') ? 'password' : name === 'phoneNumber' ? 'tel' : 'text'}
        autocomplete={name.toLowerCase().includes('password') ? 'new-password' : name === 'username' ? 'username' : name === 'email' ? 'email' : 'off'}
        onBlur={() => revalidate(name)}
        onChange={(next) => {
          onChange(name, next);
          setServerError('');
          if (submitted || touched.current.has(name)) window.requestAnimationFrame(() => revalidate(name));
          if (name === 'password' && (submitted || touched.current.has('confirmPassword'))) {
            window.requestAnimationFrame(() => revalidate('confirmPassword'));
          }
        }} />
    </Form.FormItem>
  );

  return (
    <Form ref={formRef} className="user-management-form" layout="vertical" labelAlign="top"
      disabled={disabled} initialData={value} scrollToFirstError="smooth"
      onSubmit={({ validateResult }) => {
        setSubmitted(true);
        const messages = getErrors(validateResult);
        setErrors(messages);
        if (validateResult === true) onSubmit(); else focusError(messages);
      }}>
      <section className="user-management-form__section">
        <div className="user-management-form__section-heading"><Typography.Title level="h3">{mode === 'create' ? '账号信息' : '基本信息'}</Typography.Title><span>* 为必填项</span></div>
        <div className="user-management-form__grid">
          {mode === 'create' && field('username', '用户名', '请输入用户名', '登录使用，最多 100 个字符')}
          {field('nickname', '昵称', '请输入昵称', '用于展示，最多 100 个字符')}
          {mode === 'edit' && field('userType', '用户类型', '如 员工、合作伙伴')}
          {mode === 'create' && field('password', '密码', '请输入密码', '最多 72 个 UTF-8 字节')}
          {mode === 'create' && field('confirmPassword', '确认密码', '请再次输入密码')}
        </div>
      </section>
      <section className="user-management-form__section">
        <div className="user-management-form__section-heading"><Typography.Title level="h3">联系方式</Typography.Title><span>选填 · 填写后校验格式</span></div>
        <div className="user-management-form__grid">
          {field('email', '邮箱', '如 user@example.com')}
          {field('phoneNumber', '手机号', '如 +86 138 0013 8000')}
        </div>
        <Alert className="user-management-form__hint" theme="info" message="邮箱和手机号需要保持唯一。手机号支持国家区号、空格和短横线。" />
      </section>
      <Collapse className="user-management-form__more" borderless expandIconPlacement="right" defaultValue={mode === 'edit' ? ['more'] : []}>
        <Collapse.Panel value="more" header={<span className="user-management-form__more-heading"><strong>更多资料</strong><small>姓名、职务、用户类型、语言、地区与时区</small></span>}>
          <div className="user-management-form__grid">
            {field('givenName', '名', '请输入名')}{field('familyName', '姓', '请输入姓')}
            {field('title', '职务', '请输入职务')}
            {mode === 'create' && field('userType', '用户类型', '如 员工、合作伙伴')}
            {field('preferredLanguage', '首选语言', '如 zh-CN')}
            {field('locale', '地区（Locale）', '如 zh-CN')}
            {field('timezone', '时区', '如 Asia/Shanghai')}
          </div>
        </Collapse.Panel>
      </Collapse>
      {mode === 'create' && <div className="user-management-form__status">
        <div><strong>启用账号</strong><p>启用后允许该用户登录</p></div>
        <Form.FormItem name="isActive"><Switch value={value.isActive ?? true} onChange={(next) => onChange('isActive', next)} /></Form.FormItem>
      </div>}
      {(errorCount > 0 || serverError) && <Alert className="user-management-form__errors" theme="error" aria-live="assertive" aria-atomic="true"
        title={serverError ? '暂时无法保存' : `请检查 ${errorCount} 项内容`}
        message={serverError || '请修正标记字段后重新提交。已填写内容会保留。'} />}
    </Form>
  );
});

UserForm.displayName = 'UserForm';
export default UserForm;
