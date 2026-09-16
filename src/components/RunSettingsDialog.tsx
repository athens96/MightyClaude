import { useState } from 'react'
import { Bot, Info, SlidersHorizontal, Sparkles } from 'lucide-react'
import type { ClaudeModelCatalog, ClaudeModelOption, ClaudeRunSettings, ProviderRuntime } from '../../shared/types'
import { providerEffortLevels } from '../../shared/provider-options'
import { Modal } from './Modal'

const PERMISSION_DESCRIPTIONS: Record<ClaudeRunSettings['permissionMode'], string> = {
  manual: 'CLI에서 이미 허용한 도구를 사용합니다.',
  plan: '코드를 수정하지 않고 읽기와 계획 중심으로 진행합니다.',
  acceptEdits: '파일 편집을 자동 허용합니다. 그 밖의 도구는 기존 권한을 따릅니다.',
}

export function RunSettingsDialog({ sessionTitle, settings, selectedModel, catalog, providerRuntime, remoteHost, unavailableModel, running, onSave, onClose }: {
  sessionTitle: string
  settings: ClaudeRunSettings
  selectedModel: ClaudeModelOption
  catalog: ClaudeModelCatalog
  providerRuntime: ProviderRuntime
  remoteHost?: string
  unavailableModel: boolean
  running: boolean
  onSave: (settings: ClaudeRunSettings) => void
  onClose: () => void
}) {
  const [permissionMode, setPermissionMode] = useState(settings.permissionMode)
  const [maxTurns, setMaxTurns] = useState(settings.maxTurns?.toString() ?? '')
  const [maxBudget, setMaxBudget] = useState(settings.maxBudgetUsd?.toString() ?? '')
  const [errors, setErrors] = useState<{ maxTurns?: string; maxBudget?: string }>({})
  const capabilities = providerRuntime.capabilities
  const effortAvailable = capabilities.effort && providerEffortLevels(providerRuntime.id, selectedModel.value, catalog).length > 0
  const effortNote = !capabilities.effort
    ? `${providerRuntime.name} 실행기는 사고 강도 선택을 지원하지 않습니다.`
    : selectedModel.supportsEffort === false || /haiku/i.test(selectedModel.value)
      ? '이 모델은 사고 강도 설정을 지원하지 않습니다.'
      : '이 모델의 지원 강도를 확인할 수 없어 Auto를 사용합니다. CLI가 기본 강도를 결정합니다.'
  const permissionDescription = providerRuntime.id === 'codex'
    ? permissionMode === 'acceptEdits' ? '프로젝트 폴더 안에서 파일 수정과 명령 실행을 허용합니다.' : '읽기 전용 샌드박스에서 작업합니다. 이 실행기는 별도 계획 모드를 제공하지 않습니다.'
    : PERMISSION_DESCRIPTIONS[permissionMode]

  function save() {
    if (running) return
    const turns = !capabilities.maxTurns || maxTurns.trim() === '' ? null : Number(maxTurns)
    const budget = !capabilities.maxBudgetUsd || maxBudget.trim() === '' ? null : Number(maxBudget)
    const nextErrors: typeof errors = {}
    if (turns !== null && (!Number.isInteger(turns) || turns < 1 || turns > 1000)) nextErrors.maxTurns = '1부터 1,000 사이의 정수를 입력하세요.'
    if (budget !== null && (!Number.isFinite(budget) || budget <= 0 || budget > 10_000)) nextErrors.maxBudget = '0보다 크고 10,000 이하인 USD 금액을 입력하세요.'
    setErrors(nextErrors)
    if (Object.keys(nextErrors).length > 0) return
    onSave({ ...settings, permissionMode: capabilities.permissionModes.includes(permissionMode) ? permissionMode : 'manual', maxTurns: turns, maxBudgetUsd: budget })
    onClose()
  }

  return (
    <Modal title={`${sessionTitle} 실행 설정`} description="이 실행 창의 다음 요청부터 적용됩니다." className="run-settings-modal" onClose={onClose}>
      <section className="selected-model-info" aria-label="선택한 모델 정보">
        <div className="selected-model-heading">{providerRuntime.id === 'claude' ? <span className="claude-glyph" aria-hidden="true">✳</span> : providerRuntime.id === 'codex' ? <Bot size={16} /> : <Sparkles size={16} />}<strong>{selectedModel.displayName}</strong><span className="model-source-badge">{catalog.source === 'cli' ? 'CLI에서 확인' : catalog.source === 'preview' ? '미리보기' : '기본 목록'}</span></div>
        <p>{selectedModel.description}</p>
        {unavailableModel && <p className="saved-model-note">현재 모델 목록에 없는 저장된 모델입니다. 모델 ID는 유지되며, 사용 가능 여부는 {providerRuntime.name}에서 확인합니다.</p>}
        <div className="model-catalog-note"><Info size={12} /><span>{catalog.detail}</span></div>
        {!effortAvailable && <p className="field-description">{effortNote}</p>}
        <div className="model-catalog-note"><Info size={12} /><span>{remoteHost ? `${remoteHost}의 ${providerRuntime.name}에서 실행합니다. ` : `${providerRuntime.name} · `}{providerRuntime.detail}</span></div>
      </section>
      <form noValidate onSubmit={(event) => { event.preventDefault(); save() }}>
        <fieldset disabled={running} className="run-settings-fields">
          <div className="run-setting-field">
            <label className="form-label" htmlFor="run-permission-mode">작업 권한</label>
            <select id="run-permission-mode" data-autofocus className="form-input form-select" value={permissionMode} onChange={(event) => setPermissionMode(event.target.value as ClaudeRunSettings['permissionMode'])} aria-describedby="permission-description">
              <option value="manual" disabled={!capabilities.permissionModes.includes('manual')}>{providerRuntime.id === 'codex' ? '읽기 전용' : '기본 권한 사용'}</option>
              <option value="plan" disabled={!capabilities.permissionModes.includes('plan')}>계획만 진행{!capabilities.permissionModes.includes('plan') ? ' · 미지원' : ''}</option>
              <option value="acceptEdits" disabled={!capabilities.permissionModes.includes('acceptEdits')}>{providerRuntime.id === 'codex' ? '워크스페이스 쓰기 허용' : '파일 수정 허용'}</option>
            </select>
            <p className="field-description" id="permission-description">{permissionDescription}</p>
          </div>
          <div className="run-limit-fields">
            <div className="run-setting-field">
              <label className="form-label" htmlFor="run-max-turns">최대 턴 수</label>
              <input className="form-input" id="run-max-turns" type="number" inputMode="numeric" min="1" max="1000" step="1" placeholder={capabilities.maxTurns ? '제한 없음' : '미지원'} disabled={!capabilities.maxTurns} value={maxTurns} onChange={(event) => { setMaxTurns(event.target.value); setErrors((current) => ({ ...current, maxTurns: undefined })) }} aria-invalid={Boolean(errors.maxTurns)} aria-describedby={errors.maxTurns ? 'max-turns-error' : 'max-turns-help'} />
              {errors.maxTurns ? <p className="field-error" id="max-turns-error" role="alert">{errors.maxTurns}</p> : <p className="field-description" id="max-turns-help">{capabilities.maxTurns ? '요청 한 번의 에이전트 턴 수 · 1–1,000' : `${providerRuntime.name} 실행기는 턴 수 제한을 제공하지 않습니다.`}</p>}
            </div>
            <div className="run-setting-field">
              <label className="form-label" htmlFor="run-max-budget">비용 한도 (USD)</label>
              <input className="form-input" id="run-max-budget" type="number" inputMode="decimal" min="0" max="10000" step="any" placeholder={capabilities.maxBudgetUsd ? '제한 없음' : '미지원'} disabled={!capabilities.maxBudgetUsd} value={maxBudget} onChange={(event) => { setMaxBudget(event.target.value); setErrors((current) => ({ ...current, maxBudget: undefined })) }} aria-invalid={Boolean(errors.maxBudget)} aria-describedby={errors.maxBudget ? 'max-budget-error' : 'max-budget-help'} />
              {errors.maxBudget ? <p className="field-error" id="max-budget-error" role="alert">{errors.maxBudget}</p> : <p className="field-description" id="max-budget-help">{capabilities.maxBudgetUsd ? '요청별 CLI 비용 한도 · 최대 $10,000' : `${providerRuntime.name} 실행기는 USD 한도를 제공하지 않습니다.`}</p>}
            </div>
          </div>
        </fieldset>
        <div className="form-note run-permission-note"><Info size={14} /><p>현재 앱은 실행 중 승인 요청에 응답할 수 없습니다. 추가 승인이 필요한 도구는 실행되지 않을 수 있습니다. 제한을 비우면 CLI 기본 동작을 따릅니다.</p></div>
        {running && <p className="field-description" role="status">실행이 끝난 뒤 설정을 변경할 수 있습니다.</p>}
        <div className="modal-footer"><button className="secondary-button" type="button" onClick={onClose}>취소</button><button className="primary-button" type="submit" disabled={running}><SlidersHorizontal size={14} />설정 저장</button></div>
      </form>
    </Modal>
  )
}
