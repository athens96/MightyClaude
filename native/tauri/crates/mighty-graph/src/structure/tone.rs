//! The 파이프라인 view's colours by state (C# `MightyStructure.Tone`, MightyCode.Core/MightyStructureTone.cs; macOS
//! MightyStructureTone.swift): every column takes its step's tone, every card its own; a step still to come takes none. The page gets
//! these as words (`Tone::name`), never `idle`.

use mighty_core::shell::Tone;

use super::Item;
use super::Step;
use super::board::{BoardColumn, Card, CardKind};
use crate::timeline;

/// `Tone.OfStatus`: a card's status in the block buckets; `done` is done and `pending` none.
pub fn of_status(status: &str) -> Option<Tone> {
    match status {
        "pending" => None,
        "done" => Some(Tone::Done),
        _ => Some(Tone::of(timeline::status(status))),
    }
}

/// `Tone.OfStep`: done, error and stopped their own; the current step runs, or waits while its request waits; pending none.
pub fn of_step(step: Step, waiting: bool) -> Option<Tone> {
    match step {
        Step::Done => Some(Tone::Done),
        Step::Current => Some(if waiting { Tone::Wait } else { Tone::Run }),
        Step::Error => Some(Tone::Err),
        Step::Stopped => Some(Tone::Stop),
        Step::Pending => None,
    }
}

/// `Tone.OfColumn` (MightyStructureTone.cs:38): a step's own, but the current step follows its newest request, and a free board's done
/// 결과 whose request awaits notices waits.
pub fn of_column(column: &BoardColumn) -> Option<Tone> {
    if column.step.step == Step::Done && column.cards.iter().any(|c| c.kind == CardKind::Result && c.item.awaiting) {
        return Some(Tone::Wait);
    }
    if column.step.step == Step::Current {
        return Some(current(column.cards.iter().rev().find(|c| c.kind != CardKind::Agent).map(|c| &*c.item)));
    }
    of_step(column.step.step, false)
}

/// The current step's tone for its newest request: its status's once it finished, else running or waiting.
fn current(newest: Option<&Item>) -> Tone {
    let Some(newest) = newest else { return Tone::Run };
    let tone = of_status(&newest.status).unwrap_or(Tone::Run);
    if newest.finished {
        tone
    } else if tone == Tone::Wait {
        Tone::Wait
    } else {
        Tone::Run
    }
}

/// `Tone.OfCard`: a free board's 요청 and 결과 take their column's; every other card its own status's.
pub fn of_card(card: &Card, column: &BoardColumn) -> Option<Tone> {
    match card.kind {
        CardKind::Request | CardKind::Result => of_column(column),
        CardKind::Agent => card.node.as_ref().and_then(|n| of_status(&n.status)),
        _ => of_status(&card.item.status),
    }
}
