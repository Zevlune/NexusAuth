import { useContext, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Space } from 'tdesign-react';
import { PageActionsContext } from './context';

/** Keep page-owned actions beside the shared content breadcrumb. */
export default function PageActions({ children }: { children: ReactNode }) {
  const target = useContext(PageActionsContext);
  return target ? createPortal(<Space size="small" align="center">{children}</Space>, target) : null;
}
