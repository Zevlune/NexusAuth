import { DashboardIcon, LockOnIcon, UserIcon } from 'tdesign-icons-react';

interface IProp {
    iconName: string;
}

const iconMap: Record<string, React.ReactNode> = {
    "dashboard": <DashboardIcon />,
    "user": <UserIcon />,
    "lock": <LockOnIcon />,
    "key": <LockOnIcon />
};

const IconRenderer = (iconName: string) => {
    const icon = iconMap[iconName];
    return icon || null;
};

const IconComponent = (props: IProp) => {
    return (IconRenderer(props.iconName))

}
export default IconComponent
