import { useMemo, useState, type ReactNode } from "react";

export type HistoryPeriodPreset =
  | "today"
  | "1h"
  | "8h"
  | "24h"
  | "yesterday"
  | "7d";

export type HistoryPeriod = {
  from: string;
  to: string;
};

export type HistoryPeriodController = {
  draft: HistoryPeriod;
  applied: HistoryPeriod;
  activePreset: HistoryPeriodPreset | "custom";
  revision: number;
  error: string;
  setFrom: (value: string) => void;
  setTo: (value: string) => void;
  apply: () => boolean;
  applyPreset: (preset: HistoryPeriodPreset) => void;
};

const presetLabels: Record<HistoryPeriodPreset, string> = {
  today: "Hoje",
  "1h": "Última 1h",
  "8h": "Últimas 8h",
  "24h": "Últimas 24h",
  yesterday: "Ontem",
  "7d": "Últimos 7 dias",
};

export function localDateTimeInput(date: Date) {
  const shifted = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return shifted.toISOString().slice(0, 16);
}

export function historyPeriodForPreset(
  preset: HistoryPeriodPreset,
  now = new Date(),
): HistoryPeriod {
  const startOfToday = new Date(
    now.getFullYear(),
    now.getMonth(),
    now.getDate(),
  );
  let from = startOfToday;
  let to = now;

  if (preset === "1h") from = new Date(now.getTime() - 60 * 60_000);
  if (preset === "8h") from = new Date(now.getTime() - 8 * 60 * 60_000);
  if (preset === "24h") from = new Date(now.getTime() - 24 * 60 * 60_000);
  if (preset === "7d") from = new Date(now.getTime() - 7 * 24 * 60 * 60_000);
  if (preset === "yesterday") {
    to = startOfToday;
    from = new Date(startOfToday.getTime() - 24 * 60 * 60_000);
  }

  return {
    from: localDateTimeInput(from),
    to: localDateTimeInput(to),
  };
}

export function useHistoryPeriod(
  initialPreset: HistoryPeriodPreset,
  maximumRangeDays: number,
): HistoryPeriodController {
  const initial = useMemo(
    () => historyPeriodForPreset(initialPreset),
    [initialPreset],
  );
  const [draft, setDraft] = useState<HistoryPeriod>(initial);
  const [applied, setApplied] = useState<HistoryPeriod>(initial);
  const [activePreset, setActivePreset] = useState<
    HistoryPeriodPreset | "custom"
  >(initialPreset);
  const [revision, setRevision] = useState(0);
  const [error, setError] = useState("");

  const validate = (period: HistoryPeriod) => {
    const from = new Date(period.from);
    const to = new Date(period.to);
    if (
      !period.from ||
      !period.to ||
      Number.isNaN(from.getTime()) ||
      Number.isNaN(to.getTime())
    ) {
      setError("Informe o início e o fim do período.");
      return false;
    }
    if (from >= to) {
      setError("A data final deve ser posterior à data inicial.");
      return false;
    }
    if (to.getTime() - from.getTime() > maximumRangeDays * 24 * 60 * 60_000) {
      setError(`O período máximo para esta consulta é de ${maximumRangeDays} dias.`);
      return false;
    }
    setError("");
    return true;
  };

  return {
    draft,
    applied,
    activePreset,
    revision,
    error,
    setFrom: (from) => {
      setDraft((current) => ({ ...current, from }));
      setActivePreset("custom");
    },
    setTo: (to) => {
      setDraft((current) => ({ ...current, to }));
      setActivePreset("custom");
    },
    apply: () => {
      if (!validate(draft)) return false;
      setApplied({ ...draft });
      setRevision((current) => current + 1);
      return true;
    },
    applyPreset: (preset) => {
      const period = historyPeriodForPreset(preset);
      setDraft(period);
      setApplied(period);
      setActivePreset(preset);
      setError("");
      setRevision((current) => current + 1);
    },
  };
}

export default function HistoryPeriodFilter({
  period,
  loading,
  maximumRangeLabel,
  presets = ["today", "8h", "24h", "yesterday"],
  search,
  searchPlaceholder = "Buscar no período…",
  onSearch,
  onCommit,
  resultSummary,
  children,
}: {
  period: HistoryPeriodController;
  loading: boolean;
  maximumRangeLabel: string;
  presets?: HistoryPeriodPreset[];
  search?: string;
  searchPlaceholder?: string;
  onSearch?: (value: string) => void;
  onCommit?: () => void;
  resultSummary?: string;
  children?: ReactNode;
}) {
  const commit = () => {
    if (period.apply()) onCommit?.();
  };
  const applyPreset = (preset: HistoryPeriodPreset) => {
    period.applyPreset(preset);
    onCommit?.();
  };

  return (
    <section className="history-filter-card">
      <div className="history-filter-heading">
        <div>
          <span>Período da consulta</span>
          <small>Datas, busca e filtros são aplicados juntos.</small>
        </div>
        <div>
          {resultSummary && <strong>{resultSummary}</strong>}
          <small>{maximumRangeLabel}</small>
        </div>
      </div>

      <div className="history-period-presets" aria-label="Períodos rápidos">
        {presets.map((preset) => (
          <button
            type="button"
            className={period.activePreset === preset ? "active" : ""}
            onClick={() => applyPreset(preset)}
            disabled={loading}
            key={preset}
          >
            {presetLabels[preset]}
          </button>
        ))}
      </div>

      <div className="history-filter-fields">
        <label>
          <span>De</span>
          <input
            type="datetime-local"
            value={period.draft.from}
            onChange={(event) => period.setFrom(event.target.value)}
          />
        </label>
        <span className="history-filter-separator">até</span>
        <label>
          <span>Até</span>
          <input
            type="datetime-local"
            value={period.draft.to}
            onChange={(event) => period.setTo(event.target.value)}
          />
        </label>
        {onSearch && (
          <label className="history-search-field">
            <span>Buscar</span>
            <div>
              <input
                value={search ?? ""}
                maxLength={120}
                onChange={(event) => onSearch(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === "Enter") commit();
                }}
                placeholder={searchPlaceholder}
              />
              {search && (
                <button
                  type="button"
                  onClick={() => onSearch("")}
                  aria-label="Limpar busca"
                >
                  ×
                </button>
              )}
            </div>
          </label>
        )}
        {children}
        <button
          className="history-apply-button"
          type="button"
          onClick={commit}
          disabled={loading}
        >
          {loading ? "Consultando…" : "Aplicar filtros"}
        </button>
      </div>
      {period.error && <div className="history-filter-error">{period.error}</div>}
    </section>
  );
}
