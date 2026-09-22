# CLI 계정 (로그인 확인 · 로그아웃 · 계정 변경)

설정 › **CLI 계정**에서 Claude Code·Codex·Gemini CLI가 어떤 계정으로 로그인돼 있는지 보고, 로그아웃하거나 다른 계정으로 바꿀 수 있다. 세 CLI는 자격 증명을 각자 보관하며, 앱은 계정 표시에 필요한 값(이메일·플랜·로그인 방식)만 읽는다. 토큰은 읽어도 화면·로그·상태 파일에 남기지 않는다.

## 상태를 어디서 읽나

| CLI | 방법 | 표시 |
|---|---|---|
| Claude | `claude auth status --json` | 이메일(없으면 조직명) · 구독 종류 · Claude 구독 / Anthropic Console. Bedrock 등 외부 백엔드는 설정 상태와 별도 인증 안내 |
| Codex | `codex login status` + `~/.codex/auth.json`의 id 토큰 안 계정 정보(이메일, ChatGPT 플랜) | 이메일 · 플랜 · ChatGPT / API 키 |
| Gemini | 상태 명령이 없어 `~/.gemini/settings.json`의 `security.auth.selectedType`, `google_accounts.json`의 `active`, `oauth_creds.json` 존재 여부 | Google 계정 이메일 / Gemini API 키 / Vertex AI |

## 로그아웃

- Claude: `claude auth logout`, Codex: `codex logout`.
- Gemini: CLI의 `/auth` 화면이 하는 것과 같이 `oauth_creds.json`을 지우고 `google_accounts.json`의 `active`를 `old`로 옮긴다.
- 그 실행기로 실행 중인 요청이 있거나 CLI 업데이트 중이면 막는다. 터미널에서 직접 쓰는 CLI에도 같이 적용된다는 점을 확인 창에서 알린다.

## 로그인 · 계정 변경

로그인은 브라우저 인증이 끼는 대화형 절차라 앱 안의 **터미널 실행 창**에서 진행한다. "로그인"을 누르면 설정을 닫고 로컬 워크스페이스에 "<CLI> 로그인" 터미널 창을 추가한 뒤, 셸이 뜨면 명령을 붙여 넣고 Enter를 눌러 실행한다(붙여넣기만으로는 셸이 실행하지 않는다). 세 번 시도해도 입력하지 못하면 직접 입력할 명령을 알려준다.

| CLI | 입력하는 명령 |
|---|---|
| Claude | `claude auth login` (메뉴에서 Console 과금을 고르면 `--console`) |
| Claude Bedrock 설정 | Claude CLI의 `/setup-bedrock` 설정 마법사 |
| Codex | `codex login` |
| Gemini | `gemini` (로그아웃 상태로 시작하면 인증 방식을 묻고 브라우저를 연다) |

"계정 변경"은 확인 후 로그아웃하고 곧바로 로그인 터미널을 연다. 브라우저가 같은 계정으로 바로 넘어가려 하면 계정 선택 화면에서 원하는 계정을 고른다. 로그인 터미널을 연 뒤에는 앱이(설정 창이 닫혀 있어도) 5초마다 상태를 확인해 로그인이 끝나면 멈추고, 10분이 지나거나 그 터미널 창을 닫거나 설정의 "대기 취소"를 누르면 그만둔다. 바꾼 계정은 각 실행 창의 **다음 요청부터** 적용된다.

설정은 시트라서 메인 창의 오류 배너가 가려진다. 계정 관련 안내(실행 중이라 바꿀 수 없음, 로컬 워크스페이스 없음, 로그아웃 확인 실패 등)는 해당 CLI 줄 안에 표시한다.

## AWS Bedrock 설정

Claude 줄의 **Bedrock 설정**은 일반 로그인 여부와 관계없이 사용할 수 있다. 현재 계정을 먼저 로그아웃하지 않고 **Claude Bedrock 설정** 터미널을 열어 공식 CLI의 `/setup-bedrock` 마법사를 실행한다. 터미널에서 AWS 인증 방법·리전 등 마법사가 묻는 항목을 완료한 뒤 설정 화면으로 돌아와 **상태 다시 확인**을 누른다. 앱은 AWS 비밀 키를 입력받거나 별도로 저장하지 않는다.

Bedrock의 **설정됨** 표시는 백엔드가 선택됐다는 뜻이다. AWS 자격 증명, 리전, 모델 접근 권한 또는 실제 모델 호출 성공을 확인한 결과가 아니다. 따라서 일반 브라우저 로그인처럼 5초 간격으로 완료를 판정하지 않는다. 리전은 AWS의 정확한 리전 코드를 사용해야 하며 서울은 `ap-northeast-2`다.

Bedrock의 AWS 자격 증명 등 외부 백엔드 설정은 `claude auth logout`으로 지워지지 않는다. 이 상태에는 앱의 로그아웃·계정 변경 버튼을 표시하지 않고 CLI와 해당 클라우드의 설정 방법을 안내한다.

## 한계

- 로컬 워크스페이스가 하나도 없으면 로그인 터미널을 열 수 없다.
- API 키 방식 로그인(`codex login --with-api-key`, `GEMINI_API_KEY`)은 상태 표시만 하고 앱에서 키를 입력받지 않는다. Gemini의 API 키·Vertex AI 방식은 앱에서 로그아웃할 수 없어 버튼 대신 바꾸는 방법을 안내한다.
- 실행 중인지 여부는 앱의 에이전트 실행 창만 본다. 터미널 창에서 직접 실행 중인 CLI가 있어도 로그아웃을 막지 않는다.
- Claude CLI가 `auth status --json`을 지원하지 않거나 시간 안에 응답하지 않으면 "로그인되지 않음"이 아니라 "상태를 읽지 못했습니다"로 표시한다. Codex 계정 정보는 `CODEX_HOME`이 있으면 그 폴더의 `auth.json`에서 읽는다.
- 상태 표시줄의 계정 사용량은 다음 갱신 주기에 새 계정 기준으로 바뀐다.

### 계정·워크스페이스 변경 후 모델 목록

로컬 Claude·Codex 모델 목록은 워크스페이스 폴더와 제공자별로 확인합니다. 실제 세션 전환, 제공자 변경, 새 대화, 로그인 완료·로그아웃, 계정 상태 새로고침 때 해당 환경의 목록을 다시 읽습니다. Bedrock 설정 마법사나 외부 터미널에서 자격 증명을 바꾼 경우 계정 새로고침 또는 작성창 모델 메뉴의 **모델 목록 새로고침**을 누르세요. 계정 상태 표시가 이전과 같아도 목록은 갱신합니다.

Claude의 저장된 모델 선택까지 초기화하려면 **설정 → CLI 계정 → Claude → 모델 초기화·다시 불러오기**를 누릅니다. 모든 로컬 Claude 창의 모델과 추론 강도를 기본값으로 돌리고, 앱의 Claude 모델 캐시를 버린 뒤 워크스페이스별 CLI 모델 목록을 다시 조회합니다. Claude 실행 또는 전송 준비 중에는 초기화를 시작하지 않으며, 초기화 중에는 새 Claude 요청을 받지 않습니다. 대화 기록·초안·이어가기 ID와 다른 제공자·원격 창의 선택은 보존합니다. 조회 실패는 계정 줄에 표시하며, 모델 목록을 읽었다는 사실이 AWS 모델 호출 권한 검증을 의미하지는 않습니다.

이어가기 요청에서 Claude 모델이 기본값이면 `--model default`를 명시하여 이전 CLI 대화의 모델을 자동 복원하지 않도록 합니다. 이 앱 내부 초기화는 Claude 사용자 설정의 모델 고정값을 삭제하지 않습니다. 사용자 설정까지 지우려면 아래 초기화 도구를 먼저 실행하고 Claude Code 로그인을 완료한 뒤 앱에서 다시 불러옵니다.

전송 시 필요한 조회가 진행 중이면 입력과 첨부를 보관하고 완료를 기다린 뒤 최신 선택으로 실행합니다. 조회 중인 요청은 중지할 수 있고 추가 입력은 대기열에 들어갑니다. 같은 세션의 반복 클릭과 가까운 시점의 조회는 합치며, 오래된 조회 결과는 최신 환경을 덮어쓰지 않습니다. 실행 중인 요청의 모델은 바꾸지 않습니다.

CLI의 새 목록에 없는 이전의 구체적인 모델이나 지원되지 않는 사고 강도는 현재 기본값이나 대응하는 별칭으로 보정하고 대화에 안내를 남깁니다. 복구된 이어가기 요청에 현재 기본 모델 ID를 확인할 수 있으면 그 ID를 명시해 이전 대화의 낡은 모델이 다시 선택되지 않게 합니다. 유효한 Claude 별칭과 목록에서 확인한 적 없는 사용자 지정 모델은 유지하며, 조회 실패 시 대체 목록만으로 선택을 바꾸지 않습니다. 모델 목록 확인은 실제 모델 호출이나 AWS 접근 권한 검증을 대신하지 않습니다. 원격 워크스페이스는 연결된 호스트가 제공하는 목록을 사용합니다.

Bedrock 계정 상태 확인에서 터미널 환경과 Claude 사용자 설정의 `AWS_BEARER_TOKEN_BEDROCK` 값이 서로 다르면 인증 정보가 두 곳에 다르게 설정되어 있다는 안내를 표시합니다. 앞뒤 공백을 제외하고 비교하며, 키 값은 화면·로그에 출력하거나 따로 저장하지 않습니다. 이 안내는 어느 키가 유효한지 판정하지 않습니다. AWS 403 오류가 발생하면 **설정 → CLI 계정 → Bedrock 설정**에서 사용할 인증 정보를 확인하고 상태를 새로고침하세요. 앱은 기존 자격 증명을 자동으로 지우거나 설정 파일을 수정하지 않습니다.

CLI 탐색·모델 조회·실제 실행은 해당 작업 폴더의 로그인 셸 환경을 공유합니다. 계정 상태 확인과 로그아웃도 같은 방식으로 사용자 홈에서 환경을 읽습니다. 인증 또는 모델 목록을 갱신하면 환경도 다시 읽으며 새 셸에서 명시적으로 `unset`된 변수는 사라지지만, 선언 줄만 삭제하면 앱 시작 환경에서 상속된 값이 남을 수 있어 앱과 터미널을 다시 시작해야 합니다. 환경은 메모리에만 보관합니다. 셸 실행에 실패하면 앱 시작 환경을 사용했다는 안내를 표시하며, 읽지 못한 터미널의 키와 충돌한다고 단정하지 않습니다. CLI 자체의 설정 파일 우선순위는 바꾸지 않으므로, 셸 환경 동기화만으로 설정 파일에 있는 잘못된 키나 403 오류가 해결되는 것은 아닙니다.

### Bedrock 인증 진단

기존 Bedrock 설정을 지우고 Claude Code에서 다시 설정하려면 `python3 scripts/reset-claude-bedrock.py`로 삭제 계획을 확인한 뒤 `--apply`를 붙여 실행합니다. 이 도구는 사용자 Claude 설정과 셸 시작 파일의 식별 가능한 Bedrock 항목을 정리하며 원본을 비공개 백업에 보관합니다. Codex 로그인, Claude OAuth 자격 증명, `~/.aws` 프로필은 삭제하지 않습니다. 홈 폴더 쓰기가 허용된 사용자 터미널에서 실행해야 합니다.

모델 고정값도 모두 초기화하려면 `--reset-models`를 추가합니다. 사용자 설정의 모델 선택·목록 제한·모델 ID 매핑과 셸의 Claude 모델 지정 변수를 함께 지웁니다. 예를 들어 `python3 scripts/reset-claude-bedrock.py --reset-models --apply`를 실행합니다. 프로젝트·조직의 관리 설정이나 기존 CLI 대화 파일은 수정하지 않습니다. 모델을 새로 선택하려면 새 Claude Code 세션을 시작하거나 `/setup-bedrock`에서 다시 설정합니다.

파일 초기화는 이미 실행 중인 앱이나 터미널의 환경 변수를 지우지 않습니다. 현재 확인된 구성에서는 적용 성공 후 로그인할 터미널에서 `unset AWS_BEARER_TOKEN_BEDROCK CLAUDE_CODE_USE_BEDROCK AWS_REGION`을 실행하고 Claude Code를 새로 시작합니다. MightyClaude도 종료 후 다시 실행합니다. Bedrock 단기 키로 다시 설정할 때는 Claude Code에서 `/setup-bedrock`을 입력하여 API 키 방식과 발급 리전을 선택합니다. 마법사의 저장 위치와 동작은 [Claude Code Bedrock 설정 문서](https://code.claude.com/docs/en/amazon-bedrock#sign-in-with-bedrock)를 참고하세요.

터미널에서도 인증이 실패하면 저장 위치 비교와 AWS 응답 확인을 분리해서 확인합니다.

```bash
python3 scripts/diagnose-bedrock.py
python3 scripts/diagnose-bedrock.py --check-network
```

기본 실행은 현재 터미널 환경, 새 로그인 셸 환경, Claude 사용자 설정을 읽어 토큰의 존재·일치 여부와 해석 가능한 단기 토큰의 리전·표기 만료 시각을 확인합니다. 토큰 원문과 해시는 출력하지 않으며 설정을 수정하지 않습니다. 단기 토큰의 표기 만료 전이라도 발급 원본 AWS 세션이 만료되거나 권한이 변경되면 사용할 수 없습니다. 알 수 없는 형식은 유효하지 않은 키라고 단정하지 않습니다.

설정된 리전에서 AWS가 제공하는 Claude 모델과 시스템 추론 프로필을 조회하려면 다음 명령을 사용합니다.

```bash
python3 scripts/diagnose-bedrock.py --list-models
```

이 모드는 사용자 설정을 적용한 토큰과 리전으로 공식 Bedrock 관리 API의 [ListFoundationModels](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_ListFoundationModels.html)와 [ListInferenceProfiles](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_ListInferenceProfiles.html)를 읽습니다. `catalog.foundationModels.modelIds`에는 공개 Claude 기본 모델 ID를, `catalog.inferenceProfiles.profiles`에는 `SYSTEM_DEFINED` 프로필의 공개 Claude ID와 `ACTIVE` 상태를 표시합니다. 기본 모델의 `LEGACY` 상태만으로 목록에서 제외하지 않습니다. 모델 이름·설명·ARN·계정 ID·페이지 커서·AWS 응답 원문은 출력하지 않습니다. 두 목록은 독립적이므로 해당 리전의 기본 모델 목록에 없는 `global.` 프로필도 유지하며, `apac.` 같은 접두사를 추정해서 붙이지 않습니다. 이 모드는 `--model`로 특정 모델을 선택하지 않습니다.

목록 조회는 모델 추론을 요청하지 않으며 `--check-network`·`--check-inference`와 함께 사용할 수 없습니다. 설정된 공식 AWS 리전만 사용하고 프록시·사용자 지정 엔드포인트·Mantle 구성은 건너뜁니다. 시스템 프로필은 한 페이지에 최대 1,000개, 최대 5페이지를 읽고 각 응답은 1 MiB, 각 목록은 5,000개 원본 항목으로 제한합니다. 중복 ID는 합치며, 제한 도달·잘못되거나 반복된 커서·일부 요청 실패 시 이미 읽은 목록을 보존하고 `complete: false`와 `incompleteReason`을 표시합니다. `fetchStatus`에는 마지막 요청의 공개 상태만, `filteredEntries`에는 공개 Claude ID·프로필 형식 검증을 통과하지 못해 제외한 항목 수를 표시합니다. `complete: true`는 이 범위에서 모든 페이지를 읽었다는 의미이며 모델 호출 권한이나 추론 성공을 뜻하지 않습니다.

목록 조회 권한과 모델 호출 권한은 별개입니다. 계정의 모델 계약·권한·리전 가용성은 별도의 [GetFoundationModelAvailability API](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_GetFoundationModelAvailability.html)로 확인할 수 있지만, 이 진단의 목록 모드는 그 API를 호출하거나 계정 설정·동의 상태를 변경하지 않습니다. 실제 추론 성공 여부는 아래 `--check-inference`로 별도 확인합니다.

`--check-network`는 공식 AWS Bedrock Runtime 주소에 메시지가 없는 `{}` 요청을 보내 인증 오류와 입력 검증 오류를 구분하는 데 사용합니다. 모델 추론은 요청하지 않으며, 입력 검증 응답도 모델 사용 권한이나 실제 추론 성공을 보장하지 않습니다. 프록시·사용자 지정 엔드포인트를 사용하는 구성은 이 검사의 지원 범위 밖입니다. 네트워크가 제한된 개발 환경에서는 AWS에 연결할 수 있는 사용자의 터미널에서 실행해야 합니다.

HTTP 400만으로 원인을 구분할 수 없다면 다음 명령으로 실제 모델 응답을 확인합니다.

```bash
python3 scripts/diagnose-bedrock.py --check-inference
```

이 옵션은 사용자 설정을 적용한 토큰으로 Bedrock **InvokeModel 요청을 한 번** 보냅니다. 고정된 짧은 테스트 문장과 최대 출력 1토큰을 사용하며, 성공 시 소량의 모델 사용료가 발생합니다. 대화 내용·프로젝트 파일은 보내지 않고 모델 답변이나 AWS 오류 본문도 출력하지 않습니다. `inferenceSucceeded`가 참이면 해당 토큰·리전·모델의 단일 호출 성공을 확인한 것입니다. Claude Code의 스트리밍 호출이나 다른 모델의 권한까지 검증한 결과는 아닙니다. 기본 모델은 `global.anthropic.claude-fable-5-1`이며 다른 공개 Claude 모델 ID는 `--model`로 지정할 수 있습니다. 출력의 `requestedModel`은 이 진단이 선택한 모델이며 Claude Code에 저장된 선택 모델을 뜻하지 않습니다. Mantle 구성은 이 Runtime 진단의 지원 범위 밖입니다.

Fable 5.1은 서울(`ap-northeast-2`)의 Bedrock Runtime에서 Global 추론 프로필을 지원하며 In-Region·APAC 추론은 지원하지 않습니다. 이 모델에 리전만 보고 `apac.` 접두사를 붙이면 안 됩니다. Global 프로필은 요청을 다른 AWS 리전에서 처리할 수 있고 해당 프로필의 접근 권한이 필요합니다. [AWS Fable 5.1 모델카드](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-anthropic-claude-fable-5-1.html)를 참고하세요. Fable 5.1의 추가 데이터 보존·검토 조건은 [AWS 데이터 보존 문서](https://docs.aws.amazon.com/bedrock/latest/userguide/data-retention.html)에 설명되어 있으며 이 진단은 약정 생성·보존 모드 변경 API를 직접 호출하지 않습니다.

AWS가 `AccessDeniedException`과 함께 명시적인 인증 실패 메시지를 반환하면 `errorType`은 유지하고 `outcome`을 `server_rejected_authentication`으로 표시합니다. 이것만으로 키 만료, 발급 세션 종료, 잘못된 키 또는 권한 문제 중 어느 것이 원인인지 확정하지 않습니다. 인증 실패가 명시되지 않은 일반 `AccessDeniedException`은 `access_denied_key_validity_unknown`으로 남깁니다.

AWS 오류 본문에 Bedrock 모델 호출을 SCP(Service Control Policy)가 명시적으로 거부한 표준 오류 문장이 있으면 진단은 `service_control_policy_denied`로 구분합니다. 앱도 확인된 Claude 오류 응답에는 조직 관리자에게 SCP를 확인하도록 안내합니다. CLI의 일반적인 키 갱신 안내만으로 인증 실패라고 판단하지 않습니다. SCP의 명시적 Deny는 키 재발급이나 IAM Allow 추가만으로 해제되지 않으며, 어떤 정책·리전 조건이 적용됐는지는 조직 관리자가 확인해야 합니다. [AWS 접근 거부 오류 안내](https://docs.aws.amazon.com/IAM/latest/UserGuide/troubleshoot_access-denied.html)를 참고하세요.

앱의 CLI 모델 목록은 실제 호출 권한이 검증된 목록이 아닙니다. AWS 모델·프로필 목록 조회 성공과 ACTIVE 상태도 SCP·IAM을 포함한 실제 모델 호출 허용을 보장하지 않습니다. 약정 상태와 데이터 보존 모드는 별도 조건이며, `inherit`만으로 유효 보존 모드를 단정하거나 모든 403을 동의 누락으로 분류하지 않습니다.

단기 키의 리전·만료 조건은 [AWS API 키 설명](https://docs.aws.amazon.com/bedrock/latest/userguide/api-keys-how.html)을 참고하세요. 이 진단은 사용자 설정 파일을 기준으로 하며, 조직의 관리 설정이나 프로젝트별 인증 덮어쓰기까지 재현하지는 않습니다.

`[bedrock-discovery] Failed to list models`는 OpenClaw의 모델 자동 탐색에서도 출력됩니다. 로컬 OpenClaw 2026.1.30에서는 `ListFoundationModels` 요청의 실패 로그이며, Claude Code의 추론 결과가 아닙니다. `.zshrc`의 `source <(openclaw completion --shell zsh)`가 셸 시작마다 이 경로를 실행할 수 있습니다. Claude 사용자 설정의 `env.AWS_REGION`은 OpenClaw에 적용되지 않습니다. OpenClaw 탐색 설정과 셸의 `AWS_REGION`·`AWS_DEFAULT_REGION`이 모두 없으면 이 버전은 `us-east-1`을 사용하므로, 서울 리전 단기 키와 맞지 않습니다. 셸에도 발급 리전을 지정하고 키를 불러온 다음 자동완성을 실행해야 합니다. 이 설정을 고쳤더라도 실제 Claude 모델 호출 성공은 별도로 확인해야 합니다.
