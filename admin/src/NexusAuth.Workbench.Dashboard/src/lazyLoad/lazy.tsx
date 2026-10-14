import type { ComponentType } from 'react';

type LazyComponentModule = {
    default: ComponentType;
};

export const modules = import.meta.glob<LazyComponentModule>('../pages/**/index.tsx');
export const componentPaths = Object.keys(modules).map((path: string) => path.replace('../pages', ''));

export const lazyLoad = Object.keys(modules).reduce<Record<string, () => Promise<LazyComponentModule>>>((prev, path: string) => {
    const formatPath = path.replace('../pages', '');
    prev[formatPath] = async () => {
        // 这里其实就是动态加载js，如果报错了说明js资源不存在
        return await modules[path]();
    }
    return prev;
}, {});

