import { useCallback, useEffect, useMemo, useState } from "react";
import InteractiveChart, { type InteractiveChartOption } from "./InteractiveChart";
import "./MetricsScreen.css";

type MetricView =
  | "speed"
  | "hourlyProductivity"
  | "hourlyBreaks"
  | "hourlyLoss"
  | "interruptions"
  | "correlations"
  | "table";

type ProductivitySample = {
  capturedAtUtc: string;
  speedMpm: number | null;
  paperPresent: boolean | null;
  quality: string;
};

type ProductivityInterval = {
  start: Date;
  end: Date;
  speedMpm: number;
  paperPresent: boolean;
  productive: boolean;
};

type ProductionInterruption = {
  start: Date;
  end: Date;
  durationMinutes: number;
};

type ProductiveRun = {
  start: Date;
  end: Date;
  durationMinutes: number;
};

type CommandEvent = {
  id: number;
  commandName: string;
  previousValueJson: string | null;
  currentValueJson: string;
  observedAtUtc: string;
  origin: string;
};

type StatusChange = {
  id: number;
  fieldName: string;
  previousValueJson: string | null;
  currentValueJson: string;
  observedAtUtc: string;
};

type CorrelationEvent = {
  id: string;
  kind: "command" | "status";
  name: string;
  previousValueJson: string | null;
  currentValueJson: string;
  observedAt: Date;
  offsetMilliseconds: number;
};

type BreakCorrelation = {
  interruption: ProductionInterruption;
  events: CorrelationEvent[];
};

const DEFAULT_PRODUCTIVE_SPEED_MPM = 10;
const DEFAULT_SAMPLE_INTERVAL_MILLISECONDS = 10_000;
const MAX_CONTINUOUS_GAP_MILLISECONDS = 5 * 60_000;
const MINIMUM_INTERRUPTION_MILLISECONDS = 60_000;
const MAX_RANGE_MILLISECONDS = 7 * 24 * 60 * 60_000;
const CORRELATION_LOOKBACK_MILLISECONDS = 5 * 60_000;
const CORRELATION_AFTER_MILLISECONDS = 60_000;
const CORRELATION_EVENT_LIMIT = 5_000;
const MAX_CORRELATION_EVENTS_PER_BREAK = 8;
const HOURS = Array.from({ length: 24 }, (_, index) => index);

const CONTINUOUS_STATUS_FIELD_PATTERN =
  /speed|torque|current|pressure|temperature|level|reference|setpoint|percent|mpm|rpm/i;
const BREAK_SOURCE_FIELDS = new Set([
  "dryingSectionGroup3UpperMasterSpeedMPM",
  "dryingSectionGroup3PaperPresence",
]);

const startOfDay = (value = new Date()) =>
  new Date(value.getFullYear(), value.getMonth(), value.getDate());

const formatInputDate = (value: Date) => {
  const offset = value.getTimezoneOffset();
  return new Date(value.getTime() - offset * 60_000).toISOString().slice(0, 16);
};

const formatNumber = (value: number, digits = 1) =>
  value.toLocaleString("pt-BR", {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  });

const formatPeriod = (start: Date, end: Date) =>
  `${start.toLocaleString("pt-BR", {
    dateStyle: "short",
    timeStyle: "short",
  })} até ${end.toLocaleString("pt-BR", {
    dateStyle: "short",
    timeStyle: "short",
  })}`;

const formatDuration = (minutes: number | null) => {
  if (minutes === null || !Number.isFinite(minutes)) return "—";
  if (minutes < 60) return `${formatNumber(minutes, 1)} min`;
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = Math.round(minutes % 60);
  return `${formatNumber(hours, 0)}h ${remainingMinutes.toString().padStart(2, "0")}min`;
};

const average = (values: number[]) =>
  values.length > 0
    ? values.reduce((total, value) => total + value, 0) / values.length
    : null;

const parseStoredValue = (value: string | null) => {
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
};

const formatCorrelationOffset = (milliseconds: number) => {
  const absoluteSeconds = Math.round(Math.abs(milliseconds) / 1000);
  if (absoluteSeconds <= 5) return "no início";
  const minutes = Math.floor(absoluteSeconds / 60);
  const seconds = absoluteSeconds % 60;
  const duration =
    minutes > 0
      ? `${minutes}min${seconds > 0 ? ` ${seconds}s` : ""}`
      : `${seconds}s`;
  return `${duration} ${milliseconds < 0 ? "antes" : "depois"}`;
};

const isValidSample = (
  sample: ProductivitySample,
): sample is ProductivitySample & { speedMpm: number; paperPresent: boolean } =>
  typeof sample.speedMpm === "number" &&
  Number.isFinite(sample.speedMpm) &&
  typeof sample.paperPresent === "boolean" &&
  sample.quality === "Good";

function inferSampleIntervalMilliseconds(samples: ProductivitySample[]) {
  const validSamples = samples.filter(isValidSample);
  const occurrences = new Map<number, number>();
  for (let index = 1; index < validSamples.length; index += 1) {
    const interval =
      new Date(validSamples[index].capturedAtUtc).getTime() -
      new Date(validSamples[index - 1].capturedAtUtc).getTime();
    if (interval <= 0 || interval > MAX_CONTINUOUS_GAP_MILLISECONDS) continue;
    const rounded = Math.round(interval / 1000) * 1000;
    occurrences.set(rounded, (occurrences.get(rounded) ?? 0) + 1);
  }

  let selectedInterval = DEFAULT_SAMPLE_INTERVAL_MILLISECONDS;
  let selectedCount = 0;
  occurrences.forEach((count, interval) => {
    if (
      count > selectedCount ||
      (count === selectedCount && interval < selectedInterval)
    ) {
      selectedInterval = interval;
      selectedCount = count;
    }
  });
  return selectedInterval;
}

function buildIntervals(
  samples: ProductivitySample[],
  periodStart: Date,
  periodEnd: Date,
  productiveSpeedMpm: number,
) {
  const validSamples = samples
    .filter(isValidSample)
    .slice()
    .sort(
      (left, right) =>
        new Date(left.capturedAtUtc).getTime() -
        new Date(right.capturedAtUtc).getTime(),
    );
  const cadence = inferSampleIntervalMilliseconds(validSamples);
  const intervals: ProductivityInterval[] = [];

  validSamples.forEach((sample, index) => {
    const sampleAt = new Date(sample.capturedAtUtc).getTime();
    const nextAt =
      index + 1 < validSamples.length
        ? new Date(validSamples[index + 1].capturedAtUtc).getTime()
        : sampleAt + cadence;
    const start = Math.max(sampleAt, periodStart.getTime());
    const end = Math.min(
      sampleAt + cadence,
      nextAt,
      periodEnd.getTime(),
    );
    if (end <= start) return;
    intervals.push({
      start: new Date(start),
      end: new Date(end),
      speedMpm: sample.speedMpm,
      paperPresent: sample.paperPresent,
      productive: sample.paperPresent && sample.speedMpm >= productiveSpeedMpm,
    });
  });

  return { cadence, intervals };
}

function buildInterruptions(intervals: ProductivityInterval[]) {
  const interruptions: ProductionInterruption[] = [];
  let activeStart: Date | null = null;
  let activeEnd: Date | null = null;

  const appendActive = () => {
    if (!activeStart || !activeEnd) return;
    const durationMilliseconds = activeEnd.getTime() - activeStart.getTime();
    if (durationMilliseconds >= MINIMUM_INTERRUPTION_MILLISECONDS) {
      interruptions.push({
        start: activeStart,
        end: activeEnd,
        durationMinutes: durationMilliseconds / 60_000,
      });
    }
    activeStart = null;
    activeEnd = null;
  };

  intervals.forEach((interval) => {
    if (interval.productive) {
      appendActive();
      return;
    }
    if (
      activeEnd &&
      interval.start.getTime() - activeEnd.getTime() >
        DEFAULT_SAMPLE_INTERVAL_MILLISECONDS
    ) {
      appendActive();
    }
    activeStart ??= interval.start;
    activeEnd = interval.end;
  });
  appendActive();
  return interruptions;
}

function buildProductiveRuns(intervals: ProductivityInterval[]) {
  const runs: ProductiveRun[] = [];
  let activeStart: Date | null = null;
  let activeEnd: Date | null = null;

  const appendActive = () => {
    if (activeStart && activeEnd) {
      runs.push({
        start: activeStart,
        end: activeEnd,
        durationMinutes:
          (activeEnd.getTime() - activeStart.getTime()) / 60_000,
      });
    }
    activeStart = null;
    activeEnd = null;
  };

  intervals.forEach((interval) => {
    if (!interval.productive) {
      appendActive();
      return;
    }
    if (
      activeEnd &&
      interval.start.getTime() - activeEnd.getTime() >
        DEFAULT_SAMPLE_INTERVAL_MILLISECONDS
    ) {
      appendActive();
    }
    activeStart ??= interval.start;
    activeEnd = interval.end;
  });
  appendActive();
  return runs;
}

function calculateReliabilityMetrics(
  intervals: ProductivityInterval[],
  interruptions: ProductionInterruption[],
  cadence: number,
) {
  const mttrMinutes = average(
    interruptions.map((interruption) => interruption.durationMinutes),
  );
  const totalProductiveMinutes = intervals.reduce(
    (total, interval) =>
      total +
      (interval.productive
        ? (interval.end.getTime() - interval.start.getTime()) / 60_000
        : 0),
    0,
  );
  const productiveRuns = buildProductiveRuns(intervals);
  const runsEndingInFailure = productiveRuns.filter((run) =>
    interruptions.some(
      (interruption) =>
        Math.abs(run.end.getTime() - interruption.start.getTime()) <= cadence,
    ),
  );

  return {
    mttrMinutes,
    mtbfMinutes:
      interruptions.length > 0
        ? totalProductiveMinutes / interruptions.length
        : null,
    mttfMinutes: average(
      runsEndingInFailure.map((run) => run.durationMinutes),
    ),
    failuresWithOperatingRun: runsEndingInFailure.length,
  };
}

function isDiscreteStatusChange(change: StatusChange) {
  if (BREAK_SOURCE_FIELDS.has(change.fieldName)) return false;
  if (CONTINUOUS_STATUS_FIELD_PATTERN.test(change.fieldName)) return false;
  const currentValue = parseStoredValue(change.currentValueJson);
  const previousValue = parseStoredValue(change.previousValueJson);
  return currentValue !== previousValue;
}

function buildBreakCorrelations(
  interruptions: ProductionInterruption[],
  commandEvents: CommandEvent[],
  statusChanges: StatusChange[],
) {
  const sourceEvents = [
    ...commandEvents.map((event): Omit<CorrelationEvent, "offsetMilliseconds"> => ({
      id: `command-${event.id}`,
      kind: "command",
      name: event.commandName,
      previousValueJson: event.previousValueJson,
      currentValueJson: event.currentValueJson,
      observedAt: new Date(event.observedAtUtc),
    })),
    ...statusChanges
      .filter(isDiscreteStatusChange)
      .map((event): Omit<CorrelationEvent, "offsetMilliseconds"> => ({
        id: `status-${event.id}`,
        kind: "status",
        name: event.fieldName,
        previousValueJson: event.previousValueJson,
        currentValueJson: event.currentValueJson,
        observedAt: new Date(event.observedAtUtc),
      })),
  ];

  return interruptions.map((interruption): BreakCorrelation => {
    const breakAt = interruption.start.getTime();
    const candidates = sourceEvents
      .map((event) => ({
        ...event,
        offsetMilliseconds: event.observedAt.getTime() - breakAt,
      }))
      .filter(
        (event) =>
          event.offsetMilliseconds >= -CORRELATION_LOOKBACK_MILLISECONDS &&
          event.offsetMilliseconds <= CORRELATION_AFTER_MILLISECONDS,
      )
      .sort(
        (left, right) =>
          Math.abs(left.offsetMilliseconds) -
          Math.abs(right.offsetMilliseconds),
      );
    const statusQuota = 3;
    const events = [
      ...candidates
        .filter((event) => event.kind === "command")
        .slice(0, MAX_CORRELATION_EVENTS_PER_BREAK - statusQuota),
      ...candidates
        .filter((event) => event.kind === "status")
        .slice(0, statusQuota),
    ].sort(
      (left, right) =>
        Math.abs(left.offsetMilliseconds) -
        Math.abs(right.offsetMilliseconds),
    );
    return { interruption, events };
  });
}

function summarizeProductivity(
  samples: ProductivitySample[],
  periodStart: Date,
  periodEnd: Date,
  productiveSpeedMpm: number,
) {
  const { cadence, intervals } = buildIntervals(
    samples,
    periodStart,
    periodEnd,
    productiveSpeedMpm,
  );
  let coveredMilliseconds = 0;
  let productiveMilliseconds = 0;
  let paperMilliseconds = 0;
  let weightedProductiveSpeed = 0;
  let weightedGeneralSpeed = 0;

  intervals.forEach((interval) => {
    const duration = interval.end.getTime() - interval.start.getTime();
    coveredMilliseconds += duration;
    weightedGeneralSpeed += interval.speedMpm * duration;
    if (interval.paperPresent) paperMilliseconds += duration;
    if (interval.productive) {
      productiveMilliseconds += duration;
      weightedProductiveSpeed += interval.speedMpm * duration;
    }
  });

  const hourlyCovered = HOURS.map(() => 0);
  const hourlyProductive = HOURS.map(() => 0);
  intervals.forEach((interval) => {
    let cursor = interval.start.getTime();
    const end = interval.end.getTime();
    while (cursor < end) {
      const current = new Date(cursor);
      const nextHour = new Date(
        current.getFullYear(),
        current.getMonth(),
        current.getDate(),
        current.getHours() + 1,
      ).getTime();
      const segmentEnd = Math.min(end, nextHour);
      const duration = segmentEnd - cursor;
      hourlyCovered[current.getHours()] += duration;
      if (interval.productive) hourlyProductive[current.getHours()] += duration;
      cursor = segmentEnd;
    }
  });

  const unproductiveMilliseconds = Math.max(
    0,
    coveredMilliseconds - productiveMilliseconds,
  );
  const interruptions = buildInterruptions(intervals);
  const productiveRuns = buildProductiveRuns(intervals);
  const reliability = calculateReliabilityMetrics(
    intervals,
    interruptions,
    cadence,
  );
  const hourlyBreaks = HOURS.map(() => 0);
  interruptions.forEach((interruption) => {
    hourlyBreaks[interruption.start.getHours()] += 1;
  });

  return {
    cadence,
    intervals,
    interruptions,
    reliability,
    hourlyBreaks,
    longestProductiveRunMinutes:
      productiveRuns.length > 0
        ? Math.max(...productiveRuns.map((run) => run.durationMinutes))
        : null,
    coveredMinutes: coveredMilliseconds / 60_000,
    productiveMinutes: productiveMilliseconds / 60_000,
    unproductiveMinutes: unproductiveMilliseconds / 60_000,
    paperPresentMinutes: paperMilliseconds / 60_000,
    productivityPercent:
      coveredMilliseconds > 0
        ? (productiveMilliseconds / coveredMilliseconds) * 100
        : 0,
    paperPresencePercent:
      coveredMilliseconds > 0 ? (paperMilliseconds / coveredMilliseconds) * 100 : 0,
    productiveAverageSpeed:
      productiveMilliseconds > 0
        ? weightedProductiveSpeed / productiveMilliseconds
        : 0,
    generalAverageSpeed:
      coveredMilliseconds > 0 ? weightedGeneralSpeed / coveredMilliseconds : 0,
    maximumSpeed:
      intervals.length > 0
        ? Math.max(...intervals.map((interval) => interval.speedMpm))
        : 0,
    hourlyProductivity: HOURS.map((hour) =>
      hourlyCovered[hour] > 0
        ? (hourlyProductive[hour] / hourlyCovered[hour]) * 100
        : 0,
    ),
    hourlyUnproductiveMinutes: HOURS.map(
      (hour) => (hourlyCovered[hour] - hourlyProductive[hour]) / 60_000,
    ),
  };
}

function SpeedChart({
  samples,
  interruptions,
  productiveSpeedMpm,
}: {
  samples: ProductivitySample[];
  interruptions: ProductionInterruption[];
  productiveSpeedMpm: number;
}) {
  const rows = samples.filter(isValidSample);
  const speeds = rows.map((sample) => sample.speedMpm);
  const productiveSpeeds = rows
    .filter(
      (sample) =>
        sample.paperPresent && sample.speedMpm >= productiveSpeedMpm,
    )
    .map((sample) => sample.speedMpm);
  const average =
    productiveSpeeds.length > 0
      ? productiveSpeeds.reduce((sum, value) => sum + value, 0) /
        productiveSpeeds.length
      : 0;
  const maximum = Math.max(1, ...speeds);
  const option: InteractiveChartOption = {
    backgroundColor: "transparent",
    animation: false,
    grid: { left: 64, right: 25, top: 34, bottom: 72 },
    tooltip: {
      trigger: "axis",
      axisPointer: { type: "cross" },
      valueFormatter: (value: unknown) =>
        `${formatNumber(Number(value), 1)} m/min`,
    },
    xAxis: {
      type: "time",
      axisLabel: { hideOverlap: true },
      splitLine: { show: false },
    },
    yAxis: {
      type: "value",
      min: 0,
      max: Math.ceil(maximum / 25) * 25,
      name: "m/min",
      splitLine: { lineStyle: { color: "#26364d" } },
    },
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
    series: [
      {
        type: "line",
        name: "Velocidade",
        showSymbol: false,
        sampling: "lttb",
        connectNulls: false,
        data: rows.map((sample) => [sample.capturedAtUtc, sample.speedMpm]),
        lineStyle: { width: 2, color: "#55a2ff" },
        areaStyle: { color: "#245fa133" },
        markLine: {
          symbol: "none",
          data: [
            {
              yAxis: productiveSpeedMpm,
              name: "Mínima produtiva",
              lineStyle: { color: "#e7b85c", type: "dashed" },
            },
            ...(average > 0
              ? [
                  {
                    yAxis: average,
                    name: "Média produtiva",
                    lineStyle: { color: "#43d49a", type: "dashed" },
                  },
                ]
              : []),
          ],
        },
        markArea: {
          silent: true,
          itemStyle: { color: "#dc55451c" },
          data: interruptions.map((interruption) => [
            { xAxis: interruption.start },
            { xAxis: interruption.end },
          ]),
        },
      },
    ],
  };

  return (
    <InteractiveChart
      option={option}
      ariaLabel="Velocidade do terceiro grupo com interrupções de produção destacadas"
    />
  );
}

function HourlyChart({
  values,
  unit,
  maximum,
  color,
  digits = 1,
}: {
  values: number[];
  unit: string;
  maximum?: number;
  color: string;
  digits?: number;
}) {
  const option: InteractiveChartOption = {
    backgroundColor: "transparent",
    animationDuration: 250,
    grid: { left: 62, right: 24, top: 28, bottom: 70 },
    tooltip: {
      trigger: "axis",
      axisPointer: { type: "shadow" },
      valueFormatter: (value: unknown) =>
        `${formatNumber(Number(value), digits)} ${unit}`,
    },
    xAxis: {
      type: "category",
      data: HOURS.map((hour) => `${hour.toString().padStart(2, "0")}h`),
      axisLabel: { interval: 1 },
    },
    yAxis: {
      type: "value",
      min: 0,
      max: maximum ?? Math.max(1, ...values),
      name: unit,
      splitLine: { lineStyle: { color: "#26364d" } },
    },
    dataZoom: [
      { type: "inside" },
      { type: "slider", height: 22, bottom: 16 },
    ],
    series: [
      {
        type: "bar",
        name: unit,
        data: values.map((value) => Number(value.toFixed(digits))),
        barMaxWidth: 28,
        itemStyle: { color, borderRadius: [5, 5, 0, 0] },
      },
    ],
  };

  return (
    <InteractiveChart
      option={option}
      ariaLabel={`Indicador horário em ${unit}`}
    />
  );
}

function InterruptionChart({
  interruptions,
}: {
  interruptions: ProductionInterruption[];
}) {
  const option: InteractiveChartOption = {
    backgroundColor: "transparent",
    animationDuration: 250,
    grid: { left: 62, right: 24, top: 28, bottom: 82 },
    tooltip: {
      trigger: "axis",
      axisPointer: { type: "shadow" },
      valueFormatter: (value: unknown) =>
        `${formatNumber(Number(value), 1)} min`,
    },
    xAxis: {
      type: "category",
      data: interruptions.map(
        (interruption, index) =>
          `#${index + 1} · ${interruption.start.toLocaleTimeString("pt-BR", {
            hour: "2-digit",
            minute: "2-digit",
          })}`,
      ),
      axisLabel: {
        hideOverlap: true,
        rotate: interruptions.length > 16 ? 35 : 0,
      },
    },
    yAxis: {
      type: "value",
      min: 0,
      name: "min",
      splitLine: { lineStyle: { color: "#26364d" } },
    },
    dataZoom: [
      { type: "inside" },
      { type: "slider", height: 22, bottom: 16 },
    ],
    series: [
      {
        type: "bar",
        name: "Duração",
        data: interruptions.map((interruption) =>
          Number(interruption.durationMinutes.toFixed(2)),
        ),
        barMaxWidth: 28,
        itemStyle: { color: "#df765d", borderRadius: [5, 5, 0, 0] },
      },
    ],
  };

  return (
    <InteractiveChart
      option={option}
      ariaLabel="Duração das interrupções de produção"
    />
  );
}

function CorrelationAnalysis({
  correlations,
  historyTruncated,
  warning,
}: {
  correlations: BreakCorrelation[];
  historyTruncated: boolean;
  warning: string;
}) {
  const recurrence = new Map<
    string,
    { kind: CorrelationEvent["kind"]; name: string; breaks: number }
  >();
  correlations.forEach((correlation) => {
    const seenInBreak = new Set<string>();
    correlation.events.forEach((event) => {
      const key = `${event.kind}:${event.name}`;
      if (seenInBreak.has(key)) return;
      seenInBreak.add(key);
      const current = recurrence.get(key);
      recurrence.set(key, {
        kind: event.kind,
        name: event.name,
        breaks: (current?.breaks ?? 0) + 1,
      });
    });
  });
  const recurringEvents = [...recurrence.values()]
    .filter((event) => event.breaks >= 2)
    .sort((left, right) => right.breaks - left.breaks)
    .slice(0, 4);

  return (
    <div className="metrics-correlations">
      <div className="correlation-explanation">
        <strong>Correlação temporal, não causa comprovada</strong>
        <span>
          Eventos entre 5 minutos antes e 1 minuto depois do início de cada
          quebra. Leituras contínuas e os próprios sinais usados para detectar a
          quebra são descartados.
        </span>
      </div>

      {(historyTruncated || warning) && (
        <div className="correlation-warning">
          {warning ||
            "O período atingiu o limite de 5.000 eventos. A análise pode estar incompleta; reduza o intervalo para maior precisão."}
        </div>
      )}

      <section className="correlation-patterns">
        <span>Padrões recorrentes</span>
        {recurringEvents.length === 0 ? (
          <small>
            Nenhum comando ou estado apareceu próximo de duas ou mais quebras.
          </small>
        ) : (
          <div>
            {recurringEvents.map((event) => (
              <article key={`${event.kind}-${event.name}`}>
                <b>{event.name}</b>
                <small>
                  {event.kind === "command" ? "Comando" : "Mudança de estado"} ·{" "}
                  {event.breaks} de {correlations.length} quebras
                </small>
              </article>
            ))}
          </div>
        )}
      </section>

      <div className="correlation-breaks">
        {correlations.length === 0 ? (
          <div className="metrics-stop-empty">
            Nenhuma quebra confirmada para correlacionar.
          </div>
        ) : (
          correlations.map((correlation, index) => (
            <article
              className="correlation-break"
              key={`${correlation.interruption.start.toISOString()}-${index}`}
            >
              <header>
                <div>
                  <span>Quebra {index + 1}</span>
                  <b>
                    {correlation.interruption.start.toLocaleString("pt-BR")}
                  </b>
                </div>
                <small>
                  Duração:{" "}
                  {formatDuration(correlation.interruption.durationMinutes)}
                </small>
              </header>
              {correlation.events.length === 0 ? (
                <p>Nenhum evento discreto encontrado na janela analisada.</p>
              ) : (
                <div className="correlation-events">
                  {correlation.events.map((event) => (
                    <div className="correlation-event" key={event.id}>
                      <em className={`correlation-event-${event.kind}`}>
                        {event.kind === "command" ? "Comando" : "Estado"}
                      </em>
                      <div>
                        <b>{event.name}</b>
                        <small>
                          {parseStoredValue(event.previousValueJson)} →{" "}
                          {parseStoredValue(event.currentValueJson)}
                        </small>
                      </div>
                      <span>
                        {formatCorrelationOffset(event.offsetMilliseconds)}
                      </span>
                    </div>
                  ))}
                </div>
              )}
            </article>
          ))
        )}
      </div>
    </div>
  );
}

export default function MetricsScreen() {
  const now = new Date();
  const [periodStart, setPeriodStart] = useState(() => startOfDay());
  const [periodEnd, setPeriodEnd] = useState(now);
  const [draftStart, setDraftStart] = useState(() => startOfDay());
  const [draftEnd, setDraftEnd] = useState(now);
  const [productiveSpeedMpm, setProductiveSpeedMpm] = useState(
    DEFAULT_PRODUCTIVE_SPEED_MPM,
  );
  const [samples, setSamples] = useState<ProductivitySample[]>([]);
  const [commandEvents, setCommandEvents] = useState<CommandEvent[]>([]);
  const [statusChanges, setStatusChanges] = useState<StatusChange[]>([]);
  const [correlationWarning, setCorrelationWarning] = useState("");
  const [view, setView] = useState<MetricView>("speed");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  const load = useCallback(async (start: Date, end: Date) => {
    setLoading(true);
    setError("");
    try {
      const parameters = new URLSearchParams({
        fromUtc: start.toISOString(),
        toUtc: end.toISOString(),
      });
      const eventParameters = new URLSearchParams({
        fromUtc: new Date(
          start.getTime() - CORRELATION_LOOKBACK_MILLISECONDS,
        ).toISOString(),
        toUtc: end.toISOString(),
        limit: String(CORRELATION_EVENT_LIMIT),
      });
      const [response, commandsResponse, statusResponse] = await Promise.all([
        fetch(`/api/history/productivity?${parameters}`, {
          headers: { Accept: "application/json" },
        }),
        fetch(`/api/history/commands?${eventParameters}`, {
          headers: { Accept: "application/json" },
        }),
        fetch(`/api/history/status-changes?${eventParameters}`, {
          headers: { Accept: "application/json" },
        }),
      ]);
      if (
        response.status === 401 ||
        commandsResponse.status === 401 ||
        statusResponse.status === 401
      ) {
        window.location.reload();
        return;
      }
      if (!response.ok) {
        const body = (await response.json().catch(() => null)) as {
          error?: string;
        } | null;
        throw new Error(body?.error ?? `Consulta falhou (${response.status}).`);
      }
      setSamples((await response.json()) as ProductivitySample[]);
      const unavailableSources: string[] = [];
      if (commandsResponse.ok) {
        setCommandEvents((await commandsResponse.json()) as CommandEvent[]);
      } else {
        setCommandEvents([]);
        unavailableSources.push("comandos");
      }
      if (statusResponse.ok) {
        setStatusChanges((await statusResponse.json()) as StatusChange[]);
      } else {
        setStatusChanges([]);
        unavailableSources.push("mudanças de estado");
      }
      setCorrelationWarning(
        unavailableSources.length > 0
          ? `Não foi possível consultar ${unavailableSources.join(" e ")}. A correlação está incompleta.`
          : "",
      );
    } catch (exception) {
      setSamples([]);
      setCommandEvents([]);
      setStatusChanges([]);
      setCorrelationWarning("");
      setError(
        exception instanceof Error
          ? exception.message
          : "Não foi possível carregar as métricas.",
      );
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load(periodStart, periodEnd);
  }, [load, periodEnd, periodStart]);

  const applyPeriod = (start = draftStart, end = draftEnd) => {
    if (end <= start) {
      setError("A data final deve ser posterior à data inicial.");
      return;
    }
    if (end.getTime() - start.getTime() > MAX_RANGE_MILLISECONDS) {
      setError("O período máximo para métricas é de 7 dias.");
      return;
    }
    setDraftStart(start);
    setDraftEnd(end);
    setPeriodStart(start);
    setPeriodEnd(end);
  };

  const applyShortcut = (kind: "today" | "8h" | "24h" | "yesterday") => {
    const current = new Date();
    let start = startOfDay(current);
    let end = current;
    if (kind === "8h") start = new Date(current.getTime() - 8 * 60 * 60_000);
    if (kind === "24h") start = new Date(current.getTime() - 24 * 60 * 60_000);
    if (kind === "yesterday") {
      end = startOfDay(current);
      start = new Date(end.getTime() - 24 * 60 * 60_000);
    }
    applyPeriod(start, end);
  };

  const summary = useMemo(
    () =>
      summarizeProductivity(
        samples,
        periodStart,
        periodEnd,
        productiveSpeedMpm,
      ),
    [periodEnd, periodStart, productiveSpeedMpm, samples],
  );
  const correlations = useMemo(
    () =>
      buildBreakCorrelations(
        summary.interruptions,
        commandEvents,
        statusChanges,
      ),
    [commandEvents, statusChanges, summary.interruptions],
  );
  const correlationHistoryTruncated =
    commandEvents.length >= CORRELATION_EVENT_LIMIT ||
    statusChanges.length >= CORRELATION_EVENT_LIMIT;
  const longestInterruption =
    summary.interruptions.length > 0
      ? Math.max(
          ...summary.interruptions.map(
            (interruption) => interruption.durationMinutes,
          ),
        )
      : null;
  const lastInterruption =
    summary.interruptions.length > 0
      ? summary.interruptions[summary.interruptions.length - 1]
      : null;
  const viewTitles: Record<MetricView, string> = {
    speed: "Velocidade do terceiro grupo",
    hourlyProductivity: "Produtividade por hora",
    hourlyBreaks: "Número de quebras por hora",
    hourlyLoss: "Tempo sem produção por hora",
    interruptions: "Duração das interrupções",
    correlations: "Eventos próximos às quebras",
    table: "Tabela de interrupções",
  };

  return (
    <section className="metrics-screen">
      <div className="metrics-heading">
        <div>
          <p className="metrics-eyebrow">Produção</p>
          <h1>Métricas da máquina</h1>
          <span>
            Produtividade calculada pela velocidade e presença de papel no
            terceiro grupo.
          </span>
        </div>
      </div>

      {error && (
        <div className="metrics-error" role="alert">
          {error}
        </div>
      )}

      <section className="metrics-search-card">
        <div className="metrics-search-card-heading">
          <div>
            <span>Período e regra de produtividade</span>
            <small>
              Todos os indicadores e gráficos usam os mesmos critérios.
            </small>
          </div>
          <small>Período máximo na tela: 7 dias</small>
        </div>

        <div className="metrics-date-action">
          <div className="metrics-range-fields">
            <label>
              De
              <input
                type="datetime-local"
                value={formatInputDate(draftStart)}
                onChange={(event) => setDraftStart(new Date(event.target.value))}
              />
            </label>
            <span>até</span>
            <label>
              Até
              <input
                type="datetime-local"
                value={formatInputDate(draftEnd)}
                onChange={(event) => setDraftEnd(new Date(event.target.value))}
              />
            </label>
          </div>
          <div className="metrics-range-shortcuts">
            <button type="button" onClick={() => applyShortcut("today")}>
              Hoje
            </button>
            <button type="button" onClick={() => applyShortcut("8h")}>
              Últimas 8h
            </button>
            <button type="button" onClick={() => applyShortcut("24h")}>
              Últimas 24h
            </button>
            <button type="button" onClick={() => applyShortcut("yesterday")}>
              Ontem
            </button>
          </div>
          <button
            className="metrics-apply-range"
            type="button"
            onClick={() => applyPeriod()}
            disabled={loading}
          >
            {loading ? "Carregando…" : "Aplicar período"}
          </button>
        </div>

        <div className="metrics-production-rule">
          <div>
            <span>Condição produtiva</span>
            <strong>
              <i />
              Papel presente
              <em>+</em>
              velocidade mínima
            </strong>
          </div>
          <label>
            Velocidade mínima
            <span>
              <input
                type="number"
                min={0}
                max={500}
                step={5}
                value={productiveSpeedMpm}
                onChange={(event) =>
                  setProductiveSpeedMpm(
                    Math.min(500, Math.max(0, Number(event.target.value) || 0)),
                  )
                }
              />
              m/min
            </span>
          </label>
          <small>
            Fonte: dryingSectionGroup3UpperMasterSpeedMPM e
            dryingSectionGroup3PaperPresence.
          </small>
        </div>
      </section>

      <section className="metrics-summary">
        <div className="productivity">
          <span>Produtividade operacional</span>
          <b>
            {summary.coveredMinutes > 0
              ? `${formatNumber(summary.productivityPercent, 1)}%`
              : "—"}
          </b>
          <small>{formatDuration(summary.coveredMinutes)} com dados válidos</small>
        </div>
        <div>
          <span>Tempo produtivo</span>
          <b>{formatDuration(summary.productiveMinutes)}</b>
          <small>Papel presente e velocidade dentro do critério</small>
        </div>
        <div>
          <span>Tempo sem produção</span>
          <b>{formatDuration(summary.unproductiveMinutes)}</b>
          <small>Uma ou ambas as condições não foram atendidas</small>
        </div>
        <div>
          <span>Velocidade média produtiva</span>
          <b>
            {summary.productiveAverageSpeed > 0
              ? `${formatNumber(summary.productiveAverageSpeed, 1)} m/min`
              : "—"}
          </b>
          <small>Somente durante produção confirmada</small>
        </div>
        <div>
          <span>Velocidade média geral</span>
          <b>
            {summary.coveredMinutes > 0
              ? `${formatNumber(summary.generalAverageSpeed, 1)} m/min`
              : "—"}
          </b>
          <small>Inclui todo o período com dados válidos</small>
        </div>
        <div>
          <span>Velocidade máxima</span>
          <b>
            {summary.intervals.length > 0
              ? `${formatNumber(summary.maximumSpeed, 1)} m/min`
              : "—"}
          </b>
          <small>Terceiro grupo de secagem</small>
        </div>
        <div>
          <span>Papel presente</span>
          <b>
            {summary.coveredMinutes > 0
              ? `${formatNumber(summary.paperPresencePercent, 1)}%`
              : "—"}
          </b>
          <small>{formatDuration(summary.paperPresentMinutes)} no período</small>
        </div>
        <div>
          <span>Interrupções confirmadas</span>
          <b>{formatNumber(summary.interruptions.length, 0)}</b>
          <small>
            Maior: {formatDuration(longestInterruption)} · ciclo de{" "}
            {formatNumber(summary.cadence / 1000, 0)} s
          </small>
        </div>
        <div>
          <span>Tempo máximo sem quebra</span>
          <b>{formatDuration(summary.longestProductiveRunMinutes)}</b>
          <small>Maior período produtivo contínuo</small>
        </div>
        <div>
          <span>Duração da última quebra</span>
          <b>{formatDuration(lastInterruption?.durationMinutes ?? null)}</b>
          <small>
            {lastInterruption
              ? `Início: ${lastInterruption.start.toLocaleString("pt-BR")}`
              : "Nenhuma quebra confirmada no período"}
          </small>
        </div>
      </section>

      <section className="reliability-panel">
        <div className="reliability-heading">
          <div>
            <p className="metrics-eyebrow">Confiabilidade operacional</p>
            <h2>Indicadores de manutenção</h2>
          </div>
          <small>
            Base: quebras confirmadas e somente intervalos efetivamente
            cobertos por dados válidos.
          </small>
        </div>
        <div className="reliability-grid">
          <article>
            <span>MTTR</span>
            <b>{formatDuration(summary.reliability.mttrMinutes)}</b>
            <small>Tempo médio para restaurar a produção após uma quebra</small>
          </article>
          <article>
            <span>MTBF</span>
            <b>{formatDuration(summary.reliability.mtbfMinutes)}</b>
            <small>Tempo produtivo acumulado por quebra confirmada</small>
          </article>
          <article>
            <span>MTTF</span>
            <b>{formatDuration(summary.reliability.mttfMinutes)}</b>
            <small>
              Duração média dos períodos produtivos que terminaram em quebra
            </small>
          </article>
        </div>
        <p>
          {summary.interruptions.length > 0
            ? `${summary.interruptions.length} quebra${summary.interruptions.length > 1 ? "s" : ""} usada${summary.interruptions.length > 1 ? "s" : ""} no MTTR/MTBF e ${summary.reliability.failuresWithOperatingRun} com período produtivo anterior identificado no MTTF.`
            : "Os indicadores exigem pelo menos uma quebra confirmada no período."}
        </p>
      </section>

      <div className="metrics-workspace">
        <nav className="metrics-navigation" aria-label="Tipos de métrica">
          <button
            type="button"
            className={view === "speed" ? "active" : ""}
            onClick={() => setView("speed")}
          >
            Velocidade da máquina
          </button>
          <button
            type="button"
            className={view === "hourlyProductivity" ? "active" : ""}
            onClick={() => setView("hourlyProductivity")}
          >
            Produtividade H/H
          </button>
          <button
            type="button"
            className={view === "hourlyLoss" ? "active" : ""}
            onClick={() => setView("hourlyLoss")}
          >
            Tempo sem produção H/H
          </button>
          <button
            type="button"
            className={view === "hourlyBreaks" ? "active" : ""}
            onClick={() => setView("hourlyBreaks")}
          >
            Número de quebras H/H
          </button>
          <button
            type="button"
            className={view === "interruptions" ? "active" : ""}
            onClick={() => setView("interruptions")}
          >
            Tempo de cada interrupção
          </button>
          <button
            type="button"
            className={view === "correlations" ? "active" : ""}
            onClick={() => setView("correlations")}
          >
            Correlação de eventos
          </button>
          <button
            type="button"
            className={view === "table" ? "active" : ""}
            onClick={() => setView("table")}
          >
            Tabela de interrupções
          </button>
        </nav>

        <article className="metrics-chart-card">
          <div className="metrics-chart-title">
            <div>
              <h2>{viewTitles[view]}</h2>
              <span>{formatPeriod(periodStart, periodEnd)}</span>
            </div>
            {view !== "table" && view !== "correlations" && (
              <div className="metrics-interaction-hint">
                <b>Interativo</b>
                <span>Role para zoom · arraste para navegar</span>
              </div>
            )}
          </div>

          {loading ? (
            <div className="metrics-empty">Carregando dados…</div>
          ) : summary.intervals.length === 0 ? (
            <div className="metrics-empty">
              Sem dados válidos de velocidade e papel no período.
            </div>
          ) : (
            <>
              {view === "speed" && (
                <SpeedChart
                  samples={samples}
                  interruptions={summary.interruptions}
                  productiveSpeedMpm={productiveSpeedMpm}
                />
              )}
              {view === "hourlyProductivity" && (
                <HourlyChart
                  values={summary.hourlyProductivity}
                  unit="%"
                  maximum={100}
                  color="#43d49a"
                />
              )}
              {view === "hourlyLoss" && (
                <HourlyChart
                  values={summary.hourlyUnproductiveMinutes}
                  unit="min"
                  color="#df765d"
                />
              )}
              {view === "hourlyBreaks" && (
                <HourlyChart
                  values={summary.hourlyBreaks}
                  unit="quebras"
                  color="#e7b85c"
                  digits={0}
                />
              )}
              {view === "interruptions" &&
                (summary.interruptions.length > 0 ? (
                  <InterruptionChart interruptions={summary.interruptions} />
                ) : (
                  <div className="metrics-empty">
                    Nenhuma interrupção superior a 60 segundos.
                  </div>
                ))}
              {view === "correlations" && (
                <CorrelationAnalysis
                  correlations={correlations}
                  historyTruncated={correlationHistoryTruncated}
                  warning={correlationWarning}
                />
              )}
              {view === "table" && (
                <div className="metrics-stop-table">
                  <div className="metrics-stop-head">
                    <span>Início</span>
                    <span>Fim</span>
                    <span>Duração</span>
                    <span>Condição</span>
                  </div>
                  {summary.interruptions.length === 0 ? (
                    <div className="metrics-stop-empty">
                      Nenhuma interrupção confirmada.
                    </div>
                  ) : (
                    summary.interruptions.map((interruption, index) => (
                      <div
                        className="metrics-stop-row"
                        key={`${interruption.start.toISOString()}-${index}`}
                      >
                        <span>{interruption.start.toLocaleString("pt-BR")}</span>
                        <span>{interruption.end.toLocaleString("pt-BR")}</span>
                        <b>{formatDuration(interruption.durationMinutes)}</b>
                        <em>Sem produtividade</em>
                      </div>
                    ))
                  )}
                </div>
              )}
            </>
          )}
        </article>
      </div>

      <p className="metrics-note">
        Interrupções são listadas após 60 segundos contínuos sem a condição
        produtiva. Intervalos sem amostras não são contabilizados.
      </p>
    </section>
  );
}
