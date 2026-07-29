import {
  lazy,
  Suspense,
  useCallback,
  useEffect,
  useMemo,
  useState,
} from "react";
import HistoryPeriodFilter, { useHistoryPeriod } from "./HistoryPeriodFilter";
import type { InteractiveChartOption } from "./InteractiveChart";
import { operatorCategoryLabel, operatorVariableLabel } from "./OperatorTranslations";
import {
  sortVariableFilters,
  type SharedVariableFilter,
} from "./VariableFilters";
import "./GraphWorkspace.css";

const InteractiveChart = lazy(() => import("./InteractiveChart"));

type ProcessVariable = {
  fieldName: string;
  category: string;
  unit: string;
};

type ProcessTrendPoint = {
  capturedAtUtc: string;
  value: number | null;
};

type ProcessTrendSeries = ProcessVariable & {
  points: ProcessTrendPoint[];
};

type ProcessTrend = {
  series: ProcessTrendSeries[];
};

type GraphPanel = {
  title: string;
  variables: string[];
};

type GraphLayoutResponse = {
  charts: GraphPanel[];
  revision: number;
  updatedAtUtc: string | null;
};

type GraphWorkspaceProps = {
  currentStatus: Record<string, unknown>;
};

const maximumVariablesPerChart = 8;
const colors = [
  "#55a2ff",
  "#e8aa55",
  "#56d4a0",
  "#c48cff",
  "#ff7185",
  "#4fd2e7",
  "#b6cf62",
  "#ef8ed7",
] as const;

const defaultCharts: GraphPanel[] = [
  {
    title: "Velocidade dos grupos",
    variables: [
      "dryingSectionGroup1UpperMasterSpeedMPM",
      "dryingSectionGroup2UpperMasterSpeedMPM",
      "dryingSectionGroup3UpperMasterSpeedMPM",
    ],
  },
  {
    title: "Torque dos grupos",
    variables: [
      "dryingSectionGroup1UpperMasterTorque",
      "dryingSectionGroup2UpperMasterTorque",
      "dryingSectionGroup3UpperMasterTorque",
    ],
  },
  {
    title: "Pressão de vapor",
    variables: [
      "dryingSectionGroup1SteamPressure",
      "dryingSectionGroup2SteamPressure",
      "dryingSectionGroup3SteamPressure",
    ],
  },
];

async function requestJson<T>(url: string, init?: RequestInit): Promise<T> {
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
    const body = await response.json().catch(() => null) as {
      error?: string;
      detail?: string;
    } | null;
    throw new Error(body?.error ?? body?.detail ?? `Consulta falhou (${response.status}).`);
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

function normalizeSearch(value: string) {
  return value
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLocaleLowerCase("pt-BR");
}

function isProcessVariable(fieldName: string, value: unknown) {
  if (typeof value === "boolean") return true;
  if (typeof value !== "number") return false;
  const lower = fieldName.toLocaleLowerCase("en-US");
  return [
    "speed",
    "torque",
    "pressure",
    "mmh2o",
    "position",
    "temperature",
    "level",
    "vacuum",
    "flow",
    "setpoint",
    "ctrloutput",
    "ratio",
    "current",
    "voltage",
    "frequency",
    "diameter",
    "faultcode",
    "eventcounter",
  ].some((part) => lower.includes(part)) || lower.endsWith("state");
}

function describeVariable(fieldName: string, value?: unknown): ProcessVariable {
  const lower = fieldName.toLocaleLowerCase("en-US");
  if (lower.endsWith("state") || lower.includes("faultcode") || lower.includes("eventcounter"))
    return { fieldName, category: "Estado", unit: "código" };
  if (lower.includes("torque"))
    return { fieldName, category: "Torque", unit: "%" };
  if (lower.includes("speed")) {
    const pump = lower.includes("pump");
    return { fieldName, category: pump ? "Bombas" : "Velocidade", unit: pump ? "%" : "m/min" };
  }
  if (lower.includes("mmh2o"))
    return { fieldName, category: "Headbox", unit: "mmH₂O" };
  if (lower.includes("pressure"))
    return { fieldName, category: "Pressão", unit: "bar" };
  if (lower.includes("temperature"))
    return { fieldName, category: "Temperatura", unit: "°C" };
  if (lower.includes("position") || lower.includes("diameter"))
    return { fieldName, category: "Posição", unit: "mm" };
  if (lower.includes("current"))
    return { fieldName, category: "Elétrica", unit: "A" };
  if (lower.includes("voltage"))
    return { fieldName, category: "Elétrica", unit: "V" };
  if (lower.includes("frequency"))
    return { fieldName, category: "Elétrica", unit: "Hz" };
  if (typeof value === "boolean")
    return { fieldName, category: "Estado", unit: "0/1" };
  if (
    lower.includes("level") ||
    lower.includes("setpoint") ||
    lower.includes("ctrloutput") ||
    lower.includes("ratio")
  ) {
    return { fieldName, category: "Processo", unit: "%" };
  }
  return { fieldName, category: "Processo", unit: "unidade PLC" };
}

function cloneDefaultCharts() {
  return defaultCharts.map((chart) => ({ ...chart, variables: [...chart.variables] }));
}

function replaceFilter(
  filters: SharedVariableFilter[],
  updated: SharedVariableFilter,
) {
  return sortVariableFilters([
    ...filters
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
  ]);
}

export default function GraphWorkspace({ currentStatus }: GraphWorkspaceProps) {
  const period = useHistoryPeriod("today", 31);
  const [charts, setCharts] = useState<GraphPanel[]>(cloneDefaultCharts);
  const [layoutRevision, setLayoutRevision] = useState(0);
  const [layoutDirty, setLayoutDirty] = useState(false);
  const [activeChartIndex, setActiveChartIndex] = useState(0);
  const [catalog, setCatalog] = useState<ProcessVariable[]>([]);
  const [filters, setFilters] = useState<SharedVariableFilter[]>([]);
  const [selectedFilterIds, setSelectedFilterIds] = useState<Array<number | null>>(
    [null, null, null],
  );
  const [filterName, setFilterName] = useState("");
  const [category, setCategory] = useState("Todas");
  const [search, setSearch] = useState("");
  const [trend, setTrend] = useState<ProcessTrend>({ series: [] });
  const [loading, setLoading] = useState(false);
  const [initializing, setInitializing] = useState(true);
  const [savingLayout, setSavingLayout] = useState(false);
  const [mutatingFilter, setMutatingFilter] = useState(false);
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    let cancelled = false;
    const fallbackCatalog = Object.entries(currentStatus)
      .filter(([fieldName, value]) => isProcessVariable(fieldName, value))
      .map(([fieldName, value]) => describeVariable(fieldName, value));

    Promise.all([
      requestJson<SharedVariableFilter[]>("/api/me/break-analysis-filters"),
      requestJson<GraphLayoutResponse>("/api/me/graph-layout"),
      requestJson<ProcessVariable[]>("/api/history/process-trends/catalog")
        .catch(() => fallbackCatalog),
    ])
      .then(([loadedFilters, layout, loadedCatalog]) => {
        if (cancelled) return;
        const nextCharts = layout.charts.length === 3
          ? layout.charts.map((chart) => ({
              title: chart.title,
              variables: [...chart.variables].slice(0, maximumVariablesPerChart),
            }))
          : cloneDefaultCharts();
        setFilters(sortVariableFilters(loadedFilters));
        setCharts(nextCharts);
        setSelectedFilterIds(nextCharts.map((chart) => {
          const matching = loadedFilters.find((filter) => {
            const applicableVariables =
              filter.variables.slice(0, maximumVariablesPerChart);
            return applicableVariables.length === chart.variables.length &&
              applicableVariables.every(
                (variable, index) => variable === chart.variables[index],
              );
          });
          return matching?.id ?? null;
        }));
        setLayoutRevision(layout.revision);
        setCatalog(loadedCatalog.length > 0 ? loadedCatalog : fallbackCatalog);
      })
      .catch((reason) => {
        if (!cancelled)
          setError(reason instanceof Error ? reason.message : "Não foi possível abrir os gráficos.");
      })
      .finally(() => {
        if (!cancelled) setInitializing(false);
      });
    return () => { cancelled = true; };
  }, [currentStatus]);

  const variableCatalog = useMemo(() => {
    const byField = new Map<string, ProcessVariable>();
    for (const variable of catalog)
      byField.set(variable.fieldName, variable);
    for (const [fieldName, value] of Object.entries(currentStatus)) {
      if (!byField.has(fieldName) &&
          isProcessVariable(fieldName, value)) {
        byField.set(fieldName, describeVariable(fieldName, value));
      }
    }
    for (const fieldName of charts.flatMap((chart) => chart.variables)) {
      if (!byField.has(fieldName))
        byField.set(fieldName, describeVariable(fieldName));
    }
    for (const fieldName of filters.flatMap((filter) => filter.variables)) {
      if (!byField.has(fieldName))
        byField.set(fieldName, describeVariable(fieldName));
    }
    return [...byField.values()].sort((left, right) =>
      left.category.localeCompare(right.category, "pt-BR") ||
      operatorVariableLabel(left.fieldName).localeCompare(
        operatorVariableLabel(right.fieldName),
        "pt-BR",
      ));
  }, [catalog, charts, currentStatus, filters]);

  const activeChart = charts[activeChartIndex] ?? charts[0];
  const activeFilterId = selectedFilterIds[activeChartIndex] ?? null;
  const activeFilter = filters.find((filter) => filter.id === activeFilterId) ?? null;
  const categories = useMemo(
    () => ["Todas", ...new Set(variableCatalog.map((item) => item.category))],
    [variableCatalog],
  );
  const visibleVariables = useMemo(() => {
    const normalized = normalizeSearch(search.trim());
    return variableCatalog.filter((item) =>
      (category === "Todas" || item.category === category) &&
      (!normalized || normalizeSearch([
        operatorVariableLabel(item.fieldName),
        item.fieldName,
        operatorCategoryLabel(item.category),
        item.unit,
      ].join(" ")).includes(normalized)));
  }, [category, search, variableCatalog]);

  const loadTrend = useCallback(async () => {
    const variables = [...new Set(charts.flatMap((chart) => chart.variables))];
    if (variables.length === 0) {
      setTrend({ series: [] });
      return;
    }

    setLoading(true);
    setError("");
    try {
      const parameters = new URLSearchParams({
        fromUtc: new Date(period.applied.from).toISOString(),
        toUtc: new Date(period.applied.to).toISOString(),
        maxPoints: "1200",
      });
      variables.forEach((variable) => parameters.append("variables", variable));
      setTrend(await requestJson<ProcessTrend>(
        `/api/history/process-trends?${parameters}`,
      ));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Não foi possível consultar as tendências.");
    } finally {
      setLoading(false);
    }
  }, [
    charts,
    period.applied.from,
    period.applied.to,
    period.revision,
  ]);

  useEffect(() => {
    if (initializing) return;
    const timer = window.setTimeout(() => void loadTrend(), 180);
    return () => window.clearTimeout(timer);
  }, [initializing, loadTrend]);

  const updateChart = (update: (chart: GraphPanel) => GraphPanel) => {
    setCharts((current) => current.map((chart, index) =>
      index === activeChartIndex ? update(chart) : chart));
    setLayoutDirty(true);
  };

  const toggleVariable = (fieldName: string) => {
    if (!activeChart) return;
    if (
      !activeChart.variables.includes(fieldName) &&
      activeChart.variables.length >= maximumVariablesPerChart
    ) {
      setNotice(`Cada gráfico pode exibir no máximo ${maximumVariablesPerChart} variáveis.`);
      return;
    }
    updateChart((chart) => ({
      ...chart,
      variables: chart.variables.includes(fieldName)
        ? chart.variables.filter((field) => field !== fieldName)
        : [...chart.variables, fieldName],
    }));
    setNotice("");
  };

  const selectFilter = (filterId: number | null) => {
    setSelectedFilterIds((current) => current.map((value, index) =>
      index === activeChartIndex ? filterId : value));
    const filter = filters.find((item) => item.id === filterId);
    if (!filter) {
      setFilterName("");
      return;
    }
    const variables = filter.variables.slice(0, maximumVariablesPerChart);
    updateChart((chart) => ({ ...chart, variables }));
    setFilterName(filter.name);
    setNotice(
      filter.variables.length > maximumVariablesPerChart
        ? `O filtro possui ${filter.variables.length} variáveis; as primeiras ${maximumVariablesPerChart} foram aplicadas neste gráfico.`
        : `Filtro "${filter.name}" aplicado ao gráfico ${activeChartIndex + 1}.`,
    );
  };

  const saveNewFilter = async () => {
    if (!activeChart || !filterName.trim() || activeChart.variables.length === 0) return;
    setMutatingFilter(true);
    setNotice("");
    try {
      const created = await requestJson<SharedVariableFilter>(
        "/api/me/break-analysis-filters",
        {
          method: "POST",
          body: JSON.stringify({
            name: filterName.trim(),
            variables: activeChart.variables,
            isDefault: false,
          }),
        },
      );
      setFilters((current) => replaceFilter(current, created));
      setSelectedFilterIds((current) => current.map((value, index) =>
        index === activeChartIndex ? created.id : value));
      setNotice("Filtro compartilhado salvo.");
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : "Não foi possível salvar o filtro.");
    } finally {
      setMutatingFilter(false);
    }
  };

  const updateActiveFilter = async (makeDefault = false) => {
    if (!activeFilter || !activeChart || activeChart.variables.length === 0) return;
    setMutatingFilter(true);
    setNotice("");
    try {
      const updated = await requestJson<SharedVariableFilter>(
        `/api/me/break-analysis-filters/${activeFilter.id}`,
        {
          method: "PUT",
          body: JSON.stringify({
            name: filterName.trim() || activeFilter.name,
            variables: activeChart.variables,
            isDefault: makeDefault || activeFilter.isDefault,
            revision: activeFilter.revision,
          }),
        },
      );
      setFilters((current) => replaceFilter(current, updated));
      setFilterName(updated.name);
      setNotice(makeDefault
        ? "Filtro definido como padrão compartilhado."
        : "Filtro compartilhado atualizado.");
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : "Não foi possível atualizar o filtro.");
    } finally {
      setMutatingFilter(false);
    }
  };

  const deleteActiveFilter = async () => {
    if (!activeFilter ||
        !window.confirm(`Excluir o filtro compartilhado "${activeFilter.name}"?`)) return;
    setMutatingFilter(true);
    setNotice("");
    try {
      await requestJson<void>(
        `/api/me/break-analysis-filters/${activeFilter.id}?revision=${activeFilter.revision}`,
        { method: "DELETE" },
      );
      setFilters((current) => current.filter((filter) => filter.id !== activeFilter.id));
      setSelectedFilterIds((current) => current.map((id) =>
        id === activeFilter.id ? null : id));
      setFilterName("");
      setNotice("Filtro compartilhado excluído. Os gráficos mantiveram suas variáveis.");
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : "Não foi possível excluir o filtro.");
    } finally {
      setMutatingFilter(false);
    }
  };

  const saveLayout = async () => {
    if (charts.some((chart) => chart.variables.length === 0)) {
      setNotice("Selecione ao menos uma variável em cada gráfico antes de salvar o layout.");
      return;
    }
    setSavingLayout(true);
    setNotice("");
    try {
      const saved = await requestJson<GraphLayoutResponse>("/api/me/graph-layout", {
        method: "PUT",
        body: JSON.stringify({ charts, revision: layoutRevision }),
      });
      setLayoutRevision(saved.revision);
      setLayoutDirty(false);
      setNotice("Layout dos três gráficos salvo para o seu usuário.");
    } catch (reason) {
      setNotice(reason instanceof Error ? reason.message : "Não foi possível salvar o layout.");
    } finally {
      setSavingLayout(false);
    }
  };

  const restoreDefaults = () => {
    setCharts(cloneDefaultCharts());
    setSelectedFilterIds([null, null, null]);
    setFilterName("");
    setLayoutDirty(true);
    setNotice("Configuração padrão restaurada. Salve o layout para mantê-la.");
  };

  const trendByField = useMemo(
    () => new Map(trend.series.map((series) => [series.fieldName, series])),
    [trend.series],
  );
  const sampleCount = Math.max(0, ...trend.series.map((series) => series.points.length));

  return (
    <section className="graph-workspace">
      <div className="graph-workspace-heading">
        <div>
          <p className="eyebrow">Tendências de processo</p>
          <h1>Correlação em três gráficos</h1>
          <span>
            Combine qualquer variável do processo e reutilize os mesmos filtros da Análise de Quebras.
          </span>
        </div>
        <div className="graph-layout-actions">
          <span className={layoutDirty ? "dirty" : ""}>
            {layoutDirty ? "Layout não salvo" : `Layout salvo · revisão ${layoutRevision}`}
          </span>
          <button type="button" onClick={restoreDefaults} disabled={initializing}>
            Restaurar padrão
          </button>
          <button
            type="button"
            className="primary"
            onClick={() => void saveLayout()}
            disabled={initializing || savingLayout || !layoutDirty}
          >
            {savingLayout ? "Salvando…" : "Salvar layout"}
          </button>
        </div>
      </div>

      <HistoryPeriodFilter
        period={period}
        loading={loading || initializing}
        maximumRangeLabel="Período máximo: 31 dias"
        presets={["today", "1h", "8h", "24h", "yesterday", "7d"]}
        resultSummary={`${sampleCount.toLocaleString("pt-BR")} pontos por série`}
      />

      {error && <div className="error-banner">{error}</div>}
      {notice && <div className="graph-notice" role="status">{notice}</div>}

      <section className="graph-configurator">
        <div className="graph-panel-tabs" role="tablist" aria-label="Gráfico em configuração">
          {charts.map((chart, index) => (
            <button
              type="button"
              role="tab"
              aria-selected={activeChartIndex === index}
              className={activeChartIndex === index ? "active" : ""}
              onClick={() => {
                setActiveChartIndex(index);
                const filter = filters.find((item) => item.id === selectedFilterIds[index]);
                setFilterName(filter?.name ?? "");
                setNotice("");
              }}
              key={index}
            >
              <b>Gráfico {index + 1}</b>
              <span>{chart.title}</span>
              <em>{chart.variables.length} séries</em>
            </button>
          ))}
        </div>

        {activeChart && (
          <div className="graph-config-body">
            <div className="graph-config-fields">
              <label className="graph-title-field">
                <span>Título do gráfico</span>
                <input
                  value={activeChart.title}
                  maxLength={60}
                  onChange={(event) =>
                    updateChart((chart) => ({ ...chart, title: event.target.value }))}
                />
              </label>
              <label>
                <span>Filtro compartilhado</span>
                <select
                  value={activeFilterId ?? ""}
                  onChange={(event) =>
                    selectFilter(event.target.value ? Number(event.target.value) : null)}
                >
                  <option value="">Seleção personalizada</option>
                  {filters.map((filter) => (
                    <option value={filter.id} key={filter.id}>
                      {filter.name}{filter.isDefault ? " · padrão" : ""}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                <span>Nome do filtro</span>
                <input
                  value={filterName}
                  maxLength={80}
                  placeholder="Ex.: Pressão e velocidade"
                  onChange={(event) => setFilterName(event.target.value)}
                />
              </label>
              <label>
                <span>Grupo de variáveis</span>
                <select value={category} onChange={(event) => setCategory(event.target.value)}>
                  {categories.map((item) => (
                    <option value={item} key={item}>{operatorCategoryLabel(item)}</option>
                  ))}
                </select>
              </label>
            </div>

            <div className="graph-filter-actions">
              <span>
                Biblioteca compartilhada com <b>Análise de Quebras</b>
              </span>
              <button
                type="button"
                onClick={() => void saveNewFilter()}
                disabled={
                  mutatingFilter ||
                  !filterName.trim() ||
                  activeChart.variables.length === 0 ||
                  filters.length >= 25
                }
              >
                Salvar como novo
              </button>
              <button
                type="button"
                onClick={() => void updateActiveFilter()}
                disabled={mutatingFilter || !activeFilter || activeChart.variables.length === 0}
              >
                Atualizar filtro
              </button>
              <button
                type="button"
                onClick={() => void updateActiveFilter(true)}
                disabled={mutatingFilter || !activeFilter || activeFilter.isDefault}
              >
                Definir padrão
              </button>
              <button
                type="button"
                className="danger"
                onClick={() => void deleteActiveFilter()}
                disabled={mutatingFilter || !activeFilter}
              >
                Excluir
              </button>
            </div>

            <div className="graph-variable-tools">
              <div className="graph-variable-search">
                <span>Buscar variável</span>
                <input
                  type="search"
                  value={search}
                  placeholder="Velocidade, pressão, torque…"
                  onChange={(event) => setSearch(event.target.value)}
                />
              </div>
              <div className="graph-selection-summary">
                <b>{activeChart.variables.length}/{maximumVariablesPerChart}</b>
                <span>variáveis no gráfico {activeChartIndex + 1}</span>
                <button
                  type="button"
                  onClick={() => updateChart((chart) => ({ ...chart, variables: [] }))}
                  disabled={activeChart.variables.length === 0}
                >
                  Limpar
                </button>
              </div>
            </div>

            <div className="graph-variable-list">
              {visibleVariables.map((variable) => (
                <label key={variable.fieldName}>
                  <input
                    type="checkbox"
                    checked={activeChart.variables.includes(variable.fieldName)}
                    onChange={() => toggleVariable(variable.fieldName)}
                  />
                  <span>
                    <b>{operatorVariableLabel(variable.fieldName)}</b>
                    <small>{variable.fieldName}</small>
                  </span>
                  <em>{variable.unit}</em>
                </label>
              ))}
              {visibleVariables.length === 0 && <p>Nenhuma variável encontrada.</p>}
            </div>
          </div>
        )}
      </section>

      <section className="process-trend-grid">
        {charts.map((chart, index) => (
          <ProcessTrendChart
            chart={chart}
            index={index}
            trendByField={trendByField}
            key={index}
          />
        ))}
      </section>

      <p className="graph-note">
        Os três gráficos compartilham período, cursor e zoom. Séries com unidades diferentes
        recebem eixos independentes. Sinais detalhados seguem a retenção de snapshots; motores,
        presença de papel e vapor também utilizam a telemetria otimizada de longo prazo.
      </p>
    </section>
  );
}

function ProcessTrendChart({
  chart,
  index,
  trendByField,
}: {
  chart: GraphPanel;
  index: number;
  trendByField: ReadonlyMap<string, ProcessTrendSeries>;
}) {
  const selectedSeries = chart.variables.map((fieldName) =>
    trendByField.get(fieldName) ?? {
      ...describeVariable(fieldName),
      points: [],
    });
  const units = [...new Set(selectedSeries.map((series) => series.unit))];
  const option = useMemo<InteractiveChartOption>(() => ({
    backgroundColor: "transparent",
    animation: false,
    color: [...colors],
    legend: {
      type: "scroll",
      top: 0,
      textStyle: { color: "#aebed1", fontSize: 10 },
    },
    tooltip: {
      trigger: "axis",
      axisPointer: { type: "cross" },
    },
    grid: {
      left: 68,
      right: 32 + Math.max(0, units.length - 1) * 52,
      top: 58,
      bottom: 70,
    },
    xAxis: {
      type: "time",
      axisLabel: { hideOverlap: true },
      splitLine: { show: false },
    },
    yAxis: units.map((unit, unitIndex) => ({
      type: "value",
      scale: true,
      name: unit,
      position: unitIndex === 0 ? "left" : "right",
      offset: unitIndex <= 1 ? 0 : (unitIndex - 1) * 52,
      axisLine: {
        show: true,
        lineStyle: { color: colors[unitIndex % colors.length] },
      },
      axisLabel: { fontSize: 9 },
      splitLine: {
        show: unitIndex === 0,
        lineStyle: { color: "#26364d" },
      },
    })),
    dataZoom: [
      { type: "inside", zoomOnMouseWheel: true, moveOnMouseMove: true },
      {
        type: "slider",
        height: 24,
        bottom: 16,
        borderColor: "#34445c",
        fillerColor: "#397fd144",
      },
    ],
    series: selectedSeries.map((series, seriesIndex) => ({
      type: "line",
      name: `${operatorVariableLabel(series.fieldName)} (${series.unit})`,
      showSymbol: false,
      sampling: "lttb",
      connectNulls: false,
      yAxisIndex: Math.max(0, units.indexOf(series.unit)),
      data: series.points
        .filter((point): point is ProcessTrendPoint & { value: number } =>
          typeof point.value === "number")
        .map((point) => [point.capturedAtUtc, point.value]),
      lineStyle: { width: 1.8, color: colors[seriesIndex % colors.length] },
      emphasis: { focus: "series", lineStyle: { width: 3 } },
    })),
  }), [selectedSeries, units]);

  return (
    <article className="process-trend-card">
      <header>
        <div>
          <p className="eyebrow">Gráfico {index + 1} · {chart.variables.length} séries</p>
          <h2>{chart.title || `Gráfico ${index + 1}`}</h2>
        </div>
        <div className="process-unit-list">
          {units.map((unit) => <span key={unit}>{unit}</span>)}
        </div>
      </header>
      {selectedSeries.some((series) => series.points.length > 0) ? (
        <Suspense fallback={<div className="graph-chart-empty">Preparando gráfico…</div>}>
          <InteractiveChart
            option={option}
            ariaLabel={`${chart.title}, gráfico ${index + 1} de tendências de processo`}
            connectGroup="process-trend-workspace"
          />
        </Suspense>
      ) : (
        <div className="graph-chart-empty">
          {chart.variables.length === 0
            ? "Selecione ao menos uma variável."
            : "Sem amostras para as variáveis selecionadas neste período."}
        </div>
      )}
    </article>
  );
}
