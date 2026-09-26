export interface OnboardingStep {
  titleKey: string;
  bodyKey: string;
}

export const CONNECT_STEPS: OnboardingStep[] = [
  { titleKey: 'phone.connect.step1.title', bodyKey: 'phone.connect.step1.body' },
  { titleKey: 'phone.connect.step2.title', bodyKey: 'phone.connect.step2.body' },
  { titleKey: 'phone.connect.step3.title', bodyKey: 'phone.connect.step3.body' },
  { titleKey: 'phone.connect.step4.title', bodyKey: 'phone.connect.step4.body' },
];

export function needsOnboarding(hostCount: number): boolean {
  return hostCount === 0;
}
