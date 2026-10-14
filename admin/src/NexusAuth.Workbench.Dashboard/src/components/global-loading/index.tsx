import { Loading } from 'tdesign-react';

interface GlobalLoadingProps {
  height?: string | number;
}

export default function GlobalLoading({ height = '100vh' }: GlobalLoadingProps) {
  return <Loading loading fullscreen size="medium" text="页面加载中..." zIndex={999}
    style={{ minHeight: height }} />;
}
