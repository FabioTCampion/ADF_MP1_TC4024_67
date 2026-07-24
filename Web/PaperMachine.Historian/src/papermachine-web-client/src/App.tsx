import {
  lazy,
  Suspense,
  useCallback,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import { useAuth } from "./Auth";
import type { InteractiveChartOption } from "./InteractiveChart";
import SideMenu, { type NavigationItem, type PageId } from "./SideMenu";

const InteractiveChart = lazy(() => import("./InteractiveChart"));

type RuntimeStatus = {
  adsConnected: boolean;
  lastSuccessfulReadAtUtc: string | null;
  lastError: string | null;
  mappingVersion: string | null;
};

type CurrentSnapshot = {
  capturedAtUtc: string;
  status: Record<string, unknown>;
  commands: Record<string, unknown>;
  alarms: Record<string, boolean>;
  mappingVersion: string;
};

type AlarmEvent = {
  id: number;
  alarmName: string;
  activatedAtUtc: string;
  clearedAtUtc: string | null;
  durationMilliseconds: number | null;
  activeAtStartup: boolean;
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

type MotorTrendMotor = {
  key: string;
  speedField: string;
  torqueField: string;
  speedUnit: string;
  torqueUnit: string;
};

type MotorTrendSample = {
  capturedAtUtc: string;
  values: Record<string, { speed: number | null; torque: number | null }>;
};

type MotorTrend = {
  motors: MotorTrendMotor[];
  samples: MotorTrendSample[];
};

const navigation: readonly NavigationItem[] = [
  { id: "dashboard", label: "Visão geral", icon: "dashboard" },
  { id: "status", label: "Status atual", icon: "status" },
  { id: "graphs", label: "Gráficos", icon: "graphs" },
  { id: "alarms", label: "Alarmes", icon: "alarm" },
  { id: "commands", label: "Comandos", icon: "command" },
  { id: "history", label: "Histórico de status", icon: "history" },
];

const dateFormatter = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short",
  timeStyle: "medium",
});

const formatDate = (value: string | null | undefined) =>
  value ? dateFormatter.format(new Date(value)) : "—";

const formatDuration = (milliseconds: number | null) => {
  if (milliseconds === null) return "Em andamento";
  const totalSeconds = Math.max(0, Math.round(milliseconds / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return [hours && `${hours}h`, minutes && `${minutes}min`, `${seconds}s`].filter(Boolean).join(" ");
};

const formatFieldName = (value: string) =>
  value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/_/g, " ")
    .replace(/\b\w/g, (character) => character.toUpperCase());

const parseStoredValue = (value: string | null) => {
  if (value === null) return "—";
  try {
    return formatValue(JSON.parse(value) as unknown);
  } catch {
    return value;
  }
};

const formatValue = (value: unknown) => {
  if (typeof value === "boolean") return value ? "Ativo" : "Inativo";
  if (typeof value === "number") return Number.isInteger(value) ? String(value) : value.toFixed(2);
  if (value === null || value === undefined || value === "") return "—";
  return String(value);
};

async function fetchJson<T>(url: string): Promise<T> {
  const response = await fetch(url, { headers: { Accept: "application/json" } });
  if (response.status === 401) {
    window.location.reload();
    throw new Error("Sessão expirada.");
  }
  if (!response.ok) throw new Error(`Consulta falhou (${response.status}).`);
  return response.json() as Promise<T>;
}

export default function App() {
  const { user, logout } = useAuth();
  const [page, setPage] = useState<PageId>("dashboard");
  const [collapsed, setCollapsed] = useState(
    () => window.localStorage.getItem("historian.menu.collapsed") === "true",
  );
  const [runtime, setRuntime] = useState<RuntimeStatus | null>(null);
  const [current, setCurrent] = useState<CurrentSnapshot | null>(null);
  const [clock, setClock] = useState(new Date());

  const refreshLiveData = useCallback(async () => {
    const [runtimeResult, currentResult] = await Promise.allSettled([
      fetchJson<RuntimeStatus>("/api/runtime"),
      fetchJson<CurrentSnapshot>("/api/current"),
    ]);
    if (runtimeResult.status === "fulfilled") setRuntime(runtimeResult.value);
    if (currentResult.status === "fulfilled") setCurrent(currentResult.value);
  }, []);

  useEffect(() => {
    void refreshLiveData();
    const dataTimer = window.setInterval(() => void refreshLiveData(), 5000);
    const clockTimer = window.setInterval(() => setClock(new Date()), 1000);
    return () => {
      window.clearInterval(dataTimer);
      window.clearInterval(clockTimer);
    };
  }, [refreshLiveData]);

  const toggleMenu = () => {
    setCollapsed((value) => {
      window.localStorage.setItem("historian.menu.collapsed", String(!value));
      return !value;
    });
  };

  const collapseMenu = useCallback(() => {
    if (collapsed) return;
    window.localStorage.setItem("historian.menu.collapsed", "true");
    setCollapsed(true);
  }, [collapsed]);

  useEffect(() => {
    const collapseOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") collapseMenu();
    };
    window.addEventListener("keydown", collapseOnEscape);
    return () => window.removeEventListener("keydown", collapseOnEscape);
  }, [collapseMenu]);

  return (
    <div className={`app-shell${collapsed ? " app-shell--collapsed" : ""}`}>
      <SideMenu
        currentPage={page}
        items={navigation}
        collapsed={collapsed}
        online={runtime?.adsConnected ?? false}
        onSelect={setPage}
        onToggle={toggleMenu}
      />
      <main className="app-main" onPointerDown={collapseMenu}>
        <header className="top-bar">
          <button
            type="button"
            className="mobile-menu"
            onPointerDown={(event) => event.stopPropagation()}
            onClick={toggleMenu}
          >
            ☰
          </button>
          <div className="top-context">
            <span>Paper Machine Historian</span>
            <b>{navigation.find((item) => item.id === page)?.label}</b>
          </div>
          <div className="top-spacer" />
          <div className="top-clock">
            <span>{clock.toLocaleDateString("pt-BR")}</span>
            <b>{clock.toLocaleTimeString("pt-BR")}</b>
          </div>
          <div className="current-user">
            <div className="user-avatar">{user.displayName.slice(0, 2).toUpperCase()}</div>
            <div>
              <b>{user.displayName}</b>
              <span>{user.role === "Administrator" ? "Administrador" : "Consulta"}</span>
            </div>
            <button type="button" onClick={() => void logout()}>Sair</button>
          </div>
        </header>

        <div className="screen-content">
          {page === "dashboard" && <Dashboard runtime={runtime} current={current} />}
          {page === "status" && <CurrentStatus current={current} />}
          {page === "graphs" && <MotorGraphs />}
          {page === "alarms" && <AlarmHistory />}
          {page === "commands" && <CommandHistory />}
          {page === "history" && <StatusHistory />}
        </div>
      </main>
    </div>
  );
}

function ScreenTitle({
  eyebrow,
  title,
  subtitle,
  action,
}: {
  eyebrow: string;
  title: string;
  subtitle: string;
  action?: ReactNode;
}) {
  return (
    <div className="screen-title">
      <div>
        <p className="eyebrow">{eyebrow}</p>
        <h1>{title}</h1>
        <span>{subtitle}</span>
      </div>
      {action}
    </div>
  );
}

function Dashboard({
  runtime,
  current,
}: {
  runtime: RuntimeStatus | null;
  current: CurrentSnapshot | null;
}) {
  const [alarms, setAlarms] = useState<AlarmEvent[]>([]);
  const [commands, setCommands] = useState<CommandEvent[]>([]);

  useEffect(() => {
    Promise.all([
      fetchJson<AlarmEvent[]>("/api/history/alarms?limit=8"),
      fetchJson<CommandEvent[]>("/api/history/commands?limit=8"),
    ]).then(([alarmRows, commandRows]) => {
      setAlarms(alarmRows);
      setCommands(commandRows);
    }).catch(() => undefined);
  }, [current?.capturedAtUtc]);

  const activeAlarmCount = current
    ? Object.values(current.alarms).filter(Boolean).length
    : 0;
  const trueStatusCount = current
    ? Object.values(current.status).filter((value) => value === true).length
    : 0;

  return (
    <>
      <ScreenTitle
        eyebrow="Operação em tempo real"
        title="Visão geral"
        subtitle="Resumo da aquisição ADS e dos eventos mais recentes da máquina."
        action={<ConnectionBadge connected={runtime?.adsConnected ?? false} />}
      />

      <section className="metric-grid">
        <MetricCard label="Comunicação ADS" value={runtime?.adsConnected ? "Conectado" : "Desconectado"} accent={runtime?.adsConnected ? "green" : "red"} detail="192.168.100.1.1.1 · 851" />
        <MetricCard label="Alarmes ativos" value={String(activeAlarmCount)} accent={activeAlarmCount ? "red" : "green"} detail="Estado atual no PLC" />
        <MetricCard label="Sinais ativos" value={String(trueStatusCount)} accent="blue" detail={`${Object.keys(current?.status ?? {}).length} campos monitorados`} />
        <MetricCard label="Última leitura" value={formatDate(runtime?.lastSuccessfulReadAtUtc)} accent="neutral" detail={runtime?.mappingVersion ?? "Sem mapeamento"} />
      </section>

      {runtime?.lastError && <div className="error-banner">{runtime.lastError}</div>}

      <section className="dashboard-columns">
        <article className="panel">
          <PanelHeading eyebrow="Ocorrências" title="Alarmes recentes" />
          <div className="event-list">
            {alarms.length === 0 && <EmptyState text="Nenhum alarme registrado." />}
            {alarms.map((alarm) => (
              <div className="event-row" key={alarm.id}>
                <span className={`event-icon alarm${alarm.clearedAtUtc ? " cleared" : ""}`}>!</span>
                <div>
                  <b>{formatFieldName(alarm.alarmName)}</b>
                  <span>{formatDate(alarm.activatedAtUtc)}</span>
                </div>
                <em>{alarm.clearedAtUtc ? "Normalizado" : "Ativo"}</em>
              </div>
            ))}
          </div>
        </article>

        <article className="panel">
          <PanelHeading eyebrow="Auditoria" title="Comandos recentes" />
          <div className="event-list">
            {commands.length === 0 && <EmptyState text="Nenhuma mudança de comando registrada." />}
            {commands.map((command) => (
              <div className="event-row" key={command.id}>
                <span className="event-icon command">↳</span>
                <div>
                  <b>{formatFieldName(command.commandName)}</b>
                  <span>{formatDate(command.observedAtUtc)}</span>
                </div>
                <em>{parseStoredValue(command.currentValueJson)}</em>
              </div>
            ))}
          </div>
        </article>
      </section>
    </>
  );
}

function MetricCard({
  label,
  value,
  detail,
  accent,
}: {
  label: string;
  value: string;
  detail: string;
  accent: "green" | "red" | "blue" | "neutral";
}) {
  return (
    <article className={`metric-card metric-card--${accent}`}>
      <span>{label}</span>
      <strong>{value}</strong>
      <small>{detail}</small>
    </article>
  );
}

function ConnectionBadge({ connected }: { connected: boolean }) {
  return (
    <div className={`connection-badge${connected ? " online" : ""}`}>
      <i />
      {connected ? "Aquisição online" : "Aquisição offline"}
    </div>
  );
}

function PanelHeading({ eyebrow, title }: { eyebrow: string; title: string }) {
  return (
    <div className="panel-heading">
      <div><p className="eyebrow">{eyebrow}</p><h2>{title}</h2></div>
    </div>
  );
}

function EmptyState({ text }: { text: string }) {
  return <div className="empty-state">{text}</div>;
}

function CurrentStatus({ current }: { current: CurrentSnapshot | null }) {
  const [search, setSearch] = useState("");
  const fields = useMemo(
    () => Object.entries(current?.status ?? {}).filter(([name]) =>
      name.toLowerCase().includes(search.trim().toLowerCase())),
    [current, search],
  );

  return (
    <>
      <ScreenTitle
        eyebrow="Tempo real"
        title="Status atual"
        subtitle={`Último snapshot válido: ${formatDate(current?.capturedAtUtc)}`}
        action={<span className="result-count">{fields.length} variáveis</span>}
      />
      <div className="query-toolbar">
        <label className="search-box">
          <span>Buscar variável</span>
          <input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Velocidade, torque, estado…" />
        </label>
      </div>
      <section className="status-cards">
        {fields.map(([name, value]) => (
          <article key={name}>
            <span title={name}>{formatFieldName(name)}</span>
            <strong className={value === true ? "value-active" : value === false ? "value-inactive" : ""}>{formatValue(value)}</strong>
            <small>{name}</small>
          </article>
        ))}
      </section>
    </>
  );
}

function MotorGraphs() {
  const range = useDefaultRange();
  const [trend, setTrend] = useState<MotorTrend>({ motors: [], samples: [] });
  const [selectedMotor, setSelectedMotor] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  const query = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const parameters = rangeQuery(range.from, range.to);
      parameters.delete("limit");
      parameters.set("maxPoints", "1200");
      const result = await fetchJson<MotorTrend>(`/api/history/motors?${parameters}`);
      setTrend(result);
      setSelectedMotor((currentSelection) =>
        result.motors.some((motor) => motor.key === currentSelection)
          ? currentSelection
          : result.motors[0]?.key ?? "",
      );
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Consulta falhou.");
    } finally {
      setLoading(false);
    }
  }, [range.from, range.to]);

  useEffect(() => { void query(); }, [query]);

  const motor = trend.motors.find((item) => item.key === selectedMotor);
  const speedRows = trend.samples
    .map((sample) => [sample.capturedAtUtc, sample.values[selectedMotor]?.speed] as const)
    .filter((row): row is readonly [string, number] => typeof row[1] === "number");
  const torqueRows = trend.samples
    .map((sample) => [sample.capturedAtUtc, sample.values[selectedMotor]?.torque] as const)
    .filter((row): row is readonly [string, number] => typeof row[1] === "number");
  const applyPeriod = (hours: number) => {
    const now = new Date();
    const formatInput = (date: Date) => {
      const offset = date.getTimezoneOffset();
      return new Date(date.getTime() - offset * 60_000).toISOString().slice(0, 16);
    };
    range.setTo(formatInput(now));
    range.setFrom(formatInput(new Date(now.getTime() - hours * 60 * 60 * 1000)));
  };

  return (
    <>
      <ScreenTitle
        eyebrow="Tendências de acionamentos"
        title="Velocidade e torque"
        subtitle="Dados históricos obtidos dos snapshots de status, com zoom e navegação temporal."
        action={<span className="result-count">{trend.samples.length} amostras</span>}
      />

      <div className="period-presets" aria-label="Períodos rápidos">
        <button type="button" onClick={() => applyPeriod(1)}>1 hora</button>
        <button type="button" onClick={() => applyPeriod(8)}>8 horas</button>
        <button type="button" onClick={() => applyPeriod(24)}>24 horas</button>
        <button type="button" onClick={() => applyPeriod(24 * 7)}>7 dias</button>
      </div>

      <div className="query-toolbar graph-toolbar">
        <label className="motor-select">
          <span>Motor</span>
          <select
            value={selectedMotor}
            onChange={(event) => setSelectedMotor(event.target.value)}
            disabled={trend.motors.length === 0}
          >
            {trend.motors.map((item) => (
              <option value={item.key} key={item.key}>{formatFieldName(item.key)}</option>
            ))}
          </select>
        </label>
        <label><span>De</span><input type="datetime-local" value={range.from} onChange={(event) => range.setFrom(event.target.value)} /></label>
        <label><span>Até</span><input type="datetime-local" value={range.to} onChange={(event) => range.setTo(event.target.value)} /></label>
        <button type="button" className="primary-button" onClick={() => void query()} disabled={loading}>
          {loading ? "Consultando…" : "Atualizar"}
        </button>
      </div>

      {error && <div className="error-banner">{error}</div>}
      {!loading && trend.motors.length === 0 && (
        <div className="graph-empty">
          Nenhum par de velocidade e torque foi encontrado no período selecionado.
        </div>
      )}
      {motor && (
        <section className="trend-grid">
          <TrendChart
            title="Velocidade"
            field={motor.speedField}
            unit={formatTrendUnit(motor.speedUnit)}
            color="#55a2ff"
            rows={speedRows}
          />
          <TrendChart
            title="Torque"
            field={motor.torqueField}
            unit={formatTrendUnit(motor.torqueUnit)}
            color="#e8aa55"
            rows={torqueRows}
          />
        </section>
      )}
      <p className="graph-note">
        Resolução atual: um snapshot a cada 10 segundos. “Unidade PLC” indica que a
        engenharia do campo ainda precisa ser confirmada como %, Nm, rpm ou outra unidade.
      </p>
    </>
  );
}

function formatTrendUnit(unit: string) {
  return unit === "PLC" ? "unidade PLC" : unit;
}

function TrendChart({
  title,
  field,
  unit,
  color,
  rows,
}: {
  title: string;
  field: string;
  unit: string;
  color: string;
  rows: ReadonlyArray<readonly [string, number]>;
}) {
  const values = rows.map((row) => row[1]);
  const minimum = values.length ? Math.min(...values) : 0;
  const maximum = values.length ? Math.max(...values) : 0;
  const average = values.length
    ? values.reduce((sum, value) => sum + value, 0) / values.length
    : 0;
  const option = useMemo<InteractiveChartOption>(() => ({
    backgroundColor: "transparent",
    animation: false,
    grid: { left: 64, right: 25, top: 26, bottom: 72 },
    tooltip: {
      trigger: "axis",
      axisPointer: { type: "cross" },
      valueFormatter: (value: unknown) => `${Number(value).toFixed(2)} ${unit}`,
    },
    xAxis: {
      type: "time",
      axisLabel: { hideOverlap: true },
      splitLine: { show: false },
    },
    yAxis: {
      type: "value",
      scale: true,
      name: unit,
      splitLine: { lineStyle: { color: "#26364d" } },
    },
    dataZoom: [
      { type: "inside", zoomOnMouseWheel: true, moveOnMouseMove: true },
      {
        type: "slider",
        height: 24,
        bottom: 16,
        borderColor: "#34445c",
        fillerColor: `${color}44`,
      },
    ],
    series: [{
      type: "line",
      name: title,
      showSymbol: false,
      sampling: "lttb",
      connectNulls: false,
      data: rows,
      lineStyle: { width: 2, color },
      areaStyle: { color: `${color}1f` },
    }],
  }), [color, rows, title, unit]);

  return (
    <article className="trend-card">
      <div className="trend-heading">
        <div><p className="eyebrow">{field}</p><h2>{title}</h2></div>
        <div className="trend-stats">
          <span><small>Mín.</small><b>{minimum.toFixed(2)}</b></span>
          <span><small>Média</small><b>{average.toFixed(2)}</b></span>
          <span><small>Máx.</small><b>{maximum.toFixed(2)}</b></span>
        </div>
      </div>
      {rows.length
        ? (
          <Suspense fallback={<EmptyState text="Preparando gráfico…" />}>
            <InteractiveChart option={option} ariaLabel={`${title} do motor ao longo do período`} />
          </Suspense>
        )
        : <EmptyState text={`Sem amostras de ${title.toLowerCase()} no período.`} />}
    </article>
  );
}

function RangeFilters({
  from,
  to,
  search,
  onFrom,
  onTo,
  onSearch,
  onQuery,
  children,
}: {
  from: string;
  to: string;
  search: string;
  onFrom: (value: string) => void;
  onTo: (value: string) => void;
  onSearch: (value: string) => void;
  onQuery: () => void;
  children?: ReactNode;
}) {
  return (
    <div className="query-toolbar query-toolbar--range">
      <label><span>De</span><input type="datetime-local" value={from} onChange={(event) => onFrom(event.target.value)} /></label>
      <label><span>Até</span><input type="datetime-local" value={to} onChange={(event) => onTo(event.target.value)} /></label>
      <label className="search-box"><span>Filtrar resultado</span><input value={search} onChange={(event) => onSearch(event.target.value)} placeholder="Nome da variável…" /></label>
      {children}
      <button type="button" className="primary-button" onClick={onQuery}>Consultar</button>
    </div>
  );
}

function useDefaultRange() {
  const formatInput = (date: Date) => {
    const offset = date.getTimezoneOffset();
    return new Date(date.getTime() - offset * 60_000).toISOString().slice(0, 16);
  };
  const now = new Date();
  const [from, setFrom] = useState(formatInput(new Date(now.getTime() - 24 * 60 * 60 * 1000)));
  const [to, setTo] = useState(formatInput(now));
  return { from, to, setFrom, setTo };
}

function rangeQuery(from: string, to: string) {
  const parameters = new URLSearchParams({ limit: "1000" });
  if (from) parameters.set("fromUtc", new Date(from).toISOString());
  if (to) parameters.set("toUtc", new Date(to).toISOString());
  return parameters;
}

function AlarmHistory() {
  const range = useDefaultRange();
  const [search, setSearch] = useState("");
  const [state, setState] = useState("all");
  const [rows, setRows] = useState<AlarmEvent[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  const query = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const parameters = rangeQuery(range.from, range.to);
      if (state !== "all") parameters.set("active", String(state === "active"));
      setRows(await fetchJson<AlarmEvent[]>(`/api/history/alarms?${parameters}`));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Consulta falhou.");
    } finally {
      setLoading(false);
    }
  }, [range.from, range.to, state]);

  useEffect(() => { void query(); }, [query]);
  const filtered = rows.filter((row) => row.alarmName.toLowerCase().includes(search.toLowerCase()));

  return (
    <>
      <ScreenTitle eyebrow="Consulta de eventos" title="Histórico de alarmes" subtitle="Ativações, normalizações e duração calculada de cada ocorrência." action={<span className="result-count">{filtered.length} registros</span>} />
      <RangeFilters from={range.from} to={range.to} search={search} onFrom={range.setFrom} onTo={range.setTo} onSearch={setSearch} onQuery={() => void query()}>
        <label><span>Estado</span><select value={state} onChange={(event) => setState(event.target.value)}><option value="all">Todos</option><option value="active">Ativos</option><option value="cleared">Normalizados</option></select></label>
      </RangeFilters>
      {error && <div className="error-banner">{error}</div>}
      <DataTable headers={["Alarme", "Ativação", "Normalização", "Duração", "Estado"]} loading={loading} empty={filtered.length === 0}>
        {filtered.map((alarm) => (
          <div className="data-row alarm-row" key={alarm.id}>
            <div><b>{formatFieldName(alarm.alarmName)}</b><small>{alarm.alarmName}</small></div>
            <span>{formatDate(alarm.activatedAtUtc)}</span>
            <span>{formatDate(alarm.clearedAtUtc)}</span>
            <span>{formatDuration(alarm.durationMilliseconds)}</span>
            <span className={`state-pill${alarm.clearedAtUtc ? "" : " active"}`}>{alarm.clearedAtUtc ? "Normalizado" : "Ativo"}</span>
          </div>
        ))}
      </DataTable>
    </>
  );
}

function CommandHistory() {
  const range = useDefaultRange();
  const [search, setSearch] = useState("");
  const [rows, setRows] = useState<CommandEvent[]>([]);
  const [loading, setLoading] = useState(false);

  const query = useCallback(async () => {
    setLoading(true);
    try {
      setRows(await fetchJson<CommandEvent[]>(`/api/history/commands?${rangeQuery(range.from, range.to)}`));
    } finally {
      setLoading(false);
    }
  }, [range.from, range.to]);

  useEffect(() => { void query(); }, [query]);
  const filtered = rows.filter((row) => row.commandName.toLowerCase().includes(search.toLowerCase()));

  return (
    <>
      <ScreenTitle eyebrow="Auditoria somente leitura" title="Datalog de comandos" subtitle="Mudanças observadas na estrutura paperMachineHmiCommands." action={<span className="result-count">{filtered.length} registros</span>} />
      <RangeFilters from={range.from} to={range.to} search={search} onFrom={range.setFrom} onTo={range.setTo} onSearch={setSearch} onQuery={() => void query()} />
      <DataTable headers={["Comando", "Valor anterior", "Novo valor", "Horário", "Origem"]} loading={loading} empty={filtered.length === 0}>
        {filtered.map((command) => (
          <div className="data-row command-row" key={command.id}>
            <div><b>{formatFieldName(command.commandName)}</b><small>{command.commandName}</small></div>
            <span>{parseStoredValue(command.previousValueJson)}</span>
            <span className="changed-value">{parseStoredValue(command.currentValueJson)}</span>
            <span>{formatDate(command.observedAtUtc)}</span>
            <span>PLC observado</span>
          </div>
        ))}
      </DataTable>
    </>
  );
}

function StatusHistory() {
  const range = useDefaultRange();
  const [search, setSearch] = useState("");
  const [rows, setRows] = useState<StatusChange[]>([]);
  const [loading, setLoading] = useState(false);

  const query = useCallback(async () => {
    setLoading(true);
    try {
      setRows(await fetchJson<StatusChange[]>(`/api/history/status-changes?${rangeQuery(range.from, range.to)}`));
    } finally {
      setLoading(false);
    }
  }, [range.from, range.to]);

  useEffect(() => { void query(); }, [query]);
  const filtered = rows.filter((row) => row.fieldName.toLowerCase().includes(search.toLowerCase()));

  return (
    <>
      <ScreenTitle eyebrow="Datalog de processo" title="Histórico de status" subtitle="Alterações detectadas entre snapshots válidos do PLC." action={<span className="result-count">{filtered.length} registros</span>} />
      <RangeFilters from={range.from} to={range.to} search={search} onFrom={range.setFrom} onTo={range.setTo} onSearch={setSearch} onQuery={() => void query()} />
      <DataTable headers={["Variável", "Valor anterior", "Novo valor", "Horário"]} loading={loading} empty={filtered.length === 0} compact>
        {filtered.map((change) => (
          <div className="data-row status-row" key={change.id}>
            <div><b>{formatFieldName(change.fieldName)}</b><small>{change.fieldName}</small></div>
            <span>{parseStoredValue(change.previousValueJson)}</span>
            <span className="changed-value">{parseStoredValue(change.currentValueJson)}</span>
            <span>{formatDate(change.observedAtUtc)}</span>
          </div>
        ))}
      </DataTable>
    </>
  );
}

function DataTable({
  headers,
  loading,
  empty,
  compact = false,
  children,
}: {
  headers: string[];
  loading: boolean;
  empty: boolean;
  compact?: boolean;
  children: ReactNode;
}) {
  return (
    <section className={`data-table${compact ? " data-table--compact" : ""}`}>
      <div className="data-head">{headers.map((header) => <span key={header}>{header}</span>)}</div>
      {loading ? <EmptyState text="Consultando histórico…" /> : empty ? <EmptyState text="Nenhum registro encontrado no período." /> : children}
    </section>
  );
}
