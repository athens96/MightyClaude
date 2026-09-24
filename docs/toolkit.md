# 내 작업 도구 모음 (Toolkit)

설정 → 구성 요소 화면의 "내 작업 도구 모음" 섹션 사양.

## 개요

번들 도구(앱 내장) 한 개와 사용자가 추가한 도구를 합쳐 보여 준다.
하나의 확인 화면에서 실행될 모든 명령을 미리 보고 설치를 진행한다.
설치 결과는 프로브(파일 존재 여부)로 판정하며, 명령 종료 코드는 사용하지 않는다.

## 번들 항목

| ID | 종류 | 소스 |
|----|------|------|
| mighty-styles | plugin | athens96/mighty-styles (mighty-styles@mighty-styles) |

mighty-styles는 아직 게시되지 않아 설치 행이 실패로 표시될 수 있다. 이는 정상 동작이다.

## 항목 종류

다섯 가지 닫힌 설치 템플릿만 허용한다.

| 종류 | 필드 |
|------|------|
| plugin | source (owner/repo 또는 https URL), pluginID (name@marketplace) |
| mcp | name, executable (절대 경로 또는 PATH 이름), args 배열 |
| skill | url (https) |
| package | manager (brew/npm), name |
| repoScript | url (https), ref (40-hex SHA 또는 태그), scriptPath (상대 경로, .. 없음) |

자유 형식 셸 문자열은 어디에서도 허용되지 않는다.

## 저장소

`toolkit.json`은 `StateRepository` 앱 데이터 디렉터리 안에 `workspace-state.json` 옆에 둔다.

```json
{
  "version": 1,
  "entries": [
    {
      "id": "my-tool",
      "displayName": "My Tool",
      "install": { "kind": "brew", "manager": "brew", "name": "my-tool" },
      "approval": { "contentHash": "<sha256>" }
    }
  ]
}
```

쓰기는 임시 파일 + 이름 바꾸기로 원자적으로 수행한다.
파일을 읽을 수 없으면 오류를 화면에 표시하고 파일을 건드리지 않는다. 번들 목록은 계속 보인다.

## 승인

사용자 항목은 내용 해시(SHA-256)에 묶인 승인이 있어야 설치된다.
repoScript는 태그를 git ls-remote로 해석해 40-hex 커밋 SHA를 함께 저장한다.
내용이 바뀌면 승인이 무효가 된다.

## 탐지

파일과 경로만 확인한다. 프로세스를 실행하거나 응답을 확인하지 않는다.

| 종류 | 조건 |
|------|------|
| plugin | `~/.claude/plugins/installed_plugins.json`에 scope "user" 레코드가 있는가 |
| mcp | `~/.claude.json`의 mcpServers에 해당 이름이 있는가 |
| skill | `~/.claude/skills/<name>/SKILL.md` 존재 여부 |
| package (brew) | `<prefix>/opt/<name>` 존재 여부 |
| package (npm) | `<npm bin>/../lib/node_modules/<name>` 존재 여부 |
| repoScript | 승인된 SHA의 완료 마커 파일 존재 여부 |

## 화면 조작 (설정 → 구성 요소)

`ComponentsSettingsView.swift`의 `ToolkitSettingsSection`이 그린다.
접근성 ID는 모두 `settings-toolkit`으로 시작한다.

| 조작 | 접근성 ID | 하는 일 |
|------|-----------|---------|
| 추가 | `settings-toolkit-add` | JSON 파일 하나를 읽어 항목을 미승인 상태로 등록 |
| 삭제 | `settings-toolkit-remove-<entryId>` | 목록에서만 지움(설치물은 그대로) |
| 승인 | `settings-toolkit-approve-<entryId>` | 내용 해시를 승인(repoScript는 태그를 SHA로 고정) |
| 내보내기 | `settings-toolkit-export` | 승인 정보 없는 항목 배열을 파일로 저장 |
| 가져오기 | `settings-toolkit-import` | 배열을 읽어 모두 미승인으로 추가 |
| 설치 | `settings-toolkit-install` | 확인 시트를 연다 |
| 확인 시트 | `settings-toolkit-confirm` / `settings-toolkit-cancel` | 실행 / 취소 |
| 항목 행 | `settings-toolkit-entry-<entryId>` | 이름·상태(준비됨/설치 필요)·배지 |
| 결과 표 | `settings-toolkit-results` | 실행 뒤 항목별 설치됨/실패/미승인 |

## 설치 실행

1. "설치" 버튼을 누르면 계획을 세운다(누락되고 승인된 항목만).
2. 하나의 확인 시트에 실행될 모든 명령(argv 그대로)을 보여 준다.
3. 확인 후 목록 순서대로 실행한다. 하나 실패해도 나머지는 계속 진행한다.
4. 네트워크 오류 패턴이 감지되면 fetch 단계를 한 번 재시도한다.
5. 각 항목을 다시 프로브해 결과 표를 표시한다.

항목 종류별 명령(`ToolkitRunner.installCommands` 한 곳에서만 만든다):

| 종류 | 명령 |
|------|------|
| plugin | `claude plugin marketplace add --scope user <source>` → `claude plugin install <pluginID> --scope user --json`. 마켓플레이스 이름은 그 저장소의 매니페스트가 정하므로 pluginID의 `@` 뒤와 같아야 한다. 이미 등록된 마켓플레이스라 첫 명령이 실패해도 설치는 이어서 시도한다. |
| mcp | `claude mcp add --scope user <name> -- <executable> <args…>` |
| skill | `git clone <url> ~/.claude/skills/<이름>` |
| package | `brew install <name>` 또는 `npm install -g <name>` |
| repoScript | `git clone --no-checkout <url> <앱 데이터>/toolkit-clones/<SHA>` → `git -C … checkout <SHA>` → 스크립트. 한 단계라도 실패하면 그 항목은 거기서 멈춘다. 스크립트는 심볼릭 링크를 풀어도 클론 안에 있는 일반 파일일 때만 실행한다. |

plugin 명령은 `ClaudePluginService`를 거치지 않고 같은 `claude` 실행 파일을 argv로 직접 부른다. 설치 범위는 늘 `user`다.

## auto-run 예외

`docs/mighty-styles.md` 1.5·4.4·4.6의 auto-run 금지 규칙은 스타일 매니페스트에 적용된다.
**도구 모음(toolkit)은 그 예외다**: 사용자가 확인 화면에서 직접 설치를 승인하며,
명령은 argv 배열로만 실행되고 셸 문자열을 쓰지 않는다.

## 내보내기 / 가져오기

내보내기: 승인 정보 없이 항목 배열을 JSON으로 저장한다.
가져오기: 배열을 읽어 각 항목을 미승인 상태로 추가(기존 ID는 교체)한다.

항목 제거는 목록에서만 지운다. 설치된 파일이나 등록 정보는 건드리지 않는다.
