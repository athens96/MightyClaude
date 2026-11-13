//! The role view of a Claude pane's flow (docs/mighty-mode.md "역할 보기"): who each block is (`roles`, C# FlowRoles.cs), a sub-agent's
//! or a background command's compact card (`card`, FlowCard.cs), the delegate line and the return node (`lines`, FlowLines.cs), a
//! request's steps (`steps`, the view parts of FlowSteps.cs), the session log strip (`log`, FlowLog.cs) and the page's copy of every
//! node (`copy`, FlowCopy.cs).

pub mod card;
pub mod copy;
pub mod lines;
pub mod log;
pub mod roles;
pub mod steps;
