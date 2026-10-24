// The files pane's model as the host builds it (MainWindow.WebShell.Panes.cs) from Core's FilePaneTree, WorkspaceFiles and
// FilePreviewLoader: the workspace's rules (noise folders, links, caps, filter results) and every note are the host's words.

export interface FileRow {
  path: string;
  name: string;
  depth: number;
  dir: boolean;
  expanded: boolean;
  noise: boolean;
  symlink: boolean;
  /** source, markdown, image or unsupported: the row's symbol. */
  symbol?: string;
  /** An opened folder's caption under it; `reports` when it says the folder could not be read or was cut. */
  caption?: string;
  reports?: boolean;
}

export interface FilesModel {
  available: boolean;
  filter: string;
  filtering: boolean;
  rows: FileRow[];
  /** The notes after the rows (empty, cut, no matches, more results, the root's error). */
  notes: string[];
  selected?: string;
  /** The file the pane last previewed, shown again when it opens. */
  preview?: string;
}

export interface FileImage {
  url: string;
  /** Its size at 1:1: a bitmap's pixels (drawn one to one on the screen, so divided by devicePixelRatio), a vector's points. */
  width: number;
  height: number;
  bitmap: boolean;
  label: string;
  zoomable: boolean;
}

export interface FilePreview {
  path: string;
  name: string;
  /** The file could not be read at all: the reason alone, no head. */
  failure?: string;
  kind: 'source' | 'markdown' | 'image' | 'unsupported';
  encoding?: string;
  text?: string;
  /** Coloured spans as [kind, start, length] triples: 0 keyword, 1 string, 2 comment, 3 number. */
  tokens?: number[];
  lines?: number;
  wraps?: boolean;
  /** The notes over the source (cut, highlight cap, wrapped), and over the rendered Markdown. */
  banners: string[];
  renderedBanners?: string[];
  markdown?: boolean;
  image?: FileImage;
  size?: string;
  modified?: string;
  reason?: string;
}
