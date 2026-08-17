export type RankingProfile = "balanced" | "stability" | "speed";

export type ParameterCategory =
  | "traction"
  | "stock"
  | "drying"
  | "headbox"
  | "forming";

export type ProductionQuality = {
  id: string;
  productCode: string;
  productName: string;
  grammageGsm: number;
  productionPeriodCount: number;
  firstObservedAtUtc: string;
  lastObservedAtUtc: string;
};

export type ConditionParameterDefinition = {
  key: string;
  label: string;
  category: ParameterCategory;
  unit: string;
  decimals: number;
};

export type ConditionValueRange = {
  minimum: number;
  maximum: number;
};

export type ConditionRun = {
  id: string;
  qualityId: string;
  orderCode: string;
  startedAt: string;
  endedAt: string;
  daysAgo: number;
  durationMinutes: number;
  averageSpeedMpm: number;
  maximumSpeedMpm: number;
  speedVariationMpm: number;
  coveragePct: number;
  values: Record<string, number | null>;
  ranges: Record<string, ConditionValueRange>;
};

export type BestConditionsDataset = {
  generatedAt: string;
  currentQualityId: string | null;
  currentOrderCode: string | null;
  selectedQualityId: string | null;
  currentValues: Record<string, number | null>;
  qualities: ProductionQuality[];
  parameters: ConditionParameterDefinition[];
  runs: ConditionRun[];
  methodology: {
    minimumProductiveSpeedMpm: number;
    minimumRunMinutes: number;
    description: string;
  };
};

export type BestConditionsQuery = {
  periodDays: number;
  productCode?: string;
  grammageGsm?: number;
};

export interface BestConditionsGateway {
  load(query: BestConditionsQuery, signal?: AbortSignal): Promise<BestConditionsDataset>;
}

async function readError(response: Response) {
  try {
    const body = await response.json() as { error?: string };
    return body.error || `Falha HTTP ${response.status}.`;
  } catch {
    return `Falha HTTP ${response.status}.`;
  }
}

export const bestConditionsGateway: BestConditionsGateway = {
  async load(query, signal) {
    const search = new URLSearchParams({ periodDays: String(query.periodDays) });
    if (query.productCode && query.grammageGsm !== undefined) {
      search.set("productCode", query.productCode);
      search.set("grammageGsm", String(query.grammageGsm));
    }
    const response = await fetch(`/api/production/best-conditions?${search}`, {
      credentials: "same-origin",
      signal,
    });
    if (!response.ok) throw new Error(await readError(response));
    return response.json() as Promise<BestConditionsDataset>;
  },
};
