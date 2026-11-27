// Browser preview only: simple stand-ins for the game's "cs2/ui" components.
import { ReactNode, useState } from "react";

export const Panel = ({ header, className, onClose, children }: { header: ReactNode; className?: string; onClose?: () => void; children?: ReactNode }) => (
    <div className={`preview-panel ${className ?? ""}`}>
        <div className="preview-panel-header">
            <div>{header}</div>
            <div className="preview-close" onClick={onClose}>{"x"}</div>
        </div>
        <div className="preview-panel-content">{children}</div>
    </div>
);

export const FloatingButton = ({ selected, onSelect, tooltipLabel }: { src?: string; selected?: boolean; onSelect?: () => void; tooltipLabel?: string }) => (
    <div className={`preview-button ${selected ? "on" : ""}`} title={tooltipLabel} onClick={onSelect}>{"LT"}</div>
);

export const Tooltip = ({ tooltip, children }: { tooltip: ReactNode; children: ReactNode }) => {
    const [show, setShow] = useState(false);
    return (
        <div className="preview-tip-host" onMouseEnter={() => setShow(true)} onMouseLeave={() => setShow(false)}>
            {children}
            {show && <div className="preview-tip">{tooltip}</div>}
        </div>
    );
};
