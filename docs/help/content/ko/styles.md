---
title: 스타일
order: 4
section: styles
---
스타일은 요청을 보내는 방식을 정해 둔 작업 흐름입니다. 단계와 단추가 준비되어 있어서, 긴 작업을 정해진 순서로 진행할 수 있습니다. 스타일을 쓰지 않으면 지금처럼 자유롭게 요청합니다(CLI).

## 스타일 고르기 {#pick}
스타일은 이 Mac의 Claude 창에서, 마이티 보기일 때 씁니다.
1. Claude 창을 {{ui:graph.view.mighty}} 보기로 바꿉니다.
2. 입력창 위의 스타일 메뉴({{ui:guidedPanel.stylesMenuAccessibility}})를 엽니다.
3. 스타일을 고릅니다. 다시 자유 요청으로 돌아가려면 CLI를 고릅니다.

선택은 창마다 저장됩니다. 실행 중에는 바꿀 수 없습니다. 스타일에 필요한 플러그인이나 도구가 없으면 {{ui:guidedPanel.installButton}} 단추가 보입니다. 누르면 새 터미널 창에 설치 명령이 채워지고, {{kbd:Enter}}는 직접 누릅니다.

> [!note]
> 기본 스타일의 단계 이름과 단추는 지금은 한국어로만 보입니다.

## 기본 스타일 {#bundled}
| 스타일 | 하는 일 |
|---|---|
| 오로보로스 (Ouroboros) | 인터뷰로 목표를 분명히 한 뒤 시드 → 실행 → 평가 → 진화 순서로 진행합니다. 첫 요청이 인터뷰가 됩니다. Ouroboros 플러그인과 `uvx`가 필요합니다. |
| 페이퍼신 (Paperthin) | 더하기보다 덜어내기에 맞춘 작은 스킬들을 단추로 모아 둡니다. 입력한 글은 그대로 보냅니다. |
| 슈퍼파워 (Superpowers) | 브레인스토밍 → 계획 → 실행 → 완료 순서로 진행합니다. 계획 파일의 체크박스를 보고 단계와 진행 막대가 저절로 넘어갑니다. |
| 클러드 플랜 | 새 요청마다 Claude의 계획 모드로 시작합니다. 계획 카드를 승인하면 실행합니다. 체크리스트 진행과 백그라운드 작업이 위젯으로 보입니다. |

## 계획 카드 {#plan-card}
Claude가 계획 모드(Plan mode, 또는 클러드 플랜 스타일)에서 계획을 마치면 계획 카드가 뜹니다.

![[plan-card]]

1. 계획을 읽습니다. {{ui:plan.card.expand}}를 누르면 문서처럼 크게 봅니다.
2. 하나를 고릅니다.
   - {{ui:plan.card.approveAuto}}: 승인하고 파일 수정은 묻지 않고 진행합니다.
   - {{ui:plan.card.approveConfirm}}: 승인하고 작업마다 확인을 받습니다.
   - {{ui:plan.card.revise}}: 바꿀 점을 적어 {{ui:plan.card.reviseSend}}를 누르면 Claude가 다시 계획합니다.
   - {{ui:plan.card.cancel}}: 계획을 거절하고 이번 작업을 멈춥니다.

마이티 보기의 다이어그램에서는 계획이 요청 아래 블록에 뜨고, 네 가지 답은 입력창 쪽(스타일을 쓰면 스타일 패널)에 나옵니다. 계획 블록은 결과 카드처럼 창 크기에 맞춰지고, 크기를 직접 바꿨다면 {{ui:graph.result.fitToWindow}} 단추가 생깁니다. 기본 보기와 타임라인에서는 계획과 답이 한 카드로 입력창 위에 뜹니다.

데스크톱 펫도 끝난 계획을 보여 줍니다. 받은 시각과 계획의 첫 줄들, 답할 버튼이 나옵니다. 펫에서 바로 {{ui:plan.card.approveAuto}} 단추나 {{ui:plan.card.cancel}} 단추를 누를 수 있습니다. 수정 요청을 쓰거나 계획 전체를 읽으려면 {{ui:companion.plan.review}} 단추(Mac에서는 {{ui:plan.card.revise}} 단추도)를 누르세요. 네 가지 답이 모두 있는 작은 창에 계획이 열리고, 계획에 답하면 창이 닫힙니다.

답한 계획은 기록으로 남아 다시 볼 수 있습니다.

![[plan-document]]

## 다른 스타일 추가하기 {#custom}
기본 스타일 말고 파일로 된 스타일도 쓸 수 있습니다.
1. {{ui:menu.settings}} → {{ui:settings.nav.styles}}에서 {{ui:settings.styles.registerButton}}을 누릅니다. 저장소의 `.claude/mighty-styles/` 안의 스타일은 저절로 찾습니다. 목록이 맞지 않으면 {{ui:settings.styles.rescanButton}}을 누릅니다.
2. 새 스타일은 처음 한 번 내용을 확인해야 합니다. {{ui:settings.styles.viewButton}} 단추로 출처, 자동 허용할 도구, 설치 명령, 모든 단추를 확인합니다.
3. 괜찮으면 {{ui:settings.styles.allowButton}}을 누릅니다.

스타일 파일이 바뀌면 다시 확인을 받습니다. 허용을 거두려면 같은 곳의 {{ui:settings.styles.revokeButton}}를 누릅니다.

> [!warning]
> 도구를 자동 허용하는 스타일은 그 도구를 묻지 않고 실행합니다. 출처를 믿을 수 있을 때만 허용하세요.
