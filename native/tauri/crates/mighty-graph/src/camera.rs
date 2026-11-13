//! Where the Mighty diagram's camera stands (C# `MightyGraphCamera`, MightyCode.Core/MightyGraphCamera.cs, a port of macOS
//! MightyGraphCamera): the canvas's centreline and origin, the offsets that put a block in view, the anchors a trim, a re-aim or a resize
//! asks for, the stranded-card rule and the result reveal. Frames are in node coordinates (screen y = node y × zoom + camera y).

use std::collections::{HashMap, HashSet};

use crate::layout;

/// C# `GraphRect(X, Y, W, H)` (MightyGraphCamera.cs:286).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct GraphRect {
    pub x: f64,
    pub y: f64,
    pub w: f64,
    pub h: f64,
}

impl GraphRect {
    pub const fn new(x: f64, y: f64, w: f64, h: f64) -> Self {
        GraphRect { x, y, w, h }
    }
    pub fn max_x(&self) -> f64 {
        self.x + self.w
    }
    pub fn max_y(&self) -> f64 {
        self.y + self.h
    }
    pub fn mid_x(&self) -> f64 {
        self.x + self.w / 2.0
    }
    pub fn mid_y(&self) -> f64 {
        self.y + self.h / 2.0
    }
    /// `OffsetBy`.
    pub fn offset_by(&self, dx: f64, dy: f64) -> Self {
        GraphRect { x: self.x + dx, y: self.y + dy, ..*self }
    }
}

/// A width and height (C#'s `(double W, double H)` tuples).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Size {
    pub w: f64,
    pub h: f64,
}

impl Size {
    pub const fn new(w: f64, h: f64) -> Self {
        Size { w, h }
    }
}

/// A camera or a point (C#'s `(double X, double Y)` tuples).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Point {
    pub x: f64,
    pub y: f64,
}

impl Point {
    pub const fn new(x: f64, y: f64) -> Self {
        Point { x, y }
    }
}

/// `RequestWidth`, `Margin`, `CentreX` (MightyGraphCamera.cs:8-10).
pub const REQUEST_WIDTH: f64 = 500.0;
pub const MARGIN: f64 = 24.0;
pub const CENTRE_X: f64 = MARGIN + REQUEST_WIDTH / 2.0;

/// `X(treeWidth)`: where a tree of that width starts so it stands on the centreline.
pub fn x(tree_width: f64) -> f64 {
    CENTRE_X - tree_width / 2.0
}

/// `OriginX`.
pub fn origin_x(leading_min_x: f64) -> f64 {
    0f64.min(leading_min_x - MARGIN)
}

/// `CanvasWidth`.
pub fn canvas_width(leading: f64, trailing: f64) -> f64 {
    (trailing + MARGIN).max(REQUEST_WIDTH + MARGIN * 2.0) - origin_x(leading)
}

/// `PendingNodeID`: the next-request draft's block.
pub const PENDING_NODE_ID: &str = mighty_core::graph::PENDING_NODE_ID;

/// `IsAuxiliary(nodeID)`: the draft, the history block and the attachments beside the flow (MightyGraphCamera.cs:18).
pub fn is_auxiliary(node_id: &str) -> bool {
    node_id == PENDING_NODE_ID
        || node_id == layout::HISTORY_NODE_ID
        || node_id.ends_with(":result-files")
        || node_id.contains(&format!(":{}", layout::EXECUTION_SUFFIX))
        || node_id.contains(&format!(":{}", layout::IMAGES_SUFFIX))
        || node_id.contains(&format!(":{}", layout::PLAN_RECORD_SUFFIX))
}

/// `ShowsTop`: the camera shows the top of the diagram, so scrolling further up asks for older requests (MightyGraphCamera.cs:29).
pub fn shows_top(camera_y: f64, zoom: f64, top: f64, slack: f64) -> bool {
    zoom.is_finite() && zoom > 0.0 && camera_y.is_finite() && top.is_finite() && -camera_y / zoom <= top + slack
}

/// `CameraOffset(frame, viewport, zoom, alignTop)`.
pub fn camera_offset(frame: GraphRect, viewport: Size, zoom: f64, align_top: bool) -> Point {
    Point::new(
        (viewport.w - frame.w * zoom) / 2.0 - frame.x * zoom,
        (if align_top { 16.0 } else { 16f64.max((viewport.h - frame.h * zoom) / 2.0) }) - frame.y * zoom,
    )
}

/// `RevealOffset`: a newly finished result card's bottom 16pt above the viewport's bottom, or — taller than the viewport — its top where an
/// align-top re-aim puts it.
pub fn reveal_offset(frame: GraphRect, viewport: Size, zoom: f64) -> Point {
    let top = camera_offset(frame, viewport, zoom, true);
    Point::new(top.x, top.y.max(viewport.h - 16.0 - frame.max_y() * zoom))
}

/// `CameraOffset(frame, viewport, zoom, alignTop, alignBottom)`: `align_bottom` (a revealed result) wins over `align_top`.
pub fn camera_offset_aligned(frame: GraphRect, viewport: Size, zoom: f64, align_top: bool, align_bottom: bool) -> Point {
    if align_bottom { reveal_offset(frame, viewport, zoom) } else { camera_offset(frame, viewport, zoom, align_top) }
}

/// `ResultReveal.RunProgress(Id, Finished)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RunProgress {
    pub id: String,
    pub finished: bool,
}

impl RunProgress {
    pub fn new(id: &str, finished: bool) -> Self {
        RunProgress { id: id.into(), finished }
    }
}

/// `ResultReveal` (MightyGraphCamera.cs:68): when the camera puts a newly finished request's result card right above the composer. Once,
/// for a request of the pane's own that finishes while the diagram watches; a card not measured yet is held until its height arrives.
#[derive(Debug, Clone, Default)]
pub struct ResultReveal {
    holding_id: Option<String>,
    awaiting_measure: bool,
    request_in_flight: bool,
}

impl ResultReveal {
    pub fn new() -> Self {
        Self::default()
    }
    /// `HoldingID`: the card the camera is holding above the composer.
    pub fn holding_id(&self) -> Option<&str> {
        self.holding_id.as_deref()
    }
    /// `AwaitingMeasure`.
    pub fn awaiting_measure(&self) -> bool {
        self.awaiting_measure
    }
    /// `RequestInFlight`.
    pub fn request_in_flight(&self) -> bool {
        self.request_in_flight
    }

    /// `FinishedRunID`: the newest finished run, which the previous observation saw unfinished or — with a request in flight — did not have
    /// yet and which is the newest run.
    pub fn finished_run_id(previous: &[RunProgress], current: &[RunProgress], request_in_flight: bool) -> Option<String> {
        let latest = current.iter().rev().find(|r| r.finished)?;
        if let Some(before) = previous.iter().find(|r| r.id == latest.id) {
            return (!before.finished).then(|| latest.id.clone());
        }
        (request_in_flight && current.last().is_some_and(|last| last.id == latest.id)).then(|| latest.id.clone())
    }

    /// `PaneRunning`: a request starting ends any hold and marks a request in flight.
    pub fn pane_running(&mut self, was_running: bool, is_running: bool) {
        if !is_running {
            return;
        }
        if !was_running {
            self.cancel();
        }
        self.request_in_flight = true;
    }

    /// `RunsChanged`: the card to place now, if any.
    pub fn runs_changed(
        &mut self,
        previous: &[RunProgress],
        current: &[RunProgress],
        result_id: impl Fn(&str) -> String,
        measured: impl Fn(&str) -> bool,
    ) -> Option<String> {
        let run = Self::finished_run_id(previous, current, self.request_in_flight)?;
        self.request_in_flight = false;
        let id = result_id(&run);
        self.awaiting_measure = !measured(&id);
        self.holding_id = Some(id.clone());
        (!self.awaiting_measure).then_some(id)
    }

    /// `ContentMeasured`.
    pub fn content_measured(&mut self, id: &str) -> Option<String> {
        if self.holding_id.as_deref() != Some(id) {
            return None;
        }
        self.awaiting_measure = false;
        Some(id.into())
    }

    /// `MeasureTimedOut`.
    pub fn measure_timed_out(&mut self, id: &str) -> Option<String> {
        if self.holding_id.as_deref() != Some(id) || !self.awaiting_measure {
            return None;
        }
        self.awaiting_measure = false;
        Some(id.into())
    }

    /// `ViewportChanged`.
    pub fn viewport_changed(&self) -> Option<String> {
        if self.awaiting_measure { None } else { self.holding_id.clone() }
    }

    /// `DraftChanged`.
    pub fn draft_changed(&mut self, was_empty: bool, is_empty: bool) {
        if was_empty && !is_empty {
            self.cancel();
        }
    }

    /// `Cancel`.
    pub fn cancel(&mut self) {
        self.holding_id = None;
        self.awaiting_measure = false;
    }
}

/// `Anchor`: hold the camera, or re-aim it at a node.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Anchor {
    Hold,
    Reaim { node_id: String, align_top: bool },
}

impl Anchor {
    pub fn reaim(node_id: &str, align_top: bool) -> Self {
        Anchor::Reaim { node_id: node_id.into(), align_top }
    }
}

/// `TrimAnchor`.
pub fn trim_anchor(
    previous_run_ids: &[String],
    run_ids: &[String],
    selected_node_id: Option<&str>,
    layout_node_ids: &HashSet<String>,
) -> Anchor {
    let Some(last) = run_ids.last() else {
        if previous_run_ids.is_empty() {
            return Anchor::Hold;
        }
        return reaim_anchor(None, selected_node_id, layout_node_ids);
    };
    let previous: HashSet<&String> = previous_run_ids.iter().collect();
    let current: HashSet<&String> = run_ids.iter().collect();
    if Some(last) != previous_run_ids.last() || previous == current {
        return Anchor::Hold;
    }
    // Older requests loaded above the first one are laid out at negative y: nothing already placed moved.
    if run_ids.len() > previous_run_ids.len() && run_ids[run_ids.len() - previous_run_ids.len()..] == *previous_run_ids {
        return Anchor::Hold;
    }
    reaim_anchor(Some(last), selected_node_id, layout_node_ids)
}

/// `ReaimAnchor`.
pub fn reaim_anchor(newest_run_id: Option<&str>, selected_node_id: Option<&str>, layout_node_ids: &HashSet<String>) -> Anchor {
    if let Some(selected) = selected_node_id
        && !is_auxiliary(selected)
        && layout_node_ids.contains(selected)
    {
        return Anchor::reaim(selected, false);
    }
    let Some(newest) = newest_run_id else {
        if !layout_node_ids.contains(PENDING_NODE_ID) {
            return Anchor::Hold;
        }
        return Anchor::reaim(PENDING_NODE_ID, true);
    };
    let newest = mighty_core::graph::node_id(newest, "request");
    if !layout_node_ids.contains(&newest) {
        return Anchor::Hold;
    }
    Anchor::reaim(&newest, true)
}

/// `ResizeAnchor`.
pub fn resize_anchor(
    fitted_result_id: Option<&str>,
    target_id: Option<&str>,
    target_align_top: bool,
    frames: &HashMap<String, GraphRect>,
) -> Anchor {
    let Some(fitted_id) = fitted_result_id else { return Anchor::Hold };
    let Some(fitted) = frames.get(fitted_id) else { return Anchor::Hold };
    if let Some(target_id) = target_id
        && target_id != fitted_id
        && let Some(target) = frames.get(target_id)
        && target.y > fitted.y
    {
        return Anchor::reaim(target_id, target_align_top);
    }
    Anchor::reaim(fitted_id, true)
}

/// `LostFrameIndex`: the card the user saw most of when no current card shows any more, or none.
pub fn lost_frame_index(previous: &[GraphRect], current: &[GraphRect], camera: Point, viewport: Size, zoom: f64) -> Option<usize> {
    if !zoom.is_finite()
        || zoom <= 0.0
        || !camera.x.is_finite()
        || !camera.y.is_finite()
        || viewport.w <= 0.0
        || viewport.h <= 0.0
        || !viewport.w.is_finite()
        || !viewport.h.is_finite()
        || current.is_empty()
    {
        return None;
    }
    let (vx, vy, vw, vh) = (-camera.x / zoom, -camera.y / zoom, viewport.w / zoom, viewport.h / zoom);
    let shown = |frame: &GraphRect, at_least: f64| -> Option<f64> {
        let ix = frame.x.max(vx);
        let iy = frame.y.max(vy);
        let iw = (frame.x + frame.w).min(vx + vw) - ix;
        let ih = (frame.y + frame.h).min(vy + vh) - iy;
        if iw <= 0.0 || ih <= 0.0 {
            return None;
        }
        let least = at_least / zoom;
        if iw < least.min(frame.w) || ih < least.min(frame.h) {
            return None;
        }
        Some(iw * ih)
    };
    const STRANDED_OVERLAP: f64 = 24.0;
    const STRANDED_RESIDUE: f64 = 4.0;
    let mut best: Option<usize> = None;
    let mut best_area = -1.0;
    for (i, frame) in previous.iter().enumerate() {
        if let Some(area) = shown(frame, STRANDED_OVERLAP)
            && area > best_area
        {
            best = Some(i);
            best_area = area;
        }
    }
    let best = best?;
    if current.iter().any(|f| shown(f, STRANDED_RESIDUE).is_some()) { None } else { Some(best) }
}

/// `StrandedWatch.Loss(LookedAtNodeID, NewestRunID)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Loss {
    pub looked_at_node_id: String,
    pub newest_run_id: Option<String>,
}

/// `StrandedWatch` (MightyGraphCamera.cs:236): the memory the stranded rule needs between layout passes. A pass is judged against the last
/// frames seen through the same camera.
#[derive(Debug, Clone)]
pub struct StrandedWatch {
    frames: Vec<GraphRect>,
    node_ids: Vec<String>,
    camera: Option<Point>,
    zoom: f64,
    newest_run_id: Option<String>,
    is_withholding: bool,
}

impl Default for StrandedWatch {
    fn default() -> Self {
        StrandedWatch { frames: vec![], node_ids: vec![], camera: None, zoom: 1.0, newest_run_id: None, is_withholding: false }
    }
}

impl StrandedWatch {
    pub fn new() -> Self {
        Self::default()
    }
    /// `IsWithholding`: a move arrived while unsettled and still waits for its verdict.
    pub fn is_withholding(&self) -> bool {
        self.is_withholding
    }

    /// `Observe`. `settled` is false while a drag, a resize, a recent user move or a pending camera target still decides where the camera goes.
    pub fn observe(
        &mut self,
        nodes: &[(String, GraphRect)],
        camera: Point,
        zoom: f64,
        viewport: Size,
        newest_run_id: Option<&str>,
        settled: bool,
    ) -> Option<Loss> {
        // The draft block comes and goes with the composer's text: it is neither something that moved nor something the user lost.
        let after: Vec<(String, GraphRect)> = nodes.iter().filter(|(id, _)| id != PENDING_NODE_ID).cloned().collect();
        if Some(camera) != self.camera || zoom != self.zoom || newest_run_id != self.newest_run_id.as_deref() {
            self.rebase(&after, camera, zoom, newest_run_id);
            return None;
        }
        let moved = self.node_ids.len() != after.len()
            || after.iter().enumerate().any(|(i, (id, f))| *id != self.node_ids[i] || *f != self.frames[i]);
        if !moved {
            self.is_withholding = false;
            return None;
        }
        if !settled {
            self.is_withholding = true;
            return None;
        }
        let current: Vec<GraphRect> = after.iter().map(|(_, f)| *f).collect();
        let loss = lost_frame_index(&self.frames, &current, camera, viewport, zoom)
            .map(|i| Loss { looked_at_node_id: self.node_ids[i].clone(), newest_run_id: newest_run_id.map(str::to_string) });
        self.rebase(&after, camera, zoom, newest_run_id);
        loss
    }

    fn rebase(&mut self, nodes: &[(String, GraphRect)], camera: Point, zoom: f64, newest_run_id: Option<&str>) {
        self.node_ids = nodes.iter().map(|(id, _)| id.clone()).collect();
        self.frames = nodes.iter().map(|(_, f)| *f).collect();
        self.camera = Some(camera);
        self.zoom = zoom;
        self.newest_run_id = newest_run_id.map(str::to_string);
        self.is_withholding = false;
    }
}

/// `TrimToken`.
pub fn trim_token(sequence: i32, node_id: &str) -> String {
    format!("trim:{sequence}:{node_id}")
}

/// `AdmittedCamera`.
pub fn admitted_camera(
    target_token: Option<&str>,
    consumed_token: Option<&str>,
    target_camera: Option<Point>,
    current: Point,
    requested: Point,
) -> Option<Point> {
    if target_token.is_none() || target_token == consumed_token {
        return Some(requested);
    }
    let target = target_camera?;
    Some(Point::new(target.x + requested.x - current.x, target.y + requested.y - current.y))
}
