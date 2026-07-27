import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "./Auth";
import HistoryPeriodFilter, { useHistoryPeriod } from "./HistoryPeriodFilter";
import type { InteractiveChartOption } from "./InteractiveChart";
import {
  operatorCategoryLabel as categoryLabel,
  operatorCommandLabel as commandLabel,
  operatorEvidenceKindLabel as evidenceKindLabel,
  operatorVariableLabel as fieldLabel,
} from "./OperatorTranslations";
import "./BreakAnalysisScreen.css";
import "./BreakAnalysisEnhancements.css";

const InteractiveChart = lazy(() => import("./InteractiveChart"));

type BreakEvent = {
  id: number; startedAtUtc: string; speedAtStartMpm: number;
  diagnosticSampleCount: number; analysisStatus: string;
  causeCategory: string | null; causeDescription: string | null;
  analysisNotes: string | null; analyzedBy: string | null;
};
type Sample = {
  capturedAtUtc: string; offsetMilliseconds: number;
  status: Record<string, unknown>; quality: string;
};
type Summary = {
  fieldName: string; category: string; unit: string; sampleCount: number;
  minimum: number | null; maximum: number | null; average: number | null;
  standardDeviation: number | null; valueAtBreak: number | null;
  baselineAverage: number | null; criticalAverage: number | null;
  delta: number | null; anomalyScore: number | null;
};
type Evidence = {
  id: number; kind: string; name: string;
  previousValueJson: string | null; currentValueJson: string | null;
  observedAtUtc: string; offsetMilliseconds: number;
  description: string | null; severity: string | null;
};
type Diagnostic = {
  event: BreakEvent; samples: Sample[]; summary: Summary[]; evidence: Evidence[];
};
type HistoryPage<T> = {
  items: T[]; total: number; offset: number; limit: number; hasMore: boolean;
};
type BreakAnalysisFilter = {
  id: number; userId: number; name: string; variables: string[];
  isDefault: boolean; revision: number;
  createdAtUtc: string; updatedAtUtc: string;
};

const dateTime = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short", timeStyle: "medium",
});
const colors = ["#68a7ff", "#f2ae63", "#63d5ad", "#e7789d", "#b28cff", "#e2cf60", "#6fd0df", "#ff806f"];
const priorityFields = [
  "dryingSectionGroup3UpperMasterSpeedMPM", "headBoxMMH2O",
  "headboxLipsPosition_mm", "stockPumpSpeed", "mixPumpSpeed",
];

async function fetchJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    cache: "no-store",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    ...init,
  });
  if (response.status === 401) {
    window.location.reload();
    throw new Error("Sessão expirada.");
  }
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(body?.error ?? `Consulta falhou (${response.status}).`);
  }
  return response.status === 204 ? (undefined as T) : response.json() as Promise<T>;
}

function formatNumber(value: number | null, digits = 2) {
  return value === null ? "—" : value.toLocaleString("pt-BR", {
    minimumFractionDigits: digits, maximumFractionDigits: digits,
  });
}
function formatStoredValue(value: string | null) {
  if (value === null) return "—";
  try {
    const parsed = JSON.parse(value) as unknown;
    if (parsed === true) return "Ligado";
    if (parsed === false) return "Desligado";
    if (parsed === null) return "—";
    if (typeof parsed === "object") return JSON.stringify(parsed);
    return String(parsed);
  } catch {
    return value;
  }
}
function normalizeSearch(value: string) {
  return value.normalize("NFD").replace(/[\u0300-\u036f]/g, "").toLocaleLowerCase("pt-BR");
}
function defaultVariables(summary: Summary[]) {
  const available = new Set(summary.map((item) => item.fieldName));
  const selected = priorityFields.filter((field) => available.has(field));
  for (const item of summary) {
    if (selected.length >= 6) break;
    if (!selected.includes(item.fieldName) && (item.anomalyScore ?? 0) > 0)
      selected.push(item.fieldName);
  }
  return selected.length ? selected : summary.slice(0, 6).map((item) => item.fieldName);
}
function sortFilters(filters: BreakAnalysisFilter[]) {
  return [...filters].sort((left, right) =>
    Number(right.isDefault) - Number(left.isDefault) ||
    left.name.localeCompare(right.name, "pt-BR"));
}

export default function BreakAnalysisScreen() {
  const { user } = useAuth();
  const period = useHistoryPeriod("7d", 366);
  const [search, setSearch] = useState("");
  const [appliedSearch, setAppliedSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState("Todos");
  const [appliedStatusFilter, setAppliedStatusFilter] = useState("Todos");
  const [causeFilter, setCauseFilter] = useState("Todas");
  const [appliedCauseFilter, setAppliedCauseFilter] = useState("Todas");
  const [events, setEvents] = useState<BreakEvent[]>([]);
  const [totalEvents, setTotalEvents] = useState(0);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [diagnostic, setDiagnostic] = useState<Diagnostic | null>(null);
  const [selectedVariables, setSelectedVariables] = useState<string[]>([]);
  const [filters, setFilters] = useState<BreakAnalysisFilter[]>([]);
  const [filtersLoaded, setFiltersLoaded] = useState(false);
  const [filtersAvailable, setFiltersAvailable] = useState(true);
  const [activeFilterId, setActiveFilterId] = useState<number | null>(null);
  const [filterName, setFilterName] = useState("");
  const [selectionDirty, setSelectionDirty] = useState(false);
  const [unavailableVariableCount, setUnavailableVariableCount] = useState(0);
  const [selectionInitializedFor, setSelectionInitializedFor] = useState<number | null>(null);
  const [filterNotice, setFilterNotice] = useState("");
  const [filterMutating, setFilterMutating] = useState(false);
  const [category, setCategory] = useState("Todas");
  const [variableSearch, setVariableSearch] = useState("");
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [analysisStatus, setAnalysisStatus] = useState("Pendente");
  const [causeCategory, setCauseCategory] = useState("");
  const [causeDescription, setCauseDescription] = useState("");
  const [analysisNotes, setAnalysisNotes] = useState("");

  const loadEvents = useCallback(async (offset = 0, append = false) => {
    if (append) setLoadingMore(true);
    else setLoading(true);
    setError(null);
    try {
      const query = new URLSearchParams({
        fromUtc: new Date(period.applied.from).toISOString(),
        toUtc: new Date(period.applied.to).toISOString(),
        offset: String(offset),
        limit: "100",
      });
      if (appliedSearch) query.set("search", appliedSearch);
      if (appliedStatusFilter !== "Todos")
        query.set("analysisStatus", appliedStatusFilter);
      if (appliedCauseFilter !== "Todas")
        query.set("causeCategory", appliedCauseFilter);
      const page = await fetchJson<HistoryPage<BreakEvent>>(
        `/api/history/breaks/search?${query}`,
      );
      setTotalEvents(page.total);
      if (append) {
        setEvents((current) => [...current, ...page.items]);
      } else {
        setEvents(page.items);
        setSelectedId((current) =>
          current !== null && page.items.some((item) => item.id === current)
            ? current : page.items[0]?.id ?? null);
      }
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Não foi possível consultar as quebras.");
    } finally {
      if (append) setLoadingMore(false);
      else setLoading(false);
    }
  }, [
    appliedCauseFilter,
    appliedSearch,
    appliedStatusFilter,
    period.applied.from,
    period.applied.to,
    period.revision,
  ]);

  useEffect(() => { void loadEvents(0, false); }, [loadEvents]);
  useEffect(() => {
    setFiltersLoaded(false);
    setFiltersAvailable(true);
    setFilterNotice("");
    fetchJson<BreakAnalysisFilter[]>("/api/me/break-analysis-filters")
      .then((result) => setFilters(sortFilters(result)))
      .catch(() => {
        setFilters([]);
        setFiltersAvailable(false);
        setFilterNotice(
          "Filtros personalizados indisponíveis. A seleção automática foi aplicada e as alterações estão desabilitadas.",
        );
      })
      .finally(() => setFiltersLoaded(true));
  }, [user.id]);
  useEffect(() => {
    if (selectedId === null) {
      setDiagnostic(null);
      return;
    }
    setLoading(true);
    fetchJson<Diagnostic>(`/api/history/breaks/${selectedId}/diagnostic`)
      .then((result) => {
        setDiagnostic(result);
        setSelectionInitializedFor(null);
        setCategory("Todas");
        setVariableSearch("");
        setAnalysisStatus(result.event.analysisStatus);
        setCauseCategory(result.event.causeCategory ?? "");
        setCauseDescription(result.event.causeDescription ?? "");
        setAnalysisNotes(result.event.analysisNotes ?? "");
      })
      .catch((reason) => setError(reason instanceof Error ? reason.message : "Falha ao abrir diagnóstico."))
      .finally(() => setLoading(false));
  }, [selectedId]);
  useEffect(() => {
    if (
      !diagnostic ||
      !filtersLoaded ||
      selectionInitializedFor === diagnostic.event.id
    ) return;

    const defaultFilter = filters.find((filter) => filter.isDefault);
    if (defaultFilter) {
      const available = new Set(diagnostic.summary.map((item) => item.fieldName));
      setSelectedVariables(defaultFilter.variables.filter((field) => available.has(field)));
      setUnavailableVariableCount(
        defaultFilter.variables.filter((field) => !available.has(field)).length,
      );
      setActiveFilterId(defaultFilter.id);
      setFilterName(defaultFilter.name);
    } else {
      setSelectedVariables(defaultVariables(diagnostic.summary));
      setUnavailableVariableCount(0);
      setActiveFilterId(null);
      setFilterName("");
    }
    setSelectionDirty(false);
    setSelectionInitializedFor(diagnostic.event.id);
  }, [diagnostic, filters, filtersLoaded, selectionInitializedFor]);

  const causeOptions = ["Todas", ...new Set([
    ...(causeFilter === "Todas" ? [] : [causeFilter]),
    ...events
      .map((event) => event.causeCategory)
      .filter((value): value is string => Boolean(value)),
  ])];
  const categories = useMemo(
    () => ["Todas", ...new Set(diagnostic?.summary.map((item) => item.category) ?? [])],
    [diagnostic],
  );
  const visibleSummary = useMemo(() => {
    const search = normalizeSearch(variableSearch.trim());
    return diagnostic?.summary.filter((item) =>
      (category === "Todas" || item.category === category) &&
      (!search || normalizeSearch([
        fieldLabel(item.fieldName), item.fieldName, categoryLabel(item.category), item.unit,
      ].join(" ")).includes(search))) ?? [];
  }, [category, diagnostic, variableSearch]);
  const summaryByField = useMemo(
    () => new Map(diagnostic?.summary.map((item) => [item.fieldName, item]) ?? []),
    [diagnostic],
  );
  const activeFilter = useMemo(
    () => filters.find((filter) => filter.id === activeFilterId) ?? null,
    [activeFilterId, filters],
  );
  const commandsBeforeBreak = useMemo(
    () => (diagnostic?.evidence ?? [])
      .filter((item) =>
        normalizeSearch(item.kind) === "comando" &&
        item.offsetMilliseconds <= 0)
      .sort((left, right) => right.offsetMilliseconds - left.offsetMilliseconds),
    [diagnostic],
  );

  const chartOption = useMemo<InteractiveChartOption>(() => {
    const selectedUnits = [...new Set(selectedVariables.map(
      (field) => summaryByField.get(field)?.unit ?? "unidade PLC",
    ))];
    const units = selectedUnits.length > 0 ? selectedUnits : ["unidade PLC"];
    return {
      backgroundColor: "transparent", animation: false, color: colors,
      tooltip: { trigger: "axis" },
      legend: { type: "scroll", top: 0, textStyle: { color: "#aebed1", fontSize: 10 } },
      grid: { left: 64, right: 24 + Math.max(0, units.length - 1) * 48, top: 58, bottom: 68 },
      xAxis: {
        type: "value", min: -180, max: 0, name: "segundos antes da quebra",
        nameLocation: "middle", nameGap: 38,
        axisLabel: { formatter: (value: number) => value === 0 ? "T0" : `${value}s` },
      },
      yAxis: units.map((unit, index) => ({
        type: "value", scale: true, name: unit,
        position: index === 0 ? "left" : "right",
        offset: index <= 1 ? 0 : (index - 1) * 48,
        axisLine: { show: true, lineStyle: { color: colors[index % colors.length] } },
        axisLabel: { fontSize: 9 },
      })),
      dataZoom: [
        { type: "inside", xAxisIndex: 0 },
        { type: "slider", xAxisIndex: 0, bottom: 10, height: 18 },
      ],
      series: selectedVariables.map((field, index) => {
        const unit = summaryByField.get(field)?.unit ?? "unidade PLC";
        return {
          name: `${fieldLabel(field)} (${unit})`,
          type: "line", showSymbol: false, connectNulls: false,
          yAxisIndex: Math.max(0, units.indexOf(unit)),
          lineStyle: { width: 2 }, itemStyle: { color: colors[index % colors.length] },
          data: diagnostic?.samples.map((sample) => {
            const value = sample.status[field];
            return [sample.offsetMilliseconds / 1_000,
              typeof value === "number" && Number.isFinite(value) ? value : null];
          }) ?? [],
          markLine: index === 0 ? {
            silent: true, symbol: "none",
            label: { formatter: "Quebra T0", color: "#ffb39f" },
            lineStyle: { color: "#f06e5d", width: 2 }, data: [{ xAxis: 0 }],
          } : undefined,
        };
      }),
    };
  }, [diagnostic, selectedVariables, summaryByField]);

  const applyFilter = (filter: BreakAnalysisFilter) => {
    if (!diagnostic) return;
    const available = new Set(diagnostic.summary.map((item) => item.fieldName));
    setSelectedVariables(filter.variables.filter((field) => available.has(field)));
    setUnavailableVariableCount(
      filter.variables.filter((field) => !available.has(field)).length,
    );
    setActiveFilterId(filter.id);
    setFilterName(filter.name);
    setSelectionDirty(false);
    setFilterNotice("");
  };

  const restoreAutomaticSelection = () => {
    if (!diagnostic) return;
    setSelectedVariables(defaultVariables(diagnostic.summary));
    setUnavailableVariableCount(0);
    setActiveFilterId(null);
    setFilterName("");
    setSelectionDirty(false);
    if (filtersAvailable) setFilterNotice("");
  };

  const toggleVariable = (field: string) => {
    if (!selectedVariables.includes(field) && selectedVariables.length >= 32) {
      setFilterNotice("Cada filtro pode conter no máximo 32 variáveis.");
      return;
    }
    setSelectedVariables((current) =>
      current.includes(field)
        ? current.filter((item) => item !== field)
        : [...current, field]);
    setSelectionDirty(true);
  };

  const replaceFilter = (updated: BreakAnalysisFilter) => {
    setFilters((current) => sortFilters([
      ...current
        .filter((filter) => filter.id !== updated.id)
        .map((filter) =>
          updated.isDefault && filter.isDefault
            ? {
                ...filter,
                isDefault: false,
                revision: filter.revision + 1,
                updatedAtUtc: updated.updatedAtUtc,
              }
            : filter),
      updated,
    ]));
  };

  const variablesForUpdate = (filter: BreakAnalysisFilter) => {
    if (!diagnostic) return selectedVariables;
    const available = new Set(diagnostic.summary.map((item) => item.fieldName));
    const remainingSelected = new Set(selectedVariables);
    const merged: string[] = [];
    for (const variable of filter.variables) {
      if (!available.has(variable) || remainingSelected.delete(variable))
        merged.push(variable);
    }
    for (const variable of selectedVariables) {
      if (remainingSelected.delete(variable))
        merged.push(variable);
    }
    return merged;
  };

  const saveNewFilter = async () => {
    if (!filtersAvailable || !filterName.trim() || selectedVariables.length === 0) return;
    setFilterMutating(true);
    setFilterNotice("");
    try {
      const created = await fetchJson<BreakAnalysisFilter>(
        "/api/me/break-analysis-filters",
        {
          method: "POST",
          body: JSON.stringify({
            name: filterName.trim(),
            variables: selectedVariables,
            isDefault: false,
          }),
        },
      );
      replaceFilter(created);
      applyFilter(created);
      setFilterNotice("Filtro salvo.");
    } catch (reason) {
      setFilterNotice(
        reason instanceof Error ? reason.message : "Não foi possível salvar o filtro.",
      );
    } finally {
      setFilterMutating(false);
    }
  };

  const updateActiveFilter = async () => {
    if (
      !filtersAvailable ||
      !activeFilter ||
      !filterName.trim() ||
      (selectedVariables.length === 0 && unavailableVariableCount === 0)
    ) return;
    setFilterMutating(true);
    setFilterNotice("");
    try {
      const updated = await fetchJson<BreakAnalysisFilter>(
        `/api/me/break-analysis-filters/${activeFilter.id}`,
        {
          method: "PUT",
          body: JSON.stringify({
            name: filterName.trim(),
            variables: variablesForUpdate(activeFilter),
            isDefault: activeFilter.isDefault,
            revision: activeFilter.revision,
          }),
        },
      );
      replaceFilter(updated);
      applyFilter(updated);
      setFilterNotice("Filtro atualizado.");
    } catch (reason) {
      setFilterNotice(
        reason instanceof Error ? reason.message : "Não foi possível atualizar o filtro.",
      );
    } finally {
      setFilterMutating(false);
    }
  };

  const makeActiveFilterDefault = async () => {
    if (!filtersAvailable || !activeFilter || activeFilter.isDefault) return;
    setFilterMutating(true);
    setFilterNotice("");
    try {
      const updated = await fetchJson<BreakAnalysisFilter>(
        `/api/me/break-analysis-filters/${activeFilter.id}`,
        {
          method: "PUT",
          body: JSON.stringify({
            name: activeFilter.name,
            variables: activeFilter.variables,
            isDefault: true,
            revision: activeFilter.revision,
          }),
        },
      );
      replaceFilter(updated);
      setActiveFilterId(updated.id);
      setFilterNotice("Filtro definido como padrão.");
    } catch (reason) {
      setFilterNotice(
        reason instanceof Error ? reason.message : "Não foi possível definir o filtro padrão.",
      );
    } finally {
      setFilterMutating(false);
    }
  };

  const deleteActiveFilter = async () => {
    if (
      !filtersAvailable ||
      !activeFilter ||
      !window.confirm(`Excluir o filtro "${activeFilter.name}"?`)
    ) return;
    setFilterMutating(true);
    setFilterNotice("");
    try {
      await fetchJson<void>(
        `/api/me/break-analysis-filters/${activeFilter.id}?revision=${activeFilter.revision}`,
        { method: "DELETE" },
      );
      setFilters((current) => current.filter((filter) => filter.id !== activeFilter.id));
      restoreAutomaticSelection();
      setFilterNotice("Filtro excluído.");
    } catch (reason) {
      setFilterNotice(
        reason instanceof Error ? reason.message : "Não foi possível excluir o filtro.",
      );
    } finally {
      setFilterMutating(false);
    }
  };

  const saveAnalysis = async () => {
    if (!diagnostic) return;
    setSaving(true);
    setError(null);
    try {
      await fetchJson<void>(`/api/history/breaks/${diagnostic.event.id}/analysis`, {
        method: "PUT",
        body: JSON.stringify({ analysisStatus, causeCategory, causeDescription, analysisNotes }),
      });
      await loadEvents();
      setDiagnostic(await fetchJson<Diagnostic>(
        `/api/history/breaks/${diagnostic.event.id}/diagnostic`,
      ));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Não foi possível salvar a análise.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <section className="break-analysis">
      <div className="break-heading">
        <div><p>DIAGNÓSTICO DE PROCESSO</p><h1>Análise de quebras</h1>
          <span>Janela congelada de T-180 s até T0, sem amostras posteriores ao evento.</span></div>
      </div>
      <HistoryPeriodFilter
        period={period}
        loading={loading}
        maximumRangeLabel="Período máximo: 366 dias"
        presets={["today", "8h", "24h", "yesterday", "7d"]}
        search={search}
        searchPlaceholder="ID, causa, descrição, notas ou responsável…"
        onSearch={setSearch}
        onCommit={() => {
          setAppliedSearch(search.trim());
          setAppliedStatusFilter(statusFilter);
          setAppliedCauseFilter(causeFilter);
        }}
        resultSummary={`${events.length.toLocaleString("pt-BR")} de ${totalEvents.toLocaleString("pt-BR")} carregadas`}
      >
        <label>
          <span>Status</span>
          <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)}>
            <option>Todos</option>
            <option>Pendente</option>
            <option>Em análise</option>
            <option>Concluída</option>
          </select>
        </label>
        <label>
          <span>Causa</span>
          <input
            list="break-cause-options"
            value={causeFilter}
            maxLength={100}
            onChange={(event) => setCauseFilter(event.target.value)}
            placeholder="Todas"
          />
          <datalist id="break-cause-options">
            {causeOptions.map((item) => <option value={item} key={item} />)}
          </datalist>
        </label>
      </HistoryPeriodFilter>
      {error && <div className="break-error">{error}</div>}
      <div className="break-layout">
        <aside className="break-event-list">
          <header><b>Quebras encontradas</b><span>{totalEvents}</span></header>
          {events.map((event, index) => (
            <button type="button" key={event.id}
              className={event.id === selectedId ? "active" : ""}
              onClick={() => setSelectedId(event.id)}>
              <div><b>#{index + 1}</b><time>{dateTime.format(new Date(event.startedAtUtc))}</time></div>
              <span>{event.speedAtStartMpm.toFixed(1)} m/min</span>
              <small>{event.diagnosticSampleCount} amostras · {event.analysisStatus}</small>
              {event.causeCategory && <em>{event.causeCategory}</em>}
            </button>
          ))}
          {!loading && events.length === 0 && <p>Nenhuma quebra no período.</p>}
          {events.length < totalEvents && (
            <button
              type="button"
              className="break-load-more"
              onClick={() => void loadEvents(events.length, true)}
              disabled={loadingMore}
            >
              {loadingMore ? "Carregando…" : `Carregar mais (${events.length}/${totalEvents})`}
            </button>
          )}
        </aside>
        <div className="break-workspace">
          {!diagnostic ? <div className="break-empty">Selecione uma quebra com diagnóstico.</div> : <>
            <div className="break-overview">
              <article><span>Instante</span><b>{dateTime.format(new Date(diagnostic.event.startedAtUtc))}</b></article>
              <article><span>Velocidade T0</span><b>{diagnostic.event.speedAtStartMpm.toFixed(1)} <small>m/min</small></b></article>
              <article><span>Amostras</span><b>{diagnostic.samples.length}</b></article>
              <article><span>Evidências</span><b>{diagnostic.evidence.length}</b></article>
              <article><span>Análise</span><b>{diagnostic.event.analysisStatus}</b></article>
            </div>
            <section className="break-chart-card">
              <header><div><h2>Variáveis antes da quebra</h2><span>Selecione os sinais que deseja comparar nos 3 minutos anteriores.</span></div>
                <div className="break-chart-actions">
                  <div className="break-selection-count" aria-live="polite">
                    <b>{selectedVariables.length}</b>
                    <span>
                      {selectedVariables.length === 1 ? "selecionada" : "selecionadas"}
                      {selectionDirty && " · não salvo"}
                    </span>
                  </div>
                  <button type="button" className="break-clear-selection"
                    onClick={() => {
                      setSelectedVariables([]);
                      setSelectionDirty(true);
                    }}
                    disabled={selectedVariables.length === 0}>
                    Limpar seleção
                  </button>
                  <label>Grupo<select value={category} onChange={(event) => setCategory(event.target.value)}>
                    {categories.map((item) => <option key={item} value={item}>{categoryLabel(item)}</option>)}
                  </select></label>
                </div></header>
              <div className="break-filter-manager">
                <label>
                  <span>Meus filtros</span>
                  <select
                    value={activeFilterId === null ? "automatic" : String(activeFilterId)}
                    onChange={(event) => {
                      if (event.target.value === "automatic") {
                        restoreAutomaticSelection();
                        return;
                      }
                      const selected = filters.find(
                        (filter) => filter.id === Number(event.target.value),
                      );
                      if (selected) applyFilter(selected);
                    }}
                    disabled={!filtersLoaded}
                  >
                    <option value="automatic">Seleção automática</option>
                    {filters.map((filter) => (
                      <option value={filter.id} key={filter.id}>
                        {filter.name}{filter.isDefault ? " · padrão" : ""}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="break-filter-name">
                  <span>Nome do filtro</span>
                  <input
                    value={filterName}
                    maxLength={80}
                    placeholder="Ex.: Velocidade G1/G2/G3"
                    onChange={(event) => {
                      setFilterName(event.target.value);
                      if (activeFilter) setSelectionDirty(true);
                    }}
                    disabled={!filtersAvailable || filterMutating}
                  />
                </label>
                <div className="break-filter-actions">
                  <button
                    type="button"
                    onClick={() => void saveNewFilter()}
                    disabled={
                      !filtersAvailable ||
                      filterMutating ||
                      !filterName.trim() ||
                      selectedVariables.length === 0 ||
                      filters.length >= 25
                    }
                  >
                    Salvar como novo
                  </button>
                  <button
                    type="button"
                    onClick={() => void updateActiveFilter()}
                    disabled={
                      !filtersAvailable ||
                      filterMutating ||
                      !activeFilter ||
                      !filterName.trim() ||
                      (selectedVariables.length === 0 && unavailableVariableCount === 0)
                    }
                  >
                    Atualizar
                  </button>
                  <button
                    type="button"
                    onClick={() => void makeActiveFilterDefault()}
                    disabled={
                      !filtersAvailable ||
                      filterMutating ||
                      !activeFilter ||
                      activeFilter.isDefault
                    }
                  >
                    Definir padrão
                  </button>
                  <button
                    type="button"
                    className="danger"
                    onClick={() => void deleteActiveFilter()}
                    disabled={!filtersAvailable || filterMutating || !activeFilter}
                  >
                    Excluir
                  </button>
                  <button
                    type="button"
                    onClick={restoreAutomaticSelection}
                    disabled={!diagnostic || filterMutating}
                  >
                    Restaurar automática
                  </button>
                </div>
                {(filterNotice || unavailableVariableCount > 0) && (
                  <div
                    className={`break-filter-notice ${
                      !filtersAvailable || unavailableVariableCount > 0 ? "warning" : ""
                    }`}
                    role="status"
                  >
                    {filterNotice && <span>{filterNotice}</span>}
                    {unavailableVariableCount > 0 && (
                      <span>
                        {unavailableVariableCount} variável
                        {unavailableVariableCount === 1 ? "" : "is"} deste filtro não
                        {unavailableVariableCount === 1 ? " está" : " estão"} disponível
                        {unavailableVariableCount === 1 ? "" : "is"} nesta quebra.
                      </span>
                    )}
                  </div>
                )}
              </div>
              <div className="break-chart-layout">
                <div className="break-variable-sidebar">
                  <div className="break-variable-search">
                    <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="6.5" /><path d="m16 16 4 4" /></svg>
                    <input type="search" value={variableSearch}
                      onChange={(event) => setVariableSearch(event.target.value)}
                      placeholder="Buscar variável…" aria-label="Buscar variável" />
                    {variableSearch && <button type="button" onClick={() => setVariableSearch("")}
                      aria-label="Limpar busca">×</button>}
                  </div>
                  <div className="break-variable-list">{visibleSummary.map((item) => (
                    <label key={item.fieldName}>
                      <input type="checkbox" checked={selectedVariables.includes(item.fieldName)}
                        onChange={() => toggleVariable(item.fieldName)} />
                      <span><b>{fieldLabel(item.fieldName)}</b><small>{item.fieldName}</small></span>
                      <em>{item.unit}</em>
                    </label>
                  ))}
                    {visibleSummary.length === 0 &&
                      <p>Nenhuma variável encontrada para este filtro.</p>}
                  </div>
                </div>
                {diagnostic.samples.length === 0
                  ? <div className="break-empty">Evento anterior à versão de diagnóstico; não há janela de amostras disponível.</div>
                  : selectedVariables.length === 0
                    ? <div className="break-empty break-chart-empty"><b>Gráfico limpo</b>
                      <span>Marque uma ou mais variáveis na lista para iniciar a comparação.</span></div>
                  : <Suspense fallback={<div className="break-empty">Preparando gráfico…</div>}>
                    <InteractiveChart key={diagnostic.event.id} option={chartOption}
                      ariaLabel="Variáveis nos 180 segundos anteriores à quebra" />
                </Suspense>}
              </div>
            </section>
            <section className="break-panel break-command-panel">
              <header>
                <div>
                  <h2>Comandos do operador antes da quebra</h2>
                  <span>
                    Eventos on-change da estrutura de comandos entre T-180 s e
                    T0, ordenados do mais próximo para o mais distante.
                  </span>
                </div>
                <strong>{commandsBeforeBreak.length} comando{commandsBeforeBreak.length === 1 ? "" : "s"}</strong>
              </header>
              <div className="break-command-table">
                <div className="break-command-head">
                  <span>Horário</span>
                  <span>Antes da quebra</span>
                  <span>Comando</span>
                  <span>Valor anterior</span>
                  <span>Novo valor</span>
                  <span>Origem</span>
                </div>
                {commandsBeforeBreak.map((command) => (
                  <div className="break-command-row" key={command.id}>
                    <time>{dateTime.format(new Date(command.observedAtUtc))}</time>
                    <strong>
                      {command.offsetMilliseconds === 0
                        ? "T0"
                        : `T${Math.round(command.offsetMilliseconds / 1000)}s`}
                    </strong>
                    <div>
                      <b>{commandLabel(command.name)}</b>
                      <small>{command.name}</small>
                    </div>
                    <span>{formatStoredValue(command.previousValueJson)}</span>
                    <span className="break-command-new-value">
                      {formatStoredValue(command.currentValueJson)}
                    </span>
                    <em>
                      {command.description === "AdsOnChange"
                        ? "ADS on-change"
                        : command.description ?? "PLC observado"}
                    </em>
                  </div>
                ))}
                {commandsBeforeBreak.length === 0 && (
                  <p>Nenhum comando foi registrado nos três minutos anteriores.</p>
                )}
              </div>
              <footer>
                Estes eventos mostram alterações recebidas pela estrutura de
                comandos; a identificação nominal do operador depende da HMI
                disponibilizar essa informação ao CLP.
              </footer>
            </section>
            <div className="break-details-grid">
              <section className="break-panel"><header><div><h2>Maiores alterações</h2>
                <span>Base T-180/T-60 comparada aos 30 s finais.</span></div></header>
                <div className="break-summary-table">
                  <div className="break-summary-head"><span>Variável</span><span>Base</span><span>Final</span><span>Δ</span><span>Índice</span></div>
                  {diagnostic.summary.slice(0, 12).map((item) => (
                    <div className="break-summary-row" key={item.fieldName}>
                      <span><b>{fieldLabel(item.fieldName)}</b><small>{item.unit}</small></span>
                      <span>{formatNumber(item.baselineAverage)}</span><span>{formatNumber(item.criticalAverage)}</span>
                      <span>{formatNumber(item.delta)}</span><strong>{formatNumber(item.anomalyScore, 1)}</strong>
                    </div>
                  ))}
                </div>
              </section>
              <section className="break-panel"><header><div><h2>Linha do tempo</h2>
                <span>Alarmes, comandos e mudanças discretas.</span></div></header>
                <div className="break-evidence-list">{diagnostic.evidence.map((item) => (
                  <article key={item.id}>
                    <time>{item.offsetMilliseconds === 0 ? "T0" : `T${Math.round(item.offsetMilliseconds / 1000)}s`}</time>
                    <em>{evidenceKindLabel(item.kind)}</em><div><b>{
                      normalizeSearch(item.kind) === "comando"
                        ? commandLabel(item.name)
                        : item.description ?? fieldLabel(item.name)
                    }</b><small>{item.name}</small></div>
                    <span>{formatStoredValue(item.currentValueJson)}</span>
                  </article>
                ))}{diagnostic.evidence.length === 0 && <p>Nenhuma evidência discreta na janela.</p>}</div>
              </section>
            </div>
            <section className="break-analysis-form">
              <header><div><h2>Conclusão do analista</h2>
                <span>O índice aponta mudança estatística; não prova causalidade sozinho.</span></div></header>
              <div><label>Status<select value={analysisStatus} onChange={(event) => setAnalysisStatus(event.target.value)} disabled={user.role !== "Administrator"}>
                <option>Pendente</option><option>Em análise</option><option>Concluída</option>
              </select></label><label>Categoria da causa<input value={causeCategory}
                onChange={(event) => setCauseCategory(event.target.value)} maxLength={100}
                disabled={user.role !== "Administrator"} placeholder="Ex.: Instabilidade do headbox" /></label></div>
              <label>Descrição da causa<textarea value={causeDescription}
                onChange={(event) => setCauseDescription(event.target.value)} maxLength={1000}
                disabled={user.role !== "Administrator"} /></label>
              <label>Observações<textarea value={analysisNotes}
                onChange={(event) => setAnalysisNotes(event.target.value)} maxLength={4000}
                disabled={user.role !== "Administrator"} /></label>
              <footer><span>{diagnostic.event.analyzedBy
                ? `Última análise: ${diagnostic.event.analyzedBy}` : "Ainda sem conclusão registrada."}</span>
                {user.role === "Administrator" && <button type="button" onClick={() => void saveAnalysis()}
                  disabled={saving}>{saving ? "Salvando…" : "Salvar análise"}</button>}</footer>
            </section>
          </>}
        </div>
      </div>
    </section>
  );
}
