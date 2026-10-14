import './style.less';
import { Image } from 'tdesign-react';

interface BrandComponentProps {
  className?: string;
  compact?: boolean;
}

const BrandComponent = ({ className, compact = false }: BrandComponentProps) => {
  const classes = ['brand', compact ? 'brand--compact' : '', className || '']
    .filter(Boolean)
    .join(' ');

  return (
    <div className={classes} aria-label="NexusAuth Workbench">
      <Image
        className="brand__mark"
        fit="contain"
        src="/brand/nexusauth-logo.svg"
        alt={compact ? 'NexusAuth' : ''}
      />
      {!compact && (
        <span className="brand__copy">
          <span className="brand__name">NexusAuth</span>
          <span className="brand__product">WORKBENCH</span>
        </span>
      )}
    </div>
  );
};

export default BrandComponent;
