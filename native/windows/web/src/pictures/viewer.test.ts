import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import { Viewer } from './PictureViewer';
import { clampPan, clampZoom, drawnSize, fitScale, stepZoom, zoomPercent, ZoomMax, ZoomMin } from './viewer';

describe('picture viewer zoom', () => {
  it('steps by 1.25 and stays between 25% and 400%, as the WinUI viewer', () => {
    expect(stepZoom(1, 1)).toBe(1.25);
    expect(stepZoom(1, -1)).toBe(0.8);
    expect(stepZoom(3.5, 1)).toBe(ZoomMax);
    expect(stepZoom(0.3, -1)).toBe(ZoomMin);
    expect(clampZoom(10)).toBe(4);
    expect(clampZoom(0)).toBe(0.25);
    let zoom = 1;
    for (let i = 0; i < 20; i++) zoom = stepZoom(zoom, 1);
    expect(zoom).toBe(4);
  });

  it('words the zoom as a whole percent', () => {
    expect(zoomPercent(1)).toBe('100%');
    expect(zoomPercent(1.25 * 1.25)).toBe('156%');
    expect(zoomPercent(0.25)).toBe('25%');
  });
});

describe('picture viewer fit', () => {
  it('fits a large picture to the room on its tighter side and never enlarges a small one', () => {
    expect(fitScale({ width: 2000, height: 1000 }, { width: 1000, height: 800 })).toBe(0.5);
    expect(fitScale({ width: 1000, height: 2000 }, { width: 1000, height: 800 })).toBe(0.4);
    expect(fitScale({ width: 100, height: 50 }, { width: 1000, height: 800 })).toBe(1);
    expect(fitScale({ width: 0, height: 0 }, { width: 1000, height: 800 })).toBe(1);
    expect(fitScale({ width: 100, height: 50 }, { width: 0, height: 0 })).toBe(1);
  });

  it('draws the zoom over the fitted size', () => {
    expect(drawnSize({ width: 2000, height: 1000 }, { width: 1000, height: 800 }, 1)).toEqual({ width: 1000, height: 500 });
    expect(drawnSize({ width: 2000, height: 1000 }, { width: 1000, height: 800 }, 2)).toEqual({ width: 2000, height: 1000 });
  });

  it('keeps a pan inside the overhang and none where the picture fits', () => {
    const room = { width: 1000, height: 800 };
    expect(clampPan({ x: 900, y: 50 }, { width: 2000, height: 600 }, room)).toEqual({ x: 500, y: 0 });
    expect(clampPan({ x: -900, y: -500 }, { width: 2000, height: 1000 }, room)).toEqual({ x: -500, y: -100 });
    expect(clampPan({ x: 20, y: 20 }, { width: 500, height: 400 }, room)).toEqual({ x: 0, y: 0 });
  });
});

describe('PictureViewer', () => {
  const services = {
    bridge: { call: () => Promise.resolve(null) },
    store: { dispatch: () => {} },
    t: translator([{ 'images.open': 'View image', 'images.copy': 'Copy image', 'images.save': 'Save image', 'menu.showInExplorer': 'Show in Explorer', 'images.openDefault': 'Open in default app', 'files.image.fit': 'Fit', 'files.image.zoomIn': 'Zoom in', 'files.image.zoomOut': 'Zoom out', 'settings.closeButton': 'Close' }]),
  } as unknown as AppServices;
  const render = () => renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services },
    createElement(Viewer, { picture: { url: 'data:image/png;base64,AA==', alt: 'shot.png', source: { paneId: 'p', hash: 'h' } } })));

  it('draws a modal dialog over the window with the picture, the zoom and the host actions', () => {
    const html = render();
    expect(html).toContain('role="dialog"');
    expect(html).toContain('aria-modal="true"');
    expect(html).toContain('aria-label="View image"');
    expect(html).toContain('data-page-overlay=""');
    expect(html).toContain('src="data:image/png;base64,AA=="');
    expect(html).toContain('alt="shot.png"');
    for (const word of ['Fit', 'Copy image', 'Save image', 'Show in Explorer', 'Open in default app']) expect(html).toContain('>' + word + '<');
    for (const label of ['Zoom in', 'Zoom out', 'Close']) expect(html).toContain('aria-label="' + label + '"');
    expect(html).toContain('>100%<');
  });
});
