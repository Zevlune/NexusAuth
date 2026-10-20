# NexusAuth user guides

The repository contains two applications with different responsibilities. Their operating instructions are maintained separately.

| Application | Components | Purpose | Manual |
| --- | --- | --- | --- |
| NexusAuth.Host | `src/NexusAuth.Host` | Standalone OAuth/OIDC identity provider, login, account security and identity endpoints | [Host manual (Chinese)](../../document/NexusAuth.Host使用手册.md) |
| Workbench | `admin/src/NexusAuth.Workbench.Api` and `admin/src/NexusAuth.Workbench.Dashboard` | One management application: API and BFF authentication on the server, management interface in Dashboard | [Workbench manual (Chinese)](../../document/Workbench使用手册.md) |

Host can run independently. Workbench requires an available identity provider and currently shares the NexusAuth database with Host. Dashboard calls Workbench API; it does not issue identity tokens.

Use the Host manual to deploy an identity provider or integrate a business application. Use the Workbench manual to deploy and operate the management interface. Each manual documents its own configuration, startup commands, workflows and troubleshooting.

The current defaults require configuration before a local run: Host enables SMTP without supplying a password, Workbench's Authority and Dashboard's development proxy point to LAN addresses, and the Compose `admin-api` service has no published host port. The manuals explain the necessary local and container settings.

Additional references:

- [Documentation index](../../document/README.md)
- [OAuth/OIDC protocol design](../../document/11-OAuth-OIDC协议设计.md)
- [SMTP configuration and verification](../../document/SMTP发信试验.md)
- [Workbench API login integration](../../document/Workbench-API统一登录接入模板.md)
- [Dashboard login integration](../../document/Workbench-Dashboard统一登录接入模板.md)
- [APISIX integration](../../document/13-APISIX网关对接手册.md)
- [Repository overview](../../README.md)

The linked manuals are the current operating references. This English page is an orientation to those manuals.
