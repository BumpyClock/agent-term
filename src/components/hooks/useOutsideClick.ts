import { useEffect, type RefObject } from 'react';

type OutsideClickHandler = (event: MouseEvent) => void;

export function useOutsideClick<T extends HTMLElement>(
  ref: RefObject<T | null>,
  handler: OutsideClickHandler,
  enabled = true
) {
  useEffect(() => {
    if (!enabled) return;

    const handleMouseDown = (event: MouseEvent) => {
      if (!ref.current || ref.current.contains(event.target as Node)) return;
      handler(event);
    };

    window.addEventListener('mousedown', handleMouseDown);
    return () => {
      window.removeEventListener('mousedown', handleMouseDown);
    };
  }, [enabled, handler, ref]);
}
