import { lazy, Suspense, useEffect, useMemo, useState } from "react";
import { useAuth } from "./Auth";
import type { InteractiveChartOption } from "./InteractiveChart";
import "./BreakAnalysisScreen.css";

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
  id: number; kind: string; name: string; currentValueJson: string | null;
  offsetMilliseconds: number; description: string | null; severity: string | null;
};
type Diagnostic = {
  event: BreakEvent; samples: Sample[]; summary: Summary[]; evidence: Evidence[];
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

function localInputValue(date: Date) {
  const shifted = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return shifted.toISOString().slice(0, 16);
}
function formatNumber(value: number | null, digits = 2) {
  return value === null ? "—" : value.toLocaleString("pt-BR", {
    minimumFractionDigits: digits, maximumFractionDigits: digits,
  });
}
function fieldLabel(field: string) {
  return field
    .replace(/dryingSection/gi, "Secagem ")
    .replace(/formingBoard/gi, "Mesa ")
    .replace(/headBox/gi, "Headbox ")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/_/g, " ").replace(/\s+/g, " ").trim();
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

export default function BreakAnalysisScreen() {
  const { user } = useAuth();
  const initialNow = useMemo(() => new Date(), []);
  const [from, setFrom] = useState(localInputValue(new Date(initialNow.getTime() - 7 * 86_400_000)));
  const [to, setTo] = useState(localInputValue(initialNow));
  const [statusFilter, setStatusFilter] = useState("Todos");
  const [causeFilter, setCauseFilter] = useState("Todas");
  const [events, setEvents] = useState<BreakEvent[]>([]);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [diagnostic, setDiagnostic] = useState<Diagnostic | null>(null);
  const [selectedVariables, setSelectedVariables] = useState<string[]>([]);
  const [category, setCategory] = useState("Todas");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [analysisStatus, setAnalysisStatus] = useState("Pendente");
  const [causeCategory, setCauseCategory] = useState("");
  const [causeDescription, setCauseDescription] = useState("");
  const [analysisNotes, setAnalysisNotes] = useState("");

  const loadEvents = async () => {
    setLoading(true);
    setError(null);
    try {
      const query = new URLSearchParams({
        fromUtc: new Date(from).toISOString(), toUtc: new Date(to).toISOString(), limit: "500",
      });
      const rows = await fetchJson<BreakEvent[]>(`/api/history/breaks?${query}`);
      setEvents(rows);
      setSelectedId((current) =>
        current !== null && rows.some((item) => item.id === current)
          ? current : rows[0]?.id ?? null);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Não foi possível consultar as quebras.");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { void loadEvents(); }, []);
  useEffect(() => {
    if (selectedId === null) {
      setDiagnostic(null);
      return;
    }
    setLoading(true);
    fetchJson<Diagnostic>(`/api/history/breaks/${selectedId}/diagnostic`)
      .then((result) => {
        setDiagnostic(result);
        setSelectedVariables(defaultVariables(result.summary));
        setCategory("Todas");
        setAnalysisStatus(result.event.analysisStatus);
        setCauseCategory(result.event.causeCategory ?? "");
        setCauseDescription(result.event.causeDescription ?? "");
        setAnalysisNotes(result.event.analysisNotes ?? "");
      })
      .catch((reason) => setError(reason instanceof Error ? reason.message : "Falha ao abrir diagnóstico."))
      .finally(() => setLoading(false));
  }, [selectedId]);

  const causeOptions = ["Todas", ...new Set(
    events.map((event) => event.causeCategory).filter((value): value is string => Boolean(value)),
  )];
  const visibleEvents = events.filter((event) =>
    (statusFilter === "Todos" || event.analysisStatus === statusFilter) &&
    (causeFilter === "Todas" || event.causeCategory === causeFilter));
  const categories = useMemo(
    () => ["Todas", ...new Set(diagnostic?.summary.map((item) => item.category) ?? [])],
    [diagnostic],
  );
  const visibleSummary = diagnostic?.summary.filter(
    (item) => category === "Todas" || item.category === category,
  ) ?? [];
  const summaryByField = useMemo(
    () => new Map(diagnostic?.summary.map((item) => [item.fieldName, item]) ?? []),
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

  const toggleVariable = (field: string) => setSelectedVariables((current) =>
    current.includes(field) ? current.filter((item) => item !== field) : [...current, field]);

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
        <div className="break-filters">
          <label>Início<input type="datetime-local" value={from} onChange={(event) => setFrom(event.target.value)} /></label>
          <label>Fim<input type="datetime-local" value={to} onChange={(event) => setTo(event.target.value)} /></label>
          <label>Status<select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)}>
            <option>Todos</option><option>Pendente</option><option>Em análise</option><option>Concluída</option>
          </select></label>
          <label>Causa<select value={causeFilter} onChange={(event) => setCauseFilter(event.target.value)}>
            {causeOptions.map((item) => <option key={item}>{item}</option>)}
          </select></label>
          <button type="button" onClick={() => void loadEvents()} disabled={loading}>Consultar</button>
        </div>
      </div>
      {error && <div className="break-error">{error}</div>}
      <div className="break-layout">
        <aside className="break-event-list">
          <header><b>Quebras encontradas</b><span>{visibleEvents.length}</span></header>
          {visibleEvents.map((event) => (
            <button type="button" key={event.id}
              className={event.id === selectedId ? "active" : ""}
              onClick={() => setSelectedId(event.id)}>
              <div><b>#{event.id}</b><time>{dateTime.format(new Date(event.startedAtUtc))}</time></div>
              <span>{event.speedAtStartMpm.toFixed(1)} m/min</span>
              <small>{event.diagnosticSampleCount} amostras · {event.analysisStatus}</small>
              {event.causeCategory && <em>{event.causeCategory}</em>}
            </button>
          ))}
          {!loading && visibleEvents.length === 0 && <p>Nenhuma quebra no período.</p>}
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
              <header><div><h2>Variáveis antes da quebra</h2><span>Trocar de evento redefine o gráfico.</span></div>
                <label>Grupo<select value={category} onChange={(event) => setCategory(event.target.value)}>
                  {categories.map((item) => <option key={item}>{item}</option>)}
                </select></label></header>
              <div className="break-chart-layout">
                <div className="break-variable-list">{visibleSummary.map((item) => (
                  <label key={item.fieldName}>
                    <input type="checkbox" checked={selectedVariables.includes(item.fieldName)}
                      onChange={() => toggleVariable(item.fieldName)} />
                    <span><b>{fieldLabel(item.fieldName)}</b><small>{item.fieldName}</small></span>
                    <em>{item.unit}</em>
                  </label>
                ))}</div>
                {diagnostic.samples.length === 0
                  ? <div className="break-empty">Evento anterior à versão de diagnóstico; não há janela de amostras disponível.</div>
                  : <Suspense fallback={<div className="break-empty">Preparando gráfico…</div>}>
                    <InteractiveChart key={diagnostic.event.id} option={chartOption}
                      ariaLabel="Variáveis nos 180 segundos anteriores à quebra" />
                  </Suspense>}
              </div>
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
                    <em>{item.kind}</em><div><b>{item.description ?? fieldLabel(item.name)}</b><small>{item.name}</small></div>
                    <span>{item.currentValueJson ?? "—"}</span>
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
