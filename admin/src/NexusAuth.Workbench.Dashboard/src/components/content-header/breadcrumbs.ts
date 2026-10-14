import { matchPath } from 'react-router-dom';

export type ContentMenu = {
  id: string;
  name: string;
  route: string;
  parentId: string | null;
  componentPath?: string | null;
};

export type ContentBreadcrumb = { label: string; path?: string };

export function getContentBreadcrumbs(menus: ContentMenu[], pathname: string, fallback: string): ContentBreadcrumb[] {
  const byId = new Map(menus.map((menu) => [menu.id, menu]));
  const paths = new Map<string, string>();
  const getPath = (menu: ContentMenu, seen = new Set<string>()): string => {
    if (paths.has(menu.id)) return paths.get(menu.id)!;
    if (seen.has(menu.id)) return menu.route;
    seen.add(menu.id);
    const parent = menu.parentId ? byId.get(menu.parentId) : undefined;
    const path = !parent || menu.route.startsWith('/')
      ? menu.route
      : `${getPath(parent, seen).replace(/\/$/, '')}/${menu.route.replace(/^\//, '')}`;
    paths.set(menu.id, path);
    return path;
  };

  const current = menus.find((menu) => matchPath({ path: getPath(menu), end: true }, pathname));
  const chain: ContentMenu[] = [];
  const seen = new Set<string>();
  let menu = current;
  while (menu && !seen.has(menu.id)) {
    seen.add(menu.id);
    chain.unshift(menu);
    menu = menu.parentId ? byId.get(menu.parentId) : undefined;
  }

  return chain.length ? chain.map((item, index) => ({
    label: item.name,
    path: index < chain.length - 1 && item.componentPath ? getPath(item) : undefined,
  })) : [{ label: fallback }];
}
