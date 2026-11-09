# 개발 도구 기능 (Mac)

[`docs/devtool-gap-review.md`](devtool-gap-review.md)에서 고른 항목을 구현한 기록이다. 항목마다 모델 규칙은 `MightyCore`에, 화면은 `MightyClaude`에 둔다. Windows는 아직 따라가지 않았다.

## 1. 요청 되돌리기

- 모델: `MightyCore/RunRevert.swift`, 검사: `Tests/MightyCoreTests/RunRevertTests.swift`.
- 화면: 마이티 보기의 결과 블록 머리에 되돌리기 단추(`arrow.uturn.backward.circle`)가 붙는다. `FileOriginalStore`가 그 요청이 바꾼 파일의 원본을 하나라도 갖고 있을 때만 보인다. 누르면 `RunChangesSheet`가 열린다.
- 되돌리기는 `FileOriginalStore`가 보관한 "요청이 처음 바꾸기 전 내용"을 파일에 다시 쓴다. 변경사항 보기가 기준으로 쓰는 바로 그 내용이다.
- 거부 규칙(한 파일의 거부가 다른 파일을 막지 않는다):
  - `laterRun`: 같은 창의 더 뒤 요청도 그 파일을 바꿨다. 앞 요청을 되돌리면 뒤 요청의 일까지 사라지므로 뒤 요청부터 되돌려야 한다.
  - `missing`: 파일이 없어졌거나 일반 파일이 아니거나 폴더 밖을 가리킨다.
  - `unreadable`: 지금 파일이 1 MiB를 넘거나 바이너리라 diff로 읽을 수 없다.
  - `unchanged`: 이미 원본과 같다.
- 요청이 새로 만든 파일은 지우지 않고 휴지통으로 옮긴다.
- 다시 쓸 때 지금 파일의 인코딩(BOM 포함, CP949·UTF-16도)과 권한을 그대로 둔다.
- 확인 창은 "요청 뒤에 직접 고친 내용도 함께 사라진다"고 알린다. 각 줄에 되돌리면 돌아올 줄 수와 빠질 줄 수를 보여 준다.
