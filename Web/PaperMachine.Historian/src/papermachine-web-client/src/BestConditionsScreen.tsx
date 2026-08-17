import { useEffect, useMemo, useState } from "react";
import {
  bestConditionsGateway,
  type BestConditionsDataset,
  type ConditionRun,
  type ParameterCategory,
  type RankingProfile,
} from "./BestConditionsData";
import "./BestConditionsScreen.css";

type RankedRun = ConditionRun & { score: number };

const categoryOptions: { id: "all" | ParameterCategory; label: string }[] = [
  { id: "all", label: "Todos" },
  { id: "stock", label: "Massa" },
  { id: "headbox", label: "Caixa de entrada" },
  { id: "forming", label: "Formação" },
  { id: "traction", label: "Passes" },
  { id: "drying", label: "Secagem" },
];

const profileLabels: Record<RankingProfile, { label: string; help: string }> = {
  balanced: { label: "Equilíbrio", help: "Combina velocidade, estabilidade e tempo sem quebra." },
  stability: { label: "Estabilidade", help: "Prioriza campanhas longas e com pouca oscilação." },
  speed: { label: "Velocidade", help: "Prioriza a maior velocidade média sustentável." },
};

function normalize(value: number, minimum: number, maximum: number) {
  return maximum === minimum ? 1 : (value - minimum) / (maximum - minimum);
}

function rankRuns(runs: ConditionRun[], profile: RankingProfile): RankedRun[] {
  if (runs.length === 0) return [];
  const durations = runs.map((run) => run.durationMinutes);
  const speeds = runs.map((run) => run.averageSpeedMpm);
  const variations = runs.map((run) => run.speedVariationMpm);
  const minDuration = Math.min(...durations);
  const maxDuration = Math.max(...durations);
  const minSpeed = Math.min(...speeds);
  const maxSpeed = Math.max(...speeds);
  const minVariation = Math.min(...variations);
  const maxVariation = Math.max(...variations);
  const weights = profile === "stability"
    ? { duration: .58, speed: .12, stability: .30 }
    : profile === "speed"
      ? { duration: .22, speed: .65, stability: .13 }
      : { duration: .42, speed: .38, stability: .20 };

  return runs
    .map((run) => {
      const duration = normalize(run.durationMinutes, minDuration, maxDuration);
      const speed = normalize(run.averageSpeedMpm, minSpeed, maxSpeed);
      const stability = 1 - normalize(run.speedVariationMpm, minVariation, maxVariation);
      return {
        ...run,
        score: Math.max(0, Math.round((duration * weights.duration + speed * weights.speed + stability * weights.stability) * 100)),
      };
    })
    .sort((a, b) => b.score - a.score);
}

function formatDuration(minutes: number) {
  const hours = Math.floor(minutes / 60);
  const remainder = minutes % 60;
  return `${hours}h ${remainder.toString().padStart(2, "0")}min`;
}

function formatValue(value: number | null | undefined, decimals: number, unit: string) {
  if (value === null || value === undefined || !Number.isFinite(value)) return "—";
  const formatted = value.toLocaleString("pt-BR", {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  });
  return unit ? `${formatted} ${unit}` : formatted;
}

function statusClass(current: number | null | undefined, low: number | null, high: number | null) {
  if (current === null || current === undefined || low === null || high === null) return "unavailable";
  if (current >= low && current <= high) return "inside";
  const distance = current < low ? low - current : current - high;
  return distance <= (high - low) * .65 ? "near" : "outside";
}

function statusLabel(status: ReturnType<typeof statusClass>) {
  if (status === "inside") return "Dentro da faixa";
  if (status === "near") return "Próximo da faixa";
  if (status === "unavailable") return "Sem dados";
  return "Fora da faixa";
}

export default function BestConditionsScreen() {
  const [data, setData] = useState<BestConditionsDataset | null>(null);
  const [qualityId, setQualityId] = useState("");
  const [periodDays, setPeriodDays] = useState(90);
  const [profile, setProfile] = useState<RankingProfile>("balanced");
  const [selectedRunId, setSelectedRunId] = useState("");
  const [category, setCategory] = useState<"all" | ParameterCategory>("all");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    const selected = data?.qualities.find((quality) => quality.id === qualityId);
    setLoading(true);
    setError("");
    void bestConditionsGateway.load({
      periodDays,
      productCode: selected?.productCode,
      grammageGsm: selected?.grammageGsm,
    }, controller.signal).then((loaded) => {
      setData(loaded);
      if (!qualityId) {
        setQualityId(
          loaded.selectedQualityId ?? loaded.currentQualityId ?? loaded.qualities[0]?.id ?? "",
        );
      }
    }).catch((reason: unknown) => {
      if (reason instanceof DOMException && reason.name === "AbortError") return;
      setError(reason instanceof Error ? reason.message : "Não foi possível carregar a análise.");
    }).finally(() => {
      if (!controller.signal.aborted) setLoading(false);
    });
    return () => controller.abort();
  // `data` is intentionally excluded: only a filter change starts a new request.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [periodDays, qualityId]);

  const rankedRuns = useMemo(() => {
    if (!data) return [];
    return rankRuns(data.runs.filter((run) => run.qualityId === qualityId), profile);
  }, [data, profile, qualityId]);

  const selectedRun = rankedRuns.find((run) => run.id === selectedRunId) ?? rankedRuns[0] ?? null;
  const selectedQuality = data?.qualities.find((quality) => quality.id === qualityId) ?? null;
  const currentQuality = data?.qualities.find((quality) => quality.id === data.currentQualityId) ?? null;
  const visibleParameters = data?.parameters.filter((parameter) => category === "all" || parameter.category === category) ?? [];
  const comparableParameters = selectedRun && data
    ? data.parameters.filter((parameter) =>
        selectedRun.values[parameter.key] !== null &&
        selectedRun.values[parameter.key] !== undefined &&
        data.currentValues[parameter.key] !== null &&
        data.currentValues[parameter.key] !== undefined &&
        selectedRun.ranges[parameter.key] !== undefined)
    : [];
  const inRangeCount = selectedRun && data
    ? comparableParameters.filter((parameter) => {
        const current = data.currentValues[parameter.key]!;
        const range = selectedRun.ranges[parameter.key];
        return current >= range.minimum && current <= range.maximum;
      }).length
    : 0;

  if (!data) {
    return <div className="best-conditions-loading">{error || "Consultando o histórico de produção…"}</div>;
  }

  return (
    <section className="best-conditions-screen">
      <header className="best-conditions-heading">
        <div>
          <p className="best-conditions-eyebrow">Inteligência operacional</p>
          <h1>Melhores condições de produção</h1>
          <span>Encontre as campanhas mais estáveis para o mesmo produto e gramatura e compare os parâmetros utilizados.</span>
        </div>
        <div className="historian-data-badge" title="Dados calculados pelo Historian">
          <i />
          <span><b>Dados históricos</b>{loading ? "Atualizando análise…" : "Calculado pelo Historian"}</span>
        </div>
      </header>

      {error && <div className="conditions-error" role="alert">{error}</div>}

      <section className="conditions-filter-card" aria-label="Filtros da análise">
        <label className="quality-filter">
          <span>Produção comparável</span>
          <select disabled={loading || data.qualities.length === 0} value={qualityId} onChange={(event) => { setQualityId(event.target.value); setSelectedRunId(""); }}>
            {data.qualities.length === 0 && <option value="">Sem histórico de produto e gramatura</option>}
            {data.qualities.map((quality) => (
              <option value={quality.id} key={quality.id}>
                {quality.productName} · {quality.grammageGsm} g/m²
              </option>
            ))}
          </select>
          <small>Compara apenas campanhas do mesmo produto e gramatura.</small>
        </label>

        <div className="period-filter">
          <span>Janela histórica</span>
          <div>
            {[30, 90, 180].map((days) => (
              <button disabled={loading} type="button" className={periodDays === days ? "active" : ""} onClick={() => { setPeriodDays(days); setSelectedRunId(""); }} key={days}>
                {days} dias
              </button>
            ))}
          </div>
          <small>{rankedRuns.length} campanhas comparáveis encontradas.</small>
        </div>

        <div className="profile-filter">
          <span>O que significa “melhor”?</span>
          <div>
            {(Object.keys(profileLabels) as RankingProfile[]).map((profileId) => (
              <button type="button" className={profile === profileId ? "active" : ""} onClick={() => { setProfile(profileId); setSelectedRunId(""); }} key={profileId}>
                {profileLabels[profileId].label}
              </button>
            ))}
          </div>
          <small>{profileLabels[profile].help}</small>
        </div>
      </section>

      <section className="conditions-context-grid">
        <article className="current-production-card">
          <div><span>Produção atual</span><em>{currentQuality ? "Em produção" : "Sem contexto"}</em></div>
          <b>{currentQuality?.productName ?? "Não identificada"}</b>
          <p>{data.currentOrderCode ?? "Sem OP integrada"}{currentQuality ? ` · ${currentQuality.grammageGsm} g/m²` : ""}</p>
        </article>
        <article><span>Tempo sem quebra</span><b>{selectedRun ? formatDuration(selectedRun.durationMinutes) : "—"}</b><small>{selectedRun?.orderCode ?? "Sem campanha"}</small></article>
        <article><span>Velocidade média</span><b>{selectedRun?.averageSpeedMpm.toLocaleString("pt-BR", { maximumFractionDigits: 1 }) ?? "—"}<small> m/min</small></b><small>Máxima de {selectedRun?.maximumSpeedMpm.toLocaleString("pt-BR", { maximumFractionDigits: 1 }) ?? "—"} m/min</small></article>
        <article><span>Parâmetros aderentes</span><b>{inRangeCount}<small> / {comparableParameters.length}</small></b><small>Condição atual dentro da faixa</small></article>
      </section>

      {qualityId !== data.currentQualityId && (
        <div className="quality-context-warning">
          Você está analisando <b>{selectedQuality?.productName} {selectedQuality?.grammageGsm} g/m²</b>. A coluna “Atual” continua mostrando a produção vigente para facilitar a comparação visual.
        </div>
      )}

      <div className="conditions-workspace">
        <aside className="run-ranking" aria-label="Ranking de campanhas">
          <header>
            <div><p>Ranking histórico</p><h2>Melhores campanhas</h2></div>
            <span>{profileLabels[profile].label}</span>
          </header>
          {rankedRuns.length === 0 ? (
            <div className="no-ranked-runs">Nenhum trecho produtivo contínuo foi encontrado para este produto e gramatura. Confira a integração da produção e o histórico disponível.</div>
          ) : rankedRuns.map((run, index) => (
            <button type="button" className={selectedRun?.id === run.id ? "active" : ""} onClick={() => setSelectedRunId(run.id)} key={run.id}>
              <span className="run-position">{index + 1}</span>
              <div className="run-main">
                <div><b>{run.orderCode}</b><em>{new Date(run.startedAt).toLocaleDateString("pt-BR")}</em></div>
                <strong>{formatDuration(run.durationMinutes)} sem quebra</strong>
                <small>{run.averageSpeedMpm.toLocaleString("pt-BR", { maximumFractionDigits: 1 })} m/min · oscilação ±{run.speedVariationMpm.toLocaleString("pt-BR", { maximumFractionDigits: 1 })}</small>
              </div>
              <div className="run-score"><b>{run.score}</b><small>score</small></div>
            </button>
          ))}
          <footer>O score é recalculado quando o critério de ranking muda.</footer>
        </aside>

        <section className="condition-comparison">
          {selectedRun ? (
            <>
              <header className="comparison-heading">
                <div>
                  <p>Campanha selecionada</p>
                  <h2>{selectedRun.orderCode}</h2>
                  <span>{new Date(selectedRun.startedAt).toLocaleString("pt-BR")} · cobertura de dados {selectedRun.coveragePct}%</span>
                </div>
                <div className="selected-score" style={{ "--score": `${selectedRun.score * 3.6}deg` } as React.CSSProperties}>
                  <span><b>{selectedRun.score}</b><small>score</small></span>
                </div>
              </header>

              <div className="comparison-legend">
                <span><i className="current" />Valor atual</span>
                <span><i className="target" />Faixa da campanha selecionada</span>
              </div>

              <nav className="parameter-categories" aria-label="Categorias de parâmetros">
                {categoryOptions.map((option) => (
                  <button type="button" className={category === option.id ? "active" : ""} onClick={() => setCategory(option.id)} key={option.id}>{option.label}</button>
                ))}
              </nav>

              <div className="parameter-table">
                <div className="parameter-table-head"><span>Parâmetro</span><span>Melhor ocasião</span><span>Faixa observada</span><span>Atual</span><span>Situação</span></div>
                {visibleParameters.map((parameter) => {
                  const target = selectedRun.values[parameter.key];
                  const current = data.currentValues[parameter.key];
                  const range = selectedRun.ranges[parameter.key];
                  const low = range?.minimum ?? null;
                  const high = range?.maximum ?? null;
                  const status = statusClass(current, low, high);
                  const delta = target !== null && target !== undefined && current !== null && current !== undefined
                    ? current - target
                    : null;
                  return (
                    <div className="parameter-row" key={parameter.key}>
                      <span className="parameter-name">{parameter.label}</span>
                      <b>{formatValue(target, parameter.decimals, parameter.unit)}</b>
                      <span className="parameter-range">{range ? `${formatValue(low, parameter.decimals, parameter.unit)} — ${formatValue(high, parameter.decimals, parameter.unit)}` : "—"}</span>
                      <strong>{formatValue(current, parameter.decimals, parameter.unit)}{delta !== null && <small>{delta >= 0 ? "+" : ""}{delta.toLocaleString("pt-BR", { minimumFractionDigits: parameter.decimals, maximumFractionDigits: parameter.decimals })}</small>}</strong>
                      <em className={status}><i />{statusLabel(status)}</em>
                    </div>
                  );
                })}
              </div>
            </>
          ) : <div className="no-ranked-runs">Selecione outra janela histórica para visualizar uma campanha.</div>}
        </section>
      </div>

      <div className="prototype-next-step">
        <b>Regra da análise</b>
        <span>{data.methodology.description} Trechos mínimos de {data.methodology.minimumRunMinutes.toLocaleString("pt-BR")} minutos e velocidade mínima de {data.methodology.minimumProductiveSpeedMpm.toLocaleString("pt-BR")} m/min.</span>
      </div>
    </section>
  );
}
