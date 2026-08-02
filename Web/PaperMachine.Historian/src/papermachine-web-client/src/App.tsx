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
import HistoryPeriodFilter, {
  useHistoryPeriod,
} from "./HistoryPeriodFilter";
import type { InteractiveChartOption } from "./InteractiveChart";
import { operatorVariableLabel } from "./OperatorTranslations";
import SideMenu, { type NavigationItem, type PageId } from "./SideMenu";
import { ServerClockProvider } from "./ServerClock";

const InteractiveChart = lazy(() => import("./InteractiveChart"));
const MetricsScreen = lazy(() => import("./MetricsScreen"));
const BreakAnalysisScreen = lazy(() => import("./BreakAnalysisScreen"));
const GraphWorkspace = lazy(() => import("./GraphWorkspace"));
const UsersScreen = lazy(() => import("./UsersScreen"));
const UpdatesScreen = lazy(() => import("./UpdatesScreen"));

type RuntimeStatus = {
  adsConnected: boolean;
  serverTimeUtc: string;
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

type HistoryPage<T> = {
  items: T[];
  total: number;
  offset: number;
  limit: number;
  hasMore: boolean;
};

type StorageStatus = {
  databaseBytes: number;
  walBytes: number;
  sharedMemoryBytes: number;
  reusableBytes: number;
  freeDiskBytes: number;
  totalDiskBytes: number;
  telemetrySampleCount: number;
  diagnosticSnapshotCount: number;
  statusChangeCount: number;
  oldestTelemetryAtUtc: string | null;
  newestTelemetryAtUtc: string | null;
  lastMaintenanceAtUtc: string | null;
};

type ProductionItem = {
  position: number;
  customerName: string | null;
  orderCode: string | null;
  productCode: string | null;
  format: number | null;
  diameter: number | null;
  grammageGsm: number | null;
  plannedQuantityKg: number | null;
  producedQuantityKg: number | null;
};

type ProductionReference = {
  position: number;
  referenceType: string;
  referenceValue: string;
};

type ProductionIntegration = {
  enabled: boolean;
  configured: boolean;
  sourceSystem: string;
  status: "Disabled" | "NeverSynced" | "Online" | "Stale" | "Faulted";
  stale: boolean;
  lastAttemptAtUtc: string | null;
  lastSuccessfulSyncAtUtc: string | null;
  lastError: string | null;
  currentRun: {
    externalRunId: string;
    productionOrderCode: string | null;
    machineCode: string | null;
    isProducing: boolean;
    expectedEndAtUtc: string | null;
    lastObservedAtUtc: string;
    qualityKey: string | null;
    qualityProductCode: string | null;
    qualityGrammageGsm: number | null;
    isMixedQuality: boolean;
    items: ProductionItem[];
    references: ProductionReference[];
  } | null;
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
  steamPressures: Record<string, number | null>;
};

type MotorTrend = {
  motors: MotorTrendMotor[];
  steamPressures: {
    key: string;
    label: string;
    field: string;
    unit: string;
  }[];
  samples: MotorTrendSample[];
};

const navigation: readonly NavigationItem[] = [
  { id: "dashboard", label: "Visão geral", icon: "dashboard" },
  { id: "metrics", label: "Métricas", icon: "metrics" },
  { id: "breaks", label: "Análise de quebras", icon: "breaks" },
  { id: "status", label: "Status atual", icon: "status" },
  { id: "graphs", label: "Gráficos", icon: "graphs" },
  { id: "alarms", label: "Alarmes", icon: "alarm" },
  { id: "commands", label: "Comandos", icon: "command" },
  { id: "history", label: "Histórico de status", icon: "history" },
  { id: "users", label: "Usuários", icon: "users" },
  { id: "updates", label: "Atualizações", icon: "updates" },
];

const dateFormatter = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short",
  timeStyle: "medium",
});

const machineSpeedField = "dryingSectionGroup3UpperMasterSpeedMPM";
const paperPresenceField = "dryingSectionGroup3PaperPresence";
const stockPumpStateField = "stockPumpState";
const stockPumpRunningState = 1;
const steamPressureCatalog = [
  { key: "drying-1", shortLabel: "G1", label: "Grupo 1", field: "dryingSectionGroup1SteamPressure" },
  { key: "drying-2", shortLabel: "G2", label: "Grupo 2", field: "dryingSectionGroup2SteamPressure" },
  { key: "drying-3", shortLabel: "G3", label: "Grupo 3", field: "dryingSectionGroup3SteamPressure" },
] as const;
const groupRunningSpeedMpm = 1;

const machineGroupCatalog = [
  {
    key: "forming",
    label: "Mesa formadora",
    speedFields: [
      "formingBoardSuctionRollSpeed",
      "formingBoardTractionRollSpeed",
    ],
  },
  {
    key: "press",
    label: "Prensas",
    speedFields: ["firstPressSectionSpeed", "secondPressSectionSpeed"],
  },
  {
    key: "drying-1",
    label: "Secagem 1",
    speedFields: [
      "dryingSectionGroup1UpperMasterSpeedMPM",
      "dryingSectionGroup1LowerMasterSpeedMPM",
    ],
  },
  {
    key: "drying-2",
    label: "Secagem 2",
    speedFields: [
      "dryingSectionGroup2UpperMasterSpeedMPM",
      "dryingSectionGroup2LowerMasterSpeedMPM",
    ],
  },
  {
    key: "drying-3",
    label: "Secagem 3",
    speedFields: [
      "dryingSectionGroup3UpperMasterSpeedMPM",
      "dryingSectionGroup3LowerMasterSpeedMPM",
    ],
  },
  {
    key: "winder",
    label: "Enroladeira",
    speedFields: ["winderSpeedMPM"],
  },
] as const;

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

const readEffectivePaperPresence = (current: CurrentSnapshot | null) => {
  const sensorPresent = readStatusBoolean(current, paperPresenceField);
  const stockPumpState = readStatusNumber(current, stockPumpStateField);

  if (sensorPresent === false ||
      (stockPumpState !== null && stockPumpState !== stockPumpRunningState)) {
    return false;
  }

  return sensorPresent === true && stockPumpState === stockPumpRunningState
    ? true
    : null;
};

const paperPresenceDetail = (current: CurrentSnapshot | null) => {
  const sensorPresent = readStatusBoolean(current, paperPresenceField);
  const stockPumpState = readStatusNumber(current, stockPumpStateField);

  if (sensorPresent === true && stockPumpState !== null &&
      stockPumpState !== stockPumpRunningState) {
    return stockPumpState === 2
      ? "Sensor G3 ativo · bomba de massa em falha"
      : "Sensor G3 ativo · bomba de massa parada";
  }
  if (sensorPresent === false) return "Sensor do terceiro grupo sem papel";
  if (stockPumpState === stockPumpRunningState) return "Sensor G3 + bomba de massa ligada";
  if (stockPumpState === 2) return "Bomba de massa em falha";
  if (stockPumpState === 0) return "Bomba de massa parada";
  return "Aguardando sensor G3 e bomba de massa";
};

const formatMachineSpeed = (speed: number | null) =>
  speed === null ? "—" : speed.toLocaleString("pt-BR", {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
  });

const formatSteamPressure = (pressure: number | null) =>
  pressure === null ? "—" : pressure.toLocaleString("pt-BR", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });

const formatBytes = (bytes: number | null | undefined) => {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = Math.max(0, bytes);
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toLocaleString("pt-BR", {
    minimumFractionDigits: unit >= 2 ? 1 : 0,
    maximumFractionDigits: unit >= 2 ? 1 : 0,
  })} ${units[unit]}`;
};

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
  const response = await fetch(url, {
    cache: "no-store",
    headers: { Accept: "application/json" },
  });
  if (response.status === 401) {
    window.location.reload();
    throw new Error("Sessão expirada.");
  }
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(body?.error ?? `Consulta falhou (${response.status}).`);
  }
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
  const [storage, setStorage] = useState<StorageStatus | null>(null);
  const [production, setProduction] = useState<ProductionIntegration | null>(null);
  const [clock, setClock] = useState(new Date());
  const serverOffsetMilliseconds = runtime?.serverTimeUtc
    ? new Date(runtime.serverTimeUtc).getTime() - Date.now()
    : null;
  const serverClock = new Date(
    clock.getTime() + (serverOffsetMilliseconds ?? 0),
  );
  const clockDifferenceMinutes = serverOffsetMilliseconds === null
    ? null
    : Math.round(Math.abs(serverOffsetMilliseconds) / 60_000);
  const machineSpeed = readStatusNumber(current, machineSpeedField);
  const paperPresent = readEffectivePaperPresence(current);
  const paperDetail = paperPresenceDetail(current);
  const steamPressures = steamPressureCatalog.map((pressure) => ({
    ...pressure,
    value: readStatusNumber(current, pressure.field),
  }));
  const visibleNavigation = useMemo(
    () => navigation.filter((item) => {
      if (item.id === "users") return user.permissions.includes("users.view");
      if (item.id === "updates") return user.permissions.includes("updates.view");
      return true;
    }),
    [user.permissions],
  );

  useEffect(() => {
    const pageTitle =
      visibleNavigation.find((item) => item.id === page)?.label ?? "Paper Machine";
    document.title = `CPNTeck | ${pageTitle}`;
  }, [page, visibleNavigation]);

  const refreshLiveData = useCallback(async () => {
    const [runtimeResult, currentResult, productionResult] = await Promise.allSettled([
      fetchJson<RuntimeStatus>("/api/runtime"),
      fetchJson<CurrentSnapshot>("/api/current"),
      fetchJson<ProductionIntegration>("/api/production/current"),
    ]);
    if (runtimeResult.status === "fulfilled") setRuntime(runtimeResult.value);
    if (currentResult.status === "fulfilled") setCurrent(currentResult.value);
    if (productionResult.status === "fulfilled") setProduction(productionResult.value);
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

  useEffect(() => {
    const refreshStorage = () => {
      void fetchJson<StorageStatus>("/api/storage")
        .then(setStorage)
        .catch(() => undefined);
    };
    refreshStorage();
    const timer = window.setInterval(refreshStorage, 60_000);
    return () => window.clearInterval(timer);
  }, []);

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
    <ServerClockProvider serverTimeUtc={runtime?.serverTimeUtc}>
    <div className={`app-shell${collapsed ? " app-shell--collapsed" : ""}`}>
      <SideMenu
        currentPage={page}
        items={visibleNavigation}
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
            <b>{visibleNavigation.find((item) => item.id === page)?.label}</b>
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
            <div className="top-steam-pressure">
              <span>Pressão de vapor</span>
              <div className="top-steam-values">
                {steamPressures.map((pressure) => (
                  <b key={pressure.key}>
                    <small>{pressure.shortLabel}</small>
                    {formatSteamPressure(pressure.value)}
                  </b>
                ))}
                <em>bar</em>
              </div>
            </div>
            <div className={`top-paper-state top-paper-state--${paperPresenceClass(paperPresent)}`}>
              <i />
              <div>
                <span>{paperDetail}</span>
                <b>{paperPresenceLabel(paperPresent)}</b>
              </div>
            </div>
          </div>
          <div className="top-clock">
            <span>{serverClock.toLocaleDateString("pt-BR")}</span>
            <b>{serverClock.toLocaleTimeString("pt-BR")}</b>
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
          {clockDifferenceMinutes !== null && clockDifferenceMinutes >= 5 && (
            <div className="clock-warning" role="alert">
              Divergência de relógio detectada: servidor e dispositivo diferem em
              {" "}{clockDifferenceMinutes.toLocaleString("pt-BR")} minutos.
              Os períodos abaixo usam o horário do servidor.
            </div>
          )}
          {page === "dashboard" && (
            <Dashboard
              runtime={runtime}
              current={current}
              storage={storage}
              production={production}
            />
          )}
          {page === "metrics" && (
            <Suspense fallback={<EmptyState text="Preparando métricas…" />}>
              <MetricsScreen />
            </Suspense>
          )}
          {page === "breaks" && (
            <Suspense fallback={<EmptyState text="Preparando análise de quebras…" />}>
              <BreakAnalysisScreen />
            </Suspense>
          )}
          {page === "status" && <CurrentStatus current={current} />}
          {page === "graphs" && (
            <Suspense fallback={<EmptyState text="Preparando gráficos…" />}>
              <GraphWorkspace currentStatus={current?.status ?? {}} />
            </Suspense>
          )}
          {page === "alarms" && <AlarmHistory />}
          {page === "commands" && <CommandHistory />}
          {page === "history" && <StatusHistory />}
          {page === "users" && (
            <Suspense fallback={<EmptyState text="Preparando gestão de usuários…" />}>
              <UsersScreen />
            </Suspense>
          )}
          {page === "updates" && (
            <Suspense fallback={<EmptyState text="Preparando atualizações…" />}>
              <UpdatesScreen />
            </Suspense>
          )}
        </div>
      </main>
    </div>
    </ServerClockProvider>
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
  storage,
  production,
}: {
  runtime: RuntimeStatus | null;
  current: CurrentSnapshot | null;
  storage: StorageStatus | null;
  production: ProductionIntegration | null;
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
  const paperPresent = readEffectivePaperPresence(current);
  const paperDetail = paperPresenceDetail(current);
  const steamPressures = steamPressureCatalog.map((pressure) => ({
    ...pressure,
    value: readStatusNumber(current, pressure.field),
  }));
  const freeDiskPercent = storage && storage.totalDiskBytes > 0
    ? storage.freeDiskBytes / storage.totalDiskBytes * 100
    : null;

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
        <div className="machine-steam-readout">
          <span>Pressão de vapor</span>
          <div>
            {steamPressures.map((pressure) => (
              <b key={pressure.key}>
                <small>{pressure.label}</small>
                <strong>{formatSteamPressure(pressure.value)}</strong>
              </b>
            ))}
          </div>
          <em>bar</em>
        </div>
        <div className={`machine-paper-readout machine-paper-readout--${paperPresenceClass(paperPresent)}`}>
          <i />
          <div>
            <span>Presença de papel</span>
            <strong>{paperPresenceLabel(paperPresent)}</strong>
            <small>{paperDetail}</small>
          </div>
        </div>
      </section>

      <ProductionContextCard production={production} />

      <MachineGroupStatus
        current={current}
        connected={runtime?.adsConnected ?? false}
      />

      <section className="metric-grid">
        <MetricCard label="Comunicação ADS" value={runtime?.adsConnected ? "Conectado" : "Desconectado"} accent={runtime?.adsConnected ? "green" : "red"} detail="192.168.100.1.1.1 · 851" />
        <MetricCard label="Alarmes ativos" value={String(activeAlarmCount)} accent={activeAlarmCount ? "red" : "green"} detail="Estado atual no PLC" />
        <MetricCard label="Sinais ativos" value={String(trueStatusCount)} accent="blue" detail={`${Object.keys(current?.status ?? {}).length} campos monitorados`} />
        <MetricCard label="Última leitura" value={formatDate(runtime?.lastSuccessfulReadAtUtc)} accent="neutral" detail={runtime?.mappingVersion ?? "Sem mapeamento"} />
        <MetricCard
          label="Banco de dados"
          value={formatBytes(storage?.databaseBytes)}
          accent={freeDiskPercent !== null && freeDiskPercent < 15 ? "red" : "blue"}
          detail={
            storage
              ? `${formatBytes(storage.freeDiskBytes)} livres · WAL ${formatBytes(storage.walBytes)}`
              : "Consultando armazenamento"
          }
        />
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

function ProductionContextCard({
  production,
}: {
  production: ProductionIntegration | null;
}) {
  const run = production?.currentRun;
  const customers = Array.from(new Set(
    (run?.items ?? []).map((item) => item.customerName).filter(Boolean),
  )) as string[];
  const orders = Array.from(new Set(
    (run?.items ?? []).map((item) => item.orderCode).filter(Boolean),
  )) as string[];
  const jumbos = (run?.references ?? [])
    .filter((reference) => reference.referenceType === "Jumbo")
    .map((reference) => reference.referenceValue);
  const hasSingleQuality = Boolean(
    run?.qualityProductCode ||
    run?.qualityGrammageGsm !== null && run?.qualityGrammageGsm !== undefined,
  );
  const quality = run?.isMixedQuality
    ? "Qualidade mista"
    : hasSingleQuality
      ? [
          run?.qualityProductCode,
          run?.qualityGrammageGsm !== null && run?.qualityGrammageGsm !== undefined
            ? `${run.qualityGrammageGsm.toLocaleString("pt-BR")} g/m²`
            : null,
        ].filter(Boolean).join(" · ")
      : "Qualidade não informada";
  const statusLabel = !production
    ? "Consultando"
    : !production.enabled
      ? "Integração desabilitada"
      : production.status === "Online"
        ? "ERP sincronizado"
        : production.status === "Stale"
          ? "Dados desatualizados"
          : production.status === "Faulted"
            ? "ERP indisponível"
            : "Aguardando primeira leitura";
  const stateClass = production?.status === "Online"
    ? "online"
    : production?.status === "Faulted" || production?.status === "Stale"
      ? "warning"
      : "neutral";
  const productionState = !run
    ? "unknown"
    : run.isProducing
      ? "running"
      : "stopped";
  const productionStateLabel = !run
    ? "Sem contexto"
    : `${production?.stale || production?.status === "Faulted" ? "Último estado: " : ""}${
        run.isProducing ? "Produzindo" : "Parado"
      }`;

  return (
    <section className={`production-context-card production-context-card--${stateClass}`}>
      <div className="production-context-heading">
        <div>
          <p className="eyebrow">Contexto de produção ERP</p>
          <h2>{quality}</h2>
          <span>{statusLabel}</span>
        </div>
        <div className={`production-state production-state--${productionState}`}>
          <i />
          {productionStateLabel}
        </div>
      </div>
      <div className="production-context-grid">
        <div><span>OP</span><b>{run?.productionOrderCode ?? "—"}</b></div>
        <div><span>Mapa</span><b>{run?.externalRunId ?? "—"}</b></div>
        <div><span>Cliente</span><b>{customers.join(", ") || "—"}</b></div>
        <div><span>Pedidos</span><b>{orders.join(", ") || "—"}</b></div>
        <div><span>Jumbos</span><b>{jumbos.join(", ") || "—"}</b></div>
        <div><span>Última sincronização</span><b>{formatDate(production?.lastSuccessfulSyncAtUtc)}</b></div>
      </div>
      {run?.isMixedQuality && (
        <p className="production-context-alert">
          O ERP retornou produtos ou gramaturas diferentes. Nenhuma qualidade única foi presumida.
        </p>
      )}
      {production?.lastError && (
        <p className="production-context-alert">{production.lastError}</p>
      )}
    </section>
  );
}

function MachineGroupStatus({
  current,
  connected,
}: {
  current: CurrentSnapshot | null;
  connected: boolean;
}) {
  const groups = machineGroupCatalog.map((group) => {
    const speeds = group.speedFields
      .map((field) => readStatusNumber(current, field))
      .filter((value): value is number => value !== null);
    const referenceSpeed =
      speeds.length > 0
        ? Math.max(...speeds.map((speed) => Math.abs(speed)))
        : null;
    const state =
      !connected || referenceSpeed === null
        ? "unknown"
        : referenceSpeed >= groupRunningSpeedMpm
          ? "running"
          : "stopped";
    return { ...group, referenceSpeed, state };
  });

  return (
    <section className="group-status-panel" aria-label="Status dos grupos da máquina">
      <div className="group-status-heading">
        <div>
          <p className="eyebrow">Acionamentos principais</p>
          <h2>Status dos grupos</h2>
        </div>
        <small>Ligado quando a velocidade medida é ≥ {groupRunningSpeedMpm} m/min</small>
      </div>
      <div className="group-status-grid">
        {groups.map((group) => (
          <article
            className={`group-status-card group-status-card--${group.state}`}
            key={group.key}
          >
            <i />
            <div>
              <span>{group.label}</span>
              <b>
                {group.state === "running"
                  ? "Ligado"
                  : group.state === "stopped"
                    ? "Desligado"
                    : "Sem leitura"}
              </b>
              <small>
                {group.referenceSpeed === null
                  ? "Velocidade indisponível"
                  : `${formatMachineSpeed(group.referenceSpeed)} m/min`}
              </small>
            </div>
          </article>
        ))}
      </div>
    </section>
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
  const period = useHistoryPeriod("today", 31);
  const [trend, setTrend] = useState<MotorTrend>({
    motors: [],
    steamPressures: [],
    samples: [],
  });
  const [selectedGroup, setSelectedGroup] = useState("drying-1");
  const [selectedMotors, setSelectedMotors] = useState<string[]>([]);
  const [selectedSteamPressures, setSelectedSteamPressures] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  const query = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const parameters = new URLSearchParams({
        fromUtc: new Date(period.applied.from).toISOString(),
        toUtc: new Date(period.applied.to).toISOString(),
      });
      parameters.set("maxPoints", "1200");
      setTrend(await fetchJson<MotorTrend>(`/api/history/motors?${parameters}`));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Consulta falhou.");
    } finally {
      setLoading(false);
    }
  }, [period.applied.from, period.applied.to, period.revision]);

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

  useEffect(() => {
    setSelectedSteamPressures((current) => {
      const validSelection = current.filter((key) =>
        trend.steamPressures.some((pressure) => pressure.key === key));
      return validSelection.length > 0
        ? validSelection
        : trend.steamPressures.map((pressure) => pressure.key);
    });
  }, [trend.steamPressures]);

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
  const toggleSteamPressure = (key: string) => {
    setSelectedSteamPressures((current) =>
      current.includes(key)
        ? current.filter((selectedKey) => selectedKey !== key)
        : [...current, key]);
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
  const steamPressureSeries: TrendSeries[] = trend.steamPressures
    .map((pressure, index) => ({
      key: pressure.key,
      label: pressure.label,
      field: pressure.field,
      unit: pressure.unit,
      color: trendColors[(index + 2) % trendColors.length],
      rows: trend.samples
        .map((sample) =>
          [sample.capturedAtUtc, sample.steamPressures?.[pressure.key]] as const)
        .filter((row): row is readonly [string, number] => typeof row[1] === "number"),
    }))
    .filter((series) => selectedSteamPressures.includes(series.key));
  const latestSample = trend.samples[trend.samples.length - 1];
  return (
    <>
      <ScreenTitle
        eyebrow="Tendências de processo"
        title="Velocidade, torque e vapor"
        subtitle="Compare os motores e as pressões de vapor dos três grupos de secagem no mesmo período."
        action={<span className="result-count">{trend.samples.length} amostras</span>}
      />

      <HistoryPeriodFilter
        period={period}
        loading={loading}
        maximumRangeLabel="Período máximo: 31 dias"
        presets={["today", "1h", "8h", "24h", "yesterday", "7d"]}
        resultSummary={`${trend.samples.length.toLocaleString("pt-BR")} amostras`}
      >
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
      </HistoryPeriodFilter>

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
                      V {currentValue?.speed?.toFixed(1) ?? "—"} {formatTrendUnit(motor.speedUnit)}
                      {" · "}
                      T {currentValue?.torque?.toFixed(1) ?? "—"} {formatTrendUnit(motor.torqueUnit)}
                    </small>
                  </span>
                </label>
              );
            })}
          </div>
        </section>
      )}

      {trend.steamPressures.length > 0 && (
        <section className="motor-selection steam-selection">
          <div className="motor-selection-heading">
            <div>
              <p className="eyebrow">Séries visíveis</p>
              <h2>Pressão de vapor</h2>
            </div>
            <div>
              <button type="button" onClick={() => setSelectedSteamPressures(trend.steamPressures.map((pressure) => pressure.key))}>Todos</button>
              <button type="button" onClick={() => setSelectedSteamPressures([])}>Nenhum</button>
            </div>
          </div>
          <div className="motor-checks steam-checks">
            {trend.steamPressures.map((pressure, index) => (
              <label key={pressure.key}>
                <input
                  type="checkbox"
                  checked={selectedSteamPressures.includes(pressure.key)}
                  onChange={() => toggleSteamPressure(pressure.key)}
                />
                <i
                  style={{
                    backgroundColor: trendColors[(index + 2) % trendColors.length],
                    color: trendColors[(index + 2) % trendColors.length],
                  }}
                />
                <span>
                  <b>{pressure.label}</b>
                  <small>
                    {formatSteamPressure(latestSample?.steamPressures?.[pressure.key] ?? null)} {pressure.unit}
                  </small>
                </span>
              </label>
            ))}
          </div>
        </section>
      )}

      {error && <div className="error-banner">{error}</div>}
      {!loading && trend.motors.length === 0 && trend.steamPressures.length === 0 && (
        <div className="graph-empty">
          Nenhuma tendência de motor ou pressão de vapor foi encontrada no período selecionado.
        </div>
      )}
      {(activeGroup || trend.steamPressures.length > 0) && (
        <section className="trend-grid">
          {activeGroup && <TrendChart title="Velocidade" series={speedSeries} />}
          {activeGroup && <TrendChart title="Torque" series={torqueSeries} />}
          {trend.steamPressures.length > 0 && (
            <TrendChart title="Pressão de vapor" series={steamPressureSeries} />
          )}
        </section>
      )}
      <p className="graph-note">
        Resolução atual: telemetria a cada 5 segundos; períodos acima de 12 horas usam
        médias de 1 minuto. Velocidades dos acionamentos
        sincronizados são exibidas em m/min; velocidades das bombas e todos os torques, em %;
        pressões de vapor, em bar.
      </p>
    </>
  );
}

// Mantido durante a transição para preservar o contrato visual anterior como referência.
void MotorGraphs;

function formatTrendUnit(unit: string) {
  return unit === "PLC" ? "unidade PLC" : unit;
}

function TrendChart({ title, series }: { title: string; series: TrendSeries[] }) {
  const units = [...new Set(series.map((item) => item.unit))];
  const axisUnit = units.length === 1 ? units[0] : "valor de processo";
  const seriesIdentity = `${title}:${series.map((item) => item.field).join("|")}`;
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
            <InteractiveChart
              key={seriesIdentity}
              option={option}
              ariaLabel={`${title} dos motores do grupo ao longo do período`}
            />
          </Suspense>
        )
        : <EmptyState text="Selecione ao menos um motor com amostras no período." />}
    </article>
  );
}

const HISTORY_PAGE_SIZE = 100;

function pagedHistoryParameters(
  from: string,
  to: string,
  search: string,
  offset: number,
) {
  const parameters = new URLSearchParams({
    fromUtc: new Date(from).toISOString(),
    toUtc: new Date(to).toISOString(),
    offset: String(offset),
    limit: String(HISTORY_PAGE_SIZE),
  });
  if (search) parameters.set("search", search);
  return parameters;
}

function HistoryPagination({
  loaded,
  total,
  loading,
  onLoadMore,
}: {
  loaded: number;
  total: number;
  loading: boolean;
  onLoadMore: () => void;
}) {
  if (loaded >= total) return null;
  return (
    <div className="history-pagination">
      <span>
        Mostrando {loaded.toLocaleString("pt-BR")} de{" "}
        {total.toLocaleString("pt-BR")}
      </span>
      <button type="button" onClick={onLoadMore} disabled={loading}>
        {loading ? "Carregando…" : "Carregar mais"}
      </button>
    </div>
  );
}

function AlarmHistory() {
  const period = useHistoryPeriod("today", 31);
  const [search, setSearch] = useState("");
  const [appliedSearch, setAppliedSearch] = useState("");
  const [state, setState] = useState("all");
  const [appliedState, setAppliedState] = useState("all");
  const [rows, setRows] = useState<AlarmEvent[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState("");

  const query = useCallback(async (offset = 0, append = false) => {
    if (append) setLoadingMore(true);
    else setLoading(true);
    setError("");
    try {
      const parameters = pagedHistoryParameters(
        period.applied.from,
        period.applied.to,
        appliedSearch,
        offset,
      );
      if (appliedState !== "all")
        parameters.set("active", String(appliedState === "active"));
      const page = await fetchJson<HistoryPage<AlarmEvent>>(
        `/api/history/alarms/search?${parameters}`,
      );
      setRows((current) => append ? [...current, ...page.items] : page.items);
      setTotal(page.total);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Consulta falhou.");
    } finally {
      if (append) setLoadingMore(false);
      else setLoading(false);
    }
  }, [
    appliedSearch,
    appliedState,
    period.applied.from,
    period.applied.to,
    period.revision,
  ]);

  useEffect(() => { void query(0, false); }, [query]);
  const commitFilters = () => {
    setAppliedSearch(search.trim());
    setAppliedState(state);
  };

  return (
    <>
      <ScreenTitle eyebrow="Consulta de eventos" title="Histórico de alarmes" subtitle="Ativações, normalizações e duração calculada de cada ocorrência." action={<span className="result-count">{total.toLocaleString("pt-BR")} registros</span>} />
      <HistoryPeriodFilter
        period={period}
        loading={loading}
        maximumRangeLabel="Período máximo: 31 dias"
        presets={["today", "8h", "24h", "yesterday", "7d"]}
        search={search}
        searchPlaceholder="Alarme, área, severidade ou código do drive…"
        onSearch={setSearch}
        onCommit={commitFilters}
        resultSummary={`${rows.length.toLocaleString("pt-BR")} de ${total.toLocaleString("pt-BR")} carregados`}
      >
        <label>
          <span>Estado</span>
          <select value={state} onChange={(event) => setState(event.target.value)}>
            <option value="all">Todos</option>
            <option value="active">Ativos</option>
            <option value="cleared">Normalizados</option>
          </select>
        </label>
      </HistoryPeriodFilter>
      {error && <div className="error-banner">{error}</div>}
      <DataTable headers={["Alarme", "Diagnóstico C2000 Plus", "Ativação", "Normalização", "Duração", "Estado"]} loading={loading} empty={rows.length === 0} layout="alarm">
        {rows.map((alarm) => (
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
      <HistoryPagination
        loaded={rows.length}
        total={total}
        loading={loadingMore}
        onLoadMore={() => void query(rows.length, true)}
      />
    </>
  );
}

function CommandHistory() {
  const period = useHistoryPeriod("today", 31);
  const [search, setSearch] = useState("");
  const [appliedSearch, setAppliedSearch] = useState("");
  const [rows, setRows] = useState<CommandEvent[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState("");

  const query = useCallback(async (offset = 0, append = false) => {
    if (append) setLoadingMore(true);
    else setLoading(true);
    setError("");
    try {
      const parameters = pagedHistoryParameters(
        period.applied.from,
        period.applied.to,
        appliedSearch,
        offset,
      );
      const page = await fetchJson<HistoryPage<CommandEvent>>(
        `/api/history/commands/search?${parameters}`,
      );
      setRows((current) => append ? [...current, ...page.items] : page.items);
      setTotal(page.total);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Consulta falhou.");
    } finally {
      if (append) setLoadingMore(false);
      else setLoading(false);
    }
  }, [
    appliedSearch,
    period.applied.from,
    period.applied.to,
    period.revision,
  ]);

  useEffect(() => { void query(0, false); }, [query]);

  return (
    <>
      <ScreenTitle eyebrow="Auditoria somente leitura" title="Datalog de comandos" subtitle="Eventos recebidos diretamente por notificação ADS on-change da estrutura paperMachineHmiCommands." action={<span className="result-count">{total.toLocaleString("pt-BR")} registros</span>} />
      <HistoryPeriodFilter
        period={period}
        loading={loading}
        maximumRangeLabel="Período máximo: 31 dias"
        presets={["today", "8h", "24h", "yesterday", "7d"]}
        search={search}
        searchPlaceholder="Variável, valor ou origem…"
        onSearch={setSearch}
        onCommit={() => setAppliedSearch(search.trim())}
        resultSummary={`${rows.length.toLocaleString("pt-BR")} de ${total.toLocaleString("pt-BR")} carregados`}
      />
      {error && <div className="error-banner">{error}</div>}
      <DataTable headers={["Comando", "Valor anterior", "Novo valor", "Horário", "Origem"]} loading={loading} empty={rows.length === 0}>
        {rows.map((command) => (
          <div className="data-row command-row" key={command.id}>
            <div><b>{operatorVariableLabel(command.commandName)}</b><small>{command.commandName}</small></div>
            <span>{parseStoredValue(command.previousValueJson)}</span>
            <span className="changed-value">{parseStoredValue(command.currentValueJson)}</span>
            <span>{formatDate(command.observedAtUtc)}</span>
            <span>{command.origin === "AdsOnChange" ? "ADS on-change" : "PLC observado"}</span>
          </div>
        ))}
      </DataTable>
      <HistoryPagination
        loaded={rows.length}
        total={total}
        loading={loadingMore}
        onLoadMore={() => void query(rows.length, true)}
      />
    </>
  );
}

function StatusHistory() {
  const period = useHistoryPeriod("today", 31);
  const [search, setSearch] = useState("");
  const [appliedSearch, setAppliedSearch] = useState("");
  const [rows, setRows] = useState<StatusChange[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState("");

  const query = useCallback(async (offset = 0, append = false) => {
    if (append) setLoadingMore(true);
    else setLoading(true);
    setError("");
    try {
      const parameters = pagedHistoryParameters(
        period.applied.from,
        period.applied.to,
        appliedSearch,
        offset,
      );
      const page = await fetchJson<HistoryPage<StatusChange>>(
        `/api/history/status-changes/search?${parameters}`,
      );
      setRows((current) => append ? [...current, ...page.items] : page.items);
      setTotal(page.total);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Consulta falhou.");
    } finally {
      if (append) setLoadingMore(false);
      else setLoading(false);
    }
  }, [
    appliedSearch,
    period.applied.from,
    period.applied.to,
    period.revision,
  ]);

  useEffect(() => { void query(0, false); }, [query]);

  return (
    <>
      <ScreenTitle eyebrow="Datalog de processo" title="Histórico de status" subtitle="Alterações detectadas entre snapshots válidos do PLC." action={<span className="result-count">{total.toLocaleString("pt-BR")} registros</span>} />
      <HistoryPeriodFilter
        period={period}
        loading={loading}
        maximumRangeLabel="Período máximo: 31 dias"
        presets={["today", "8h", "24h", "yesterday", "7d"]}
        search={search}
        searchPlaceholder="Variável ou valor registrado…"
        onSearch={setSearch}
        onCommit={() => setAppliedSearch(search.trim())}
        resultSummary={`${rows.length.toLocaleString("pt-BR")} de ${total.toLocaleString("pt-BR")} carregados`}
      />
      {error && <div className="error-banner">{error}</div>}
      <DataTable headers={["Variável", "Valor anterior", "Novo valor", "Horário"]} loading={loading} empty={rows.length === 0} compact>
        {rows.map((change) => (
          <div className="data-row status-row" key={change.id}>
            <div><b>{operatorVariableLabel(change.fieldName)}</b><small>{change.fieldName}</small></div>
            <span>{parseStoredValue(change.previousValueJson)}</span>
            <span className="changed-value">{parseStoredValue(change.currentValueJson)}</span>
            <span>{formatDate(change.observedAtUtc)}</span>
          </div>
        ))}
      </DataTable>
      <HistoryPagination
        loaded={rows.length}
        total={total}
        loading={loadingMore}
        onLoadMore={() => void query(rows.length, true)}
      />
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
