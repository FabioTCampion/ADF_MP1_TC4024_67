import { useCallback, useEffect, useState } from "react";
import { useAuth } from "./Auth";

type UserRole = "Viewer" | "Administrator";

type ManagedUser = {
  id: number;
  userName: string;
  displayName: string;
  role: UserRole;
  isActive: boolean;
  createdAtUtc: string;
  lastLoginAtUtc: string | null;
};

type UserDraft = {
  id?: number;
  userName: string;
  displayName: string;
  password: string;
  role: UserRole;
  isActive: boolean;
};

const emptyDraft = (): UserDraft => ({
  userName: "",
  displayName: "",
  password: "",
  role: "Viewer",
  isActive: true,
});

const dateFormatter = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short",
  timeStyle: "short",
});

const roleLabel: Record<UserRole, string> = {
  Administrator: "Administrador",
  Viewer: "Consulta",
};

async function readError(response: Response, fallback: string) {
  const result = (await response.json().catch(() => null)) as { error?: string } | null;
  return result?.error ?? fallback;
}

export default function UsersScreen() {
  const { user: authenticatedUser } = useAuth();
  const [users, setUsers] = useState<ManagedUser[]>([]);
  const [editing, setEditing] = useState<UserDraft | null>(null);
  const [resetting, setResetting] = useState<ManagedUser | null>(null);
  const [newPassword, setNewPassword] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const response = await fetch("/api/users", {
        headers: { Accept: "application/json" },
      });
      if (response.status === 401) {
        window.location.reload();
        return;
      }
      if (!response.ok)
        throw new Error(await readError(response, "Não foi possível carregar os usuários."));
      setUsers((await response.json()) as ManagedUser[]);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Falha ao carregar usuários.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const editUser = (managedUser: ManagedUser) => {
    setNotice("");
    setError("");
    setEditing({
      id: managedUser.id,
      userName: managedUser.userName,
      displayName: managedUser.displayName,
      password: "",
      role: managedUser.role,
      isActive: managedUser.isActive,
    });
  };

  const saveUser = async () => {
    if (!editing) return;
    setSaving(true);
    setError("");
    setNotice("");
    const creating = editing.id === undefined;
    try {
      const response = await fetch(
        creating ? "/api/users" : `/api/users/${editing.id}`,
        {
          method: creating ? "POST" : "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(
            creating
              ? {
                userName: editing.userName,
                displayName: editing.displayName,
                password: editing.password,
                role: editing.role,
              }
              : {
                displayName: editing.displayName,
                role: editing.role,
                isActive: editing.isActive,
              },
          ),
        },
      );
      if (!response.ok)
        throw new Error(await readError(response, "Não foi possível salvar o usuário."));
      setEditing(null);
      setNotice(creating ? "Usuário criado com sucesso." : "Usuário atualizado com sucesso.");
      await load();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Falha ao salvar usuário.");
    } finally {
      setSaving(false);
    }
  };

  const resetPassword = async () => {
    if (!resetting) return;
    setSaving(true);
    setError("");
    setNotice("");
    try {
      const response = await fetch(`/api/users/${resetting.id}/password`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ password: newPassword }),
      });
      if (!response.ok)
        throw new Error(await readError(response, "Não foi possível redefinir a senha."));
      setResetting(null);
      setNewPassword("");
      setNotice(`Senha de ${resetting.displayName} redefinida com sucesso.`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Falha ao redefinir senha.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <div className="screen-title">
        <div>
          <p className="eyebrow">Administração</p>
          <h1>Usuários e acessos</h1>
          <span>Cadastre usuários locais e defina o nível de acesso ao histórico da máquina.</span>
        </div>
        <button
          type="button"
          className="primary-button users-new-button"
          onClick={() => {
            setError("");
            setNotice("");
            setEditing(emptyDraft());
          }}
        >
          Novo usuário
        </button>
      </div>

      {error && <div className="users-message users-message--error">{error}</div>}
      {notice && <div className="users-message users-message--success">{notice}</div>}

      <section className="access-profiles">
        <div className="users-section-heading">
          <div>
            <h2>Perfis de acesso</h2>
            <span>Perfis fixos e adequados à aplicação de consulta.</span>
          </div>
        </div>
        <div className="access-profile-grid">
          <article className="access-profile-card access-profile-card--admin">
            <div>
              <span>Gestão completa</span>
              <h3>Administrador</h3>
              <p>Consulta todos os dados e administra usuários, perfis, status e senhas.</p>
            </div>
            <small>Inclui gestão de acessos</small>
          </article>
          <article className="access-profile-card">
            <div>
              <span>Somente leitura</span>
              <h3>Consulta</h3>
              <p>Acessa dashboards, métricas, gráficos, alarmes, comandos e históricos.</p>
            </div>
            <small>Sem acesso à administração</small>
          </article>
        </div>
      </section>

      <div className="users-section-heading">
        <div>
          <h2>Usuários</h2>
          <span>{users.length} cadastro{users.length === 1 ? "" : "s"} no banco local.</span>
        </div>
      </div>
      <section className="users-table">
        <div className="users-head">
          <span>Nome</span>
          <span>Usuário</span>
          <span>Perfil</span>
          <span>Último acesso</span>
          <span>Status</span>
          <span>Ações</span>
        </div>
        {loading && <div className="users-empty">Carregando usuários…</div>}
        {!loading && users.length === 0 && (
          <div className="users-empty">Nenhum usuário cadastrado.</div>
        )}
        {!loading && users.map((managedUser) => (
          <div
            className={`users-row${managedUser.id === authenticatedUser.id ? " users-row--current" : ""}`}
            key={managedUser.id}
          >
            <b>
              {managedUser.displayName}
              {managedUser.id === authenticatedUser.id && (
                <small className="current-session-badge">Sessão atual</small>
              )}
            </b>
            <span>{managedUser.userName}</span>
            <span className={`role-badge role-badge--${managedUser.role.toLowerCase()}`}>
              {roleLabel[managedUser.role]}
            </span>
            <span>{managedUser.lastLoginAtUtc ? dateFormatter.format(new Date(managedUser.lastLoginAtUtc)) : "Nunca acessou"}</span>
            <span className={managedUser.isActive ? "user-active" : "user-inactive"}>
              <i />
              {managedUser.isActive ? "Ativo" : "Desativado"}
            </span>
            <div className="users-row-actions">
              <button type="button" onClick={() => editUser(managedUser)}>Editar</button>
              <button
                type="button"
                onClick={() => {
                  setError("");
                  setNotice("");
                  setNewPassword("");
                  setResetting(managedUser);
                }}
              >
                Redefinir senha
              </button>
            </div>
          </div>
        ))}
      </section>

      {editing && (
        <div className="user-modal-backdrop" role="presentation">
          <form
            className="user-modal"
            onSubmit={(event) => {
              event.preventDefault();
              void saveUser();
            }}
          >
            <div className="user-modal-heading">
              <div>
                <span>{editing.id ? "Cadastro existente" : "Novo acesso local"}</span>
                <h2>{editing.id ? "Editar usuário" : "Novo usuário"}</h2>
              </div>
              <button type="button" onClick={() => setEditing(null)} aria-label="Fechar">×</button>
            </div>
            <label>
              Nome completo
              <input
                autoFocus
                required
                value={editing.displayName}
                onChange={(event) => setEditing({ ...editing, displayName: event.target.value })}
              />
            </label>
            {!editing.id && (
              <>
                <label>
                  Usuário
                  <input
                    required
                    minLength={3}
                    value={editing.userName}
                    onChange={(event) => setEditing({ ...editing, userName: event.target.value })}
                    autoComplete="off"
                  />
                </label>
                <label>
                  Senha inicial
                  <input
                    required
                    minLength={8}
                    type="password"
                    value={editing.password}
                    onChange={(event) => setEditing({ ...editing, password: event.target.value })}
                    autoComplete="new-password"
                  />
                </label>
              </>
            )}
            {editing.id && (
              <label>
                Usuário
                <input value={editing.userName} disabled />
              </label>
            )}
            <label>
              Perfil de acesso
              <select
                value={editing.role}
                disabled={editing.id === authenticatedUser.id}
                onChange={(event) => setEditing({ ...editing, role: event.target.value as UserRole })}
              >
                <option value="Viewer">Consulta</option>
                <option value="Administrator">Administrador</option>
              </select>
            </label>
            {editing.id && (
              <label className="user-active-toggle">
                <input
                  type="checkbox"
                  checked={editing.isActive}
                  disabled={editing.id === authenticatedUser.id}
                  onChange={(event) => setEditing({ ...editing, isActive: event.target.checked })}
                />
                <span>
                  Usuário ativo
                  {editing.id === authenticatedUser.id && <small>A sessão atual deve permanecer ativa.</small>}
                </span>
              </label>
            )}
            <div className="user-modal-actions">
              <button type="button" onClick={() => setEditing(null)}>Cancelar</button>
              <button className="primary-button" disabled={saving}>
                {saving ? "Salvando…" : "Salvar usuário"}
              </button>
            </div>
          </form>
        </div>
      )}

      {resetting && (
        <div className="user-modal-backdrop" role="presentation">
          <form
            className="user-modal user-password-modal"
            onSubmit={(event) => {
              event.preventDefault();
              void resetPassword();
            }}
          >
            <div className="user-modal-heading">
              <div>
                <span>Credencial local</span>
                <h2>Redefinir senha</h2>
              </div>
              <button type="button" onClick={() => setResetting(null)} aria-label="Fechar">×</button>
            </div>
            <p>Defina uma nova senha para <b>{resetting.displayName}</b>.</p>
            <label>
              Nova senha
              <input
                autoFocus
                required
                minLength={8}
                type="password"
                value={newPassword}
                onChange={(event) => setNewPassword(event.target.value)}
                autoComplete="new-password"
              />
            </label>
            <small className="password-hint">Use pelo menos 8 caracteres.</small>
            <div className="user-modal-actions">
              <button type="button" onClick={() => setResetting(null)}>Cancelar</button>
              <button className="primary-button" disabled={saving}>
                {saving ? "Redefinindo…" : "Redefinir senha"}
              </button>
            </div>
          </form>
        </div>
      )}
    </>
  );
}
