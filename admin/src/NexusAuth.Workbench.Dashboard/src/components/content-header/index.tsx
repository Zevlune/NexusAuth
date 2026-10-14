import { Breadcrumb } from 'tdesign-react';
import { useNavigate } from 'react-router-dom';
import type { ContentBreadcrumb } from './breadcrumbs';
import './style.less';

export default function ContentHeader({ items, actionsRef }: {
  items: ContentBreadcrumb[];
  actionsRef: (element: HTMLDivElement | null) => void;
}) {
  const navigate = useNavigate();

  return (
    <div className="content-header">
      <nav className="content-header__navigation" aria-label="面包屑导航">
        <Breadcrumb className="content-header__breadcrumb" maxItemWidth="160px">
          {items.map((item, index) => (
            <Breadcrumb.BreadcrumbItem key={`${index}-${item.label}`} href={item.path}
              aria-current={index === items.length - 1 ? 'page' : undefined}
              onClick={(event) => {
                if (item.path && event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) {
                  event.preventDefault();
                  navigate(item.path);
                }
              }}>
              {item.label}
            </Breadcrumb.BreadcrumbItem>
          ))}
        </Breadcrumb>
      </nav>
      <div className="content-header__actions" ref={actionsRef} />
    </div>
  );
}
