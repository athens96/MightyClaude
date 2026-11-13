//! The diagram's half of following the newest work (C# `MightyGraphFollow`, MightyCode.Core/MightyGraphFollow.cs, a port of macOS
//! MightyGraphFollow): while the reader is at the newest work, the newest block's bottom sits right above the composer; a reader who moved
//! away is left where they are until they come back or send a request. Cameras and frames are in node coordinates.

use mighty_core::graph::MightyGraphRun;

use crate::camera::{self, GraphRect, Point, Size};
use crate::layout::{self, Layout};
use crate::support;

/// `Margin`: screen points between the followed block's bottom and the composer.
pub const MARGIN: f64 = 16.0;
/// `Slack`: how far the followed block's bottom may hide behind the composer and the reader still counts as at the newest work.
pub const SLACK: f64 = 36.0;

/// `TargetID`: the block a following camera keeps above the composer, the newest request's lowest block in the flow (of blocks in one row
/// the one added last); with no request the draft block.
pub fn target_id(layout: &Layout, runs: &[MightyGraphRun]) -> Option<String> {
    if runs.is_empty() {
        return layout.nodes.iter().find(|n| n.kind == "draft").map(|n| n.id.clone());
    }
    // The newest work: the newest request, or a notice run after it that is a turn of its own.
    let newest = support::node_id(&runs[support::follow_run_index(runs).unwrap_or(runs.len() - 1)], "");
    let mut best: Option<&layout::Node> = None;
    for node in &layout.nodes {
        // The return node stands where the result will; a delegate line is a label on a branch, never a block to follow
        // (MightyGraphFollow.cs:40).
        let wanted = matches!(
            node.kind.as_str(),
            "request" | layout::WORK_KIND | "result" | "plan" | "agent" | layout::RETURN_KIND | layout::STEP_KIND
        ) && node.id.starts_with(&newest);
        if wanted && node.frame.max_y() >= best.map_or(f64::NEG_INFINITY, |b| b.frame.max_y()) {
            best = Some(node);
        }
    }
    best.map(|n| n.id.clone())
}

fn valid(camera: Point, viewport: Size, zoom: f64) -> bool {
    zoom.is_finite()
        && zoom > 0.0
        && camera.x.is_finite()
        && camera.y.is_finite()
        && viewport.w > 0.0
        && viewport.h > 0.0
        && viewport.w.is_finite()
        && viewport.h.is_finite()
}

/// `IsAtNewest`: the followed block's bottom is on screen, or hidden behind the composer by no more than the slack.
pub fn is_at_newest(target: GraphRect, camera: Point, viewport: Size, zoom: f64) -> bool {
    if !valid(camera, viewport, zoom) {
        return false;
    }
    let bottom = camera.y + target.max_y() * zoom;
    bottom >= 0.0 && bottom <= viewport.h - MARGIN + SLACK
}

/// `CentredX`: the camera x that puts the diagram's centreline in the middle of the viewport.
pub fn centred_x(viewport: Size, zoom: f64) -> f64 {
    viewport.w / 2.0 - camera::CENTRE_X * zoom
}

/// `Camera`: the camera that puts `target`'s bottom `gap` points above the viewport's bottom, at camera x `x`.
pub fn camera(target: GraphRect, viewport: Size, zoom: f64, x: f64, gap: f64) -> Point {
    Point::new(x, viewport.h - gap - target.max_y() * zoom)
}

/// `CameraOffset`: `follows` (the first placement, a send) puts the block's bottom right above the composer on the centreline.
pub fn camera_offset(frame: GraphRect, viewport: Size, zoom: f64, align_top: bool, align_bottom: bool, follows: bool) -> Point {
    if follows {
        camera(frame, viewport, zoom, centred_x(viewport, zoom), MARGIN)
    } else {
        camera::camera_offset_aligned(frame, viewport, zoom, align_top, align_bottom)
    }
}

/// `RunChange`: what a change of the diagram's run ids asks of the camera.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RunChange {
    None,
    First,
    Follow,
    Trim,
}

/// `RunsChanged`: a new last request is the user's own send (follow it); any other change is a trim to re-aim through, unless the camera
/// follows.
pub fn runs_changed(previous: Option<&[String]>, current: &[String], following: bool) -> RunChange {
    let Some(previous) = previous else { return RunChange::First };
    if previous == current {
        return RunChange::None;
    }
    if let Some(last) = current.last()
        && previous.last() != Some(last)
    {
        return RunChange::Follow;
    }
    if following { RunChange::None } else { RunChange::Trim }
}

/// `Watch`: the diagram camera's follow state between layout passes.
#[derive(Debug, Clone)]
pub struct Watch {
    following: bool,
    target_id: Option<String>,
    gap: Option<f64>,
    judging: bool,
}

impl Default for Watch {
    fn default() -> Self {
        Watch { following: true, target_id: None, gap: None, judging: false }
    }
}

impl Watch {
    pub fn new() -> Self {
        Self::default()
    }
    /// `Following`.
    pub fn following(&self) -> bool {
        self.following
    }
    /// `Start`: a send or the first placement.
    pub fn start(&mut self) {
        *self = Watch::default();
    }
    /// `Stop`: nothing follows until the reader comes back to the newest work or sends a request.
    pub fn stop(&mut self) {
        *self = Watch { following: false, target_id: None, gap: None, judging: false };
    }
    /// `UserChangedLayout`: the user opened, folded or resized a block; the next pass judges where that left them.
    pub fn user_changed_layout(&mut self) {
        if self.following {
            self.judging = true;
        }
    }
    /// `Moved`: the user moved the camera, or an aim landed: follow only when the camera shows the newest work.
    pub fn moved(&mut self, target_id: Option<&str>, target: Option<GraphRect>, camera: Point, viewport: Size, zoom: f64) {
        let (Some(id), Some(frame)) = (target_id, target) else {
            self.stop();
            return;
        };
        if !is_at_newest(frame, camera, viewport, zoom) {
            self.stop();
            return;
        }
        self.following = true;
        self.target_id = Some(id.into());
        self.gap = Some(viewport.h - (camera.y + frame.max_y() * zoom));
    }
    /// `Observe`: a layout pass; the camera to move to, none to stay. Only y moves.
    pub fn observe(
        &mut self,
        target_id: Option<&str>,
        target: Option<GraphRect>,
        camera: Point,
        viewport: Size,
        zoom: f64,
    ) -> Option<Point> {
        if !self.following || !valid(camera, viewport, zoom) {
            return None;
        }
        let (Some(id), Some(frame)) = (target_id, target) else { return None };
        if self.judging {
            self.judging = false;
            self.moved(Some(id), Some(frame), camera, viewport, zoom);
            return None;
        }
        let kept = if self.target_id.as_deref() == Some(id) { self.gap.unwrap_or(MARGIN) } else { MARGIN };
        self.target_id = Some(id.into());
        self.gap = Some(kept);
        let next = self::camera(frame, viewport, zoom, camera.x, kept);
        ((next.y - camera.y).abs() > 0.5).then_some(next)
    }
}
