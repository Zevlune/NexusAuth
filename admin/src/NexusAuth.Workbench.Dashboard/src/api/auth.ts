import {
    DEFAULT_AUTHENTICATED_ROUTE,
    DEFAULT_AUTHENTICATED_ROUTE_LABEL,
} from '../router/defaults';

export interface MenuDefinition {
    id: string;
    route: string;
    componentPath: string | null;
    name: string;
    show: boolean;
    iconName: string | null;
    icon?: string | null;
    parentId: string | null;
    parentPaths: string[];
    children: MenuDefinition[];
}

export const getMenuList = () => {
    return Promise.resolve<MenuDefinition[]>([
        {
            id: 'dashboard',
            route: DEFAULT_AUTHENTICATED_ROUTE,
            componentPath: '/dashboard/index.tsx',
            name: DEFAULT_AUTHENTICATED_ROUTE_LABEL,
            show: true,
            iconName: "dashboard",
            parentId: null,
            parentPaths: [],
            children: [],
        },
        {
            id: 'oauth-user',
            route: '/user-management',
            componentPath: '/user-management/index.tsx',
            name: '用户管理',
            show: true,
            iconName: "user",
            parentId: null,
            parentPaths: [],
            children: [],
        },
        {
            id: 'oauth',
            route: '/oauth',
            componentPath: null,
            name: '应用中心',
            show: true,
            iconName: "key",
            parentId: null,
            parentPaths: [],
            children: []
        },
        {
            id: 'oauth-client',
            route: '/oauth/client-management',
            componentPath: '/client-management/index.tsx',
            name: '应用管理',
            show: true,
            iconName: null,
            parentId: 'oauth',
            parentPaths: [],
            children: [],
        },
        {
            id: 'oauth-client-create',
            route: '/oauth/client-management/create',
            componentPath: '/client-management/create/index.tsx',
            name: '新增应用',
            show: false,
            iconName: null,
            parentId: 'oauth-client',
            parentPaths: [],
            children: [],
        },
        {
            id: 'oauth-client-edit',
            route: '/oauth/client-management/edit/:id',
            componentPath: '/client-management/edit/index.tsx',
            name: '编辑应用',
            show: false,
            iconName: null,
            parentId: 'oauth-client',
            parentPaths: [],
            children: [],
        },
        {
            id: 'oauth-scim-credential',
            route: '/oauth/scim-credential-management',
            componentPath: '/scim-credential-management/index.tsx',
            name: 'SCIM 凭证',
            show: true,
            iconName: null,
            parentId: 'oauth',
            parentPaths: [],
            children: [],
        },
        {
            id: 'oauth-open-api-credential',
            route: '/oauth/open-api-credential-management',
            componentPath: '/open-api-credential-management/index.tsx',
            name: '开放 API 凭据',
            show: true,
            iconName: null,
            parentId: 'oauth',
            parentPaths: [],
            children: [],
        },
        {
            id: 'oauth-user-compat',
            route: '/oauth/user-management',
            componentPath: '/user-management/index.tsx',
            name: '用户管理',
            show: false,
            iconName: null,
            parentId: 'oauth',
            parentPaths: [],
            children: [],
        },
        {
            id: 'oauth-login-audit',
            route: '/oauth/login-audit-management',
            componentPath: '/login-audit-management/index.tsx',
            name: '登录记录',
            show: true,
            iconName: null,
            parentId: 'oauth',
            parentPaths: [],
            children: [],
        },
        {
            id: 'oauth-api-resource',
            route: '/oauth/api-resource-management',
            componentPath: '/api-resource-management/index.tsx',
            name: '服务资源',
            show: true,
            iconName: null,
            parentId: 'oauth',
            parentPaths: [],
            children: [],
        },
    ]);
};

export const getVisibleMenuList = async () => {
    const menus = await getMenuList();
    return menus.filter(menu => menu.show === true);
};
