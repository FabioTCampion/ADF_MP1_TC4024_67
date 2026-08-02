import { useCallback, useEffect, useMemo, useState } from "react";

type UpdateStatus = {
  enabled: boolean;
  tokenConfigured: boolean;
  provider: string;
  repository: string;
  currentVersion: string;
  state: string;
  lastCheckedAtUtc: string | null;
  availableVersion: string | null;
  releaseName: string | null;
  releaseNotes: string | null;
  publishedAtUtc: string | null;
  packageFileName: string | null;
  packageSizeBytes: number | null;
  downloadedBytes: number;
  packageSha256: string | null;
  downloadedAtUtc: string | null;
  installRequestedAtUtc: string | null;
  installedAtUtc: string | null;
  lastInstallError: string | null;
  lastError: string | null;
};

const dateFormatter = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short",
  timeStyle: "medium",
});

const stateLabels: Record<string, string> = {
  disabled: "Desabilitado",
  idle: "Aguardando verificação",
  checking: "Consultando GitHub",
  available: "Atualização disponível",
  downloading: "Baixando pacote",
  ready: "Pronta para instalar",
  installRequested: "Instalação solicitada",
  installing: "Instalando",
  succeeded: "Atualização concluída",
  failed: "Falha na atualização",
  error: "Falha na verificação",
  upToDate: "Sistema atualizado",
};

const stateTone = (state: string) => {
  if (state === "failed" || state === "error") return "red";
  if (state === "ready" || state === "available") return "blue";
  if (state === "succeeded" || state === "upToDate") return "green";
  return "neutral";
};

const formatDate = (value: string | null) =>
  value ? dateFormatter.format(new Date(value)) : "—";

const formatBytes = (bytes: number | null | undefined) => {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) return "—";
  const units = ["B", "KB", "MB", "GB"];
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

async function updateRequest(
  url: string,
  options?: RequestInit,
): Promise<UpdateStatus> {
  const response = await fetch(url, {
    cache: "no-store",
    ...options,
    headers: {
      Accept: "application/json",
      ...(options?.body ? { "Content-Type": "application/json" } : {}),
      ...options?.headers,
    },
  });
  if (response.status === 401) {
    window.location.reload();
    throw new Error("Sessão expirada.");
  }
  if (!response.ok) {
    const result = (await response.json().catch(() => null)) as { error?: string } | null;
    throw new Error(result?.error ?? `Operação falhou (${response.status}).`);
  }
  return response.json() as Promise<UpdateStatus>;
}

export default function UpdatesScreen() {
  const [status, setStatus] = useState<UpdateStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [action, setAction] = useState("");
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [confirming, setConfirming] = useState(false);
  const [confirmedVersion, setConfirmedVersion] = useState("");

  const refresh = useCallback(async (quiet = false) => {
    if (!quiet) setLoading(true);
    try {
      setStatus(await updateRequest("/api/updates/status"));
      if (!quiet) setError("");
    } catch (exception) {
      if (!quiet) {
        setError(exception instanceof Error ? exception.message : "Consulta falhou.");
      }
    } finally {
      if (!quiet) setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
    const timer = window.setInterval(() => void refresh(true), 3_000);
    return () => window.clearInterval(timer);
  }, [refresh]);

  const execute = async (
    name: string,
    url: string,
    options?: RequestInit,
  ) => {
    setAction(name);
    setError("");
    setMessage("");
    try {
      const result = await updateRequest(url, options);
      setStatus(result);
      setMessage(
        name === "check"
          ? "Verificação concluída."
          : name === "download"
            ? "Pacote baixado e validado."
            : "Instalação solicitada. A página continuará acompanhando o servidor.",
      );
      return true;
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Operação falhou.");
      return false;
    } finally {
      setAction("");
    }
  };

  const downloadPercent = useMemo(() => {
    if (!status?.packageSizeBytes || status.packageSizeBytes <= 0) return 0;
    return Math.min(100, status.downloadedBytes / status.packageSizeBytes * 100);
  }, [status]);

  const requestInstall = async () => {
    if (!status?.availableVersion) return;
    const accepted = await execute("install", "/api/updates/install", {
      method: "POST",
      body: JSON.stringify({ version: confirmedVersion }),
    });
    if (accepted) {
      setConfirming(false);
      setConfirmedVersion("");
    }
  };

  return (
    <>
      <div className="screen-title">
        <div>
          <p>Administração do servidor</p>
          <h1>Atualizações</h1>
          <span>
            Releases privadas verificadas, baixadas e instaladas com rollback automático.
          </span>
        </div>
        <span className={`update-state update-state--${stateTone(status?.state ?? "idle")}`}>
          {stateLabels[status?.state ?? "idle"] ?? status?.state}
        </span>
      </div>

      {error && <div className="users-message users-message--error">{error}</div>}
      {message && <div className="users-message users-message--success">{message}</div>}

      <section className="update-summary-grid">
        <article>
          <span>Versão instalada</span>
          <strong>{status?.currentVersion ?? "—"}</strong>
          <small>Release atualmente executada pelo serviço</small>
        </article>
        <article>
          <span>Versão disponível</span>
          <strong>{status?.availableVersion ?? "Nenhuma"}</strong>
          <small>{status?.releaseName || "Canal estável do repositório"}</small>
        </article>
        <article>
          <span>Última verificação</span>
          <strong>{formatDate(status?.lastCheckedAtUtc ?? null)}</strong>
          <small>A cada 30 minutos em segundo plano</small>
        </article>
        <article>
          <span>Repositório</span>
          <strong>{status?.repository ?? "—"}</strong>
          <small>{status?.provider ?? "GitHub"} · acesso privado somente leitura</small>
        </article>
      </section>

      <section className="update-panel">
        <div className="update-panel-heading">
          <div>
            <p className="eyebrow">Pacote de atualização</p>
            <h2>{status?.packageFileName ?? "Nenhum pacote preparado"}</h2>
            <span>
              Publicação: {formatDate(status?.publishedAtUtc ?? null)} · tamanho{" "}
              {formatBytes(status?.packageSizeBytes)}
            </span>
          </div>
          <div className="update-actions">
            <button
              type="button"
              disabled={!status?.enabled || action !== ""}
              onClick={() => void execute("check", "/api/updates/check", { method: "POST" })}
            >
              {action === "check" ? "Verificando…" : "Verificar agora"}
            </button>
            {status?.state === "available" && (
              <button
                type="button"
                className="primary-button"
                disabled={action !== ""}
                onClick={() => void execute("download", "/api/updates/download", { method: "POST" })}
              >
                {action === "download" ? "Baixando…" : "Baixar atualização"}
              </button>
            )}
            {status?.state === "ready" && (
              <button
                type="button"
                className="update-install-button"
                disabled={action !== ""}
                onClick={() => setConfirming(true)}
              >
                Instalar {status.availableVersion}
              </button>
            )}
          </div>
        </div>

        {!status?.enabled && (
          <div className="update-notice">
            As atualizações estão desabilitadas nesta configuração. Em produção, defina
            <code> Updates:Enabled = true</code>.
          </div>
        )}
        {status?.enabled && !status.tokenConfigured && (
          <div className="update-notice update-notice--warning">
            O token do repositório privado ainda não foi configurado. Execute
            <code> Set-PaperMachineHistorianUpdateToken.ps1</code> no servidor.
          </div>
        )}

        {(status?.state === "downloading" || downloadPercent > 0) && (
          <div className="update-progress">
            <div>
              <span>Download e validação SHA-256</span>
              <b>{downloadPercent.toLocaleString("pt-BR", { maximumFractionDigits: 1 })}%</b>
            </div>
            <progress value={downloadPercent} max={100} />
            <small>
              {formatBytes(status?.downloadedBytes)} de {formatBytes(status?.packageSizeBytes)}
            </small>
          </div>
        )}

        {status?.packageSha256 && (
          <div className="update-hash">
            <span>SHA-256 validado</span>
            <code>{status.packageSha256}</code>
          </div>
        )}

        {status?.lastInstallError && (
          <div className="update-error-detail">
            <b>Falha na última instalação</b>
            <span>{status.lastInstallError}</span>
          </div>
        )}

        {status?.lastError && status.lastError !== status.lastInstallError && (
          <div className="update-error-detail">
            <b>Última falha de comunicação</b>
            <span>{status.lastError}</span>
          </div>
        )}
      </section>

      <section className="update-details-grid">
        <article>
          <p className="eyebrow">Notas da versão</p>
          <h2>{status?.releaseName || "Sem atualização disponível"}</h2>
          <pre>{status?.releaseNotes || "As notas serão carregadas diretamente da Release do GitHub."}</pre>
        </article>
        <article>
          <p className="eyebrow">Proteções</p>
          <h2>Instalação controlada</h2>
          <ul>
            <li>Somente administradores podem solicitar a instalação.</li>
            <li>Manifesto e SHA-256 são verificados antes da troca.</li>
            <li>Banco, configuração e chaves recebem backup consistente.</li>
            <li>A tarefa externa executa como SYSTEM e não reinicia o PLC.</li>
            <li>Falha de validação restaura automaticamente a release anterior.</li>
          </ul>
        </article>
      </section>

      {loading && !status && <div className="empty-state">Consultando atualizações…</div>}

      {confirming && status?.availableVersion && (
        <div className="user-modal-backdrop">
          <section className="user-modal update-confirm-modal">
            <div className="user-modal-heading">
              <div>
                <span>Confirmação administrativa</span>
                <h2>Instalar versão {status.availableVersion}</h2>
              </div>
              <button type="button" onClick={() => setConfirming(false)}>×</button>
            </div>
            <p>
              O Historian ficará indisponível por alguns segundos. O TwinCAT e o PLC não
              serão reiniciados, mas haverá um pequeno intervalo sem coleta.
            </p>
            <label>
              Digite <b>{status.availableVersion}</b> para confirmar
              <input
                autoFocus
                value={confirmedVersion}
                onChange={(event) => setConfirmedVersion(event.target.value)}
              />
            </label>
            <div className="user-modal-actions">
              <button type="button" onClick={() => setConfirming(false)}>Cancelar</button>
              <button
                type="button"
                className="update-install-button"
                disabled={confirmedVersion !== status.availableVersion || action !== ""}
                onClick={() => void requestInstall()}
              >
                {action === "install" ? "Solicitando…" : "Confirmar instalação"}
              </button>
            </div>
          </section>
        </div>
      )}
    </>
  );
}
