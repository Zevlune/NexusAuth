import assert from 'node:assert/strict';
import test from 'node:test';
import { getContentBreadcrumbs, type ContentBreadcrumb, type ContentMenu } from './breadcrumbs.ts';

const crumb = (label: string, path?: string): ContentBreadcrumb => ({ label, path });

const menus: ContentMenu[] = [
  { id: 'users', name: '用户管理', route: '/user-management', parentId: null, componentPath: '/user-management/index.tsx' },
  { id: 'oauth', name: '应用中心', route: '/oauth', parentId: null, componentPath: null },
  { id: 'client', name: '应用管理', route: '/oauth/client-management', parentId: 'oauth', componentPath: '/client-management/index.tsx' },
  { id: 'client-create', name: '新增应用', route: '/oauth/client-management/create', parentId: 'client', componentPath: '/client-management/create/index.tsx' },
  { id: 'client-edit', name: '编辑应用', route: '/oauth/client-management/edit/:id', parentId: 'client', componentPath: '/client-management/edit/index.tsx' },
];

test('root menu renders only the current menu without a link', () => {
  assert.deepEqual(getContentBreadcrumbs(menus, '/user-management', 'fallback'), [
    crumb('用户管理'),
  ]);
});

test('应用中心 -> 应用管理 builds a linked ancestor chain', () => {
  assert.deepEqual(getContentBreadcrumbs(menus, '/oauth/client-management', 'fallback'), [
    crumb('应用中心'),
    crumb('应用管理'),
  ]);
});

test('hidden create route includes 新增应用 as the current crumb without a link', () => {
  const breadcrumbs = getContentBreadcrumbs(menus, '/oauth/client-management/create', 'fallback');

  assert.deepEqual(breadcrumbs, [
    crumb('应用中心'),
    crumb('应用管理', '/oauth/client-management'),
    crumb('新增应用'),
  ]);
  assert.equal(breadcrumbs.some((crumb) => crumb.path === '/oauth/client-management/create'), false);
});

test('parameterized edit route matches the id route and links the parent chain', () => {
  assert.deepEqual(getContentBreadcrumbs(menus, '/oauth/client-management/edit/42', 'fallback'), [
    crumb('应用中心'),
    crumb('应用管理', '/oauth/client-management'),
    crumb('编辑应用'),
  ]);
});

test('relative routes concatenate with their parent path', () => {
  const relativeMenus: ContentMenu[] = [
    { id: 'app', name: '应用中心', route: '/app', parentId: null, componentPath: null },
    { id: 'child', name: '子页面', route: 'child', parentId: 'app', componentPath: '/child/index.tsx' },
    { id: 'grand', name: '孙页面', route: 'grand', parentId: 'child', componentPath: '/grand/index.tsx' },
  ];

  assert.deepEqual(getContentBreadcrumbs(relativeMenus, '/app/child/grand', 'fallback'), [
    crumb('应用中心'),
    crumb('子页面', '/app/child'),
    crumb('孙页面'),
  ]);
});

test('a parent menu without componentPath stays unclickable while its linked grandparent is clickable', () => {
  const nestedMenus: ContentMenu[] = [
    { id: 'app', name: '应用中心', route: '/app', parentId: null, componentPath: '/app/index.tsx' },
    { id: 'child', name: '子页面', route: 'child', parentId: 'app', componentPath: null },
    { id: 'grand', name: '孙页面', route: 'grand', parentId: 'child', componentPath: '/grand/index.tsx' },
  ];

  assert.deepEqual(getContentBreadcrumbs(nestedMenus, '/app/child/grand', 'fallback'), [
    crumb('应用中心', '/app'),
    crumb('子页面'),
    crumb('孙页面'),
  ]);
});

test('unknown pathname falls back to the provided label', () => {
  assert.deepEqual(getContentBreadcrumbs(menus, '/not-in-menu', '未知页面'), [
    { label: '未知页面' },
  ]);
  assert.deepEqual(getContentBreadcrumbs([], '/anything', '空菜单'), [
    { label: '空菜单' },
  ]);
});

test('circular parentId does not hang and falls back', async () => {
  const circularMenus: ContentMenu[] = [
    { id: 'a', name: 'A', route: 'a', parentId: 'b', componentPath: null },
    { id: 'b', name: 'B', route: 'b', parentId: 'a', componentPath: '/b/index.tsx' },
  ];

  const result = await Promise.race([
    Promise.resolve().then(() => getContentBreadcrumbs(circularMenus, '/b', '循环菜单')),
    new Promise((_, reject) => {
      setTimeout(() => reject(new Error('getContentBreadcrumbs did not terminate for a circular parentId')), 2000);
    }),
  ]);

  assert.deepEqual(result, [{ label: '循环菜单' }]);
});
