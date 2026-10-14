import { Outlet, useLocation } from 'react-router-dom';
import { Suspense, useCallback, useEffect, useRef, useState } from 'react';

import GlobalLoading from '../../../components/global-loading';
import { setPageLoading } from '../../../page-loading';
import ContentHeader from '../../../components/content-header';
import type { ContentBreadcrumb } from '../../../components/content-header/breadcrumbs';
import { PageActionsContext } from '../../../components/page-actions/context';

const PublicContent = ({ breadcrumbs }: { breadcrumbs: ContentBreadcrumb[] }) => {
    const { pathname } = useLocation();
    const [actionsTarget, setActionsTarget] = useState<HTMLDivElement | null>(null);
    const showContentHeader = pathname !== '/dashboard';
    const contentInnerRef = useRef<HTMLDivElement | null>(null);
    const contentRef = useRef<HTMLDivElement | null>(null);
    const [contentMaxHeight, setContentMaxHeight] = useState<number>();

    useEffect(() => {
        setPageLoading(false);
    }, [pathname]);

    const updateContentMaxHeight = useCallback(() => {
        if (!contentRef.current) {
            return;
        }
        const { top } = contentRef.current.getBoundingClientRect();
        const nextHeight = Math.max(Math.floor(window.innerHeight - top), 0);
        setContentMaxHeight((prev) => (prev === nextHeight ? prev : nextHeight));
    }, []);

    useEffect(() => {
        updateContentMaxHeight();

        const frame = window.requestAnimationFrame(() => {
            updateContentMaxHeight();
        });

        return () => {
            window.cancelAnimationFrame(frame);
        };
    }, [pathname, updateContentMaxHeight]);

    useEffect(() => {
        updateContentMaxHeight();

        const resizeObserver = new ResizeObserver(() => {
            updateContentMaxHeight();
        });

        if (contentInnerRef.current) {
            resizeObserver.observe(contentInnerRef.current);
        }

        window.addEventListener('resize', updateContentMaxHeight);

        return () => {
            resizeObserver.disconnect();
            window.removeEventListener('resize', updateContentMaxHeight);
        };
    }, [updateContentMaxHeight]);

    return (

        <PageActionsContext.Provider key={pathname} value={actionsTarget}>
          <div className="layout-content-inner" ref={contentInnerRef}>
            <div className="layout-main-content" ref={contentRef} style={{ maxHeight: contentMaxHeight }}>
                {showContentHeader && <ContentHeader items={breadcrumbs} actionsRef={setActionsTarget} />}
                <Suspense fallback={<GlobalLoading height={contentMaxHeight || 320} />}>
                    <div className="layout-page-body" key={pathname}><Outlet /></div>
                </Suspense>
            </div>
          </div>
        </PageActionsContext.Provider>
    )
}
export default PublicContent
