# Dashboard 组件检查

检查范围：`src/pages`、共享内容区与导航、登录、错误/加载状态。业务行为及现有 API 调用保持不变。

## 面包屑

参考 `NexusAuthorization/dashboard/src/pages/policy-workbench/shared.tsx` 的原生 `Breadcrumb` 与顶部按钮排列：只显示实际菜单祖先和当前菜单，使用默认分隔符，顶部操作与面包屑共用一行，下方间距为 16px。保留可访问的 `nav` 语义和 React Router 导航。登录和仪表盘不显示面包屑。

## 页面组件

| 页面 / 区域 | 主要 TDesign 组件 |
| --- | --- |
| 共享布局与导航 | Layout、Menu、Tabs、Breadcrumb、Button、Dropdown、Avatar、Input、Tooltip、Space |
| 用户管理 | Card、Statistic、Table、Pagination、Tag、Avatar、Drawer、Dialog、Dropdown、Form、Input、Switch、Collapse、Alert、Descriptions、Empty、Loading |
| 仪表盘 | Card、List、Tag、Typography、Alert、Empty、Button |
| 应用管理 | Card、Descriptions、Avatar、Tag、Form、Input、Select、Loading、Empty、Pagination、Drawer、Popconfirm |
| 新增 / 编辑应用 | Card、Descriptions、Form、Input、Select、Textarea、Switch、Alert、Loading、Tooltip、Popconfirm |
| 服务资源 | Card、Descriptions、Avatar、Form、Input、Select、Switch、Drawer、Dialog、Loading、Empty、Pagination |
| SCIM / 开放 API 凭据 | Card、Table、Tag、Form、Drawer、Dialog、Alert、Space、Textarea、Button；其他表单控件保持 TDesign |
| 登录记录 | Card、Table、Tag、Form、Input、Select、Pagination、Tooltip |
| 字典 / 字段取值 / 产品 / saleRefId 配置 | Card、Table、Tag、Form、Input、Select、Pagination、Space；产品选择使用 Checkbox |
| 智 V V | Card、Menu、Typography、Empty、Button，以及官方 `@tdesign-react/chat` 的 ChatBot |
| 登录 | Card、Typography、Button、Tag、Tooltip |
| 404 / 全局加载 | Empty、Button、Loading |
| 品牌图片 | Image |

## 保留的实现

- `div`、`section`、`nav` 等仅用于布局、语义区域或组件内容组合，不用来重写按钮、输入、表格、提示、折叠等控件。
- Logo、品牌字样、响应式栅格及图表中心文字为项目自身内容与布局。
- 图表继续使用已有 `@ant-design/charts`；当前 TDesign React 包没有对应图表组件。
- “公司主体映射配置”菜单指向的页面源文件在本次检查前就不存在；本次不新增该业务页面。
