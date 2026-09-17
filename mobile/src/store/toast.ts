import { create } from 'zustand';

export type ToastTone = 'info' | 'success' | 'error';

export interface Toast {
  id: number;
  message: string;
  tone: ToastTone;
}

interface ToastStore {
  toasts: Toast[];
  show: (message: string, tone?: ToastTone) => void;
  dismiss: (id: number) => void;
}

let nextId = 1;

export const useToastStore = create<ToastStore>((set) => ({
  toasts: [],
  show: (message, tone = 'info') => {
    const id = nextId++;
    set((prev) => ({ toasts: [...prev.toasts, { id, message, tone }] }));
    setTimeout(() => {
      set((prev) => ({ toasts: prev.toasts.filter((toast) => toast.id !== id) }));
    }, 2600);
  },
  dismiss: (id) => set((prev) => ({ toasts: prev.toasts.filter((toast) => toast.id !== id) })),
}));

export function showToast(message: string, tone: ToastTone = 'info'): void {
  useToastStore.getState().show(message, tone);
}
