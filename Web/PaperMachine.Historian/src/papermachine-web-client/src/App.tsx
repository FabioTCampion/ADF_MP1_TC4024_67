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
  displayName: string;
  description: string;
  recommendedAction: string;
  severity: string;
  area: string;
  catalogVersion: string;
  activatedAtUtc: string;
  clearedAtUtc: string | null;
  durationMilliseconds: number | null;
  activeAtStartup: boolean;
  driveModel: string | null;
  driveFaultCode: number | null;
  driveFaultCodeHex: string | null;
  driveFaultMnemonic: string | null;
  driveFaultTitle: string | null;
  driveFaultDescription: string | null;
  driveRecommendedAction: string | null;
  driveFaultTorque: number | null;
  driveFaultEventCounter: number | null;
  manualReference: string | null;
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

const machineSpeedField = "dryingSectionGroup3UpperMasterSpeedMPM";
const paperPresenceField = "dryingSectionGroup3PaperPresence";

const formatDate = (value: string | null | undefined) =>
  value ? dateFormatter.format(new Date(value)) : "—";

const readStatusNumber = (
  current: CurrentSnapshot | null,
  fieldName: string,
) => {
  const value = current?.status[fieldName];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
};

const readStatusBoolean = (
  current: CurrentSnapshot | null,
  fieldName: string,
) => {
  const value = current?.status[fieldName];
  return typeof value === "boolean" ? value : null;
};

const formatMachineSpeed = (speed: number | null) =>
  speed === null ? "—" : speed.toLocaleString("pt-BR", {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
  });

const paperPresenceLabel = (paperPresent: boolean | null) => {
  if (paperPresent === null) return "Sem leitura";
  return paperPresent ? "Papel presente" : "Sem papel";
};

const paperPresenceClass = (paperPresent: boolean | null) => {
  if (paperPresent === null) return "unknown";
  return paperPresent ? "present" : "absent";
};

const formatDuration = (milliseconds: number | null) => {
  if (milliseconds === null) return "Em andamento";
  const totalSeconds = Math.max(0, Math.round(milliseconds / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return [hours && `${hours}h`, minutes && `${minutes}min`, `${seconds}s`].filter(Boolean).join(" ");
};

const formatFaultCode = (alarm: AlarmEvent) => {
  if (alarm.driveFaultCode === null) return "—";
  const mnemonic = alarm.driveFaultMnemonic ? ` · ${alarm.driveFaultMnemonic}` : "";
  return `${alarm.driveFaultCodeHex ?? alarm.driveFaultCode}${mnemonic}`;
};

const formatFaultTorque = (alarm: AlarmEvent) =>
  alarm.driveFaultTorque === null ? "—" : `${alarm.driveFaultTorque.toFixed(2)} (unidade PLC)`;

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
  const machineSpeed = readStatusNumber(current, machineSpeedField);
  const paperPresent = readStatusBoolean(current, paperPresenceField);

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
          <div className="top-machine-state" aria-label="Estado atual da máquina">
            <div className="top-machine-speed">
              <span>Velocidade atual</span>
              <b>
                {formatMachineSpeed(machineSpeed)}
                <small>m/min</small>
              </b>
            </div>
            <div className={`top-paper-state top-paper-state--${paperPresenceClass(paperPresent)}`}>
              <i />
              <div>
                <span>Sensor de papel</span>
                <b>{paperPresenceLabel(paperPresent)}</b>
              </div>
            </div>
          </div>
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
  const machineSpeed = readStatusNumber(current, machineSpeedField);
  const paperPresent = readStatusBoolean(current, paperPresenceField);

  return (
    <>
      <ScreenTitle
        eyebrow="Operação em tempo real"
        title="Visão geral"
        subtitle="Resumo da aquisição ADS e dos eventos mais recentes da máquina."
        action={<ConnectionBadge connected={runtime?.adsConnected ?? false} />}
      />

      <section className="machine-overview-card" aria-label="Estado operacional da máquina">
        <div className="machine-overview-heading">
          <p className="eyebrow">Produção em tempo real</p>
          <h2>Estado atual da máquina</h2>
          <span>Leitura do terceiro grupo de secagem</span>
        </div>
        <div className="machine-speed-readout">
          <span>Velocidade atual</span>
          <strong>{formatMachineSpeed(machineSpeed)}</strong>
          <small>m/min</small>
        </div>
        <div className={`machine-paper-readout machine-paper-readout--${paperPresenceClass(paperPresent)}`}>
          <i />
          <div>
            <span>Presença de papel</span>
            <strong>{paperPresenceLabel(paperPresent)}</strong>
            <small>Sensor do terceiro grupo</small>
          </div>
        </div>
      </section>

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
                  <b>{alarm.displayName}</b>
                  <span>
                    {formatDate(alarm.activatedAtUtc)}
                    {alarm.driveFaultCode !== null ? ` · ${formatFaultCode(alarm)}` : ""}
                    {alarm.driveFaultTorque !== null ? ` · torque ${formatFaultTorque(alarm)}` : ""}
                  </span>
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

type MotorGroup = {
  key: string;
  label: string;
  motors: MotorTrendMotor[];
};

type TrendSeries = {
  key: string;
  label: string;
  field: string;
  unit: string;
  color: string;
  rows: ReadonlyArray<readonly [string, number]>;
};

const trendColors = [
  "#55a2ff",
  "#e8aa55",
  "#56d4a0",
  "#c48cff",
  "#ff7185",
  "#4fd2e7",
  "#b6cf62",
  "#ef8ed7",
] as const;

const motorGroupCatalog = [
  { key: "drying-1", label: "Secagem — Grupo 1", matches: (key: string) => key.startsWith("dryingSectionGroup1") },
  { key: "drying-2", label: "Secagem — Grupo 2", matches: (key: string) => key.startsWith("dryingSectionGroup2") },
  { key: "drying-3", label: "Secagem — Grupo 3", matches: (key: string) => key.startsWith("dryingSectionGroup3") },
  { key: "forming", label: "Mesa formadora", matches: (key: string) => key.startsWith("formingBoard") },
  { key: "presses", label: "Prensas", matches: (key: string) => key.endsWith("PressSection") },
  { key: "pumps", label: "Bombas de processo", matches: (key: string) => key.endsWith("Pump") },
  { key: "winder", label: "Enroladeira", matches: (key: string) => key.startsWith("winder") },
] as const;

function buildMotorGroups(motors: MotorTrendMotor[]): MotorGroup[] {
  const assigned = new Set<string>();
  const groups: MotorGroup[] = motorGroupCatalog
    .map((group) => {
      const groupMotors = motors.filter((motor) => group.matches(motor.key));
      groupMotors.forEach((motor) => assigned.add(motor.key));
      return { key: group.key, label: group.label, motors: groupMotors };
    })
    .filter((group) => group.motors.length > 0);
  const remaining = motors.filter((motor) => !assigned.has(motor.key));
  if (remaining.length > 0) {
    groups.push({ key: "others", label: "Outros acionamentos", motors: remaining });
  }
  return groups;
}

function formatMotorMember(key: string) {
  const member = key
    .replace(/^dryingSectionGroup\d/, "")
    .replace(/^formingBoard/, "")
    .replace(/Section$/, "")
    .replace(/^winder$/, "Enroladeira")
    .replace(/^Upper/, "Superior")
    .replace(/^Lower/, "Inferior")
    .replace(/Master/, " Mestre")
    .replace(/Slave(\d)/, " Escravo $1");
  return formatFieldName(member);
}

function MotorGraphs() {
  const range = useDefaultRange();
  const [trend, setTrend] = useState<MotorTrend>({ motors: [], samples: [] });
  const [selectedGroup, setSelectedGroup] = useState("drying-1");
  const [selectedMotors, setSelectedMotors] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  const query = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const parameters = rangeQuery(range.from, range.to);
      parameters.delete("limit");
      parameters.set("maxPoints", "1200");
      setTrend(await fetchJson<MotorTrend>(`/api/history/motors?${parameters}`));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Consulta falhou.");
    } finally {
      setLoading(false);
    }
  }, [range.from, range.to]);

  useEffect(() => { void query(); }, [query]);

  const groups = useMemo(() => buildMotorGroups(trend.motors), [trend.motors]);
  const activeGroup = groups.find((group) => group.key === selectedGroup) ?? groups[0];

  useEffect(() => {
    if (!activeGroup) return;
    if (activeGroup.key !== selectedGroup) setSelectedGroup(activeGroup.key);
    setSelectedMotors((current) => {
      const validSelection = current.filter((key) =>
        activeGroup.motors.some((motor) => motor.key === key));
      return validSelection.length > 0
        ? validSelection
        : activeGroup.motors.map((motor) => motor.key);
    });
  }, [activeGroup, selectedGroup]);

  const toggleMotor = (key: string) => {
    setSelectedMotors((current) =>
      current.includes(key)
        ? current.filter((selectedKey) => selectedKey !== key)
        : [...current, key]);
  };
  const changeGroup = (key: string) => {
    const group = groups.find((item) => item.key === key);
    setSelectedGroup(key);
    setSelectedMotors(group?.motors.map((motor) => motor.key) ?? []);
  };
  const buildSeries = (metric: "speed" | "torque"): TrendSeries[] =>
    (activeGroup?.motors ?? [])
      .map((motor, index) => ({
        key: motor.key,
        label: formatMotorMember(motor.key),
        field: metric === "speed" ? motor.speedField : motor.torqueField,
        unit: formatTrendUnit(metric === "speed" ? motor.speedUnit : motor.torqueUnit),
        color: trendColors[index % trendColors.length],
        rows: trend.samples
          .map((sample) =>
            [sample.capturedAtUtc, sample.values[motor.key]?.[metric]] as const)
          .filter((row): row is readonly [string, number] => typeof row[1] === "number"),
      }))
      .filter((series) => selectedMotors.includes(series.key));
  const speedSeries = buildSeries("speed");
  const torqueSeries = buildSeries("torque");
  const latestSample = trend.samples[trend.samples.length - 1];
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
        subtitle="Compare os motores de cada grupo no mesmo eixo e oculte séries individualmente."
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
          <span>Grupo</span>
          <select
            value={activeGroup?.key ?? ""}
            onChange={(event) => changeGroup(event.target.value)}
            disabled={groups.length === 0}
          >
            {groups.map((group) => (
              <option value={group.key} key={group.key}>
                {group.label} · {group.motors.length} {group.motors.length === 1 ? "motor" : "motores"}
              </option>
            ))}
          </select>
        </label>
        <label><span>De</span><input type="datetime-local" value={range.from} onChange={(event) => range.setFrom(event.target.value)} /></label>
        <label><span>Até</span><input type="datetime-local" value={range.to} onChange={(event) => range.setTo(event.target.value)} /></label>
        <button type="button" className="primary-button" onClick={() => void query()} disabled={loading}>
          {loading ? "Consultando…" : "Atualizar"}
        </button>
      </div>

      {activeGroup && (
        <section className="motor-selection">
          <div className="motor-selection-heading">
            <div>
              <p className="eyebrow">Séries visíveis</p>
              <h2>{activeGroup.label}</h2>
            </div>
            <div>
              <button type="button" onClick={() => setSelectedMotors(activeGroup.motors.map((motor) => motor.key))}>Todos</button>
              <button type="button" onClick={() => setSelectedMotors([])}>Nenhum</button>
            </div>
          </div>
          <div className="motor-checks">
            {activeGroup.motors.map((motor, index) => {
              const currentValue = latestSample?.values[motor.key];
              return (
                <label key={motor.key}>
                  <input
                    type="checkbox"
                    checked={selectedMotors.includes(motor.key)}
                    onChange={() => toggleMotor(motor.key)}
                  />
                  <i
                    style={{
                      backgroundColor: trendColors[index % trendColors.length],
                      color: trendColors[index % trendColors.length],
                    }}
                  />
                  <span>
                    <b>{formatMotorMember(motor.key)}</b>
                    <small>
                      V {currentValue?.speed?.toFixed(1) ?? "—"} · T {currentValue?.torque?.toFixed(1) ?? "—"}
                    </small>
                  </span>
                </label>
              );
            })}
          </div>
        </section>
      )}

      {error && <div className="error-banner">{error}</div>}
      {!loading && trend.motors.length === 0 && (
        <div className="graph-empty">
          Nenhum par de velocidade e torque foi encontrado no período selecionado.
        </div>
      )}
      {activeGroup && (
        <section className="trend-grid">
          <TrendChart title="Velocidade" series={speedSeries} />
          <TrendChart title="Torque" series={torqueSeries} />
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

function TrendChart({ title, series }: { title: string; series: TrendSeries[] }) {
  const units = [...new Set(series.map((item) => item.unit))];
  const axisUnit = units.length === 1 ? units[0] : "valor de processo";
  const option = useMemo<InteractiveChartOption>(() => ({
    backgroundColor: "transparent",
    animation: false,
    color: series.map((item) => item.color),
    grid: { left: 64, right: 25, top: 26, bottom: 72 },
    tooltip: {
      trigger: "axis",
      axisPointer: { type: "cross" },
      valueFormatter: (value: unknown) => `${Number(value).toFixed(2)} ${axisUnit}`,
    },
    xAxis: {
      type: "time",
      axisLabel: { hideOverlap: true },
      splitLine: { show: false },
    },
    yAxis: {
      type: "value",
      scale: true,
      name: axisUnit,
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
    series: series.map((item) => ({
      type: "line",
      name: item.label,
      showSymbol: false,
      sampling: "lttb",
      connectNulls: false,
      data: item.rows,
      lineStyle: { width: 1.8, color: item.color },
      emphasis: { focus: "series", lineStyle: { width: 3 } },
    })),
  }), [axisUnit, series]);

  return (
    <article className="trend-card">
      <div className="trend-heading">
        <div>
          <p className="eyebrow">{series.length} séries visíveis</p>
          <h2>{title}</h2>
        </div>
        <div className="trend-legend">
          {series.map((item) => (
            <span key={item.key}><i style={{ backgroundColor: item.color }} />{item.label}</span>
          ))}
        </div>
      </div>
      {series.some((item) => item.rows.length > 0)
        ? (
          <Suspense fallback={<EmptyState text="Preparando gráfico…" />}>
            <InteractiveChart option={option} ariaLabel={`${title} dos motores do grupo ao longo do período`} />
          </Suspense>
        )
        : <EmptyState text="Selecione ao menos um motor com amostras no período." />}
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
  const filtered = rows.filter((row) => {
    const term = search.toLowerCase();
    return row.alarmName.toLowerCase().includes(term) ||
      row.displayName.toLowerCase().includes(term) ||
      row.description.toLowerCase().includes(term) ||
      row.driveFaultTitle?.toLowerCase().includes(term);
  });

  return (
    <>
      <ScreenTitle eyebrow="Consulta de eventos" title="Histórico de alarmes" subtitle="Ativações, normalizações e duração calculada de cada ocorrência." action={<span className="result-count">{filtered.length} registros</span>} />
      <RangeFilters from={range.from} to={range.to} search={search} onFrom={range.setFrom} onTo={range.setTo} onSearch={setSearch} onQuery={() => void query()}>
        <label><span>Estado</span><select value={state} onChange={(event) => setState(event.target.value)}><option value="all">Todos</option><option value="active">Ativos</option><option value="cleared">Normalizados</option></select></label>
      </RangeFilters>
      {error && <div className="error-banner">{error}</div>}
      <DataTable headers={["Alarme", "Diagnóstico C2000 Plus", "Ativação", "Normalização", "Duração", "Estado"]} loading={loading} empty={filtered.length === 0} layout="alarm">
        {filtered.map((alarm) => (
          <div className="data-row alarm-row" key={alarm.id}>
            <div className="alarm-copy">
              <div className="alarm-title-line">
                <b>{alarm.displayName}</b>
                <span className={`severity-badge severity-${alarm.severity.toLowerCase()}`}>{alarm.severity}</span>
              </div>
              <small>{alarm.area} · variável: {alarm.alarmName}</small>
              <p>{alarm.description}</p>
              <em>Ação: {alarm.recommendedAction}</em>
            </div>
            <div className="drive-diagnostic">
              {alarm.driveFaultCode === null
                ? <span className="no-diagnostic">Sem diagnóstico de drive associado</span>
                : (
                  <>
                    <b>{formatFaultCode(alarm)}</b>
                    <strong>{alarm.driveFaultTitle}</strong>
                    <span>{alarm.driveFaultDescription}</span>
                    <span>Torque na falha: <em>{formatFaultTorque(alarm)}</em></span>
                    <small>{alarm.driveModel} · evento #{alarm.driveFaultEventCounter}</small>
                    <small>{alarm.manualReference}</small>
                    <p>Ação do manual: {alarm.driveRecommendedAction}</p>
                  </>
                )}
            </div>
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
  layout,
  children,
}: {
  headers: string[];
  loading: boolean;
  empty: boolean;
  compact?: boolean;
  layout?: "alarm";
  children: ReactNode;
}) {
  return (
    <section className={`data-table${compact ? " data-table--compact" : ""}${layout ? ` data-table--${layout}` : ""}`}>
      <div className="data-head">{headers.map((header) => <span key={header}>{header}</span>)}</div>
      {loading ? <EmptyState text="Consultando histórico…" /> : empty ? <EmptyState text="Nenhum registro encontrado no período." /> : children}
    </section>
  );
}
