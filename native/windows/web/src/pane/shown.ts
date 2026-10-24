import { createContext } from 'react';

/** Whether the pane around is the one its tab group shows (a pane behind another tab stays mounted, hidden). */
export const PaneShownContext = createContext(true);
