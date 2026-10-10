-- Local preview only. Existing users and their passwords are preserved.
\connect nexusauth
SET search_path TO nexusauth, public;

INSERT INTO nexusauth.users
    (id, username, password_hash, email, nickname, given_name, family_name, title, user_type,
     preferred_language, locale, timezone, gender, is_active, is_system_account, created_at, updated_at)
VALUES
    ('6c6ab9ae-9650-4b11-8c68-046cfd28d901', 'demo.alice', crypt('DemoPass123!', gen_salt('bf', 12)),
     'alice@example.test', 'Alice 演示账号', 'Alice', 'Demo', '产品经理', 'employee', 'zh-CN', 'zh-CN', 'Asia/Shanghai', 0, true, false, NOW() - INTERVAL '7 days', NOW()),
    ('6c6ab9ae-9650-4b11-8c68-046cfd28d902', 'demo.bob', crypt('DemoPass123!', gen_salt('bf', 12)),
     'bob@example.test', 'Bob 演示账号', 'Bob', 'Demo', '研发工程师', 'employee', 'zh-CN', 'zh-CN', 'Asia/Shanghai', 0, true, false, NOW() - INTERVAL '3 days', NOW()),
    ('6c6ab9ae-9650-4b11-8c68-046cfd28d903', 'demo.disabled', crypt('DemoPass123!', gen_salt('bf', 12)),
     'disabled@example.test', '已停用演示账号', 'Disabled', 'Demo', '停用账号示例', 'employee', 'zh-CN', 'zh-CN', 'Asia/Shanghai', 0, false, false, NOW() - INTERVAL '14 days', NOW())
ON CONFLICT DO NOTHING;

-- These are explicitly marked seed events, not actual device logins.
INSERT INTO nexusauth.login_audit_logs
    (id, user_id, username, client_id, is_successful, failure_reason, ip_address, user_agent, occurred_at)
SELECT 'aa71e1b0-93a5-48c6-a921-6231bd711001', id, username, 'local-demo', true, NULL,
    '127.0.0.1', 'Local preview seed (synthetic)', NOW() - INTERVAL '1 day'
FROM nexusauth.users WHERE username = 'demo.alice'
ON CONFLICT DO NOTHING;

-- Passkeys and TOTP are enrolled by the user, never fabricated in seed data.
