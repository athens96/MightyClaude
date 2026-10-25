// The picture viewer's rules (PictureViewer.tsx), as the WinUI viewer keeps them (MainWindow.TranscriptImages.cs
// OpenTranscriptImage: − and + by 1.25 between 25% and 400%, 맞춤 back to the fitted size). Pure, for the tests.

/**
 * How the host finds a picture again (MainWindow.WebShell.cs `imageAction`), the same way its thumbnail or tile was fetched:
 * a pane's log picture by its hash (`imageData`), a diagram step's by run, step and item id (`mightyImages`). Never a path.
 */
export type PictureSource = { paneId: string; hash: string } | { paneId: string; runId: string; step: string; id: string };

/** The picture the viewer shows: the data URL the page already holds, its name, and where the host finds it. */
export interface OpenPicture {
  url: string;
  alt: string;
  source: PictureSource;
}

/** The viewer's buttons the host answers (PaneView.TranscriptImageAction). */
export type PictureAction = 'copy' | 'save' | 'reveal' | 'openDefault';

export interface Size { width: number; height: number }
export interface Offset { x: number; y: number }

export const ZoomMin = 0.25;
export const ZoomMax = 4;
export const ZoomStep = 1.25;

/** A zoom kept between {@link ZoomMin} and {@link ZoomMax}. */
export function clampZoom(zoom: number): number {
  return Math.min(ZoomMax, Math.max(ZoomMin, zoom));
}

/** One − (direction −1) or + (1) press: 1.25 times smaller or larger, kept in range. */
export function stepZoom(zoom: number, direction: 1 | -1): number {
  return clampZoom(direction > 0 ? zoom * ZoomStep : zoom / ZoomStep);
}

/** The scale at which the whole picture fits the room, never larger than its own size (zoom 1 is this size). */
export function fitScale(picture: Size, room: Size): number {
  if (picture.width <= 0 || picture.height <= 0 || room.width <= 0 || room.height <= 0) return 1;
  return Math.min(1, room.width / picture.width, room.height / picture.height);
}

/** The picture's drawn size at `zoom` over its fitted size. */
export function drawnSize(picture: Size, room: Size, zoom: number): Size {
  const scale = fitScale(picture, room) * zoom;
  return { width: picture.width * scale, height: picture.height * scale };
}

/** A pan kept so the picture never leaves the room: none along a side where it fits, else at most its overhang either way. */
export function clampPan(offset: Offset, drawn: Size, room: Size): Offset {
  const limitX = Math.max(0, (drawn.width - room.width) / 2);
  const limitY = Math.max(0, (drawn.height - room.height) / 2);
  return { x: Math.min(limitX, Math.max(-limitX, offset.x)), y: Math.min(limitY, Math.max(-limitY, offset.y)) };
}

/** The zoom's words beside the buttons: a whole percent. */
export function zoomPercent(zoom: number): string {
  return Math.round(zoom * 100) + '%';
}
