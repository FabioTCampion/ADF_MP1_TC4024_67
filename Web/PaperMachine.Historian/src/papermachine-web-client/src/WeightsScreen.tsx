import { useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "./Auth";
import HistoryPeriodFilter, { useHistoryPeriod } from "./HistoryPeriodFilter";
import "./WeightsScreen.css";

type WeightCapture = {
  id: number;
  plcEventCounter: number;
  capturedAtUtc: string;
  observedAtUtc: string;
  weightKg: number;
  correctedWeightKg: number | null;
  correctionReason: string | null;
  correctedBy: string | null;
  correctedAtUtc: string | null;
  captureStatus: number;
  productionExternalRunId: string | null;
  productionOrderCode: string | null;
  productCode: string | null;
  grammageGsm: number | null;
  productionWidthMm: number | null;
};

const dateTimeFormatter = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short",
  timeStyle: "short",
});

const weightFormatter = new Intl.NumberFormat("pt-BR", {
  minimumFractionDigits: 1,
  maximumFractionDigits: 2,
});

const compactWeightFormatter = new Intl.NumberFormat("pt-BR", {
  maximumFractionDigits: 0,
});

function readError(response: Response, fallback: string) {
  return response.json()
    .then((body: { error?: string }) => body.error ?? fallback)
    .catch(() => fallback);
}

function effectiveWeight(capture: WeightCapture) {
  return capture.correctedWeightKg ?? capture.weightKg;
}

function statusLabel(status: number) {
  if (status === 20) return "Capturado";
  if (status === 10) return "Pendente";
  return `Status ${status}`;
}

function formatInterval(milliseconds: number | null) {
  if (milliseconds === null) return "—";
  const totalMinutes = Math.max(0, Math.round(milliseconds / 60_000));
  const days = Math.floor(totalMinutes / 1_440);
  const hours = Math.floor((totalMinutes % 1_440) / 60);
  const minutes = totalMinutes % 60;
  if (days > 0) return `${days}d ${hours}h`;
  if (hours > 0) return `${hours}h ${minutes.toString().padStart(2, "0")}min`;
  return `${minutes} min`;
}

export default function WeightsScreen() {
  const { user } = useAuth();
  const period = useHistoryPeriod("today", 366);
  const [search, setSearch] = useState("");
  const [appliedSearch, setAppliedSearch] = useState("");
  const [captures, setCaptures] = useState<WeightCapture[]>([]);
  const [editing, setEditing] = useState<WeightCapture | null>(null);
  const [deleting, setDeleting] = useState<WeightCapture | null>(null);
  const [correctedWeight, setCorrectedWeight] = useState("");
  const [reason, setReason] = useState("");
  const [deletionReason, setDeletionReason] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [exporting, setExporting] = useState<"pdf" | "excel" | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const canEdit = user.permissions.includes("weights.edit");
  const canDelete = user.permissions.includes("weights.delete");
  const canExport = user.permissions.includes("reports.generate");

  const load = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const query = new URLSearchParams({
        fromUtc: new Date(period.applied.from).toISOString(),
        toUtc: new Date(period.applied.to).toISOString(),
        limit: "5000",
      });
      const response = await fetch(`/api/production/weights?${query}`, {
        cache: "no-store",
        headers: { Accept: "application/json" },
      });
      if (response.status === 401) {
        window.location.reload();
        return;
      }
      if (!response.ok)
        throw new Error(await readError(response, "Não foi possível consultar as pesagens."));
      setCaptures((await response.json()) as WeightCapture[]);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Falha ao consultar pesagens.");
    } finally {
      setLoading(false);
    }
  }, [period.applied.from, period.applied.to, period.revision]);

  useEffect(() => {
    void load();
  }, [load]);

  const filteredCaptures = useMemo(() => {
    const term = appliedSearch.trim().toLocaleLowerCase("pt-BR");
    if (!term) return captures;
    return captures.filter((capture) => [
      capture.productionExternalRunId,
      capture.productionOrderCode,
      capture.productCode,
      String(capture.plcEventCounter),
    ].some((value) => value?.toLocaleLowerCase("pt-BR").includes(term)));
  }, [appliedSearch, captures]);

  const summary = useMemo(() => {
    const weights = filteredCaptures.map(effectiveWeight);
    const totalWeight = weights.reduce((total, weight) => total + weight, 0);
    return {
      totalWeight,
      averageWeight: weights.length ? totalWeight / weights.length : null,
      minimumWeight: weights.length ? Math.min(...weights) : null,
      maximumWeight: weights.length ? Math.max(...weights) : null,
      correctedCount: filteredCaptures.filter((capture) => capture.correctedWeightKg !== null).length,
      latest: filteredCaptures[0] ?? null,
    };
  }, [filteredCaptures]);

  const cadence = useMemo(() => {
    const ordered = [...filteredCaptures].sort(
      (left, right) => new Date(right.capturedAtUtc).getTime() - new Date(left.capturedAtUtc).getTime(),
    );
    const intervalByCapture = new Map<number, number>();
    const intervals: number[] = [];
    for (let index = 0; index < ordered.length - 1; index += 1) {
      const interval = new Date(ordered[index].capturedAtUtc).getTime() -
        new Date(ordered[index + 1].capturedAtUtc).getTime();
      if (interval <= 0) continue;
      intervalByCapture.set(ordered[index].id, interval);
      intervals.push(interval);
    }

    const average = intervals.length
      ? intervals.reduce((total, interval) => total + interval, 0) / intervals.length
      : null;
    const sortedIntervals = [...intervals].sort((left, right) => left - right);
    const middle = Math.floor(sortedIntervals.length / 2);
    const median = sortedIntervals.length === 0
      ? null
      : sortedIntervals.length % 2 === 0
        ? (sortedIntervals[middle - 1] + sortedIntervals[middle]) / 2
        : sortedIntervals[middle];
    const tolerance = median === null ? null : Math.max(median * 0.5, 15 * 60_000);
    const standardMinimum = median === null || tolerance === null
      ? null
      : Math.max(0, median - tolerance);
    const standardMaximum = median === null || tolerance === null
      ? null
      : median + tolerance;
    const outlierIds = new Set<number>();
    if (intervals.length >= 3 && median !== null && tolerance !== null) {
      for (const [captureId, interval] of intervalByCapture) {
        if (Math.abs(interval - median) > tolerance) outlierIds.add(captureId);
      }
    }

    return {
      average,
      median,
      standardMinimum,
      standardMaximum,
      intervalByCapture,
      outlierIds,
      intervalCount: intervals.length,
    };
  }, [filteredCaptures]);

  const dailySummary = useMemo(() => {
    const groups = new Map<string, { date: Date; count: number; total: number; minimum: number; maximum: number; corrected: number }>();
    for (const capture of filteredCaptures) {
      const date = new Date(capture.capturedAtUtc);
      const key = `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
      const weight = effectiveWeight(capture);
      const current = groups.get(key) ?? { date: new Date(date.getFullYear(), date.getMonth(), date.getDate()), count: 0, total: 0, minimum: weight, maximum: weight, corrected: 0 };
      current.count += 1;
      current.total += weight;
      current.minimum = Math.min(current.minimum, weight);
      current.maximum = Math.max(current.maximum, weight);
      if (capture.correctedWeightKg !== null) current.corrected += 1;
      groups.set(key, current);
    }
    return [...groups.values()].sort((left, right) => right.date.getTime() - left.date.getTime());
  }, [filteredCaptures]);

  const productSummary = useMemo(() => {
    const groups = new Map<string, { product: string; grammage: number | null; count: number; total: number }>();
    for (const capture of filteredCaptures) {
      const product = capture.productCode ?? "Sem produto";
      const key = `${product}|${capture.grammageGsm ?? "-"}`;
      const current = groups.get(key) ?? { product, grammage: capture.grammageGsm, count: 0, total: 0 };
      current.count += 1;
      current.total += effectiveWeight(capture);
      groups.set(key, current);
    }
    return [...groups.values()].sort((left, right) => right.total - left.total);
  }, [filteredCaptures]);

  const maximumDailyTotal = Math.max(0, ...dailySummary.map((item) => item.total));

  const openEditor = (capture: WeightCapture) => {
    setEditing(capture);
    setCorrectedWeight(String(effectiveWeight(capture)).replace(".", ","));
    setReason("");
    setError("");
    setNotice("");
  };

  const openDelete = (capture: WeightCapture) => {
    setDeleting(capture);
    setDeletionReason("");
    setError("");
    setNotice("");
  };

  const downloadExport = async (format: "pdf" | "excel") => {
    setExporting(format);
    setError("");
    setNotice("");
    try {
      const query = new URLSearchParams({
        start: new Date(period.applied.from).toISOString(),
        end: new Date(period.applied.to).toISOString(),
      });
      if (appliedSearch.trim()) query.set("search", appliedSearch.trim());
      const response = await fetch(`/api/reports/weights/${format}?${query}`, {
        cache: "no-store",
        headers: {
          Accept: format === "pdf"
            ? "application/pdf"
            : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        },
      });
      if (response.status === 401) {
        window.location.reload();
        return;
      }
      if (!response.ok)
        throw new Error(await readError(response, "Não foi possível exportar as pesagens."));
      const disposition = response.headers.get("Content-Disposition") ?? "";
      const encodedName = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
      const simpleName = disposition.match(/filename="?([^";]+)"?/i)?.[1];
      const fileName = encodedName
        ? decodeURIComponent(encodedName)
        : simpleName ?? (format === "pdf" ? "Relatorio-Pesos.pdf" : "Pesos-Capturados.xlsx");
      const url = URL.createObjectURL(await response.blob());
      const link = document.createElement("a");
      link.href = url;
      link.download = fileName;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
      setNotice(format === "pdf"
        ? "Relatório PDF gerado com os filtros aplicados."
        : "Planilha Excel gerada com os filtros aplicados.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Falha ao exportar pesagens.");
    } finally {
      setExporting(null);
    }
  };

  const saveCorrection = async () => {
    if (!editing) return;
    const normalizedWeight = correctedWeight.trim();
    const parsedWeight = normalizedWeight.includes(",")
      ? Number(normalizedWeight.replace(/\./g, "").replace(",", "."))
      : Number(normalizedWeight);
    if (!Number.isFinite(parsedWeight) || parsedWeight <= 0) {
      setError("Informe um peso válido maior que zero.");
      return;
    }
    if (reason.trim().length < 5) {
      setError("Descreva o motivo da correção com pelo menos 5 caracteres.");
      return;
    }

    setSaving(true);
    setError("");
    try {
      const response = await fetch(`/api/production/weights/${editing.id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ weightKg: parsedWeight, reason: reason.trim() }),
      });
      if (!response.ok)
        throw new Error(await readError(response, "Não foi possível corrigir a pesagem."));
      setEditing(null);
      setNotice("Peso corrigido e alteração registrada no histórico.");
      await load();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Falha ao corrigir pesagem.");
    } finally {
      setSaving(false);
    }
  };

  const deleteCapture = async () => {
    if (!deleting) return;
    if (deletionReason.trim().length < 5) {
      setError("Descreva o motivo da exclusão com pelo menos 5 caracteres.");
      return;
    }

    setSaving(true);
    setError("");
    try {
      const response = await fetch(`/api/production/weights/${deleting.id}`, {
        method: "DELETE",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason: deletionReason.trim() }),
      });
      if (!response.ok)
        throw new Error(await readError(response, "Não foi possível excluir a pesagem."));
      setDeleting(null);
      setNotice("Apontamento excluído da consulta e preservado no histórico de auditoria.");
      await load();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Falha ao excluir pesagem.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <div className="screen-title weights-title">
        <div>
          <p className="eyebrow">Produção · Balança de jumbos</p>
          <h1>Pesos capturados</h1>
          <span>Consulte as pesagens registradas pelo PLC e confira as correções realizadas.</span>
        </div>
        <div className="weights-title-actions">
          {canExport && (
            <div className="weights-export-actions" aria-label="Exportar pesos capturados">
              <button className="weights-export-button" type="button" onClick={() => void downloadExport("pdf")} disabled={loading || exporting !== null}>
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path d="M7 3h7l4 4v14H7z" />
                  <path d="M14 3v5h5M9.5 13h6M9.5 16h6" />
                </svg>
                <span>
                  <b>{exporting === "pdf" ? "Gerando PDF…" : "Gerar relatório PDF"}</b>
                  <small>Resumo, pesos e intervalos</small>
                </span>
              </button>
              <button className="weights-export-button" type="button" onClick={() => void downloadExport("excel")} disabled={loading || exporting !== null}>
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path d="M7 3h7l4 4v14H7z" />
                  <path d="M14 3v5h5M9.5 12l5 5M14.5 12l-5 5" />
                </svg>
                <span>
                  <b>{exporting === "excel" ? "Gerando Excel…" : "Exportar para Excel"}</b>
                  <small>Dados filtráveis em planilha</small>
                </span>
              </button>
            </div>
          )}
          <div className={`weights-access-badge${canEdit ? " weights-access-badge--edit" : ""}`}>
            <i />
            <span>{canEdit ? "Edição de supervisor" : "Somente consulta"}</span>
          </div>
        </div>
      </div>

      <section className="weights-summary" aria-label="Resumo das pesagens">
        <article className="weights-summary-card weights-summary-card--primary">
          <span>Último peso</span>
          <strong>{summary.latest ? weightFormatter.format(effectiveWeight(summary.latest)) : "—"}<small>kg</small></strong>
          <p>{summary.latest ? dateTimeFormatter.format(new Date(summary.latest.capturedAtUtc)) : "Nenhuma captura no período"}</p>
        </article>
        <article className="weights-summary-card">
          <span>Capturas no período</span>
          <strong>{filteredCaptures.length.toLocaleString("pt-BR")}</strong>
          <p>Registros recebidos do PLC</p>
        </article>
        <article className="weights-summary-card">
          <span>Peso acumulado</span>
          <strong>{compactWeightFormatter.format(summary.totalWeight)}<small>kg</small></strong>
          <p>Considerando valores corrigidos</p>
        </article>
        <article className="weights-summary-card weights-summary-card--corrected">
          <span>Pesos corrigidos</span>
          <strong>{summary.correctedCount.toLocaleString("pt-BR")}</strong>
          <p>{summary.correctedCount === 1 ? "Alteração auditada" : "Alterações auditadas"}</p>
        </article>
      </section>

      <HistoryPeriodFilter
        period={period}
        loading={loading}
        maximumRangeLabel="Período máximo: 366 dias"
        presets={["today", "24h", "yesterday", "7d", "30d"]}
        search={search}
        searchPlaceholder="Jumbo, OP, produto ou evento…"
        onSearch={setSearch}
        onCommit={() => {
          setNotice("");
          setAppliedSearch(search.trim());
        }}
        resultSummary={`${filteredCaptures.length.toLocaleString("pt-BR")} pesagens · ${(summary.totalWeight / 1000).toLocaleString("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} t`}
      />

      {error && <div className="weights-message weights-message--error" role="alert">{error}</div>}
      {notice && <div className="weights-message weights-message--success" role="status">{notice}</div>}

      <section className="weights-cadence" aria-label="Cadência das capturas de peso">
        <div className="weights-cadence-heading">
          <div>
            <p className="eyebrow">Cadência operacional</p>
            <h2>Intervalo entre capturas</h2>
            <span>Calculado entre pesagens consecutivas dentro do período e dos filtros selecionados.</span>
          </div>
          <small>Desvio relevante: mais de 50% do intervalo típico, respeitando tolerância mínima de 15 minutos.</small>
        </div>
        <div className="weights-cadence-grid">
          <article><span>Intervalo médio</span><b>{formatInterval(cadence.average)}</b><small>{cadence.intervalCount} intervalo{cadence.intervalCount === 1 ? "" : "s"} calculado{cadence.intervalCount === 1 ? "" : "s"}</small></article>
          <article><span>Intervalo típico</span><b>{formatInterval(cadence.median)}</b><small>Mediana, menos sensível a paradas longas</small></article>
          <article><span>Faixa considerada padrão</span><b>{cadence.standardMinimum === null ? "—" : `${formatInterval(cadence.standardMinimum)} a ${formatInterval(cadence.standardMaximum)}`}</b><small>{cadence.intervalCount < 3 ? "Aguardando pelo menos 4 capturas" : "Aplicada à marcação dos registros"}</small></article>
          <article className={cadence.outlierIds.size ? "weights-cadence-alert" : ""}><span>Fora do padrão</span><b>{cadence.outlierIds.size.toLocaleString("pt-BR")}</b><small>{cadence.outlierIds.size ? "Capturas que requerem conferência" : "Nenhuma divergência relevante"}</small></article>
        </div>
      </section>

      <section className="weights-manager-summary" aria-label="Consolidação gerencial do período">
        <div className="weights-manager-heading">
          <div>
            <p className="eyebrow">Visão gerencial</p>
            <h2>Consolidado do período</h2>
            <span>Médias, faixa de peso e distribuição da produção selecionada.</span>
          </div>
          <div className="weights-manager-kpis">
            <div><span>Peso médio</span><b>{summary.averageWeight === null ? "—" : `${weightFormatter.format(summary.averageWeight)} kg`}</b></div>
            <div><span>Menor peso</span><b>{summary.minimumWeight === null ? "—" : `${weightFormatter.format(summary.minimumWeight)} kg`}</b></div>
            <div><span>Maior peso</span><b>{summary.maximumWeight === null ? "—" : `${weightFormatter.format(summary.maximumWeight)} kg`}</b></div>
          </div>
        </div>
        <div className="weights-manager-grid">
          <article className="weights-consolidation-panel">
            <div className="weights-panel-title">
              <div><h3>Produção por dia</h3><span>Peso total e quantidade de jumbos</span></div>
              <b>{dailySummary.length} dia{dailySummary.length === 1 ? "" : "s"}</b>
            </div>
            <div className="weights-daily-head"><span>Data</span><span>Volume</span><span>Jumbos</span><span>Média</span><span>Faixa de peso</span><span>Ajustes</span></div>
            <div className="weights-consolidation-scroll">
              {dailySummary.length === 0 && <div className="weights-manager-empty">Sem produção no período.</div>}
              {dailySummary.map((item) => (
                <div className="weights-daily-row" key={item.date.toISOString()}>
                  <b>{item.date.toLocaleDateString("pt-BR", { day: "2-digit", month: "short" })}</b>
                  <div className="weights-volume-cell">
                    <span>{(item.total / 1000).toLocaleString("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} t</span>
                    <i><em style={{ width: `${maximumDailyTotal ? Math.max(3, item.total / maximumDailyTotal * 100) : 0}%` }} /></i>
                  </div>
                  <span>{item.count}</span>
                  <span>{weightFormatter.format(item.total / item.count)} kg</span>
                  <span>{weightFormatter.format(item.minimum)}–{weightFormatter.format(item.maximum)} kg</span>
                  <span className={item.corrected ? "weights-adjustment-count" : ""}>{item.corrected}</span>
                </div>
              ))}
            </div>
          </article>

          <article className="weights-consolidation-panel">
            <div className="weights-panel-title">
              <div><h3>Consolidado por produto</h3><span>Participação no peso do período</span></div>
              <b>{productSummary.length} produto{productSummary.length === 1 ? "" : "s"}</b>
            </div>
            <div className="weights-product-list">
              {productSummary.length === 0 && <div className="weights-manager-empty">Sem produtos no período.</div>}
              {productSummary.map((item) => {
                const share = summary.totalWeight ? item.total / summary.totalWeight * 100 : 0;
                return (
                  <div className="weights-product-row" key={`${item.product}-${item.grammage}`}>
                    <div><b>{item.product}</b><span>{item.grammage === null ? "Gramatura não informada" : `${item.grammage.toLocaleString("pt-BR")} g/m²`} · {item.count} jumbo{item.count === 1 ? "" : "s"}</span></div>
                    <div><b>{(item.total / 1000).toLocaleString("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} t</b><span>{share.toLocaleString("pt-BR", { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%</span></div>
                    <i><em style={{ width: `${share}%` }} /></i>
                  </div>
                );
              })}
            </div>
          </article>
        </div>
      </section>

      <section className="weights-table" aria-label="Histórico de pesos capturados">
        <div className="weights-table-heading">
          <div>
            <h2>Histórico de pesagens</h2>
            <span>{filteredCaptures.length.toLocaleString("pt-BR")} registro{filteredCaptures.length === 1 ? "" : "s"} exibido{filteredCaptures.length === 1 ? "" : "s"}</span>
          </div>
          <small>Período de {dateTimeFormatter.format(new Date(period.applied.from))} a {dateTimeFormatter.format(new Date(period.applied.to))}</small>
        </div>
        <div className="weights-head">
          <span>Data e hora</span>
          <span>Jumbo / OP</span>
          <span>Produto</span>
          <span>Peso capturado</span>
          <span>Peso considerado</span>
          <span>Intervalo</span>
          <span>Status</span>
          <span>Ação</span>
        </div>
        {loading && <div className="weights-empty">Consultando pesagens…</div>}
        {!loading && filteredCaptures.length === 0 && (
          <div className="weights-empty">
            <b>Nenhuma pesagem encontrada</b>
            <span>Ajuste o período ou a busca para localizar outros registros.</span>
          </div>
        )}
        {!loading && filteredCaptures.map((capture) => {
          const corrected = capture.correctedWeightKg !== null;
          const interval = cadence.intervalByCapture.get(capture.id) ?? null;
          const outsideInterval = cadence.outlierIds.has(capture.id);
          return (
            <div className={`weights-row${corrected ? " weights-row--corrected" : ""}${outsideInterval ? " weights-row--interval-outlier" : ""}`} key={capture.id}>
              <div className="weights-date-cell">
                <b>{new Date(capture.capturedAtUtc).toLocaleDateString("pt-BR")}</b>
                <span>{new Date(capture.capturedAtUtc).toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit", second: "2-digit" })}</span>
                <small>Evento #{capture.plcEventCounter}</small>
              </div>
              <div>
                <b>{capture.productionExternalRunId ?? "—"}</b>
                <span>OP {capture.productionOrderCode ?? "não informada"}</span>
              </div>
              <div>
                <b>{capture.productCode ?? "Sem produto"}</b>
                <span>{capture.grammageGsm === null ? "Gramatura —" : `${capture.grammageGsm.toLocaleString("pt-BR")} g/m²`}</span>
                <small>{capture.productionWidthMm === null ? "Largura —" : `${capture.productionWidthMm.toLocaleString("pt-BR")} mm`}</small>
              </div>
              <span className={corrected ? "weights-original--changed" : "weights-original"}>
                {weightFormatter.format(capture.weightKg)} kg
              </span>
              <div className="weights-effective">
                <b>{weightFormatter.format(effectiveWeight(capture))} <small>kg</small></b>
                {corrected && (
                  <span title={capture.correctionReason ?? undefined}>
                    Corrigido por {capture.correctedBy ?? "supervisor"}
                  </span>
                )}
              </div>
              <div className="weights-interval-cell">
                <b>{formatInterval(interval)}</b>
                {outsideInterval && <span>Fora do padrão</span>}
                {interval === null && <small>Primeira no período</small>}
              </div>
              <span className={`weights-status weights-status--${capture.captureStatus === 20 ? "ok" : "pending"}`}>
                <i />{statusLabel(capture.captureStatus)}
              </span>
              <div className="weights-actions">
                {canEdit && <button type="button" onClick={() => openEditor(capture)}>{corrected ? "Revisar" : "Corrigir"}</button>}
                {canDelete && <button className="weights-delete-button" type="button" onClick={() => openDelete(capture)}>Excluir</button>}
                {!canEdit && !canDelete && <span>—</span>}
              </div>
            </div>
          );
        })}
      </section>

      {editing && (
        <div className="weights-modal-backdrop" role="presentation">
          <form
            className="weights-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="weights-modal-title"
            onSubmit={(event) => {
              event.preventDefault();
              void saveCorrection();
            }}
          >
            <div className="weights-modal-heading">
              <div>
                <span>Correção auditada · Evento #{editing.plcEventCounter}</span>
                <h2 id="weights-modal-title">Corrigir peso capturado</h2>
              </div>
              <button type="button" onClick={() => setEditing(null)} aria-label="Fechar">×</button>
            </div>
            <div className="weights-modal-context">
              <div><span>Capturado pelo PLC</span><b>{weightFormatter.format(editing.weightKg)} kg</b></div>
              <div><span>Jumbo / OP</span><b>{editing.productionExternalRunId ?? "—"} · {editing.productionOrderCode ?? "—"}</b></div>
              <div><span>Data e hora</span><b>{dateTimeFormatter.format(new Date(editing.capturedAtUtc))}</b></div>
            </div>
            {error && <div className="weights-message weights-message--error" role="alert">{error}</div>}
            <label>
              <span>Novo peso (kg)</span>
              <input
                autoFocus
                required
                inputMode="decimal"
                value={correctedWeight}
                onChange={(event) => setCorrectedWeight(event.target.value)}
                placeholder="Ex.: 4.876,5"
              />
            </label>
            <label>
              <span>Motivo da correção</span>
              <textarea
                required
                minLength={5}
                maxLength={500}
                rows={3}
                value={reason}
                onChange={(event) => setReason(event.target.value)}
                placeholder="Ex.: Valor conferido no ticket da balança."
              />
              <small>O motivo, seu usuário e o horário ficarão registrados.</small>
            </label>
            <div className="weights-modal-actions">
              <button type="button" onClick={() => setEditing(null)}>Cancelar</button>
              <button className="primary-button" disabled={saving}>{saving ? "Salvando…" : "Salvar correção"}</button>
            </div>
          </form>
        </div>
      )}

      {deleting && (
        <div className="weights-modal-backdrop" role="presentation">
          <form
            className="weights-modal weights-delete-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="weights-delete-title"
            onSubmit={(event) => {
              event.preventDefault();
              void deleteCapture();
            }}
          >
            <div className="weights-modal-heading">
              <div>
                <span>Exclusão auditada · Evento #{deleting.plcEventCounter}</span>
                <h2 id="weights-delete-title">Excluir apontamento de peso</h2>
              </div>
              <button type="button" onClick={() => setDeleting(null)} aria-label="Fechar">×</button>
            </div>
            <div className="weights-delete-warning">
              <b>Este peso deixará de aparecer nas consultas e nos consolidados.</b>
              <span>O registro original será mantido internamente com seu usuário, horário e motivo da exclusão.</span>
            </div>
            <div className="weights-modal-context">
              <div><span>Peso considerado</span><b>{weightFormatter.format(effectiveWeight(deleting))} kg</b></div>
              <div><span>Jumbo / OP</span><b>{deleting.productionExternalRunId ?? "—"} · {deleting.productionOrderCode ?? "—"}</b></div>
              <div><span>Data e hora</span><b>{dateTimeFormatter.format(new Date(deleting.capturedAtUtc))}</b></div>
            </div>
            {error && <div className="weights-message weights-message--error" role="alert">{error}</div>}
            <label>
              <span>Motivo da exclusão</span>
              <textarea
                autoFocus
                required
                minLength={5}
                maxLength={500}
                rows={3}
                value={deletionReason}
                onChange={(event) => setDeletionReason(event.target.value)}
                placeholder="Ex.: Apontamento duplicado confirmado pelo supervisor."
              />
              <small>O motivo ficará registrado na auditoria.</small>
            </label>
            <div className="weights-modal-actions">
              <button type="button" onClick={() => setDeleting(null)}>Cancelar</button>
              <button className="weights-confirm-delete" disabled={saving}>{saving ? "Excluindo…" : "Excluir apontamento"}</button>
            </div>
          </form>
        </div>
      )}
    </>
  );
}
