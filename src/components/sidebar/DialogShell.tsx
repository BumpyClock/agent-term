import type { ReactNode } from 'react';

interface DialogShellProps {
  title: string;
  onClose: () => void;
  children: ReactNode;
  actions: ReactNode;
}

export function DialogShell({ title, onClose, children, actions }: DialogShellProps) {
  return (
    <div className="dialog-overlay" onClick={onClose}>
      <div className="dialog" onClick={(event) => event.stopPropagation()}>
        <div className="dialog-title">{title}</div>
        {children}
        <div className="dialog-actions">{actions}</div>
      </div>
    </div>
  );
}
